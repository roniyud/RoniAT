# RoniAT Windows Service and Development Guide

This guide explains how to run RoniAT as a Windows service, how Cloudflare Tunnel fits in, and how to keep developing safely.

## Current Service Setup

The production setup is:

- `RoniAT-TradingEngine` Windows service runs the .NET Trading Engine on port `3001`.
- `cloudflared` Windows service exposes `http://localhost:3001` through Cloudflare Tunnel.
- The dashboard is built into `apps/trading-dashboard/dist` and served by the Trading Engine.
- The development Vite server on `5173` is not needed in production.

Check status:

```powershell
Get-Service RoniAT-TradingEngine, cloudflared
Invoke-WebRequest http://localhost:3001/health
Invoke-WebRequest https://roniyud.com/health
```

## One-Time Code Requirements

The Trading Engine must support Windows Service hosting:

- `Program.cs` must call `builder.Host.UseWindowsService()`.
- `trading-engine.csproj` must reference `Microsoft.Extensions.Hosting.WindowsServices`.
- SQLite relative paths must be normalized to the app content root, not `C:\Windows\System32`.

These changes are already applied in this repo.

## Install or Recreate Trading Engine Service

Run PowerShell as Administrator.

Build the dashboard:

```powershell
cd C:\RONI\RoniAT
npm --prefix .\apps\trading-dashboard run build
```

Publish the Trading Engine:

```powershell
dotnet publish .\services\trading-engine\trading-engine.csproj `
  -c Release `
  -o C:\RONI\RoniAT\publish\trading-engine
```

Set service environment variables:

```powershell
[Environment]::SetEnvironmentVariable("DashboardAuth__Username", "admin", "Machine")
[Environment]::SetEnvironmentVariable("DashboardAuth__Password", "YOUR_REAL_PASSWORD", "Machine")
[Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Machine")
```

Create the service:

```powershell
sc.exe create RoniAT-TradingEngine `
  binPath= "`"C:\RONI\RoniAT\publish\trading-engine\trading-engine.exe`" --contentRoot `"C:\RONI\RoniAT\services\trading-engine`" --urls http://0.0.0.0:3001" `
  start= auto `
  DisplayName= "RoniAT Trading Engine"
```

Start it:

```powershell
sc.exe start RoniAT-TradingEngine
```

Verify:

```powershell
Get-Service RoniAT-TradingEngine
Invoke-WebRequest http://localhost:3001/health
Invoke-WebRequest https://roniyud.com/health
```

## Update Existing Service After Code Changes

If you changed backend code:

```powershell
cd C:\RONI\RoniAT
sc.exe stop RoniAT-TradingEngine

npm --prefix .\apps\trading-dashboard run build

dotnet publish .\services\trading-engine\trading-engine.csproj `
  -c Release `
  -o C:\RONI\RoniAT\publish\trading-engine

sc.exe start RoniAT-TradingEngine
```

If you changed only frontend code:

```powershell
cd C:\RONI\RoniAT
sc.exe stop RoniAT-TradingEngine

npm --prefix .\apps\trading-dashboard run build

dotnet publish .\services\trading-engine\trading-engine.csproj `
  -c Release `
  -o C:\RONI\RoniAT\publish\trading-engine

sc.exe start RoniAT-TradingEngine
```

The publish step is still useful because the service runs from `publish/trading-engine`.

## Development Mode

The service uses port `3001`. Development also uses port `3001`, so stop the service first:

```powershell
sc.exe stop RoniAT-TradingEngine
```

Start the normal development flow:

```powershell
cd C:\RONI\RoniAT
.\scripts\start-roniat.local.ps1 -RestartTradingEngine -WithWhatsApp
```

Use:

- Dashboard dev UI: `http://localhost:5173`
- API: `http://localhost:3001`

When finished developing, stop dev windows/processes and return to service mode:

```powershell
cd C:\RONI\RoniAT
npm --prefix .\apps\trading-dashboard run build

dotnet publish .\services\trading-engine\trading-engine.csproj `
  -c Release `
  -o C:\RONI\RoniAT\publish\trading-engine

sc.exe start RoniAT-TradingEngine
```

## Cloudflare Tunnel

The Cloudflare service should run separately:

```powershell
Get-Service cloudflared
C:\cloudflared\cloudflared.exe tunnel --config C:\cloudflared\config.yml info roniyud-tunnel
```

Expected config:

```yaml
tunnel: roniyud-tunnel
credentials-file: C:\Users\Administrator\.cloudflared\63465b9d-bad4-4864-b76a-91ae1810b0f9.json

ingress:
  - hostname: roniyud.com
    service: http://localhost:3001
  - hostname: www.roniyud.com
    service: http://localhost:3001
  - service: http_status:404
```

If the public site gives `502` or `503`, check:

```powershell
Get-Service RoniAT-TradingEngine, cloudflared
Invoke-WebRequest http://localhost:3001/health
Invoke-WebRequest https://roniyud.com/health
```

## Useful Service Commands

```powershell
Get-Service RoniAT-TradingEngine
sc.exe start RoniAT-TradingEngine
sc.exe stop RoniAT-TradingEngine
sc.exe qc RoniAT-TradingEngine
```

Delete and recreate the service if needed:

```powershell
sc.exe stop RoniAT-TradingEngine
sc.exe delete RoniAT-TradingEngine
```

Then run the create command again.

## Login Notes

The service reads dashboard login from Machine environment variables:

```powershell
[Environment]::GetEnvironmentVariable("DashboardAuth__Username", "Machine")
[Environment]::GetEnvironmentVariable("DashboardAuth__Password", "Machine")
```

After changing these values, restart the service:

```powershell
sc.exe stop RoniAT-TradingEngine
sc.exe start RoniAT-TradingEngine
```

