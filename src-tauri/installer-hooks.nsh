; NSIS installer hooks for the DSH desktop shell (referenced from
; tauri.conf.json: bundle.windows.nsis.installerHooks).
!macro NSIS_HOOK_POSTUNINSTALL
  ; The tray's autostart toggle registers the shell under
  ; HKCU\Software\Microsoft\Windows\CurrentVersion\Run\<product name>, which
  ; tauri-plugin-autostart names after `package_info().name` ("DSH"). Removing
  ; the program must remove that entry too: a leftover value pointing at a
  ; deleted executable fails silently on every later sign-in. The entry may
  ; legitimately be absent, so the command's exit code is ignored.
  nsExec::ExecToLog 'reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "DSH" /f'
!macroend
