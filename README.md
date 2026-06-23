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

The current configured broker mode is `Paper`, backed by `PaperBrokerAdapter`. Legacy `/api/paper/...` aliases are still available during development.

Realtime updates are available through SignalR:

```text
/hubs/trading
```

The dashboard listens for `trading.updated` and refreshes immediately, with polling kept as a fallback.
