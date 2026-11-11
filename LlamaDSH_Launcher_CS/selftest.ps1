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
    [string]$BaseDir = $env:TEMP,
    # 编译超时(秒): 构建环境坏了(NuGet 不可达/SDK 缺件套)时不该让打包一直挂着
    [int]$BuildTimeoutSec = 180
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
# 卫生类问题(临时目录删不掉等)只警告不判失败: 真实用例全过时, 不该因为残留目录中止打包。
# 但注册表残留仍算 FAIL —— 那会留在用户的「卸载或更改程序」列表里。
function Warn([string]$name, [string]$detail = '') { "WARN  $name" + $(if ($detail) { "   => $detail" } else { '' }) }

# 中途抛终止性错误也必须清场: 测试用的卸载注册表键留在用户机器上比测试失败本身更糟。
trap {
    $e = $_
    Write-Output "!! 测试脚本异常中止: $($e.GetType().Name): $($e.Message)"
    try { Cleanup } catch { }
    Write-Output "SUMMARY PASS=$pass FAIL=$($fail + 1) SKIP=$skip"
    exit 1
}

# ---------------------------------------------------------------- 0. 环境
"== Windows 安装方式 / 血统 回归测试 =="
"被测源: $csRoot"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { Skip '全部用例' '本机没有 dotnet, 无法编译被测源'; ""; "PASS=$pass FAIL=$fail SKIP=$skip"; exit 0 }

$guidRe = '\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}'
# AppId 取不到就必须明确报错退出: 以前直接 .Matches[0] 会在 null 上调方法, 现场只看到一条
# PowerShell 空值栈, 分不清"测试失败"还是"脚本自己崩了"(打包那边也只会看到非零退出码)。
$appIdMatch = Select-String -Path (Join-Path $csRoot 'installer.iss') -Pattern 'AppId=(\{\{[0-9A-Fa-f-]{36}\})' | Select-Object -First 1
if (-not $appIdMatch) {
    "FAIL  无法从 installer.iss 解析 AppId(格式应为 AppId={{<GUID>})"
    "SUMMARY PASS=0 FAIL=1 SKIP=0"
    exit 1
}
$realGuid = $appIdMatch.Matches[0].Groups[1].Value.Substring(1)   # 去掉 Inno 的 "{{" 转义
$testGuid = '{D1B2C3A4-AAAA-4BBB-8CCC-DDDDEEEEEEEE}'
# 本机真实安装(如果有的话)在测试前后必须一模一样
$realUninstKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$realGuid`_is1"
$realKeyBefore = Test-Path $realUninstKey

$work = Join-Path $BaseDir ("scopeself_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Remove-WithRetry([string]$path) {
    # 刚结束的被测进程/杀软扫描常常还压着 bin 下的句柄, 一次删不掉是常态(本机 %TEMP% 下
    # 攒着 4 个上一轮的中止残留就是这么来的)。重试几次, 还不行就报告, 不谎报"已清干净"。
    for ($i = 0; $i -lt 4; $i++) {
        Remove-Item -Recurse -Force $path -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $path)) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return -not (Test-Path -LiteralPath $path)
}
$script:workGone = $true
function Cleanup {
    foreach ($root in 'HKCU:', 'HKLM:') {
        $k = "$root\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$testGuid`_is1"
        Remove-Item -Path $k -Recurse -Force -ErrorAction SilentlyContinue
    }
    $script:workGone = Remove-WithRetry $work
}
foreach ($root in 'HKCU:', 'HKLM:') {
    Remove-Item -Path "$root\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$testGuid`_is1" -Recurse -Force -ErrorAction SilentlyContinue
}
# 上一轮异常中止留下的工作目录顺手清掉(只是 %TEMP% 里的 bin/obj, 不放行也没人收)
Get-ChildItem -Path $BaseDir -Directory -Filter 'scopeself_*' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($_.FullName -ne $work) { Remove-Item -Recurse -Force $_.FullName -ErrorAction SilentlyContinue }
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

        if (o.ContainsKey("--safe"))
        {
            // 界面上"这份 exe 可不可信 / 提权要不要核对路径"必须由**实测的目录可写性**决定,
            // 不能由安装方式推断: 三级判定认不出来会兜底成 machine, 按安装方式推断就会在
            // 用户可写的目录(比如这个临时 bin 目录)上宣称"exe 被 ACL 保护"。
            Chk("InstallDirWritable 探测给出结论(非 null)", InstallScope.InstallDirWritable != null, InstallScope.WritabilityLabel);
            Chk("临时 bin 目录可写 -> ElevationNote 说「当前用户可改写」",
                InstallScope.ElevationNote.Contains("当前用户可改写"),
                InstallScope.ElevationNote.Replace("\n", " / "));
            Chk("ElevationNote 不说「ACL 保护」这种按安装方式推断的话",
                !InstallScope.ElevationNote.Contains("通常由系统 ACL 保护"), "-");
            Chk("UacFact 非空(提权静默/弹框要有依据)", !string.IsNullOrEmpty(InstallScope.UacFact), InstallScope.UacFact);
            Chk("Summary 带上实测的可写性与 UAC 事实",
                InstallScope.Summary.Contains("目录可写=") && InstallScope.Summary.Contains("UAC="), InstallScope.Summary);
        }

        if (o.ContainsKey("--marker-note"))
        {
            Chk("被忽略的标记文件会说明原因", InstallScope.MarkerNote.Contains(o["--marker-note"]), InstallScope.MarkerNote);
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
            string reason;
            Chk("TryMark(可写路径) -> true 且封顶生效",
                LineageGuard.TryMarkRelaunchAttempted(mk, out reason)
                && LineageGuard.Decide(true, "ElevateCreateProcess", mk) == LineageGuard.Decision.CappedAlreadyReset,
                "reason=" + reason);
            // 封顶是"判定持续成立"那类机器上唯一的下限: 凭证写不出来就必须返回 false,
            // 由调用方放弃重启(fail-closed)。返回 true 就等于把无限反复启动放回去。
            Chk("TryMark 写不出来(建不出目录) -> false",
                !LineageGuard.TryMarkRelaunchAttempted(@"Z:\llamadsh-nope\x.marker", out reason), "reason=" + reason);
            // 被封顶放过的那次启动会主动清凭证: 留着只靠 TTL 过期, 会把窗口内下一次真需要
            // 切血统的启动也压掉(= 当初 WMI 必失败的症状回来)
            LineageGuard.ClearRelaunchMarker(mk);
            Chk("ClearRelaunchMarker 后 -> 重新 Relaunch(不靠 TTL 到点)",
                LineageGuard.Decide(true, "ElevateCreateProcess", mk) == LineageGuard.Decision.Relaunch,
                LineageGuard.Decide(true, "ElevateCreateProcess", mk).ToString());
            LineageGuard.TryMarkRelaunchAttempted(mk, out reason);
            Chk("TryMark 写完立刻 Decide -> Capped(回读校验成立)",
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

            // marker 的默认位置必须是当前用户独占的目录。不能图省事放 %TEMP%: 本机实测 TEMP 被
            // 重定向到 D:\Temp 且 ACL 给「Authenticated Users: Modify」—— 多用户共享可写 +
            // 文件名固定可预测 = 别人能预置它压制别人的血统重置, 或做成符号链接让提权实例写到别处。
            Chk("marker 默认落在 %LOCALAPPDATA%\\LlamaDSH_Launcher",
                LineageGuard.MarkerDirectory == Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LlamaDSH_Launcher"),
                LineageGuard.MarkerDirectory);
            Chk("marker 默认路径不在 %TEMP% 下",
                !LineageGuard.MarkerPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
                LineageGuard.MarkerPath);
            Chk("TTL 不超过 60s(窗口越长越容易压掉真正需要的重置)",
                LineageGuard.MarkerTtlSeconds > 0 && LineageGuard.MarkerTtlSeconds <= 60,
                LineageGuard.MarkerTtlSeconds + "s");

            try { File.Delete(mk); } catch { }
        }

        return Bad == 0 ? 0 : 1;
    }
}
'@ | Set-Content -Path (Join-Path $work 'Program.cs') -Encoding UTF8

Push-Location $work
# 编译用 Start-Process + WaitForExit(ms) 跑，而不是直接 & dotnet：
#  ① /nodeReuse:false + UseSharedCompilation=false 不给 dotnet 留 MSBuild/Roslyn 构建服务器进程。
#     不加的话这些进程会继承被重定向的输出句柄，上层(打包脚本的重定向 / CI 管道)要等它们到
#     idle 超时才拿到 EOF —— 本机实测同一条命令因此从 ~5s 变成 603s，打包看起来像卡在 [6/8]。
#  ② WaitForExit(超时) 让"构建环境出问题"变成一条明确的失败，而不是让整条打包链无限挂着。
$bOut = Join-Path $work 'build_out.txt'
$bErr = Join-Path $work 'build_err.txt'
$bp = Start-Process dotnet -PassThru -NoNewWindow -WorkingDirectory $work `
      -RedirectStandardOutput $bOut -RedirectStandardError $bErr `
      -ArgumentList @('build','-c','Release','-nologo','--verbosity','quiet','/nodeReuse:false','/p:UseSharedCompilation=false')
if (-not $bp.WaitForExit($BuildTimeoutSec * 1000)) {
    try { $bp.Kill(); } catch { }
    Check "dotnet build 在 $BuildTimeoutSec 秒内没结束(构建环境异常, 不是被测代码的问题)" $false "work=$work"
    Pop-Location; Cleanup; ""; "SUMMARY PASS=$pass FAIL=$fail SKIP=$skip"; exit 1
}
$build = ((Get-Content $bOut -Raw -ErrorAction SilentlyContinue) + "`n" +
          (Get-Content $bErr -Raw -ErrorAction SilentlyContinue))
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

# 4b) 标记 scope=user 但 dir= 指向别处(整个安装目录被复制到别处时标记会跟着跑):
#     照单全收就会拿"另一处装的"方式给现在这份定性, 而用户级/机器级的安全含义正好相反。
Set-Content -Path $markerNext -Value "scope=user`r`ndir=C:\llamadsh_definitely_not_here`r`n" -Encoding ASCII
RunCase '标记 dir= 与当前目录不符 -> 不采用标记(走兜底)并说明原因' @(
    '--scope','machine','--by','兜底(无标记且注册表里认不出这份)','--marker-note','与当前目录不符')
Remove-Item $markerNext -Force -ErrorAction SilentlyContinue

# 4c) 界面上的安全语气必须由实测的目录可写性决定(这里 exe 在临时目录里, 当前用户可写),
#     不能因为"三级判定兜底成 machine"就宣称 exe 被 ACL 保护。
RunCase '安全语气来自实测目录可写性 + 本机 UAC 事实' @('--safe','1')

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

# C2.5 静默安装与安装方式文案
Check '静默模式有统一的提问规则(信息性询问跳过, 不弹窗等人)' (($iss -match 'function AskYesNoOrAssume') -and ($iss -match 'AskYesNoOrAssume\('))
Check '环境检查的信息性 MsgBox 不再在静默下弹' (-not ($iss -match "MsgBox\('环境检查结果"))
Check '缺 .NET 时静默走明确失败而不是挂住' ($iss -match 'if WizardSilent then[\s\S]{0,400}静默安装不会自动下载安装它')
Check '「仅为自己安装」文案说清了对 .NET 运行时的前提(中英文都改)' `
    (($iss -match '要求本机已有 .NET 8 运行时') -and ($iss -match 'needs \.NET 8 runtime already present'))
Check '[Run] 注释不再宣称 runasoriginaluser 挡兼容层血统' `
    (-not ($iss -match '不继承 Setup 的提权状态与'))

# C3 已修掉的错误写法不得复活
Check 'App.xaml.cs 不再"非空即重启"' ($appSrc -notmatch '!string\.IsNullOrEmpty\(compatLayer\)')
Check 'App.xaml.cs 的 explorer 重启不带附加参数' ($appSrc -match 'Arguments = "\\""\+ exePath \+ "\\""' -or $appSrc -match 'Arguments = "\\""\s*\+\s*exePath\s*\+\s"\\""')
Check '血统判定已抽到 LineageGuard' ($appSrc -match 'Services\.LineageGuard\.Decide')
Check 'InstallScope 读注册表要求 InstallLocation 匹配当前目录' ($scopeSrc -match 'UninstallKeyMatchesRoot')

# C4 本次修复引入的不变量(哪条被改回去都会让对应缺陷复活, 而且现场看不出来)
$lineageSrc = [IO.File]::ReadAllText((Join-Path $csRoot 'Services\LineageGuard.cs'))
$mwSrc = [IO.File]::ReadAllText((Join-Path $csRoot 'MainWindow.xaml.cs'))
$xamlSrc = [IO.File]::ReadAllText((Join-Path $csRoot 'MainWindow.xaml'))
$apiSrc = [IO.File]::ReadAllText((Join-Path $csRoot 'Services\AgentApi.cs'))
$nodeSrc = [IO.File]::ReadAllText((Join-Path $csRoot 'Services\NodeSetup.cs'))

# 封顶是"判定持续成立"那类机器上唯一的下限: 凭证落不下去就必须放弃重启
Check 'App.xaml.cs 用 TryMarkRelaunchAttempted 而不是写失败也照重启' `
    (($appSrc -match 'TryMarkRelaunchAttempted') -and ($appSrc -notmatch '(?<!Try)MarkRelaunchAttempted\('))
Check '凭证用完会被主动清掉(不靠 TTL 到点)' ($lineageSrc -match 'ClearRelaunchMarker')
Check 'marker 默认位置用 LOCALAPPDATA 而非共享可写的 TEMP' `
    (($lineageSrc -match 'LocalApplicationData') -and ($appSrc -notmatch 'Path\.GetTempPath'))
# 界面那句"这份 exe 可不可信"必须由实测目录可写性决定; 按安装方式推断会在兜底时谎报"被 ACL 保护"
Check 'InstallScope 的安全语气由实测可写性推导' ($scopeSrc -match 'InstallDirWritable == true')
Check 'InstallScope 不再写"通常由系统 ACL 保护"这种推断话术' ($scopeSrc -notmatch '通常由系统 ACL 保护')
Check 'UI 不再断言"用户级安装：会弹 UAC 确认"(依机器不成立)' ($mwSrc -notmatch '会弹 UAC 确认')
Check '提权路径的提示带上本机 UAC 事实(而非猜)' (($mwSrc -match 'InstallScope\.UacFact') -and ($nodeSrc -match 'InstallScope\.UacFact'))
# agent 侧: 下发过启动 ≠ 拉起了进程(§1.2)，类型错误两端都是 400(§1)
Check 'agent 启动端点回三态而不是"已下发"一律成功' `
    (($mwSrc -match 'AgentStartOutcome\(') -and ($mwSrc -notmatch '启动指令已下发'))
Check 'StartLlama/StartDsh 进入即置 start_failed(悲观默认)' `
    (($mwSrc -match '_llamaStartOutcome = "start_failed"') -and ($mwSrc -match '_dshStartOutcome = "start_failed"'))
Check '请求体类型错误回 400(与 Linux _body_shape_error 同口径)' `
    (($apiSrc -match 'AgentBadRequestException') -and ($apiSrc -match 'Send\(ctx, 400') -and ($mwSrc -match 'AgentErr400\('))
Check '并发停止不再互相覆盖停止回执' `
    (($mwSrc -match 'ConcurrentQueue<TaskCompletionSource<bool>>') -and ($mwSrc -match 'SettleStopWaiters'))
# 历史日志整文件重写必须按字节: ReadAllText→WriteAllText 的文本往返会把非法 UTF-8 字节
# 换成 U+FFFD(不可恢复的内容损失), Linux 侧 applog.scrub_existing 同一个修法
$logSrc = [IO.File]::ReadAllText((Join-Path $csRoot 'Logging.cs'))
Check 'Logging.cs 的历史日志脱敏按字节重写(RedactBytes, 不用 ReadAllText 往返)' `
    (($logSrc -match 'RedactBytes') -and ($logSrc -notmatch 'string text = File\.ReadAllText\(path\)'))

""
# 先离开临时目录再删: 当前目录正指着它时 Windows 不允许删除
Pop-Location
Cleanup
# 收尾自查: 测试用的注册表键必须清干净(留在用户的「卸载或更改程序」列表里比测试失败更糟);
# 临时工程删不掉只记 WARN —— 真实用例全过时不该因为 %TEMP% 里多一个目录就中止打包。
Check '测试用的注册表键已清除' (-not (Test-Path $hkcu) -and -not (Test-Path $hklm))
if (-not $workGone) { Warn '临时工程目录没能删掉(下次运行会顺手清)' $work }
Check '本机真实安装未被触碰' ((Test-Path $realUninstKey) -eq $realKeyBefore) "测试前存在=$realKeyBefore 测试后存在=$(Test-Path $realUninstKey)"
"== 汇总: PASS=$pass FAIL=$fail SKIP=$skip =="
# ASCII 哨兵行: 一键封包.cmd 用 findstr 抓它。中文标记在 GBK 管道里会被 findstr 错过
# (批处理文件是 UTF-8, PowerShell 输出走控制台代码页), 所以匹配标记必须是纯 ASCII。
"SUMMARY PASS=$pass FAIL=$fail SKIP=$skip"
if ($fail -gt 0) { "失败项: " + ($failures -join '; '); exit 1 }
exit 0
