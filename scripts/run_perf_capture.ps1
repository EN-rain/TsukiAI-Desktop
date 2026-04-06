param(
    [string]$Mode = "baseline",
    [string]$OutputDir = ".perf",
    [int]$DurationSeconds = 600,
    [int]$SampleIntervalSeconds = 5
)

$ErrorActionPreference = "Stop"

if ($Mode -ne "baseline" -and $Mode -ne "candidate") {
    throw "Mode must be 'baseline' or 'candidate'"
}

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

function Get-PercentileValue {
    param(
        [double[]]$Values,
        [double]$Percentile
    )

    if (-not $Values -or $Values.Count -eq 0) {
        return $null
    }

    $sorted = $Values | Sort-Object
    $index = [Math]::Ceiling($Percentile * $sorted.Count) - 1
    if ($index -lt 0) { $index = 0 }
    if ($index -ge $sorted.Count) { $index = $sorted.Count - 1 }
    return [Math]::Round([double]$sorted[$index], 2)
}

function Parse-LatestPercentiles {
    param(
        [string[]]$Lines,
        [string]$Operation
    )

    $pattern = "operation=${Operation}_percentiles, p50_ms=(?<p50>[\d\.]+), p95_ms=(?<p95>[\d\.]+)"
    $matches = foreach ($line in $Lines) {
        if ($line -match $pattern) {
            [pscustomobject]@{
                P50 = [double]$Matches["p50"]
                P95 = [double]$Matches["p95"]
            }
        }
    }

    if (-not $matches -or $matches.Count -eq 0) {
        return $null
    }

    return $matches[-1]
}

function Get-WindowLogLines {
    param(
        [string]$LogPath,
        [datetime]$StartLocal,
        [datetime]$EndLocal
    )

    if (-not (Test-Path $LogPath)) {
        return @()
    }

    $timestampPattern = '^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]'
    $results = New-Object System.Collections.Generic.List[string]

    foreach ($line in Get-Content $LogPath) {
        if ($line -notmatch $timestampPattern) {
            continue
        }

        $ts = [datetime]::ParseExact($Matches["ts"], "yyyy-MM-dd HH:mm:ss.fff", [System.Globalization.CultureInfo]::InvariantCulture)
        if ($ts -ge $StartLocal -and $ts -le $EndLocal) {
            $results.Add($line)
        }
    }

    return $results
}

function Get-ProcessSamples {
    param(
        [hashtable]$PreviousSnapshot,
        [double]$IntervalSeconds,
        [int]$CpuCount
    )

    $current = @{}
    $bridgeCpuPct = 0.0

    Get-Process | Where-Object { $_.ProcessName -match '^node$|^TsukiAI' } | ForEach-Object {
        $cpuValue = 0.0
        if ($null -ne $_.CPU) {
            $cpuValue = [double]$_.CPU
        }

        $current[$_.Id] = [pscustomobject]@{
            Id = $_.Id
            Name = $_.ProcessName
            CPU = $cpuValue
            WS = [int64]$_.WS
            PM = [int64]$_.PM
        }

        if ($_.ProcessName -eq 'node' -and $PreviousSnapshot.ContainsKey($_.Id)) {
            $cpuDelta = [double]($_.CPU - $PreviousSnapshot[$_.Id].CPU)
            if ($cpuDelta -gt 0 -and $IntervalSeconds -gt 0 -and $CpuCount -gt 0) {
                $bridgeCpuPct += ($cpuDelta / ($IntervalSeconds * $CpuCount)) * 100.0
            }
        }
    }

    return [pscustomobject]@{
        Snapshot = $current
        BridgeCpuPct = [Math]::Round($bridgeCpuPct, 2)
    }
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outFile = Join-Path $OutputDir "$Mode`_$timestamp.json"
$logPath = Join-Path $env:APPDATA "TsukiAI\dev.log"

Write-Host "[perf] Running capture mode=$Mode duration=${DurationSeconds}s"
Write-Host "[perf] Runtime log path: $logPath"

$buildSw = [System.Diagnostics.Stopwatch]::StartNew()
dotnet build TsukiAI.sln | Out-Host
$buildSw.Stop()

$testSw = [System.Diagnostics.Stopwatch]::StartNew()
dotnet test TsukiAI.sln | Out-Host
$testSw.Stop()

$captureStartLocal = Get-Date
$captureStartUtc = $captureStartLocal.ToUniversalTime()
$captureEndLocal = $captureStartLocal.AddSeconds($DurationSeconds)
$cpuCount = [Environment]::ProcessorCount
$bridgeCpuSamples = New-Object System.Collections.Generic.List[double]
$processSnapshot = @()
$previous = @{}

Write-Host "[perf] Capture window started at $($captureStartLocal.ToString("s")). Run a live voice session now."

while ((Get-Date) -lt $captureEndLocal) {
    $sample = Get-ProcessSamples -PreviousSnapshot $previous -IntervalSeconds $SampleIntervalSeconds -CpuCount $cpuCount
    $previous = $sample.Snapshot
    if ($sample.BridgeCpuPct -gt 0) {
        $bridgeCpuSamples.Add($sample.BridgeCpuPct)
    }

    Start-Sleep -Seconds $SampleIntervalSeconds
}

$processSnapshot = Get-Process | Where-Object { $_.ProcessName -match '^node$|^TsukiAI' } |
    Select-Object ProcessName, Id, CPU, WS, PM

$windowLines = Get-WindowLogLines -LogPath $logPath -StartLocal $captureStartLocal -EndLocal (Get-Date)
$sttPercentiles = Parse-LatestPercentiles -Lines $windowLines -Operation "stt"
$totalPercentiles = Parse-LatestPercentiles -Lines $windowLines -Operation "total"
$llmPercentiles = Parse-LatestPercentiles -Lines $windowLines -Operation "llm"
$ttsPercentiles = Parse-LatestPercentiles -Lines $windowLines -Operation "tts"

$successfulTurns = ($windowLines | Where-Object { $_ -match '\[VoiceFlow\] total_ms=' }).Count
$pipelineErrors = ($windowLines | Where-Object { $_ -match 'component=pipeline, operation=process_text, status=error' }).Count
$totalTurnAttempts = $successfulTurns + $pipelineErrors
$pipelineErrorRatePct = if ($totalTurnAttempts -gt 0) {
    [Math]::Round(($pipelineErrors / $totalTurnAttempts) * 100.0, 2)
} else {
    $null
}

$payload = [ordered]@{
    captured_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    mode = $Mode
    duration_seconds = $DurationSeconds
    sample_interval_seconds = $SampleIntervalSeconds
    log_path = $logPath
    automation = [ordered]@{
        dotnet_build_ms = [int]$buildSw.ElapsedMilliseconds
        dotnet_test_ms = [int]$testSw.ElapsedMilliseconds
    }
    process_snapshot = $processSnapshot
    runtime_metrics = [ordered]@{
        bridge_cpu_avg_pct = if ($bridgeCpuSamples.Count -gt 0) { [Math]::Round((($bridgeCpuSamples | Measure-Object -Average).Average), 2) } else { $null }
        bridge_cpu_p95_pct = Get-PercentileValue -Values $bridgeCpuSamples.ToArray() -Percentile 0.95
        stt_latency_p50_ms = if ($sttPercentiles) { $sttPercentiles.P50 } else { $null }
        stt_latency_p95_ms = if ($sttPercentiles) { $sttPercentiles.P95 } else { $null }
        llm_latency_p50_ms = if ($llmPercentiles) { $llmPercentiles.P50 } else { $null }
        llm_latency_p95_ms = if ($llmPercentiles) { $llmPercentiles.P95 } else { $null }
        tts_latency_p50_ms = if ($ttsPercentiles) { $ttsPercentiles.P50 } else { $null }
        tts_latency_p95_ms = if ($ttsPercentiles) { $ttsPercentiles.P95 } else { $null }
        end_to_end_latency_p50_ms = if ($totalPercentiles) { $totalPercentiles.P50 } else { $null }
        end_to_end_latency_p95_ms = if ($totalPercentiles) { $totalPercentiles.P95 } else { $null }
        pipeline_error_rate_pct = $pipelineErrorRatePct
        successful_turns = $successfulTurns
        pipeline_errors = $pipelineErrors
        log_lines_in_window = $windowLines.Count
    }
}

$payload | ConvertTo-Json -Depth 6 | Set-Content -Path $outFile -Encoding UTF8
Write-Host "[perf] Wrote $outFile"
