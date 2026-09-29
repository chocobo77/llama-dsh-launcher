@echo off
chcp 65001 >nul
setlocal
title LlamaDSH 启动器 · 一键封包

set "ROOT=%~dp0"
set "PROJ=%ROOT%LlamaDSH_Launcher_CS"
set "CSProj=%PROJ%\LlamaDSH_Launcher.csproj"
set "ISS=%PROJ%\installer.iss"
set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "PUBDIR=%PROJ%\bin\Release\net8.0-windows\win-x64\publish"
set "OUTDIR=%PROJ%\installer_output"
set "LNXSRC=%ROOT%LlamaDSH_Launcher_Linux"
set "LNXSTG=%PROJ%\installer_staging_linux"

echo ==============================================
echo   LlamaDSH 启动器 · 一键封包
echo ==============================================

rem ---------- [1/7] 可选: 版本号参数 (一键封包.cmd 1.2.0) ----------
if "%~1"=="" goto :envcheck
echo [1/7] 更新版本号: %~1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$iss='%ISS%'; $v='%~1'; $c=[IO.File]::ReadAllText($iss); $k='#define MyAppVersion'; $i=$c.IndexOf($k); if($i -lt 0){exit 1}; $j=$c.IndexOf([char]10,$i); if($j -lt 0){exit 1}; $c=$c.Substring(0,$i)+$k+' '+[char]34+$v+[char]34+$c.Substring($j); [IO.File]::WriteAllText($iss,$c,(New-Object System.Text.UTF8Encoding($true)))"
if errorlevel 1 goto :fail
findstr /C:"#define MyAppVersion" "%ISS%"

:envcheck
echo [2/7] 检查构建环境...
where dotnet >nul 2>nul
if errorlevel 1 (
    echo   [错误] 未找到 dotnet CLI
    goto :fail
)
if not exist "%ISCC%" (
    echo   [错误] 未找到 Inno Setup: "%ISCC%"
    goto :fail
)
if not exist "%CSProj%" (
    echo   [错误] 未找到工程: %CSProj%
    goto :fail
)
echo   dotnet / Inno Setup 就绪

rem 从 installer.iss 读取版本号, 用于 Linux 包命名 (解析失败回退 dev)
set "LNXVER=dev"
for /f "tokens=3" %%V in ('findstr /C:"#define MyAppVersion" "%ISS%" 2^>nul') do set "LNXVER=%%V"
set LNXVER=%LNXVER:"=%

rem ---------- [3/7] 结束运行中的实例, 避免发布目录文件被占用 ----------
echo [3/7] 结束运行中的 LlamaDSH_Launcher...
taskkill /IM LlamaDSH_Launcher.exe /F >nul 2>nul

rem ---------- [4/7] 构建 gputemp 并发布单文件主程序 ----------
echo [4/7] 构建 gputemp 并发布单文件主程序 (dotnet publish)...
rem 先构建 gputemp 单文件(框架依赖，随 .NET 8 Desktop Runtime 运行)。
rem 保证新克隆仓库(缺 gputemp.exe)也能封包；产物同步到 csproj 引用的目录，
rem 这样 launcher 发布时也能把 gputemp.exe 一并打进输出目录。
set "GPUBIN=%ROOT%gputemp\bin\Release\net8.0-windows\win-x64\publish\gputemp.exe"
dotnet publish "%ROOT%gputemp\gputemp.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -v q -nologo
if errorlevel 1 goto :fail
if not exist "%GPUBIN%" (
    echo   [错误] gputemp 构建产物缺失
    goto :fail
)
copy /Y "%GPUBIN%" "%PROJ%\gputemp\gputemp.exe" >nul
if errorlevel 1 goto :fail
if exist "%PUBDIR%" rd /s /q "%PUBDIR%"
dotnet publish "%CSProj%" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -v q -nologo
if errorlevel 1 goto :fail
if not exist "%PUBDIR%\LlamaDSH_Launcher.exe" (
    echo   [错误] 发布产物缺失
    goto :fail
)

rem ---------- [5/7] 刷新 installer_staging (ISCC 的打包源, 不刷新会打进旧版程序!) ----------
echo [5/7] 刷新 installer_staging...
if not exist "%PROJ%\installer_staging" mkdir "%PROJ%\installer_staging"
copy /Y "%PUBDIR%\LlamaDSH_Launcher.exe" "%PROJ%\installer_staging\LlamaDSH_Launcher.exe" >nul
if errorlevel 1 goto :fail
copy /Y "%GPUBIN%" "%PROJ%\installer_staging\gputemp.exe" >nul
if errorlevel 1 goto :fail
copy /Y "%PROJ%\Resources\app.ico" "%PROJ%\installer_staging\app.ico" >nul
if errorlevel 1 goto :fail

rem ---------- [6/7] 生成安装包 ----------
echo [6/7] 生成安装包 (Inno Setup)...
"%ISCC%" "%ISS%" /Q
if errorlevel 1 goto :fail

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
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%LNXSTG%\*' -DestinationPath '%LNXZIP%' -Force"
if errorlevel 1 goto :fail

:cleanup
rem 清理输出目录, 只保留 Setup.exe 与 Linux zip (防止历史散文件混入误导)
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%OUTDIR%' | Where-Object { $_.Name -ne 'LlamaDSH_Launcher_Setup.exe' -and $_.Name -notlike 'LlamaDSH_Launcher_Linux_*.zip' } | Remove-Item -Recurse -Force"

echo.
echo ==============================================
echo   封包完成
for %%F in ("%OUTDIR%\LlamaDSH_Launcher_Setup.exe") do echo   安装包: %%F  大小 %%~zF 字节
if exist "%LNXZIP%" for %%F in ("%LNXZIP%") do echo   Linux 包: %%F  大小 %%~zF 字节
echo   单文件主程序: %PUBDIR%\LlamaDSH_Launcher.exe
findstr /C:"#define MyAppVersion" "%ISS%"
echo ==============================================
pause
exit /b 0

:fail
echo.
echo   [封包失败] 请检查上方错误信息
pause
exit /b 1
