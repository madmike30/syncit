@echo off
REM publish.bat — Build and publish SyncIt to a single folder
REM Output: publish\ folder containing SyncIt.Tray.exe + SyncIt.Service.exe + dependencies

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

echo [1/3] Publishing SyncIt.Service...
dotnet publish "%ROOT%SyncIt.Service\SyncIt.Service.csproj" ^
    -c %CONFIG% ^
    -o "%OUT%" ^
    --self-contained false ^
    -p:PublishSingleFile=false
if errorlevel 1 (
    echo.
    echo ERROR: Service publish failed!
    exit /b 1
)

echo.
echo [2/3] Publishing SyncIt.Tray...
dotnet publish "%ROOT%SyncIt.Tray\SyncIt.Tray.csproj" ^
    -c %CONFIG% ^
    -o "%OUT%" ^
    --self-contained false ^
    -p:PublishSingleFile=false
if errorlevel 1 (
    echo.
    echo ERROR: Tray publish failed!
    exit /b 1
)

echo.
echo [3/3] Done!
echo.
echo Published to: %OUT%
echo.
echo To run: double-click SyncIt.Tray.exe (or SyncIt.bat)
echo   - The tray icon appears in the system tray
echo   - The sync service starts automatically in the background
echo   - No console windows
echo.
