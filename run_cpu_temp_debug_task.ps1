$ErrorActionPreference = "Stop"

$taskName = "RodionCpuTempHelperDebug"
$helper = "C:\Projects\wizard-monitoring\cpu_temp_helper\publish2\cpu_temp_helper.exe"
$out = "C:\Projects\wizard-monitoring\cpu_temp_helper\cpu_temp2.txt"

if (-not (Test-Path -LiteralPath $helper)) {
    throw "Helper not found: $helper"
}

$action = New-ScheduledTaskAction -Execute $helper -Argument "--watch --interval-ms 2000 --out `"$out`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5)
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
