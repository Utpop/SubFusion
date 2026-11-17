@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

REM 直接运行 dist 目录版，不再使用 dist\SingleExe 的单文件 EXE。
REM 单文件版体积大、发布慢，且之前多次因发布被中断而残留旧版本，导致改了代码却仍旧报警。
REM 这里每次启动都从源码重新发布目录版，保证运行的一定是当前代码。

set "APP_DIR=%CD%\dist\SubFusion"
set "APP_EXE=%APP_DIR%\SubFusion.exe"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET SDK not found. Please install .NET SDK and retry.
    exit /b 1
)

echo Building SubFusion from source (Release)...
dotnet publish "src\SubFusion.App\SubFusion.App.csproj" -c Release -o "%APP_DIR%" --nologo
if errorlevel 1 (
    echo Publish failed.
    exit /b 1
)

start "" "%APP_EXE%" %*
exit /b 0
