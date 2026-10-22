# Copyright (c) 2026 Ziur / OpControl IT Solutions
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

param(
    [string]$Version = "",
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$DotnetRoot = "C:\BuildTools\opcontrol-parental\dotnet",
    [string]$WixTool = "C:\BuildTools\opcontrol-parental\tools\wix.exe"
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $props = Get-Content (Join-Path $RepoRoot "Directory.Build.props") -Raw
    $match = [regex]::Match($props, '<Version>([^<]+)</Version>')
    if (-not $match.Success) { throw "Version not found in Directory.Build.props" }
    $Version = $match.Groups[1].Value
}

$dotnet = Join-Path $DotnetRoot "dotnet.exe"
if (-not (Test-Path $dotnet)) { throw "SDK not found at $dotnet. See README build-host setup." }
if (-not (Test-Path $WixTool)) { throw "WiX not found at $WixTool. See README build-host setup." }

$env:DOTNET_ROOT = $DotnetRoot
$env:PATH = "$DotnetRoot;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:NUGET_PACKAGES = "C:\BuildTools\opcontrol-parental\nuget"
$artifacts = Join-Path $RepoRoot "artifacts"
$publish = Join-Path $artifacts "publish-$Version"
$client = Join-Path $publish "client"
$helper = Join-Path $publish "helper"
$msi = Join-Path $artifacts "OpControlParental-$Version.msi"
$setupDir = Join-Path $artifacts "setup-$Version"
New-Item -ItemType Directory -Force $artifacts, $client, $helper, $setupDir | Out-Null

Write-Host "Publishing client $Version..."
& $dotnet publish (Join-Path $RepoRoot "src\OpControl.Parental.Client.Windows") -c Release -r win-x64 -o $client -p:Version=$Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Publishing helper $Version..."
& $dotnet publish (Join-Path $RepoRoot "src\OpControl.Parental.Client.Windows.Helper") -c Release -r win-x64 -o $helper -p:Version=$Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Building MSI..."
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "build-msi-windows.ps1") -Version $Version -PublishRoot $publish -OutFile $msi -InstallerDir $PSScriptRoot -WixTool $WixTool -DotnetRoot $DotnetRoot
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $msi)) { throw "MSI build failed" }

Write-Host "Building setup bootstrapper..."
& $dotnet publish (Join-Path $RepoRoot "src\OpControl.Parental.Setup") -c Release -r win-x64 -o $setupDir -p:Version=$Version -p:MsiPath=$msi
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$setup = Join-Path $artifacts "OpControlParental-Setup-$Version.exe"
Copy-Item (Join-Path $setupDir "OpControl.Parental.Setup.exe") $setup -Force
Remove-Item $setupDir -Recurse -Force

Write-Host ""
Write-Host "Artifacts:"
Get-FileHash $msi -Algorithm SHA256 | Format-List Path, Hash
Get-FileHash $setup -Algorithm SHA256 | Format-List Path, Hash
Get-Item $msi, $setup | Select-Object Name, Length
