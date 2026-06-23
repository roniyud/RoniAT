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

The dashboard Chart tab currently uses generated candle data for UI development. Real market data will be wired in when the broker or market-data adapter is added.

Paper trading is enabled in the trading engine. Each accepted entry signal creates a filled paper entry order, working paper stop-loss/take-profit orders, a paper execution, and an open paper position in SQLite.

Paper trading control endpoints:

```text
POST /api/paper/orders/cancel-working
POST /api/paper/positions/close
POST /api/paper/flatten
```

Realtime updates are available through SignalR:

```text
/hubs/trading
```

The dashboard listens for `trading.updated` and refreshes immediately, with polling kept as a fallback.
