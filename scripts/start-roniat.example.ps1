param(
    [switch]$WithWhatsApp,
    [switch]$RestartTradingEngine,
    [switch]$PublicDashboard,
    [string]$ComputerIp = ""
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot

$DashboardUsername = "admin"
$DashboardPassword = "CHANGE_ME"
$DashboardHost = "0.0.0.0"
$ApiPort = 3001
$DashboardPort = 5173

function Get-RoniComputerIp {
    if (-not [string]::IsNullOrWhiteSpace($ComputerIp)) {
        return $ComputerIp
    }

    $candidate = Get-NetIPConfiguration |
        Where-Object {
            $_.IPv4Address -and
            $_.IPv4DefaultGateway -and
            $_.IPv4Address.IPAddress -notlike "127.*" -and
            $_.IPv4Address.IPAddress -notlike "169.254.*" -and
            $_.InterfaceAlias -notmatch "vEthernet|Tailscale|Loopback|Docker|WSL|WireGuard|Fortinet|VPN|TAP"
        } |
        Select-Object -First 1

    if ($candidate) {
        return $candidate.IPv4Address.IPAddress
    }

    $fallback = Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object {
            $_.IPAddress -notlike "127.*" -and
            $_.IPAddress -notlike "169.254.*" -and
            $_.InterfaceAlias -notmatch "vEthernet|Tailscale|Loopback|Docker|WSL|WireGuard|Fortinet|VPN|TAP"
        } |
        Select-Object -First 1

    if ($fallback) {
        return $fallback.IPAddress
    }

    return "localhost"
}

$ResolvedComputerIp = Get-RoniComputerIp
$TradingEngineUrl = "http://$ResolvedComputerIp`:$ApiPort"
$DashboardUrl = "http://$ResolvedComputerIp`:$DashboardPort"
$PublicDashboardUrl = "http://$ResolvedComputerIp`:$ApiPort"

function Start-RoniWindow {
    param(
        [string]$Title,
        [string]$WorkDir,
        [string]$Command
    )

    $script = @"
`$Host.UI.RawUI.WindowTitle = '$Title'
Set-Location -LiteralPath '$WorkDir'
$Command
"@

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    Start-Process powershell.exe -ArgumentList @("-NoExit", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encoded)
}

function Stop-RoniProcessByPattern {
    param([string[]]$Patterns)

    Get-CimInstance Win32_Process |
        Where-Object {
            if (-not $_.CommandLine) { return $false }
            foreach ($pattern in $Patterns) {
                if ($_.CommandLine -like "*$pattern*") { return $true }
            }
            return $false
        } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
}

function Stop-RoniPortOwner {
    param([int]$Port)

    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        ForEach-Object {
            Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue
        }
}

if ($RestartTradingEngine) {
    Stop-RoniPortOwner $ApiPort
    Get-Process trading-engine -ErrorAction SilentlyContinue | Stop-Process -Force
    Stop-RoniProcessByPattern @("trading-engine.csproj", "services\trading-engine", "services/trading-engine", "bin\Debug\net8.0\trading-engine.exe")
    Start-Sleep -Seconds 2
}

Stop-RoniPortOwner $DashboardPort
Stop-RoniProcessByPattern @("apps\trading-dashboard", "apps/trading-dashboard", "vite.js")
if ($WithWhatsApp) {
    Stop-RoniProcessByPattern @("apps\whatsapp-listener", "apps/whatsapp-listener", "index.js")
}

if ($PublicDashboard) {
    Push-Location "$Root\apps\trading-dashboard"
    try {
        $env:VITE_API_BASE_URL = ""
        npm run build
    }
    finally {
        Pop-Location
    }
}

$backendCommand = @"
`$env:DashboardAuth__Username = '$DashboardUsername'
`$env:DashboardAuth__Password = '$DashboardPassword'
`$env:Dashboard__AllowedOrigins__0 = 'http://localhost:$DashboardPort'
`$env:Dashboard__AllowedOrigins__1 = '$DashboardUrl'
`$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --no-launch-profile --project '$Root\services\trading-engine\trading-engine.csproj' --urls 'http://0.0.0.0:$ApiPort'
"@

$dashboardCommand = @"
`$env:VITE_API_BASE_URL = '$TradingEngineUrl'
npm run dev -- --host $DashboardHost --port $DashboardPort --strictPort
"@

Start-RoniWindow -Title "RoniAT Trading Engine" -WorkDir $Root -Command $backendCommand
if (-not $PublicDashboard) {
    Start-Sleep -Seconds 3
    Start-RoniWindow -Title "RoniAT Dashboard" -WorkDir "$Root\apps\trading-dashboard" -Command $dashboardCommand
}

if ($WithWhatsApp) {
    $whatsappCommand = @"
`$env:TRADING_ENGINE_URL = '$TradingEngineUrl'
`$env:TRADING_ENGINE_USERNAME = '$DashboardUsername'
`$env:TRADING_ENGINE_PASSWORD = '$DashboardPassword'
npm start
"@

    Start-RoniWindow -Title "RoniAT WhatsApp Listener" -WorkDir "$Root\apps\whatsapp-listener" -Command $whatsappCommand
}

Write-Host "RoniAT startup launched."
if ($PublicDashboard) {
    Write-Host "Dashboard/API: http://localhost:$ApiPort"
    Write-Host "Dashboard/API from phone on same network: $PublicDashboardUrl"
}
else {
    Write-Host "Dashboard: http://localhost:$DashboardPort"
    Write-Host "Dashboard from phone on same network: $DashboardUrl"
}
Write-Host "API:       $TradingEngineUrl"
Write-Host "Login:     $DashboardUsername"
