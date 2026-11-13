; NSIS installer hooks for the DSH desktop shell (referenced from
; tauri.conf.json: bundle.windows.nsis.installerHooks).
;
; The shell ships no DSH runtime: it mounts into an existing harness (a
; deepseek-harness checkout) at <harness>\apps\desktop-tauri and runs that
; checkout's own CLI — which is also what the shell's launch tiers look for when
; they walk up from their own executable.
;
; This file is a template: scripts/prepare-installer-hooks.mjs substitutes
; @@INSTALLER_DIR@@ with the absolute path of src-tauri/installer (Tauri inlines
; the hooks into the generated installer.nsi, so relative "File" paths would
; resolve against the build directory instead of the sources).
;
; So before anything is copied, find the harness:
;   1. detect-harness.ps1: remembered root, running Hosts, session buckets, PATH,
;      then a depth-limited disk scan;
;   2. if nothing is found, ask: deploy a fresh harness from the official
;      repository, or point at an existing directory;
;   3. cancel aborts, because installing into a directory that is not a harness
;      produces an application that cannot start.

!include "LogicLib.nsh"

!macro NSIS_HOOK_PREINSTALL
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "@@INSTALLER_DIR@@\detect-harness.ps1"
  File "@@INSTALLER_DIR@@\deploy-harness.ps1"

  StrCpy $1 "" ; harness root

  DetailPrint "正在查找已安装的 DeepSeek Harness…"
  nsExec::ExecToStack 'powershell -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\detect-harness.ps1" -Best'
  Pop $0 ; exit code
  Pop $2 ; first output line
  ${If} $0 == "0"
  ${AndIf} $2 != ""
  ${AndIf} $2 != "NO-HARNESS-FOUND"
    StrCpy $1 $2
    DetailPrint "找到 Harness：$1"
  ${EndIf}

  ${If} $1 == ""
    DetailPrint "未找到 Harness，等待用户选择"
    MessageBox MB_YESNOCANCEL|MB_ICONQUESTION \
      "没有找到已安装的 DeepSeek Harness。$\r$\n$\r$\n是：从官方仓库部署一个新的 Harness$\r$\n（需要 git、Node.js 24.9+ 与网络，耗时几分钟）$\r$\n否：手动指定已有的 Harness 目录$\r$\n取消：退出安装" \
      IDYES dsh_deploy IDNO dsh_pick
    Abort

    dsh_deploy:
      StrCpy $3 "D:\Program files"
      ${IfNot} ${FileExists} "$3\*.*"
        StrCpy $3 "$LOCALAPPDATA"
      ${EndIf}
      nsDialogs::SelectFolderDialog "选择 Harness 的部署位置（将在其中创建 deepseek-harness 目录）" "$3"
      Pop $4
      ${If} $4 == ""
      ${OrIf} $4 == "error"
        Abort
      ${EndIf}
      StrCpy $5 "$4\deepseek-harness"
      DetailPrint "部署 Harness 到 $5（git clone + pnpm install + build，可能需要几分钟，界面暂时不会有反应）"
      nsExec::ExecToLog 'powershell -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\deploy-harness.ps1" -Target "$5"'
      Pop $0
      ${If} $0 != "0"
        MessageBox MB_OK|MB_ICONSTOP \
          "Harness 部署失败（退出码 $0）。$\r$\n$\r$\n请查看 %TEMP%\dsh-deploy.log，然后重新运行安装包并选择手动指定目录。"
        Abort
      ${EndIf}
      StrCpy $1 $5
      Goto dsh_have_root

    dsh_pick:
      dsh_pick_again:
      nsDialogs::SelectFolderDialog "选择已安装的 DeepSeek Harness 目录（包含 apps\cli\lib\bin.js）" "D:\Program files"
      Pop $4
      ${If} $4 == ""
      ${OrIf} $4 == "error"
        Abort
      ${EndIf}
      ; Accept the harness root, or one level up if the user pointed there.
      ${IfNot} ${FileExists} "$4\apps\cli\lib\bin.js"
        ${If} ${FileExists} "$4\deepseek-harness\apps\cli\lib\bin.js"
          StrCpy $4 "$4\deepseek-harness"
        ${Else}
          MessageBox MB_OK|MB_ICONEXCLAMATION "$4 看起来不是 DSH Harness 目录（找不到 apps\cli\lib\bin.js）。"
          Goto dsh_pick_again
        ${EndIf}
      ${EndIf}
      StrCpy $1 $4

    dsh_have_root:
  ${EndIf}

  StrCpy $INSTDIR "$1\apps\desktop-tauri"
  ; Tauri's template runs `SetOutPath $INSTDIR` BEFORE this hook, so every File
  ; command that follows would still write into the directory picked at init time
  ; (the private default). Re-point the output path at the directory we just
  ; chose; SetOutPath also creates it.
  SetOutPath "$INSTDIR"
  WriteRegStr HKCU "Software\DeepSeek\DSH Desktop" "HarnessRoot" "$1"
  DetailPrint "安装位置：$INSTDIR"
!macroend

!macro NSIS_HOOK_POSTINSTALL
  WriteRegStr HKCU "Software\DeepSeek\DSH Desktop" "InstallRoot" "$INSTDIR"
!macroend

!macro NSIS_HOOK_POSTUNINSTALL
  ; The tray's autostart toggle registers the shell under
  ; HKCU\Software\Microsoft\Windows\CurrentVersion\Run\<product name>, which
  ; tauri-plugin-autostart names after `package_info().name` ("DSH"). Removing
  ; the program must remove that entry too: a leftover value pointing at a
  ; deleted executable fails silently on every later sign-in. The entry may
  ; legitimately be absent, so the command's exit code is ignored.
  nsExec::ExecToLog 'reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "DSH" /f'
  ; HarnessRoot stays on purpose: it is a hint for the next install, and the
  ; detection validates it before trusting it.
  DeleteRegValue HKCU "Software\DeepSeek\DSH Desktop" "InstallRoot"
!macroend
