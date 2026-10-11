@echo off
setlocal DisableDelayedExpansion
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Controller.ps1" -Source "%~dp0."
if errorlevel 1 (
  echo Controller installation failed. Review the error above.
  pause
  exit /b 1
)
echo Controller installation complete.
exit /b 0
