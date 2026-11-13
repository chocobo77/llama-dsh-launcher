# DSH Desktop (Tauri shell)

English | [中文](README.zh.md)

A Tauri counterpart to `apps/desktop` (the Electron shell): a Rust + WebView2
shell that hosts the DSH Web GUI and supervises the `dsh` service process.

## What it does

1. **Locates the dsh runtime** (first hit wins):
   1. `DSH_TAURI_CMD` (+ optional `DSH_TAURI_ARGS` JSON array, `DSH_TAURI_CWD`) — fully custom;
   2. the bundle's `resources/cli/lib/bin.js` (see *Bundling a runtime*), optionally with a bundled `resources/node/node.exe`;
   3. `DSH_CHECKOUT` — a checkout containing `apps/cli/lib/bin.js`;
   4. dev default — the monorepo checkout inferred from the executable location;
   5. `dsh` on PATH (`cmd.exe /C dsh web …`; force-killing the shell kills the
      cmd wrapper only — prefer 1–4 for packaged use).
2. Binds its own fixed port (`3090` by default, `DSH_TAURI_PORT` to change) and
   runs `web --no-open --port <port>`. A busy port is reported on the splash and
   is never dodged by drifting to a random port: the port is part of the main
   window's origin, and cookies on `127.0.0.1` ignore ports — every new port
   adds another session cookie to the same jar, sent to every DSH port, until
   the request headers pass Node's 16 KB cap and the whole UI answers `431`.
3. Watches child stdout for the authenticated startup line
   `dsh web: http://127.0.0.1:<port>/?token=…` (the web GUI 401s without the token).
4. Shows a splash window first; once the token URL is captured it destroys the
   splash and recreates the main window with
   `WebviewUrl::External(token URL)`. This is required: DSH session cookies are
   `SameSite=Strict`, so navigating from the app's own `tauri.localhost` origin
   lands on the 401 page — the main window's document origin must be the host
   itself, exactly like a browser opening the printed URL.
5. **Supervisor thread** (mirrors the Electron shell's backend-controller):
   unexpected exits and startup failures restart with 2s/5s/10s backoff under a
   3-crash budget. Only a run that stayed up (`STABLE_SECS` = 60s) repays that
   budget, so a "ready, then dead" crash loop does exhaust it. When the budget
   is gone the supervisor **pauses instead of exiting**: the splash and the tray
   tooltip say so, and the tray's restart entry resumes supervision with a fresh
   budget. Manual "restart service" doesn't consume the quota; quitting
   (tray/quit_app) never triggers auto-restart.
6. Closing the window hides to tray (like the Electron shell); tray: left click
   shows the window, right click opens the menu (show / autostart toggle /
   restart dsh service / check for updates / quit). Stopping the child is
   two-phase: Ctrl+C so `dsh` runs its own bounded teardown, then kill after 3s.
   Every host is placed in a kill-on-close job object, so a force-killed or
   crashed shell never leaves a host tree behind — including the PATH tier's
   `cmd.exe` wrapper.
7. Single instance (second launch focuses the existing window); window
   geometry persists via the window-state plugin.
8. **Autostart**: the tray's "Toggle launch at login" writes
   `HKCU\...\Run\DSH` with `--autostart`. Such launches stay hidden in the
   tray, and a crash-restart splash does not pop a window either (log and tray
   tooltip only). `src-tauri/installer-hooks.nsh` deletes that Run value during
   uninstall, so removing the app leaves no login-time failure behind.
9. **Updater**: tray "Check for updates…" is two-step — the first click reports
   the version (splash log + tray tooltip), the click after it downloads,
   verifies, installs and restarts, stopping the host first.

## Development

Prereqs: Rust (MSVC toolchain, with VS Build Tools / WebView2), Node + pnpm.

```sh
pnpm install            # installs @tauri-apps/cli
pnpm --filter @deepseek-ai/dsh-desktop-tauri dev
```

Unit tests: `cd src-tauri && cargo test`.

## Packaging (Windows NSIS)

```sh
export TAURI_SIGNING_PRIVATE_KEY="$(cat keys/updater.key)"
export TAURI_SIGNING_PRIVATE_KEY_PASSWORD=""
pnpm --filter @deepseek-ai/dsh-desktop-tauri build
# artifacts in src-tauri/target/release/bundle/nsis/:
#   setup.exe and setup.exe.sig (minisign); maintain latest.json yourself
#   when publishing (URL + signature + version manifest)
```

The signing keypair lives in `keys/updater.key` (gitignored — **back it up
offline**; losing it breaks future signed updates). The public key is already
in `tauri.conf.json → plugins.updater.pubkey`, and
`plugins.updater.endpoints` already points at
`https://download.deepseek.com/dsh-desk-tauri/feeds/win-x64/latest.json`
— a `dsh-desk-tauri` object prefix separate from the Electron shell's
`dsh-desk`, so the two updaters never share manifests. Endpoints are baked
into installed apps; the feed goes live once the first publish runs.

## Auto-update (verified end-to-end)

Publishing mirrors the Electron shell's environment contract
(`apps/desktop/scripts/desktop-auto-update-environment.mjs`):

```sh
node scripts/publish-updates.mjs         # stages dist-update/ (latest.json + artifacts)
node scripts/publish-updates.mjs --cos   # also uploads to COS (same env names)
```

- **Endpoints must be https** — the updater validates at init; an http
  endpoint panics the app at startup.
- The version's single source of truth is `tauri.conf.json → version`
  (package.json / Cargo.toml are kept equal but unused).
- Support/E2E hook: start with `DSH_TAURI_UPDATE_CHECK=1` to run the tray
  update flow (check → download → verify → install) 3 seconds after startup.
- Known behavior: the silent NSIS update terminates the running app and
  does **not** relaunch it (upstream behavior). Observed: installed 0.1.0 →
  feed 0.1.1 → log "发现更新 0.1.1，开始下载并安装…" → installed version
  becomes 0.1.1 → relaunch reports up-to-date.
- Offline E2E recipe: self-signed cert with an `IP=127.0.0.1` SAN trusted in
  `CurrentUser\Root` (rustls uses the platform store) → import the PFX into
  **LocalMachine\My** (http.sys needs a machine-store key) →
  `netsh http add sslcert ipport=127.0.0.1:18443 …` → HttpListener serves
  `latest.json` + setup.exe → run the old install with the hook. Remove the
  cert and the binding afterwards.

## Desktop behavior conventions

- **No terminal window**: `main.rs` carries
  `#![cfg_attr(all(not(debug_assertions), target_os = "windows"), windows_subsystem = "windows")]`
  — without it the exe is a console binary and Windows shows a black window
  (title: the exe path) on every launch. Every spawn in the launch ladder
  (node, the PATH fallback's cmd wrapper, the where.exe probe) uses
  `CREATE_NO_WINDOW`. Do not switch to the "attach a hidden console" idea —
  its conhost window re-shows and cannot be reliably suppressed.
- **No native menu bar**: removed; everything lives in the **tray** (show,
  autostart toggle, restart service, check updates, quit).
- **Login bridge**: the main window injects `EXTERNAL_LINKS_JS` so in-page
  `window.open` calls (account-authorization preopened popups, help links)
  route to the system browser via the `open_external` command + opener
  plugin (http(s) only, checked by parsing the URL). A native webview denies
  `window.open` by default and the web login flow has no fallback — without
  this bridge sign-in silently does nothing. Keep the script.
- **IPC from the Host page is remote IPC**: the main window's document origin
  is the host, which Tauri classifies as a *remote* origin, so nothing that
  page invokes is reachable unless a capability grants it.
  `capabilities/host-page.json` grants that origin exactly one command
  (`allow-open-external`) and only for loopback hosts;
  `capabilities/default.json` grants the local splash its four commands.
  `build.rs` autogenerates the command permissions into
  `src-tauri/permissions/autogenerated/` (gitignored). Widening what the Host
  page may call is a security decision, not a convenience.
- **Navigation guard**: `on_navigation` keeps the main window on the host
  origin and routes everything else to the system browser — the equivalent of
  the Electron shell's `will-navigate` handler.
- **File drops belong to the page**: the main window uses
  `disable_drag_drop_handler()`, otherwise Tauri intercepts OS file drops and
  the Web GUI's attachment drop target never receives a `drop` event.
- **Icon**: `icon-src/icon.png` is the official
  `apps/desktop/resources/icon-windows.png` (the whale); regenerate with
  `pnpm exec tauri icon icon-src/icon.png` after changing it.

## Host log pipes (silent-crash prevention)

The host and its children (e.g. the Serena MCP cp936 banner) can emit
non-UTF-8 bytes. The Rust readers MUST pump the pipes at byte level with
lossy decoding (`pump_lines` in server.rs): a `BufReader::lines()` loop
ends at the first invalid sequence, dropping the pipe's read handle, so
the host's next log write gets EPIPE and the process exits 1 silently —
visible as "服务意外停止" crash loops. Unit test
`pump_lines_survives_non_utf8…` pins this behavior.

## The closure must carry the client build environment (login regression)

runtime-cli is deployed from the repository's built package libs. Those client
libs MUST be built with an inlined `DSH_CLIENT_VERSION` define; without it the
account UI's `client()` throws at the first sign-in click ("登录失败:
operation not completed", and no browser ever opens):

```powershell
$env:DSH_CLIENT_VERSION = (Get-Content package.json | ConvertFrom-Json).version
$env:DSH_CLIENT_COMMIT_HASH = git rev-parse HEAD   # checkouts whose .git was cleared: use the official baseline hash in LOCAL-CHANGES.txt at the repo root
pnpm run build:lib:client; pnpm run build:web
cd apps\desktop-tauri; node scripts\prepare-runtime-cli.mjs
```

`verify-runtime-cli.mjs` fails the closure when it finds the un-inlined
`({}).DSH_CLIENT_VERSION` shape. Frequent DSH releases are exactly the point:
per release, rebuild the closure as above, `pnpm run build` a new signed
installer, `publish:updates` to the feed — installed shells follow via the
tray "检查更新" (or the `DSH_TAURI_UPDATE_CHECK=1` startup hook).

## Runtime closure regression guard

`pnpm run verify:runtime` (auto-run at the end of `prepare:runtime` and as a
`build` pre-check): asserts closure integrity (no symlinks, web-frontend
dist, every workspace dep resolved, app self-links removed) and runs a
`bin.js --version` probe from inside the closure. An empty placeholder
directory passes silently (the fallback chain takes over).

## Bundling a runtime (self-contained installer)

One-shot (requires `pnpm install` and repo build artifacts from `pnpm run build`):

```sh
node scripts/prepare-runtime-cli.mjs   # generates runtime-cli/ and runtime-node/
pnpm exec tauri build
```

What the script does (details and pitfalls):

1. `pnpm --filter @deepseek-ai/dsh deploy --prod --config.node-linker=hoisted --config.allowUnusedPatches=true runtime-cli`
   — **hoisted** (flat real directories) is required: the default symlink
   layout breaks once the bundler copies the tree; `allowUnusedPatches` is
   needed because the repository's global patch set does not match the CLI
   subset; the newer shared-lockfile deploy is buggy here (it drops the
   workspace-linked `dsh-web-frontend`), while the hoisted path is complete.
2. Materializes the workspace symlinks left behind by the deploy into real
   directories (links into vendor/packages are guaranteed to break on an
   installed machine).
3. Removes the app self-links (`dsh-desktop`/`dsh-desktop-tauri`) that the
   deploy drags in, otherwise the whole Rust target tree lands in the bundle
   (it once exceeded 9 GB).
4. `runtime-node/node.exe`: a portable node (the script copies the node that
   runs it; swap in the node.exe from the official win-x64 zip instead).
5. `tauri.conf.json → bundle.resources` maps both into the package as
   `resources/cli/` and `resources/node/`; the shell prefers them through the
   launch ladder (log line `spawned [bundled resources/cli]`).

`runtime-cli/` and `runtime-node/` are gitignored; the closure is pure runtime
(office/speech/terminal natives, ~520 MB). The deploy runs into staging
directories and is verified there before the working closure is replaced, so a
failed deploy can never leave the bundle with an empty closure.

## Environment overrides

| Var | Meaning |
| --- | --- |
| `DSH_TAURI_CMD` / `DSH_TAURI_ARGS` / `DSH_TAURI_CWD` | fully custom launch (ARGS is a JSON array) |
| `DSH_TAURI_NODE` | node executable (wins over bundled/PATH node) |
| `DSH_CHECKOUT` | checkout root containing `apps/cli/lib/bin.js` |
| `DSH_TAURI_PORT` | fixed host port (default 3090; a busy port is reported, never dodged) |
| `DSH_TAURI_PROFILE` | profile the host boots (default `web`; `desktop` is reserved for the Electron shell) |
| `DSH_TAURI_HOME` | sets the child's `DSH_HOME`: sessions, logs, and the task-board ledger lock are per-home, so an installed app running beside a terminal session needs its own |
| `DSH_TAURI_CWD` | the Host's working directory — sessions are bucketed per workspace, so set it to the project you use in the browser (installed-app default: the user's home, never the install directory) |
| `DSH_TAURI_TAKEOVER` | default on: before starting its Host the shell ends the DSH host that owns the task-board ledger in this `DSH_HOME` (graceful Ctrl+C, then force). `0` leaves the other host running and the board stays with it |

Shell log: `%TEMP%/dsh-tauri.log` (also shown at the bottom of the splash).
Every line carries a UTC timestamp and `token=` values are masked, because the
host prints its authenticated URL and every stdout line is mirrored there.

## One host per state root

Only one Host can serve a `$DSH_HOME`: the task board has a single owner
(`$DSH_HOME/task-board/ledger-v2.lock`), and a Host holds kernel write leases on
every session it has opened — a second Host gets `session/writer-held` the
moment it resumes one of them. Before starting its Host the shell therefore
retires the other DSH Hosts on this machine — every login session but session 0,
because the two Hosts are routinely in different ones: first the one recorded in
the board lock, verified against its start time because PIDs get recycled, then
any other live process whose image is exactly a JS runtime (`node.exe` and
friends), whose command line runs a `bin.js` with the whole argument `web`, and
whose account is the one running this app — the account and image checks exist
because a takeover force-kills process trees. An **absolute** script path must
exist on disk; a **relative** one (the shape a developer gets from a checkout)
cannot be verified, because it belongs to the other process's working directory
which Windows does not expose cheaply, so the image, boundary and whole-argument
rules carry that case alone. Each takeover line also names what triggered the
launch (cold start, crash restart, second launch, tray restart). Each gets Ctrl+C
and is forced only after the same grace the shell gives its own child, and every
action is logged. One limit is Windows integrity levels: a Host started from an
elevated terminal is invisible (no command line) and untouchable to a desktop app
started from Explorer, and the shell then logs `接管受阻` instead of guessing. Run
both at the same privilege, or start the app elevated. Set
`DSH_TAURI_TAKEOVER=0` to leave the other hosts running, and note that an
already-running instance keeps whatever policy its own environment had: launching
the app again focuses that instance **and** makes it retake the state root, then
bounce its own Host so it re-acquires the task board.

## Differences vs the Electron shell

- Runtime source: the Electron shell bundles its own primary runtime; the
  Tauri shell reuses the checkout's dsh CLI by default, and can leave the
  checkout behind once a runtime is bundled as above.
- Uses the public `dsh web` startup-line protocol (token URL) instead of the
  `dsh-desktop-host` stdio JSON handshake — simpler, but no host business IPC:
  there is no shutdown handshake, so the shell sends Ctrl+C (which `dsh`
  answers with its own bounded teardown) and only then kills, where the
  Electron shell issues an explicit shutdown request and confirms running
  tasks first.
- Not yet ported: crash recovery dialogs, dsh command installation manager,
  accounts/login, mandatory-update window, telemetry, macOS/Linux packaging.
