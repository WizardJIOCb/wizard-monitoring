$ErrorActionPreference = "Stop"

$logPath = Join-Path $PSScriptRoot "disable_fast_startup_hags.log"

function Write-Log {
    param([string]$Message)
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Log "Requesting elevation through UAC..."
    Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`""
    ) | Out-Null
    exit 0
}

$powerPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power"
$graphicsPath = "HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers"

$beforeFastStartup = (Get-ItemProperty -Path $powerPath -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
$beforeHags = (Get-ItemProperty -Path $graphicsPath -Name HwSchMode -ErrorAction SilentlyContinue).HwSchMode

Write-Log "Before: HiberbootEnabled=$beforeFastStartup HwSchMode=$beforeHags"

Set-ItemProperty -Path $powerPath -Name HiberbootEnabled -Type DWord -Value 0
Set-ItemProperty -Path $graphicsPath -Name HwSchMode -Type DWord -Value 1

$afterFastStartup = (Get-ItemProperty -Path $powerPath -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
$afterHags = (Get-ItemProperty -Path $graphicsPath -Name HwSchMode -ErrorAction SilentlyContinue).HwSchMode

Write-Log "After: HiberbootEnabled=$afterFastStartup HwSchMode=$afterHags"
Write-Log "Done. Restart Windows to fully apply HAGS."
