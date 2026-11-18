mod harness_update;
mod server;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;
use std::time::{Duration, Instant};

use tauri::menu::{Menu, MenuItem};
use tauri::tray::{TrayIcon, TrayIconBuilder};
use tauri::{AppHandle, Manager, RunEvent, Url, WebviewUrl, WebviewWindowBuilder, WindowEvent};
use tauri_plugin_autostart::MacosLauncher;
use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};
use tauri_plugin_updater::UpdaterExt;

pub use server::ServerState;

pub const MAIN_LABEL: &str = "main";
const SPLASH_LABEL: &str = "splash";

const MAX_AUTO_RESTARTS: u32 = 3;
const BACKOFF_SECS: [u64; 3] = [2, 5, 10];
const STARTUP_TIMEOUT_SECS: u64 = 120;
/// A host run at least this long counts as healthy and returns the crash
/// budget to zero; see `spent_after_run`.
const STABLE_SECS: u64 = 60;

/// Launched via the autostart entry (`--autostart`) or `--hidden`: start in
/// the tray, no window until the user asks for one.
static HIDDEN_START: AtomicBool = AtomicBool::new(false);
/// Only the first main window honours HIDDEN_START.
static FIRST_MAIN: AtomicBool = AtomicBool::new(true);

/// DSH web auth cookies are `SameSite=Strict`. A top-level navigation from a
/// different document origin (the app's own `tauri.localhost`) is therefore
/// rejected, and the WebView lands on the 401 page — exactly what Strict
/// means. So the main window's *document origin* must be the host itself:
/// create it with `WebviewUrl::External(token_url)` once the startup line is
/// captured, the same thing a browser does when you open the printed URL.
///
/// The splash window is only the fallback: shown while the host is starting,
/// after a crash, or when startup failed (it runs on the app origin and has
/// no auth constraint).
#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct ServerStatus {
    state: &'static str,
    url: Option<String>,
    error: Option<String>,
    exit_code: Option<i32>,
    port: Option<u16>,
    restarts: u32,
    max_restarts: u32,
    log: Vec<String>,
}

fn snapshot(state: &ServerState) -> ServerStatus {
    let url = state.url.lock().unwrap().clone();
    let error = state.error.lock().unwrap().clone();
    let exit_code = *state.exit_code.lock().unwrap();
    let spawning = *state.spawning.lock().unwrap();
    let port = *state.port.lock().unwrap();
    let restarts = *state.restarts.lock().unwrap();
    let log = state.log.lock().unwrap().clone();
    let state = if url.is_some() {
        "ready"
    } else if error.is_some() {
        "error"
    } else if exit_code.is_some() {
        "exited"
    } else if spawning {
        "spawning"
    } else {
        "idle"
    };
    ServerStatus {
        state,
        url,
        error,
        exit_code,
        port: if port != 0 { Some(port) } else { None },
        restarts,
        max_restarts: MAX_AUTO_RESTARTS,
        log,
    }
}

fn status_of(app: &AppHandle) -> ServerStatus {
    let state = app.state::<ServerState>();
    snapshot(&state)
}

fn log_app(app: &AppHandle, line: String) {
    let state = app.state::<ServerState>();
    server::push_log(state.inner(), line);
}

/// Poll until the host publishes its token URL. Ok(url) on success;
/// Err when the child failed or exited before publishing it.
fn wait_for_url(app: &AppHandle, timeout: Duration) -> Result<String, ServerStatus> {
    let start = Instant::now();
    loop {
        {
            let state = app.state::<ServerState>();
            if let Some(url) = state.url.lock().unwrap().as_ref() {
                return Ok(url.clone());
            }
            let st = snapshot(&state);
            if st.state == "error" || st.state == "exited" {
                return Err(st);
            }
        }
        if start.elapsed() > timeout {
            return Err(status_of(app));
        }
        std::thread::sleep(Duration::from_millis(300));
    }
}

/// Restarts already spent by the current crash streak (0 while healthy). A run
/// that stayed up `STABLE_SECS` returns the budget to zero; a run that died
/// earlier spends one more slot. Counting a run that died *after* publishing
/// its URL is what makes the cap reachable at all: the common crash loop is
/// "ready, then dead", and resetting on every ready made the limit
/// unreachable (observed in the field as an endless "第 1/3 次").
fn spent_after_run(spent: u32, stable: bool) -> u32 {
    if stable {
        0
    } else {
        spent + 1
    }
}

/// Backoff before the restart that follows `spent` budget, `None` once the
/// budget is gone.
fn backoff_for(spent: u32) -> Option<u64> {
    BACKOFF_SECS.get(spent.saturating_sub(1) as usize).copied()
}

fn window_size(app: &AppHandle) -> (f64, f64) {
    app
        .get_webview_window(MAIN_LABEL)
        .or_else(|| app.get_webview_window(SPLASH_LABEL))
        .and_then(|w| w.inner_size().ok().map(|s| (s.width as f64, s.height as f64)))
        .unwrap_or((1280.0, 860.0))
}

/// Windows the user is currently looking at. A crash restart must not pop one
/// open on an autostart-hidden or tray-resident session.
fn user_visible_windows(app: &AppHandle) -> bool {
    [MAIN_LABEL, SPLASH_LABEL].iter().any(|label| {
        app.get_webview_window(label)
            .map(|w| w.is_visible().unwrap_or(false))
            .unwrap_or(false)
    })
}

fn same_origin(a: &Url, b: &Url) -> bool {
    a.scheme() == b.scheme()
        && a.host_str() == b.host_str()
        && a.port_or_known_default() == b.port_or_known_default()
}

/// Opens an http(s) URL in the system default browser. The parse is the
/// scheme check: anything that is not an absolute http(s) URL — file paths,
/// UNC shares, protocol handlers, strings with embedded NULs — is rejected.
fn open_http(app: &AppHandle, url: &str) -> Result<(), String> {
    let parsed: Url = url
        .parse()
        .map_err(|_| format!("不是合法的 URL: {url}"))?;
    if parsed.scheme() != "http" && parsed.scheme() != "https" {
        return Err("只允许打开 http(s) 链接".to_string());
    }
    use tauri_plugin_opener::OpenerExt;
    app.opener().open_url(parsed.as_str(), None::<&str>).map_err(|e| e.to_string())
}

fn create_main_window(app: &AppHandle, url: &str, size: (f64, f64)) -> tauri::Result<()> {
    let parsed: Url = url
        .parse()
        .map_err(|_| std::io::Error::other(format!("invalid host URL: {url}")))?;
    let (width, height) = size;
    // The Host page is a remote IPC origin, so its every command is granted
    // narrowly in `capabilities/host-page.json`. Keep the navigation guard
    // symmetric with that: anything leaving the Host origin goes to the
    // system browser instead of loading in this window.
    let guard_origin = parsed.clone();
    let guard_app = app.clone();
    let window = WebviewWindowBuilder::new(app, MAIN_LABEL, WebviewUrl::External(parsed))
        .title("DSH")
        .inner_size(width, height)
        .min_inner_size(720.0, 480.0)
        .visible(false)
        // The window-state plugin restores the saved geometry after `build`,
        // so `center` is only the first-run fallback — do not reposition here.
        .center()
        // The Web GUI attaches files through DOM drop events; Tauri's own
        // drop handler intercepts OS file drops and swallows them.
        .disable_drag_drop_handler()
        .on_navigation(move |url| {
            if url.scheme() == "about" || url.scheme() == "blob" || same_origin(url, &guard_origin) {
                return true;
            }
            if let Err(e) = open_http(&guard_app, url.as_str()) {
                log_app(&guard_app, format!("导航拦截失败: {e}"));
            }
            false
        })
        // Login and every other in-page `window.open` (account authorization
        // popups, help links) route to the system browser: the web UI preopens
        // an `about:blank` popup and navigates it later, which a native
        // webview cannot do — without this bridge the sign-in click silently
        // dies. Mirrors the Electron shell's openExternal handling.
        .initialization_script(EXTERNAL_LINKS_JS)
        .build()?;
    if let Some(splash) = app.get_webview_window(SPLASH_LABEL) {
        let _ = splash.destroy();
    }
    let hidden_first = HIDDEN_START.load(Ordering::Relaxed) && FIRST_MAIN.swap(false, Ordering::Relaxed);
    log_app(app, format!("main window created (hidden_first={hidden_first})"));
    if !hidden_first {
        let _ = window.show();
        let _ = window.set_focus();
        if cfg!(debug_assertions) {
            window.open_devtools();
        }
    }
    Ok(())
}

/// `show` is false for tray-resident sessions: the state is still reported
/// through the log and the tray tooltip, without interrupting the user.
fn create_splash_window(app: &AppHandle, size: (f64, f64), show: bool) -> tauri::Result<()> {
    let (width, height) = size;
    let window = WebviewWindowBuilder::new(app, SPLASH_LABEL, WebviewUrl::App("index.html".into()))
        .title("DSH")
        .inner_size(width, height)
        .min_inner_size(720.0, 480.0)
        .visible(false)
        .center()
        .build()?;
    if let Some(main) = app.get_webview_window(MAIN_LABEL) {
        let _ = main.destroy();
    }
    log_app(app, format!("splash created (show={show})"));
    if show {
        let _ = window.show();
        let _ = window.set_focus();
    }
    Ok(())
}

/// Runs before any page script in the main window. Overrides `window.open`:
/// direct calls open the system browser; the preopen pattern (UI opens
/// `about:blank` on click, then sets `location` once the Host returns the
/// authorization URL) gets a stub whose navigation also opens the system
/// browser. Mirrors the Electron shell's `setWindowOpenHandler` +
/// `shell.openExternal` flow. `open_external` enforces http(s) server-side.
const EXTERNAL_LINKS_JS: &str = r#"
(() => {
  if (window.__dshExternalLinks) return;
  window.__dshExternalLinks = true;
  const openExternal = (raw) => {
    try {
      const u = new URL(String(raw), location.href);
      if (u.protocol !== 'http:' && u.protocol !== 'https:') return;
      const invoke = window.__TAURI__ && window.__TAURI__.core && window.__TAURI__.core.invoke;
      if (!invoke) { console.warn('dsh: Tauri IPC unavailable, link dropped', u.href); return; }
      invoke('open_external', { url: u.href }).catch((e) => console.warn('dsh: open_external rejected', e));
    } catch (e) { console.warn('dsh: link dropped', e) }
  };
  window.open = function (url) {
    if (url !== undefined && url !== '' && url !== 'about:blank') {
      openExternal(url);
      return null;
    }
    const stub = {
      closed: false,
      opener: null,
      focus() {},
      blur() {},
      close() { stub.closed = true; },
      document: { write() {}, writeln() {}, open() {}, close() {} },
      location: {
        href: 'about:blank',
        // The account UI assigns `location.href` too, so the property is an
        // accessor: an assignment navigates externally, not silently.
        set href(next) { if (!stub.closed) openExternal(next); },
        get href() { return stub.location.href; },
        replace(next) { if (!stub.closed) openExternal(next); return next; },
        assign(next) { if (!stub.closed) openExternal(next); },
      },
    };
    return stub;
  };
})();
"#;

/// Opens an http(s) URL in the system default browser. Remote (Host-page)
/// origins reach it only through `capabilities/host-page.json`.
#[tauri::command]
fn open_external(app: AppHandle, url: String) -> Result<(), String> {
    open_http(&app, &url)
}

/// Tray icon kept for the shell's own notices: a hidden session has no window
/// to report a failure in, and the tooltip is the only surface it sees.
static TRAY: OnceLock<TrayIcon> = OnceLock::new();

fn tray_notice(text: &str) {
    if let Some(tray) = TRAY.get() {
        let _ = tray.set_tooltip(Some(text));
    }
}

fn build_tray(app: &mut tauri::App) -> tauri::Result<()> {
    let show = MenuItem::with_id(app.handle(), "show", "显示 DSH", true, None::<&str>)?;
    let autostart = MenuItem::with_id(app.handle(), "autostart", "开机自动启动（切换）", true, None::<&str>)?;
    let restart = MenuItem::with_id(app.handle(), "restart", "重启 DSH 服务", true, None::<&str>)?;
    let hupdate = MenuItem::with_id(app.handle(), "hupdate", "检查 harness 更新…", true, None::<&str>)?;
    let update = MenuItem::with_id(app.handle(), "update", "检查安装包更新…", true, None::<&str>)?;
    let quit = MenuItem::with_id(app.handle(), "quit", "退出", true, None::<&str>)?;
    let menu = Menu::with_items(app.handle(), &[&show, &autostart, &restart, &hupdate, &update, &quit])?;

    let icon = app
        .default_window_icon()
        .ok_or_else(|| std::io::Error::other("missing window icon"))?
        .clone();

    let tray = TrayIconBuilder::with_id("dsh-tray")
        .icon(icon)
        .menu(&menu)
        // Left click shows the window; the menu stays on right click.
        .show_menu_on_left_click(false)
        .tooltip("DSH Desktop")
        .on_menu_event(|app, event| match event.id().as_ref() {
            "show" => show_app(app),
            "autostart" => toggle_autostart(app),
            "restart" => manual_restart(app),
            "hupdate" => check_harness_update(app, true),
            "update" => check_update(app, true),
            "quit" => request_quit(app),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let tauri::tray::TrayIconEvent::Click {
                button: tauri::tray::MouseButton::Left,
                button_state: tauri::tray::MouseButtonState::Up,
                ..
            } = event
            {
                show_app(tray.app_handle());
            }
        })
        .build(app)?;
    let _ = TRAY.set(tray);
    Ok(())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // Diagnostics without side effects: say which launch tier would be used and
    // with what arguments, then exit. This is how a support report distinguishes
    // "the harness mount did not happen" from "the harness CLI is broken" without
    // starting a Host, and it is what the installer test asserts on.
    if std::env::args().any(|a| a == "--print-launch-plan") {
        match server::launch_plan(3090) {
            Ok(plan) => {
                println!("source:  {}", plan.source);
                println!("program: {}", plan.program);
                println!("args:    {:?}", plan.args);
                println!("cwd:     {:?}", plan.cwd);
                std::process::exit(0);
            }
            Err(message) => {
                eprintln!("launch plan unavailable: {message}");
                std::process::exit(1);
            }
        }
    }

    let hidden = std::env::args()
        .any(|a| a == "--autostart" || a == "--hidden");
    HIDDEN_START.store(hidden, Ordering::Relaxed);

    let app = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, argv, _cwd| {
            // A second launch means "give this machine DSH now": focus the
            // window and retake the state root. Focusing alone would leave an
            // already-running instance (possibly one started with
            // DSH_TAURI_TAKEOVER=0) sitting there while another Host keeps the
            // task board and the session write leases.
            show_app(app);
            // Update checks from a second launch route to the running shell,
            // so both entries can be exercised (and supported) without
            // clicking the notification area.
            if argv.iter().any(|a| a == "--check-update") {
                check_update(app, true);
                return;
            }
            if argv.iter().any(|a| a == "--check-harness-update") {
                check_harness_update(app, true);
                return;
            }
            server::retake_state_root(app);
        }))
        .plugin(tauri_plugin_autostart::init(
            MacosLauncher::LaunchAgent,
            Some(vec!["--autostart"]),
        ))
        .plugin(
            // Deliberately exclude StateFlags::VISIBLE/FULLSCREEN: restoring a
            // visible window would defeat `--autostart` hidden launches.
            tauri_plugin_window_state::Builder::default()
                .with_state_flags(
                    tauri_plugin_window_state::StateFlags::POSITION
                        | tauri_plugin_window_state::StateFlags::SIZE
                        | tauri_plugin_window_state::StateFlags::MAXIMIZED,
                )
                .build(),
        )
        .plugin(tauri_plugin_updater::Builder::new().build())
        // Update results are reported in a native dialog: the status log only
        // reaches the splash window, so a tray-only report looked like a dead
        // menu entry whenever the main window was open.
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .manage(ServerState::default())
        .setup(|app| {
            build_tray(app)?;

            // Bundled runtime (installed app): resources/cli/lib/bin.js and
            // an optional portable resources/node/node.exe.
            if let Ok(cli) = app.path().resolve("cli/lib/bin.js", tauri::path::BaseDirectory::Resource) {
                if cli.is_file() {
                    server::set_bundled_cli(cli);
                }
            }
            #[cfg(windows)]
            if let Ok(node) = app.path().resolve("node/node.exe", tauri::path::BaseDirectory::Resource) {
                if node.is_file() {
                    server::set_bundled_node(node);
                }
            }

            // Start on the splash so the user always sees real startup
            // progress; hidden (autostart) launches stay in the tray.
            let hidden_now = HIDDEN_START.load(Ordering::Relaxed);
            log_app(app.handle(), format!("startup hidden_start={hidden_now}"));
            if !hidden_now {
                create_splash_window(app.handle(), (1280.0, 860.0), true)?;
            }

            let handle = app.handle().clone();
            std::thread::spawn(move || supervise(&handle));

            // Support/E2E hook: `DSH_TAURI_UPDATE_CHECK=1` runs the same
            // update flow as the tray entry a few seconds after startup,
            // headless: it logs instead of opening dialogs and installs an
            // update as soon as it finds one.
            if std::env::var_os("DSH_TAURI_UPDATE_CHECK").is_some() {
                let handle = app.handle().clone();
                std::thread::spawn(move || {
                    std::thread::sleep(Duration::from_secs(3));
                    log_app(&handle, "DSH_TAURI_UPDATE_CHECK：自动检查更新".to_string());
                    check_update(&handle, false);
                });
            } else if std::env::args().any(|a| a == "--check-update") {
                // First launch carried the flag: run the installer-feed flow.
                let handle = app.handle().clone();
                std::thread::spawn(move || {
                    std::thread::sleep(Duration::from_secs(2));
                    check_update(&handle, true);
                });
            } else if std::env::args().any(|a| a == "--check-harness-update") {
                // First launch carried the flag: run the harness git flow.
                let handle = app.handle().clone();
                std::thread::spawn(move || {
                    std::thread::sleep(Duration::from_secs(2));
                    check_harness_update(&handle, true);
                });
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            server_status,
            restart_server_cmd,
            check_update_cmd,
            quit_app,
            open_external
        ])
        .build(tauri::generate_context!())
        .expect("error while building dsh-desktop-tauri");

    app.run(|app_handle, event| match event {
        RunEvent::WindowEvent {
            label,
            event: WindowEvent::CloseRequested { api, .. },
            ..
        } => {
            if label == MAIN_LABEL || label == SPLASH_LABEL {
                // Hide to tray instead of quitting, matching the Electron shell.
                api.prevent_close();
                if let Some(w) = app_handle.get_webview_window(&label) {
                    let _ = w.hide();
                }
            }
        }
        RunEvent::Exit => {
            let state = app_handle.state::<ServerState>();
            *state.quit_requested.lock().unwrap() = true;
            server::stop_server(&state);
        }
        _ => {}
    });
}

/// Single lifecycle owner for the host process (mirrors the Electron shell's
/// backend-controller supervision):
/// - spawn → wait for the token URL → (re)create the main window
/// - when the host stops: distinguish intentional stop (manual restart,
///   quit) from a crash; crashes restart with backoff up to `MAX_AUTO_RESTARTS`
/// - once the budget is gone the supervisor waits, visible through the splash
///   and the tray tooltip, for a manual restart: it never stops watching,
///   because the tray's restart entry has no other supervisor to wake.
fn supervise(app: &AppHandle) {
    let mut main_existed = true;
    // Recorded before each launch so a takeover line can say what asked for it:
    // "冷启动", "主动重启（托盘或二次启动）", "崩溃后自动重启". Without this, a
    // crash restart that retires a Host the user started minutes later reads as
    // an unprovoked kill.
    let mut reason = "冷启动";
    loop {
        {
            let state = app.state::<ServerState>();
            if *state.quit_requested.lock().unwrap() {
                return;
            }
            *state.spawn_reason.lock().unwrap() = reason.to_string();
            server::spawn_server(app.clone(), state.inner());
        }
        let ready_at = match wait_for_url(app, Duration::from_secs(STARTUP_TIMEOUT_SECS)) {
            Ok(url) => {
                if let Err(e) = create_main_window(app, &url, window_size(app)) {
                    log_app(app, format!("main window recreate failed: {e}"));
                }
                main_existed = true;
                Some(Instant::now())
            }
            // Startup failed; the splash renders the failure state.
            Err(_) => None,
        };

        // Wait until the host is no longer running.
        loop {
            std::thread::sleep(Duration::from_millis(300));
            let state = app.state::<ServerState>();
            if *state.quit_requested.lock().unwrap() {
                return;
            }
            let child_gone = state.child.lock().unwrap().is_none();
            let spawning = *state.spawning.lock().unwrap();
            if child_gone && !spawning {
                break;
            }
        }

        let (natural, errored) = {
            let state = app.state::<ServerState>();
            let natural = state.exit_code.lock().unwrap().is_some();
            let errored = state.error.lock().unwrap().is_some();
            (natural, errored)
        };
        if !natural && !errored {
            // Intentional stop (manual restart): supervisor respawns now.
            reason = "主动重启（托盘或二次启动）";
            continue;
        }

        let stable = ready_at.map_or(false, |t| t.elapsed() >= Duration::from_secs(STABLE_SECS));
        reason = "崩溃后自动重启";
        let spent = {
            let state = app.state::<ServerState>();
            let mut spent = state.restarts.lock().unwrap();
            *spent = spent_after_run(*spent, stable);
            *spent
        };
        let Some(backoff) = backoff_for(spent) else {
            log_app(
                app,
                format!("自动重启额度已用尽（{MAX_AUTO_RESTARTS} 次），已停止自动重启：托盘「重启 DSH 服务」可再试"),
            );
            tray_notice("DSH 服务反复崩溃：托盘「重启 DSH 服务」恢复");
            if main_existed {
                let show = user_visible_windows(app);
                if let Err(e) = create_splash_window(app, window_size(app), show) {
                    log_app(app, format!("splash recreate failed: {e}"));
                }
            }
            if !wait_for_resume_or_quit(app) {
                return;
            }
            log_app(app, "手动恢复监督：重置重启额度".to_string());
            reason = "托盘手动恢复";
            *app.state::<ServerState>().restarts.lock().unwrap() = 0;
            continue;
        };
        log_app(
            app,
            format!("服务意外停止，{backoff}s 后自动重启（第 {spent}/{MAX_AUTO_RESTARTS} 次）"),
        );
        if main_existed {
            let show = user_visible_windows(app);
            if let Err(e) = create_splash_window(app, window_size(app), show) {
                log_app(app, format!("splash recreate failed: {e}"));
            }
        }
        std::thread::sleep(Duration::from_secs(backoff));
    }
}

/// Paused supervision waits here; only quit or a manual restart ends the wait.
/// The resume flag is cleared on entry so a restart requested *before* the
/// pause does not silently rearm the exhausted budget.
fn wait_for_resume_or_quit(app: &AppHandle) -> bool {
    let state = app.state::<ServerState>();
    *state.resume_requested.lock().unwrap() = false;
    loop {
        std::thread::sleep(Duration::from_millis(300));
        if *state.quit_requested.lock().unwrap() {
            return false;
        }
        if *state.resume_requested.lock().unwrap() {
            *state.resume_requested.lock().unwrap() = false;
            return true;
        }
    }
}

/// Manual restart (tray/menu/splash): stop the host and show the splash;
/// the supervisor notices the intentional stop and spawns a fresh host, or
/// wakes up if it had paused on an exhausted crash budget.
fn manual_restart(app: &AppHandle) {
    {
        let state = app.state::<ServerState>();
        if *state.quit_requested.lock().unwrap() {
            return;
        }
        server::stop_server(&state);
        *state.error.lock().unwrap() = None;
        *state.exit_code.lock().unwrap() = None;
        *state.resume_requested.lock().unwrap() = true;
    }
    let size = window_size(app);
    if app.get_webview_window(SPLASH_LABEL).is_none() {
        if let Err(e) = create_splash_window(app, size, true) {
            log_app(app, format!("splash recreate failed: {e}"));
        }
    } else {
        reload_splash(app);
    }
}

fn request_quit(app: &AppHandle) {
    {
        let state = app.state::<ServerState>();
        *state.quit_requested.lock().unwrap() = true;
        server::stop_server(&state);
    }
    app.exit(0);
}

const UPDATE_TITLE: &str = "DSH 更新";
/// How long an update check may take before it is reported as a timeout.
const UPDATE_TIMEOUT_SECS: u64 = 30;

/// One update flow at a time, across both entries (installer feed and
/// harness git): the harness flow mutates a git worktree and runs pnpm, and
/// two interleaved runs would race reset/clean/build.
static UPDATE_IN_FLIGHT: AtomicBool = AtomicBool::new(false);

/// Held for the whole check + apply; releases on drop, so every early return
/// and panic path unwinds the flag.
struct UpdateSlot;

impl UpdateSlot {
    fn acquire() -> Option<Self> {
        UPDATE_IN_FLIGHT
            .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
            .ok()
            .map(|_| Self)
    }
}

impl Drop for UpdateSlot {
    fn drop(&mut self) {
        UPDATE_IN_FLIGHT.store(false, Ordering::Relaxed);
    }
}

/// Harness git update, shallow by design: the local repo keeps no history,
/// only the latest official state. Fetch --depth 1 → compare SHA → ask →
/// stop host → sync (stash tracked changes, reset --hard) → rebuild → restart.
///
/// The whole flow runs on a plain thread: the fetch may block for minutes and
/// the native dialogs block until answered — neither may park a tokio worker.
fn check_harness_update(app: &AppHandle, interactive: bool) {
    let Some(_slot) = UpdateSlot::acquire() else {
        log_app(app, "已有更新流程在进行，忽略本次触发".to_string());
        return;
    };
    let handle = app.clone();
    let started = Instant::now();
    log_app(&handle, "检查 harness 更新（git 浅同步官方最新发布 tag）".to_string());
    tray_notice("正在检查 harness 更新…");
    std::thread::spawn(move || {
        let _slot = _slot;
        let root = match harness_update::harness_root() {
            Some(root) => root,
            None => {
                let text = "未找到 harness checkout：外壳必须挂在 harness 内（或设置 DSH_REPO_ROOT）才能按 git 方式更新。".to_string();
                if interactive {
                    update_notice(&handle, &text);
                } else {
                    log_app(&handle, text);
                }
                return;
            }
        };
        let info = match harness_update::check(&root) {
            Ok(info) => info,
            Err(e) => {
                let text = format!(
                    "检查 harness 更新失败（{} 秒后放弃）：\n{e}",
                    started.elapsed().as_secs()
                );
                if interactive {
                    update_notice(&handle, &text);
                } else {
                    log_app(&handle, text);
                }
                return;
            }
        };
        if info.needs_init {
            // Deployed file copy, not a repo: offer to build the local git
            // store in place, history-free, aligned to the official latest.
            let text = format!(
                "harness 不是 git 仓库（{}）。\n\n现在初始化本地 git 并同步官方最新版本吗？\n  - 无历史：只保留官方最新状态（fetch --depth 1）\n  - 已跟踪文件的本地改动会先存入 stash（同步失败会自动放回）\n  - 上个版本残留的未跟踪文件将被清理（已安装的外壳、LOCAL-CHANGES*、scripts/verify-local-changes.mjs、.agents、.claude、*.log 保留）\n  - 构建产物、node_modules 不受影响\n  - 随后自动重放本地内核补丁 → 重建 → 校验，任一阶段失败即判定更新失败（不会以缺防线的版本启动），最后重启外壳",
                root.display()
            );
            if interactive && !update_confirm(&handle, text) {
                log_app(&handle, "暂缓 harness 初始化".to_string());
                return;
            }
            apply_harness_update(&handle, root, true, _slot, false);
            return;
        }
        let via_note = if info.via == "origin" || info.via.is_empty() {
            String::new()
        } else {
            format!("（经镜像 {}）", info.via)
        };
        if !info.update_available() {
            let mut text = "harness 已与官方最新版本一致。".to_string();
            // Nothing to gate, but a patch that is already gone still has to be
            // said out loud: it errors nowhere else (2026-11-16/17 accident).
            if !harness_update::skip_local_patches() && harness_update::has_patch_verifier(&root) {
                let sink = {
                    let handle = handle.clone();
                    move |line: String| log_app(&handle, format!("harness-update: {line}"))
                };
                if harness_update::verify_local_changes(&root, "source", sink).is_err() {
                    text = "harness 已与官方最新版本一致，但本地内核补丁校验未通过。

运行中的实例可能已静默失去 text-tool-call 兜底 / tool-fs-search 路径预检。修复：在 checkout 内跑 node scripts/verify-local-changes.mjs 确认缺哪条，再用 restore-local-patches.mjs（harness-patches，或 DSH_LOCAL_PATCHES_DIR 所指）重放后重建。".to_string();
                }
            }
            if interactive {
                update_notice(&handle, &text);
            } else {
                log_app(&handle, text);
            }
            return;
        }
        log_app(
            &handle,
            format!(
                "harness 有更新：本地 {} → 官方 {} {}",
                harness_update::short(&info.local),
                harness_update::short(&info.remote),
                via_note
            ),
        );
        let mut notes = String::new();
        if info.dirty {
            notes.push_str("\n注意：检测到本地未提交改动，会在同步前存入 stash（同步失败自动放回；成功则保留在 stash 里供手动恢复）。");
        }
        notes.push_str("\n注意：上个版本残留的未跟踪文件会被清理（已安装的外壳、LOCAL-CHANGES*、scripts/verify-local-changes.mjs、.agents、.claude、*.log 保留）。");
        notes.push_str("\n注意：本地内核补丁（text-tool-call 兜底、tool-fs-search 路径预检）会被 reset/clean 抹掉，同步后自动从 checkout 外的规范副本重放；阶段0/2/4 任一步非零退出即判定更新失败。");
        let text = format!(
            "harness 有新版本：{local} → {remote}。{notes}\n\n现在同步吗？将执行：\n  1. 停止 DSH 服务\n  2. 同步官方最新（fetch --depth 1 + reset，无历史）\n  3. 重放本地内核补丁（阶段2；锚点失配即中止）\n  4. 重建（pnpm install + 官方 artifacts 管线，需几分钟）\n  5. 校验产物已带补丁（阶段4），通过才重启外壳",
            local = harness_update::short(&info.local),
            remote = harness_update::short(&info.remote),
        );
        if interactive && !update_confirm(&handle, text) {
            log_app(&handle, "暂缓 harness 更新".to_string());
            tray_notice("harness 更新待确认：官方有新版本");
            return;
        }
        // The check just fetched; reuse its FETCH_HEAD. If upstream moved in
        // the seconds the dialog was open, the next check catches it.
        apply_harness_update(&handle, root, false, _slot, true);
    });
}

/// The mutating half, on its own thread so dialogs and the UI stay responsive.
/// Progress streams into the status log and the tray tooltip. `_slot` keeps
/// the in-flight guard held for the whole run; failures on the sync side
/// restart the host on the old artifacts, rebuild failures leave the choice
/// to the user (a half-built tree must not auto-spawn).
///
/// Around the sync sit the local-patch stages (see harness_update): 0 records
/// the pre-overwrite baseline, 2 replays the patches the reset/clean just wiped,
/// 4 asserts the rebuilt products still carry them. Any non-zero exit there
/// fails the update — 0 only logs, 2 rolls back to the old artifacts, 4 leaves
/// the service down rather than boot a build whose defences are silently gone.
fn apply_harness_update(
    app: &AppHandle,
    root: std::path::PathBuf,
    bootstrap: bool,
    _slot: UpdateSlot,
    reuse_fetch: bool,
) {
    let handle = app.clone();
    std::thread::spawn(move || {
        let _slot = _slot;
        {
            let state = handle.state::<ServerState>();
            server::stop_server(state.inner());
            server::push_log(state.inner(), "harness 更新：已停止 DSH 服务".into());
        }
        tray_notice("harness 更新中：同步官方最新…");
        let log = {
            let handle = handle.clone();
            move |line: String| {
                let state = handle.state::<ServerState>();
                server::push_log(state.inner(), format!("harness-update: {line}"));
            }
        };
        log(format!("checkout: {}", root.display()));
        // 补丁门禁：阶段 0 记录基线，阶段 2 重放，阶段 4 校验产物。
        // replay = 补丁有规范副本可重放；asserted = checkout 带断言可跑门禁。
        // 两者分开：校验脚本自己丢了，也不能顺手把重放关掉——那正是补丁丢失后
        // 最可能出现的状态（重放才是恢复手段）。
        let asserted = !harness_update::skip_local_patches() && harness_update::has_patch_verifier(&root);
        let replay = !harness_update::skip_local_patches()
            && (asserted || harness_update::local_patches_dir(&root).is_some());
        if harness_update::skip_local_patches() {
            log(format!(
                "已设置 {}：本次更新跳过补丁阶段 0/2/4",
                harness_update::SKIP_PATCHES_ENV
            ));
        } else if !replay {
            log(format!(
                "既没有 scripts/verify-local-changes.mjs 也没有补丁规范副本（{} 未设置）：本次更新无补丁可重放",
                harness_update::PATCHES_DIR_ENV
            ));
        } else if !asserted {
            log("该 checkout 没有 scripts/verify-local-changes.mjs：仍能重放补丁，但产物无法校验（阶段 0/4 跳过）".into());
        }
        if asserted {
            // 阶段 0（覆盖前）只记录不阻断："更新前就已缺失"正是阶段 2 要修的
            // 情形，拦住它等于让人无法自愈。
            tray_notice("harness 更新中：校验补丁基线（阶段 0）…");
            match harness_update::verify_local_changes(&root, "source", log.clone()) {
                Ok(()) => log("阶段 0 通过：本地补丁基线完整".into()),
                Err(e) => log(format!("警告（阶段 0）：更新前补丁基线未通过，阶段 2 将重放补丁：\n{e}")),
            }
        }
        tray_notice("harness 更新中：同步官方最新…");
        let synced = if bootstrap {
            harness_update::bootstrap(&root, &log)
        } else {
            harness_update::sync(&root, &log, reuse_fetch)
        };
        match synced {
            Ok(head) => log(format!("当前版本 {}", harness_update::short(&head))),
            Err(e) => {
                log(format!("同步失败：{e}"));
                // Sources may be half-moved but the built artifacts are still
                // the consistent pre-sync set: bring the service back.
                manual_restart(&handle);
                update_notice(&handle, format!("harness 更新失败（同步）：\n{e}\n\n已用原产物恢复 DSH 服务。"));
                return;
            }
        }
        // 阶段 2（重放）：补丁源文件 + agent.ts 四处接线，规范副本在 checkout 外。
        if replay {
            tray_notice("harness 更新中：重放本地内核补丁（阶段 2）…");
            // 重放脚本只看文件在不在，"文件在但内容被清空"也报 OK；所以立刻用
            // 源码断言复核一遍——缺防线要在几分钟重建之前失败，并报在阶段 2。
            let replayed = harness_update::restore_local_patches(&root, log.clone())
                .and_then(|()| {
                    if asserted {
                        harness_update::verify_local_changes(&root, "source", log.clone())
                    } else {
                        Ok(())
                    }
                });
            if let Err(e) = replayed {
                log(format!("阶段 2 失败：本地补丁未能重放：\n{e}"));
                // 源码已在新版本，产物仍是更新前那一套完整的：用它把服务拉回来。
                // 绝不带着缺防线的源码去构建，更不以新版本启动。
                manual_restart(&handle);
                update_notice(&handle, format!("harness 更新失败（阶段 2 重放本地补丁）：\n{e}\n\n已按失败语义中止：未重建、未以新版本启动，DSH 服务已用更新前的产物恢复。锚点失配说明官方代码结构变了，需要人工适配补丁（对照 LOCAL-CHANGES.txt 与 harness-patches），修好后再跑一次更新。"));
                return;
            }
            log("阶段 2 通过：本地补丁已重放，源码断言复核通过".into());
        }
        tray_notice("harness 更新中：重建…");
        if let Err(e) = harness_update::rebuild(&root, log.clone()) {
            log(format!("重建失败：{e}"));
            update_notice(&handle, format!("harness 更新失败（重建）：\n{e}\n\nDSH 服务保持停止：产物可能不完整，自动重启有崩溃风险。修复后重试更新，或在托盘「重启 DSH 服务」手动拉起。"));
            return;
        }
        // 阶段 4（校验）：产物里必须真的带着那两处防线，否则不启动新版本。
        if asserted {
            tray_notice("harness 更新中：校验构建产物（阶段 4）…");
            if let Err(e) = harness_update::verify_local_changes(&root, "build", log.clone()) {
                log(format!("阶段 4 失败：构建产物未通过补丁校验：\n{e}"));
                update_notice(&handle, format!("harness 更新失败（阶段 4 产物校验）：\n{e}\n\n已重建但产物缺少本地防线，按失败语义不启动新版本：DSH 服务保持停止——缺防线的构建会静默退化，比停机更难查。修复后重试更新；确需先跑起来再用托盘「重启 DSH 服务」。"));
                return;
            }
            log("阶段 4 通过：产物已带本地补丁".into());
        }
        log("重建与补丁校验完成，重启外壳以加载新产物".into());
        update_notice(&handle, "harness 已同步官方最新，补丁重放与产物校验均通过，即将重启外壳。");
        handle.restart();
    });
}


/// Report an update outcome where the user is looking. `log_app` only feeds
/// the splash window, so a tray click that merely logged looked like a dead
/// menu entry once the main window was up.
fn update_notice(app: &AppHandle, text: impl std::fmt::Display) {
    let text = text.to_string();
    log_app(app, format!("更新: {}", text.replace('\n', " ")));
    app.dialog()
        .message(text)
        .title(UPDATE_TITLE)
        .kind(MessageDialogKind::Info)
        .blocking_show();
}

/// Ask before an install: it stops the DSH service and restarts the shell.
fn update_confirm(app: &AppHandle, text: impl Into<String>) -> bool {
    app.dialog()
        .message(text.into())
        .title(UPDATE_TITLE)
        .kind(MessageDialogKind::Info)
        .buttons(MessageDialogButtons::OkCancelCustom(
            "立即安装".into(),
            "稍后".into(),
        ))
        .blocking_show()
}

/// Turn an updater error into something a user can act on. The configured
/// endpoint is still a placeholder, and "error decoding response body" alone
/// reads like a bug in the shell rather than a feed that does not exist yet.
fn explain_update_error(e: &tauri_plugin_updater::Error) -> String {
    let raw = e.to_string();
    let lower = raw.to_lowercase();
    if lower.contains("404") {
        format!("更新源不存在（HTTP 404）：这个安装包还没有对应的发布清单。\n\n原始错误：{raw}")
    } else if lower.contains("timed out") || lower.contains("timeout") {
        format!("连接更新源超时（{UPDATE_TIMEOUT_SECS} 秒）。\n\n原始错误：{raw}")
    } else if lower.contains("decode") || lower.contains("json") || lower.contains("expected") {
        format!(
            "更新源返回的不是更新清单（多为 404 页面或被代理拦截）。\n\n原始错误：{raw}"
        )
    } else {
        raw
    }
}

/// Updater with a bounded wait and an endpoint override, so a test machine can
/// point at its own feed without rebuilding:
/// `DSH_TAURI_UPDATE_ENDPOINT=https://host/feeds/win-x64/latest.json`
/// (comma separated for several).
fn updater_from(app: &AppHandle) -> Result<tauri_plugin_updater::Updater, String> {
    let mut builder = app
        .updater_builder()
        .timeout(Duration::from_secs(UPDATE_TIMEOUT_SECS));
    let endpoints: Vec<Url> = std::env::var("DSH_TAURI_UPDATE_ENDPOINT")
        .unwrap_or_default()
        .split(',')
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .filter_map(|s| Url::parse(s).ok())
        .collect();
    if !endpoints.is_empty() {
        builder = builder.endpoints(endpoints).map_err(|e| e.to_string())?;
    }
    builder.build().map_err(|e| e.to_string())
}

/// Tray and splash "检查更新…": one click checks and, when a release is found,
/// asks before downloading and installing.
///
/// `interactive` is false for the `DSH_TAURI_UPDATE_CHECK` support hook, which
/// runs headless: it logs and installs without a dialog nobody is there to
/// answer.
fn check_update(app: &AppHandle, interactive: bool) {
    let Some(_slot) = UpdateSlot::acquire() else {
        log_app(app, "已有更新流程在进行，忽略本次触发".to_string());
        return;
    };
    let handle = app.clone();
    log_app(
        &handle,
        format!("检查安装包更新（interactive={interactive}）"),
    );
    tauri::async_runtime::spawn(async move {
        let _slot = _slot;
        let updater = match updater_from(&handle) {
            Ok(updater) => updater,
            Err(e) => {
                let text = format!("无法检查更新：{e}");
                if interactive {
                    update_notice(&handle, &text);
                } else {
                    log_app(&handle, text);
                }
                return;
            }
        };
        let current = handle.package_info().version.to_string();
        match updater.check().await {
            Ok(Some(update)) => {
                let version = update.version.clone();
                log_app(&handle, format!("发现更新 {version}（当前 {current}）"));
                if interactive {
                    let text = format!(
                        "发现新版本 {version}（当前 {current}）。\n\n现在下载并安装吗？安装前会先停止 DSH 服务，装好后自动重启。"
                    );
                    if !update_confirm(&handle, text) {
                        log_app(&handle, format!("暂缓安装 {version}"));
                        tray_notice(&format!("DSH 更新 {version} 可用"));
                        return;
                    }
                }
                install_update(&handle, update, interactive).await;
            }
            Ok(None) => {
                let text = format!("当前已是最新版本 {current}。");
                if interactive {
                    update_notice(&handle, text);
                } else {
                    log_app(&handle, text);
                }
            }
            Err(e) => {
                let text = format!("检查更新失败：{}", explain_update_error(&e));
                if interactive {
                    update_notice(&handle, &text);
                } else {
                    log_app(&handle, text);
                }
            }
        }
    });
}

async fn install_update(
    app: &AppHandle,
    update: tauri_plugin_updater::Update,
    interactive: bool,
) {
    let version = update.version.clone();
    log_app(app, format!("确认安装更新 {version}：先停止 DSH 服务"));
    {
        let state = app.state::<ServerState>();
        server::stop_server(state.inner());
    }
    tray_notice(&format!("DSH 正在安装更新 {version}…"));
    match update.download_and_install(|_chunk, _total| {}, || {}).await {
        Ok(_) => {
            log_app(app, "更新已安装，正在重启".to_string());
            app.restart();
        }
        Err(e) => {
            let text = format!("更新安装失败：{}", explain_update_error(&e));
            if interactive {
                update_notice(app, &text);
            } else {
                log_app(app, text);
            }
        }
    }
}

fn toggle_autostart(app: &AppHandle) {
    use tauri_plugin_autostart::ManagerExt;
    let al = app.autolaunch();
    match al.is_enabled() {
        Ok(true) => match al.disable() {
            Ok(()) => log_app(app, "已关闭开机自动启动".to_string()),
            Err(e) => log_app(app, format!("关闭开机自动启动失败: {e}")),
        },
        Ok(false) => match al.enable() {
            Ok(()) => log_app(app, "已开启开机自动启动（随登录会话以托盘方式启动）".to_string()),
            Err(e) => log_app(app, format!("开启开机自动启动失败: {e}")),
        },
        Err(e) => log_app(app, format!("读取自启动状态失败: {e}")),
    }
}

fn show_app(app: &AppHandle) {
    for label in [MAIN_LABEL, SPLASH_LABEL] {
        if let Some(w) = app.get_webview_window(label) {
            let _ = w.unminimize();
            let _ = w.show();
            let _ = w.set_focus();
            return;
        }
    }
    // Nothing to show: a second launch, or a tray click on a failed hidden
    // start, must still surface the state instead of doing nothing.
    if let Err(e) = create_splash_window(app, (1280.0, 860.0), true) {
        log_app(app, format!("splash recreate failed: {e}"));
    }
}

fn reload_splash(app: &AppHandle) {
    if let Some(w) = app.get_webview_window(SPLASH_LABEL) {
        let _ = w.eval("location.reload()");
    }
}

#[tauri::command]
fn server_status(state: tauri::State<'_, ServerState>) -> ServerStatus {
    snapshot(&state)
}

#[tauri::command]
fn restart_server_cmd(app: AppHandle) {
    manual_restart(&app);
}

#[tauri::command]
fn check_update_cmd(app: AppHandle) {
    check_update(&app, true);
}

#[tauri::command]
fn quit_app(app: AppHandle) {
    request_quit(&app);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crash_budget_is_spent_by_short_runs_and_repaid_by_a_stable_one() {
        // A crash loop that reaches "ready" before dying must still climb to
        // the cap: ready itself no longer repays the budget.
        let mut spent = 0;
        let mut restarts = 0;
        while let Some(backoff) = backoff_for(spent) {
            spent = spent_after_run(spent, false);
            restarts += 1;
            assert!(backoff > 0);
            if restarts > 16 {
                panic!("budget never ran out");
            }
        }
        assert_eq!(spent, MAX_AUTO_RESTARTS + 1);
        assert_eq!(backoff_for(MAX_AUTO_RESTARTS), Some(10));
        assert_eq!(backoff_for(MAX_AUTO_RESTARTS + 1), None);

        // One run that stayed up returns the whole budget.
        assert_eq!(spent_after_run(MAX_AUTO_RESTARTS, true), 0);
        assert_eq!(backoff_for(spent_after_run(MAX_AUTO_RESTARTS, true)), Some(2));
    }

    #[test]
    fn navigation_guard_accepts_only_the_host_origin_and_inline_schemes() {
        let host: Url = "http://127.0.0.1:3080/?token=x".parse().unwrap();
        assert!(same_origin(&"http://127.0.0.1:3080/session".parse().unwrap(), &host));
        assert!(!same_origin(&"http://127.0.0.1:3081/".parse().unwrap(), &host));
        assert!(!same_origin(&"https://127.0.0.1:3080/".parse().unwrap(), &host));
        assert!(!same_origin(&"http://example.com/".parse().unwrap(), &host));
    }
}
