@echo off
chcp 65001 >nul
setlocal EnableExtensions
title LlamaDSH 启动器 · 一键封包

set "ROOT=%~dp0"
set "PROJ=%ROOT%LlamaDSH_Launcher_CS"
set "CSProj=%PROJ%\LlamaDSH_Launcher.csproj"
set "ISS=%PROJ%\installer.iss"
set "PUBDIR=%PROJ%\bin\Release\net8.0-windows\win-x64\publish"
set "OUTDIR=%PROJ%\installer_output"
set "STAGE=%PROJ%\installer_staging"
set "LNXSRC=%ROOT%LlamaDSH_Launcher_Linux"
set "LNXSTG=%PROJ%\installer_staging_linux"
set "SETUPEXE=%OUTDIR%\LlamaDSH_Launcher_Setup.exe"

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

:envcheck
echo [2/7] 检查构建环境...
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

rem 从 installer.iss 读取版本号, 用于 Linux 包命名 (解析失败回退 dev)
set "LNXVER=dev"
for /f "tokens=3" %%V in ('findstr /C:"#define MyAppVersion" "%ISS%" 2^>nul') do set "LNXVER=%%~V"
set "LNXVER=%LNXVER:"=%"

rem ---------- [3/7] 结束运行中的实例, 避免发布目录文件被占用 ----------
echo [3/7] 结束运行中的 LlamaDSH_Launcher...
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
set "GPUBIN=%ROOT%gputemp\bin\Release\net8.0-windows\win-x64\publish\gputemp.exe"
dotnet publish "%ROOT%gputemp\gputemp.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -v q -nologo
if errorlevel 1 goto :fail
if not exist "%GPUBIN%" (
    echo   [错误] gputemp 构建产物缺失: %GPUBIN%
    goto :fail
)
copy /Y "%GPUBIN%" "%PROJ%\gputemp\gputemp.exe" >nul
if errorlevel 1 goto :fail
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
rem 先清空：确保打进安装包的只有本次产物，历史残留不会混进去
if exist "%STAGE%" rd /s /q "%STAGE%"
mkdir "%STAGE%"
if errorlevel 1 goto :fail
copy /Y "%PUBDIR%\LlamaDSH_Launcher.exe" "%STAGE%\LlamaDSH_Launcher.exe" >nul
if errorlevel 1 goto :fail
copy /Y "%GPUBIN%" "%STAGE%\gputemp.exe" >nul
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
"%ISCC%" "%ISS%" /Q
if errorlevel 1 (
    echo   [错误] Inno Setup 编译失败（ISCC 退出码 %errorlevel%）
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

rem ---------- [7/7] 打包 Linux 复刻版 (Python 源码 zip, 排除缓存与测试产物) ----------
set "LNXZIP=%OUTDIR%\LlamaDSH_Launcher_Linux_%LNXVER%.zip"
if not exist "%LNXSRC%\main.py" (
    echo [7/7] 跳过 Linux 启动器打包（未找到 %LNXSRC%\main.py）
    goto :cleanup
)
echo [7/7] 打包 Linux 启动器...
if exist "%LNXSTG%" rd /s /q "%LNXSTG%"
mkdir "%LNXSTG%"
robocopy "%LNXSRC%" "%LNXSTG%" /E /XD __pycache__ gui-test-screenshots .git /XF *.pyc >nul
rem robocopy 退出码 <8 均为成功
if errorlevel 8 goto :fail
if exist "%LNXZIP%" del /f /q "%LNXZIP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%LNXSTG%\*' -DestinationPath '%LNXZIP%' -Force"
if errorlevel 1 goto :fail
if not exist "%LNXZIP%" (
    echo   [错误] 未生成 Linux 包: %LNXZIP%
    goto :fail
)
rem 打完即清理 staging 副本（否则仓库里长期留一份完整源码拷贝）
if exist "%LNXSTG%" rd /s /q "%LNXSTG%"

:cleanup
rem 清理输出目录, 只保留 Setup.exe 与 Linux zip (防止历史散文件混入误导)
if exist "%OUTDIR%" powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%OUTDIR%' | Where-Object { $_.Name -ne 'LlamaDSH_Launcher_Setup.exe' -and $_.Name -notlike 'LlamaDSH_Launcher_Linux_*.zip' } | Remove-Item -Recurse -Force"

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
call :do_pause
exit /b 1
