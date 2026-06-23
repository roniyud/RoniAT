# RoniAT

Automated trading assistant workspace.

## Structure

```text
apps/
  whatsapp-listener/   Node.js WhatsApp signal listener
  trading-dashboard/   Vue 3 dashboard and mobile PWA
services/
  trading-engine/      .NET trading engine and broker integration
packages/
  shared-contracts/    Shared schemas used by apps and services
docs/                  Architecture and implementation notes
scripts/               Local automation scripts
```

The WhatsApp listener receives and parses trading signals. The trading engine will be the only service that talks to brokers and persists trading state. The dashboard will connect to the trading engine API for desktop and mobile control.

## Local Run

Start the trading engine:

```powershell
cd D:\RONI\RoniAT\services\trading-engine
dotnet run
```

Start the WhatsApp listener in a second terminal:

```powershell
cd D:\RONI\RoniAT\apps\whatsapp-listener
$env:TRADING_ENGINE_URL='http://localhost:5066'
npm start
```

Start the dashboard in a third terminal:

```powershell
cd D:\RONI\RoniAT\apps\trading-dashboard
npm run dev
```

Dashboard URL:

```text
http://localhost:5173
```

The dashboard Chart tab reads candles from the trading engine:

```text
GET /api/market-data/candles?symbol=MNQ1!&timeframe=5m
```

The current trading engine provider is still mock market data, but it is now isolated behind `IMarketDataProvider` so it can be replaced by IBKR, Rithmic, or another market-data source without changing the dashboard.

Paper trading is enabled in the trading engine. Each accepted entry signal creates a filled paper entry order, working paper stop-loss/take-profit orders, a paper execution, and an open paper position in SQLite.

Broker control endpoints:

```text
GET  /api/broker
POST /api/broker/orders/cancel-working
POST /api/broker/positions/close
POST /api/broker/flatten
```

Audit log endpoint:

```text
GET /api/audit-logs
```

The dashboard Audit tab shows recent risk, signal, and paper broker events so rejected signals and manual actions can be traced from the UI.

Manual paper trade endpoint:

```text
POST /api/manual-trades
```

The dashboard Trade tab submits manual entries through the same validation, risk checks, paper broker adapter, SignalR updates, and audit logging used by WhatsApp signals.

The current configured broker mode is `Paper`, backed by `PaperBrokerAdapter`. Legacy `/api/paper/...` aliases are still available during development.

Broker modes:

```json
"Broker": {
  "Mode": "Paper"
}
```

Supported values are `Paper` and `IBKR`. Runtime broker settings can be changed from the dashboard Settings tab and are persisted locally in `services/trading-engine/storage/broker-settings.json`.

Broker settings endpoints:

```text
GET /api/broker/settings
PUT /api/broker/settings
POST /api/broker/test-connection
```

`IBKR` is currently a read-only adapter: it can verify an IB Gateway/TWS API session and managed account, writes audit/broker events, and blocks live order actions with `broker_blocked` status. It does not place live orders yet.

The connection test opens an IBKR API socket through the local official CSharpAPI project under `D:\RONI\IB\TWS API\source\CSharpClient\client`, waits for the API handshake (`nextValidId`), requests managed accounts, and verifies the configured Paper/Live account when provided. It does not subscribe to data, request positions, or place orders.

When runtime broker mode is `IBKR` and the active IBKR environment is enabled, the trading engine runs a read-only connection monitor every 10 seconds. The monitor retries the IBKR API handshake, updates broker status, broadcasts dashboard refresh events, and writes audit events only when the connection transitions between connected and disconnected states.

IBKR skeleton settings:

```json
"IBKR": {
  "Paper": {
    "Host": "127.0.0.1",
    "Port": 4002,
    "ClientId": 5324,
    "Account": "",
    "Enabled": false,
    "ReadOnly": true
  },
  "Live": {
    "Host": "127.0.0.1",
    "Port": 4001,
    "ClientId": 11,
    "Account": "",
    "Enabled": false,
    "ReadOnly": true
  }
}
```

Risk validation runs before any signal reaches the broker adapter. Current defaults allow only `MNQ1!`, up to 7 contracts per signal, reject duplicate signals inside a short window, and reject new entries while an open position already exists for the same symbol. Rejected signals are still saved with status `rejected_by_risk`, but no broker orders are created.

Risk settings endpoint:

```text
GET /api/risk/settings
PUT /api/risk/settings
```

The dashboard Settings tab can update these controls at runtime. The trading engine persists runtime risk settings locally in `services/trading-engine/storage/risk-settings.json`, which is intentionally ignored by Git.

Safety control endpoints:

```text
POST /api/safety/lock
POST /api/safety/emergency-stop
POST /api/safety/resume
```

The dashboard safety banner exposes Lock, Emergency Stop, and Resume. Lock disables auto trading and rejects new WhatsApp/manual entries. Emergency Stop flattens paper positions, cancels working paper orders, disables auto trading, and keeps trading locked until Resume is confirmed.

Realtime updates are available through SignalR:

```text
/hubs/trading
```

The dashboard listens for `trading.updated` and refreshes immediately, with polling kept as a fallback.
