export type ApiState = 'loading' | 'online' | 'offline'

export type TradingSignal = {
  id: number
  type: 'entry'
  direction: 'LONG' | 'SHORT'
  contracts: number
  stop_loss: number
  take_profit_1: number
  take_profit_2: number
  entry_price: number
  symbol: string
  status: string
  created_at: string
}

export type TradingSignalRequest = {
  type: 'entry'
  direction: 'LONG' | 'SHORT'
  contracts: number
  stop_loss: number
  take_profit_1: number
  take_profit_2: number
  entry_price: number
  symbol: string
}

export type MarketOrderRequest = {
  symbol: string
  direction: 'LONG' | 'SHORT'
  contracts: number
  reference_price?: number | null
  attach_protection?: boolean
  protection_distance?: number | null
}

export type MarketOrderResponse = {
  ok: boolean
  status: string
  message: string
  order?: OrderRecord | null
  position?: PositionRecord | null
}

export type ProtectionUpdateRequest = {
  symbol: string
  stop_loss?: number | null
  take_profit?: number | null
}

export type ProtectionUpdateResponse = {
  ok: boolean
  status: string
  message: string
  position?: PositionRecord | null
}

export type OrderRecord = {
  id: number
  signalId?: number | null
  brokerOrderId?: string | null
  symbol: string
  direction: string
  orderType: string
  quantity: number
  price?: number | null
  stopPrice?: number | null
  status: string
  createdAt?: string | null
  updatedAt?: string | null
}

export type PositionRecord = {
  id: number
  symbol: string
  direction: string
  quantity: number
  averagePrice: number
  stopLoss?: number | null
  takeProfit1?: number | null
  takeProfit2?: number | null
  openedAt?: string | null
  updatedAt?: string | null
}

export type ClosedPositionRecord = {
  id: number
  symbol: string
  direction: string
  quantity: number
  averagePrice: number
  exitPrice?: number | null
  stopLoss?: number | null
  takeProfit1?: number | null
  takeProfit2?: number | null
  realizedPnl?: number | null
  closeReason: string
  openedAt: string
  closedAt: string
}

export type PaperActionResult = {
  cancelledOrders: number
  closedPositions: number
}

export type DailyPerformance = {
  date: string
  realized_pnl: number
  closed_trades: number
}

export type BrokerMode = {
  mode: string
  environment: string
  configured: boolean
  enabled: boolean
  connected: boolean
  read_only: boolean
  message: string
}

export type BrokerConnectionTestResult = {
  ok: boolean
  mode: string
  environment: string
  host: string
  port: number
  handshake_ok: boolean
  account_verified: boolean
  managed_accounts: string[]
  selected_account?: string | null
  server_version?: number | null
  message: string
  tested_at: string
}

export type IBKRSettings = {
  host: string
  port: number
  client_id: number
  account: string
  enabled: boolean
  read_only: boolean
}

export type BrokerSettings = {
  mode: 'Paper' | 'IBKR'
  ibkr_environment: 'Paper' | 'Live'
  ibkr_paper: IBKRSettings
  ibkr_live: IBKRSettings
}

export type RiskSettings = {
  max_contracts_per_signal: number
  max_loss_per_trade: number
  max_daily_loss: number
  max_entry_price_deviation_points: number
  chart_market_protection_distance_points: number
  allowed_symbols: string[]
  test_mode: boolean
  ignore_tp2: boolean
  enable_auto_trading: boolean
  reject_duplicate_signals: boolean
  duplicate_window_seconds: number
  allow_position_stacking: boolean
  trading_locked: boolean
  emergency_stop_active: boolean
  stop_loss_failsafe_enabled: boolean
  stop_loss_failsafe_poll_seconds: number
  stop_loss_failsafe_confirm_seconds: number
  stop_loss_failsafe_cooldown_seconds: number
}

export type AuditLogRecord = {
  id: number
  action: string
  details: string
  created_at: string
}
