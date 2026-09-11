; LlamaDSH_Launcher 安装程序脚本
; Inno Setup 6 中文版

#define MyAppName "DeepSeek Harness & Llama 启动器"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "LlamaDSH"
#define MyAppExeName "LlamaDSH_Launcher.exe"
#define MyAppIcon "app.ico"

[Setup]
AppId={{A7F8B3C4-D5E6-4F78-9A0B-1C2D3E4F5A6B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\LlamaDSH_Launcher
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=
OutputDir={#SourcePath}installer_output
OutputBaseFilename=LlamaDSH_Launcher_Setup
SetupIconFile=installer_staging\{#MyAppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
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
chinesesimplified.UninstallSuccessLabel=%1 已成功从您的计算机中删除。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"
Name: "startmenuicon"; Description: "创建开始菜单快捷方式"; GroupDescription: "附加选项:"

[Files]
Source: "installer_staging\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer_staging\gputemp.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "installer_staging\{#MyAppIcon}"; DestDir: "{app}\Resources"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Resources\{#MyAppIcon}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Resources\{#MyAppIcon}"; Tasks: desktopicon
Name: "{userstartmenu}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Resources\{#MyAppIcon}"; Tasks: startmenuicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动程序"; Flags: nowait postinstall skipifsilent

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

    SetStatus(ProgressPage, '下载完成。');
    ProgressPage.SetProgress(5, 10);

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
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Messages: String;
  HasError: Boolean;
begin
  Result := '';
  Messages := '';
  HasError := False;

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
    if MsgBox('检测到系统缺少 .NET ' + DOTNET8_VERSION + ' Desktop Runtime（本程序运行必需，约 50MB）。' + #13#10#13#10 +
              '是否现在自动下载并安装？' + #13#10 +
              '（下载和安装进度将显示在安装向导内，完成后继续本程序的安装）',
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
    // 非阻断性警告
    if Length(Messages) > 0 then
    begin
      if MsgBox('环境检查结果：' + #13#10#13#10 + Messages + #13#10 + '是否继续安装？',
                mbConfirmation, MB_YESNO) = IDNO then
        Result := '用户取消安装。';
    end;
  end;
end;
