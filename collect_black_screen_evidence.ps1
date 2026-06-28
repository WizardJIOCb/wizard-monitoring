param(
    [int]$Hours = 72,
    [string]$LogRoot = "C:\Projects\wizard-monitoring\black_screen_logs"
)

$ErrorActionPreference = "SilentlyContinue"

New-Item -ItemType Directory -Path $LogRoot -Force | Out-Null

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outDir = Join-Path $LogRoot ("report_{0}" -f $stamp)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$start = (Get-Date).AddHours(-$Hours)
$systemProviders = @(
    "Display",
    "nvlddmkm",
    "NVIDIA OpenGL Driver",
    "Microsoft-Windows-WHEA-Logger",
    "WHEA-Logger",
    "Microsoft-Windows-Kernel-Power",
    "Microsoft-Windows-Kernel-Boot",
    "Microsoft-Windows-Kernel-General",
    "Microsoft-Windows-DxgKrnl",
    "BugCheck",
    "EventLog",
    "volmgr",
    "disk",
    "stornvme",
    "storahci",
    "iaStorAfsService",
    "ACPI"
)

$importantIds = @(1, 13, 14, 17, 18, 19, 41, 55, 56, 129, 141, 153, 4101, 6008, 7000, 7001, 7022, 7023, 7031, 7034)

function Export-Events {
    param(
        [string]$LogName,
        [string]$Path,
        [string[]]$Providers,
        [int[]]$Ids
    )

    $events = Get-WinEvent -FilterHashtable @{ LogName = $LogName; StartTime = $start } |
        Where-Object {
            ($Providers -contains $_.ProviderName) -or
            ($_.ProviderName -like "*NVIDIA*") -or
            ($_.ProviderName -like "*Display*") -or
            ($Ids -contains $_.Id)
        } |
        Sort-Object TimeCreated

    $events |
        Select-Object TimeCreated, Id, ProviderName, LevelDisplayName,
            @{ Name = "Message"; Expression = { ($_.Message -replace "\r?\n", " ") } } |
        Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8
}

Export-Events -LogName "System" -Path (Join-Path $outDir "system_events.csv") -Providers $systemProviders -Ids $importantIds
Export-Events -LogName "Application" -Path (Join-Path $outDir "application_events.csv") -Providers @("Application Error", "Application Hang", "Windows Error Reporting", "Desktop Window Manager") -Ids @(1000, 1001, 1002, 4101)

Get-CimInstance -Namespace root\cimv2 -ClassName Win32_ReliabilityRecords |
    Where-Object {
        try {
            [Management.ManagementDateTimeConverter]::ToDateTime($_.TimeGenerated) -ge $start
        } catch {
            $false
        }
    } |
    Select-Object TimeGenerated, SourceName, EventIdentifier, ProductName, Message |
    Export-Csv -LiteralPath (Join-Path $outDir "reliability_records.csv") -NoTypeInformation -Encoding UTF8

$latestTelemetry = Get-ChildItem -LiteralPath $LogRoot -Filter "telemetry_*.csv" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 3

foreach ($file in $latestTelemetry) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $outDir $file.Name) -Force
    Get-Content -LiteralPath $file.FullName -Tail 500 |
        Set-Content -LiteralPath (Join-Path $outDir ("tail_500_" + $file.Name)) -Encoding UTF8
}

$nvidiaSmi = Join-Path $env:WINDIR "System32\nvidia-smi.exe"
if (-not (Test-Path -LiteralPath $nvidiaSmi)) {
    $nvidiaSmi = "C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
}

if (Test-Path -LiteralPath $nvidiaSmi) {
    & $nvidiaSmi -q > (Join-Path $outDir "nvidia_smi_q.txt") 2>&1
    & $nvidiaSmi --query-gpu=timestamp,name,driver_version,temperature.gpu,utilization.gpu,memory.used,memory.total,power.draw,fan.speed,pstate --format=csv > (Join-Path $outDir "nvidia_smi_now.csv") 2>&1
}

Get-CimInstance Win32_VideoController |
    Select-Object Name, DriverVersion, DriverDate, Status, AdapterRAM, PNPDeviceID |
    Export-Csv -LiteralPath (Join-Path $outDir "video_controllers.csv") -NoTypeInformation -Encoding UTF8

Get-CimInstance Win32_OperatingSystem |
    Select-Object Caption, Version, BuildNumber, LastBootUpTime, LocalDateTime, FreePhysicalMemory, TotalVisibleMemorySize |
    Export-Csv -LiteralPath (Join-Path $outDir "os_state.csv") -NoTypeInformation -Encoding UTF8

Get-CimInstance Win32_Processor |
    Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed |
    Export-Csv -LiteralPath (Join-Path $outDir "cpu.csv") -NoTypeInformation -Encoding UTF8

@"
Black screen evidence report
Created: $(Get-Date -Format o)
Window: last $Hours hours

Start with:
- system_events.csv: Kernel-Power 41, EventLog 6008, WHEA, Display/nvlddmkm, disk/storage events.
- reliability_records.csv: LiveKernelEvent 117/141 and Windows Error Reporting records.
- tail_500_telemetry_*.csv: last GPU/CPU/RAM telemetry before the hard power-off.

Important interpretation:
- Kernel-Power 41 by itself usually means "Windows noticed an improper shutdown"; it is not the root cause.
- WHEA-Logger 17/18/19 points toward PCIe/CPU/RAM/power/hardware instability.
- Display 4101 or nvlddmkm events point toward NVIDIA driver/GPU reset.
- If telemetry stops abruptly with normal temperatures and no Windows errors, suspect a hard GPU/PSU/display-link level hang.
"@ | Set-Content -LiteralPath (Join-Path $outDir "README.txt") -Encoding UTF8

Write-Host "Report created: $outDir"
