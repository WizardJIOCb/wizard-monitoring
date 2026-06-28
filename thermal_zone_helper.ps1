param(
    [int]$IntervalSeconds = 5,
    [string]$OutFile = "C:\Projects\wizard-monitoring\cpu_temp_helper\cpu_temp_acpi.txt"
)

$createdNew = $false
$mutex = [Threading.Mutex]::new($true, "RodionThermalZoneHelper", [ref]$createdNew)
if (-not $createdNew) {
    return
}

try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutFile) -Force | Out-Null
    $statusFile = [IO.Path]::ChangeExtension($OutFile, ".status.txt")
    Set-Content -LiteralPath $statusFile -Value ("started " + (Get-Date).ToString("o")) -Encoding ASCII
    $lastTemp = $null

    while ($true) {
        $temp = $null

        try {
            $samples = Get-Counter '\Thermal Zone Information(*)\Temperature' -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop |
                Select-Object -ExpandProperty CounterSamples
            $rawValues = @($samples | ForEach-Object { [string]$_.CookedValue })

            $values = foreach ($sample in $samples) {
                $celsius = [double]$sample.CookedValue - 273.15
                if ($celsius -gt 0 -and $celsius -lt 115) {
                    $celsius
                }
            }

            if ($values) {
                $temp = [math]::Round(($values | Measure-Object -Maximum).Maximum, 0)
            }

            if ($null -eq $temp) {
                $typeperf = & typeperf "\Thermal Zone Information(*)\Temperature" -sc 1 2>$null
                $numbers = foreach ($line in $typeperf) {
                    if ($line -match '"[^"]+","([\d.]+)"') {
                        [double]$matches[1]
                    }
                }

                $typeperfValues = foreach ($number in $numbers) {
                    $celsius = $number - 273.15
                    if ($celsius -gt 0 -and $celsius -lt 115) {
                        $celsius
                    }
                }

                if ($typeperfValues) {
                    $temp = [math]::Round(($typeperfValues | Measure-Object -Maximum).Maximum, 0)
                    $rawValues = @($numbers | ForEach-Object { [string]$_ })
                }
            }
        } catch {
            $temp = $null
            Set-Content -LiteralPath $statusFile -Value ("error " + (Get-Date).ToString("o") + " " + $_.Exception.Message) -Encoding ASCII
        }

        if ($null -ne $temp) {
            $lastTemp = $temp
            Set-Content -LiteralPath $OutFile -Value ([string]$temp) -Encoding ASCII
            Set-Content -LiteralPath $statusFile -Value ("write " + (Get-Date).ToString("o") + " " + $temp) -Encoding ASCII
        } elseif ($null -ne $lastTemp) {
            Set-Content -LiteralPath $OutFile -Value ([string]$lastTemp) -Encoding ASCII
            Set-Content -LiteralPath $statusFile -Value ("stale " + (Get-Date).ToString("o") + " " + $lastTemp) -Encoding ASCII
        } else {
            Set-Content -LiteralPath $statusFile -Value ("no-temp " + (Get-Date).ToString("o") + " raw=" + ($rawValues -join ";")) -Encoding ASCII
        }

        Start-Sleep -Seconds $IntervalSeconds
    }
}
finally {
    if ($createdNew) {
        $mutex.ReleaseMutex() | Out-Null
        $mutex.Dispose()
    }
}
