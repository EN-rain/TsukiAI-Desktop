<#
.SYNOPSIS
    Imports local desktop TsukiAI data into a server data directory (Docker volume).

.DESCRIPTION
    Copies conversation history and provider state from %APPDATA%\TsukiAI into the
    target directory, and writes a REDACTED settings.json (API keys stripped) so
    secrets stay out of server files. Run this once before first web deployment:

        .\scripts\import-local-data.ps1 -TargetDir .\tsuki-data

    Then set the stripped keys via environment variables in .env / Docker secrets
    instead. The script prints exactly which variables to set.

.PARAMETER TargetDir
    Destination directory that will be mounted as the API container's /data volume.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$TargetDir
)

$ErrorActionPreference = "Stop"

$sourceDir = Join-Path $env:APPDATA "TsukiAI"
if (-not (Test-Path $sourceDir)) {
    throw "Source directory not found: $sourceDir (is TsukiAI desktop installed on this machine?)"
}

New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null

# --- Files copied as-is -------------------------------------------------
$copied = @()
foreach ($name in @("voice_chat_history.json", "chat_history.json", "provider-state.json")) {
    $src = Join-Path $sourceDir $name
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $TargetDir $name) -Force
        $copied += $name
    }
}
Write-Host "Copied: $($copied -join ', ')"

# --- settings.json, with secrets stripped --------------------------------
$secretKeys = @(
    "RemoteInferenceApiKey",
    "CerebrasApiKey",
    "GroqApiKey",
    "GeminiApiKey",
    "GitHubApiKey",
    "MistralApiKey",
    "DeepLApiKey",
    "AssemblyAIApiKey",
    "DiscordBotToken"
)

$stripped = @()
$settingsPath = Join-Path $sourceDir "settings.json"
if (Test-Path $settingsPath) {
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    foreach ($key in $secretKeys) {
        if ($settings.PSObject.Properties[$key] -and -not [string]::IsNullOrWhiteSpace($settings.$key)) {
            $stripped += $key
        }
        if ($settings.PSObject.Properties[$key]) {
            $settings.$key = ""
        }
    }
    $settings | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $TargetDir "settings.json") -Encoding UTF8
    Write-Host "settings.json copied with $($stripped.Count) secret(s) stripped."
}
else {
    Write-Warning "No settings.json found in $sourceDir - skipping."
}

# --- What to set on the server instead -----------------------------------
Write-Host ""
Write-Host "Set these environment variables on the server (in .env next to docker-compose.yml):"
foreach ($key in $stripped) {
    $envName = switch ($key) {
        "RemoteInferenceApiKey" { "TSUKI_REMOTE_INFERENCE_API_KEY" }
        "CerebrasApiKey"        { "TSUKI_CEREBRAS_API_KEY" }
        "GroqApiKey"            { "TSUKI_GROQ_API_KEY" }
        "GeminiApiKey"          { "TSUKI_GEMINI_API_KEY" }
        "GitHubApiKey"          { "TSUKI_GITHUB_API_KEY" }
        "MistralApiKey"         { "TSUKI_MISTRAL_API_KEY" }
        "DeepLApiKey"           { "TSUKI_DEEPL_API_KEY" }
        "AssemblyAIApiKey"      { "TSUKI_ASSEMBLYAI_API_KEY" }
        "DiscordBotToken"       { "TSUKI_DISCORD_BOT_TOKEN" }
    }
    Write-Host "  $envName=<your $key>"
}
Write-Host ""
Write-Host "Do not forget: TSUKI_WEB_PASSWORD=<login password for the web app>"
Write-Host ""
Write-Host "Done. Mount $TargetDir as /data on the api container (see docker-compose.yml)."
