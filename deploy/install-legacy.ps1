# Copyright (c) 2026 Ziur / OpControl IT Solutions
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

param(
    [string]$ServerUrl = "https://parental.opcontrol.net",
    [Parameter(Mandatory = $true)][string]$EnrollmentCode,
    [string]$DeviceName = $env:COMPUTERNAME,
    [Parameter(Mandatory = $true)][string]$MonitoredAccount
)

$ErrorActionPreference = "Stop"
$stage = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = "C:\Program Files\OpControlParental"
$data = "C:\ProgramData\OpControlParental"

if (Test-Path "$root\Client\OpControl.Parental.Client.exe") {
    & "$root\Client\OpControl.Parental.Client.exe" --restore-dns
}
Get-Process "OpControl.Parental.Helper" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
sc.exe stop OpControlParental 2>$null | Out-Null
Start-Sleep -Seconds 2
sc.exe delete OpControlParental 2>$null | Out-Null
Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like "OpControl *" -or $_.TaskPath -like "*OpControl*" } | Unregister-ScheduledTask -Confirm:$false

New-Item -ItemType Directory -Force -Path "$root\Client", "$root\Helper", $data, "$data\private", "$data\public", "$data\user", "$data\install" | Out-Null
Copy-Item "$stage\client\*" "$root\Client" -Recurse -Force
Copy-Item "$stage\helper\*" "$root\Helper" -Recurse -Force
Copy-Item "$stage\install-legacy.ps1", "$stage\uninstall-legacy.ps1" "$data\install" -Force

$config = [ordered]@{
    ServerUrl        = $ServerUrl
    EnrollmentCode   = $EnrollmentCode
    DeviceName       = $DeviceName
    OsAccountName    = $MonitoredAccount
    MonitoredAccount = $MonitoredAccount
    HelperPath       = "$root\Helper\OpControl.Parental.Helper.exe"
    IdleThresholdSeconds = 180
} | ConvertTo-Json
Set-Content -Path "$data\config.json" -Value $config -Encoding UTF8
icacls "$data\config.json" /inheritance:r /grant:r "SYSTEM:F" "Administrators:F" | Out-Null
icacls "$data\private" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" | Out-Null
icacls "$data\public" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "Users:(OI)(CI)RX" | Out-Null
icacls "$data\user" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "Users:(OI)(CI)M" | Out-Null

sc.exe create OpControlParental binPath= "`"$root\Client\OpControl.Parental.Client.exe`"" start= auto DisplayName= "OpControl Parental" | Out-Null
sc.exe description OpControlParental "OpControl Parental device service" | Out-Null
sc.exe failure OpControlParental reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe start OpControlParental | Out-Null
Start-Sleep -Seconds 3

Write-Host "Installed for monitored account $MonitoredAccount against $ServerUrl"
sc.exe query OpControlParental
Write-Host "Data directory: $data"
Write-Host "Uninstall: run uninstall-legacy.ps1 as administrator"
