@echo off
rem Builds runtime\windows-x64 (the MiniUI engine, from ..\miniUI or MINIUI_ROOT) and ui\MiniUI.App.dll (FileXfer's
rem native component; MiniUI loads it from beside the start page).
rem Needs the .NET 9 SDK and the Visual Studio C++ build tools (Native AOT). Users of the folder need neither.
setlocal
cd /d "%~dp0..\.."
if "%MINIUI_ROOT%"=="" set "MINIUI_ROOT=%CD%\..\miniUI"
if not exist "%MINIUI_ROOT%\tools\build.cmd" (echo MiniUI source not found: %MINIUI_ROOT% & exit /b 1)
set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
call "%MINIUI_ROOT%\tools\build.cmd" || exit /b 1
dotnet publish native\FileXfer.Native.csproj -c Release -r win-x64 -nologo -v q -o artifacts\native-win-x64 || exit /b 1
if not exist runtime\windows-x64 mkdir runtime\windows-x64
copy /y "%MINIUI_ROOT%\dist\windows-x64\aot\MiniUI.Engine.dll" runtime\windows-x64\ >nul || exit /b 1
copy /y "%MINIUI_ROOT%\dist\windows-x64\aot\MiniUI.Bridge.dll" runtime\windows-x64\ >nul || exit /b 1
if exist runtime\windows-x64\FileXfer.Native.dll del runtime\windows-x64\FileXfer.Native.dll
copy /y artifacts\native-win-x64\FileXfer.Native.dll ui\MiniUI.App.dll >nul || exit /b 1
echo built: runtime\windows-x64 and ui\MiniUI.App.dll
dir /b runtime\windows-x64
