use std::net::TcpListener;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::sync::{Mutex, OnceLock};
use std::thread;
use std::time::{Duration, Instant};

use tauri::{AppHandle, Manager};

/// The shell's own fixed host port. It is deliberately not `3080`: that is the
/// default a `dsh web` started from a terminal (or a dev session) binds, and a
/// collision there used to push the shell onto an OS-assigned port. The port is
/// part of the main window's origin (`http://127.0.0.1:<port>`, required by the
/// `SameSite=Strict` session cookie), and cookies on `127.0.0.1` ignore ports:
/// every new port adds another session cookie to the same jar, sent to every
/// DSH port, until the request headers pass Node's 16 KB cap and the whole UI
/// answers `431`. A stable port means one cookie that gets replaced.
/// The board lock records the owner's start time; WMI reports process creation
/// to the second, so compare with slack. Reused PIDs differ by far more.
/// How long the process-enumeration helper may take before it is abandoned: a
/// stalled query must not become a stalled Host.
const ENUMERATION_TIMEOUT: Duration = Duration::from_secs(5);

const START_TIME_TOLERANCE_MS: u64 = 120_000;

pub const DEFAULT_PORT: u16 = 3090;
pub const LOG_CAP: usize = 300;

/// Ceiling for one line kept in the in-memory ring: the whole ring is
/// serialized to the splash on every poll, so an unbounded line is an
/// unbounded IPC payload. The log file keeps the full line.
const RING_LINE_MAX: usize = 2_000;

/// How long a Ctrl+C'd host gets to dispose its tree before the shell kills it.
const GRACEFUL_EXIT_TIMEOUT: Duration = Duration::from_millis(3_000);

/// Everything the shell knows about the supervised `dsh web` child process.
/// The splash page polls `server_status`; the supervisor thread (lib.rs) and
/// the tray/menu drive the lifecycle — mirroring what apps/desktop's
/// backend-controller does for Electron.
#[derive(Default)]
pub struct ServerState {
    pub child: Mutex<Option<std::process::Child>>,
    pub url: Mutex<Option<String>>,
    pub error: Mutex<Option<String>>,
    pub exit_code: Mutex<Option<i32>>,
    pub spawning: Mutex<bool>,
    pub port: Mutex<u16>,
    pub log: Mutex<Vec<String>>,
    /// Cleared when the supervisor should stop supervising (quit paths).
    pub quit_requested: Mutex<bool>,
    /// Monotonic id of the current host instance. Only that instance's own
    /// pumps may publish a token URL, so a straggler line from a previous
    /// instance (its grandchildren can hold the pipe open) cannot revive a
    /// dead URL and have the supervisor load it into the main window.
    pub generation: Mutex<u64>,
    /// Set by a manual restart while the supervisor is paused after spending
    /// its crash budget; read by the supervisor's pause wait.
    pub resume_requested: Mutex<bool>,
    /// The most recent Host PIDs this shell started and stopped. A restart stops
    /// the Host, clears the child slot, then spawns — and the Host can still be
    /// tearing down when the new spawn's takeover enumerates processes. Without
    /// this, our own dying process reads as "1 other host" and gets Ctrl+C and a
    /// force-kill in the log, which is both misleading and a needless delay.
    pub recent_children: Mutex<Vec<u32>>,
    /// What made the supervisor launch the current Host, so a takeover line in
    /// the log says who asked. A crash restart silently retiring a Host the user
    /// started minutes earlier otherwise reads as an unprovoked kill.
    pub spawn_reason: Mutex<String>,
    /// Set when a restart was requested while a spawn was in flight and
    /// `stop_server` therefore found nothing to stop. `spawn_server` consumes it
    /// at its tail: without this, clicking "restart" during a spawn is a silent
    /// no-op that leaves the user staring at the same Host.
    pub restart_requested: Mutex<bool>,
    /// Crash restarts spent so far, surfaced to the splash so the count is
    /// not only inferable from log text.
    pub restarts: Mutex<u32>,
}

/// Shell and Host log lines share this entry point: UTC-timestamped,
/// token-masked, full-length in the file, ring-capped in memory. A log
/// without timestamps makes a crash loop's cadence unjudgeable after the fact.
pub fn push_log(state: &ServerState, line: String) {
    let stamped = format!("{} {}", utc_stamp(), redact_secrets(&line));
    {
        let mut log = state.log.lock().unwrap();
        if log.len() >= LOG_CAP {
            log.remove(0);
        }
        log.push(shorten(&stamped));
    }
    append_log_file(&stamped);
}

fn utc_stamp() -> String {
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let (y, m, d) = civil_from_days((secs / 86_400) as i64);
    let rem = secs % 86_400;
    format!(
        "{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}Z",
        rem / 3600,
        (rem % 3600) / 60,
        rem % 60
    )
}

/// Days since 1970-01-01 (signed) to a proleptic Gregorian date. Hand-rolled
/// so a log prefix does not cost a date dependency.
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

/// Mask `token=<value>`: the Host prints its authenticated URL on stdout and
/// every stdout line is mirrored into `%TEMP%\dsh-tauri.log`.
fn redact_secrets(line: &str) -> String {
    let mut out = String::with_capacity(line.len());
    let mut rest = line;
    while let Some(i) = rest.find("token=") {
        out.push_str(&rest[..i + "token=".len()]);
        let value = &rest[i + "token=".len()..];
        let end = value
            .find(|c: char| c == '&' || c == ' ' || c == '"' || c == '\'' || c == ')')
            .unwrap_or(value.len());
        out.push_str("***");
        rest = &value[end..];
    }
    out.push_str(rest);
    out
}

fn shorten(line: &str) -> String {
    if line.len() <= RING_LINE_MAX {
        return line.to_string();
    }
    let mut cut = RING_LINE_MAX;
    while !line.is_char_boundary(cut) {
        cut -= 1;
    }
    let mut cut_line = line[..cut].to_string();
    cut_line.push_str(" … (truncated)");
    cut_line
}

pub struct LaunchPlan {
    pub program: String,
    pub args: Vec<String>,
    pub cwd: Option<PathBuf>,
    pub source: &'static str,
}

fn env_var(name: &str) -> Option<String> {
    std::env::var(name)
        .ok()
        .map(|v| v.trim().to_string())
        .filter(|v| !v.is_empty())
}

/// `resources/cli/lib/bin.js` from the bundle, resolved once in setup.
/// Bundled via `bundle.resources` (see tauri.conf.json); the shipped folder
/// only holds a placeholder until a runtime is dropped in — the existence
/// check below decides whether it counts.
static BUNDLED_CLI: OnceLock<Option<PathBuf>> = OnceLock::new();
static BUNDLED_NODE: OnceLock<Option<PathBuf>> = OnceLock::new();

pub fn set_bundled_cli(path: PathBuf) {
    let _ = BUNDLED_CLI.set(Some(path));
}

pub fn set_bundled_node(path: PathBuf) {
    let _ = BUNDLED_NODE.set(Some(path));
}

fn bundled_cli() -> Option<PathBuf> {
    BUNDLED_CLI.get().cloned().flatten()
}

fn node_program() -> String {
    env_var("DSH_TAURI_NODE").unwrap_or_else(|| {
        BUNDLED_NODE
            .get()
            .cloned()
            .flatten()
            .map(|p| p.to_string_lossy().into_owned())
            .unwrap_or_else(|| "node".to_string())
    })
}

pub fn log_file_path() -> &'static PathBuf {
    static LOG: OnceLock<PathBuf> = OnceLock::new();
    LOG.get_or_init(|| std::env::temp_dir().join("dsh-tauri.log"))
}

pub fn append_log_file(line: &str) {
    use std::io::Write;
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(log_file_path())
    {
        let _ = writeln!(f, "{line}");
    }
}

/// Walk up from the executable looking for the monorepo checkout:
/// <checkout>/apps/desktop-tauri/src-tauri/target/(debug|release)/<exe> in dev.
fn default_checkout() -> Option<PathBuf> {
    // Since the app left the monorepo, "walk up until we find apps/cli" only
    // works when it happens to live inside a checkout. Name it when it does not:
    // DSH_REPO_ROOT is the same variable scripts/prepare-runtime-cli.mjs uses.
    if let Some(root) = env_var("DSH_REPO_ROOT").map(PathBuf::from) {
        if root.join("apps").join("cli").join("lib").join("bin.js").is_file() {
            return Some(root);
        }
    }
    let exe = std::env::current_exe().ok()?;
    let mut dir = exe.parent()?.to_path_buf();
    for _ in 0..8 {
        if dir.join("apps").join("cli").join("lib").join("bin.js").is_file() {
            return Some(dir);
        }
        dir = dir.parent()?.to_path_buf();
    }
    None
}

/// Is `dsh` on PATH (the shim the Desktop "管理 dsh 命令" flow or npm installs)?
fn dsh_on_path() -> bool {
    let mut cmd = Command::new("where.exe");
    cmd.arg("dsh").stdout(Stdio::null()).stderr(Stdio::null());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW: no probe flash
    }
    cmd.status().map(|s| s.success()).unwrap_or(false)
}

/// Resolve how to start the host. Ladder, first hit wins:
/// 1. `DSH_TAURI_CMD` (+ optional `DSH_TAURI_ARGS` JSON array, `DSH_TAURI_CWD`): fully custom.
/// 2. Bundled runtime: `resources/cli/lib/bin.js` (see `bundle.resources`).
/// 3. `DSH_CHECKOUT`: checkout root whose `apps/cli/lib/bin.js` runs `dsh web`.
/// 4. Dev default: monorepo checkout inferred from the exe location.
/// 5. `dsh` on PATH (`cmd.exe /C dsh web …`). The wrapper is never left behind
///    with its node child: every child goes into a kill-on-close job object.
pub fn launch_plan(port: u16) -> Result<LaunchPlan, String> {
    if let Some(cmd) = env_var("DSH_TAURI_CMD") {
        let args = match env_var("DSH_TAURI_ARGS") {
            Some(json) => serde_json::from_str::<Vec<String>>(&json)
                .map_err(|e| format!("DSH_TAURI_ARGS 不是合法的 JSON 数组: {e}"))?,
            None => default_web_args(port),
        };
        return Ok(LaunchPlan {
            program: cmd,
            args,
            cwd: env_var("DSH_TAURI_CWD").map(PathBuf::from),
            source: "DSH_TAURI_CMD",
        });
    }

    if let Some(cli) = bundled_cli() {
        if cli.is_file() {
            let cwd = workspace_override()
                .or_else(default_workspace)
                .or_else(|| cli.parent().and_then(|p| p.parent()).map(PathBuf::from));
            return Ok(LaunchPlan {
                program: node_program(),
                args: cli_args(cli, port),
                cwd,
                source: "bundled resources/cli",
            });
        }
    }

    let checkout = match env_var("DSH_CHECKOUT").map(PathBuf::from) {
        Some(c) => {
            if c.join("apps").join("cli").join("lib").join("bin.js").is_file() {
                Some(c)
            } else {
                return Err(format!(
                    "DSH_CHECKOUT（{}）下找不到 apps/cli/lib/bin.js",
                    c.display()
                ));
            }
        }
        None => default_checkout(),
    };

    if let Some(checkout) = checkout {
        let bin = checkout.join("apps").join("cli").join("lib").join("bin.js");
        return Ok(LaunchPlan {
            program: node_program(),
            args: cli_args(bin, port),
            cwd: Some(workspace_override().unwrap_or(checkout)),
            source: "checkout apps/cli",
        });
    }

    if dsh_on_path() {
        return Ok(LaunchPlan {
            program: "cmd.exe".into(),
            args: {
                let mut a = vec!["/C".into(), "dsh".into()];
                a.extend(default_web_args(port));
                a
            },
            cwd: None,
            source: "PATH dsh",
        });
    }

    Err("未找到 dsh 运行时：安装包内应捆绑 resources/cli，或设置 DSH_CHECKOUT/DSH_TAURI_CMD，或安装 dsh 命令".into())
}

fn cli_args(bin: PathBuf, port: u16) -> Vec<String> {
    let mut a = vec![bin.to_string_lossy().into_owned()];
    a.extend(default_web_args(port));
    a
}

fn default_web_args(port: u16) -> Vec<String> {
    web_args(&web_profile(), port)
}

/// The profile to boot: `dsh <profile> …` (the CLI expands a bare name to
/// `--profile <name>`). `web` is the shipped web profile. `DSH_TAURI_PROFILE`
/// moves the installed app onto a profile of its own, so its plugin layer stops
/// colliding with a profile someone is editing from a terminal. `desktop` is
/// reserved by the CLI for the Electron shell and is rejected there, so pick a
/// different name.
fn web_profile() -> String {
    env_var("DSH_TAURI_PROFILE")
        .filter(|p| !p.trim().is_empty())
        .unwrap_or_else(|| "web".into())
}

/// Launcher arguments: the profile name first, then the app's own arguments.
fn web_args(profile: &str, port: u16) -> Vec<String> {
    vec![
        profile.into(),
        "--no-open".into(),
        "--port".into(),
        port.to_string(),
    ]
}

/// The Host's working directory. Sessions are bucketed per workspace, so an
/// installed app started inside its own install directory shows an unrelated —
/// effectively empty — session list. `DSH_TAURI_CWD` pins the workspace the app
/// should continue; otherwise a GUI install falls back to the user's home, a
/// stable and meaningful bucket.
fn workspace_override() -> Option<PathBuf> {
    env_var("DSH_TAURI_CWD")
        .filter(|p| !p.trim().is_empty())
        .map(PathBuf::from)
}

fn default_workspace() -> Option<PathBuf> {
    env_var("USERPROFILE").map(PathBuf::from)
}

/// `DSH_TAURI_HOME` becomes the child's `DSH_HOME`: the machine-level state root
/// holding sessions, logs, and the task-board ledger lock
/// (`$DSH_HOME/task-board/ledger-v2.lock`). The board is single-owner per home,
/// so an installed app that must run beside a terminal session needs its own
/// home — a different profile alone does not lift that lock.
fn home_override() -> Option<String> {
    env_var("DSH_TAURI_HOME").filter(|h| !h.trim().is_empty())
}

/// The task-board plugin records its owner in `$DSH_HOME/task-board/
/// ledger-v2.lock` as `{"pid":…,"token":"…","startedAt":<ms>,"probe":"exact"}`.
#[derive(Debug, PartialEq, Eq)]
pub struct BoardOwner {
    pub pid: u32,
    pub started_at_ms: Option<u64>,
}

/// Read the owner out of a lock file body. Anything unreadable means "no
/// takeover", never a guess.
pub fn parse_board_lock(text: &str) -> Option<BoardOwner> {
    let value: serde_json::Value = serde_json::from_str(text).ok()?;
    let pid = value.get("pid")?.as_u64()? as u32;
    if pid == 0 {
        return None;
    }
    Some(BoardOwner {
        pid,
        started_at_ms: value.get("startedAt").and_then(|v| v.as_u64()),
    })
}

/// Should this row be treated as a DSH web Host? A takeover force-kills process
/// trees on the strength of this decision, so the evidence is more than a
/// substring: the image has to *be* a JS runtime (not end with one —
/// `mynode.exe` is somebody else's program), the script has to be `bin.js`, the
/// argument after it has to be exactly `web` (`web-server` is another tool), and
/// the script file has to exist (a `cmd.exe /c set "PATH=…bin.js web…"` wrapper
/// mentions the text without running it).
pub fn is_dsh_host_row(name: &str, cmdline: &str) -> bool {
    let script = match dsh_host_script(cmdline) {
        Some(script) => script,
        None => return false,
    };
    const RUNTIMES: [&str; 3] = ["node.exe", "bun.exe", "deno.exe"];
    if !RUNTIMES.iter().any(|r| name.eq_ignore_ascii_case(r)) {
        return false;
    }
    match script_kind(&script) {
        // Absolute: the file has to exist, which is what rejects a PATH dump or
        // an echo that merely contains the text.
        ScriptKind::Absolute => std::path::Path::new(&script).is_file(),
        // Relative — `node apps/cli/lib/bin.js web`, the shape a developer gets
        // when starting the Host from a checkout. It is relative to *that*
        // process's working directory, which we cannot read, and resolving it
        // against our own cwd was a real regression: it hid a live Host and the
        // takeover never happened. The image gate, the path-boundary rule and
        // the whole-`web`-argument rule still have to carry the decision.
        ScriptKind::Relative => true,
        // Fragments of an env dump or an echo: separators that no single path
        // token contains.
        ScriptKind::NotAPath => false,
    }
}

/// Why a rejected lock owner was rejected, so the log stops being a dead end.
pub fn host_rejection_reason(name: &str, cmdline: &str) -> String {
    const RUNTIMES: [&str; 3] = ["node.exe", "bun.exe", "deno.exe"];
    match dsh_host_script(cmdline) {
        None => "命令行里没有 `bin.js` + 完整参数 `web`".into(),
        Some(script) => {
            if !RUNTIMES.iter().any(|r| name.eq_ignore_ascii_case(r)) {
                format!("镜像名是 {name}，不是 node.exe/bun.exe/deno.exe")
            } else {
                match script_kind(&script) {
                    ScriptKind::NotAPath => format!("{script} 不像一个路径"),
                    // Not reachable today: a relative script passes the gate.
                    // Kept honest so nobody reads it as a real rejection.
                    ScriptKind::Relative => format!("{script} 是相对路径（不做存在性校验，不应出现在这里）"),
                    ScriptKind::Absolute => format!("{script} 这个文件不存在"),
                }
            }
        }
    }
}

enum ScriptKind {
    Absolute,
    Relative,
    NotAPath,
}

fn script_kind(script: &str) -> ScriptKind {
    let body = script.strip_prefix("//?/").unwrap_or(script);
    let bytes = body.as_bytes();
    // A drive prefix: `c:/…` is absolute, `d:bin.js` is drive-*relative* and
    // belongs to the other process's cwd, which we cannot read. Treating the
    // second form as absolute was the mistake that hid a live Host once.
    if bytes.get(1) == Some(&b':') && bytes.first().is_some_and(|c| c.is_ascii_alphabetic()) {
        return if matches!(bytes.get(2), Some(b'/')) {
            ScriptKind::Absolute
        } else {
            ScriptKind::Relative
        };
    }
    // UNC share.
    if body.starts_with("//") {
        return ScriptKind::Absolute;
    }
    // A `:` or `;` anywhere else means the token spans a list (`PATH=a;b`,
    // `--require=c:/x`), i.e. it came from an env dump rather than a command.
    if body.contains(':') || body.contains(';') {
        return ScriptKind::NotAPath;
    }
    ScriptKind::Relative
}

/// The `bin.js` a command line runs, when it runs `bin.js web`; `None` otherwise.
/// Case-folding keeps every index aligned with the case-preserving copy, so the
/// path can be cut from either.
pub fn dsh_host_script(cmdline: &str) -> Option<String> {
    // Both device-path spellings, then one separator style: every caller works
    // on this shape, so `//?/` cannot be stripped in one place and missed in
    // another.
    let flat = cmdline
        .replace("\\\\?\\", "")
        .replace('\\', "/")
        .replace("//?/", "");
    let lower = flat.to_ascii_lowercase();
    let bytes = lower.as_bytes();
    const NAME: &str = "bin.js";
    let mut at = 0usize;
    while let Some(hit) = lower[at..].find(NAME) {
        let start = at + hit;
        // A path boundary, not the tail of some other name. `:` counts because
        // a drive-relative token (`d:bin.js`) puts the drive right in front of
        // it; anything that came from a `PATH=`/`--require=` fragment is caught
        // one step later, where a `:` in the middle marks a non-path token.
        let before_ok = start == 0 || matches!(bytes[start - 1], b'/' | b'"' | b'\'' | b' ' | b':');
        let mut token = start;
        while token > 0 && !matches!(bytes[token - 1], b' ' | b'"' | b'\'') {
            token -= 1;
        }
        let mut j = start + NAME.len();
        // Command lines quote the path: `"…\bin.js" web`.
        while j < bytes.len() && matches!(bytes[j], b' ' | b'"' | b'\'') {
            j += 1;
        }
        let whole_argument = matches!(bytes.get(j + 3), None | Some(b' ') | Some(b'"') | Some(b'\''));
        if before_ok && lower[j..].starts_with("web") && whole_argument {
            return Some(flat[token..start + NAME.len()].to_string());
        }
        at = start + NAME.len();
    }
    None
}

/// Last-writer-wins policy for the single-owner board: two hosts cannot share
/// one `$DSH_HOME`, and running the desktop app beside a terminal session
/// should not require a second state root. So before starting its Host the
/// shell retires whichever DSH host currently owns the board.
/// `DSH_TAURI_TAKEOVER=0` keeps the non-intrusive behaviour instead.
fn takeover_enabled() -> bool {
    match env_var("DSH_TAURI_TAKEOVER") {
        Some(v) => !matches!(
            v.trim().to_ascii_lowercase().as_str(),
            "0" | "false" | "off" | "no"
        ),
        None => true,
    }
}

/// The home whose board is at stake: the override the shell itself injects, or
/// the inherited `DSH_HOME`, or `%USERPROFILE%\.dsh`.
fn board_lock_path() -> Option<PathBuf> {
    let home = home_override()
        .map(PathBuf::from)
        .or_else(|| env_var("DSH_HOME").map(PathBuf::from))
        .or_else(|| env_var("USERPROFILE").map(|u| PathBuf::from(u).join(".dsh")))?;
    Some(home.join("task-board").join("ledger-v2.lock"))
}

/// Resolve the host port. No fallback to an ephemeral port on purpose: a
/// drifting port silently creates a new origin (see `DEFAULT_PORT`), which both
/// breaks "open the same address in a browser" support and grows the shared
/// `127.0.0.1` cookie jar until the host starts answering 431. A conflict is
/// reported to the splash instead.
pub fn pick_port(preferred: u16) -> Result<u16, String> {
    match TcpListener::bind(("127.0.0.1", preferred)) {
        Ok(_) => Ok(preferred),
        Err(e) if e.kind() == std::io::ErrorKind::AddrInUse => Err(format!(
            "端口 {preferred} 已被占用（另一个 DSH host 未退出，或本机其他服务）。用 DSH_TAURI_PORT 固定一个端口，或先退出占用进程；外壳不会改用随机端口，因为换端口会换掉主窗 origin 并累积 127.0.0.1 的会话 cookie。"
        )),
        Err(e) => Err(format!("无法探测端口 {preferred}: {e}")),
    }
}

/// Take the child down. The watchdog then leaves; the supervisor sees an
/// idle state (no exit_code) and treats it as an intentional restart or quit.
///
/// `dsh` disposes its whole tree on SIGINT/SIGTERM (`apps/cli/src/profile-boot.ts`),
/// so the stop is two-phase: a console Ctrl+C first, then `GRACEFUL_EXIT_TIMEOUT`
/// to finish, then `kill`. The wait runs with the child lock released: a
/// `spawn_server` in flight blocks on that lock and is stopped here once it
/// publishes its child, instead of being missed and left running.
pub fn stop_server(state: &ServerState) {
    let taken = state.child.lock().unwrap().take();
    *state.spawning.lock().unwrap() = false;
    *state.url.lock().unwrap() = None;
    *state.error.lock().unwrap() = None;
    *state.exit_code.lock().unwrap() = None;

    if taken.is_none() && *state.spawning.lock().unwrap() {
        // A spawn is in flight and owns no child slot yet: remember the restart
        // so the spawn tail tears the child down as soon as it exists.
        *state.restart_requested.lock().unwrap() = true;
        push_log(state, "停止请求到达时正在启动新 host：已登记，启动完成后立即重启".into());
    }
    if let Some(mut child) = taken {
        {
            let mut recent = state.recent_children.lock().unwrap();
            recent.push(child.id());
            // A handful is plenty: the window we care about is seconds wide.
            if recent.len() > 8 {
                recent.remove(0);
            }
        }
        let signalled = request_graceful_exit(child.id());
        #[cfg(windows)]
        if !signalled {
            push_log(
                state,
                format!(
                    "优雅停止不可用：Ctrl+C 未送达（诊断 {}），将强制结束",
                    win::ctrl_c_diagnostic()
                ),
            );
        }
        let deadline = Instant::now() + GRACEFUL_EXIT_TIMEOUT;
        while signalled
            && child.try_wait().ok().flatten().is_none()
            && Instant::now() < deadline
        {
            thread::sleep(Duration::from_millis(100));
        }
        if child.try_wait().ok().flatten().is_none() {
            let _ = child.kill();
        }
        let _ = child.wait();
    }
}

/// One row of the process enumeration. `created_ms` is what the force-kill path
/// re-checks: a PID that exited and got recycled must not be killed over.
#[derive(Debug)]
pub struct ProcRow {
    pub pid: u32,
    pub created_ms: Option<u64>,
    pub name: String,
    pub parent: u32,
    pub session: u32,
    pub cmdline: String,
}

/// Run one PowerShell helper with a deadline, returning its stdout. A stalled
/// helper must never become a stalled Host, so it is killed and the caller sees
/// an empty string (and logs why).
#[cfg(windows)]
fn run_helper(script: &str, limit: Duration) -> String {
    use std::os::windows::process::CommandExt;
    // (the helper joins the kill-on-close Job right after it spawns)
    let mut child = match Command::new("powershell.exe")
        .args(["-NoProfile", "-NonInteractive", "-Command", script])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .creation_flags(0x0800_0000) // CREATE_NO_WINDOW
        .spawn()
    {
        Ok(child) => child,
        Err(_) => return String::new(),
    };
    // Same Job as the Host: if the shell is killed outright, a helper mid-query
    // must not be left running as an orphan.
    let _ = win::assign_to_kill_job(&child);
    let stdout = match child.stdout.take() {
        Some(out) => out,
        None => {
            let _ = child.kill();
            return String::new();
        }
    };
    let (sender, receiver) = std::sync::mpsc::channel();
    thread::spawn(move || {
        let mut stdout = stdout;
        let mut bytes = Vec::new();
        if std::io::Read::read_to_end(&mut stdout, &mut bytes).is_ok() {
            let _ = sender.send(bytes);
        }
    });
    match receiver.recv_timeout(limit) {
        Ok(bytes) => {
            let _ = child.wait();
            String::from_utf8_lossy(&bytes).into_owned()
        }
        Err(_) => {
            let _ = child.kill();
            let _ = child.wait();
            String::new()
        }
    }
}

/// Every process outside session 0 that has a command line, in one CIM call per
/// Host launch. Session 0 is excluded because a Host there is a service, not the
/// user's other DSH; every other session is in scope because the two Hosts
/// fighting over one state root are routinely in different login sessions (a
/// console terminal and an app started from Explorer, an RDP session and the
/// console) — filtering to our own session hides the very process to retire.
#[cfg(windows)]
fn list_session_processes() -> Result<Vec<ProcRow>, &'static str> {
    use std::os::windows::process::CommandExt;
    let script = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-CimInstance Win32_Process | \
        Where-Object { $_.CommandLine -and $_.SessionId -ne 0 } | \
        ForEach-Object { \"$($_.ProcessId)|$(([DateTimeOffset]$_.CreationDate).ToUnixTimeMilliseconds())|$($_.Name)|$($_.ParentProcessId)|$($_.SessionId)|$($_.CommandLine)\" }";
    let mut child = match Command::new("powershell.exe")
        .args(["-NoProfile", "-NonInteractive", "-Command", script])
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .creation_flags(0x0800_0000) // CREATE_NO_WINDOW
        .spawn()
    {
        Ok(child) => child,
        Err(_) => return Err("进程枚举未能启动（powershell 不可用）"),
    };
    let _ = win::assign_to_kill_job(&child);
    let stdout = match child.stdout.take() {
        Some(out) => out,
        None => {
            let _ = child.kill();
            return Err("进程枚举没有输出管道");
        }
    };
    let (sender, receiver) = std::sync::mpsc::channel();
    thread::spawn(move || {
        let mut stdout = stdout;
        let mut bytes = Vec::new();
        if std::io::Read::read_to_end(&mut stdout, &mut bytes).is_ok() {
            let _ = sender.send(bytes);
        }
    });
    match receiver.recv_timeout(ENUMERATION_TIMEOUT) {
        Ok(bytes) => {
            let _ = child.wait();
            Ok(parse_process_rows(&String::from_utf8_lossy(&bytes)))
        }
        Err(_) => {
            let _ = child.kill();
            let _ = child.wait();
            Err("进程枚举超时")
        }
    }
}

/// Parse `pid|createdMs|name|parentId|sessionId|commandLine` rows: only the first
/// five separators are consumed, so a command line containing `|` survives
/// intact; an unparsable timestamp degrades to `None` rather than dropping it.
pub fn parse_process_rows(text: &str) -> Vec<ProcRow> {
    let mut rows = Vec::new();
    for line in text.lines() {
        let mut parts = line.splitn(6, '|');
        let pid = match parts.next().and_then(|v| v.trim().parse::<u32>().ok()) {
            Some(pid) if pid != 0 => pid,
            _ => continue,
        };
        let created_ms = parts.next().and_then(|v| v.trim().parse::<u64>().ok());
        let name = match parts.next() {
            Some(v) => v.trim().to_string(),
            None => continue,
        };
        let parent = parts
            .next()
            .and_then(|v| v.trim().parse::<u32>().ok())
            .unwrap_or(0);
        let session = parts
            .next()
            .and_then(|v| v.trim().parse::<u32>().ok())
            .unwrap_or(0);
        let cmdline = match parts.next() {
            Some(v) => v.trim().to_string(),
            None => continue,
        };
        rows.push(ProcRow {
            pid,
            created_ms,
            name,
            parent,
            session,
            cmdline,
        });
    }
    rows
}

/// Every PID reachable from `roots` along parent→child edges. Protecting our own
/// Host by its own PID is not enough: under a PATH shim the PID we own is
/// `cmd.exe /C dsh web` and the Host is that wrapper's node child, which would
/// otherwise match and be killed as "someone else's Host".
pub fn own_pids(rows: &[ProcRow], roots: &[u32]) -> std::collections::HashSet<u32> {
    let mut owned: std::collections::HashSet<u32> = roots.iter().copied().collect();
    let mut grew = true;
    while grew {
        grew = false;
        for row in rows {
            if owned.contains(&row.parent) && owned.insert(row.pid) {
                grew = true;
            }
        }
    }
    owned
}

/// The account behind each candidate PID. Never touching another user's process
/// is the one rule a takeover must not bend, and `Win32_Process` has no owner
/// column, so this is a second small CIM call over the handful of candidates.
#[cfg(windows)]
fn process_owners(pids: &[u32]) -> std::collections::HashMap<u32, String> {
    let list = pids
        .iter()
        .map(|p| p.to_string())
        .collect::<Vec<_>>()
        .join(",");
    let script = format!(
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8; foreach($i in @({list})){{ $p=Get-CimInstance Win32_Process -Filter \"ProcessId=$i\"; if($p){{ $o=$p | Invoke-CimMethod -MethodName GetOwner; if($o){{ \"$i|$($o.User)\" }} }} }}"
    );
    let mut owners = std::collections::HashMap::new();
    for line in run_helper(&script, HELPER_TIMEOUT).lines() {
        if let Some((pid, user)) = line.split_once('|') {
            if let Ok(pid) = pid.trim().parse::<u32>() {
                owners.insert(pid, user.trim().to_string());
            }
        }
    }
    owners
}

/// When a live PID was created, for the identity check in front of `taskkill`.
#[cfg(windows)]
fn process_created_ms(pid: u32) -> Option<u64> {
    let script = format!(
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $p=Get-CimInstance Win32_Process -Filter \"ProcessId={pid}\"; if($p){{ \"$(([DateTimeOffset]$p.CreationDate).ToUnixTimeMilliseconds())\" }}"
    );
    run_helper(&script, HELPER_TIMEOUT)
        .trim()
        .parse::<u64>()
        .ok()
}

/// A helper call that must not outlive a patience budget of its own.
const HELPER_TIMEOUT: Duration = Duration::from_secs(3);

/// Force a process tree down once the graceful window has expired.
#[cfg(windows)]
fn force_kill_tree(pid: u32) {
    use std::os::windows::process::CommandExt;
    let mut kill = Command::new("taskkill.exe");
    kill.args(["/PID", &pid.to_string(), "/T", "/F"])
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .creation_flags(0x0800_0000);
    let _ = kill.output();
}

/// Last-writer-wins across the state root. One Host per `$DSH_HOME` is what the
/// state actually supports: the task board has a single owner, and a Host holds
/// kernel write leases on every session it has opened — a second Host gets
/// `session/writer-held` on each of them. So before starting its Host the shell
/// retires the other DSH Hosts of this login session: the one recorded in the
/// board lock first (verified against its start time, because PIDs are
/// recycled), then any other process whose command line is a `bin.js web` Host.
/// Each gets Ctrl+C and is forced only after the same grace the shell gives its
/// own child; every action is logged. `DSH_TAURI_TAKEOVER=0` opts out.
#[cfg(windows)]
fn retire_other_hosts(state: &ServerState, own_roots: &dyn Fn() -> Vec<u32>) -> usize {
    if !takeover_enabled() {
        push_log(state, "接管已关闭（DSH_TAURI_TAKEOVER=0），不动其它 host".into());
        return 0;
    }
    let me = std::process::id();
    let rows = match list_session_processes() {
        Ok(rows) => rows,
        Err(why) => {
            // An empty result here would read exactly like "nothing to do".
            push_log(state, format!("接管跳过：{why}；本轮不动任何进程"));
            return 0;
        }
    };
    // Our own tree, re-read rather than cached: the caller may hold the child
    // lock (spawn) or not (a second launch), and the Host may be replaced while
    // this runs.
    let mine = own_pids(&rows, &own_roots());
    // The roots did their one job. Left in place they would grant immunity to a
    // recycled PID — and, through the parent→child walk, to its whole unrelated
    // tree — which turns into "识别到 0 台" for a Host that really is there.
    if let Ok(mut recent) = state.recent_children.lock() {
        recent.clear();
    }
    let mut targets: Vec<(u32, Option<u64>)> = Vec::new();

    let lock_path = board_lock_path();
    let lock_owner = lock_path
        .as_ref()
        .and_then(|path| std::fs::read_to_string(path).ok())
        .and_then(|body| parse_board_lock(&body));
    if lock_owner.is_none() {
        // The lock is the cheap, exact answer to "who holds the board". When it
        // is absent or unreadable, that has to be in the log: otherwise a
        // rename or a different state root turns the whole owner path dead, and
        // the enumeration pass is left guessing from command lines alone.
        match &lock_path {
            Some(path) if path.exists() => push_log(
                state,
                format!("接管：任务板锁存在但读不到持有者：{}", path.display()),
            ),
            Some(path) => push_log(
                state,
                format!("接管：任务板锁不在预期位置：{}（上一台可能没拿到板）", path.display()),
            ),
            None => push_log(state, "接管：无法确定状态根路径，跳过账本持有者检查".into()),
        }
    }
    if let Some(owner) = &lock_owner {
        if owner.pid != me && !mine.contains(&owner.pid) {
            match rows.iter().find(|row| row.pid == owner.pid) {
                // A dead owner needs nothing: the Host steals the stale lock. A
                // live one with no row is a privilege gap — WMI hides the
                // command line of a higher-integrity process, and Ctrl+C and
                // taskkill cannot reach it either. Nothing is killed here:
                // without the command line or the start time there is no way to
                // tell our Host from a PID recycled into something else.
                None => {
                    if matches!(win::probe_alive(owner.pid), win::Alive::Live) {
                        push_log(
                            state,
                            format!(
                                "接管受阻：账本里的 host（PID {}）存活，但本程序读不到它的命令行——它权限更高（例如 dsh web 在管理员终端里启动，而本程序是普通权限）。请用「以管理员身份运行」启动本程序，或让 dsh web 与之同级权限。身份无法确认，不强杀。",
                                owner.pid
                            ),
                        );
                    }
                }
                Some(row) => {
                    let stale = match (owner.started_at_ms, row.created_ms) {
                        (Some(recorded), Some(actual)) => {
                            if recorded.abs_diff(actual) < START_TIME_TOLERANCE_MS {
                                false
                            } else {
                                push_log(
                                    state,
                                    format!(
                                        "接管跳过：账本记录的 PID {} 启动时间不符（记录 {recorded}，实际 {actual}，可能 PID 已被复用）",
                                        owner.pid
                                    ),
                                );
                                true
                            }
                        }
                        _ => {
                            push_log(
                                state,
                                format!("接管跳过：无法核对账本里的 PID {} 启动时间", owner.pid),
                            );
                            true
                        }
                    };
                    if stale {
                        // Leave it to the enumerated pass, which matches on the
                        // command line instead of on a PID we cannot vouch for.
                    } else if !is_dsh_host_row(&row.name, &row.cmdline) {
                        push_log(
                            state,
                            format!(
                                "接管跳过：账本里的 PID {} 不像 DSH host（{}）",
                                owner.pid,
                                host_rejection_reason(&row.name, &row.cmdline)
                            ),
                        );
                    } else {
                        targets.push((owner.pid, row.created_ms));
                    }
                }
            }
        }
    }

    let enumerated = rows.len();
    for row in &rows {
        if row.pid == me
            || mine.contains(&row.pid)
            || targets.iter().any(|(pid, _)| *pid == row.pid)
        {
            continue;
        }
        if is_dsh_host_row(&row.name, &row.cmdline) {
            targets.push((row.pid, row.created_ms));
        }
    }

    push_log(
        state,
        format!("接管：枚举 {enumerated} 个进程，识别到 {} 台其它 host", targets.len()),
    );
    if targets.is_empty() {
        // Almost-matches verbatim: a takeover that finds nothing otherwise looks
        // identical to a machine that never ran a second Host.
        let near: Vec<String> = rows
            .iter()
            .filter(|row| row.cmdline.to_ascii_lowercase().contains("bin.js"))
            .map(|row| {
                let cut: String = row.cmdline.chars().take(80).collect();
                // The login session goes into the line: two Hosts fighting over
                // one state root are often in different ones, and "why was this
                // one not taken over" is the question this answers.
                format!("{} {} (会话 {}): {}", row.pid, row.name, row.session, cut)
            })
            .collect();
        if !near.is_empty() {
            push_log(
                state,
                format!(
                    "接管：命令行含 bin.js 的进程 {} 个，但都不像 host：{}",
                    near.len(),
                    near.join(" ｜ ")
                ),
            );
        }
        return 0;
    }

    // Another user's Host is none of our business, however exact the match —
    // especially once this app is started elevated, when it *could* kill it.
    let ours = std::env::var("USERNAME").unwrap_or_default();
    let pids: Vec<u32> = targets.iter().map(|(pid, _)| *pid).collect();
    let owners = process_owners(&pids);
    targets.retain(|(pid, _)| match owners.get(pid) {
        Some(user) if user.eq_ignore_ascii_case(&ours) => true,
        Some(user) => {
            push_log(state, format!("接管跳过：PID {pid} 属于账户 {user}，不是 {ours}"));
            false
        }
        None => {
            push_log(state, format!("接管跳过：读不到 PID {pid} 的所属账户，不动手"));
            false
        }
    });
    if targets.is_empty() {
        return 0;
    }

    for (pid, _) in &targets {
        if win::send_ctrl_c(*pid) {
            push_log(state, format!("接管 DSH 服务：已向 PID {pid} 的进程组发送 Ctrl+C"));
        } else {
            push_log(
                state,
                format!(
                    "接管 DSH 服务：PID {pid} 收不到定向 Ctrl+C（诊断 {}），只能强制结束，它的清理不会运行",
                    win::ctrl_c_diagnostic()
                ),
            );
        }
    }
    let deadline = Instant::now() + GRACEFUL_EXIT_TIMEOUT;
    let mut retired = 0usize;
    while Instant::now() < deadline && targets.iter().any(|(pid, _)| win::process_alive(*pid)) {
        thread::sleep(Duration::from_millis(100));
    }
    for (pid, created) in &targets {
        if !win::process_alive(*pid) {
            retired += 1;
            continue;
        }
        // A PID that died inside the grace window can already belong to a new,
        // unrelated process; the creation time captured at enumeration is the
        // only thing that says so, so `taskkill /T /F` waits for that check.
        match (*created, process_created_ms(*pid)) {
            (Some(want), Some(actual)) => {
                if want.abs_diff(actual) >= START_TIME_TOLERANCE_MS {
                    push_log(
                        state,
                        format!("接管：PID {pid} 启动时间已变（{want} → {actual}），PID 已被复用，跳过强杀"),
                    );
                    continue;
                }
            }
            _ => {
                push_log(state, format!("接管：无法核对 PID {pid} 的启动时间，跳过强杀"));
                continue;
            }
        }
        force_kill_tree(*pid);
        push_log(
            state,
            format!("接管：PID {pid} 未在 {:?} 内退出，已强制结束", GRACEFUL_EXIT_TIMEOUT),
        );
        retired += 1;
    }
    retired
}

#[cfg(not(windows))]
fn retire_other_hosts(_state: &ServerState, _own_roots: &dyn Fn() -> Vec<u32>) -> usize {
    0
}

/// Set while a retake is running, so a second (or third) launch cannot stack
/// takeovers on top of one another.
static TAKING_OVER: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

/// Retake the state root: retire the other DSH Hosts, and if there were any,
/// bounce our own Host so it re-acquires the task board. A second launch of the
/// app calls this — clicking the icon again means "give this machine DSH", and
/// focusing the window alone would leave an instance whose environment disables
/// takeover sitting there while another Host keeps the board and the session
/// write leases.
///
/// The single-instance callback runs inside a window procedure, so none of this
/// may happen inline: enumeration (up to 5 s), the grace window (3 s) and the
/// stop would freeze the window, the tray and the webview, and the second
/// instance would time out waiting for the handoff. Only a re-entrancy check
/// stays on the caller's thread.
pub fn retake_state_root(app: &AppHandle) {
    if TAKING_OVER.swap(true, std::sync::atomic::Ordering::SeqCst) {
        return;
    }
    let app = app.clone();
    thread::spawn(move || {
        // Cleared on the way out, including a panic, so the flag cannot latch.
        struct Guard;
        impl Drop for Guard {
            fn drop(&mut self) {
                TAKING_OVER.store(false, std::sync::atomic::Ordering::SeqCst);
            }
        }
        let _guard = Guard;
        let state = app.state::<ServerState>();
        // A closure, not a cached PID: the sweep runs for seconds and the
        // supervisor may have replaced the Host in that window — a fresh Host
        // matches `bin.js web` exactly like a foreign one.
        let own_roots = || -> Vec<u32> {
            let mut roots: Vec<u32> = state.recent_children.lock().unwrap().clone();
            if let Some(c) = state.child.lock().unwrap().as_ref() {
                roots.push(c.id());
            }
            roots
        };
        let retired = retire_other_hosts(&state, &own_roots);
        if retired == 0 {
            return;
        }
        push_log(&state, format!("接管：已结束 {retired} 台 host，重启本机服务以重新获取任务板"));
        *state.spawn_reason.lock().unwrap() = "第二次启动（挤兑）".to_string();
        stop_server(&state);
        drop(state);
        let state = app.state::<ServerState>();
        spawn_server(app.clone(), &state);
    });
}

pub fn spawn_server(app: AppHandle, state: &ServerState) {
    // The child lock spans the whole spawn so a racing `stop_server` (quit
    // while the supervisor is restarting) blocks here, then takes and stops
    // the child this call creates rather than missing it.
    let mut child_slot = state.child.lock().unwrap();
    if child_slot.is_some() {
        return; // already running
    }
    if *state.quit_requested.lock().unwrap() {
        return;
    }
    *state.exit_code.lock().unwrap() = None;
    *state.error.lock().unwrap() = None;
    // One critical section for both: bump the generation and clear any stale
    // token URL. Done as two statements, a straggler pump from the previous
    // instance could still pass its generation check in between and publish a
    // dead URL into the new generation — exactly what the counter guards.
    // Lock order everywhere here is generation → url.
    let generation = {
        let mut g = state.generation.lock().unwrap();
        *g += 1;
        *state.url.lock().unwrap() = None;
        *g
    };

    // One board per state root: hand it to this launch by retiring whoever holds
    // it, rather than starting a Host that cannot serve the task board.
    // `child_slot` is held for the whole spawn, so this must read through the
    // guard: re-locking `state.child` here self-deadlocks (std Mutex is not
    // reentrant), which is how 0.1.7 froze with nothing after the splash line.
    push_log(
        state,
        format!(
            "启动阶段：接管其它 DSH host（本次启动原因：{}）",
            state.spawn_reason.lock().unwrap()
        ),
    );
    // The slot is empty here by construction (we hold the lock and checked), but
    // the Host we may have just stopped can still be shutting down, so those PIDs
    // are the roots this pass needs.
    let roots: Vec<u32> = state.recent_children.lock().unwrap().clone();
    retire_other_hosts(state, &|| roots.clone());
    push_log(state, "启动阶段：接管结束，开始选择端口".into());

    let preferred = env_var("DSH_TAURI_PORT")
        .and_then(|p| p.parse::<u16>().ok())
        .unwrap_or(DEFAULT_PORT);
    let port = match pick_port(preferred) {
        Ok(port) => port,
        Err(e) => {
            *state.error.lock().unwrap() = Some(e.clone());
            push_log(state, format!("error: {e}"));
            return;
        }
    };
    *state.port.lock().unwrap() = port;

    let plan = match launch_plan(port) {
        Ok(p) => p,
        Err(e) => {
            *state.error.lock().unwrap() = Some(e.clone());
            push_log(state, format!("error: {e}"));
            return;
        }
    };

    let mut cmd = Command::new(&plan.program);
    cmd.args(&plan.args)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    if let Some(cwd) = &plan.cwd {
        cmd.current_dir(cwd);
    }
    if let Some(home) = home_override() {
        cmd.env("DSH_HOME", home);
    }
    // Every tier of the ladder (node, and especially the PATH fallback's
    // cmd.exe wrapper) is console-subsystem: without this the GUI parent
    // spawning them pops a visible console window for the whole run.
    // Node's own children default to windowsHide, so the descendant tree
    // stays windowless too. CREATE_NEW_PROCESS_GROUP makes the Host its own
    // group leader, so `send_ctrl_c` can signal *only* it: a broadcast (group
    // id 0) also lands on this process once it attaches to the Host's console,
    // which used to kill the shell on a tray restart.
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0200); // CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP
    }

    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            let msg = format!("无法启动 {} {}: {e}", plan.program, plan.args.join(" "));
            *state.error.lock().unwrap() = Some(msg.clone());
            push_log(state, format!("error: {msg}"));
            return;
        }
    };

    let stdout = child.stdout.take();
    let stderr = child.stderr.take();
    // The kill-on-close job makes the Host tree die with this process even if
    // the shell is force-quit or crashes, and covers the PATH tier's case
    // where killing `cmd.exe` alone would leave node behind.
    attach_to_kill_job(&child);
    *child_slot = Some(child);
    *state.spawning.lock().unwrap() = true;

    if *state.restart_requested.lock().unwrap() {
        // A restart landed during the spawn. Tear this child down and let the
        // supervisor respawn, rather than leaving the request unacknowledged.
        *state.restart_requested.lock().unwrap() = false;
        push_log(state, "启动期间收到重启请求：结束刚启动的 host 以重启".into());
        if let Some(mut child) = child_slot.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
        *state.spawning.lock().unwrap() = false;
        return;
    }
    if *state.quit_requested.lock().unwrap() {
        // A quit landed during the spawn: `stop_server` already ran and found
        // nothing, so stop the child created here before releasing the lock.
        if let Some(mut child) = child_slot.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
        *state.spawning.lock().unwrap() = false;
        return;
    }

    push_log(
        state,
        format!(
            "spawned [{}]: {} {} (cwd: {:?})",
            plan.source,
            plan.program,
            plan.args.join(" "),
            plan.cwd
        ),
    );

    // stdout: watch for `dsh web: http://127.0.0.1:PORT/?token=…`.
    if let Some(stdout) = stdout {
        let handle = app.clone();
        thread::spawn(move || {
            let state = handle.state::<ServerState>();
            pump_lines(stdout, state.inner(), true, generation);
        });
    }
    if let Some(stderr) = stderr {
        let handle = app.clone();
        thread::spawn(move || {
            let state = handle.state::<ServerState>();
            pump_lines(stderr, state.inner(), false, generation);
        });
    }

    // Watchdog: notice child exit without blocking the stdout readers.
    let handle = app.clone();
    thread::spawn(move || {
        let state = handle.state::<ServerState>();
        loop {
            thread::sleep(Duration::from_millis(400));
            if *state.generation.lock().unwrap() != generation {
                // A newer Host owns the slot now. Watching it too would log the
                // same exit twice and spend the crash budget twice.
                return;
            }
            let exit = {
                let mut guard = state.child.lock().unwrap();
                match guard.as_mut() {
                    None => return, // stop_server took it: intentional
                    Some(c) => match c.try_wait() {
                        Ok(None) => continue,
                        Ok(Some(status)) => {
                            let code = status.code();
                            *guard = None;
                            code
                        }
                        Err(_) => {
                            *guard = None;
                            None
                        }
                    },
                }
            };
            *state.spawning.lock().unwrap() = false;
            *state.exit_code.lock().unwrap() = exit;
            *state.url.lock().unwrap() = None;
            push_log(state.inner(), format!("server exited: {exit:?}"));
            return;
        }
    });
}

/// Best-effort graceful stop: deliver Ctrl+C to the child's own console, which
/// Node raises as SIGINT and `dsh` answers with its bounded teardown.
#[cfg(windows)]
fn request_graceful_exit(pid: u32) -> bool {
    win::send_ctrl_c(pid)
}

#[cfg(not(windows))]
fn request_graceful_exit(_pid: u32) -> bool {
    false
}

#[cfg(windows)]
fn attach_to_kill_job(child: &std::process::Child) {
    win::assign_to_kill_job(child);
}

#[cfg(not(windows))]
fn attach_to_kill_job(_child: &std::process::Child) {}

#[cfg(windows)]
mod win {
    use std::ffi::c_void;
    use std::os::windows::io::AsRawHandle;
    use std::sync::OnceLock;

    type Handle = *mut c_void;

    /// The job handle is deliberately never closed: closing it would kill the
    /// Host. The OS releases it when this process ends, which is exactly the
    /// no-orphan guarantee the shell needs.
    struct JobHandle(Handle);
    // Safety: written once by `OnceLock`, read-only afterwards, and the OS
    // object it names is safe to pass from any thread.
    unsafe impl Send for JobHandle {}
    unsafe impl Sync for JobHandle {}

    static KILL_JOB: OnceLock<JobHandle> = OnceLock::new();

    /// `JOBOBJECT_BASIC_LIMIT_INFORMATION` (Win32), x64 layout.
    #[repr(C)]
    struct BasicLimitInformation {
        per_process_user_time_limit: i64,
        per_job_user_time_limit: i64,
        limit_flags: u32,
        minimum_working_set_size: usize,
        maximum_working_set_size: usize,
        active_process_limit: usize,
        affinity: usize,
        priority_class: u32,
        scheduling_class: u32,
    }

    const JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: u32 = 0x2000;
    const JOB_OBJECT_BASIC_LIMIT_INFORMATION: i32 = 2;
    const CTRL_C_EVENT: u32 = 0;

    extern "system" {
        fn AttachConsole(dw_process_id: u32) -> i32;
        fn FreeConsole() -> i32;
        fn GetConsoleProcessList(lp_process_list: *mut u32, n_process_list_size: u32) -> u32;
        fn GenerateConsoleCtrlEvent(ctrl_event: u32, dw_process_group_id: u32) -> i32;
        fn SetConsoleCtrlHandler(handler: Option<extern "system" fn(u32) -> i32>, add: i32) -> i32;
        fn CreateJobObjectW(attributes: *const c_void, name: *const u16) -> Handle;
        fn SetInformationJobObject(job: Handle, class: i32, info: *const c_void, size: u32) -> i32;
        fn AssignProcessToJobObject(job: Handle, process: Handle) -> i32;
        fn OpenProcess(desired_access: u32, inherit_handle: i32, process_id: u32) -> Handle;
        fn CloseHandle(handle: Handle) -> i32;
        fn GetExitCodeProcess(process: Handle, exit_code: *mut u32) -> i32;
    }

    const PROCESS_QUERY_LIMITED_INFORMATION: u32 = 0x1000;
    const STILL_ACTIVE: u32 = 259;
    const ATTACH_PARENT_PROCESS: u32 = u32::MAX;

    /// Why the last `send_ctrl_c` failed: 1 = AttachConsole refused, otherwise
    /// the `GetLastError` code from `GenerateConsoleCtrlEvent` (0 = delivered).
    static DIAGNOSTIC: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

    pub fn ctrl_c_diagnostic() -> u32 {
        DIAGNOSTIC.load(std::sync::atomic::Ordering::Relaxed)
    }

    extern "system" {
        fn GetLastError() -> u32;
    }

    /// Live, dead, or unreadable. `OpenProcess` can be refused (a higher
    /// integrity level, an ACL, an anti-virus filter), and reading that as
    /// "dead" silently bypasses every guard that depends on liveness.
    #[derive(Debug, PartialEq)]
    pub enum Alive {
        Live,
        Dead,
        Unknown(u32),
    }

    pub fn probe_alive(pid: u32) -> Alive {
        let handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
        if handle.is_null() {
            return Alive::Unknown(unsafe { GetLastError() });
        }
        let mut code = 0u32;
        let ok = unsafe { GetExitCodeProcess(handle, &mut code) };
        unsafe { CloseHandle(handle) };
        if ok == 0 {
            return Alive::Unknown(unsafe { GetLastError() });
        }
        if code == STILL_ACTIVE {
            Alive::Live
        } else {
            Alive::Dead
        }
    }

    pub fn process_alive(pid: u32) -> bool {
        matches!(probe_alive(pid), Alive::Live)
    }

    /// The console handover is process-global: only one thread at a time may
    /// detach, attach and raise events, or one thread's `FreeConsole` breaks
    /// another's `GenerateConsoleCtrlEvent` and the diagnostic code ends up
    /// describing someone else's attempt.
    static CONSOLE_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

    /// Attach to the child's console and raise Ctrl+C for its process group.
    /// Deliver Ctrl+C to one process **group**, never to the whole console.
    /// `GenerateConsoleCtrlEvent(_, 0)` broadcasts to every process attached to
    /// the console — and attaching to the Host's console puts this GUI process
    /// in that list, which is how a tray restart used to kill the shell itself.
    pub fn send_ctrl_c(pid: u32) -> bool {
        let _console = CONSOLE_LOCK.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        // A dev run already owns a console, which blocks AttachConsole; drop it
        // for the duration and hand it back afterwards.
        let mut probe = [0u32; 1];
        let owns_console = unsafe { GetConsoleProcessList(probe.as_mut_ptr(), 1) } > 0;
        if owns_console {
            unsafe { FreeConsole() };
        }
        if unsafe { AttachConsole(pid) } == 0 {
            if owns_console {
                unsafe { AttachConsole(ATTACH_PARENT_PROCESS) };
            }
            DIAGNOSTIC.store(1_000 + unsafe { GetLastError() }, std::sync::atomic::Ordering::Relaxed);
            return false;
        }
        // Belt and braces: swallow a stray event in this process as well.
        const CTRL_BREAK_EVENT: u32 = 1;
        extern "system" fn ignore_ctrl(event: u32) -> i32 {
            (event == CTRL_C_EVENT || event == CTRL_BREAK_EVENT) as i32
        }
        let guarded = unsafe { SetConsoleCtrlHandler(Some(ignore_ctrl), 1) };
        // Non-zero group id = only the processes of that group, i.e. the Host we
        // spawned with CREATE_NEW_PROCESS_GROUP.
        let raw = unsafe { GenerateConsoleCtrlEvent(CTRL_C_EVENT, pid) };
        if raw == 0 {
            DIAGNOSTIC.store(unsafe { GetLastError() }, std::sync::atomic::Ordering::Relaxed);
        }
        let sent = raw != 0;
        if guarded != 0 {
            unsafe { SetConsoleCtrlHandler(Some(ignore_ctrl), 0) };
        }
        unsafe { FreeConsole() };
        if owns_console {
            unsafe { AttachConsole(ATTACH_PARENT_PROCESS) };
        }
        sent
    }

    pub fn assign_to_kill_job(child: &std::process::Child) {
        let JobHandle(job) = KILL_JOB.get_or_init(|| {
            let handle = unsafe { CreateJobObjectW(std::ptr::null(), std::ptr::null()) };
            if handle.is_null() {
                return JobHandle(handle);
            }
            let info = BasicLimitInformation {
                per_process_user_time_limit: 0,
                per_job_user_time_limit: 0,
                limit_flags: JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                minimum_working_set_size: 0,
                maximum_working_set_size: 0,
                active_process_limit: 0,
                affinity: 0,
                priority_class: 0,
                scheduling_class: 0,
            };
            let ok = unsafe {
                SetInformationJobObject(
                    handle,
                    JOB_OBJECT_BASIC_LIMIT_INFORMATION,
                    &info as *const BasicLimitInformation as *const c_void,
                    std::mem::size_of::<BasicLimitInformation>() as u32,
                )
            };
            JobHandle(if ok == 0 {
                std::ptr::null_mut()
            } else {
                handle
            })
        });
        if job.is_null() {
            return;
        }
        let _ = unsafe { AssignProcessToJobObject(*job, child.as_raw_handle()) };
    }
}

// Byte-level line pump for the child's stdout/stderr. The host (and its
// children, e.g. the Serena MCP banner) can emit non-UTF-8 bytes; a
// BufReader::lines() loop ENDS on the first invalid sequence, dropping the
// pipe's read handle — the host's next write then hits EPIPE and exits
// silently (observed as `server exited: Some(1)` crash loops). This pump
// never rejects bytes: lines are decoded lossily and draining continues.
fn pump_lines<R: std::io::Read>(
    mut pipe: R,
    state: &ServerState,
    mark_ready: bool,
    generation: u64,
) {
    let mut line: Vec<u8> = Vec::with_capacity(8 * 1024);
    let mut chunk = [0u8; 16 * 1024];
    let mut overflow_warned = false;
    loop {
        // A superseded instance's pipe can stay open for minutes (its
        // grandchildren inherit the write handle), and its late output would
        // otherwise keep landing in the ring as if it belonged to the current
        // host. Drop the read handle and stop as soon as we are outdated.
        if *state.generation.lock().unwrap() != generation {
            return;
        }
        match pipe.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => {
                for &b in &chunk[..n] {
                    if b == b'\n' {
                        emit_pumped_line(&mut line, state, mark_ready, generation);
                    } else {
                        line.push(b);
                        // Never let an unterminated stream grow unbounded.
                        if line.len() >= 1 << 20 {
                            line.truncate(1 << 16);
                            line.extend_from_slice(b"... (line truncated)");
                            emit_pumped_line(&mut line, state, mark_ready, generation);
                            if !overflow_warned {
                                overflow_warned = true;
                                push_log(state, "reader: dropped an oversized line".into());
                            }
                        }
                    }
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::Interrupted => continue,
            Err(e) => {
                push_log(state, format!("reader: {e}"));
                break;
            }
        }
    }
    if !line.is_empty() {
        emit_pumped_line(&mut line, state, mark_ready, generation);
    }
}

fn emit_pumped_line(line: &mut Vec<u8>, state: &ServerState, mark_ready: bool, generation: u64) {
    let raw = std::mem::take(line);
    let text = String::from_utf8_lossy(&raw);
    let trimmed = text.trim_end();
    if mark_ready {
        push_log(state, trimmed.to_string());
        if let Some(rest) = trimmed.strip_prefix("dsh web: ") {
            let rest = rest.trim();
            // Check-and-set under one pair of locks: three separately locked
            // reads let two pumps both see an empty slot.
            let mut published = false;
            {
                let g = state.generation.lock().unwrap();
                if *g == generation {
                    let mut slot = state.url.lock().unwrap();
                    if slot.is_none()
                        && (rest.starts_with("http://") || rest.starts_with("https://"))
                    {
                        *slot = Some(rest.to_string());
                        published = true;
                    }
                }
            }
            if published {
                push_log(state, format!("ready: {rest}"));
            }
        }
    } else {
        push_log(state, format!("[stderr] {trimmed}"));
    }
}


#[cfg(test)]
mod tests {
    use super::*;

    fn state() -> ServerState {
        ServerState {
            child: Mutex::new(None),
            url: Mutex::new(None),
            error: Mutex::new(None),
            exit_code: Mutex::new(None),
            spawning: Mutex::new(false),
            port: Mutex::new(0),
            log: Mutex::new(Vec::new()),
            quit_requested: Mutex::new(false),
            generation: Mutex::new(1),
            resume_requested: Mutex::new(false),
            recent_children: Mutex::new(Vec::new()),
            spawn_reason: Mutex::new("冷启动".to_string()),
            restart_requested: Mutex::new(false),
            restarts: Mutex::new(0),
        }
    }

    #[test]
    fn pump_lines_survives_non_utf8_and_still_marks_ready() {
        // cp936 box-drawing bytes (the Serena MCP banner) must not end the
        // pump; the ready URL after them still has to be observed.
        let state = state();
        let mut feed: Vec<u8> = Vec::new();
        feed.extend_from_slice(b"Starting MCP server \xCD\xE5\xBC\x82 line\n");
        feed.extend_from_slice(b"dsh web: http://127.0.0.1:1/?token=x\n");
        pump_lines(feed.as_slice(), &state, true, 1);
        assert_eq!(
            state.url.lock().unwrap().as_deref(),
            Some("http://127.0.0.1:1/?token=x")
        );
    }

    #[test]
    fn the_first_url_published_by_a_pump_keeps_the_slot() {
        // Two pumps for the same generation must not both publish: the check and
        // the set are one critical section now, so the first line wins and the
        // supervisor never sees a port that no Host is listening on.
        let state = state();
        *state.generation.lock().unwrap() = 7;
        emit_pumped_line(
            &mut b"dsh web: http://127.0.0.1:3090/?token=a".to_vec(),
            &state,
            true,
            7,
        );
        emit_pumped_line(
            &mut b"dsh web: http://127.0.0.1:3091/?token=b".to_vec(),
            &state,
            true,
            7,
        );
        assert_eq!(
            state.url.lock().unwrap().as_deref(),
            Some("http://127.0.0.1:3090/?token=a"),
            "the first URL published wins the slot"
        );
    }

    #[test]
        fn pump_lines_ignores_ready_from_a_previous_generation() {
        // A grandchild that inherits the pipe handle can emit after its
        // parent died; that line must not hand a dead URL to the supervisor.
        let state = state();
        *state.generation.lock().unwrap() = 2;
        pump_lines(
            b"dsh web: http://127.0.0.1:1/?token=x\n".as_slice(),
            &state,
            true,
            1,
        );
        assert_eq!(state.url.lock().unwrap().as_deref(), None);
    }

    /// A pipe that keeps producing but whose instance has been superseded —
    /// the shape left behind when a killed host's grandchildren keep the write
    /// handle open. The pump must stop noticing its old instance, not drain
    /// the stream into the ring.
    struct ChattyReader {
        reads: std::sync::Arc<std::sync::atomic::AtomicUsize>,
    }

    impl std::io::Read for ChattyReader {
        fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
            let n = self.reads.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            if n >= 500_000 {
                return Ok(0); // safety valve: never spin forever
            }
            let data: &[u8] = b"output with no terminator, keeps streaming";
            buf[..data.len()].copy_from_slice(data);
            Ok(data.len())
        }
    }

    #[test]
    fn stale_pump_stops_reading_once_superseded() {
        let state = std::sync::Arc::new(state());
        let reads = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let counter = reads.clone();
        let pumping_state = state.clone();
        let pumping = std::thread::spawn(move || {
            pump_lines(
                ChattyReader { reads: counter },
                pumping_state.as_ref(),
                true,
                1,
            )
        });
        std::thread::sleep(Duration::from_millis(30));
        let at_flip = reads.load(std::sync::atomic::Ordering::Relaxed);
        let ring_at_flip = state.log.lock().unwrap().len();
        *state.generation.lock().unwrap() = 99;
        pumping.join().expect("pump thread panicked");
        let after = reads.load(std::sync::atomic::Ordering::Relaxed);
        assert!(
            after - at_flip < 1_000,
            "pump kept draining a superseded pipe: {} more reads after the flip",
            after - at_flip
        );
        assert!(
            state.log.lock().unwrap().len() <= ring_at_flip + 1,
            "superseded pump kept writing into the ring"
        );
        assert_eq!(state.url.lock().unwrap().as_deref(), None);
    }

    #[test]
    fn board_lock_body_yields_the_owner_and_its_start_time() {
        let owner = parse_board_lock(
            r#"{"pid":16936,"token":"7541f116-38f4-42b8-9037-64d427534356","startedAt":1794493085112,"probe":"exact"}"#,
        )
        .expect("the plugin's lock shape must parse");
        assert_eq!(owner.pid, 16936);
        assert_eq!(owner.started_at_ms, Some(1794493085112));
        assert_eq!(parse_board_lock(""), None);
        assert_eq!(parse_board_lock("{\"token\":\"x\"}"), None);
        assert_eq!(parse_board_lock("{\"pid\":0}"), None);
    }

    fn process_rows_survive_pipes_inside_the_command_line() {
        let rows = parse_process_rows(
            "4368|1794536344074|node.exe|15280|1|node cli/lib/bin.js web --no-open --port 3114\n\
             garbage line\n\
             7|not-a-number|node.exe|0|1|node bin.js web | more\n\
             0|1794536344074|node.exe|0|1|node bin.js web\n",
        );
        assert_eq!(rows.len(), 2, "garbage and pid 0 must be dropped");
        assert_eq!(rows[0].pid, 4368);
        assert_eq!(rows[0].created_ms, Some(1794536344074));
        assert_eq!(rows[0].parent, 15280, "the parent chain protects our own tree");
        assert_eq!(rows[0].session, 1);
        assert_eq!(
            rows[1].created_ms,
            None,
            "an unparsable timestamp must not drop the row"
        );
        assert_eq!(rows[1].cmdline, "node bin.js web | more", "pipes stay inside the command line");
    }

    #[test]
    fn own_tree_covers_children_not_just_the_direct_child() {
        // A PATH shim means the PID we own is cmd.exe and the Host is its child.
        let rows = parse_process_rows(
            "10|1|cmd.exe|4|1|cmd /C dsh web\n\
             11|1|node.exe|10|1|node cli/lib/bin.js web\n\
             12|1|python.exe|11|1|python server.py\n\
             99|1|node.exe|7|1|node other/bin.js web\n",
        );
        let mine = own_pids(&rows, &[4]);
        assert!(mine.contains(&10) && mine.contains(&11) && mine.contains(&12));
        assert!(!mine.contains(&99), "an unrelated subtree stays fair game");
    }

    #[test]
    fn takeover_targets_only_real_dsh_web_hosts() {
        // the two real shapes of a Host command line
        assert!(dsh_host_script(r#""C:\Program Files\nodejs\node.exe"  apps/cli/lib/bin.js web"#).is_some());
        assert!(
            dsh_host_script(r#""\\?\C:\Users\x\AppData\Local\DSH\node\node.exe" \\?\C:\Users\x\.dsh\cli\lib\bin.js web --no-open --port 3090"#).is_some()
        );
        // `web` has to be a whole argument: those are other programs
        assert!(dsh_host_script("node cli/lib/bin.js webhook-server").is_none());
        assert!(dsh_host_script("node cli/lib/bin.js web-server").is_none());
        assert!(dsh_host_script("node cli/lib/bin.js web2").is_none());
        // the tail of another name is not a script path
        assert!(dsh_host_script("node cli/lib/ubin.js web").is_none());
    }

    #[test]
    fn drive_relative_and_device_path_shapes_are_classified_consistently() {
        // `d:bin.js` is drive-relative: no absolute stat, so the other gates
        // decide and a real Host is not hidden by our cwd.
        assert!(is_dsh_host_row("node.exe", "node d:bin.js web"));
        // The forward-slash device prefix has to normalise the same way, so an
        // absolute path that does not exist is still rejected.
        assert!(!is_dsh_host_row("node.exe", "node //?/C:/definitely/not/here/bin.js web"));
        assert!(!is_dsh_host_row("node.exe", r#"node \?\C:\definitely\not\here\bin.js web"#));
    }

    #[test]
    fn a_relative_host_command_line_is_still_a_host() {
        // The shape a developer gets from a checkout: `node apps/cli/lib/bin.js
        // web`. The path is relative to *that* process's cwd, which the shell
        // cannot read; resolving it against our own cwd hid a live Host and the
        // takeover never ran. Pinned because that regression cost an afternoon.
        assert!(is_dsh_host_row(
            "node.exe",
            r#""C:\Program Files\nodejs\node.exe"  apps/cli/lib/bin.js web"#
        ));
        // A PATH dump is not a command position: separators no single path has.
        assert!(!is_dsh_host_row(
            "node.exe",
            r#""C:\Program Files\nodejs\node.exe" /c set "PATH=C:\Program Files\nodejs;C:\x\cli\lib\bin.js web""#
        ));
        assert!(!host_rejection_reason("cmd.exe", "node serve.js").is_empty());
    }

    #[test]
    fn the_script_gate_and_the_image_gate_both_have_to_pass() {
        // The file has to be named `bin.js` itself: a *suffix* (`fake-bin.js`) is
        // not a script boundary, which is exactly what the gate is for.
        let dir = std::env::temp_dir().join("dsh-tauri-fake-cli");
        std::fs::create_dir_all(&dir).expect("temp dir must be creatable");
        let script = dir.join("bin.js");
        std::fs::write(&script, "// stands in for the real CLI").expect("temp file must be writable");
        assert!(
            dsh_host_script(&format!("node {}/fake-bin.js web", dir.display())).is_none(),
            "a name that merely ends in bin.js is not the CLI"
        );
        let host = format!("\"C:\\Program Files\\nodejs\\node.exe\" \"{}\" web", script.display());
        assert!(
            is_dsh_host_row("NODE.EXE", &host),
            "a real bin.js, run by node.exe, with `web` is a Host"
        );
        assert!(
            !is_dsh_host_row("mynode.exe", &host),
            "the image name must match exactly, not end with it"
        );
        assert!(
            !is_dsh_host_row("node.exe", &format!("node {} webhook-server", script.display())),
            "`webhook-server` is a different program"
        );
        assert!(
            !is_dsh_host_row(
                "cmd.exe",
                r#""C:\Windows\System32\cmd.exe" /c set "PATH=C:\Program Files\nodejs;C:\x\cli\lib\bin.js web""#
            ),
            "a wrapper that merely mentions the text is not running it"
        );
        assert!(
            !is_dsh_host_row("node.exe", "node c:/definitely/not/here/bin.js web"),
            "an absolute script that does not exist is not a Host"
        );
        assert!(
            is_dsh_host_row("node.exe", "node definitely/not/here/bin.js web"),
            "a relative script cannot be verified (it is relative to the other process's cwd), so the other gates decide"
        );
        let _ = std::fs::remove_file(&script);
    }

    #[cfg(windows)]
    #[test]
    fn the_process_sweep_lists_this_process_with_its_start_time() {
        // The takeover is only as good as this CIM route: if it comes back empty
        // or without creation times, takeover silently does nothing.
        let me = std::process::id();
        let rows = list_session_processes().expect("the enumeration must work from a test");
        let row = rows
            .iter()
            .find(|row| row.pid == me)
            .expect("this test process must be listed");
        let created = row.created_ms.expect("creation time must come back");
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis() as u64;
        assert!(
            created <= now && now - created < 600_000,
            "implausible start {created} vs now {now}"
        );
        assert!(!row.name.is_empty(), "the image name must come back");
        assert!(!row.cmdline.is_empty(), "the command line must come back");
        assert!(matches!(win::probe_alive(me), win::Alive::Live));
        assert!(
            !matches!(win::probe_alive(u32::MAX), win::Alive::Live),
            "a PID that cannot be opened must never read as live"
        );
        // the owner check has to resolve our own process to our own account
        let owners = process_owners(&[me]);
        let ours = std::env::var("USERNAME").unwrap_or_default();
        assert_eq!(
            owners.get(&me).map(|user| user.to_ascii_lowercase()),
            Some(ours.to_ascii_lowercase()),
            "our own process must resolve to our own account"
        );
    }

    #[cfg(windows)]
    #[test]
    fn ctrl_c_is_delivered_to_the_targets_group_only() {
        // A broadcast (group id 0) used to strike the shell itself; this test
        // dies with the harness if that regression comes back.
        use std::os::windows::process::CommandExt;
        // Opt-in: this test detaches the harness console for a moment, which is
        // disruptive inside an interactive shell. Run it with DSH_TEST_CTRL_C=1.
        if std::env::var("DSH_TEST_CTRL_C").is_err() {
            eprintln!("skipped: set DSH_TEST_CTRL_C=1 to exercise the Ctrl+C console dance");
            return;
        }
        // A stand-in Host: its own console (so AttachConsole can reach it, the
        // way the real child's is reachable from a GUI parent) and its own
        // process group (the way `spawn_server` starts it).
        let mut child = Command::new("node")
            .args(["-e", "setInterval(() => {}, 1000)"])
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .creation_flags(0x0000_0010 | 0x0000_0200) // NEW_CONSOLE | NEW_PROCESS_GROUP
            .spawn()
            .expect("the stand-in Host must start");
        let sent = win::send_ctrl_c(child.id());
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            if let Some(status) = child.try_wait().expect("try_wait must work") {
                assert!(sent, "the group-targeted signal must be accepted");
                assert!(!status.success(), "the child should have been interrupted: {status}");
                return; // still here: this process survived the signal
            }
            assert!(
                Instant::now() < deadline,
                "the child ignored Ctrl+C (send={sent}, diagnostic={})",
                win::ctrl_c_diagnostic()
            );
            thread::sleep(Duration::from_millis(100));
        }
    }

    #[test]
    fn push_log_masks_tokens_and_timestamps() {
        let state = state();
        push_log(
            &state,
            "dsh web: http://127.0.0.1:3080/?token=secret-value".to_string(),
        );
        let ring = state.log.lock().unwrap();
        let line = ring.last().unwrap();
        assert!(!line.contains("secret-value"), "token leaked: {line}");
        assert!(line.contains("token=***"), "unexpected mask: {line}");
        let stamp = &line[..20];
        assert!(stamp.contains('T') && stamp.ends_with('Z'), "bad stamp: {stamp}");
    }

    #[test]
    fn ring_lines_are_capped_and_char_safe() {
        let wide = "é".repeat(RING_LINE_MAX);
        let cut = shorten(&wide);
        assert!(cut.len() <= RING_LINE_MAX + " … (truncated)".len());
        assert!(cut.ends_with(" … (truncated)"));
        assert!(shorten("short").eq("short"));
    }

    #[test]
    fn civil_from_days_matches_known_dates() {
        assert_eq!(civil_from_days(0), (1970, 1, 1));
        assert_eq!(civil_from_days(10_957), (2000, 1, 1));
        assert_eq!(civil_from_days(-1), (1969, 12, 31));
    }

    #[test]
    fn pick_port_uses_the_preferred_port_when_free() {
        // A port the OS just handed out is free; asking for it must not drift.
        let free = TcpListener::bind(("127.0.0.1", 0))
            .unwrap()
            .local_addr()
            .unwrap()
            .port();
        let picked = pick_port(free).expect("a free preferred port must be accepted");
        assert_eq!(picked, free);
    }

    #[test]
    fn pick_port_refuses_to_drift_when_preferred_is_busy() {
        let held = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let busy = held.local_addr().unwrap().port();
        let message = pick_port(busy).expect_err("a busy port must be reported, not dodged");
        assert!(message.contains(&busy.to_string()), "message lacks the port: {message}");
        assert!(message.contains("DSH_TAURI_PORT"), "message lacks the remedy: {message}");
    }

    #[test]
    fn launch_plan_ladder_order() {
        // NOTE: env vars are process-global; keep all ladder assertions in
        // one test to avoid racing with other tests.
        std::env::set_var("DSH_TAURI_CMD", "my-node");
        std::env::set_var("DSH_TAURI_ARGS", "[\"web\",\"--port\",\"9\"]");
        std::env::set_var("DSH_TAURI_CWD", "C:\\tmp");
        let plan = launch_plan(9).unwrap();
        assert_eq!(plan.program, "my-node");
        assert_eq!(plan.args, vec!["web", "--port", "9"]);
        assert_eq!(plan.source, "DSH_TAURI_CMD");

        std::env::set_var("DSH_TAURI_ARGS", "not-json");
        assert!(launch_plan(9).is_err());

        std::env::remove_var("DSH_TAURI_CMD");
        std::env::remove_var("DSH_TAURI_ARGS");
        std::env::remove_var("DSH_TAURI_CWD");

        std::env::set_var("DSH_CHECKOUT", "C:\\definitely-not-a-dsh-checkout-xyz");
        assert!(launch_plan(9).is_err());
        std::env::remove_var("DSH_CHECKOUT");

        // The remaining tiers are ambient: which one answers depends on whether
        // this folder sits inside a checkout and whether `dsh` is on PATH. Pin
        // the checkout tier with a synthetic one instead, so the assertion holds
        // wherever the project lives — it used to pass only by accident of being
        // inside the monorepo, and the move broke it.
        let fake = std::env::temp_dir().join("dsh-tauri-fake-checkout");
        let bin = fake.join("apps").join("cli").join("lib").join("bin.js");
        std::fs::create_dir_all(bin.parent().unwrap()).expect("fake checkout must be creatable");
        std::fs::write(&bin, "// stands in for the real CLI").expect("fake CLI must be writable");
        std::env::set_var("DSH_CHECKOUT", &fake);
        let plan = launch_plan(9).expect("the checkout tier must resolve");
        assert_eq!(plan.source, "checkout apps/cli");
        std::env::remove_var("DSH_CHECKOUT");
        std::env::set_var("DSH_REPO_ROOT", &fake);
        let plan = launch_plan(9).expect("DSH_REPO_ROOT must be honoured");
        assert_eq!(plan.source, "checkout apps/cli");
        std::env::remove_var("DSH_REPO_ROOT");

        // A configured profile replaces the booted profile name and leaves the
        // app arguments intact; DSH_TAURI_HOME is handed to the child as
        // DSH_HOME (which is what actually isolates the task-board ledger).
        std::env::set_var("DSH_CHECKOUT", &fake);
        std::env::set_var("DSH_TAURI_PROFILE", "tauri");
        let plan = launch_plan(3090).expect("profile override must still resolve a plan");
        let at = plan.args.iter().position(|a| a == "tauri").expect("profile token in args");
        assert_eq!(&plan.args[at + 1..], ["--no-open", "--port", "3090"]);
        std::env::remove_var("DSH_TAURI_PROFILE");
        assert_eq!(web_profile(), "web");
        std::env::set_var("DSH_TAURI_HOME", "D:\\dsh-desktop-home");
        assert_eq!(home_override().as_deref(), Some("D:\\dsh-desktop-home"));
        std::env::remove_var("DSH_TAURI_HOME");
        assert_eq!(home_override(), None);

        // A configured workspace wins over whatever the tier would default to:
        // sessions are bucketed per workspace, so this is what lets the
        // installed app continue the same list the web UI shows.
        std::env::set_var("DSH_TAURI_CWD", "D:\\work\\site");
        let plan = launch_plan(3092).expect("workspace override must still resolve a plan");
        assert_eq!(plan.cwd, Some(PathBuf::from("D:\\work\\site")));
        std::env::remove_var("DSH_TAURI_CWD");
        let plan = launch_plan(3092).expect("the default workspace must resolve too");
        assert_ne!(plan.cwd, Some(PathBuf::from("D:\\work\\site")));

        // With no env overrides the plan must still resolve in this repo's
        // dev layout (tests run from src-tauri, checkout walk reaches it).
        let plan = launch_plan(4321);
        assert!(plan.is_ok(), "launch_plan: {:?}", plan.err());
    }
}








