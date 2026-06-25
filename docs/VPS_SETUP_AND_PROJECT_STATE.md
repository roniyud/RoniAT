# RoniAT VPS Setup And Project State

Last updated: 2026-06-25

## Current Project State

RoniAT is a local trading automation system with:

- .NET trading engine: `services/trading-engine`
- Vue dashboard: `apps/trading-dashboard`
- WhatsApp listener: `apps/whatsapp-listener`
- SQLite database: `services/trading-engine/storage/roniat.db`
- Runtime settings:
  - `services/trading-engine/storage/risk-settings.json`
  - `services/trading-engine/storage/broker-settings.json`
  - `services/trading-engine/storage/daily-performance.json`

The project is designed to run from any root path, for example:

- `D:\RONI\RoniAT`
- `C:\RONI\RoniAT`

The startup script uses its own location to resolve the project root.

## Main Startup Command

From the project root:

```powershell
.\scripts\start-roniat.local.ps1 -RestartTradingEngine -WithWhatsApp
```

For public dashboard mode on port `3001`:

```powershell
.\scripts\start-roniat.local.ps1 -RestartTradingEngine -WithWhatsApp -PublicDashboard
```

The dashboard/API production port is currently `3001`.

## VPS Recommended Setup

Recommended Windows VPS baseline:

- Windows Server 2022/2025 Datacenter is OK.
- 2 vCPU / 4GB RAM can work as a starting point.
- 4 vCPU / 8GB RAM is safer for IBKR Gateway plus WhatsApp listener.
- 60GB+ SSD/NVMe.
- Static public IP.

Preferred zone:

- New York / US East for US futures and IBKR latency.
- London is a good compromise for access from Israel and US market connectivity.

## Required Software On VPS

Install:

- Git is optional; not required if deploying by ZIP.
- .NET 8 SDK or Runtime.
- Node.js LTS.
- IBKR Gateway.
- Browser runtime needed by WhatsApp listener, if applicable.
- Optional but recommended: Caddy or Cloudflare Tunnel for HTTPS.

## Package Deployment Without Git

Stop local running services before packaging, so SQLite is clean:

```powershell
Get-Process trading-engine -ErrorAction SilentlyContinue | Stop-Process -Force
```

Create a full package including the database:

```powershell
cd D:\RONI

$source = "D:\RONI\RoniAT"
$target = "D:\RONI\RoniAT-full-vps-package.zip"

if (Test-Path $target) {
  Remove-Item $target -Force
}

$excludePatterns = @(
  "\\.git\\",
  "\\node_modules\\",
  "\\bin\\",
  "\\obj\\",
  "\\.build-check\\",
  "\\roniat.db-shm$",
  "\\roniat.db-wal$",
  "\\*.log$"
)

$files = Get-ChildItem $source -Recurse -File | Where-Object {
  $path = $_.FullName
  -not ($excludePatterns | Where-Object { $path -match $_ })
}

Compress-Archive -Path $files.FullName -DestinationPath $target -Force

Write-Host "Created full package with DB: $target"
```

This includes:

- `roniat.db`
- `risk-settings.json`
- `broker-settings.json`
- `daily-performance.json`

It excludes:

- `.git`
- `node_modules`
- `.NET bin/obj`
- temporary SQLite `db-wal/db-shm`
- logs

## Extract On VPS

If the VPS has no `D:` drive, use `C:`.

```powershell
New-Item -ItemType Directory -Force C:\RONI
Expand-Archive C:\Path\To\RoniAT-full-vps-package.zip -DestinationPath C:\RONI\RoniAT -Force
cd C:\RONI\RoniAT
```

Install/build dashboard:

```powershell
cd C:\RONI\RoniAT\apps\trading-dashboard
npm install
npm run build
```

Build engine:

```powershell
cd C:\RONI\RoniAT\services\trading-engine
dotnet restore
dotnet build
```

Run:

```powershell
cd C:\RONI\RoniAT
.\scripts\start-roniat.local.ps1 -RestartTradingEngine -WithWhatsApp
```

## Open Public Port

If exposing port `3001` directly:

```powershell
.\scripts\open-public-port-3001.ps1
```

Prefer HTTPS through Cloudflare/Caddy instead of exposing raw HTTP long term.

## Current Trading Behavior

Supported broker modes:

- Paper
- IBKR
- Tastytrade

IBKR Paper is currently the main tested broker path.

Important risk settings:

- `enable_auto_trading`
- `close_unmanaged_broker_positions`
- `system_managed_protection_enabled`
- `chart_market_protection_distance_points`
- `max_loss_per_trade`
- `max_daily_loss`

## System Managed Protection

When `System Managed Protection` is enabled:

- Entry order is sent to the broker as a plain market order.
- TP/SL are not submitted to the broker.
- RoniAT creates local working orders:
  - `system_stop_loss`
  - `system_take_profit`
- These appear in Working Orders with `System Managed`.
- Chart lines appear as `Managed SL` and `Managed TP`.
- Lines can be dragged to update levels.
- Cancel removes local managed protection.
- A background service checks price every second.
- If price reaches TP/SL, RoniAT sends a market close order.

Critical operational risk:

If RoniAT, the VPS, internet, IBKR Gateway, or broker API connection is down, system-managed TP/SL will not execute because they are not broker-side orders.

## Recent Fixes To Remember

- Added `SystemManagedProtectionService`.
- Added `SystemManagedProtectionOrders`.
- Added `SystemOwnedPositionTracker` to avoid the unmanaged guard closing positions opened by RoniAT before DB sync completes.
- Fixed reverse behavior for chart market buttons:
  - If current position is LONG 1 and user clicks Sell Market 1, server sends SELL 2 so final position becomes SHORT 1.
  - Managed TP/SL are then attached to the new reversed position.

## IBKR Notes

- Use IB Gateway instead of full TWS on VPS when possible.
- Gateway must remain logged in.
- Avoid logging into the same IBKR username elsewhere if it disconnects Gateway.
- API must not be read-only if live/paper orders are needed.
- Paper default socket is usually `4002`.
- Live default socket is usually `4001`.
- Use different Client IDs for different API clients.

## Mobile Access

Recommended:

- Domain managed by Cloudflare Free.
- `app.yourdomain.com` points to VPS.
- Use HTTPS.

Avoid relying on free `.tk` domains for production trading access.

## After VPS Restart

Verify:

1. IBKR Gateway is running and logged in.
2. Trading Engine is running.
3. Dashboard opens.
4. WhatsApp listener is running.
5. Broker connection test passes.
6. `System Managed Protection` setting is as intended.
7. Test with Paper before any live usage.

## Files That May Contain Secrets

Handle these carefully:

- `services/trading-engine/appsettings.json`
- `services/trading-engine/storage/broker-settings.json`
- `services/trading-engine/storage/risk-settings.json`
- WhatsApp session/auth folders, if present.
- Any OAuth tokens for Tastytrade.

Delete deployment ZIP files after extracting if they contain secrets.
