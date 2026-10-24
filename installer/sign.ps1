# Signs a file with a PFX passed via env vars (CI secrets). Never commit certificates.
param([Parameter(Mandatory)][string]$File)
$ErrorActionPreference = 'Stop'
if (-not $env:SIGN_CERT_PFX_BASE64) { Write-Host "No signing certificate configured; skipping."; exit 0 }
$pfx = Join-Path $env:RUNNER_TEMP 'sign.pfx'
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGN_CERT_PFX_BASE64))
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
        Where-Object FullName -match 'x64' | Sort-Object FullName -Descending | Select-Object -First 1
    & $signtool.FullName sign /f $pfx /p $env:SIGN_CERT_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $File
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
} finally { Remove-Item $pfx -ErrorAction SilentlyContinue }
