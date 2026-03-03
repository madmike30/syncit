@echo off
REM run-dev.bat — Quick build and run for development
REM Builds in Debug mode, then launches the tray app (which starts the service).

setlocal
set "ROOT=%~dp0"

echo Building SyncIt (Debug)...
dotnet build "%ROOT%SyncIt.sln" -c Debug --no-restore >nul 2>&1
if errorlevel 1 (
    echo Build failed! Showing errors:
    echo.
    dotnet build "%ROOT%SyncIt.sln" -c Debug --no-restore
    pause
    exit /b 1
)

echo Starting SyncIt...
start "" "%ROOT%SyncIt.Tray\bin\Debug\net8.0-windows10.0.19041.0\SyncIt.Tray.exe"
