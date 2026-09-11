[CmdletBinding()]
param(
    [string]$OpenVoiceUrl = $env:TSUKI_OPENVOICE_URL,
    [int]$ApiPort = 5000,
    [int]$BridgePort = 3001
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$apiRoot = Join-Path $projectRoot 'TsukiAI.Api'
$bridgeRoot = Join-Path $projectRoot 'discord-voice-bridge'
$apiDll = Join-Path $apiRoot 'bin\Debug\net8.0\TsukiAI.Api.dll'

function Read-SecretText([string]$Prompt) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Test-ListeningPort([int]$Port) {
    return [bool](netstat -ano | Select-String (":$Port\s+"))
}

function Wait-ApiHealth([string]$Url, [string]$ApiKey, [int]$TimeoutSeconds = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -Headers @{ 'X-Api-Key' = $ApiKey } -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                return $true
            }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    } while ((Get-Date) -lt $deadline)
    return $false
}

if (-not (Test-Path -LiteralPath $apiDll)) {
    throw "Built API is missing: $apiDll. Build TsukiAI.Api first."
}
if (-not (Test-Path -LiteralPath (Join-Path $bridgeRoot 'index.js'))) {
    throw "Discord bridge is missing: $bridgeRoot\index.js"
}

$apiStdout = Join-Path $env:TEMP 'tsuki-api.stdout.log'
$apiStderr = Join-Path $env:TEMP 'tsuki-api.stderr.log'
$bridgeStdout = Join-Path $env:TEMP 'tsuki-discord-bridge.stdout.log'
$bridgeStderr = Join-Path $env:TEMP 'tsuki-discord-bridge.stderr.log'
$apiProcess = $null
$bridgeProcess = $null
$startedApi = $false
$localApiKey = $env:TSUKI_API_KEY

try {
    if ([string]::IsNullOrWhiteSpace($OpenVoiceUrl)) {
        throw 'Set TSUKI_OPENVOICE_URL in this PowerShell session before starting the stack.'
    }
    if ([string]::IsNullOrWhiteSpace($env:TSUKI_OPENVOICE_API_KEY)) {
        throw 'Set TSUKI_OPENVOICE_API_KEY in this PowerShell session before starting the stack.'
    }

    $discordToken = $env:DISCORD_TOKEN
    if ([string]::IsNullOrWhiteSpace($discordToken)) {
        $discordToken = Read-SecretText 'Discord bot token (not saved to disk)'
    }
    if ([string]::IsNullOrWhiteSpace($discordToken) -or $discordToken -notmatch '^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$') {
        throw 'The Discord token format is invalid.'
    }

    $apiHealthUrl = "http://127.0.0.1:$ApiPort/api/voice/health"
    $apiAlreadyListening = Test-ListeningPort $ApiPort

    if (-not $apiAlreadyListening) {
        $localApiKey = 'local-' + [Guid]::NewGuid().ToString('N')
        $env:TSUKI_API_KEY = $localApiKey
        $env:TSUKI_OPENVOICE_URL = $OpenVoiceUrl
        $env:TSUKI_VOICE_RUNTIME_V2 = 'true'
        $env:TSUKI_VOICE_API_CONTROLLER = 'true'
        $env:TSUKI_SEMANTIC_MEMORY_ENABLED = 'false'
        Remove-Item -LiteralPath $apiStdout, $apiStderr -Force -ErrorAction SilentlyContinue

        $apiProcess = Start-Process -FilePath 'dotnet' -ArgumentList @(
            $apiDll,
            '--urls',
            "http://127.0.0.1:$ApiPort"
        ) -WorkingDirectory $apiRoot -RedirectStandardOutput $apiStdout -RedirectStandardError $apiStderr -WindowStyle Hidden -PassThru
        $startedApi = $true
    }
    elseif ([string]::IsNullOrWhiteSpace($localApiKey)) {
        throw "Port $ApiPort is already in use, but TSUKI_API_KEY is not set for the existing API process. Stop that API or export its key before retrying."
    }

    if (-not (Wait-ApiHealth $apiHealthUrl $localApiKey)) {
        if (Test-Path -LiteralPath $apiStderr) {
            Get-Content -LiteralPath $apiStderr -Tail 40
        }
        throw 'Tsuki API did not become healthy.'
    }

    $env:DISCORD_TOKEN = $discordToken
    $env:CSHARP_API_URL = "http://127.0.0.1:$ApiPort"
    $env:CSHARP_API_KEY = $localApiKey
    $env:BRIDGE_HTTP_PORT = [string]$BridgePort
    Remove-Item -LiteralPath $bridgeStdout, $bridgeStderr -Force -ErrorAction SilentlyContinue

    $bridgeProcess = Start-Process -FilePath 'node' -ArgumentList @('index.js') -WorkingDirectory $bridgeRoot -RedirectStandardOutput $bridgeStdout -RedirectStandardError $bridgeStderr -WindowStyle Hidden -PassThru

    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Seconds 1
        if ($bridgeProcess.HasExited) {
            if (Test-Path -LiteralPath $bridgeStderr) { Get-Content -LiteralPath $bridgeStderr -Tail 60 }
            if (Test-Path -LiteralPath $bridgeStdout) { Get-Content -LiteralPath $bridgeStdout -Tail 60 }
            throw "Discord bridge exited with code $($bridgeProcess.ExitCode)."
        }
        $bridgeReady = Test-ListeningPort $BridgePort
    } while (-not $bridgeReady -and (Get-Date) -lt $deadline)

    if (-not $bridgeReady) {
        throw "Discord bridge is running but its local HTTP port $BridgePort did not open within 30 seconds. Check $bridgeStdout and $bridgeStderr."
    }

    Write-Output "Tsuki API: healthy at http://127.0.0.1:$ApiPort"
    Write-Output "Discord bridge: running (PID $($bridgeProcess.Id))"
    Write-Output "Bridge HTTP: http://127.0.0.1:$BridgePort/play-tts"
    Write-Output "Logs: $bridgeStdout and $bridgeStderr"
}
catch {
    if ($bridgeProcess -and -not $bridgeProcess.HasExited) {
        Stop-Process -Id $bridgeProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if ($startedApi -and $apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    Remove-Item Env:DISCORD_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:CSHARP_API_URL -ErrorAction SilentlyContinue
    Remove-Item Env:CSHARP_API_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:BRIDGE_HTTP_PORT -ErrorAction SilentlyContinue
    if ($startedApi) {
        Remove-Item Env:TSUKI_API_KEY -ErrorAction SilentlyContinue
        Remove-Item Env:TSUKI_OPENVOICE_URL -ErrorAction SilentlyContinue
        Remove-Item Env:TSUKI_VOICE_RUNTIME_V2 -ErrorAction SilentlyContinue
        Remove-Item Env:TSUKI_VOICE_API_CONTROLLER -ErrorAction SilentlyContinue
        Remove-Item Env:TSUKI_SEMANTIC_MEMORY_ENABLED -ErrorAction SilentlyContinue
    }
}
