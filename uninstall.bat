@echo off
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0common\uninstall.ps1"
echo Press any key to close . . .
pause >nul
