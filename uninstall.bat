@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0common\uninstall.ps1"
set "APP_EXIT=%ERRORLEVEL%"
pause
endlocal & exit /b %APP_EXIT%
