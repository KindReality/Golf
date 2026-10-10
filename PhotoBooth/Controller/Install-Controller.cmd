@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Controller.ps1" -Source "%~dp0."
if errorlevel 1 (
  echo Controller installation failed. Review the error above.
  pause
  exit /b 1
)
echo Controller installation complete.
pause
