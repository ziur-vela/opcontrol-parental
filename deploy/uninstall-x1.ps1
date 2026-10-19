$ErrorActionPreference = "Continue"
$root = "C:\Program Files\OpControlParental"
$data = "C:\ProgramData\OpControlParental"

Get-Process "OpControl.Parental.Helper" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
sc.exe stop OpControlParental | Out-Null
Start-Sleep -Seconds 2
sc.exe delete OpControlParental | Out-Null
Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like "OpControl *" -or $_.TaskPath -like "*OpControl*" } | Unregister-ScheduledTask -Confirm:$false

Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue

Write-Host "OpControl Parental client removed from this device."
