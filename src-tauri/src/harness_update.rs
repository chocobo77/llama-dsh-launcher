//! Harness self-update, mirroring the LlamaDSH launcher's method
//! (D:\code\启动器, Services/UpdateService.cs):
//!
//! 1. `git fetch origin --prune` to learn the upstream HEAD; on failure retry
//!    direct fetch with HTTP/1.1 (some proxies break git's HTTP/2), then fall
//!    back to gh-proxy mirrors that serve the same refs.
//! 2. Compare local/remote HEAD with `rev-list --left-right --count`.
//! 3. `git pull --ff-only`: stash uncommitted changes first (they are the
//!    user's real patches and are restored afterwards), merge through a mirror
//!    when direct pull cannot reach the network. Never creates merge commits.
//! 4. Rebuild the built artifacts the Host actually runs:
//!    `pnpm install` (only when node_modules is missing) → `pnpm clean` →
//!    `pnpm build:lib` → `build:web` (vite). The launcher's exact recipe minus
//!    clean-only assumptions.
//!
//! All output is ASCII-safe logging fed to the shell's status log; dialogs are
//! the caller's business (lib.rs).

use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

/// A harness worth updating: the checkout the shell is mounted into (or one
/// pointed at explicitly), containing apps/cli/lib/bin.js and .git.
pub fn harness_root() -> Option<PathBuf> {
    crate::server::default_checkout()
}

/// git.exe discovery: PATH first, then the standard Git for Windows spots,
/// so a machine with git installed but not on PATH still works.
pub fn git_exe() -> Option<PathBuf> {
    let mut candidates: Vec<PathBuf> = Vec::new();
    if let Ok(path) = std::env::var("PATH") {
        for dir in std::env::split_paths(&path) {
            candidates.push(dir.join("git.exe"));
        }
    }
    for base in [
        "C:\\Program Files\\Git",
        "C:\\Program Files (x86)\\Git",
    ] {
        let base = PathBuf::from(base);
        candidates.push(base.join("cmd").join("git.exe"));
        candidates.push(base.join("bin").join("git.exe"));
    }
    candidates.into_iter().find(|p| p.is_file())
}

/// Run one git command in `dir`, capturing stdout+stderr. Never panics on a
/// missing git: the caller resolves the exe first.
fn run_git(git: &Path, dir: &Path, args: &[&str], timeout: Duration) -> (i32, String) {
    let mut cmd = Command::new(git);
    cmd.args(args)
        .current_dir(dir)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    match cmd.spawn() {
        Ok(mut child) => {
            let deadline = Instant::now() + timeout;
            // Both pipes must be drained or git blocks on a full pipe; small
            // outputs here, so read stdout after wait is unsafe — read both
            // via threads and join with a plain wait, then enforce the
            // deadline by killing.
            use std::io::Read;
            let mut stdout = child.stdout.take().unwrap();
            let mut stderr = child.stderr.take().unwrap();
            let t_out = std::thread::spawn(move || {
                let mut buf = Vec::new();
                let _ = stdout.read_to_end(&mut buf);
                buf
            });
            let t_err = std::thread::spawn(move || {
                let mut buf = Vec::new();
                let _ = stderr.read_to_end(&mut buf);
                buf
            });
            // Poll-based wait so a hung network fetch still respects the cap.
            loop {
                match child.try_wait() {
                    Ok(Some(status)) => {
                        let out = t_out.join().unwrap_or_default();
                        let err = t_err.join().unwrap_or_default();
                        let mut text = String::from_utf8_lossy(&out).into_owned();
                        let errtext = String::from_utf8_lossy(&err);
                        if !errtext.is_empty() {
                            text.push('\n');
                            text.push_str(&errtext);
                        }
                        return (status.code().unwrap_or(-1), text);
                    }
                    Ok(None) => {
                        if Instant::now() > deadline {
                            let _ = child.kill();
                            let _ = child.wait();
                            return (-1, "git command timed out".into());
                        }
                        std::thread::sleep(Duration::from_millis(100));
                    }
                    Err(e) => return (-1, format!("git wait failed: {e}")),
                }
            }
        }
        Err(e) => (-1, format!("git spawn failed: {e}")),
    }
}

/// Normalize a remote URL to an https GitHub URL the mirrors can proxy.
/// git@github.com:owner/repo and ssh:// forms become https; query strings and
/// a trailing .git are dropped.
fn normalize_origin(origin: &str) -> Option<String> {
    let mut u = origin.trim().to_string();
    if u.is_empty() {
        return None;
    }
    if let Some(rest) = u.strip_prefix("git@github.com:") {
        u = format!("https://github.com/{rest}");
    } else if let Some(rest) = u.strip_prefix("ssh://git@github.com/") {
        u = format!("https://github.com/{rest}");
    }
    if !u.to_lowercase().contains("github.com") {
        return None;
    }
    if let Some(q) = u.find('?') {
        u.truncate(q);
    }
    while u.ends_with('/') {
        u.pop();
    }
    if u.to_lowercase().ends_with(".git") {
        u.truncate(u.len() - 4);
    }
    Some(u)
}

/// Mirror list for `origin`, launcher-compatible: DSH_GIT_MIRROR entries
/// (semicolon separated, `{url}` placeholder or prefix form) first, then the
/// three public gh proxies the launcher uses.
fn mirror_urls(origin: &str) -> Vec<String> {
    let Some(u) = normalize_origin(origin) else {
        return Vec::new();
    };
    let mut out = Vec::new();
    if let Ok(custom) = std::env::var("DSH_GIT_MIRROR") {
        for item in custom.split(';') {
            let item = item.trim();
            if item.is_empty() {
                continue;
            }
            let url = if item.to_lowercase().contains("{url}") {
                item.replace("{url}", &u)
            } else {
                format!("{}/{}", item.trim_end_matches('/'), u)
            };
            if !out.contains(&url) {
                out.push(url);
            }
        }
    }
    for prefix in [
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://ghproxy.net/",
    ] {
        let url = format!("{prefix}{u}");
        if !out.contains(&url) {
            out.push(url);
        }
    }
    out
}

#[derive(Debug, Clone)]
pub struct Info {
    pub branch: String,
    pub local: String,
    pub remote: String,
    pub ahead: usize,
    pub behind: usize,
    /// "origin" when direct fetch worked, otherwise the mirror URL used.
    pub via: String,
}

const SHORT: Duration = Duration::from_secs(20);
const FETCH: Duration = Duration::from_secs(180);
const MERGE: Duration = Duration::from_secs(120);

/// Fetch upstream and compare. Errors name the actual cause, never a bare
/// "network problem" — that property is why the launcher's flow was ported.
pub fn check(root: &Path) -> Result<Info, String> {
    if !root.join(".git").exists() {
        return Err(format!(
            "不是 git 仓库（找不到 {}）：无法按 git 方式更新",
            root.join(".git").display()
        ));
    }
    let git = git_exe().ok_or_else(|| {
        "未找到 git.exe：请安装 Git for Windows，或把 git 加入 PATH".to_string()
    })?;

    let branch = run_git(&git, root, &["rev-parse", "--abbrev-ref", "HEAD"], SHORT)
        .1
        .trim()
        .to_string();
    if branch.is_empty() || branch == "HEAD" {
        return Err("无法确定当前分支（可能处于 detached HEAD）".into());
    }
    let local = run_git(&git, root, &["rev-parse", "HEAD"], SHORT).1.trim().to_string();
    if local.len() != 40 {
        return Err("无法读取本地 HEAD：仓库可能损坏，或 git 不可用".into());
    }

    let mut remote = String::new();
    let mut via = "origin".to_string();
    let mut fetch = run_git(&git, root, &["fetch", "origin", "--prune"], FETCH);
    if fetch.0 != 0 {
        // HTTP/1.1 retry: some proxies break git over HTTP/2 (RPC failed /
        // malloc failed) while plain HTTP/1.1 passes.
        fetch = run_git(
            &git,
            root,
            &["-c", "http.version=HTTP/1.1", "fetch", "origin", "--prune"],
            FETCH,
        );
    }
    if fetch.0 == 0 {
        remote = run_git(&git, root, &["rev-parse", &format!("origin/{branch}")], SHORT)
            .1
            .trim()
            .to_string();
        if remote.len() != 40 {
            remote = run_git(&git, root, &["rev-parse", "FETCH_HEAD"], SHORT)
                .1
                .trim()
                .to_string();
        }
    } else {
        let origin = run_git(&git, root, &["remote", "get-url", "origin"], SHORT)
            .1
            .trim()
            .to_string();
        let mut last = fetch.1.clone();
        for candidate in mirror_urls(&origin) {
            let mf = run_git(
                &git,
                root,
                &[
                    "-c",
                    "http.version=HTTP/1.1",
                    "fetch",
                    "--no-tags",
                    &candidate,
                    &branch,
                ],
                MERGE,
            );
            if mf.0 == 0 {
                remote = run_git(&git, root, &["rev-parse", "FETCH_HEAD"], SHORT)
                    .1
                    .trim()
                    .to_string();
                via = candidate;
                break;
            }
            last.push_str(&mf.1);
        }
        if remote.len() != 40 {
            return Err(format!("fetch 失败（直连与镜像都不通）：\n{}", tail(&last, 800)));
        }
    }
    if remote.len() != 40 {
        return Err(format!("fetch 成功但无法解析远端 HEAD（origin/{branch} 不存在？）"));
    }
    let counts = run_git(
        &git,
        root,
        &["rev-list", "--left-right", "--count", &format!("HEAD...{remote}")],
        SHORT,
    )
    .1;
    let mut ahead = 0usize;
    let mut behind = 0usize;
    if let Some(line) = counts.lines().next() {
        let mut it = line.split('\t');
        if let Some(a) = it.next().and_then(|s| s.trim().parse().ok()) {
            ahead = a;
        }
        if let Some(b) = it.next().and_then(|s| s.trim().parse().ok()) {
            behind = b;
        }
    }
    Ok(Info {
        branch,
        local,
        remote,
        ahead,
        behind,
        via,
    })
}

fn tail(s: &str, max: usize) -> &str {
    if s.len() <= max {
        s
    } else {
        &s[s.len() - max..]
    }
}

fn has_local_changes(git: &Path, root: &Path) -> bool {
    run_git(git, root, &["status", "--porcelain"], SHORT)
        .1
        .trim()
        .is_empty() == false
}

/// `git pull --ff-only` with the launcher's safety net: uncommitted changes
/// are stashed before the merge and restored after, mirrors cover a dead
/// direct connection, and a failed restore is reported instead of swallowed.
pub fn pull(root: &Path, log: &dyn Fn(String)) -> Result<String, String> {
    let git = git_exe().ok_or("未找到 git.exe：无法拉取上游")?;
    let stash_push = |git: &Path, root: &Path| -> Option<String> {
        let (code, _out) = run_git(
            git,
            root,
            &["stash", "push", "--include-untracked", "-m", "dsh-auto-update"],
            SHORT,
        );
        if code != 0 {
            return None;
        }
        // Only a real stash entry counts: push exits 0 with "No local changes
        // to save" when there was nothing to store.
        let sha = run_git(git, root, &["rev-parse", "stash@{0}"], SHORT)
            .1
            .trim()
            .to_string();
        (sha.len() == 40).then_some(sha)
    };
    let stash_pop = |git: &Path, root: &Path, sha: &str, log: &dyn Fn(String)| {
        let (code, out) = run_git(git, root, &["stash", "pop", sha], SHORT);
        if code != 0 {
            log(format!(
                "警告：自动恢复本地改动失败（改动保留在 stash {sha}，请手动 git stash pop）：\n{}",
                tail(&out, 400)
            ));
        } else {
            log("已恢复更新前的本地改动".into());
        }
    };

    let mut pulled = run_git(&git, root, &["pull", "--ff-only"], MERGE);
    let mut stashed: Option<String> = None;
    if pulled.0 != 0 && has_local_changes(&git, root) {
        log("本地有未提交改动：先 stash，更新后恢复".into());
        stashed = stash_push(&git, root);
        if stashed.is_none() {
            return Err("本地有未提交改动且自动 stash 失败：请先提交或手动处理".into());
        }
        pulled = run_git(&git, root, &["pull", "--ff-only"], MERGE);
    }

    if pulled.0 == 0 {
        let head = run_git(&git, root, &["rev-parse", "HEAD"], SHORT).1.trim().to_string();
        if let Some(sha) = &stashed {
            stash_pop(&git, root, sha, log);
        }
        return if head.len() == 40 {
            Ok(head)
        } else {
            Err("pull 成功但无法读取新 HEAD".into())
        };
    }

    // Direct pull failed: mirror fetch + local fast-forward, no merge commit.
    let branch = run_git(&git, root, &["rev-parse", "--abbrev-ref", "HEAD"], SHORT)
        .1
        .trim()
        .to_string();
    let origin = run_git(&git, root, &["remote", "get-url", "origin"], SHORT)
        .1
        .trim()
        .to_string();
    if !branch.is_empty() && branch != "HEAD" {
        for candidate in mirror_urls(&origin) {
            let mf = run_git(
                &git,
                root,
                &[
                    "-c",
                    "http.version=HTTP/1.1",
                    "fetch",
                    "--no-tags",
                    &candidate,
                    &branch,
                ],
                MERGE,
            );
            if mf.0 != 0 {
                continue;
            }
            log(format!("已通过镜像获取上游：{candidate}"));
            let merge = run_git(&git, root, &["merge", "--ff-only", "FETCH_HEAD"], MERGE);
            if merge.0 == 0 {
                let head = run_git(&git, root, &["rev-parse", "HEAD"], SHORT)
                    .1
                    .trim()
                    .to_string();
                if let Some(sha) = &stashed {
                    stash_pop(&git, root, sha, log);
                }
                return if head.len() == 40 {
                    Ok(head)
                } else {
                    Err("镜像拉取成功但无法读取新 HEAD".into())
                };
            }
            if let Some(sha) = &stashed {
                // Merge still failed: restore the worktree before reporting.
                stash_pop(&git, root, sha, log);
            }
            return Err(format!(
                "镜像已获取上游，但本地无法快进合并（可能有本地提交/历史分叉）：\n{}",
                tail(&merge.1, 800)
            ));
        }
    }
    Err(format!("git pull 失败：\n{}", tail(&pulled.1, 800)))
}

/// Where pnpm lives for this harness: the deployed node dir ships corepack
/// shims next to node.exe; a PATH pnpm is the fallback.
fn pnpm_for(root: &Path) -> Option<PathBuf> {
    let own = root.join("node").join("pnpm.cmd");
    if own.is_file() {
        return Some(own);
    }
    let mut candidates: Vec<PathBuf> = Vec::new();
    if let Ok(path) = std::env::var("PATH") {
        for dir in std::env::split_paths(&path) {
            candidates.push(dir.join("pnpm.cmd"));
            candidates.push(dir.join("pnpm.exe"));
        }
    }
    candidates.into_iter().find(|p| p.is_file())
}

/// One rebuild step with streamed output. `extra_path` puts the harness node
/// dir first so pnpm's shims resolve this checkout's node, not whatever else
/// is on PATH. The sink is an owned Send+Sync closure: the stderr pump runs
/// on its own thread alongside the stdout loop.
fn run_step<L>(
    program: &Path,
    args: &[&str],
    cwd: &Path,
    extra_path: &Path,
    log: L,
    timeout: Duration,
) -> Result<(), String>
where
    L: Fn(String) + Send + Sync + Clone + 'static,
{
    log(format!("$ {} {}", program.display(), args.join(" ")));
    let mut cmd = Command::new(program);
    cmd.args(args)
        .current_dir(cwd)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW
    }
    // PATH precedence: <harness>\node first, then the inherited PATH minus the
    // harness dir (appended again is harmless but keep it simple).
    let inherited = std::env::var("PATH").unwrap_or_default();
    let new_path = format!("{};{}", extra_path.display(), inherited);
    cmd.env("PATH", new_path);
    let mut child = cmd
        .spawn()
        .map_err(|e| format!("无法启动 {}：{e}", program.display()))?;
    let mut stdout = child.stdout.take().unwrap();
    let mut stderr = child.stderr.take().unwrap();
    let t_err: std::thread::JoinHandle<()> = {
        let log = log.clone();
        std::thread::spawn(move || {
            let reader = BufReader::new(stderr);
            for line in reader.lines().map_while(Result::ok) {
                if !line.trim().is_empty() {
                    log(format!("  {line}"));
                }
            }
        })
    };
    let deadline = Instant::now() + timeout;
    let reader = BufReader::new(stdout);
    let mut timed_out = false;
    for line in reader.lines() {
        if Instant::now() > deadline {
            timed_out = true;
            break;
        }
        match line {
            Ok(l) if !l.trim().is_empty() => log(format!("  {l}")),
            Ok(_) => {}
            Err(_) => break,
        }
    }
    if timed_out {
        let _ = child.kill();
        let _ = child.wait();
        return Err("步骤超时".into());
    }
    let _ = t_err.join();
    let status = child
        .wait()
        .map_err(|e| format!("等待步骤结束失败：{e}"))?;
    if status.success() {
        Ok(())
    } else {
        Err(format!("步骤失败（exit {}）", status.code().unwrap_or(-1)))
    }
}

/// Full rebuild of the artifacts the Host runs: install when node_modules is
/// missing, then the launcher's clean → build:lib → build:web chain.
pub fn rebuild<L>(root: &Path, log: L) -> Result<(), String>
where
    L: Fn(String) + Send + Sync + Clone + 'static,
{
    let node_dir = root.join("node");
    let pnpm = pnpm_for(root)
        .ok_or("未找到 pnpm：<harness>\\node\\pnpm.cmd 与 PATH 都没有；先运行一次部署或 corepack enable")?
;
    let needs_install = !root.join("node_modules").is_dir();
    if needs_install {
        run_step(
            &pnpm,
            &["install", "--frozen-lockfile"],
            root,
            &node_dir,
            log.clone(),
            Duration::from_secs(1800),
        )
        .map_err(|e| format!("pnpm install：{e}"))?;
    }
    run_step(&pnpm, &["clean"], root, &node_dir, log.clone(), Duration::from_secs(600))
        .map_err(|e| format!("pnpm clean：{e}"))?;
    run_step(
        &pnpm,
        &["build:lib"],
        root,
        &node_dir,
        log.clone(),
        Duration::from_secs(1800),
    )
    .map_err(|e| format!("pnpm build:lib：{e}"))?;
    run_step(
        &pnpm,
        &["build:web"],
        root,
        &node_dir,
        log,
        Duration::from_secs(1800),
    )
    .map_err(|e| format!("pnpm build:web：{e}"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalizes_remote_forms() {
        assert_eq!(
            normalize_origin("git@github.com:deepseek-ai/deepseek-harness.git"),
            Some("https://github.com/deepseek-ai/deepseek-harness".into())
        );
        assert_eq!(
            normalize_origin("ssh://git@github.com/deepseek-ai/deepseek-harness.git"),
            Some("https://github.com/deepseek-ai/deepseek-harness".into())
        );
        assert_eq!(
            normalize_origin("https://github.com/deepseek-ai/deepseek-harness.git?x=1"),
            Some("https://github.com/deepseek-ai/deepseek-harness".into())
        );
        assert_eq!(normalize_origin("https://gitlab.com/a/b"), None);
        assert_eq!(normalize_origin(""), None);
    }

    #[test]
    fn mirrors_cover_custom_and_defaults() {
        std::env::remove_var("DSH_GIT_MIRROR");
        let m = mirror_urls("https://github.com/a/b");
        assert_eq!(
            m,
            vec![
                "https://gh-proxy.com/https://github.com/a/b",
                "https://ghfast.top/https://github.com/a/b",
                "https://ghproxy.net/https://github.com/a/b",
            ]
        );
        std::env::set_var("DSH_GIT_MIRROR", "https://mirror.test/{url};https://p.test/");
        let m = mirror_urls("git@github.com:a/b.git");
        assert_eq!(m[0], "https://mirror.test/https://github.com/a/b");
        assert_eq!(m[1], "https://p.test/https://github.com/a/b");
        std::env::remove_var("DSH_GIT_MIRROR");
    }

    #[test]
    fn tail_keeps_the_end() {
        assert_eq!(tail("abcdef", 3), "def");
        assert_eq!(tail("ab", 10), "ab");
    }
}
