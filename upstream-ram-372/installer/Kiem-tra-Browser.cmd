@echo off
setlocal
cd /d "%~dp0"
RobloxManager.BrowserChecks.exe > "%~dp0browser-checks-result.txt" 2>&1
set "browser_test_result=%errorlevel%"
type "%~dp0browser-checks-result.txt"
echo.
echo Result saved to browser-checks-result.txt
pause
exit /b %browser_test_result%
