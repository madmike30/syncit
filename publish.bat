@echo off
REM publish.bat - Build one standalone Windows executable
REM Output: publish\SyncIt.exe (the .NET runtime and native libraries are bundled)

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
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:PublishReadyToRun=true ^
    -p:PublishTrimmed=false ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=None
if errorlevel 1 goto :publish_failed
if not exist "%OUT%\SyncIt.exe" (
    echo.
    echo ERROR: Publish produced no SyncIt.exe. Install the .NET 8 SDK to build it.
    exit /b 1
)

echo.
echo [2/2] Done!
echo.
echo Published to: %OUT%
echo.
echo To run: double-click SyncIt.exe
echo   - Single process: sync engine + tray icon (like OneDrive)
echo   - No .NET installation required
echo   - No console windows, no child processes
echo.
exit /b 0

:publish_failed
    echo.
    echo ERROR: Publish failed!
    exit /b 1
