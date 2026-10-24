<#
.SYNOPSIS
  Helps create your own Google sign-in client for SyncIt (the "advanced" path).

.DESCRIPTION
  If the Google Cloud CLI (gcloud) is installed, this script:
    1. signs you in to gcloud (browser),
    2. creates a new Google Cloud project,
    3. turns on the Google Drive API,
  and then opens the remaining Google Cloud pages in your browser, with the project already selected.

  What it cannot do: Google does not offer a supported command to create the OAuth consent screen
  (Branding/Audience) or a "Desktop app" OAuth client for a normal Gmail account. Those are 3 short
  steps in the browser; the pages are opened for you. Then import the downloaded JSON in SyncIt.

  Without gcloud, the script just opens all the pages in order.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\setup-google-client.ps1
#>
param(
    [string]$ProjectId = ("syncit-" + (Get-Random -Minimum 100000 -Maximum 999999)),
    [string]$ProjectName = "SyncIt"
)
$ErrorActionPreference = 'Stop'

function Open-Page($url, $what) {
    Write-Host ""
    Write-Host ">> $what" -ForegroundColor Cyan
    Start-Process $url
    Read-Host "   Press Enter when you're done with this page"
}

$gcloud = Get-Command gcloud -ErrorAction SilentlyContinue
if ($gcloud) {
    Write-Host "Found gcloud. Creating project '$ProjectId'..." -ForegroundColor Green
    $account = (& gcloud config get-value account 2>$null)
    if (-not $account) { & gcloud auth login --brief; if ($LASTEXITCODE) { throw "gcloud sign-in failed." } }
    & gcloud projects create $ProjectId --name=$ProjectName
    if ($LASTEXITCODE) { throw "Could not create the project. Try another -ProjectId." }
    & gcloud services enable drive.googleapis.com --project=$ProjectId
    if ($LASTEXITCODE) { throw "Could not enable the Google Drive API." }
    Write-Host "Project created and Google Drive API enabled." -ForegroundColor Green
} else {
    Write-Host "gcloud is not installed, so we'll do it all in the browser (about 10 minutes)." -ForegroundColor Yellow
    Write-Host "Tip: in SyncIt's setup window, use 'Use my own Google client (advanced)' for the same steps with buttons."
    Open-Page "https://console.cloud.google.com/projectcreate" "Create a project named SyncIt, then note its Project ID."
    $ProjectId = Read-Host "Paste the Project ID"
    Open-Page "https://console.cloud.google.com/apis/library/drive.googleapis.com?project=$ProjectId" "Click Enable to turn on the Google Drive API."
}

Open-Page "https://console.cloud.google.com/auth/branding?project=$ProjectId" "Branding: click Get started. App name: SyncIt, support email: yours, Audience: External, contact email: yours. Create."
Open-Page "https://console.cloud.google.com/auth/audience?project=$ProjectId" "Audience: add your Gmail under Test users (or click Publish app to avoid 7-day sign-in expiry)."
Open-Page "https://console.cloud.google.com/auth/clients/create?project=$ProjectId" "Create client: Application type 'Desktop app', name SyncIt, Create, then Download JSON."

Write-Host ""
Write-Host "Done. In SyncIt, click 'Import client file (.json)' and choose the client_secret_....json you downloaded." -ForegroundColor Green
