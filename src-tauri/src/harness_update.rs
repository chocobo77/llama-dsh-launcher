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
//! 3. `restore_local_patches`: the checkout is a run source, not a pristine
//!    official copy — kernel fixes that exist only here (ledger:
//!    LOCAL-CHANGES.txt) live in its sources, and step 2 wipes them: `reset
//!    --hard` over the tracked edits, `clean -fd` over the untracked new file.
//!    The replay reads canonical copies kept OUTSIDE the checkout, so nothing an
//!    update can wipe is the thing that has to restore it.
//! 4. `rebuild`: `pnpm install` + the upstream artifacts pipeline (fallback
//!    `build:lib` + `build:web`), the recipe the deployed artifacts need.
//! 5. `verify_local_changes --stage build`: the checkout's own assertions must
//!    hold over the build products the Host actually loads. A non-zero exit in
//!    2–5 fails the update: the shell is never started on a tree that has
//!    silently lost its defences (2026-11-16/17, LOCAL-CHANGES.txt).
//!
//! Direct git goes out with an HTTP/1.1 retry and then gh-proxy mirrors
//! (`DSH_GIT_MIRROR` for custom entries) — proven necessary on networks where
//! GitHub over HTTP/2 hangs.

use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::{Arc, Mutex};
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
                            // Drain the pump threads so their pipes close
                            // instead of leaking until process exit.
                            let _ = t_out.join();
                            let _ = t_err.join();
                            return (-1, "git command timed out".into());
                        }
                        std::thread::sleep(Duration::from_millis(100));
                    }
                    Err(e) => {
                        let _ = t_out.join();
                        let _ = t_err.join();
                        return (-1, format!("git wait failed: {e}"));
                    }
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

/// The checkout's own patch assertions — untracked, so the clean step below has
/// to spare it or an update deletes the checker it then has to run.
const VERIFY_SCRIPT: &str = "scripts/verify-local-changes.mjs";

/// `git clean -fd` exclusions: what an update spares on purpose.
const CLEAN_EXCLUDES: &[&str] = &[
    "apps/desktop-tauri",
    "LOCAL-CHANGES*",
    VERIFY_SCRIPT,
    ".agents",
    ".claude",
    "*.log",
];

fn clean_args(mode: &str) -> Vec<&str> {
    let mut args: Vec<&str> = vec!["clean", mode];
    for exclude in CLEAN_EXCLUDES {
        args.push("-e");
        args.push(exclude);
    }
    args
}

fn stash_tracked_changes(git: &Path, root: &Path, log: &dyn Fn(String)) -> bool {
    let (code, sha) = run_git(
        git,
        root,
        &["stash", "push", "-m", "dsh-sync-auto (tracked changes; not popped)"],
        SHORT,
    );
    if code != 0 {
        log("警告：本地改动 stash 失败，reset 将直接覆盖它们".into());
        return false;
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
            "本地已跟踪文件的改动已存入 stash（{sha}），未自动恢复；需要时 git stash pop 手动取回（git stash list 可见历史积累）"
        ));
        return true;
    }
    false
}

fn stash_pop_best_effort(git: &Path, root: &Path, log: &dyn Fn(String)) {
    let (code, out) = run_git(git, root, &["stash", "pop", "stash@{0}"], SHORT);
    if code == 0 {
        log("同步失败回滚：已把 stash 的本地改动放回工作树".into());
    } else {
        log(format!(
            "同步失败回滚：stash pop 失败，改动仍在 stash@{{0}}，请手动处理：\n{}",
            tail(&out, 400)
        ));
    }
}

/// Shorten a SHA (or any diagnostic string) safely for logs: char-boundary
/// aware, so a non-SHA payload can never split a UTF-8 sequence.
pub fn short(s: &str) -> String {
    s.chars().take(8).collect()
}

/// Bring the worktree to exactly the fetched upstream state. Local history is
/// irrelevant by design; tracked-file modifications are stashed (kept) first.
/// Any failure AFTER the stash rolls it back (best-effort pop) so a failed
/// network sync never relocates the user's uncommitted work.
///
/// After the reset, untracked leftover sources from a previous (possibly
/// different) ref are removed — they pollute the tag's build otherwise
/// (observed: master-only office.ts broke the release tag's compile). The
/// dry-run logs what will go first; the exclusions preserve genuinely local
/// assets: the installed shell, local notes and logs. Ignored paths
/// (node_modules, build outputs) are untouched because clean runs without -x.
///
/// `reuse_fetch` skips the network fetch, trusting the FETCH_HEAD a
/// `check()` moments earlier produced (same user interaction, seconds apart).
pub fn sync(root: &Path, log: &dyn Fn(String), reuse_fetch: bool) -> Result<String, String> {
    let git = git_exe().ok_or("未找到 git.exe：无法同步上游")?;
    let stashed = if has_local_changes(&git, root) {
        stash_tracked_changes(&git, root, log)
    } else {
        false
    };
    let run = || -> Result<String, String> {
        let (head, _via) = if reuse_fetch {
            let head = run_git(&git, root, &["rev-parse", "FETCH_HEAD"], SHORT)
                .1
                .trim()
                .to_string();
            if head.len() != 40 {
                return Err("没有可用的 FETCH_HEAD（上一轮检查未成功 fetch）".into());
            }
            (head, String::new())
        } else {
            shallow_fetch(&git, root)?
        };
        let (code, out) = run_git(&git, root, &["reset", "--hard", "FETCH_HEAD"], RESET);
        if code != 0 {
            return Err(format!("git reset 失败：\n{}", tail(&out, 800)));
        }
        // Dry-run first: what is about to go is logged before it goes.
        let (_, plan) = run_git(&git, root, &clean_args("-fdn"), SHORT);
        if !plan.trim().is_empty() {
            for line in plan.lines().take(20) {
                log(format!("clean: {line}"));
            }
            let more = plan.lines().count().saturating_sub(20);
            if more > 0 {
                log(format!("clean: …共 {} 项", plan.lines().count()));
            }
        }
        let (ccode, clean_out) = run_git(&git, root, &clean_args("-fd"), RESET);
        if ccode != 0 {
            log(format!("警告：git clean 失败（残留未跟踪文件可能污染下次构建）：\n{}", tail(&clean_out, 400)));
        } else if !clean_out.trim().is_empty() {
            log(format!(
                "清理了上个版本残留的未跟踪文件（{} 项）",
                clean_out.lines().count()
            ));
        }
        // Bookkeeping so later fetches and status read sensibly.
        let _ = run_git(&git, root, &["update-ref", "refs/remotes/origin/master", "FETCH_HEAD"], SHORT);
        let _ = run_git(&git, root, &["symbolic-ref", "HEAD", "refs/heads/master"], SHORT);
        log(format!("已对齐官方最新版本 {}", short(&head)));
        Ok(head)
    };
    match run() {
        Ok(head) => Ok(head),
        Err(e) => {
            if stashed {
                stash_pop_best_effort(&git, root, log);
            }
            Err(e)
        }
    }
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
    sync(root, log, false)
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
    run_captured(program, args, cwd, extra_path, log, timeout).0
}

/// The same, but every streamed line also comes back, so a caller can quote the
/// script's own verdict ("FAIL 锚点失配…") instead of "步骤失败（exit 1）".
fn run_captured<L>(
    program: &Path,
    args: &[&str],
    cwd: &Path,
    extra_path: &Path,
    log: L,
    timeout: Duration,
) -> (Result<(), String>, Vec<String>)
where
    L: Fn(String) + Send + Sync + Clone + 'static,
{
    log(format!("$ {} {}", program.display(), args.join(" ")));
    let lines: Arc<Mutex<Vec<String>>> = Arc::new(Mutex::new(Vec::new()));
    let record = {
        let log = log.clone();
        let lines = lines.clone();
        move |line: String| {
            if !line.trim().is_empty() {
                lines.lock().unwrap().push(line.clone());
                log(format!("  {line}"));
            }
        }
    };
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
    let mut child = match cmd.spawn() {
        Ok(child) => child,
        Err(e) => {
            return (
                Err(format!("无法启动 {}：{e}", program.display())),
                Vec::new(),
            )
        }
    };
    let stdout = child.stdout.take().unwrap();
    let stderr = child.stderr.take().unwrap();
    let t_err: std::thread::JoinHandle<()> = {
        let record = record.clone();
        std::thread::spawn(move || {
            let reader = BufReader::new(stderr);
            for line in reader.lines().map_while(Result::ok) {
                record(line);
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
            Ok(l) => record(l),
            Err(_) => break,
        }
    }
    if timed_out {
        let _ = child.kill();
        let _ = child.wait();
        return (
            Err("步骤超时".into()),
            lines.lock().unwrap().clone(),
        );
    }
    let _ = t_err.join();
    let collected = lines.lock().unwrap().clone();
    let status = match child.wait() {
        Ok(status) => status,
        Err(e) => {
            return (
                Err(format!("等待步骤结束失败：{e}")),
                collected,
            )
        }
    };
    if status.success() {
        (Ok(()), collected)
    } else {
        (
            Err(format!("步骤失败（exit {}）", status.code().unwrap_or(-1))),
            collected,
        )
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

// ── Local kernel patches: replay (阶段 2) and gates (阶段 0 / 4) ─────────────
//
// Why the updater owns this: a wiped patch prints nothing anywhere. The model
// just starts writing tool calls as prose and no one catches it, until a
// production session degrades — that is how 2026-11-16/17 went. So the update
// itself replays the patches and refuses to come back up on a tree that fails
// its own assertions.

/// Canonical patch copies (restore script + patch sources) live here, outside the
/// checkout: a copy inside it can be wiped by the very update it must recover.
pub const PATCHES_DIR_ENV: &str = "DSH_LOCAL_PATCHES_DIR";
/// Non-empty = run an update with no patch stages at all, for a deployment that
/// carries no kernel patches. Opting out of the gate stays deliberate & logged.
pub const SKIP_PATCHES_ENV: &str = "DSH_SKIP_LOCAL_PATCHES";
const RESTORE_SCRIPT: &str = "restore-local-patches.mjs";

pub fn skip_local_patches() -> bool {
    std::env::var_os(SKIP_PATCHES_ENV).map_or(false, |v| !v.is_empty())
}

/// Does this checkout carry its own assertions at all? Without them there is no
/// verdict to report — that must not read as "the patches are gone".
pub fn has_patch_verifier(root: &Path) -> bool {
    root.join(VERIFY_SCRIPT).is_file()
}

/// Where the canonical copies are. An explicit DSH_LOCAL_PATCHES_DIR is taken
/// at face value: a wrong one fails loudly rather than falling back to another
/// tree's copies and silently replaying those instead.
pub fn local_patches_dir(root: &Path) -> Option<PathBuf> {
    if let Some(dir) = std::env::var_os(PATCHES_DIR_ENV).filter(|s| !s.is_empty()) {
        let dir = PathBuf::from(dir);
        return dir.join(RESTORE_SCRIPT).is_file().then_some(dir);
    }
    local_patches_candidates(root)
        .into_iter()
        .find(|dir| dir.join(RESTORE_SCRIPT).is_file())
}

fn local_patches_candidates(root: &Path) -> Vec<PathBuf> {
    let mut out = vec![root.join("harness-patches")];
    if let Some(parent) = root.parent() {
        out.push(parent.join("harness-patches"));
    }
    // Dev layout: <DSH>\dsh-desktop-tauri\src-tauri\target\release\<exe> → <DSH>.
    if let Ok(exe) = std::env::current_exe() {
        let mut next = exe.parent().map(|p| p.to_path_buf());
        for _ in 0..6 {
            let Some(ancestor) = next else { break };
            out.push(ancestor.join("harness-patches"));
            next = ancestor.parent().map(|p| p.to_path_buf());
        }
    }
    // This deployment's documented home (UPDATE-RULE-REQUEST.md §4).
    out.push(PathBuf::from(r"D:\code\DSH\harness-patches"));
    out
}

/// The script's verdict, not its progress chatter: what a dialog can show.
fn patch_verdict(lines: &[String]) -> String {
    let mut kept: Vec<&str> = lines
        .iter()
        .map(|l| l.trim())
        .filter(|l| {
            l.starts_with("FAIL")
                || l.starts_with("MISSING")
                || l.contains("missing —")
                || l.contains("未应用")
        })
        .collect();
    if kept.is_empty() {
        if let Some(last) = lines.last() {
            kept.push(last.trim());
        }
    }
    kept.truncate(8);
    kept.join("\n")
}

fn quote_verdict(err: String, out: &[String]) -> String {
    let verdict = patch_verdict(out);
    if verdict.is_empty() {
        err
    } else {
        format!("{err}\n{verdict}")
    }
}

/// 阶段 0 / 阶段 4: the checkout's own assertions. stage = "source" (the
/// worktree) or "build" (the lib/ products the Host actually loads). Non-zero
/// exit ⇒ the update fails; 阶段 0 only records it, because patches already
/// missing before the overwrite is exactly what 阶段 2 exists to repair.
pub fn verify_local_changes<L>(root: &Path, stage: &str, log: L) -> Result<(), String>
where
    L: Fn(String) + Send + Sync + Clone + 'static,
{
    let script = root.join(VERIFY_SCRIPT);
    if !script.is_file() {
        return Err(format!("缺少校验脚本：{}", script.display()));
    }
    let node = crate::server::node_for_checkout(root);
    let script_arg = script.to_string_lossy().to_string();
    let mut argv: Vec<&str> = vec![script_arg.as_str()];
    if stage == "build" {
        argv.extend_from_slice(&["--stage", "build"]);
    }
    let (result, out) = run_captured(
        Path::new(&node),
        &argv,
        root,
        &root.join("node"),
        log,
        Duration::from_secs(180),
    );
    result.map_err(|e| quote_verdict(e, &out))
}

/// 阶段 2: replay the patch sources and the four anchor wirings. An anchor
/// mismatch exits 1 by design — a new official structure needs a human, and a
/// silently skipped patch is the exact failure this stage exists to catch.
pub fn restore_local_patches<L>(root: &Path, log: L) -> Result<(), String>
where
    L: Fn(String) + Send + Sync + Clone + 'static,
{
    let dir = local_patches_dir(root).ok_or_else(|| {
        format!(
            "找不到 {RESTORE_SCRIPT}（依次查过：{}）。把补丁规范副本放好，或用 {PATCHES_DIR_ENV} 指向它。",
            local_patches_candidates(root)
                .iter()
                .map(|p| p.display().to_string())
                .collect::<Vec<_>>()
                .join("、")
        )
    })?;
    let script = dir.join(RESTORE_SCRIPT);
    let node = crate::server::node_for_checkout(root);
    let script_arg = script.to_string_lossy().to_string();
    let root_arg = root.to_string_lossy().to_string();
    let (result, out) = run_captured(
        Path::new(&node),
        &[script_arg.as_str(), root_arg.as_str()],
        root,
        &root.join("node"),
        log,
        Duration::from_secs(300),
    );
    result.map_err(|e| quote_verdict(e, &out))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clean_excludes_spare_the_patch_checker() {
        assert!(CLEAN_EXCLUDES.contains(&VERIFY_SCRIPT));
        assert!(CLEAN_EXCLUDES.contains(&"LOCAL-CHANGES*"));
        let args = clean_args("-fd");
        assert_eq!(args[0], "clean");
        assert_eq!(args[1], "-fd");
        assert_eq!(
            args.iter().filter(|a| **a == "-e").count(),
            CLEAN_EXCLUDES.len()
        );
    }

    #[test]
    fn patch_verdict_keeps_failures_and_drops_progress() {
        let lines = vec![
            "OK   packages/core/agent-loop/src/text-tool-call.ts（新文件已复制）".to_string(),
            "FAIL turn 收尾: 锚点失配（官方 if 结构变化），需人工适配".to_string(),
            "2 处补丁未应用——启动流程必须中止，人工恢复后重试".to_string(),
        ];
        let v = patch_verdict(&lines);
        assert!(v.contains("锚点失配"));
        assert!(!v.contains("OK"));
    }

    #[test]
    fn patch_candidates_probe_checkout_sibling_and_documented_home() {
        let c = local_patches_candidates(Path::new(r"D:\Program files\deepseek-harness"));
        assert!(c.contains(&PathBuf::from(r"D:\Program files\harness-patches")));
        assert!(c.contains(&PathBuf::from(r"D:\code\DSH\harness-patches")));
    }

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
