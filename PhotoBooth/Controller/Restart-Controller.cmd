@echo off
setlocal DisableDelayedExpansion
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Restart-Controller.ps1" -SourceDirectory "%~dp0."
if errorlevel 1 (
    echo.
    echo Controller could not be restarted. See the error above.
    pause
    exit /b 1
)
exit /b 0
