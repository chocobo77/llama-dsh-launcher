// GUI subsystem: without this the whole exe is a *console* binary, and
// Windows allocates it a visible terminal window on every launch (title:
// the exe path) — the one thing a desktop shell must never show.
#![cfg_attr(
    all(not(debug_assertions), target_os = "windows"),
    windows_subsystem = "windows"
)]

fn main() {
    dsh_desktop_tauri_lib::run()
}
