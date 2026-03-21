@echo off
REM SyncIt — Single-click launcher
REM Builds if needed, then starts the unified app.
REM Runs as a single process: sync engine + tray icon (like OneDrive / Google Drive).

setlocal
set "ROOT=%~dp0"
set "APP_EXE=%ROOT%publish\SyncIt.exe"

REM If not published yet, do a quick publish
if not exist "%APP_EXE%" (
    echo First run — building SyncIt...
    call "%ROOT%publish.bat"
    if errorlevel 1 (
        echo Build failed! Check output above.
        pause
        exit /b 1
    )
)

REM Launch SyncIt detached, then close this console
start "" "%APP_EXE%"
