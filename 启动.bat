@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem ---- Clear env vars that may break the Python runtime ----
set "PYTHONPATH="
set "PYTHONHOME="

set "SCRIPT=%~dp0subtitle_merger.py"
set "PYW="
set "PY="

rem ---- 1) try every python.exe found in PATH ----
for /f "delims=" %%I in ('where python.exe 2^>nul') do call :test "%%~dpI."

rem ---- 2) try the usual install locations ----
call :test "%LOCALAPPDATA%\Programs\Python\Python314"
call :test "%LOCALAPPDATA%\Programs\Python\Python313"
call :test "%LOCALAPPDATA%\Programs\Python\Python312"
call :test "%LOCALAPPDATA%\Programs\Python\Python311"
call :test "%LOCALAPPDATA%\Programs\Python\Python310"
call :test "C:\Python314"
call :test "C:\Python313"
call :test "C:\Python312"
call :test "C:\Python311"
call :test "C:\Python310"
call :test "%ProgramFiles%\Python314"
call :test "%ProgramFiles%\Python313"
call :test "%ProgramFiles%\Python312"
call :test "%ProgramFiles%\Python311"
call :test "%ProgramFiles%\Python310"

if /i "%~1"=="--check" (
  echo PYW=%PYW%
  echo PY=%PY%
  if defined PYW exit /b 0
  exit /b 1
)

if not defined PYW goto :nopython

start "" "%PYW%" "%SCRIPT%"
exit /b 0

:nopython
echo.
echo   [ERROR] No usable Python 3 with tkinter support was found.
echo.
echo   Please install Python 3.8+ from https://www.python.org/downloads/
echo   and keep the "tcl/tk and IDLE" component checked during setup.
echo.
pause
exit /b 1

rem ---------------------------------------------------------------
rem %~1 = candidate folder; use the first one that can import tkinter
rem ---------------------------------------------------------------
:test
if defined PYW exit /b
if not exist "%~1\python.exe" exit /b
"%~1\python.exe" -c "import tkinter" >nul 2>nul
if errorlevel 1 exit /b
if exist "%~1\pythonw.exe" (
  set "PYW=%~1\pythonw.exe"
) else (
  set "PYW=%~1\python.exe"
)
set "PY=%~1\python.exe"
exit /b
