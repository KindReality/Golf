@echo off
setlocal DisableDelayedExpansion
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Experience.ps1" -Source "%~dp0."
if errorlevel 1 (
    echo.
    echo Experience installation failed. See the error above.
    pause
    exit /b 1
)
exit /b 0
