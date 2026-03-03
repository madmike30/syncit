#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs, starts, updates, or removes the SyncIt Windows Service.

.DESCRIPTION
    Manages the SyncIt service using sc.exe.
    The service runs as YOUR user account (not SYSTEM) so it can access:
      - Your %APPDATA%\SyncIt\ config and token files
      - Your local sync folders
      - The named pipe for tray communication

.PARAMETER Action
    install    Install and start the service (default)
    uninstall  Stop and remove the service
    start      Start the service
    stop       Stop the service
    status     Show service status

.PARAMETER ServiceExePath
    Path to SyncIt.Service.exe. Defaults to .\publish\service\SyncIt.Service.exe

.PARAMETER UserName
    Windows account to run the service as. Defaults to current user.

.PARAMETER Password
    Password for the user account. Required if not running as the target user.

.EXAMPLE
    .\ServiceInstaller.ps1 -Action install -UserName "DOMAIN\Alice" -Password "P@ssw0rd"

.EXAMPLE
    .\ServiceInstaller.ps1 -Action uninstall
#>
param(
    [ValidateSet("install", "uninstall", "start", "stop", "status")]
    [string]$Action = "install",

    [string]$ServiceExePath = "",
    [string]$UserName = "",
    [string]$Password = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ServiceName    = "SyncItService"
$DisplayName    = "SyncIt Google Drive Sync"
$Description    = "Bidirectional Google Drive sync service with deletion safety."

# Resolve service exe path
if (-not $ServiceExePath) {
    $ServiceExePath = Join-Path $PSScriptRoot "..\publish\service\SyncIt.Service.exe"
}
$ServiceExePath = Resolve-Path $ServiceExePath -ErrorAction SilentlyContinue
if (-not $ServiceExePath) {
    Write-Error "SyncIt.Service.exe not found. Build the project first:`n  dotnet publish SyncIt.Service -c Release -r win-x64 --self-contained false -o publish\service"
    exit 1
}

# Resolve user account
if (-not $UserName) {
    $UserName = "$env:USERDOMAIN\$env:USERNAME"
}

function Get-ServiceStatus {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    return $svc
}

switch ($Action) {
    "install" {
        $existing = Get-ServiceStatus
        if ($existing) {
            Write-Host "Service '$ServiceName' already exists. Use -Action uninstall first." -ForegroundColor Yellow
            exit 0
        }

        Write-Host "Installing $ServiceName..." -ForegroundColor Cyan
        Write-Host "  Exe:  $ServiceExePath"
        Write-Host "  User: $UserName"

        # Create the service
        $null = sc.exe create $ServiceName `
            binPath= "`"$ServiceExePath`"" `
            start= auto `
            DisplayName= "$DisplayName"

        if ($LASTEXITCODE -ne 0) {
            Write-Error "sc.exe create failed with code $LASTEXITCODE"
            exit 1
        }

        # Set description
        $null = sc.exe description $ServiceName "$Description"

        # Set service account
        if ($Password) {
            $null = sc.exe config $ServiceName obj= "$UserName" password= "$Password"
        } else {
            Write-Warning "No password provided. Service will run as LocalSystem (may not have access to user files)."
            Write-Warning "Re-run with -Password to configure the correct user account."
        }

        # Configure failure recovery: restart after 1min, 5min, 10min
        $null = sc.exe failure $ServiceName reset= 3600 actions= restart/60000/restart/300000/restart/600000

        # Add tray app to startup
        $startupFolder = [Environment]::GetFolderPath("Startup")
        $trayExe = Join-Path (Split-Path $ServiceExePath) "..\..\tray\SyncIt.Tray.exe"
        if (Test-Path $trayExe) {
            $shortcutPath = Join-Path $startupFolder "SyncIt Tray.lnk"
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = (Resolve-Path $trayExe).Path
            $shortcut.Save()
            Write-Host "  Tray app added to startup: $shortcutPath" -ForegroundColor Green
        }

        # Start the service
        Write-Host "Starting $ServiceName..." -ForegroundColor Cyan
        Start-Service -Name $ServiceName
        Start-Sleep -Seconds 2

        $svc = Get-ServiceStatus
        Write-Host "Service status: $($svc.Status)" -ForegroundColor ($svc.Status -eq 'Running' ? 'Green' : 'Red')

        Write-Host "`nInstallation complete!" -ForegroundColor Green
        Write-Host "Next steps:"
        Write-Host "  1. Run SyncIt.Tray.exe (or reboot for auto-start)"
        Write-Host "  2. The tray app will guide you through Google OAuth if needed"
        Write-Host "  3. Edit %APPDATA%\SyncIt\syncit.json to configure your sync pairs"
    }

    "uninstall" {
        Write-Host "Removing $ServiceName..." -ForegroundColor Cyan
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        $null = sc.exe delete $ServiceName

        # Remove startup shortcut
        $startupFolder = [Environment]::GetFolderPath("Startup")
        $shortcut = Join-Path $startupFolder "SyncIt Tray.lnk"
        if (Test-Path $shortcut) { Remove-Item $shortcut }

        Write-Host "Service removed." -ForegroundColor Green
        Write-Host "Note: your data in %APPDATA%\SyncIt\ was not removed."
    }

    "start" {
        Start-Service -Name $ServiceName
        Write-Host "Service started." -ForegroundColor Green
    }

    "stop" {
        Stop-Service -Name $ServiceName -Force
        Write-Host "Service stopped." -ForegroundColor Yellow
    }

    "status" {
        $svc = Get-ServiceStatus
        if ($svc) {
            Write-Host "Service: $($svc.Status)" -ForegroundColor ($svc.Status -eq 'Running' ? 'Green' : 'Yellow')
        } else {
            Write-Host "Service not installed." -ForegroundColor Red
        }
    }
}
