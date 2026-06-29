param(
    # Windows service that runs the published Trading Engine.
    [string]$ServiceName = "RoniAT-TradingEngine",

    # Public Cloudflare health URL to test after the service starts.
    [string]$PublicHealthUrl = "https://roniyud.com/health",

    # Use only when the frontend did not change and you want to save time.
    [switch]$SkipDashboardBuild,

    # Use only when you want to publish files without touching the running service.
    [switch]$SkipServiceRestart
)

# Stop immediately on errors so a failed build does not restart an old/broken publish.
$ErrorActionPreference = "Stop"

# Resolve all important paths from the repo root, so the script can be run from any folder.
$Root = Split-Path -Parent $PSScriptRoot
$TradingEngineProject = Join-Path $Root "services\trading-engine\trading-engine.csproj"
$PublishDir = Join-Path $Root "publish\trading-engine"
$ContentRoot = Join-Path $Root "services\trading-engine"
$LocalHealthUrl = "http://localhost:3001/health"

function Write-Step {
    param([string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Test-Admin {
    # Service stop/start usually requires an elevated PowerShell window.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Stop-RoniService {
    param([string]$Name)

    # Stop the service before overwriting published files.
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) {
        Write-Host "Service $Name does not exist yet; skipping stop." -ForegroundColor Yellow
        return
    }

    if ($service.Status -eq "Stopped") {
        Write-Host "Service $Name is already stopped."
        return
    }

    sc.exe stop $Name | Out-Host
    $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

function Start-RoniService {
    param([string]$Name)

    # Start the service again after publish and wait until Windows reports Running.
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) {
        throw "Service $Name does not exist. Create it before running this script."
    }

    sc.exe start $Name | Out-Host
    $service.WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
}

function Test-Health {
    param([string]$Url)

    # Health checks confirm the API is reachable locally and through Cloudflare.
    try {
        $response = Invoke-WebRequest -UseBasicParsing $Url -TimeoutSec 15
        Write-Host "$Url -> $($response.StatusCode)" -ForegroundColor Green
    }
    catch {
        Write-Host "$Url -> failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

if (-not (Test-Path $TradingEngineProject)) {
    throw "Trading Engine project not found: $TradingEngineProject"
}

# Warn early if PowerShell is not elevated. Build can work, service restart may fail.
if (-not (Test-Admin)) {
    Write-Host "Warning: service stop/start usually requires PowerShell as Administrator." -ForegroundColor Yellow
}

# Show the exact paths that will be used for this republish.
Write-Step "Republishing RoniAT"
Write-Host "Root:        $Root"
Write-Host "Project:     $TradingEngineProject"
Write-Host "Publish dir: $PublishDir"

if (-not $SkipServiceRestart) {
    # Free port 3001 and prevent the service from using old files while publish runs.
    Write-Step "Stopping $ServiceName"
    Stop-RoniService $ServiceName
}

if (-not $SkipDashboardBuild) {
    # Build the Vue dashboard into apps/trading-dashboard/dist.
    # The Trading Engine serves this dist folder in production.
    Write-Step "Building dashboard"
    npm --prefix (Join-Path $Root "apps\trading-dashboard") run build
}

# Compile and copy the Trading Engine release output into publish/trading-engine.
Write-Step "Publishing Trading Engine"
dotnet publish $TradingEngineProject -c Release -o $PublishDir

if (-not $SkipServiceRestart) {
    # Keep the service command correct even if it was edited manually.
    # contentRoot points the service to the real storage/config folder.
    Write-Step "Ensuring service command"
    $binPath = "`"$PublishDir\trading-engine.exe`" --contentRoot `"$ContentRoot`" --urls http://0.0.0.0:3001"
    sc.exe config $ServiceName binPath= $binPath start= auto | Out-Host

    # Bring production back online.
    Write-Step "Starting $ServiceName"
    Start-RoniService $ServiceName

    Start-Sleep -Seconds 3

    # Verify both origin and public Cloudflare routing.
    Write-Step "Health checks"
    Test-Health $LocalHealthUrl
    if (-not [string]::IsNullOrWhiteSpace($PublicHealthUrl)) {
        Test-Health $PublicHealthUrl
    }
}

Write-Step "Done"
