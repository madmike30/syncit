# run-dev.ps1 — start Service + Tray together for local development
# Usage: .\run-dev.ps1

$root = $PSScriptRoot

Write-Host "Starting SyncIt.Service in a new window..." -ForegroundColor Cyan
Start-Process powershell -ArgumentList "-NoExit -Command `"dotnet run --project '$root\SyncIt.Service'`""

Start-Sleep -Milliseconds 500   # give the service a half-second head start

Write-Host "Starting SyncIt.Tray..." -ForegroundColor Cyan
dotnet run --project "$root\SyncIt.Tray"
