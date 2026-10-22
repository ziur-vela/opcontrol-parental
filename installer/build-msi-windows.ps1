# Copyright (c) 2026 Ziur / OpControl IT Solutions
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PublishRoot,
    [Parameter(Mandatory = $true)][string]$OutFile,
    [string]$InstallerDir = $PSScriptRoot,
    [string]$WixTool = "C:\BuildTools\opcontrol-parental\tools\wix.exe",
    [string]$DotnetRoot = "C:\BuildTools\opcontrol-parental\dotnet"
)

$ErrorActionPreference = "Stop"
$env:DOTNET_ROOT = $DotnetRoot

if (-not (Test-Path $WixTool)) {
    throw "WiX not found at $WixTool. Install it with: dotnet tool install wix --tool-path C:\ocp-build\tools --version 5.0.2"
}

& $WixTool extension add -g WixToolset.Util.wixext/5.0.2 2>$null | Out-Null
& $WixTool build (Join-Path $InstallerDir "Product.wxs") `
    -arch x64 `
    -ext WixToolset.Util.wixext `
    -loc (Join-Path $InstallerDir "en-us.wxl") `
    -d "ProductVersion=$Version" `
    -d "ClientDir=$PublishRoot\client" `
    -d "HelperDir=$PublishRoot\helper" `
    -d "BrandIcon=$(Join-Path (Split-Path $InstallerDir -Parent) 'src\OpControl.Parental.Client.Windows.Helper\brand\icon-color-128.png')" `
    -d "ProductIcon=$(Join-Path (Split-Path $InstallerDir -Parent) 'src\OpControl.Parental.Server\wwwroot\brand\favicon.ico')" `
    -d "InstallerDir=$InstallerDir" `
    -o $OutFile

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $OutFile"
