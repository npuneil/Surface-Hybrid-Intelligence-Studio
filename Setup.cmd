@echo off
rem Double-click setup: Python environment, desktop shortcut and a check of every model (Setup.ps1 -Prepare).
rem Pass Setup.ps1 options to override, for example: Setup.cmd -NoShortcut
setlocal
set "SETUP_ARGS=%*"
if "%~1"=="" set "SETUP_ARGS=-Prepare"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup.ps1" %SETUP_ARGS%
set "SETUP_EXIT=%ERRORLEVEL%"
echo %CMDCMDLINE% | find /i "/c" >nul && (echo. & pause)
exit /b %SETUP_EXIT%