@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem ---- Debug launcher: keeps the console window so every message stays visible ----
set "PYTHONPATH="
set "PYTHONHOME="

set "SCRIPT=%~dp0subtitle_merger.py"
set "PY="

for /f "delims=" %%I in ('where python.exe 2^>nul') do call :test "%%~dpI."

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

if not defined PY (
  echo.
  echo [ERROR] No usable Python 3 with tkinter support was found.
  echo.
  pause
  exit /b 1
)

echo Using interpreter: %PY%
echo Running: %SCRIPT%
echo.
"%PY%" "%SCRIPT%"
echo.
echo Program exited with code %errorlevel%
pause
exit /b 0

rem ---------------------------------------------------------------
rem %~1 = candidate folder; use the first one that can import tkinter
rem ---------------------------------------------------------------
:test
if defined PY exit /b
if not exist "%~1\python.exe" exit /b
"%~1\python.exe" -c "import tkinter" >nul 2>nul
if errorlevel 1 (
  echo [skip] %~1  --  tkinter not available
  exit /b
)
set "PY=%~1\python.exe"
echo [ok]   %PY%
exit /b
