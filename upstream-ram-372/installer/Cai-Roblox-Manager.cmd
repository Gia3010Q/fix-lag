@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-RobloxManager.ps1"
if errorlevel 1 (
  echo.
  echo Installation failed. You can run RobloxManager.exe directly from the extracted folder.
  pause
  exit /b 1
)
endlocal
