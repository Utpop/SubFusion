@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

set "APP_EXE=%CD%\dist\SingleExe\SubtitleMerger.exe"

if not exist "%APP_EXE%" (
    echo Single EXE not found. Publishing release single-file build...
    where dotnet >nul 2>nul
    if errorlevel 1 (
        echo .NET SDK not found. Please install .NET SDK and retry.
        exit /b 1
    )
    dotnet publish "src\SubtitleMerger.App\SubtitleMerger.App.csproj" -c Release -p:PublishProfile=SingleFile --nologo
    if errorlevel 1 (
        echo Publish failed.
        exit /b 1
    )
)

start "" "%APP_EXE%" %*
exit /b 0
