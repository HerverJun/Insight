@echo off
set "OUTPUT_DIR=Publish"

echo ========================================================
echo Cleaning up previous build...
echo ========================================================
if exist "%OUTPUT_DIR%" (
    rd /s /q "%OUTPUT_DIR%"
)

echo.
echo ========================================================
echo Publishing Insight (Release, Win-x64)...
echo ========================================================
echo Note: This will create a framework-dependent build (requires .NET 8 runtime).
echo It excludes the nested 'runtimes' folder by flattening dependencies for win-x64.
echo.

dotnet publish -c Release -r win-x64 --self-contained false -o "%OUTPUT_DIR%"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
    echo Publish FAILED! Please check the errors above.
    echo !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ========================================================
echo Build Success!
echo Output Location: %~dp0%OUTPUT_DIR%
echo ========================================================
echo.
pause
