@echo off
setlocal
cd /d "%~dp0"

set "DOTNET_EXE=%~dp0.tools\dotnet\dotnet.exe"
if exist "%DOTNET_EXE%" goto dotnet_ready

set "DOTNET_EXE=dotnet"
where dotnet >nul 2>nul
if %ERRORLEVEL% EQU 0 goto dotnet_ready

echo .NET 10 SDK was not found.
echo Run setup-and-run.cmd first.
pause
exit /b 1

:dotnet_ready
if not exist "%~dp0.build\AutoVJ\AutoVJ.dll" (
    echo AutoVJ has not been built.
    echo Run setup-and-run.cmd first.
    pause
    exit /b 1
)

set "PATH=%~dp0.tools\ffmpeg\bin;%PATH%"
"%DOTNET_EXE%" "%~dp0.build\AutoVJ\AutoVJ.dll" %*
set "EXIT_CODE=%ERRORLEVEL%"

if "%EXIT_CODE%"=="0" exit /b 0
echo.
echo AutoVJ stopped with exit code %EXIT_CODE%.
pause
exit /b %EXIT_CODE%

