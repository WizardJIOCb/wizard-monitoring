param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RuntimeDir = Join-Path $ProjectRoot "runtime"
$WidgetExe = Join-Path $ProjectRoot "publish\WizardMonitorAppSingle_v6\WizardMonitorApp.exe"
$GpuScript = Join-Path $ProjectRoot "gpu_temp.ahk"
$AhkExe = "C:\Program Files\AutoHotkey\v2\AutoHotkey64_UIA.exe"
$AfterburnerExe = "C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe"
$CpuHelper = Join-Path $ProjectRoot "cpu_temp_helper\publish_pdh\cpu_temp_helper.exe"
$CpuTempOut = Join-Path $ProjectRoot "cpu_temp_helper\cpu_temp.txt"
$WatchdogScript = Join-Path $ProjectRoot "black_screen_watchdog.ps1"
$PowerShellExe = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
$GpuPidFile = Join-Path $RuntimeDir "gpu_temp.pid"
$WatchdogPidFile = Join-Path $RuntimeDir "black_screen_watchdog.pid"
$GpuPowerLimitTaskName = "WizardGpuPowerLimit90"
$CpuTempHelperTaskName = "WizardCpuTempHelperAdmin"

New-Item -ItemType Directory -Path $RuntimeDir -Force | Out-Null

function Write-Step {
    param(
        [string]$Name,
        [string]$State,
        [string]$Detail = ""
    )

    $line = "{0,-26} {1}" -f $Name, $State
    if ($Detail) {
        $line += " - $Detail"
    }

    Write-Host $line
}

function Get-ProcessByCommandLine {
    param([string[]]$Needles)

    $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
    foreach ($process in $processes) {
        $commandLine = [string]$process.CommandLine
        if ([string]::IsNullOrWhiteSpace($commandLine)) {
            continue
        }

        $matchesAll = $true
        foreach ($needle in $Needles) {
            if ($commandLine -notlike "*$needle*") {
                $matchesAll = $false
                break
            }
        }

        if ($matchesAll) {
            return $process
        }
    }

    return $null
}

function Test-GpuWidgetRunning {
    $exeProcess = Get-Process -Name "WizardMonitorApp" -ErrorAction SilentlyContinue | Select-Object -First 1
    return $null -ne $exeProcess
}

function Get-GpuWidgetProcesses {
    return @(Get-Process -Name "WizardMonitorApp" -ErrorAction SilentlyContinue)
}

function Stop-GpuWidgetProcesses {
    param([object[]]$Processes)

    $stopped = 0
    $failed = 0

    foreach ($process in $Processes) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Start-Sleep -Milliseconds 300
            if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
                $failed += 1
            } else {
                $stopped += 1
            }
        } catch {
            $failed += 1
        }
    }

    return [pscustomobject]@{
        Stopped = $stopped
        Failed = $failed
    }
}

function Get-GpuWidgetDetail {
    param([object[]]$Processes)

    $expectedPath = [System.IO.Path]::GetFullPath($WidgetExe)
    $actualPaths = @()
    $hasUnknownPath = $false

    foreach ($process in $Processes) {
        $path = $null
        try {
            $path = $process.Path
        } catch {
        }

        if ([string]::IsNullOrWhiteSpace($path)) {
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction SilentlyContinue
            $path = $cim.ExecutablePath
        }

        if ([string]::IsNullOrWhiteSpace($path)) {
            $hasUnknownPath = $true
            continue
        }

        $actualPaths += [System.IO.Path]::GetFullPath($path)
    }

    $allCurrent = $Processes.Count -gt 0 -and
        -not $hasUnknownPath -and
        ($actualPaths | Where-Object { -not [string]::Equals($_, $expectedPath, [System.StringComparison]::OrdinalIgnoreCase) }).Count -eq 0

    return [pscustomobject]@{
        ExpectedPath = $expectedPath
        ActualPaths = $actualPaths
        HasUnknownPath = $hasUnknownPath
        AllCurrent = $allCurrent
    }
}

function Stop-LegacyAhkWidget {
    $stopped = 0

    if (Test-Path -LiteralPath $GpuPidFile) {
        $rawPid = Get-Content -LiteralPath $GpuPidFile -Raw -ErrorAction SilentlyContinue
        if ($null -ne $rawPid) {
            $pidText = ([string]$rawPid).Trim()
            $parsedPid = 0
            if ([int]::TryParse($pidText, [ref]$parsedPid)) {
                Stop-Process -Id $parsedPid -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 200
                if (-not (Get-Process -Id $parsedPid -ErrorAction SilentlyContinue)) {
                    $stopped += 1
                }
            }
        }
    }

    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "AutoHotkey*" -and $_.MainWindowTitle -eq "gpu_temp.ahk" } |
        ForEach-Object {
            $legacyPid = $_.Id
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 200
            if (-not (Get-Process -Id $legacyPid -ErrorAction SilentlyContinue)) {
                $stopped += 1
            }
        }

    Remove-Item -LiteralPath $GpuPidFile -Force -ErrorAction SilentlyContinue
    return $stopped
}

function Test-CpuTempHelperRunning {
    if (Test-Path -LiteralPath $CpuTempOut) {
        $file = Get-Item -LiteralPath $CpuTempOut -ErrorAction SilentlyContinue
        if ($file -and $file.LastWriteTime -gt (Get-Date).AddSeconds(-10)) {
            return $true
        }
    }

    return $null -ne (Get-ProcessByCommandLine @("cpu_temp_helper.exe", "cpu_temp.txt"))
}

function Test-AfterburnerRunning {
    return $null -ne (Get-Process -Name "MSIAfterburner" -ErrorAction SilentlyContinue | Select-Object -First 1)
}

function Start-CpuTempProvider {
    if (-not (Test-Path -LiteralPath $AfterburnerExe)) {
        Write-Step "CPU temp provider" "missing" "MSI Afterburner not found"
        return
    }

    if (Test-AfterburnerRunning) {
        Write-Step "CPU temp provider" "already running" "MSI Afterburner"
        return
    }

    Start-Process -FilePath $AfterburnerExe -ArgumentList "-m" -WorkingDirectory (Split-Path -Parent $AfterburnerExe) -WindowStyle Hidden | Out-Null
    Start-Sleep -Seconds 3

    if (Test-AfterburnerRunning) {
        Write-Step "CPU temp provider" "started" "MSI Afterburner"
    } else {
        Write-Step "CPU temp provider" "start requested" "MSI Afterburner"
    }
}

function Test-WatchdogRunning {
    if (Test-Path -LiteralPath $WatchdogPidFile) {
        $rawPid = Get-Content -LiteralPath $WatchdogPidFile -Raw -ErrorAction SilentlyContinue
        if ($null -ne $rawPid) {
            $pidText = ([string]$rawPid).Trim()
            $parsedPid = 0
            if ([int]::TryParse($pidText, [ref]$parsedPid)) {
                $process = Get-Process -Id $parsedPid -ErrorAction SilentlyContinue
                if ($process -and $process.ProcessName -eq "powershell") {
                    return $true
                }
            }
        }
    }

    if ($null -ne (Get-ProcessByCommandLine @("black_screen_watchdog.ps1"))) {
        return $true
    }

    $latestTelemetry = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot "black_screen_logs") -Filter "telemetry_*.csv" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    return $latestTelemetry -and $latestTelemetry.LastWriteTime -gt (Get-Date).AddSeconds(-10)
}

function Assert-FileExists {
    param(
        [string]$Path,
        [string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Name not found: $Path"
    }
}

function Invoke-GpuPowerLimitTask {
    $task = & schtasks.exe /Query /TN $GpuPowerLimitTaskName /FO LIST 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Step "GPU power limit" "task missing" "run install_gpu_power_limit_90_startup.bat once as admin"
        return
    }

    & schtasks.exe /Run /TN $GpuPowerLimitTaskName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Step "GPU power limit" "apply requested" "257 W"
    } else {
        Write-Step "GPU power limit" "apply failed" "task exists but did not run"
    }
}

function Invoke-CpuTempHelperTask {
    & schtasks.exe /Run /TN $CpuTempHelperTaskName 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Step "CPU temp helper" "admin start requested" $CpuTempOut
        return $true
    }

    Write-Step "CPU temp helper" "task start unavailable" "will request UAC"
    return $false
}

function Start-CpuTempHelperElevated {
    Start-Process -FilePath $CpuHelper -ArgumentList @(
        "--watch",
        "--interval-ms", "2000",
        "--out", $CpuTempOut
    ) -Verb RunAs -WindowStyle Hidden | Out-Null

    Write-Step "CPU temp helper" "admin start requested" "RunAs"
}

Assert-FileExists -Path $WidgetExe -Name "Wizard Monitoring app"
Assert-FileExists -Path $CpuHelper -Name "CPU temp helper"
Assert-FileExists -Path $WatchdogScript -Name "Black screen watchdog"

Write-Host "Wizard Monitoring startup"
Write-Host "Project: $ProjectRoot"
Write-Host ""

Invoke-GpuPowerLimitTask
Start-CpuTempProvider

if (Test-CpuTempHelperRunning) {
    Write-Step "CPU temp helper" "already running"
} else {
    $requested = Invoke-CpuTempHelperTask
    if (-not $requested) {
        Start-CpuTempHelperElevated
    }
}

if (Test-WatchdogRunning) {
    Write-Step "Black screen watchdog" "already running"
} else {
    Start-Process -FilePath $PowerShellExe -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-WindowStyle", "Hidden",
        "-File", $WatchdogScript
    ) -WorkingDirectory $ProjectRoot -WindowStyle Hidden | Out-Null
    Write-Step "Black screen watchdog" "started"
}

$_widgetProcesses = Get-GpuWidgetProcesses
if ($_widgetProcesses.Count -gt 0) {
    $widgetDetail = Get-GpuWidgetDetail -Processes $_widgetProcesses
    $legacyStopped = Stop-LegacyAhkWidget
    if ($widgetDetail.AllCurrent) {
        $legacyStillRunning = Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -like "AutoHotkey*" -and $_.MainWindowTitle -eq "gpu_temp.ahk" } |
            Select-Object -First 1
        $detail = if ($legacyStillRunning) { "v6, legacy AHK still running" } elseif ($legacyStopped -gt 0) { "v6, legacy AHK stopped" } else { "v6" }
        Write-Step "GPU/CPU widget" "already running" $detail
    } else {
        $stopResult = Stop-GpuWidgetProcesses -Processes $_widgetProcesses
        if ($stopResult.Failed -gt 0) {
            Write-Step "GPU/CPU widget" "restart failed" "old exe is still running; run start.bat as admin"
        } else {
            Start-Process -FilePath $WidgetExe -WorkingDirectory (Split-Path -Parent $WidgetExe) | Out-Null
            Write-Step "GPU/CPU widget" "restarted" "v6"
        }
    }
} else {
    $legacyStopped = Stop-LegacyAhkWidget
    Start-Process -FilePath $WidgetExe -WorkingDirectory (Split-Path -Parent $WidgetExe) | Out-Null
    $legacyStillRunning = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "AutoHotkey*" -and $_.MainWindowTitle -eq "gpu_temp.ahk" } |
        Select-Object -First 1
    $detail = if ($legacyStillRunning) { "exe, legacy AHK still running" } elseif ($legacyStopped -gt 0) { "exe, legacy AHK stopped" } else { "exe" }
    Write-Step "GPU/CPU widget" "started" $detail
}

Start-Sleep -Seconds 2

Write-Host ""
Write-Step "CPU temp helper" $(if (Test-CpuTempHelperRunning) { "ok" } else { "not running" })
Write-Step "Black screen watchdog" $(if (Test-WatchdogRunning) { "ok" } else { "not running" })
Write-Step "GPU/CPU widget" $(if (Test-GpuWidgetRunning) { "ok" } else { "not running" })

$latestTelemetry = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot "black_screen_logs") -Filter "telemetry_*.csv" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($latestTelemetry) {
    Write-Host ""
    Write-Host ("Latest telemetry: {0} ({1})" -f $latestTelemetry.Name, $latestTelemetry.LastWriteTime)
}
