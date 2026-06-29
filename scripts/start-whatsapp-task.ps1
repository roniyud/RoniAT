param(
    [string]$TradingEngineUrl = "http://localhost:3001",
    [string]$Username = "",
    [string]$Password = "",
    [switch]$RestartExisting
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$WhatsAppDir = Join-Path $Root "apps\whatsapp-listener"

function Stop-WhatsAppListener {
    Get-CimInstance Win32_Process |
        Where-Object {
            $_.CommandLine -and (
                $_.CommandLine -like "*apps\whatsapp-listener*" -or
                $_.CommandLine -like "*apps/whatsapp-listener*" -or
                $_.CommandLine -like "*whatsapp-listener*" -or
                $_.CommandLine -like "*index.js*"
            )
        } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
}

if (-not (Test-Path (Join-Path $WhatsAppDir "index.js"))) {
    throw "WhatsApp listener was not found at $WhatsAppDir"
}

if ($RestartExisting) {
    Stop-WhatsAppListener
    Start-Sleep -Seconds 2
}

if ([string]::IsNullOrWhiteSpace($Username)) {
    $Username = [Environment]::GetEnvironmentVariable("DashboardAuth__Username", "Machine")
}

if ([string]::IsNullOrWhiteSpace($Password)) {
    $Password = [Environment]::GetEnvironmentVariable("DashboardAuth__Password", "Machine")
}

if ([string]::IsNullOrWhiteSpace($Username)) {
    $Username = "admin"
}

if ([string]::IsNullOrWhiteSpace($Password)) {
    throw "TRADING_ENGINE_PASSWORD is empty. Set DashboardAuth__Password at Machine level or pass -Password."
}

$env:TRADING_ENGINE_URL = $TradingEngineUrl
$env:TRADING_ENGINE_USERNAME = $Username
$env:TRADING_ENGINE_PASSWORD = $Password

Write-Host "Starting RoniAT WhatsApp Listener" -ForegroundColor Cyan
Write-Host "Directory: $WhatsAppDir"
Write-Host "Trading Engine: $TradingEngineUrl"
Write-Host "Username: $Username"
Write-Host ""

Set-Location -LiteralPath $WhatsAppDir
npm start
