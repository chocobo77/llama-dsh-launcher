mod server;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use tauri::menu::{Menu, MenuItem};
use tauri::tray::{TrayIcon, TrayIconBuilder};
use tauri::{AppHandle, Manager, RunEvent, Url, WebviewUrl, WebviewWindowBuilder, WindowEvent};
use tauri_plugin_autostart::MacosLauncher;
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
    let update = MenuItem::with_id(app.handle(), "update", "检查更新…", true, None::<&str>)?;
    let quit = MenuItem::with_id(app.handle(), "quit", "退出", true, None::<&str>)?;
    let menu = Menu::with_items(app.handle(), &[&show, &autostart, &restart, &update, &quit])?;

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
            "update" => check_update(app),
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
        .plugin(tauri_plugin_single_instance::init(|app, _argv, _cwd| {
            // A second launch means "give this machine DSH now": focus the
            // window and retake the state root. Focusing alone would leave an
            // already-running instance (possibly one started with
            // DSH_TAURI_TAKEOVER=0) sitting there while another Host keeps the
            // task board and the session write leases.
            show_app(app);
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
            // then confirms it on the hook's behalf.
            if std::env::var_os("DSH_TAURI_UPDATE_CHECK").is_some() {
                let handle = app.handle().clone();
                std::thread::spawn(move || {
                    std::thread::sleep(Duration::from_secs(3));
                    log_app(&handle, "DSH_TAURI_UPDATE_CHECK：自动检查更新".to_string());
                    check_update(&handle);
                    let deadline = Instant::now() + Duration::from_secs(120);
                    while pending_update().lock().unwrap().is_none() && Instant::now() < deadline {
                        std::thread::sleep(Duration::from_millis(500));
                    }
                    if pending_update().lock().unwrap().is_some() {
                        log_app(&handle, "DSH_TAURI_UPDATE_CHECK：自动确认安装".to_string());
                        install_update(&handle);
                    }
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

/// Update the pending-update version recorded by a `check_update` that found
/// one; the tray entry installs on the click that follows.
fn pending_update() -> &'static Mutex<Option<String>> {
    static PENDING: OnceLock<Mutex<Option<String>>> = OnceLock::new();
    PENDING.get_or_init(|| Mutex::new(None))
}

/// Tray "检查更新…": the first click reports the update, the click after it
/// installs. Installing restarts the shell, so it is never done on the user's
/// behalf from one menu entry.
fn check_update(app: &AppHandle) {
    if pending_update().lock().unwrap().is_some() {
        install_update(app);
        return;
    }
    let handle = app.clone();
    tauri::async_runtime::spawn(async move {
        match handle.updater() {
            Ok(updater) => match updater.check().await {
                Ok(Some(update)) => {
                    let version = update.version.clone();
                    *pending_update().lock().unwrap() = Some(version.clone());
                    log_app(
                        &handle,
                        format!(
                            "发现更新 {version}：再次点击「检查更新…」下载安装并重启（会先停止 DSH 服务）"
                        ),
                    );
                    tray_notice(&format!("DSH 更新 {version} 待确认：再点「检查更新」安装"));
                }
                Ok(None) => log_app(&handle, "当前已是最新版本（或未配置更新端点）".to_string()),
                Err(e) => log_app(&handle, format!("检查更新失败: {e}")),
            },
            Err(e) => log_app(&handle, format!("更新器不可用: {e}")),
        }
    });
}

fn install_update(app: &AppHandle) {
    let version = match pending_update().lock().unwrap().as_ref() {
        Some(version) => version.clone(),
        None => {
            log_app(app, "没有待安装的更新：先「检查更新…」".to_string());
            return;
        }
    };
    let handle = app.clone();
    tauri::async_runtime::spawn(async move {
        log_app(&handle, format!("确认安装更新 {version}：先停止 DSH 服务"));
        {
            let state = handle.state::<ServerState>();
            server::stop_server(state.inner());
        }
        match handle.updater() {
            Ok(updater) => match updater.check().await {
                Ok(Some(update)) => {
                    log_app(&handle, format!("下载并安装 {version}…"));
                    match update.download_and_install(|_chunk, _total| {}, || {}).await {
                        Ok(_) => {
                            *pending_update().lock().unwrap() = None;
                            log_app(&handle, "更新已安装，正在重启".to_string());
                            handle.restart();
                        }
                        Err(e) => log_app(&handle, format!("更新安装失败: {e}")),
                    }
                }
                Ok(None) => {
                    *pending_update().lock().unwrap() = None;
                    log_app(&handle, "更新已不在最新清单中".to_string());
                }
                Err(e) => log_app(&handle, format!("更新检查失败: {e}")),
            },
            Err(e) => log_app(&handle, format!("更新器不可用: {e}")),
        }
    });
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
    check_update(&app);
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
