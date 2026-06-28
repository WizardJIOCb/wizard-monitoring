$ErrorActionPreference = "Stop"

$helper = "C:\Projects\wizard-monitoring\cpu_temp_helper\publish_pdh\cpu_temp_helper.exe"
$outFile = "C:\Projects\wizard-monitoring\cpu_temp_helper\cpu_temp_acpi.txt"
if (-not (Test-Path -LiteralPath $helper)) {
    throw "Thermal zone helper not found: $helper"
}

$startup = [Environment]::GetFolderPath("Startup")
New-Item -ItemType Directory -Path $startup -Force | Out-Null

$shortcutPath = Join-Path $startup "WizardThermalZoneHelper.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $helper
$shortcut.Arguments = "--watch --interval-ms 5000 --out `"$outFile`""
$shortcut.WorkingDirectory = "C:\Projects\wizard-monitoring"
$shortcut.WindowStyle = 7
$shortcut.Description = "Writes CPU thermal zone temperature for Wizard Monitoring"
$shortcut.Save()

$alreadyRunning = Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -like "*publish_pdh*cpu_temp_helper.exe*" -and $_.CommandLine -like "*cpu_temp_acpi.txt*" }

if (-not $alreadyRunning) {
    Start-Process -FilePath $helper -ArgumentList @(
        "--watch",
        "--interval-ms", "5000",
        "--out", $outFile
    ) -WindowStyle Hidden
}

Write-Host "Installed startup shortcut: $shortcutPath"
