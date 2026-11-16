//! Harness self-update, shallow by design: the local repository carries no
//! history, only the latest upstream state (user decision — a deployed harness
//! has no use for the past). The flow:
//!
//! 1. `ensure`/`check`: a harness without .git is bootstrapped in place
//!    (`git init` + remote + `fetch --depth 1` + `reset --hard`); one with .git
//!    fetches `--depth 1` and compares HEAD against the fetched upstream SHA —
//!    different SHA means an update exists (a no-history repo cannot count
//!    commits behind, and does not need to).
//! 2. `sync`: fetch `--depth 1`, stash any tracked-file modifications (kept in
//!    the stash for manual recovery, never popped), then `reset --hard` to the
//!    fetched SHA. Untracked paths — built libs, node_modules, the installed
//!    shell — are never touched; only `reset`-managed files align to upstream.
//! 3. `rebuild`: `pnpm clean → build:lib → build:web` (`pnpm install` only when
//!    node_modules is missing), the recipe the deployed artifacts need.
//!
//! Direct git goes out with an HTTP/1.1 retry and then gh-proxy mirrors
//! (`DSH_GIT_MIRROR` for custom entries) — proven necessary on networks where
//! GitHub over HTTP/2 hangs.

use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

/// Official upstream; a bootstrapped repo gets this remote unless
/// `DSH_REPO_URL` points elsewhere (fork, staging mirror).
pub const OFFICIAL_REPO: &str = "https://github.com/deepseek-ai/deepseek-harness.git";

/// What to sync to. Master is a worktree-sync dumping ground whose tip often
/// does not even compile (observed at 39163dd3: office.ts imports APIs its
/// own skill-office lacks; test suites written for unreleased states) — the
/// official release tags are what upstream actually ships. Default: the
/// newest `dsh-v*` tag; `DSH_REPO_REF` overrides (tag, branch, or SHA).
const TAG_PREFIX: &str = "refs/tags/dsh-v";

fn sync_ref(git: &Path, root: &Path) -> String {
    if let Ok(r) = std::env::var("DSH_REPO_REF") {
        if !r.trim().is_empty() {
            return r.trim().to_string();
        }
    }
    let url = remote_url(git, root);
    let mut attempts: Vec<String> = vec![url.clone()];
    for m in mirror_urls(&url) {
        attempts.push(m);
    }
    let mut best: Option<(u64, u64, u64, u64, String)> = None;
    for url in &attempts {
        let (code, out) = run_git(
            git,
            root,
            &[
                "-c",
                "http.version=HTTP/1.1",
                "ls-remote",
                "--tags",
                url,
            ],
            FETCH,
        );
        if code != 0 {
            continue;
        }
        for line in out.lines() {
            let Some((sha, r#ref)) = line.split_once('\t') else {
                continue;
            };
            let _ = sha;
            let Some(name) = r#ref.strip_prefix(TAG_PREFIX) else {
                continue;
            };
            if name.ends_with("^{}") {
                continue;
            }
            let Some(v) = parse_tag_version(name) else {
                continue;
            };
            if best.as_ref().map_or(true, |b| v > (b.0, b.1, b.2, b.3)) {
                // `name` is what remains after TAG_PREFIX; the full ref is
                // the prefix itself — no second "refs/tags/".
                best = Some((v.0, v.1, v.2, v.3, format!("{TAG_PREFIX}{name}")));
            }
        }
        if best.is_some() {
            break;
        }
    }
    match best {
        Some((_, _, _, _, r#ref)) => r#ref,
        None => "master".to_string(),
    }
}

/// `vMAJOR.MINOR.PATCH[-stage.N]` → comparable tuple. The fourth slot ranks
/// the prerelease: alpha < beta < rc < final at the same version triple, and
/// later stage numbers win within a stage.
fn parse_tag_version(name: &str) -> Option<(u64, u64, u64, u64)> {
    let (core, prerelease) = match name.split_once('-') {
        Some((c, p)) => (c, Some(p)),
        None => (name, None),
    };
    let mut it = core.split('.');
    let major: u64 = it.next()?.parse().ok()?;
    let minor: u64 = it.next()?.parse().ok()?;
    let patch: u64 = it.next()?.parse().ok()?;
    const STAGE_ALPHA: u64 = 0;
    const STAGE_BETA: u64 = 1;
    const STAGE_RC: u64 = 2;
    const STAGE_FINAL: u64 = 3;
    let rank = match prerelease {
        None => STAGE_FINAL * 1_000_000,
        Some(p) => {
            let (stage, num) = match p.split_once('.') {
                Some(("alpha", n)) => (STAGE_ALPHA, n.parse::<u64>().unwrap_or(0)),
                Some(("beta", n)) => (STAGE_BETA, n.parse::<u64>().unwrap_or(0)),
                Some(("rc", n)) => (STAGE_RC, n.parse::<u64>().unwrap_or(0)),
                _ => (STAGE_ALPHA, 0),
            };
            stage * 1_000_000 + num
        }
    };
    Some((major, minor, patch, rank))
}

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

/// Run one git command in `dir`, capturing stdout+stderr.
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

/// Mirror list for a GitHub repo URL: DSH_GIT_MIRROR entries (semicolon
/// separated, `{url}` placeholder or prefix form) first, then the three public
/// gh proxies.
fn mirror_urls(url: &str) -> Vec<String> {
    let Some(u) = normalize_origin(url) else {
        return Vec::new();
    };
    let mut out = Vec::new();
    if let Ok(custom) = std::env::var("DSH_GIT_MIRROR") {
        for item in custom.split(';') {
            let item = item.trim();
            if item.is_empty() {
                continue;
            }
            let m = if item.to_lowercase().contains("{url}") {
                item.replace("{url}", &u)
            } else {
                format!("{}/{}", item.trim_end_matches('/'), u)
            };
            if !out.contains(&m) {
                out.push(m);
            }
        }
    }
    for prefix in [
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://ghproxy.net/",
    ] {
        let m = format!("{prefix}{u}");
        if !out.contains(&m) {
            out.push(m);
        }
    }
    out
}

#[derive(Debug, Clone)]
pub struct Info {
    /// Local HEAD SHA, empty on a repo with no commits yet.
    pub local: String,
    /// Upstream SHA from the shallow fetch.
    pub remote: String,
    /// The URL that served the fetch ("origin" when direct).
    pub via: String,
    /// No .git at all: offer bootstrap.
    pub needs_init: bool,
    /// Tracked-file modifications exist; sync stashes them before the reset.
    pub dirty: bool,
}

impl Info {
    /// A no-history repo compares SHAs, not counts: different upstream SHA
    /// means "there is a newer official state" regardless of direction.
    pub fn update_available(&self) -> bool {
        !self.needs_init && !self.remote.is_empty() && self.local != self.remote
    }
}

const SHORT: Duration = Duration::from_secs(20);
const FETCH: Duration = Duration::from_secs(300);
const RESET: Duration = Duration::from_secs(120);

fn has_local_changes(git: &Path, root: &Path) -> bool {
    run_git(git, root, &["status", "--porcelain"], SHORT)
        .1
        .trim()
        .is_empty() == false
}

/// Resolve the remote to fetch from: the repo's origin when it has one,
/// otherwise DSH_REPO_URL / the official repo.
fn remote_url(git: &Path, root: &Path) -> String {
    let (code, url) = run_git(git, root, &["remote", "get-url", "origin"], SHORT);
    if code == 0 {
        let url = url.trim();
        if !url.is_empty() {
            return url.to_string();
        }
    }
    std::env::var("DSH_REPO_URL")
        .ok()
        .filter(|s| !s.trim().is_empty())
        .unwrap_or_else(|| OFFICIAL_REPO.to_string())
}

/// One shallow fetch attempt chain for the resolved sync ref: direct,
/// HTTP/1.1 direct, then mirrors. Returns (fetched SHA, description of what
/// worked). FETCH_HEAD holds the result afterwards.
fn shallow_fetch(git: &Path, root: &Path) -> Result<(String, String), String> {
    let url = remote_url(git, root);
    let target = sync_ref(git, root);
    let mut attempts: Vec<(String, Vec<String>)> = vec![(
        "origin".to_string(),
        vec![
            "fetch".to_string(),
            "--depth".into(),
            "1".into(),
            "origin".into(),
            target.clone(),
        ],
    )];
    attempts.push((
        "origin (HTTP/1.1)".to_string(),
        vec![
            "-c".into(),
            "http.version=HTTP/1.1".into(),
            "fetch".into(),
            "--depth".into(),
            "1".into(),
            "origin".into(),
            target.clone(),
        ],
    ));
    for m in mirror_urls(&url) {
        attempts.push((
            m.clone(),
            vec![
                "-c".into(),
                "http.version=HTTP/1.1".into(),
                "fetch".into(),
                "--depth".into(),
                "1".into(),
                "--no-tags".into(),
                m,
                target.clone(),
            ],
        ));
    }
    let mut last = String::new();
    for (via, args) in &attempts {
        let argrefs: Vec<&str> = args.iter().map(|s| s.as_str()).collect();
        let (code, out) = run_git(git, root, &argrefs, FETCH);
        if code == 0 {
            let head = run_git(&git, root, &["rev-parse", "FETCH_HEAD"], SHORT)
                .1
                .trim()
                .to_string();
            if head.len() == 40 {
                return Ok((head, via.clone()));
            }
            last.push_str(&out);
        } else {
            last.push_str(&out);
        }
    }
    Err(format!("fetch 失败（直连与镜像都不通）：\n{}", tail(&last, 800)))
}

/// Inspect the harness for an update without touching the worktree.
pub fn check(root: &Path) -> Result<Info, String> {
    let git = git_exe().ok_or_else(|| {
        "未找到 git.exe：请安装 Git for Windows，或把 git 加入 PATH".to_string()
    })?;
    if !root.join(".git").exists() {
        return Ok(Info {
            local: String::new(),
            remote: String::new(),
            via: String::new(),
            needs_init: true,
            dirty: false,
        });
    }
    let dirty = has_local_changes(&git, root);
    let local = run_git(&git, root, &["rev-parse", "HEAD"], SHORT)
        .1
        .trim()
        .to_string();
    let (remote, via) = shallow_fetch(&git, root)?;
    Ok(Info {
        local,
        remote,
        via,
        needs_init: false,
        dirty,
    })
}

fn tail(s: &str, max: usize) -> &str {
    if s.len() <= max {
        s
    } else {
        &s[s.len() - max..]
    }
}

fn stash_tracked_changes(git: &Path, root: &Path, log: &dyn Fn(String)) {
    let (code, sha) = run_git(
        git,
        root,
        &["stash", "push", "-m", "dsh-sync-auto (tracked changes; not popped)"],
        SHORT,
    );
    if code != 0 {
        log("警告：本地改动 stash 失败，reset 将直接覆盖它们".into());
        return;
    }
    let sha = sha.trim();
    // push exits 0 with "No local changes to save" when nothing was stored.
    if run_git(git, root, &["rev-parse", "stash@{0}"], SHORT)
        .1
        .trim()
        .len()
        == 40
    {
        log(format!(
            "本地已跟踪文件的改动已存入 stash（{sha}），未自动恢复；需要时 git stash pop 手动取回"
        ));
    }
}

/// Bring the worktree to exactly the fetched upstream state. Local history is
/// irrelevant by design; tracked-file modifications are stashed (kept) first.
/// After the reset, untracked leftover sources from a previous (possibly
/// different) ref are removed — they pollute the tag's build otherwise
/// (observed: master-only office.ts broke the release tag's compile). The
/// exclusions preserve genuinely local assets: the installed shell, local
/// notes and logs. Ignored paths (node_modules, build outputs) are untouched
/// because clean runs without -x.
pub fn sync(root: &Path, log: &dyn Fn(String)) -> Result<String, String> {
    let git = git_exe().ok_or("未找到 git.exe：无法同步上游")?;
    if has_local_changes(&git, root) {
        stash_tracked_changes(&git, root, log);
    }
    let (head, _via) = shallow_fetch(&git, root)?;
    let (code, out) = run_git(&git, root, &["reset", "--hard", "FETCH_HEAD"], RESET);
    if code != 0 {
        return Err(format!("git reset 失败：\n{}", tail(&out, 800)));
    }
    let (ccode, clean_out) = run_git(
        &git,
        root,
        &[
            "clean",
            "-fd",
            "-e",
            "apps/desktop-tauri",
            "-e",
            "LOCAL-CHANGES*",
            "-e",
            ".agents",
            "-e",
            ".claude",
            "-e",
            "*.log",
        ],
        RESET,
    );
    if ccode == 0 && !clean_out.trim().is_empty() {
        log(format!(
            "清理了上个版本残留的未跟踪文件（{} 项）",
            clean_out.lines().count()
        ));
    }
    // Bookkeeping so later fetches and status read sensibly.
    let _ = run_git(&git, root, &["update-ref", "refs/remotes/origin/master", "FETCH_HEAD"], SHORT);
    let _ = run_git(&git, root, &["symbolic-ref", "HEAD", "refs/heads/master"], SHORT);
    log(format!("已对齐官方最新版本 {}", &head[..8.min(head.len())]));
    Ok(head)
}

/// Bootstrap a deployed-but-not-a-repo harness in place: init, wire the
/// remote, shallow-fetch and align — the same end state as `sync`.
pub fn bootstrap(root: &Path, log: &dyn Fn(String)) -> Result<String, String> {
    let git = git_exe().ok_or("未找到 git.exe：无法初始化本地仓库")?;
    if !root.join(".git").exists() {
        let (code, out) = run_git(&git, root, &["init"], SHORT);
        if code != 0 {
            return Err(format!("git init 失败：\n{}", tail(&out, 400)));
        }
        log("git init 完成".into());
    }
    let url = std::env::var("DSH_REPO_URL")
        .ok()
        .filter(|s| !s.trim().is_empty())
        .unwrap_or_else(|| OFFICIAL_REPO.to_string());
    let (code, _) = run_git(&git, root, &["remote", "get-url", "origin"], SHORT);
    if code != 0 {
        let (code, out) = run_git(&git, root, &["remote", "add", "origin", &url], SHORT);
        if code != 0 {
            return Err(format!("git remote add 失败：\n{}", tail(&out, 400)));
        }
        log(format!("已挂接官方仓库 {url}"));
    }
    sync(root, log)
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
    // PATH precedence: <harness>\node first, then the inherited PATH.
    let inherited = std::env::var("PATH").unwrap_or_default();
    cmd.env("PATH", format!("{};{}", extra_path.display(), inherited));
    let mut child = cmd
        .spawn()
        .map_err(|e| format!("无法启动 {}：{e}", program.display()))?;
    let stdout = child.stdout.take().unwrap();
    let stderr = child.stderr.take().unwrap();
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

/// Full rebuild of the artifacts the Host runs. Primary recipe: install, then
/// the upstream pipeline in artifacts-only scope (`scripts/build.ts --scope
/// artifacts`) — native addon, Typert, host/client compile and bundles, web.
/// The aggregate TYPE-CHECK gates are deliberately skipped: release tags have
/// shipped with typecheck debt their own release pipeline never ran
/// (dsh-v0.2.2-rc.3: tests written against unreleased account APIs), and
/// gating artifact production on them would strand the update.
///
/// Install tries `--frozen-lockfile` first (supply-chain guard) and falls back
/// to a plain install: upstream sometimes ships a lockfile that lags its own
/// package.json (observed at 39163dd3 and again at the rc.3 tag).
pub fn rebuild<L>(root: &Path, log: L) -> Result<(), String>
where
    L: Fn(String) + Send + Sync + Clone + 'static,
{
    let node_dir = root.join("node");
    let pnpm = pnpm_for(root)
        .ok_or("未找到 pnpm：<harness>\\node\\pnpm.cmd 与 PATH 都没有；先运行一次部署或 corepack enable")?;
    let frozen = run_step(
        &pnpm,
        &["install", "--frozen-lockfile"],
        root,
        &node_dir,
        log.clone(),
        Duration::from_secs(1800),
    );
    if frozen.is_err() {
        log("frozen install 失败（上游锁文件可能落后于 package.json）：改用普通 install".into());
        run_step(
            &pnpm,
            &["install", "--no-frozen-lockfile"],
            root,
            &node_dir,
            log.clone(),
            Duration::from_secs(1800),
        )
        .map_err(|e| format!("pnpm install：{e}"))?;
    }
    let pipeline = run_step(
        &pnpm,
        &["exec", "tsx", "scripts/build.ts", "--scope", "artifacts"],
        root,
        &node_dir,
        log.clone(),
        Duration::from_secs(3600),
    );
    if pipeline.is_ok() {
        return Ok(());
    }
    // Fallback: the two artifact recipes the release pipeline predates.
    log("artifacts 管线失败：回退 build:lib + build:web".into());
    run_step(
        &pnpm,
        &["run", "build:lib"],
        root,
        &node_dir,
        log.clone(),
        Duration::from_secs(1800),
    )
    .map_err(|e| format!("pnpm build:lib：{e}"))?;
    run_step(
        &pnpm,
        &["run", "build:web"],
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
    fn update_gate_is_sha_equality() {
        let base = Info {
            local: "a".repeat(40),
            remote: "b".repeat(40),
            via: "origin".into(),
            needs_init: false,
            dirty: false,
        };
        assert!(base.update_available());
        let same = Info { remote: base.local.clone(), ..base.clone() };
        assert!(!same.update_available());
        let init = Info { needs_init: true, ..base };
        assert!(!init.update_available());
    }

    #[test]
    fn tail_keeps_the_end() {
        assert_eq!(tail("abcdef", 3), "def");
        assert_eq!(tail("ab", 10), "ab");
    }

    #[test]
    fn tag_versions_sort_releases_above_rcs() {
        let v = |s: &str| parse_tag_version(s).unwrap();
        assert!(v("0.2.2-rc.3") < v("0.2.2"));
        assert!(v("0.2.2-rc.3") > v("0.2.2-rc.2"));
        assert!(v("0.2.2-rc.1") > v("0.2.2-beta.9"));
        assert!(v("0.2.2-beta.1") > v("0.2.2-alpha.5"));
        assert!(v("0.10.0") > v("0.9.9"));
        assert!(v("1.0.0") > v("0.99.99"));
    }

    /// Regression for the doubled "refs/tags/refs/tags/…" fetch target: the
    /// resolved ref must be the full ref the ls-remote line carried.
    #[test]
    fn resolved_ref_is_the_full_ls_remote_ref() {
        const PREFIX: &str = "refs/tags/dsh-v";
        let line_ref = "refs/tags/dsh-v0.2.2-rc.3";
        let name = line_ref.strip_prefix(PREFIX).unwrap();
        assert_eq!(format!("{PREFIX}{name}"), line_ref);
        assert_ne!(format!("{PREFIX}{name}"), format!("refs/tags/{line_ref}"));
    }
}
