param(
    [string]$ServerUrl = "https://parental.opcontrol.net",
    [Parameter(Mandatory = $true)][string]$EnrollmentCode,
    [string]$DeviceName = $env:COMPUTERNAME,
    [string]$OsAccountName = ""
)

$ErrorActionPreference = "Stop"
$stage = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = "C:\Program Files\OpControlParental"
$data = "C:\ProgramData\OpControlParental"

Get-Process "OpControl.Parental.Helper" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
sc.exe stop OpControlParental 2>$null | Out-Null
Start-Sleep -Seconds 2
sc.exe delete OpControlParental 2>$null | Out-Null
Get-ScheduledTask -TaskName "OpControl Parental Helper*" -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false

New-Item -ItemType Directory -Force -Path "$root\Client", "$root\Helper", $data, "$data\private" | Out-Null
Copy-Item "$stage\client\*" "$root\Client" -Recurse -Force
Copy-Item "$stage\helper\*" "$root\Helper" -Recurse -Force

$config = [ordered]@{
    ServerUrl      = $ServerUrl
    EnrollmentCode = $EnrollmentCode
    DeviceName     = $DeviceName
    OsAccountName  = $OsAccountName
} | ConvertTo-Json
Set-Content -Path "$data\config.json" -Value $config -Encoding UTF8
icacls "$data\config.json" /inheritance:r /grant:r "SYSTEM:F" "Administrators:F" | Out-Null
icacls "$data" /grant "Users:(OI)(CI)RX" | Out-Null
icacls "$data\private" /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" | Out-Null

sc.exe create OpControlParental binPath= "`"$root\Client\OpControl.Parental.Client.exe`"" start= auto DisplayName= "OpControl Parental" | Out-Null
sc.exe description OpControlParental "OpControl Parental device service" | Out-Null
sc.exe failure OpControlParental reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe start OpControlParental | Out-Null

$action = New-ScheduledTaskAction -Execute "$root\Helper\OpControl.Parental.Helper.exe"
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -GroupId "BUILTIN\Users"
Register-ScheduledTask -TaskName "OpControl Parental Helper" -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null

$active = (Get-CimInstance Win32_ComputerSystem).UserName
if ($active) {
    $taskSafe = $active -replace "\\", "_"
    $taskName = "OpControl Parental Helper $taskSafe"
    $userTrigger = New-ScheduledTaskTrigger -AtLogOn -User $active
    $userPrincipal = New-ScheduledTaskPrincipal -UserId $active -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $userTrigger -Principal $userPrincipal -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    Write-Host "Started helper for active user $active"
}

Write-Host "Installed. Service status:"
sc.exe query OpControlParental
Write-Host "Data directory: $data"
Write-Host "Uninstall: run uninstall-x1.ps1 as administrator"
