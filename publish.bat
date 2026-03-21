@echo off
REM publish.bat — Build and publish SyncIt to a single folder
REM Output: publish\ folder containing SyncIt.exe + dependencies

setlocal
set "ROOT=%~dp0"
set "OUT=%ROOT%publish"
set "CONFIG=Release"

echo ========================================
echo  SyncIt Publish
echo ========================================
echo.

REM Clean previous publish
if exist "%OUT%" rmdir /s /q "%OUT%"

echo [1/2] Publishing SyncIt.App...
dotnet publish "%ROOT%SyncIt.App\SyncIt.App.csproj" ^
    -c %CONFIG% ^
    -o "%OUT%" ^
    --self-contained false ^
    -p:PublishSingleFile=false
if errorlevel 1 (
    echo.
    echo ERROR: Publish failed!
    exit /b 1
)

echo.
echo [2/2] Done!
echo.
echo Published to: %OUT%
echo.
echo To run: double-click SyncIt.exe (or SyncIt.bat)
echo   - Single process: sync engine + tray icon (like OneDrive)
echo   - No console windows, no child processes
echo.
