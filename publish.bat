@echo off
chcp 65001 >nul
echo ==========================================
echo       Insight Build & Package System
echo ==========================================
echo.

echo [1/3] Cleaning previous build...
if exist "PublishOutput" (
    rmdir /s /q "PublishOutput"
)

echo [2/3] Publishing Application (Release)...
dotnet publish -c Release -r win-x64 --self-contained false -o PublishOutput

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Build failed! Please check the errors above.
    pause
    exit /b %errorlevel%
)

echo.
echo [3/3] Build Successful!
echo ==========================================
echo Output Directory: %CD%\PublishOutput
echo ==========================================
echo.

explorer PublishOutput
pause
