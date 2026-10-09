@echo off
setlocal DisableDelayedExpansion
rem Use the script's folder, even when launched from another working directory.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Restart-Experience.ps1" -SourceDirectory "%~dp0."
if errorlevel 1 (
    echo.
    echo Experience could not be restarted. See the error above.
    pause
    exit /b 1
)
exit /b 0
