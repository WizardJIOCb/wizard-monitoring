$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$LogPath = Join-Path $ProjectRoot "power_limit.log"
$LimitWatts = 257
$ExpectedDefaultWatts = 285

function Write-Log {
    param([string]$Message)

    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffzzz"), $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function Find-NvidiaSmi {
    $candidates = @(
        (Join-Path $env:WINDIR "System32\nvidia-smi.exe"),
        "C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return "nvidia-smi.exe"
}

function Invoke-NvidiaSmi {
    param(
        [string]$NvidiaSmi,
        [string[]]$Arguments
    )

    $output = & $NvidiaSmi @Arguments 2>&1
    return ($output | Out-String).Trim()
}

New-Item -ItemType Directory -Path $ProjectRoot -Force | Out-Null
$nvidiaSmi = Find-NvidiaSmi

Write-Log "START limit=${LimitWatts}W nvidia_smi=$nvidiaSmi"

$ready = $false
for ($attempt = 1; $attempt -le 60; $attempt++) {
    try {
        $query = Invoke-NvidiaSmi -NvidiaSmi $nvidiaSmi -Arguments @(
            "--query-gpu=name,power.limit,power.default_limit",
            "--format=csv,noheader,nounits"
        )

        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($query)) {
            Write-Log "GPU_READY attempt=$attempt query=$query"
            $ready = $true
            break
        }

        Write-Log "GPU_NOT_READY attempt=$attempt exit=$LASTEXITCODE output=$query"
    }
    catch {
        Write-Log "GPU_NOT_READY attempt=$attempt error=$($_.Exception.Message)"
    }

    Start-Sleep -Seconds 2
}

if (-not $ready) {
    Write-Log "FAILED gpu_not_ready"
    exit 2
}

try {
    $before = Invoke-NvidiaSmi -NvidiaSmi $nvidiaSmi -Arguments @(
        "--query-gpu=name,power.draw,power.limit,power.default_limit,power.min_limit,power.max_limit",
        "--format=csv,noheader,nounits"
    )
    Write-Log "BEFORE $before"

    $setOutput = Invoke-NvidiaSmi -NvidiaSmi $nvidiaSmi -Arguments @("-pl", "$LimitWatts")
    Write-Log "SET exit=$LASTEXITCODE output=$setOutput"

    Start-Sleep -Seconds 1

    $after = Invoke-NvidiaSmi -NvidiaSmi $nvidiaSmi -Arguments @(
        "--query-gpu=name,power.draw,power.limit,power.default_limit,power.min_limit,power.max_limit",
        "--format=csv,noheader,nounits"
    )
    Write-Log "AFTER $after"

    if ($after -notmatch "(^|,\s*)$LimitWatts(\.00)?(\s*,|$)") {
        Write-Log "WARNING limit_not_confirmed expected=${LimitWatts}W default=${ExpectedDefaultWatts}W"
        exit 3
    }

    Write-Log "OK limit=${LimitWatts}W"
    exit 0
}
catch {
    Write-Log "FAILED error=$($_.Exception.Message)"
    exit 1
}
