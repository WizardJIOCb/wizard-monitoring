param(
    [int]$IntervalSeconds = 2,
    [string]$LogRoot = "C:\Projects\wizard-monitoring\black_screen_logs",
    [string]$CpuTempFile = "C:\Projects\wizard-monitoring\cpu_temp_helper\cpu_temp.txt",
    [string]$NetAdapterMatch = "Realtek 8812BU",
    [int]$RetentionDays = 14
)

$ErrorActionPreference = "SilentlyContinue"
$script:NetAdapterMatchValue = $NetAdapterMatch
$runtimeDir = "C:\Projects\wizard-monitoring\runtime"
$watchdogPidFile = Join-Path $runtimeDir "black_screen_watchdog.pid"

$createdNew = $false
$mutex = [Threading.Mutex]::new($true, "RodionBlackScreenWatchdog", [ref]$createdNew)
if (-not $createdNew) {
    return
}

New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
Set-Content -LiteralPath $watchdogPidFile -Value $PID -Encoding ASCII

Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class WatchdogNativeTimes {
    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    public static UInt64 ToUInt64(FILETIME time) {
        return ((UInt64)time.HighDateTime << 32) | time.LowDateTime;
    }
}

public static class WatchdogNvml {
    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlFanSpeedInfo {
        public uint Version;
        public uint Fan;
        public uint Speed;
    }

    [DllImport("nvml.dll", EntryPoint="nvmlInit_v2", CallingConvention=CallingConvention.Cdecl)]
    public static extern int Init();

    [DllImport("nvml.dll", EntryPoint="nvmlDeviceGetHandleByIndex_v2", CallingConvention=CallingConvention.Cdecl)]
    public static extern int DeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint="nvmlDeviceGetNumFans", CallingConvention=CallingConvention.Cdecl)]
    public static extern int DeviceGetNumFans(IntPtr device, out uint fanCount);

    [DllImport("nvml.dll", EntryPoint="nvmlDeviceGetFanSpeedRPM", CallingConvention=CallingConvention.Cdecl)]
    public static extern int DeviceGetFanSpeedRPM(IntPtr device, ref NvmlFanSpeedInfo fanInfo);
}
"@

function Get-NvidiaSmiPath {
    $paths = @(
        (Join-Path $env:WINDIR "System32\nvidia-smi.exe"),
        "C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
    )

    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    return $null
}

function Read-CpuUsage {
    $idle = New-Object WatchdogNativeTimes+FILETIME
    $kernel = New-Object WatchdogNativeTimes+FILETIME
    $user = New-Object WatchdogNativeTimes+FILETIME

    if (-not [WatchdogNativeTimes]::GetSystemTimes([ref]$idle, [ref]$kernel, [ref]$user)) {
        return $null
    }

    $now = @{
        Idle = [WatchdogNativeTimes]::ToUInt64($idle)
        Kernel = [WatchdogNativeTimes]::ToUInt64($kernel)
        User = [WatchdogNativeTimes]::ToUInt64($user)
    }

    if (-not $script:PrevCpuTimes) {
        $script:PrevCpuTimes = $now
        return $null
    }

    $idleDelta = [double]($now.Idle - $script:PrevCpuTimes.Idle)
    $kernelDelta = [double]($now.Kernel - $script:PrevCpuTimes.Kernel)
    $userDelta = [double]($now.User - $script:PrevCpuTimes.User)
    $total = $kernelDelta + $userDelta
    $script:PrevCpuTimes = $now

    if ($total -le 0) {
        return $null
    }

    return [math]::Round((($total - $idleDelta) * 100.0) / $total, 1)
}

function Read-CpuTemperature {
    $temp = Read-CpuTemperatureFile -Path $CpuTempFile
    if ($null -ne $temp) {
        return $temp
    }

    if ($script:CpuTempSource -eq "LibreHardwareMonitor") {
        return Read-HardwareMonitorCpuTemperature -Namespace "root\LibreHardwareMonitor"
    }

    if ($script:CpuTempSource -eq "OpenHardwareMonitor") {
        return Read-HardwareMonitorCpuTemperature -Namespace "root\OpenHardwareMonitor"
    }

    if ($script:CpuTempNoSourceUntil -and (Get-Date) -lt $script:CpuTempNoSourceUntil) {
        return $null
    }

    $temp = Read-HardwareMonitorCpuTemperature -Namespace "root\LibreHardwareMonitor"
    if ($null -ne $temp) {
        $script:CpuTempSource = "LibreHardwareMonitor"
        return $temp
    }

    $temp = Read-HardwareMonitorCpuTemperature -Namespace "root\OpenHardwareMonitor"
    if ($null -ne $temp) {
        $script:CpuTempSource = "OpenHardwareMonitor"
        return $temp
    }

    $script:CpuTempNoSourceUntil = (Get-Date).AddSeconds(15)
    return $null
}

function Read-CpuTemperatureFile {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    $file = Get-Item -LiteralPath $Path
    if ($file.LastWriteTime -lt (Get-Date).AddSeconds(-15)) {
        return $null
    }

    $raw = Get-Content -LiteralPath $Path -Raw
    if ($null -eq $raw) {
        return $null
    }

    $text = ([string]$raw).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    if ($text -notmatch '^\d{1,3}([,.]\d+)?$') {
        return $null
    }

    $value = [double]::Parse($text.Replace(",", "."), [Globalization.CultureInfo]::InvariantCulture)
    if ($value -le 0 -or $value -ge 120) {
        return $null
    }

    return [math]::Round($value, 1)
}

function Read-HardwareMonitorCpuTemperature {
    param([string]$Namespace)

    try {
        $sensors = Get-CimInstance -Namespace $Namespace -ClassName Sensor -Filter "SensorType='Temperature'"
    } catch {
        return $null
    }

    $bestTemp = $null
    $bestScore = -1

    foreach ($sensor in $sensors) {
        if ($null -eq $sensor.Value) {
            continue
        }

        $name = ([string]$sensor.Name).ToLowerInvariant()
        $id = ([string]$sensor.Identifier).ToLowerInvariant()
        $score = -1

        if ($id.Contains("/intelcpu/")) {
            $score = 10
        } elseif ($id.Contains("/cpu/")) {
            $score = 8
        } elseif ($name.Contains("cpu")) {
            $score = 4
        }

        if ($score -lt 0) {
            continue
        }

        if ($name.Contains("package")) {
            $score += 6
        } elseif ($name.Contains("core max") -or $name.Contains("tctl") -or $name.Contains("tdie")) {
            $score += 5
        }

        if ($score -gt $bestScore) {
            $bestScore = $score
            $bestTemp = [math]::Round([double]$sensor.Value, 1)
        }
    }

    return $bestTemp
}

function Read-MemoryState {
    $os = Get-CimInstance Win32_OperatingSystem
    if (-not $os) {
        return @{
            UsedPct = $null
            UsedMb = $null
            TotalMb = $null
        }
    }

    $totalMb = [math]::Round($os.TotalVisibleMemorySize / 1024, 0)
    $freeMb = [math]::Round($os.FreePhysicalMemory / 1024, 0)
    $usedMb = $totalMb - $freeMb

    return @{
        UsedPct = [math]::Round(($usedMb * 100.0) / [math]::Max($totalMb, 1), 1)
        UsedMb = $usedMb
        TotalMb = $totalMb
    }
}

function Read-GpuState {
    param([string]$NvidiaSmi)

    $empty = @{
        TempC = $null
        GpuPct = $null
        MemPct = $null
        MemUsedMb = $null
        MemTotalMb = $null
        PowerW = $null
        FanPct = $null
        ClockMhz = $null
        MemClockMhz = $null
        PState = $null
        Status = "no-nvidia-smi"
    }

    if (-not $NvidiaSmi) {
        return $empty
    }

    $query = "temperature.gpu,utilization.gpu,utilization.memory,memory.used,memory.total,power.draw,fan.speed,clocks.gr,clocks.mem,pstate"
    $line = & $NvidiaSmi "--query-gpu=$query" "--format=csv,noheader,nounits" 2>$null | Select-Object -First 1

    if (-not $line) {
        $empty.Status = "nvidia-smi-no-output"
        return $empty
    }

    $parts = $line -split "\s*,\s*"
    while ($parts.Count -lt 10) {
        $parts += $null
    }

    return @{
        TempC = $parts[0]
        GpuPct = $parts[1]
        MemPct = $parts[2]
        MemUsedMb = $parts[3]
        MemTotalMb = $parts[4]
        PowerW = $parts[5]
        FanPct = $parts[6]
        ClockMhz = $parts[7]
        MemClockMhz = $parts[8]
        PState = $parts[9]
        Status = "ok"
    }
}

function Initialize-GpuRpmReader {
    if ($script:NvmlInitDone) {
        return
    }

    $script:NvmlInitDone = $true
    $script:NvmlOk = $false
    $script:NvmlFanCount = 1

    try {
        $status = [WatchdogNvml]::Init()
        if ($status -ne 0) {
            return
        }

        $device = [IntPtr]::Zero
        $status = [WatchdogNvml]::DeviceGetHandleByIndex(0, [ref]$device)
        if ($status -ne 0 -or $device -eq [IntPtr]::Zero) {
            return
        }

        $fanCount = [uint32]0
        $status = [WatchdogNvml]::DeviceGetNumFans($device, [ref]$fanCount)
        if ($status -eq 0 -and $fanCount -gt 0) {
            $script:NvmlFanCount = [int]$fanCount
        }

        $script:NvmlDevice = $device
        $script:NvmlOk = $true
    } catch {
        $script:NvmlOk = $false
    }
}

function Read-GpuFanRpm {
    Initialize-GpuRpmReader
    if (-not $script:NvmlOk) {
        return $null
    }

    $total = 0
    $readings = 0

    for ($fan = 0; $fan -lt $script:NvmlFanCount; $fan++) {
        try {
            $info = New-Object WatchdogNvml+NvmlFanSpeedInfo
            $info.Version = 0x0100000C
            $info.Fan = [uint32]$fan
            $status = [WatchdogNvml]::DeviceGetFanSpeedRPM($script:NvmlDevice, [ref]$info)
            if ($status -eq 0) {
                $total += [double]$info.Speed
                $readings += 1
            }
        } catch {
        }
    }

    if ($readings -eq 0) {
        return $null
    }

    return [math]::Round($total / $readings, 0)
}

function Is-IgnoredNetAdapter {
    param([string]$Text)

    $lower = $Text.ToLowerInvariant()
    foreach ($word in @("virtual", "vmware", "hyper-v", "vethernet", "tap", "tunnel", "loopback", "bluetooth", "openvpn", "wintun", "wireguard", "docker", "tailscale")) {
        if ($lower.Contains($word)) {
            return $true
        }
    }

    return $false
}

function Format-NetRate {
    param([Nullable[double]]$BitsPerSecond)

    if ($null -eq $BitsPerSecond) {
        return $null
    }

    if ($BitsPerSecond -ge 1000000000) {
        return ("{0:N1}G" -f ($BitsPerSecond / 1000000000)).Replace(",", ".")
    }

    if ($BitsPerSecond -ge 1000000) {
        return ("{0:N1}M" -f ($BitsPerSecond / 1000000)).Replace(",", ".")
    }

    if ($BitsPerSecond -ge 1000) {
        $kbits = $BitsPerSecond / 1000
        if ($kbits -lt 100) {
            return ("{0:N1}K" -f $kbits).Replace(",", ".")
        }

        return ("{0:N0}K" -f $kbits).Replace(",", ".")
    }

    return ("{0:N0}b" -f $BitsPerSecond).Replace(",", ".")
}

function Read-NetworkRates {
    $empty = @{
        Adapter = $null
        UpBps = $null
        DownBps = $null
        Up = $null
        Down = $null
        Status = "no-adapter"
    }

    $adapters = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq "Up" })
    if (-not $adapters.Count) {
        return $empty
    }

    $forced = @()
    if (-not [string]::IsNullOrWhiteSpace($script:NetAdapterMatchValue)) {
        $forced = @($adapters | Where-Object {
            $_.InterfaceDescription -like "*$($script:NetAdapterMatchValue)*" -or $_.Name -like "*$($script:NetAdapterMatchValue)*"
        })
    }

    $candidates = if ($forced.Count) {
        $forced
    } else {
        @($adapters | Where-Object { -not (Is-IgnoredNetAdapter ($_.Name + " " + $_.InterfaceDescription)) })
    }

    if (-not $candidates.Count) {
        $candidates = $adapters
    }

    $now = Get-Date
    $best = $null
    $bestScore = -1

    foreach ($adapter in $candidates) {
        $stats = Get-NetAdapterStatistics -Name $adapter.Name -ErrorAction SilentlyContinue
        if (-not $stats) {
            continue
        }

        $sent = [uint64]$stats.SentBytes
        $received = [uint64]$stats.ReceivedBytes
        $upBps = $null
        $downBps = $null

        if ($script:PrevNetStats -and $script:PrevNetStats.ContainsKey($adapter.Name)) {
            $prev = $script:PrevNetStats[$adapter.Name]
            $elapsed = ($now - $prev.Time).TotalSeconds
            if ($elapsed -gt 0) {
                $sentDelta = if ($sent -ge $prev.Sent) { $sent - $prev.Sent } else { 0 }
                $receivedDelta = if ($received -ge $prev.Received) { $received - $prev.Received } else { 0 }
                $upBps = [math]::Round(($sentDelta * 8.0) / $elapsed, 0)
                $downBps = [math]::Round(($receivedDelta * 8.0) / $elapsed, 0)
            }
        }

        $score = if ($null -ne $upBps -and $null -ne $downBps) { $upBps + $downBps } else { 0 }
        if ($score -gt $bestScore) {
            $bestScore = $score
            $best = @{
                Adapter = $adapter.Name
                UpBps = $upBps
                DownBps = $downBps
                Up = Format-NetRate $upBps
                Down = Format-NetRate $downBps
                Status = "ok"
            }
        }

        if (-not $script:PrevNetStats) {
            $script:PrevNetStats = @{}
        }

        $script:PrevNetStats[$adapter.Name] = @{
            Sent = $sent
            Received = $received
            Time = $now
        }
    }

    if ($best) {
        return $best
    }

    return $empty
}

function Csv {
    param($Value)

    if ($null -eq $Value) {
        $Value = ""
    }

    $text = [string]$Value
    return '"' + $text.Replace('"', '""') + '"'
}

function Get-TelemetryFile {
    param([string]$Root)

    $header = "time,uptime_sec,cpu_pct,cpu_temp_c,ram_used_pct,ram_used_mb,ram_total_mb,gpu_temp_c,gpu_util_pct,gpu_mem_util_pct,gpu_mem_used_mb,gpu_mem_total_mb,gpu_power_w,gpu_fan_pct,gpu_fan_rpm,gpu_clock_mhz,gpu_mem_clock_mhz,gpu_pstate,nv_status,net_adapter,net_up_bps,net_down_bps,net_up,net_down,net_status"
    $date = Get-Date -Format "yyyy-MM-dd"

    for ($version = 0; $version -lt 20; $version++) {
        $suffix = if ($version -eq 0) { "" } else { "_v$($version + 1)" }
        $path = Join-Path $Root ("telemetry_{0}{1}.csv" -f $date, $suffix)

        if (-not (Test-Path -LiteralPath $path)) {
            $header | Set-Content -LiteralPath $path -Encoding UTF8
            return $path
        }

        $firstLine = Get-Content -LiteralPath $path -TotalCount 1
        if ($firstLine -eq $header) {
            return $path
        }
    }

    throw "No telemetry file version available for $date"
}

try {
    New-Item -ItemType Directory -Path $LogRoot -Force | Out-Null

    Get-ChildItem -LiteralPath $LogRoot -Filter "telemetry_*.csv" |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$RetentionDays) } |
        Remove-Item -Force

    $statusLog = Join-Path $LogRoot "watchdog_status.log"
    $nvidiaSmi = Get-NvidiaSmiPath
    $bootTime = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
    Add-Content -LiteralPath $statusLog -Encoding UTF8 -Value ("{0} started boot={1} nvidia_smi={2}" -f (Get-Date).ToString("o"), $bootTime, $nvidiaSmi)

    while ($true) {
        try {
            $now = Get-Date
            $cpu = Read-CpuUsage
            $cpuTemp = Read-CpuTemperature
            $memory = Read-MemoryState
            $gpu = Read-GpuState -NvidiaSmi $nvidiaSmi
            $gpuFanRpm = Read-GpuFanRpm
            $network = Read-NetworkRates
            $uptime = [math]::Round(($now - $bootTime).TotalSeconds, 0)

            $line = @(
                (Csv $now.ToString("o")),
                (Csv $uptime),
                (Csv $cpu),
                (Csv $cpuTemp),
                (Csv $memory.UsedPct),
                (Csv $memory.UsedMb),
                (Csv $memory.TotalMb),
                (Csv $gpu.TempC),
                (Csv $gpu.GpuPct),
                (Csv $gpu.MemPct),
                (Csv $gpu.MemUsedMb),
                (Csv $gpu.MemTotalMb),
                (Csv $gpu.PowerW),
                (Csv $gpu.FanPct),
                (Csv $gpuFanRpm),
                (Csv $gpu.ClockMhz),
                (Csv $gpu.MemClockMhz),
                (Csv $gpu.PState),
                (Csv $gpu.Status),
                (Csv $network.Adapter),
                (Csv $network.UpBps),
                (Csv $network.DownBps),
                (Csv $network.Up),
                (Csv $network.Down),
                (Csv $network.Status)
            ) -join ","

            Add-Content -LiteralPath (Get-TelemetryFile -Root $LogRoot) -Encoding UTF8 -Value $line
        } catch {
            Add-Content -LiteralPath $statusLog -Encoding UTF8 -Value ("{0} loop-error {1}" -f (Get-Date).ToString("o"), $_.Exception.Message)
        }

        Start-Sleep -Seconds $IntervalSeconds
    }
} catch {
    Add-Content -LiteralPath (Join-Path $LogRoot "watchdog_status.log") -Encoding UTF8 -Value ("{0} fatal {1}" -f (Get-Date).ToString("o"), $_.Exception.Message)
}
finally {
    if ($createdNew) {
        Remove-Item -LiteralPath $watchdogPidFile -Force -ErrorAction SilentlyContinue
        $mutex.ReleaseMutex() | Out-Null
        $mutex.Dispose()
    }
}
