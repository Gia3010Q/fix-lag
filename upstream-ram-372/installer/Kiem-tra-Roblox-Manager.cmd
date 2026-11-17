@echo off
setlocal
cd /d "%~dp0"
RobloxManager.Diagnostics.exe > "%~dp0diagnostics-result.txt" 2>&1
set "mizu_test_result=%errorlevel%"
type "%~dp0diagnostics-result.txt"
echo.
echo Result saved to diagnostics-result.txt
pause
exit /b %mizu_test_result%
