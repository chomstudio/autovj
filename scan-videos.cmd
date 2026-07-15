@echo off
setlocal
cd /d "%~dp0"
dotnet run --project Catalog\AutoVJ.Catalog.csproj --no-restore -- scan %*
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" goto scan_error
echo Scan completed.
goto scan_end

:scan_error
echo Scan failed. Exit code: %EXIT_CODE%

:scan_end
pause
exit /b %EXIT_CODE%
