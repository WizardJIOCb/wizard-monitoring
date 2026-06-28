param(
    [switch]$NoPause
)

$ErrorActionPreference = "SilentlyContinue"

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RuntimeDir = Join-Path $ProjectRoot "runtime"
$GpuPidFile = Join-Path $RuntimeDir "gpu_temp.pid"
$WatchdogPidFile = Join-Path $RuntimeDir "black_screen_watchdog.pid"
$CpuTempHelperTaskName = "WizardCpuTempHelperAdmin"

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

function Stop-ByProcessId {
    param(
        [int]$ProcessId,
        [string]$Name
    )

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $process) {
        return $false
    }

    try {
        Stop-Process -Id $ProcessId -Force -ErrorAction Stop
        Write-Step $Name "stopped" "pid $ProcessId"
        return $true
    } catch {
        Write-Step $Name "failed" "pid $ProcessId, $($_.Exception.Message)"
        return $false
    }
}

function Stop-ByCommandLine {
    param(
        [string]$Name,
        [string[]]$Needles
    )

    $stopped = 0
    $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue

    foreach ($process in $processes) {
        if ($process.ProcessId -eq $PID) {
            continue
        }

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

        if (-not $matchesAll) {
            continue
        }

        try {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction Stop
            Write-Step $Name "stopped" "pid $($process.ProcessId)"
            $stopped += 1
        } catch {
            Write-Step $Name "failed" "pid $($process.ProcessId), $($_.Exception.Message)"
        }
    }

    return $stopped
}

function Stop-ByProcessName {
    param(
        [string]$Name,
        [string]$ProcessName
    )

    $stopped = 0
    $processes = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    foreach ($process in $processes) {
        if (Stop-ByProcessId -ProcessId $process.Id -Name $Name) {
            $stopped += 1
        }
    }

    return $stopped
}

function Stop-GpuWidget {
    $stopped = 0

    $exeProcesses = Get-Process -Name "WizardMonitorApp" -ErrorAction SilentlyContinue
    foreach ($process in $exeProcesses) {
        try {
            $process.CloseMainWindow() | Out-Null
            Start-Sleep -Milliseconds 500
        } catch {
        }

        if (-not $process.HasExited) {
            if (Stop-ByProcessId -ProcessId $process.Id -Name "GPU/CPU widget") {
                $stopped += 1
            }
        } else {
            Write-Step "GPU/CPU widget" "stopped" "pid $($process.Id)"
            $stopped += 1
        }
    }

    if (Test-Path -LiteralPath $GpuPidFile) {
        $rawPid = Get-Content -LiteralPath $GpuPidFile -Raw -ErrorAction SilentlyContinue
        if ($null -ne $rawPid) {
            $pidText = ([string]$rawPid).Trim()
            $parsedPid = 0
            if ([int]::TryParse($pidText, [ref]$parsedPid)) {
                if (Stop-ByProcessId -ProcessId $parsedPid -Name "GPU/CPU widget") {
                    $stopped += 1
                }
            }
        }
    }

    $ahkProcesses = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like "AutoHotkey*" -and $_.MainWindowTitle -eq "gpu_temp.ahk" }

    foreach ($process in $ahkProcesses) {
        if (Stop-ByProcessId -ProcessId $process.Id -Name "GPU/CPU widget") {
            $stopped += 1
        }
    }

    Remove-Item -LiteralPath $GpuPidFile -Force -ErrorAction SilentlyContinue

    if ($stopped -eq 0) {
        Write-Step "GPU/CPU widget" "not running"
    }
}

Write-Host "Wizard Monitoring stop"
Write-Host "Project: $ProjectRoot"
Write-Host ""

Stop-GpuWidget

$watchdogStopped = Stop-ByCommandLine -Name "Black screen watchdog" -Needles @("black_screen_watchdog.ps1")
if ($watchdogStopped -eq 0 -and (Test-Path -LiteralPath $WatchdogPidFile)) {
    $rawPid = Get-Content -LiteralPath $WatchdogPidFile -Raw -ErrorAction SilentlyContinue
    if ($null -ne $rawPid) {
        $pidText = ([string]$rawPid).Trim()
        $parsedPid = 0
        if ([int]::TryParse($pidText, [ref]$parsedPid)) {
            if (Stop-ByProcessId -ProcessId $parsedPid -Name "Black screen watchdog") {
                $watchdogStopped += 1
            }
        }
    }
}
if ($watchdogStopped -eq 0) {
    Write-Step "Black screen watchdog" "not running"
}

$cpuByNameStopped = Stop-ByProcessName -Name "CPU temp helper" -ProcessName "cpu_temp_helper"
$cpuAcpiStopped = Stop-ByCommandLine -Name "CPU temp helper" -Needles @("cpu_temp_helper.exe", "cpu_temp_acpi.txt")
$cpuMainStopped = Stop-ByCommandLine -Name "CPU temp helper" -Needles @("cpu_temp_helper.exe", "cpu_temp.txt")
$taskStopped = 0
& schtasks.exe /Query /TN $CpuTempHelperTaskName /FO LIST 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    & schtasks.exe /End /TN $CpuTempHelperTaskName 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Step "CPU temp helper task" "stopped" $CpuTempHelperTaskName
        $taskStopped = 1
    }
}
if (($cpuByNameStopped + $cpuAcpiStopped + $cpuMainStopped + $taskStopped) -eq 0) {
    Write-Step "CPU temp helper" "not running"
}

Write-Host ""
Write-Host "Stop check completed."
