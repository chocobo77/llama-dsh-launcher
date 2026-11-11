; LlamaDSH_Launcher 安装程序脚本
; Inno Setup 6 中文版

#define MyAppName "DeepSeek Harness & Llama 启动器"
#define MyAppVersion "1.1.3"
#define MyAppPublisher "LlamaDSH"
#define MyAppExeName "LlamaDSH_Launcher.exe"
#define MyAppIcon "app.ico"

[Setup]
AppId={{A7F8B3C4-D5E6-4F78-9A0B-1C2D3E4F5A6B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; 安装目录默认值按"安装方式"给(逻辑见 [Code] DesiredInstallDir):
;   仅为自己安装 → {userpf} = %LocalAppData%\Programs(与 VS Code / Inno Setup 自身同一族用户级路径)
;   为所有用户安装 → D:\Program Files\LlamaDSH_Launcher(D 盘不存在时才退回 {commonpf})
DefaultDirName={code:PickInstallDir}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; MIT 许可随安装向导展示(此前 LicenseFile= 是空值, 用户装完都不知道授权条款)
LicenseFile=..\LICENSE
OutputDir={#SourcePath}installer_output
OutputBaseFilename=LlamaDSH_Launcher_Setup
SetupIconFile=installer_staging\{#MyAppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 两种安装方式(向导里选, 也可命令行 /ALLUSERS /CURRENTUSER 覆盖):
;   仅为自己安装 → PrivilegesRequired=lowest 的默认档, 不开 UAC, 装到 %LocalAppData%\Programs,
;                  卸载信息写 HKCU;
;   为所有用户安装 → Setup 自动重启为提权实例(过 UAC), 装到 Program Files, 卸载信息写 HKLM。
; 写死 admin 的历史问题: 非管理员账户连"装给自己用"都做不到, 直接被 UAC 拦死。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
; 与 App.xaml.cs 单实例 Mutex 同名: 安装/卸载前要求关闭运行中的启动器,
; 也防止手工跑 ISCC 时把 installer_staging 里正在被占用的旧 exe 打进包
AppMutex=LlamaDSH_Launcher_SingleInstance_Mutex
; 覆盖安装时 exe 正在运行会留下半新半旧的文件树: 让安装向导主动要求关闭
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableReadyPage=no
DisableFinishedPage=no
WizardSizePercent=110
; 中文语言
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
chinesesimplified.WelcomeLabel1=欢迎安装 [name]
chinesesimplified.WelcomeLabel2=这将安装 [name/ver] 到您的计算机。%n%n建议在安装前关闭其他应用程序。
chinesesimplified.FinishedLabel=安装已完成。您现在可以启动程序了。
chinesesimplified.ClickFinish=单击"完成"退出安装向导。
chinesesimplified.StatusRunProgram=正在完成安装...
chinesesimplified.ConfirmUninstall=确定要完全删除 %1 及其所有组件吗？
; 安装方式选择页文案(这几个键 ChineseSimplified.isl / Default.isl 里都有, 这里按本项目的
; 实际默认路径改写; Text1 是默认档为 admin 时用的那套措辞, 两种都覆盖掉免得措辞不一致)
chinesesimplified.PrivilegesRequiredOverrideTitle=选择安装方式
chinesesimplified.PrivilegesRequiredOverrideInstruction=两种方式使用的是同一个程序，区别只在装到哪里、需不需要管理员权限（注意：本机若缺少 .NET 8 Desktop Runtime，只有"为所有用户安装"这条路能装它）。
chinesesimplified.PrivilegesRequiredOverrideText1=%1 可以为所有用户安装（需要管理员权限，默认装到 D:\Program Files，没有 D 盘时是系统 Program Files），或仅为您自己安装（装到当前用户目录下的 Programs 文件夹；不需要管理员权限，但前提是本机已装好 .NET 8 Desktop Runtime——它只能按机器安装，缺它时仍需管理员）。
chinesesimplified.PrivilegesRequiredOverrideText2=%1 可以仅为您自己安装（装到当前用户目录下的 Programs 文件夹；不需要管理员权限，但前提是本机已装好 .NET 8 Desktop Runtime——它只能按机器安装，缺它时仍需管理员），或为所有用户安装（需要管理员权限，默认装到 D:\Program Files，没有 D 盘时是系统 Program Files）。
chinesesimplified.PrivilegesRequiredOverrideCurrentUser=仅为自己安装(&M)（不需要管理员权限；要求本机已有 .NET 8 运行时）
chinesesimplified.PrivilegesRequiredOverrideAllUsers=为所有用户安装(&A)（需要管理员权限；缺 .NET 8 时可自动装）
english.PrivilegesRequiredOverrideText1=%1 can be installed for all users (requires administrator privileges; into D:\Program Files by default, or the system Program Files if this PC has no D drive), or for you only (into the Programs folder inside your user profile; no administrator privileges, but only if the .NET 8 Desktop Runtime is already installed here - that runtime is a per-machine install and still needs an administrator).
english.PrivilegesRequiredOverrideText2=%1 can be installed for you only (into the Programs folder inside your user profile; no administrator privileges, but only if the .NET 8 Desktop Runtime is already installed here - that runtime is a per-machine install and still needs an administrator), or for all users (requires administrator privileges; into D:\Program Files by default, or the system Program Files if this PC has no D drive).
english.PrivilegesRequiredOverrideCurrentUser=Install for &me only (no administrator privileges; needs .NET 8 runtime already present)
english.PrivilegesRequiredOverrideAllUsers=Install for &all users (administrator privileges required; can install .NET 8 too)

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"
Name: "startmenuicon"; Description: "创建开始菜单快捷方式"; GroupDescription: "附加选项:"

[Files]
Source: "installer_staging\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer_staging\gputemp.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer_staging\llamadsh-cli.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer_staging\{#MyAppIcon}"; DestDir: "{app}\Resources"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Resources\{#MyAppIcon}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Resources\{#MyAppIcon}"; Tasks: desktopicon
Name: "{autostartmenu}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Resources\{#MyAppIcon}"; Tasks: startmenuicon

[Run]
; runasoriginaluser(Inno 6+): 装完自动启动走"原始用户"的链接令牌，**只换令牌，不改继承的环境块**。
; 实测(2026-11-11 09:38，机器级安装完自动启动那一份的应用日志)：仍是
;   installer/elevated lineage detected (compat=ElevateCreateProcess, elevated=False)
; 也就是说它挡不住 __COMPAT_LAYER 兼容层血统，只挡住了"继承提权"。别把它当第一道保险——
; 血统这条链上真正起作用的是程序侧的两条：白名单判定 + 一次封顶(见 Services\LineageGuard.cs)。
; 2026-11-10「仅为自己安装」装完无限反复启动，是那两条各缺一半叠加出来的。
Filename: "{app}\{#MyAppExeName}"; Description: "启动程序"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// =====================================================
//  环境检查 - 在安装开始前执行
// =====================================================

// 用于自动下载 .NET 运行时的系统 API
function URLDownloadToFile(Caller: LongWord; URL: string; FileName: string; Reserved: LongWord; StatusCB: LongWord): HResult;
external 'URLDownloadToFileW@urlmon.dll stdcall';

const
  DOTNET8_VERSION = '8.0.30';
  DOTNET8_URL = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/' + DOTNET8_VERSION + '/windowsdesktop-runtime-' + DOTNET8_VERSION + '-win-x64.exe';
  // 构建机 2026-11-04 从上述官方 URL 下载实测: 58,510,672 字节, Authenticode Valid(CN=.NET, O=Microsoft Corporation)。
  // 该运行时稍后以 runas 提权执行, 必须先过 SHA-256 + 验签双门, 不能只看文件存在。
  DOTNET8_SHA256 = '8BD710AFA5DE396C9EB2A3B68D00279B7B9ACA372A2443E9ACD4F48FFDEF3F2D';

// =====================================================
//  安装方式(仅自己 / 所有用户) —— 与程序侧 Services\InstallScope.cs 配对
//  规则说明见 docs\安装模式与提权门控-2026-11-10.md
// =====================================================

function GetDriveTypeW2(RootPath: String): LongWord;
external 'GetDriveTypeW@kernel32.dll stdcall';

const
  APP_DIR_NAME = 'LlamaDSH_Launcher';
  // 机器级安装优先落这块盘: 模型权重与 DSH 依赖都在 D 盘, 装进系统盘会把 C 盘挤爆。
  // 按要求「D:\Program Files 不存在就直接建」, 所以只拦两种情况: 压根没有 D 盘、
  // D 盘不是本机固定盘(U 盘/网络盘/光驱上建 Program Files 是帮倒忙)。空间不足不拦。
  DATA_DRIVE_ROOT = 'D:\';
  DRIVE_FIXED = 3;   // GetDriveTypeW: 2=可移动 3=固定 4=网络 5=光盘 6=RAM 盘
  SCOPE_MARKER = 'install-scope.txt';
  // 卸载注册表子键 = [Setup] AppId + '_is1'。AppId 那边用 "{{" 转义, 这里必须是明文 GUID,
  // 两处只能同步改, 否则"另一种安装方式已装过"的检测看不见对面那一份。
  UNINST_SUBKEY = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A7F8B3C4-D5E6-4F78-9A0B-1C2D3E4F5A6B}_is1';

function LocalFixedDrive(DriveRoot: String): Boolean;
begin
  Result := DirExists(DriveRoot) and (GetDriveTypeW2(DriveRoot) = DRIVE_FIXED);
end;

// 当前安装方式下的建议安装目录。{userpf}=用户目录\Programs, {commonpf}=系统 Program Files。
function DesiredInstallDir: String;
begin
  if IsAdminInstallMode then
  begin
    if LocalFixedDrive(DATA_DRIVE_ROOT) then
      Result := DATA_DRIVE_ROOT + 'Program Files\' + APP_DIR_NAME
    else
      Result := ExpandConstant('{commonpf}\' + APP_DIR_NAME);
  end
  else
    Result := ExpandConstant('{userpf}\' + APP_DIR_NAME);
end;

// DefaultDirName 走这里: 提权重启后的那次实例里 IsAdminInstallMode 才是 True,
// 所以机器级安装看到的默认值就是 D:\Program Files 而不是用户目录。
function PickInstallDir(Param: String): String;
begin
  Result := DesiredInstallDir;
end;

// 用户级卸载项在 HKCU、机器级在 HKLM, 两边互相看不见。不提醒的话,
// 用户会以为"装了两遍"或"卸载没卸干净", 实际上是各自一份。
function OtherScopeInstallLocation: String;
var
  Loc: String;
begin
  Result := '';
  Loc := '';
  if IsAdminInstallMode then
  begin
    if RegQueryStringValue(HKCU, UNINST_SUBKEY, 'InstallLocation', Loc) then Result := Loc;
  end
  else
  begin
    if RegQueryStringValue(HKLM, UNINST_SUBKEY, 'InstallLocation', Loc) then Result := Loc;
  end;
end;

var
  ScopeWarned: Boolean;   // 跨安装方式的提醒只弹一次

// 另一种安装方式已经装过 → 装完会变成两份独立安装(各自的卸载项)。
// 放在 PrepareToInstall(点"安装"之后、复制文件之前)提示: 这时还能取消回去改选方式。
// 返回 False = 用户选择中止本次安装。
// 注意文案与按钮必须自洽: 早期版本用 MB_OK 却让用户"点取消", 而 MB_OK 只有确定一个钮,
// 点下去等于默认继续装 —— 提示里说的操作根本做不到。这里给 YES/NO, 选否直接中止。
function WarnOtherScopeInstall: Boolean;
var
  Other: String;
begin
  Result := True;
  if ScopeWarned then Exit;
  ScopeWarned := True;
  Other := OtherScopeInstallLocation;
  if Other = '' then Exit;
  // 先把检测事实写进日志: 事后能判断"到底有没有看见对面那份"。
  Log('检测到另一种安装方式已装过: ' + Other);
  if WizardSilent then
  begin
    // 静默(/SILENT /VERYSILENT)下绝对不能弹 [Code] 的 MsgBox：实测它不受 /SUPPRESSMSGBOXES
    // 管辖，无人值守安装会一直卡在等点击（2026-11-10 我自己跑测试包时就是这样把弹窗
    // 连拍到用户屏幕上的）。静默场景按"继续装"处理，只留日志。
    Log('静默模式：不弹窗，继续安装（两份将并存，卸载列表里各有一条）');
    Exit;
  end;
  if MsgBox('注意：这台机器上还有一份用另一种安装方式装的同一个程序，位于' + #13#10 +
            '    ' + Other + #13#10#13#10 +
            '两种安装方式互不覆盖，装完会在卸载列表里出现两条。' + #13#10#13#10 +
            '要把这一份也装上吗？' + #13#10 +
            '选「否」将取消本次安装，您可以返回上一步改选另一种安装方式，或先卸载另一份。',
            mbConfirmation, MB_YESNO) = IDNO then
  begin
    Log('用户选择中止: 本机已有另一种安装方式装的同一程序');
    Result := False;
  end;
end;

// 把安装方式落到安装目录里, 程序启动时据此决定「以管理员身份启动」怎么呈现:
// 机器级 = exe 由 ACL 保护, 提权运行安全; 用户级 = 当前用户能改写 exe, 提权必须过 UAC。
procedure WriteScopeMarker;
var
  Scope: String;
begin
  try
    if IsAdminInstallMode then Scope := 'machine' else Scope := 'user';
    // 纯 ASCII: Inno 的 SaveStringToFile 落的是 ANSI 字节, 掺中文到了别的代码页就成了乱码,
    // 而这个文件是要被程序逐行解析的。含义写在 docs\安装模式与提权门控-2026-11-10.md。
    SaveStringToFile(ExpandConstant('{app}\' + SCOPE_MARKER),
      'scope=' + Scope + #13#10 +
      'dir=' + ExpandConstant('{app}') + #13#10 +
      // 注意: 后两个参数是"日期分隔符/时间分隔符"(Char, 不能传空串, 传空串会 Type Mismatch),
      // 之前写成 '"' 结果出来 2026"11"10 这种怪串。
      'installedAt=' + GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':') + #13#10 +
      'written-by=installer.iss' + #13#10 +
      'note=scope is user (per-user install) or machine (all-users install).' + #13#10 +
      'note2=if this file is missing the program falls back to the uninstall registry keys.' + #13#10,
      False);
  except
    // 写失败不阻断安装(程序侧还有注册表与兜底两级判断), 但必须把原因留在 setup log 里,
    // 否则事后排查只看到"标记文件没了", 分不清是防病毒拦了还是脚本自己抛的。
    Log('安装方式标记 ' + SCOPE_MARKER + ' 写入失败: ' + GetExceptionMessage);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then WriteScopeMarker;
end;

function IsWindows10OrLater: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major >= 10);
end;

function IsX64: Boolean;
begin
  Result := IsWin64;
end;

// ----------------------------------------------------
//  .NET 8 Desktop Runtime 检测
//  优先级：dotnet --list-runtimes（最可靠） > 目录检测（回退）
// ----------------------------------------------------

// 在常见位置查找 dotnet.exe
function FindDotNetExe: String;
var
  Candidates: array[0..5] of String;
  I: Integer;
begin
  Result := '';
  Candidates[0] := ExpandConstant('{sd}') + '\Program Files\dotnet\dotnet.exe';
  Candidates[1] := ExpandConstant('{sd}') + '\Program Files (x86)\dotnet\dotnet.exe';
  Candidates[2] := GetEnv('USERPROFILE') + '\.dotnet\dotnet.exe';
  Candidates[3] := ExpandConstant('{localappdata}') + '\Microsoft\dotnet\dotnet.exe';
  if GetEnv('DOTNET_ROOT') <> '' then
    Candidates[4] := GetEnv('DOTNET_ROOT') + '\dotnet.exe'
  else
    Candidates[4] := '';
  Candidates[5] := 'dotnet.exe'; // PATH 中查找
  for I := 0 to 5 do
  begin
    if (Candidates[I] <> '') and FileExists(Candidates[I]) then
    begin
      Result := Candidates[I];
      Exit;
    end;
  end;
end;

// 用 dotnet --list-runtimes 检测（能覆盖 SDK 内置运行时、用户级安装、非标准路径等所有情况）
function CheckDotNetViaCommand: Boolean;
var
  DotNetExe, OutputFile, CmdLine: String;
  OutputAnsi: AnsiString;
  Output: String;
  ResultCode: Integer;
begin
  Result := False;
  DotNetExe := FindDotNetExe;
  if DotNetExe = '' then Exit;

  OutputFile := ExpandConstant('{tmp}\dotnet_runtimes.txt');
  // 用 cmd /c 重定向 stdout 到文件（Inno Setup Exec 不支持直接重定向）
  // 命令格式：cmd /c ""dotnet.exe" --list-runtimes > "output.txt" 2>&1"
  CmdLine := '/c ""' + DotNetExe + '" --list-runtimes > "' + OutputFile + '" 2>&1"';
  if Exec('cmd.exe', CmdLine, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringFromFile(OutputFile, OutputAnsi) then
    begin
      Output := String(OutputAnsi);
      // 输出格式：Microsoft.WindowsDesktop.App 8.0.x [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
      if Pos('Microsoft.WindowsDesktop.App 8.', Output) > 0 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
  // 清理临时文件
  if FileExists(OutputFile) then DeleteFile(OutputFile);
end;

// 目录检测（回退方案：dotnet.exe 不在 PATH 且不在常见位置时使用）
function DotNetDesktop8ExistsIn(const Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if DirExists(Root + '\shared\Microsoft.WindowsDesktop.App') then
  begin
    if FindFirst(Root + '\shared\Microsoft.WindowsDesktop.App\8.*', FindRec) then
    begin
      // 确认是目录而非文件
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        Result := True;
      FindClose(FindRec);
    end;
  end;
end;

function IsDotNet8DesktopInstalled: Boolean;
var
  Roots: array[0..4] of String;
  I: Integer;
begin
  Result := False;

  // 1. 优先用 dotnet --list-runtimes 检测（最可靠）
  if CheckDotNetViaCommand then
  begin
    Result := True;
    Exit;
  end;

  // 2. 回退：目录检测
  Roots[0] := ExpandConstant('{sd}') + '\Program Files\dotnet';
  Roots[1] := ExpandConstant('{sd}') + '\Program Files (x86)\dotnet';
  Roots[2] := GetEnv('USERPROFILE') + '\.dotnet';
  Roots[3] := ExpandConstant('{localappdata}') + '\Microsoft\dotnet';
  if GetEnv('DOTNET_ROOT') <> '' then
    Roots[4] := GetEnv('DOTNET_ROOT')
  else
    Roots[4] := '';
  for I := 0 to 4 do
  begin
    if Roots[I] <> '' then
    begin
      if DotNetDesktop8ExistsIn(Roots[I]) then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

// 进度页状态更新辅助（用 SetText 更新副标题，SetProgress 更新进度条）
procedure SetStatus(Page: TOutputProgressWizardPage; Msg: String);
begin
  Page.SetText('安装 .NET 8.0 Desktop Runtime', Msg);
end;

// ----------------------------------------------------
//  自动下载并安装 .NET 8 Desktop Runtime
//  进度集成在安装程序界面内（TOutputProgressWizardPage），不弹终端
// ----------------------------------------------------
// 完整性门: 对将要 runas 提权执行的运行时安装包做 SHA-256(硬编码基线) + Authenticode 双验证。
// 通过 powershell 现算(系统自带), 结果写临时文件回读; 任何一步不通过都返回 False。
function VerifyDownloadedDotNetInstaller(InstallerPath: String): Boolean;
var
  OutFile, PsCmd: String;
  ResultCode: Integer;
  Lines: TArrayOfString;
  I: Integer;
  HashOk, SigOk: Boolean;
begin
  Result := False;
  HashOk := False;
  SigOk := False;
  OutFile := ExpandConstant('{tmp}\dotnet_verify.txt');
  if FileExists(OutFile) then DeleteFile(OutFile);
  PsCmd := '-NoProfile -ExecutionPolicy Bypass -Command "' +
    '$ErrorActionPreference=''Stop''; $p=''' + InstallerPath + ''';' +
    'if ((Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash -ieq ''' + DOTNET8_SHA256 + ''') { Write-Output HASHOK } else { Write-Output HASHBAD };' +
    '$s = Get-AuthenticodeSignature -LiteralPath $p;' +
    'if (($s.Status -eq ''Valid'') -and ($s.SignerCertificate.Subject -match ''Microsoft Corporation'')) { Write-Output SIGOK } else { Write-Output SIGBAD }"' +
    ' > "' + OutFile + '" 2>&1';
  if not Exec('cmd.exe', '/c powershell.exe ' + PsCmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;
  if not LoadStringsFromFile(OutFile, Lines) then
    Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Pos('HASHOK', Lines[I]) > 0 then HashOk := True;
    if Pos('SIGOK', Lines[I]) > 0 then SigOk := True;
  end;
  Result := HashOk and SigOk;
end;

function DownloadAndInstallDotNet8(): Boolean;
var
  InstallerPath: String;
  ResultCode: Integer;
  Hr: HResult;
  ProgressPage: TOutputProgressWizardPage;
begin
  Result := False;
  InstallerPath := ExpandConstant('{tmp}\windowsdesktop-runtime-' + DOTNET8_VERSION + '-win-x64.exe');

  // 创建进度页（集成在安装向导内，不弹外部窗口）
  ProgressPage := CreateOutputProgressPage('安装 .NET 8.0 Desktop Runtime', '正在准备环境...');
  ProgressPage.Show;
  ProgressPage.SetProgress(1, 10);

  try
    // ===== 1. 下载 =====
    SetStatus(ProgressPage, '正在下载 .NET ' + DOTNET8_VERSION + ' Desktop Runtime（约 50MB）...');
    SetStatus(ProgressPage, '下载地址：' + DOTNET8_URL);
    ProgressPage.SetProgress(3, 10);

    // 优先 curl（静默模式 -s，隐藏窗口 SW_HIDE）
    if not Exec('curl.exe', '-L -s -o "' + InstallerPath + '" "' + DOTNET8_URL + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      // curl 不可用，回退系统 API（无进度但不弹窗）
      SetStatus(ProgressPage, 'curl 不可用，改用系统下载组件...');
      Hr := URLDownloadToFile(0, DOTNET8_URL, InstallerPath, 0, 0);
      if Hr <> 0 then
      begin
        SetStatus(ProgressPage, '下载失败（错误码：' + IntToStr(Hr) + '）');
        ProgressPage.Hide;
        MsgBox('下载 .NET ' + DOTNET8_VERSION + ' Desktop Runtime 失败。' + #13#10#13#10 +
               '请手动下载安装：https://dotnet.microsoft.com/download/dotnet/8.0', mbError, MB_OK);
        Exit;
      end;
    end
    else if ResultCode <> 0 then
    begin
      SetStatus(ProgressPage, '下载失败（curl 错误码：' + IntToStr(ResultCode) + '）');
      ProgressPage.Hide;
      MsgBox('下载 .NET ' + DOTNET8_VERSION + ' Desktop Runtime 失败。' + #13#10#13#10 +
             '请手动下载安装：https://dotnet.microsoft.com/download/dotnet/8.0', mbError, MB_OK);
      Exit;
    end;

    if not FileExists(InstallerPath) then
    begin
      SetStatus(ProgressPage, '下载完成但找不到安装文件（可能被安全软件拦截）');
      ProgressPage.Hide;
      MsgBox('下载完成但找不到安装文件，请检查安全软件是否拦截。', mbError, MB_OK);
      Exit;
    end;

    SetStatus(ProgressPage, '下载完成。校验完整性...');
    ProgressPage.SetProgress(5, 10);

    // ===== 1.5 完整性验证: SHA-256 + Authenticode(缺一不放行, 产物将以管理员权限执行) =====
    if not VerifyDownloadedDotNetInstaller(InstallerPath) then
    begin
      SetStatus(ProgressPage, '完整性校验未通过, 已删除下载文件并中止。');
      DeleteFile(InstallerPath);
      ProgressPage.Hide;
      MsgBox('下载的安装包未通过 SHA-256/数字签名校验, 已中止安装（网络通道可能被劫持）。' + #13#10#13#10 +
             '请手动下载安装：https://dotnet.microsoft.com/download/dotnet/8.0', mbError, MB_OK);
      Exit;
    end;
    SetStatus(ProgressPage, '完整性校验通过（SHA-256 + Microsoft 数字签名）。');
    ProgressPage.SetProgress(6, 10);

    // ===== 2. 安装 =====
    SetStatus(ProgressPage, '正在安装 .NET ' + DOTNET8_VERSION + ' Desktop Runtime...');
    SetStatus(ProgressPage, '安装过程中可能弹出 UAC 提权提示，请点击"是"。');
    ProgressPage.SetProgress(7, 10);

    // ShellExec 'runas' 触发 UAC 提权，SW_HIDE 隐藏安装程序窗口，/quiet 静默安装
    if not ShellExec('runas', InstallerPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      SetStatus(ProgressPage, '无法启动 .NET 安装程序（可能被安全软件拦截或用户取消 UAC）');
      ProgressPage.Hide;
      MsgBox('无法启动 .NET 安装程序。' + #13#10#13#10 +
             '请手动下载安装：https://dotnet.microsoft.com/download/dotnet/8.0', mbError, MB_OK);
      Exit;
    end;

    ProgressPage.SetProgress(9, 10);

    // .NET 安装包退出码：0=成功，1602=用户取消，其他=失败
    if ResultCode = 0 then
    begin
      Result := True;
      SetStatus(ProgressPage, '.NET ' + DOTNET8_VERSION + ' Desktop Runtime 安装成功！');
      ProgressPage.SetProgress(10, 10);
    end
    else if ResultCode = 1602 then
    begin
      SetStatus(ProgressPage, '用户取消了 .NET 安装');
      ProgressPage.Hide;
      MsgBox('您取消了 .NET 安装，本程序无法继续。', mbError, MB_OK);
      Exit;
    end
    else
    begin
      SetStatus(ProgressPage, '.NET 安装程序返回错误码：' + IntToStr(ResultCode));
      ProgressPage.Hide;
      MsgBox('.NET 安装失败（错误码：' + IntToStr(ResultCode) + '）。' + #13#10#13#10 +
             '请手动下载安装：https://dotnet.microsoft.com/download/dotnet/8.0', mbError, MB_OK);
      Exit;
    end;
  finally
    ProgressPage.Hide;
  end;

  if Result then
  begin
    MsgBox('.NET ' + DOTNET8_VERSION + ' Desktop Runtime 安装成功！' + #13#10#13#10 +
           '点击确定继续安装本程序。' + #13#10 +
           '（如运行本程序时提示缺少 .NET，请重启计算机后再试）', mbInformation, MB_OK);
  end;
end;

// ----------------------------------------------------
//  显卡检测（信息提示）
// ----------------------------------------------------
function Pad4(I: Integer): String;
begin
  Result := IntToStr(I);
  while Length(Result) < 4 do Result := '0' + Result;
end;

function HasRealGpu: Boolean;
var
  I: Integer;
  SubKey, Desc: String;
begin
  Result := False;
  for I := 0 to 31 do
  begin
    SubKey := 'SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\' + Pad4(I);
    if RegQueryStringValue(HKEY_LOCAL_MACHINE, SubKey, 'Driver Desc', Desc) then
    begin
      if (Pos('Virtual', Desc) = 0) and (Pos('Microsoft Basic Display', Desc) = 0) then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function CheckDiskSpace: Boolean;
begin
  Result := True; // 由安装向导自动检查
end;

// ----------------------------------------------------
//  安装前环境检查入口
// ----------------------------------------------------
//  静默安装的提问规则(M2)：静默下没有任何弹窗能有人回答 —— [Code] 里的 MsgBox 不受
//  /SUPPRESSMSGBOXES 管辖(实测)，/VERYSILENT 跑到那一行会一直等点击，现场只看到"安装器卡住"。
//  规则：静默模式只做**硬失败检查**，一切信息性询问跳过(只写日志)，按"继续"处理。
// ----------------------------------------------------
function AskYesNoOrAssume(const Prompt: String): Boolean;
var
  OneLine: String;
begin
  if WizardSilent then
  begin
    OneLine := Prompt;
    StringChange(OneLine, #13#10, ' | ');
    Log('静默模式: 信息性询问已跳过，按"继续"处理。原提问: ' + OneLine);
    Result := True;
  end
  else
    Result := (MsgBox(Prompt, mbConfirmation, MB_YESNO) = IDYES);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Messages: String;
  HasError: Boolean;
begin
  Result := '';
  Messages := '';
  HasError := False;

  // 检查 0: 另一种安装方式是否已经装过(先说清楚, 免得等 .NET 下载完才发现装成了两份)
  if not WarnOtherScopeInstall then
  begin
    Result := '已取消安装：这台机器上已有另一种安装方式装的同一个程序（上面已说明它的位置）。';
    Exit;
  end;

  // 检查 1: Windows 版本
  if not IsWindows10OrLater then
  begin
    Messages := Messages + '❌ 本程序需要 Windows 10 或更高版本。' + #13#10;
    HasError := True;
  end;

  // 检查 2: x64 架构
  if not IsX64 then
  begin
    Messages := Messages + '❌ 本程序需要 64 位 Windows 系统。' + #13#10;
    HasError := True;
  end;

  // 检查 2.5: .NET 8 Desktop Runtime — 缺少时自动下载安装（集成到安装流程）
  if not IsDotNet8DesktopInstalled then
  begin
    if WizardSilent then
    begin
      // 静默下不做"自动下载并安装 .NET"：那步走 ShellExec('runas')，无人应答就会挂住；
      // 而且它是机器级安装 —— 「仅为自己安装」的普通用户本来也做不到。直接明确失败，
      // 并把出路写清楚(M1: 「仅为自己安装」只在"本机已有运行时"时才真的不需要管理员)。
      Result := '缺少 .NET ' + DOTNET8_VERSION + ' Desktop Runtime：静默安装不会自动下载安装它'
        + '（安装运行时需要管理员权限）。请先装好 .NET ' + DOTNET8_VERSION + ' Desktop Runtime，'
        + '或重新运行安装包并选择「为所有用户安装」。';
      Exit;
    end;
    if MsgBox('检测到系统缺少 .NET ' + DOTNET8_VERSION + ' Desktop Runtime（本程序运行必需，约 50MB）。' + #13#10#13#10 +
              '是否现在自动下载并安装？' + #13#10 +
              '（下载和安装进度将显示在安装向导内，完成后继续本程序的安装。'
              + '注意：安装运行时需要管理员权限 —— 若刚才选的是「仅为自己安装」，这里得给出管理员账号）',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      if not DownloadAndInstallDotNet8() then
      begin
        Result := '缺少 .NET ' + DOTNET8_VERSION + ' Desktop Runtime，安装无法继续。';
        Exit;
      end;
    end
    else
    begin
      Result := '缺少 .NET ' + DOTNET8_VERSION + ' Desktop Runtime，用户取消安装。';
      Exit;
    end;
  end;

  // 检查 3: 磁盘空间 (>= 200MB)
  if not CheckDiskSpace then
  begin
    Messages := Messages + '❌ 安装目录所在磁盘剩余空间不足 200MB。' + #13#10;
    HasError := True;
  end;

  // 检查 4: GPU (仅信息提示)
  if HasRealGpu then
    Messages := Messages + '✅ 已检测到显卡，GPU 监控功能可用（NVIDIA / AMD / Intel）' + #13#10;

  // 检查 5: llama-server.exe 常见位置 (仅信息提示)
  if FileExists('C:\llama.cpp\build\bin\Release\llama-server.exe') then
    Messages := Messages + '✅ 已找到 llama-server.exe (C:\llama.cpp)' + #13#10
  else if FileExists(GetEnv('USERPROFILE') + '\llama.cpp\build\bin\Release\llama-server.exe') then
    Messages := Messages + '✅ 已找到 llama-server.exe (用户目录)' + #13#10;

  // 显示检查结果
  if HasError then
  begin
    Result := '环境检查未通过：' + #13#10#13#10 + Messages + #13#10 + '请解决上述问题后重新运行安装程序。';
  end
  else
  begin
    // 非阻断性信息(✅ 已检测到显卡之类)。这台机器上 HasRealGpu 基本总为真，所以这一问
    // 以前在 /VERYSILENT 下也照弹 = 无人值守安装卡死。静默下只记日志、继续装。
    if Length(Messages) > 0 then
    begin
      if not AskYesNoOrAssume('环境检查结果：' + #13#10#13#10 + Messages + #13#10 + '是否继续安装？') then
        Result := '用户取消安装。';
    end;
  end;
end;
