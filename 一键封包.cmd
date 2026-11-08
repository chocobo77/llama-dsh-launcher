@echo off
chcp 65001 >nul
setlocal EnableExtensions
title LlamaDSH 启动器 · 一键封包

set "ROOT=%~dp0"
set "PROJ=%ROOT%LlamaDSH_Launcher_CS"
set "CSProj=%PROJ%\LlamaDSH_Launcher.csproj"
set "ISS=%PROJ%\installer.iss"
set "PUBDIR=%PROJ%\bin\Release\net8.0-windows\win-x64\publish"
set "PKOUT=%PROJ%\installer_output"
set "STAGE=%PROJ%\installer_staging"
set "LNXSRC=%ROOT%LlamaDSH_Launcher_Linux"
set "LNXSTG=%PROJ%\installer_staging_linux"
set "SETUPEXE=%PKOUT%\LlamaDSH_Launcher_Setup.exe"
set "RUNLOG=%ROOT%pack_run.log"

rem 每步写黑匣子日志: 闪退/中断后凭 pack_run.log 定位断点
>>"%RUNLOG%" echo ==== [%date% %time%] 开始 机器=%COMPUTERNAME% 用户=%USERNAME% 文件=%~f0
if exist "%PKOUT%" (
    >>"%RUNLOG%" echo [%date% %time%] 启动时 OUTDIR 已有内容:
    dir /b "%PKOUT%" >>"%RUNLOG%" 2>nul
) else (
    >>"%RUNLOG%" echo [%date% %time%] 启动时 OUTDIR 不存在
)

rem -nopause（位置任意）或 set NOPAUSE=1：自动化/CI 下不挂住
set "NOPAUSE="
if /I "%~1"=="-nopause" set "NOPAUSE=1"
if /I "%~2"=="-nopause" set "NOPAUSE=1"

echo ==============================================
echo   LlamaDSH 启动器 · 一键封包
echo ==============================================

rem ---------- [1/7] 可选: 版本号参数 (一键封包.cmd 1.2.0 [-nopause]) ----------
set "VERARG=%~1"
if /I "%VERARG%"=="-nopause" set "VERARG="
if "%VERARG%"=="" (
    echo [1/7] 跳过（未给版本号参数，沿用 installer.iss 当前版本）
    goto :envcheck
)
echo [1/7] 更新版本号: %VERARG%
echo %VERARG%| findstr /R "^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul
if errorlevel 1 (
    echo   [错误] 版本号格式应为 x.y.z（例如 1.2.0）
    goto :fail
)
rem 只替换 MyAppVersion 那一行，保留原行尾（CRLF）与 BOM 状态
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
 "$p='%ISS%'; $b=[IO.File]::ReadAllBytes($p); $bom=($b.Length -ge 3 -and $b[0] -eq 239 -and $b[1] -eq 187 -and $b[2] -eq 191); $q=[char]34; $t=[IO.File]::ReadAllText($p); $t=[regex]::Replace($t,'(?m)^#define\s+MyAppVersion\s+[^\r\n]*','#define MyAppVersion '+$q+'%VERARG%'+$q); [IO.File]::WriteAllText($p,$t,(New-Object System.Text.UTF8Encoding($bom)))"
if errorlevel 1 goto :fail
findstr /C:"#define MyAppVersion" "%ISS%"
rem 同一版本号写进 csproj: exe 文件属性与 `cli status` 报告的版本必须和安装包一致
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
 "$p='%CSProj%'; $b=[IO.File]::ReadAllBytes($p); $bom=($b.Length -ge 3 -and $b[0] -eq 239 -and $b[1] -eq 187 -and $b[2] -eq 191); $t=[IO.File]::ReadAllText($p); $v='%VERARG%'; $t=[regex]::Replace($t,'(?m)^\s*<Version>[^\u003c]*</Version>','    <Version>'+$v+'</Version>'); $t=[regex]::Replace($t,'(?m)^\s*<AssemblyVersion>[^\u003c]*</AssemblyVersion>','    <AssemblyVersion>'+$v+'.0</AssemblyVersion>'); $t=[regex]::Replace($t,'(?m)^\s*<FileVersion>[^\u003c]*</FileVersion>','    <FileVersion>'+$v+'.0</FileVersion>'); [IO.File]::WriteAllText($p,$t,(New-Object System.Text.UTF8Encoding($bom)))"
if errorlevel 1 goto :fail
findstr /C:"<Version>" "%CSProj%"

:envcheck
echo [2/7] 检查构建环境...
call :log "2/7 环境检查"
where dotnet >nul 2>nul
if errorlevel 1 (
    echo   [错误] 未找到 dotnet CLI（需要 .NET 8 SDK）
    goto :fail
)
rem Inno Setup 自动定位：注册表(64/32 位 + 当前用户) → 常见安装路径。
rem 原版把 ISCC 写死为 "C:\Program Files (x86)\Inno Setup 6"，换个安装位置/别的用户直接卡在这一步。
call :find_iscc "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
call :find_iscc "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
call :find_iscc "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
for %%P in (
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    "C:\Program Files\Inno Setup 6\ISCC.exe"
    "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
) do if not defined ISCC if exist "%%~P" set "ISCC=%%~P"
if not defined ISCC (
    echo   [错误] 未找到 Inno Setup 6 的 ISCC.exe
    echo          请先安装 Inno Setup 6: https://jrsoftware.org/isdl.php
    echo          安装后重跑即可（本脚本自动从注册表定位，无需手改路径）
    goto :fail
)
if not exist "%CSProj%" (
    echo   [错误] 未找到工程: %CSProj%
    goto :fail
)
echo   dotnet 就绪
echo   ISCC: %ISCC%
rem ISCC 版本行见 [6/7] 生成的 iscc_build.log 第一行(跨机器排障先比对它)

rem 从 installer.iss 读取版本号, 用于 Linux 包命名 (解析失败回退 dev)
set "LNXVER=dev"
for /f "tokens=3" %%V in ('findstr /C:"#define MyAppVersion" "%ISS%" 2^>nul') do set "LNXVER=%%~V"
set "LNXVER=%LNXVER:"=%"

rem ---------- [3/7] 结束运行中的实例, 避免发布目录文件被占用 ----------
echo [3/7] 结束运行中的 LlamaDSH_Launcher...
call :log "3/7 taskkill"
taskkill /IM LlamaDSH_Launcher.exe /F >nul 2>nul
rem taskkill /F 返回后进程可能仍持有文件句柄, 必须等它真正消失再清目录/发布
set "WAITN=0"
:waitproc
tasklist /FI "IMAGENAME eq LlamaDSH_Launcher.exe" 2>nul | findstr /I "LlamaDSH_Launcher.exe" >nul
if errorlevel 1 goto :procgone
set /a WAITN+=1
if %WAITN% GEQ 20 (
    echo   [错误] LlamaDSH_Launcher.exe 仍在运行，无法安全改写产物
    goto :fail
)
ping -n 2 127.0.0.1 >nul 2>nul
goto :waitproc
:procgone
echo   已确认无运行实例

rem ---------- [4/7] 构建 gputemp 并发布单文件主程序 ----------
echo [4/7] 构建 gputemp 并发布单文件主程序 (dotnet publish)...
call :log "4/7 gputemp publish 开始"
set "GPUBIN=%ROOT%gputemp\bin\Release\net8.0-windows\win-x64\publish\gputemp.exe"
>>"%RUNLOG%" echo [%date% %time%] 现场: CSProj=%CSProj% PUBDIR=%PUBDIR% OUTDIR=%PKOUT%
dotnet publish "%ROOT%gputemp\gputemp.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -v q -nologo
if errorlevel 1 goto :fail
>>"%RUNLOG%" echo [%date% %time%] gputemp publish 后 PUBDIR:
dir /b "%ROOT%gputemp\bin\Release\net8.0-windows\win-x64\publish" >>"%RUNLOG%" 2>nul
>>"%RUNLOG%" echo [%date% %time%] gputemp publish 后 PKOUT:
dir /b "%PKOUT%" >>"%RUNLOG%" 2>nul
if not exist "%GPUBIN%" (
    echo   [错误] gputemp 构建产物缺失: %GPUBIN%
    goto :fail
)
copy /Y "%GPUBIN%" "%PROJ%\gputemp\gputemp.exe" >nul
if errorlevel 1 goto :fail
call :log "4/7 gputemp 完成 GPUBIN 就绪"
call :log "4/7 llamadsh-cli publish 开始"
dotnet publish "%ROOT%cli\llamadsh-cli.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -v q -nologo
if errorlevel 1 goto :fail
set "CLIBIN=%ROOT%cli\bin\Release\net8.0\win-x64\publish\llamadsh-cli.exe"
if not exist "%CLIBIN%" (
    echo   [错误] llamadsh-cli 构建产物缺失: %CLIBIN%
    goto :fail
)
call :log "4/7 llamadsh-cli 完成 CLIBIN 就绪"
if exist "%PUBDIR%" (
    rd /s /q "%PUBDIR%"
    if exist "%PUBDIR%" (
        echo   [错误] 无法清理发布目录（可能被占用）: %PUBDIR%
        goto :fail
    )
)
dotnet publish "%CSProj%" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -v q -nologo
if errorlevel 1 goto :fail
if not exist "%PUBDIR%\LlamaDSH_Launcher.exe" (
    echo   [错误] 发布产物缺失: %PUBDIR%\LlamaDSH_Launcher.exe
    goto :fail
)

rem ---------- [5/7] 刷新 installer_staging (ISCC 的打包源, 不刷新会打进旧版程序!) ----------
echo [5/7] 刷新 installer_staging...
call :log "4/7 主程序发布完成 / 5/7 staging 开始"
rem 先清空：确保打进安装包的只有本次产物，历史残留不会混进去
if exist "%STAGE%" rd /s /q "%STAGE%"
mkdir "%STAGE%"
if errorlevel 1 goto :fail
copy /Y "%PUBDIR%\LlamaDSH_Launcher.exe" "%STAGE%\LlamaDSH_Launcher.exe" >nul
if errorlevel 1 goto :fail
copy /Y "%GPUBIN%" "%STAGE%\gputemp.exe" >nul
if errorlevel 1 goto :fail
copy /Y "%CLIBIN%" "%STAGE%\llamadsh-cli.exe" >nul
if errorlevel 1 goto :fail
copy /Y "%PROJ%\Resources\app.ico" "%STAGE%\app.ico" >nul
if errorlevel 1 goto :fail
rem 尺寸兜底：主程序异常偏小说明发布没真正完成
for %%F in ("%STAGE%\LlamaDSH_Launcher.exe") do if %%~zF LSS 524288 (
    echo   [错误] 主程序异常偏小: %%~zF 字节
    goto :fail
)

rem ---------- [6/7] 生成安装包 ----------
echo [6/7] 生成安装包 (Inno Setup)...
if not exist "%PKOUT%" mkdir "%PKOUT%"
rem ISCC 完整输出始终写入日志文件(成功时 :cleanup 会删掉; 失败时保留供排查)
"%ISCC%" "%ISS%" > "%PKOUT%\iscc_build.log" 2>&1
if errorlevel 1 (
    echo   [错误] Inno Setup 编译失败（退出码 %errorlevel%）
    echo   完整日志: %PKOUT%\iscc_build.log  ——最后 30 行如下:
    echo   ------------------------------------------------------------
    powershell -NoProfile -Command "Get-Content -LiteralPath '%PKOUT%\iscc_build.log' -Tail 30"
    echo   ------------------------------------------------------------
    echo   常见原因: ① Setup.exe 正被占用（双击过安装包/杀软扫描中）, 关掉后重跑;
    echo             ② 磁盘满或 installer_output 目录权限问题。
    goto :fail
)
if not exist "%SETUPEXE%" (
    echo   [错误] 未生成安装包: %SETUPEXE%
    goto :fail
)
for %%F in ("%SETUPEXE%") do if %%~zF LSS 1048576 (
    echo   [错误] 安装包异常偏小: %%~zF 字节
    goto :fail
)

call :log "6/7 ISCC 编译通过"
rem ---------- [6.5] 代码签名(可选): 存在 %PROJ%\signtool-config.inc 才执行 ----------
rem 该文件不入库(见 .gitignore), 从 signtool-config.inc.example 复制填写; 内容是一行
rem   SIGNCMD=<签名命令, 用 %SETUPFILE% 占位安装包路径>
set "SIGNCMD="
set "SIGINC=%PROJ%\signtool-config.inc"
if exist "%SIGINC%" (
    for /f "usebackq tokens=1,* delims==" %%K in ("%SIGINC%") do (
        if /i "%%K"=="SIGNCMD" set "SIGNCMD=%%L"
    )
)
if defined SIGNCMD (
    echo [签名] 正在对安装包做代码签名...
    call :log "6.5 签名执行中"
    set "SETUPFILE=%SETUPEXE%"
    rem call 二次展开: SIGNCMD 里的 %SETUPFILE% 在运行时替换为安装包完整路径
    call %SIGNCMD%
    if errorlevel 1 (
        echo   [错误] 签名失败——未签名的安装包不对外分发, 中止
        set "SIGNCMD="
        set "SETUPFILE="
        goto :fail
    )
    powershell -NoProfile -Command "$s=Get-AuthenticodeSignature -LiteralPath '%SETUPEXE%'; if ($s.Status -ne 'Valid') { Write-Host '  [错误] 签名验证失败:' $s.Status; exit 1 } else { Write-Host ('  签名有效: ' + $s.SignerCertificate.Subject) }"
    if errorlevel 1 goto :fail
) else (
    echo [签名] 未配置 signtool-config.inc, 跳过代码签名（分发给他人会遇 SmartScreen 警告）
    call :log "6.5 无签名配置 跳过"
)
set "SIGNCMD="
set "SETUPFILE="

rem ---------- [7/7] 打包 Linux 复刻版 (Python 源码 zip, 排除缓存与测试产物) ----------
set "LNXZIP=%PKOUT%\LlamaDSH_Launcher_Linux_%LNXVER%.zip"
if not exist "%LNXSRC%\main.py" (
    echo [7/7] 跳过 Linux 启动器打包（未找到 %LNXSRC%\main.py）
    goto :cleanup
)
echo [7/7] 打包 Linux 启动器...
call :log "7/7 linux zip 开始"
if exist "%LNXSTG%" rd /s /q "%LNXSTG%"
mkdir "%LNXSTG%"
robocopy "%LNXSRC%" "%LNXSTG%" /E /XD __pycache__ gui-test-screenshots .git dist /XF *.pyc >nul
rem robocopy 退出码 <8 均为成功
if errorlevel 8 goto :fail
rem 先压到临时名, 成功后才替换旧包——中途失败不会把上次的好包删掉
set "LNXTMPZIP=%PKOUT%\_linux_new.zip"
if exist "%LNXTMPZIP%" del /f /q "%LNXTMPZIP%"
rem 杀软常实时扫描刚生成的文件导致偶发共享冲突, 失败自动重试一次
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Compress-Archive -Path '%LNXSTG%\*' -DestinationPath '%LNXTMPZIP%' -Force -ErrorAction Stop } catch { Write-Host ('  压缩出错: ' + $_.Exception.Message); exit 1 }"
if errorlevel 1 (
    echo   重试一次...
    ping -n 3 127.0.0.1 >nul 2>nul
    powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Compress-Archive -Path '%LNXSTG%\*' -DestinationPath '%LNXTMPZIP%' -Force -ErrorAction Stop } catch { Write-Host ('  压缩出错: ' + $_.Exception.Message); exit 1 }"
    if errorlevel 1 goto :fail
)
if not exist "%LNXTMPZIP%" (
    echo   [错误] 未生成 Linux 临时包: %LNXTMPZIP%
    goto :fail
)
if exist "%LNXZIP%" del /f /q "%LNXZIP%"
ren "%LNXTMPZIP%" "LlamaDSH_Launcher_Linux_%LNXVER%.zip"
if errorlevel 1 (
    echo   [错误] 替换旧包失败, 新包保留在: %LNXTMPZIP%
    goto :fail
)
call :log "7/7 zip 完成"
rem 打完即清理 staging 副本（否则仓库里长期留一份完整源码拷贝）
if exist "%LNXSTG%" rd /s /q "%LNXSTG%"

:cleanup
call :log "cleanup 清理输出目录"
rem 清理输出目录, 只保留 Setup.exe 与 Linux zip (防止历史散文件混入误导)
if exist "%PKOUT%" powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%PKOUT%' | Where-Object { $_.Name -ne 'LlamaDSH_Launcher_Setup.exe' -and $_.Name -notlike 'LlamaDSH_Launcher_Linux_*.zip' } | Remove-Item -Recurse -Force"

echo.
echo ==============================================
echo   封包完成
set "SETUP_HASH="
set "ZIP_HASH="
call :hash "%SETUPEXE%" SETUP_HASH
for %%F in ("%SETUPEXE%") do echo   安装包: %%~fF
for %%F in ("%SETUPEXE%") do echo          大小 %%~zF 字节  SHA256 %SETUP_HASH%
rem 注意：不能在 if(...) 块里 echo 刚由 call 赋值的变量——块在解析期就展开了它，会打印空值
if exist "%LNXZIP%" call :hash "%LNXZIP%" ZIP_HASH
if exist "%LNXZIP%" for %%F in ("%LNXZIP%") do echo   Linux 包: %%~fF
if exist "%LNXZIP%" for %%F in ("%LNXZIP%") do echo          大小 %%~zF 字节  SHA256 %ZIP_HASH%
echo   单文件主程序: %PUBDIR%\LlamaDSH_Launcher.exe
findstr /C:"#define MyAppVersion" "%ISS%"
echo ==============================================
call :do_pause
exit /b 0

:find_iscc
rem %~1 = 注册表键；读取 InstallLocation 并验证 ISCC.exe 是否存在
if defined ISCC exit /b 0
for /f "tokens=2,*" %%A in ('reg query %1 /v InstallLocation 2^>nul') do if not defined ISCC if exist "%%BISCC.exe" set "ISCC=%%BISCC.exe"
exit /b 0

:log
rem %~1 = 步骤标记, 追加一行带时间戳的黑匣子记录
>>"%RUNLOG%" echo [%date% %time%] %~1
exit /b 0

:hash
rem %~1 = 文件，%~2 = 接收 SHA256 的变量名
set "%~2="
if not exist "%~1" exit /b 0
for /f "skip=1 tokens=*" %%H in ('certutil -hashfile "%~1" SHA256 2^>nul') do if not defined %~2 set "%~2=%%H"
exit /b 0

:do_pause
if /I "%NOPAUSE%"=="1" exit /b 0
pause
exit /b 0

:fail
echo.
echo   [封包失败] 请检查上方错误信息
echo   提示: 输出目录里的临时残留（dll/pdb 等非安装包文件）会在下次成功封包时自动清理
call :log "失败退出, OUTDIR 现场如下"
if exist "%PKOUT%" dir /b /s "%PKOUT%" >>"%RUNLOG%" 2>nul
call :do_pause
exit /b 1
