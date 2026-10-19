param(
    [Parameter(Mandatory = $true)][string]$CertificateThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [string]$Artifacts = (Join-Path $PSScriptRoot "..\artifacts")
)

$ErrorActionPreference = "Stop"
$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signtool) {
    $candidates = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'x64' } | Sort-Object FullName -Descending
    if (-not $candidates) { throw "signtool.exe not found. Install the Windows SDK signing tools." }
    $signtool = $candidates[0]
}

$files = Get-ChildItem $Artifacts -Filter "OpControlParental-*.*" | Where-Object { $_.Extension -in ".msi", ".exe" }
if (-not $files) { throw "No MSI or setup EXE found in $Artifacts." }

foreach ($file in $files) {
    Write-Host "Signing $($file.Name)"
    & $signtool.FullName sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $($file.Name)" }
    & $signtool.FullName verify /pa $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $($file.Name)" }
}

Write-Host "Signed $($files.Count) installer file(s)."
