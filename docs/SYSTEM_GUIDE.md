# RoniAT System Guide

Last updated: 2026-06-25

## What RoniAT Does

RoniAT is a trading automation and control system.

It can:

- Listen to WhatsApp trading signals.
- Validate signals against risk rules.
- Send market orders to a selected broker.
- Manage open positions from a dashboard.
- Show a candlestick chart with active position levels.
- Show open orders, open positions, closed positions, and audit logs.
- Run from desktop or mobile browser.
- Work with Paper, IBKR, and Tastytrade broker modes.

The system is built so the trading engine is the only service that talks to brokers. The dashboard and WhatsApp listener send requests to the trading engine.

## Project Structure

```text
apps/
  trading-dashboard/       Vue dashboard and mobile PWA
  whatsapp-listener/       WhatsApp signal listener

services/
  trading-engine/          .NET trading engine, broker adapters, SQLite DB

docs/
  SYSTEM_GUIDE.md          This guide
  VPS_SETUP_AND_PROJECT_STATE.md
  signal-contract.md

scripts/
  start-roniat.local.ps1   Local startup script
  open-public-port-3001.ps1
```

## Diagrams

Mermaid diagrams are available here:

```text
docs/roniat-architecture.mmd
docs/system-managed-protection-sequence.mmd
```

## Main Runtime Files

These files hold important runtime data:

```text
services/trading-engine/storage/roniat.db
services/trading-engine/storage/risk-settings.json
services/trading-engine/storage/broker-settings.json
services/trading-engine/storage/daily-performance.json
```

`roniat.db` is the SQLite database. It contains signals, orders, positions, executions, closed positions, audit logs, and broker events.

## How To Start The System

From the project root:

```powershell
.\scripts\start-roniat.local.ps1 -RestartTradingEngine -WithWhatsApp
```

Public dashboard mode:

```powershell
.\scripts\start-roniat.local.ps1 -RestartTradingEngine -WithWhatsApp -PublicDashboard
```

In public dashboard mode, the .NET trading engine serves both API and dashboard on port `3001`.

## Main URLs

Development dashboard:

```text
http://localhost:5173
```

Production/public dashboard:

```text
http://localhost:3001
```

Health check:

```text
GET /health
```

Realtime hub:

```text
/hubs/trading
```

## Dashboard Login

Dashboard authentication is controlled by:

```text
services/trading-engine/appsettings.json
```

or by environment variables:

```powershell
$env:DashboardAuth__Username = "admin"
$env:DashboardAuth__Password = "your-password"
```

The browser stores a temporary login token. If the token expires, the dashboard should return to login.

## Dashboard Tabs

### Chart

The main trading screen.

It shows:

- Candlestick chart.
- Active position for the selected symbol.
- Average entry line.
- TP/SL lines.
- Managed TP/SL lines if system-managed protection is enabled.
- Buy Market / Sell Market controls.
- Close / Flatten / Cancel controls.
- Daily P&L.

The chart supports:

- Timeframe selection.
- Symbol selection.
- Dragging TP/SL lines.
- Optional confirmation before chart orders.

### Trade

Manual signal-style trade form.

This uses the same validation pipeline as WhatsApp signals.

### Signals

Shows received signals and their status.

Examples:

- accepted
- rejected_by_risk
- broker_blocked
- market_order_with_signal_protection_sent

### Orders

Shows orders created by the system.

Important order types:

- `ibkr_market`
- `ibkr_stop_loss`
- `ibkr_take_profit`
- `system_stop_loss`
- `system_take_profit`
- `tastytrade_market`
- `paper_market`

System-managed protection orders are marked as `System Managed`.

### Positions

Shows:

- Open positions.
- Closed positions.
- Day filter for closed positions.
- P&L totals.
- MaxProfit / MaxLoss for active positions.

### Audit

Shows system events.

Use this tab to understand:

- why a trade was rejected
- who opened/closed a trade
- why a protection order was updated
- if a failsafe or unmanaged guard acted

### Settings

Controls:

- Broker mode.
- IBKR settings.
- Tastytrade settings.
- Risk settings.
- Auto trading.
- Test mode.
- Max loss per trade.
- Max daily loss.
- Chart market TP/SL distance.
- Close unmanaged broker positions.
- System Managed Protection.
- Stop-loss failsafe.

## Signal Flow

WhatsApp message enters the listener.

The listener parses the message into the canonical signal shape:

```json
{
  "type": "entry",
  "direction": "SHORT",
  "contracts": 7,
  "stop_loss": 30755.5,
  "take_profit_1": 30709.5,
  "take_profit_2": 30686.25,
  "entry_price": 30736.25,
  "symbol": "MNQ1!"
}
```

The listener sends it to:

```text
POST /api/signals
```

The trading engine:

1. Saves the signal.
2. Validates the signal shape.
3. Runs risk checks.
4. Checks current price if needed.
5. Converts the signal to a market order.
6. Sends the order to the selected broker.
7. Creates local orders/position records.
8. Broadcasts a realtime dashboard update.

## Market Button Flow

The chart Buy Market / Sell Market buttons send:

```text
POST /api/market-orders
```

They use:

- selected chart symbol
- selected quantity
- latest chart price as reference price
- `attach_protection = true`
- `protection_distance = chart_market_protection_distance_points`

If System Managed Protection is enabled, TP/SL are created locally as system-managed orders.

## Reverse Behavior

If there is an existing position in the opposite direction, chart market orders now reverse automatically.

Example:

- Current position: `LONG 1`
- User clicks: `Sell Market`, quantity `1`
- Broker order sent: `SELL 2`
- Final position: `SHORT 1`
- New managed TP/SL are attached to the new short position

This is necessary because futures positions are netted by the broker.

## Broker Modes

### Paper

Local simulated broker.

Useful for testing the dashboard and risk logic without a real broker.

### IBKR

Interactive Brokers integration.

Main tested production path is IBKR Paper.

Requirements:

- IBKR Gateway or TWS running.
- API enabled.
- API not read-only for order placement.
- Correct host/port/client ID/account.

Common ports:

```text
Paper: 4002
Live:  4001
```

### Tastytrade

Tastytrade integration exists with OAuth and sandbox support.

Sandbox limitations:

- Market orders fill at `$1` in sandbox.
- Real-time market data may not be available in sandbox.
- Some valid symbols can fail in sandbox.

## Risk Settings

Important fields:

```text
enable_auto_trading
test_mode
max_loss_per_trade
max_daily_loss
max_entry_price_deviation_points
chart_market_protection_distance_points
allowed_symbols
ignore_tp2
allow_position_stacking
trading_locked
emergency_stop_active
close_unmanaged_broker_positions
system_managed_protection_enabled
stop_loss_failsafe_enabled
```

Risk settings are persisted in:

```text
services/trading-engine/storage/risk-settings.json
```

## Test Mode

When test mode is enabled:

- Incoming signals ignore the signal TP/SL values.
- The system sends a market order.
- TP/SL are calculated by fixed distance.
- The default distance is 100 points unless configured otherwise.

## System Managed Protection

When enabled:

- The broker receives only the entry market order.
- TP/SL are not submitted to the broker.
- RoniAT creates local orders:
  - `system_stop_loss`
  - `system_take_profit`
- The dashboard shows these as `System Managed`.
- The chart labels them as `Managed SL` and `Managed TP`.
- A background service checks price every second.
- When price reaches TP or SL, RoniAT sends a market close order.

For LONG:

- SL triggers when price is at or below SL.
- TP triggers when price is at or above TP.

For SHORT:

- SL triggers when price is at or above SL.
- TP triggers when price is at or below TP.

Important risk:

System-managed TP/SL are not broker-side orders. If RoniAT, the VPS, IBKR Gateway, internet, or broker API connection goes down, these protections cannot execute until the system returns.

## Broker-Side Protection

When System Managed Protection is disabled, RoniAT tries to submit TP/SL to the broker when supported.

For IBKR this creates broker-side protective orders.

Broker-side protection is safer during system downtime, but can be less flexible and broker-specific.

## Stop-Loss Failsafe

The failsafe is a separate safety layer.

When enabled:

- RoniAT watches open managed positions with an SL.
- If price crosses the SL and the position remains open for the confirmation delay, it sends a forced close.

This protects against cases where an SL was expected to close the position but did not.

## Close Unmanaged Broker Positions

When enabled:

- RoniAT checks broker positions for allowed symbols.
- If it finds a broker position that is not owned by RoniAT, it sends a market close.

This is intended to prevent manual or external positions from staying open on the selected trading symbol.

Important:

- It only applies to configured/allowed symbols.
- The system has a tracker to avoid closing positions RoniAT just opened before the DB sync completes.

## Daily P&L

Daily P&L is based on closed positions and realized P&L tracked by the system.

It is shown on the chart and in closed positions totals.

For broker-side closes, the system may infer exit price from broker sync, execution data, or latest market data depending on availability.

## Mobile Access

Recommended production approach:

- Run RoniAT on a Windows VPS.
- Expose dashboard/API on port `3001`.
- Use a real domain.
- Use Cloudflare Free DNS.
- Use HTTPS through Cloudflare, Caddy, or Cloudflare Tunnel.

Avoid exposing raw HTTP permanently.

## Main API Endpoints

Authentication:

```text
POST /api/auth/login
POST /api/auth/logout
```

Signals and trades:

```text
POST /api/signals
POST /api/manual-trades
POST /api/market-orders
GET  /api/signals
GET  /api/orders
GET  /api/positions
GET  /api/positions/closed
GET  /api/executions
```

Market data:

```text
GET  /api/market-data/candles
POST /api/market-data/stream
```

Broker:

```text
GET  /api/broker
GET  /api/broker/settings
PUT  /api/broker/settings
POST /api/broker/test-connection
POST /api/broker/orders/cancel-working
POST /api/broker/positions/close
POST /api/broker/flatten
PUT  /api/broker/protection
```

Risk and safety:

```text
GET  /api/risk/settings
PUT  /api/risk/settings
POST /api/safety/lock
POST /api/safety/emergency-stop
POST /api/safety/resume
```

Audit/performance:

```text
GET /api/audit-logs
GET /api/performance/daily
```

## Operational Checklist

Before trading:

1. IBKR Gateway is running and logged in.
2. Broker connection test passes.
3. Correct broker mode is selected.
4. Paper/Live mode is correct.
5. Read-only is off if orders should be sent.
6. Allowed symbols are correct.
7. Max loss per trade is correct.
8. Max daily loss is correct.
9. System Managed Protection setting is intentional.
10. Close Unmanaged Broker Positions setting is intentional.
11. Dashboard shows realtime chart data.
12. Test with small size first.

After a restart:

1. Confirm Trading Engine is running.
2. Confirm WhatsApp listener is running.
3. Confirm dashboard login works.
4. Confirm broker is connected.
5. Confirm open positions and working orders match expectations.

## Files With Secrets

Treat these as sensitive:

```text
services/trading-engine/appsettings.json
services/trading-engine/storage/broker-settings.json
services/trading-engine/storage/risk-settings.json
services/trading-engine/storage/roniat.db
```

Also protect WhatsApp session/auth files if present.

Deployment ZIP packages can contain secrets. Delete them after transfer/extraction if not needed.

## Known Warnings

The dashboard build may show Rolldown warnings from `@microsoft/signalr` about `/*#__PURE__*/` annotations.

These warnings are from a dependency and do not currently block the build.

## Recommended Next Improvements

- Run Trading Engine as a Windows Service or scheduled startup task.
- Run WhatsApp listener as a managed background process.
- Add automatic backups for SQLite DB and settings.
- Add HTTPS for mobile access.
- Add clear production/paper mode indicator in dashboard header.
- Add a health dashboard for IBKR Gateway, WhatsApp listener, and market data freshness.
