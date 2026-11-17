@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Mizuauto.ps1"
if errorlevel 1 (
  echo.
  echo Installation failed. You can run mizuauto.exe directly from the extracted folder.
  pause
  exit /b 1
)
endlocal
