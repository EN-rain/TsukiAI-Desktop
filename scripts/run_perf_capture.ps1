param(
    [string]$Mode = "baseline",
    [string]$OutputDir = ".perf",
    [int]$DurationSeconds = 600
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

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$outFile = Join-Path $OutputDir "$Mode`_$timestamp.json"

Write-Host "[perf] Running capture mode=$Mode duration=${DurationSeconds}s"

$buildSw = [System.Diagnostics.Stopwatch]::StartNew()
dotnet build TsukiAI.sln | Out-Host
$buildSw.Stop()

$testSw = [System.Diagnostics.Stopwatch]::StartNew()
dotnet test TsukiAI.sln | Out-Host
$testSw.Stop()

$proc = Get-Process | Where-Object { $_.ProcessName -match "TsukiAI|node" } |
    Select-Object ProcessName, Id, CPU, WS, PM

$payload = [ordered]@{
    captured_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    mode = $Mode
    duration_seconds = $DurationSeconds
    notes = "Run live Discord voice session during capture window and fill manual latency/cpu stats from app logs/system monitor."
    automation = [ordered]@{
        dotnet_build_ms = [int]$buildSw.ElapsedMilliseconds
        dotnet_test_ms = [int]$testSw.ElapsedMilliseconds
    }
    process_snapshot = $proc
    required_manual_metrics = @(
        "bridge_cpu_avg_pct",
        "bridge_cpu_p95_pct",
        "stt_latency_p50_ms",
        "stt_latency_p95_ms",
        "end_to_end_latency_p50_ms",
        "end_to_end_latency_p95_ms",
        "pipeline_error_rate_pct"
    )
}

$payload | ConvertTo-Json -Depth 6 | Set-Content -Path $outFile -Encoding UTF8
Write-Host "[perf] Wrote $outFile"
