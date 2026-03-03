@echo off
REM SyncIt — Single-click launcher
REM Builds if needed, then starts the tray app (which launches the service automatically).
REM Only the tray icon is visible — no console windows.

setlocal
set "ROOT=%~dp0"
set "TRAY_EXE=%ROOT%publish\SyncIt.Tray.exe"

REM If not published yet, do a quick publish
if not exist "%TRAY_EXE%" (
    echo First run — building SyncIt...
    call "%ROOT%publish.bat"
    if errorlevel 1 (
        echo Build failed! Check output above.
        pause
        exit /b 1
    )
)

start "" "%TRAY_EXE%"
