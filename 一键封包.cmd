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

echo ==============================================
echo   LlamaDSH 启动器 · 一键封包
echo ==============================================

rem ---------- [1/6] 可选: 版本号参数 (一键封包.cmd 1.2.0) ----------
if "%~1"=="" goto :envcheck
echo [1/6] 更新版本号: %~1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$iss='%ISS%'; $v='%~1'; $c=[IO.File]::ReadAllText($iss); $k='#define MyAppVersion'; $i=$c.IndexOf($k); if($i -lt 0){exit 1}; $j=$c.IndexOf([char]10,$i); if($j -lt 0){exit 1}; $c=$c.Substring(0,$i)+$k+' '+[char]34+$v+[char]34+$c.Substring($j); [IO.File]::WriteAllText($iss,$c,(New-Object System.Text.UTF8Encoding($true)))"
if errorlevel 1 goto :fail
findstr /C:"#define MyAppVersion" "%ISS%"

:envcheck
echo [2/6] 检查构建环境...
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

rem ---------- [3/6] 结束运行中的实例, 避免发布目录文件被占用 ----------
echo [3/6] 结束运行中的 LlamaDSH_Launcher...
taskkill /IM LlamaDSH_Launcher.exe /F >nul 2>nul

rem ---------- [4/6] 发布单文件主程序 ----------
echo [4/6] 发布单文件主程序 (dotnet publish)...
if exist "%PUBDIR%" rd /s /q "%PUBDIR%"
dotnet publish "%CSProj%" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -v q -nologo
if errorlevel 1 goto :fail
if not exist "%PUBDIR%\LlamaDSH_Launcher.exe" (
    echo   [错误] 发布产物缺失
    goto :fail
)

rem ---------- [5/6] 刷新 installer_staging (ISCC 的打包源, 不刷新会打进旧版程序!) ----------
echo [5/6] 刷新 installer_staging...
if not exist "%PROJ%\installer_staging" mkdir "%PROJ%\installer_staging"
copy /Y "%PUBDIR%\LlamaDSH_Launcher.exe" "%PROJ%\installer_staging\LlamaDSH_Launcher.exe" >nul
if errorlevel 1 goto :fail
copy /Y "%PROJ%\gputemp\gputemp.exe" "%PROJ%\installer_staging\gputemp.exe" >nul
if errorlevel 1 goto :fail
copy /Y "%PROJ%\Resources\app.ico" "%PROJ%\installer_staging\app.ico" >nul
if errorlevel 1 goto :fail

rem ---------- [6/6] 生成安装包 ----------
echo [6/6] 生成安装包 (Inno Setup)...
"%ISCC%" "%ISS%" /Q
if errorlevel 1 goto :fail

rem 清理输出目录, 只保留 Setup.exe (防止历史散文件混入误导)
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%OUTDIR%' | Where-Object { $_.Name -ne 'LlamaDSH_Launcher_Setup.exe' } | Remove-Item -Recurse -Force"

echo.
echo ==============================================
echo   封包完成
for %%F in ("%OUTDIR%\LlamaDSH_Launcher_Setup.exe") do echo   安装包: %%F  大小 %%~zF 字节
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
