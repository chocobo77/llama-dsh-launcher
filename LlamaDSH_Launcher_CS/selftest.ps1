<#
.SYNOPSIS
    Windows 侧安装方式 / 进程血统的回归测试(与 LlamaDSH_Launcher_Linux\selftest.py 对称)。

.DESCRIPTION
    覆盖的是"改动最容易碰坏、且坏了表现为界面/启动异常"的两块逻辑:
      1) Services\InstallScope.cs  —— 这份程序是「仅为自己安装」还是「为所有用户安装」的三级判定
         (标记文件 → 卸载注册表 → 兜底) 与"对面那份安装"的检测;
      2) Services\LineageGuard.cs  —— 进程血统自重启的白名单判定 + 一次封顶
         (2026-11-10「仅为自己安装」装完无限反复启动的事故就在这一条链上)。
    外加一组静态不变量检查: 安装包与 C# 两侧的 AppId/GUID 必须一致、安装方式相关指令必须在位、
    已修掉的错误写法不得复活。

    隔离做法(不改本机真实安装): 测试用程序里的卸载注册表子键 GUID 被替换成测试专用 GUID,
    真实 GUID 的一致性改由静态检查(§C)保证。HKLM 用例需要管理员, 无权限时记 SKIP 而不是失败。

    两个必须记住的兼容性事实(2026-11-11 实测):
      · 本文件要**保持 UTF-8 with BOM**。Windows PowerShell 5.1 没有 BOM 就按 ANSI 读, 中文注释
        会把引号啃掉, 报错点在很后面, 看起来像语法错(pwsh 7 不受影响, 所以本地很容易漏)。
      · 5.1 的 Start-Process -ArgumentList 带 [ValidateNotNullOrEmpty], **空串元素会被拒**
        (pwsh 7 不拒), 所以期望"空值"的用例一律传 <none> 占位。

.EXAMPLE
    pwsh -File LlamaDSH_Launcher_CS\selftest.ps1
#>
[CmdletBinding()]
param(
    [string]$BaseDir = $env:TEMP
)

$ErrorActionPreference = 'Stop'
$csRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$pass = 0; $fail = 0; $skip = 0
$failures = @()

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; "PASS  $name" }
    else {
        $script:fail++; $script:failures += $name
        "FAIL  $name" + $(if ($detail) { "   => $detail" } else { '' })
    }
}
function Skip([string]$name, [string]$why) { $script:skip++; "SKIP  $name   ($why)" }

# ---------------------------------------------------------------- 0. 环境
"== Windows 安装方式 / 血统 回归测试 =="
"被测源: $csRoot"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Skip '全部用例' '本机没有 dotnet, 无法编译被测源'; ""; "PASS=$pass FAIL=$fail SKIP=$skip"; exit 0 }

$guidRe = '\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}'
$realGuid = (Select-String -Path (Join-Path $csRoot 'installer.iss') -Pattern 'AppId=(\{\{[0-9A-Fa-f-]{36}\})' |
             Select-Object -First 1).Matches[0].Groups[1].Value.Substring(1)   # 去掉 Inno 的 "{{" 转义
$testGuid = '{D1B2C3A4-AAAA-4BBB-8CCC-DDDDEEEEEEEE}'
# 本机真实安装(如果有的话)在测试前后必须一模一样
$realUninstKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$realGuid`_is1"
$realKeyBefore = Test-Path $realUninstKey

$work = Join-Path $BaseDir ("scopeself_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Cleanup {
    foreach ($root in 'HKCU:', 'HKLM:') {
        $k = "$root\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$testGuid`_is1"
        Remove-Item -Path $k -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
foreach ($root in 'HKCU:', 'HKLM:') {
    Remove-Item -Path "$root\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$testGuid`_is1" -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- 1. 被测程序
# InstallScope 的判定在静态构造里跑一次, 所以每个用例都必须换一个新进程; 断言写在被测进程里,
# 外层只比对退出码, 避免把中文断言文本经控制台转码后再比对。
$srcScope = [IO.File]::ReadAllText((Join-Path $csRoot 'Services\InstallScope.cs'))
if ($srcScope -notmatch [regex]::Escape($realGuid)) {
    Check 'InstallScope.cs 里的卸载子键 GUID 与 installer.iss 的 AppId 一致' $false "在 InstallScope.cs 里找不到 $realGuid"
    Cleanup; ""; "PASS=$pass FAIL=$fail SKIP=$skip"; exit 1
}
# 替换成测试专用 GUID: 测试绝不往真实卸载键写东西
$srcScope = $srcScope.Replace($realGuid, $testGuid)
$srcScope | Set-Content -Path (Join-Path $work 'InstallScope.cs') -Encoding UTF8
Copy-Item (Join-Path $csRoot 'Services\LineageGuard.cs') (Join-Path $work 'LineageGuard.cs')

@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>scopeself</AssemblyName>
    <NoWarn>CS0618</NoWarn>
  </PropertyGroup>
</Project>
'@ | Set-Content -Path (Join-Path $work 'scopeself.csproj') -Encoding UTF8

@'
using System.Security.Cryptography;
using LlamaDSH_Launcher.Services;

// 被测进程: 既能按外层给的期望值检查 InstallScope(每次一个进程), 也在同一进程里跑
// LineageGuard 的纯函数用例(无状态, 不需要换进程)。
class T
{
    static int Bad = 0;

    static void Chk(string name, bool ok, string got)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + " => " + got);
        if (!ok) Bad++;
    }

    static int Main(string[] argv)
    {
        var o = new Dictionary<string, string>();
        for (int i = 0; i + 1 < argv.Length; i += 2) o[argv[i]] = argv[i + 1];

        if (o.ContainsKey("--scope"))
        {
            Chk("InstallScope.Scope", InstallScope.Scope == o["--scope"], InstallScope.Scope);
            if (o.ContainsKey("--by"))
                Chk("InstallScope.DecidedBy", InstallScope.DecidedBy == o["--by"], InstallScope.DecidedBy);
            if (o.ContainsKey("--other"))
            {
                // "<none>" 是空串的占位: Windows PowerShell 5.1 的 Start-Process -ArgumentList
                // 带 [ValidateNotNullOrEmpty], 空串元素会被拒(pwsh 7 不拒 —— 差异要写明白)。
                string wantOther = o["--other"] == "<none>" ? "" : o["--other"];
                Chk("InstallScope.OtherScopeRoot",
                    string.Equals(InstallScope.OtherScopeRoot.TrimEnd('\\'), wantOther.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
                    "\"" + InstallScope.OtherScopeRoot + "\"");
            }
            if (o.ContainsKey("--user-scoped"))
                Chk("InstallScope.IsUserScoped",
                    InstallScope.IsUserScoped == (o["--user-scoped"] == "1"),
                    InstallScope.IsUserScoped.ToString());
        }

        if (o.ContainsKey("--lineage"))
        {
            string mk = Path.Combine(Path.GetTempPath(), "scopeself_lineage_" + Guid.NewGuid().ToString("N") + ".marker");

            // 白名单: RunAsInvoker 不许触发(旧写法"非空即重启"正是用户级安装每次都自启的根因)
            Chk("compat=null,elev=false -> NoReset",
                LineageGuard.Decide(false, null, mk) == LineageGuard.Decision.NoResetNeeded, "NoResetNeeded");
            Chk("compat=RunAsInvoker -> NoReset",
                LineageGuard.Decide(false, "RunAsInvoker", mk) == LineageGuard.Decision.NoResetNeeded,
                LineageGuard.Decide(false, "RunAsInvoker", mk).ToString());
            Chk("compat=ElevateCreateProcess -> Relaunch",
                LineageGuard.Decide(false, "ElevateCreateProcess", mk) == LineageGuard.Decision.Relaunch,
                LineageGuard.Decide(false, "ElevateCreateProcess", mk).ToString());
            Chk("compat=InstallMonitorWritableFiles -> Relaunch",
                LineageGuard.Decide(false, "InstallMonitorWritableFiles", mk) == LineageGuard.Decision.Relaunch,
                LineageGuard.Decide(false, "InstallMonitorWritableFiles", mk).ToString());
            Chk("elev=true -> Relaunch",
                LineageGuard.Decide(true, null, mk) == LineageGuard.Decision.Relaunch,
                LineageGuard.Decide(true, null, mk).ToString());

            // 一次封顶: 落凭证后必须判成 CappedAlreadyReset(缺这条就是无限反复启动)
            LineageGuard.MarkRelaunchAttempted(mk);
            Chk("mark 后 -> CappedAlreadyReset",
                LineageGuard.Decide(true, "ElevateCreateProcess", mk) == LineageGuard.Decision.CappedAlreadyReset,
                LineageGuard.Decide(true, "ElevateCreateProcess", mk).ToString());

            // 过期凭证必须重新放行, 且时间基准必须一致(Now 配 FileTimeUtc 会差出整个时区)
            File.WriteAllText(mk, DateTime.UtcNow.AddSeconds(-(LineageGuard.MarkerTtlSeconds * 3)).ToFileTimeUtc().ToString());
            Chk("过期 mark -> 重新 Relaunch",
                LineageGuard.Decide(true, null, mk) == LineageGuard.Decision.Relaunch,
                LineageGuard.Decide(true, null, mk).ToString());

            // 刚写的凭证必须算数(TTL 内)
            File.WriteAllText(mk, DateTime.UtcNow.ToFileTimeUtc().ToString());
            Chk("新鲜 mark(UTC 写) -> Capped",
                LineageGuard.Decide(true, null, mk) == LineageGuard.Decision.CappedAlreadyReset,
                LineageGuard.Decide(true, null, mk).ToString());

            // 内容不认识 / 文件不可读: 一律按"没有凭证"处理, 且不得抛
            File.WriteAllText(mk, "not-a-timestamp");
            Chk("垃圾 mark -> Relaunch 且不抛",
                LineageGuard.Decide(true, null, mk) == LineageGuard.Decision.Relaunch,
                LineageGuard.Decide(true, null, mk).ToString());
            Chk("mark 路径不可读 -> 不抛",
                LineageGuard.ResetAlreadyAttempted(@"Z:\nope\missing.marker") == false, "false");

            try { File.Delete(mk); } catch { }
        }

        return Bad == 0 ? 0 : 1;
    }
}
'@ | Set-Content -Path (Join-Path $work 'Program.cs') -Encoding UTF8

Push-Location $work
$build = (& dotnet build -c Release -nologo --verbosity quiet 2>&1 | Out-String)
$exe = Get-ChildItem -Recurse (Join-Path $work 'bin') -Filter scopeself.exe -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $exe) {
    Check '被测程序能编译' $false ($build -split "`r?`n" | Where-Object { $_ -match 'error' } | Select-Object -First 3) -join ' '
    Pop-Location; Cleanup; ""; "PASS=$pass FAIL=$fail SKIP=$skip"; exit 1
}
Check '被测程序能编译(含 InstallScope.cs / LineageGuard.cs)' $true

function RunExe([string[]]$a) {
    $p = Start-Process -FilePath $exe.FullName -ArgumentList $a -Wait -PassThru -NoNewWindow `
         -RedirectStandardOutput "$work\out.txt" -RedirectStandardError "$work\err.txt"
    $text = if (Test-Path "$work\out.txt") { Get-Content "$work\out.txt" -Encoding UTF8 } else { @() }
    return @{ code = $p.ExitCode; text = $text }
}
function RunCase([string]$name, [string[]]$a) {
    $r = RunExe $a
    Check $name ($r.code -eq 0) ($r.text -join ' | ')
}

$exeDir = Split-Path -Parent $exe.FullName
$markerNext = Join-Path $exeDir 'install-scope.txt'
$hkcu = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$testGuid`_is1"
$hklm = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$testGuid`_is1"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

""
"== A. InstallScope 三级判定 =="
# 1) 什么都没有 -> 兜底按 machine, 且不谎报对面那份(--other 用 <none> 占位: 5.1 的 -ArgumentList 拒空串元素)
Remove-Item $markerNext -Force -ErrorAction SilentlyContinue
RunCase '无标记+无注册表 -> 兜底 machine' @('--scope','machine','--by','兜底(无标记且注册表里认不出这份)','--other','<none>')

# 2) 标记文件 scope=user
Set-Content -Path $markerNext -Value "scope=user`r`ndir=$exeDir`r`n" -Encoding ASCII
RunCase '标记 scope=user -> user/标记文件' @('--scope','user','--by','标记文件','--user-scoped','1')

# 3) 标记值不认识: 不许瞎猜, 退回下一级
Set-Content -Path $markerNext -Value "scope=bogus`r`n" -Encoding ASCII
RunCase '标记 scope=bogus -> 退回下一级(兜底)' @('--scope','machine','--by','兜底(无标记且注册表里认不出这份)')

# 4) 标记里没有 scope 行
Set-Content -Path $markerNext -Value "dir=$exeDir`r`n" -Encoding ASCII
RunCase '标记缺 scope 行 -> 兜底' @('--scope','machine','--by','兜底(无标记且注册表里认不出这份)')
Remove-Item $markerNext -Force -ErrorAction SilentlyContinue

# 5) HKCU 卸载项 InstallLocation 正好是这份 -> 判 user
New-Item -Path $hkcu -Force | Out-Null
Set-ItemProperty -Path $hkcu -Name InstallLocation -Value $exeDir
Set-ItemProperty -Path $hkcu -Name DisplayName -Value 'selftest 测试用, 可删'
RunCase 'HKCU 卸载项匹配 -> user/卸载注册表' @('--scope','user','--by','卸载注册表','--user-scoped','1')

# 6) HKCU 卸载项指向别处 -> 不能认成这份(残留旧键不得误导)
Set-ItemProperty -Path $hkcu -Name InstallLocation -Value (Join-Path $env:TEMP 'not_this_install')
RunCase 'HKCU 卸载项指向别处 -> 不认这份(兜底)' @('--scope','machine','--by','兜底(无标记且注册表里认不出这份)')
Remove-Item $hkcu -Recurse -Force -ErrorAction SilentlyContinue

if ($isAdmin) {
    New-Item -Path $hklm -Force | Out-Null
    Set-ItemProperty -Path $hklm -Name InstallLocation -Value $exeDir
    RunCase 'HKLM 卸载项匹配 -> machine/卸载注册表' @('--scope','machine','--by','卸载注册表')

    # 7) 对面那份: 机器级这份 + HKCU 还有一份 -> OtherScopeRoot 必须指出 HKCU 的目录
    $otherDir = Join-Path $env:TEMP 'other_scope_install'
    New-Item -ItemType Directory -Force -Path $otherDir | Out-Null
    New-Item -Path $hkcu -Force | Out-Null
    Set-ItemProperty -Path $hkcu -Name InstallLocation -Value $otherDir
    RunCase '机器级 + HKCU 另一份 -> 指出对面目录' @('--scope','machine','--by','卸载注册表','--other',$otherDir)
    Remove-Item $hkcu -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $otherDir -ErrorAction SilentlyContinue
} else {
    Skip 'HKLM 卸载项匹配 -> machine/卸载注册表' '需要管理员权限'
    Skip '机器级 + HKCU 另一份 -> 指出对面目录' '需要管理员权限'
}

""
"== B. LineageGuard 血统自重启判定 =="
RunCase '白名单 + 一次封顶 + 时间基准' @('--lineage','1')

""
"== C. 安装包 / 源码静态不变量 =="
$iss = [IO.File]::ReadAllText((Join-Path $csRoot 'installer.iss'))
$scopeSrc = [IO.File]::ReadAllText((Join-Path $csRoot 'Services\InstallScope.cs'))
$appSrc = [IO.File]::ReadAllText((Join-Path $csRoot 'App.xaml.cs'))

# C1 三处 AppId/GUID 必须一致(改了安装包忘了改 C# 的话, 跨安装方式检测就瞎了)
$codeGuidMatch = [regex]::Matches($iss, "UNINST_SUBKEY = 'Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\($guidRe)_is1'")
$scopeGuidMatch = [regex]::Matches($scopeSrc, "UninstallSubKey = @`"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\($guidRe)_is1`"")
Check 'installer.iss 里 [Code] UNINST_SUBKEY 能找到' ($codeGuidMatch.Count -ge 1)
Check 'InstallScope.cs 里 UninstallSubKey 能找到' ($scopeGuidMatch.Count -ge 1)
if ($codeGuidMatch.Count -ge 1 -and $scopeGuidMatch.Count -ge 1) {
    Check 'AppId / [Code] / C# 三处 GUID 一致' `
        ($realGuid -eq $codeGuidMatch[0].Groups[1].Value -and $realGuid -eq $scopeGuidMatch[0].Groups[1].Value) `
        "AppId=$realGuid [Code]=$($codeGuidMatch[0].Groups[1].Value) C#=$($scopeGuidMatch[0].Groups[1].Value)"
}

# C2 安装方式相关指令必须在位
Check 'PrivilegesRequired=lowest' ($iss -match '(?m)^PrivilegesRequired=lowest')
Check 'PrivilegesRequiredOverridesAllowed=dialog commandline' ($iss -match '(?m)^PrivilegesRequiredOverridesAllowed=dialog commandline')
Check 'DefaultDirName 走 {code:PickInstallDir}' ($iss -match '(?m)^DefaultDirName=\{code:PickInstallDir\}')
Check '用户级默认用 {userpf}' ($iss -match '\{userpf\}')
Check '机器级默认优先 D:\Program Files' ($iss.Contains("DATA_DRIVE_ROOT = 'D:\';"))   # 字面比较: PS 里反斜杠不是转义符, 用正则易错
Check 'D 盘只拦"不存在/非固定盘"而不拦空间不足' ($iss -match 'GetDriveTypeW2\(DriveRoot\) = DRIVE_FIXED')
Check '安装方式页中英文文案都已覆盖' (($iss -match 'chinesesimplified\.PrivilegesRequiredOverrideText2') -and ($iss -match 'english\.PrivilegesRequiredOverrideText2'))
Check '跨安装方式提醒在静默下不弹窗' ($iss -match 'if WizardSilent then')
Check '提醒的按钮与文案自洽(Yes/No 而非 MB_OK)' ($iss -match 'mbConfirmation, MB_YESNO')
Check '装完自动启动不带 Setup 血统(runasoriginaluser)' ($iss -match 'postinstall skipifsilent runasoriginaluser')

# C3 已修掉的错误写法不得复活
Check 'App.xaml.cs 不再"非空即重启"' ($appSrc -notmatch '!string\.IsNullOrEmpty\(compatLayer\)')
Check 'App.xaml.cs 的 explorer 重启不带附加参数' ($appSrc -match 'Arguments = "\\""\+ exePath \+ "\\""' -or $appSrc -match 'Arguments = "\\""\s*\+\s*exePath\s*\+\s"\\""')
Check '血统判定已抽到 LineageGuard' ($appSrc -match 'Services\.LineageGuard\.Decide')
Check 'InstallScope 读注册表要求 InstallLocation 匹配当前目录' ($scopeSrc -match 'UninstallKeyMatchesRoot')

""
# 先离开临时目录再删: 当前目录正指着它时 Windows 不允许删除
Pop-Location
Cleanup
# 收尾自查: 测试用的注册表键与临时工程必须清干净(测试自己不该留下痕迹)
Check '测试痕迹已清除(注册表键 + 临时目录)' `
    (-not (Test-Path $hkcu) -and -not (Test-Path $hklm) -and -not (Test-Path $work))
Check '本机真实安装未被触碰' ((Test-Path $realUninstKey) -eq $realKeyBefore) "测试前存在=$realKeyBefore 测试后存在=$(Test-Path $realUninstKey)"
"== 汇总: PASS=$pass FAIL=$fail SKIP=$skip =="
# ASCII 哨兵行: 一键封包.cmd 用 findstr 抓它。中文标记在 GBK 管道里会被 findstr 错过
# (批处理文件是 UTF-8, PowerShell 输出走控制台代码页), 所以匹配标记必须是纯 ASCII。
"SUMMARY PASS=$pass FAIL=$fail SKIP=$skip"
if ($fail -gt 0) { "失败项: " + ($failures -join '; '); exit 1 }
exit 0
