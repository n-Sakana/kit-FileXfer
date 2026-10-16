@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0FileXfer.ps1" %*
set "FileXfer_EXIT=%ERRORLEVEL%"
if not "%FileXfer_EXIT%"=="0" (
    echo.
    echo FileXfer stopped with exit code %FileXfer_EXIT%.
    pause
)
endlocal & exit /b %FileXfer_EXIT%
