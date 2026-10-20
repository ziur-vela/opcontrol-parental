$ErrorActionPreference = "Continue"
$root = "C:\Program Files\OpControlParental"
$data = "C:\ProgramData\OpControlParental"

sc.exe stop OpControlParental | Out-Null
Start-Sleep -Seconds 3
if (Test-Path "$root\Client\OpControl.Parental.Client.exe") {
    & "$root\Client\OpControl.Parental.Client.exe" --restore-dns
}
Get-Process "OpControl.Parental.Helper" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
sc.exe delete OpControlParental | Out-Null
Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -like "OpControl *" -or $_.TaskPath -like "*OpControl*" } | Unregister-ScheduledTask -Confirm:$false
try {
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    $rootFolder = $svc.GetFolder("\")
    foreach ($name in @("OpControl Parental Helper $env:COMPUTERNAME", "OpControl Parental Helper")) {
        try { $rootFolder.DeleteFolder($name, $null) } catch { }
    }
} catch { }

Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue

Write-Host "OpControl Parental client removed from this device."
