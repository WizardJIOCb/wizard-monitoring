$ErrorActionPreference = "Stop"

$watchdog = "C:\Projects\wizard-monitoring\black_screen_watchdog.ps1"
if (-not (Test-Path -LiteralPath $watchdog)) {
    throw "Watchdog script not found: $watchdog"
}

$startup = [Environment]::GetFolderPath("Startup")
New-Item -ItemType Directory -Path $startup -Force | Out-Null

$shortcutPath = Join-Path $startup "BlackScreenWatchdog.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = "powershell.exe"
$shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$watchdog`""
$shortcut.WorkingDirectory = "C:\Projects\wizard-monitoring"
$shortcut.WindowStyle = 7
$shortcut.Description = "Logs GPU/CPU/RAM telemetry for black screen diagnostics"
$shortcut.Save()

$alreadyRunning = Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -like "*black_screen_watchdog.ps1*" -and $_.ProcessId -ne $PID }

if (-not $alreadyRunning) {
    Start-Process -FilePath "powershell.exe" -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-WindowStyle", "Hidden",
        "-File", $watchdog
    ) -WindowStyle Hidden
}

Write-Host "Installed startup shortcut: $shortcutPath"
Write-Host "Logs: C:\Projects\wizard-monitoring\black_screen_logs"
