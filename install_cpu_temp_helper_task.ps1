$ErrorActionPreference = "Stop"

$helper = "C:\Projects\wizard-monitoring\cpu_temp_helper\publish_pdh\cpu_temp_helper.exe"
$outFile = "C:\Projects\wizard-monitoring\cpu_temp_helper\cpu_temp.txt"

if (-not (Test-Path -LiteralPath $helper)) {
    throw "Helper not found: $helper"
}

New-Item -ItemType Directory -Path (Split-Path -Parent $outFile) -Force | Out-Null

$legacyTaskName = "RodionCpuTempHelper"
if (Get-ScheduledTask -TaskName $legacyTaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $legacyTaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $legacyTaskName -Confirm:$false -ErrorAction SilentlyContinue
}

$taskName = "WizardCpuTempHelperAdmin"
$action = New-ScheduledTaskAction -Execute $helper -Argument "--watch --interval-ms 2000 --out `"$outFile`""
$trigger = New-ScheduledTaskTrigger -AtLogOn
$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName

Write-Host "Installed and started scheduled task: $taskName"
Write-Host "Task user: $currentUser"
Write-Host "CPU temperature file: $outFile"
