import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'

export type TradingUpdate = {
  event_type: string
  symbol?: string | null
  occurred_at: string
}

export type MarketTick = {
  symbol: string
  price: number
  time: number
  source: string
}

export type RealtimeStatus = 'connecting' | 'connected' | 'disconnected'

const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/+$/, '')

export function createTradingRealtimeClient(
  onUpdate: (update: TradingUpdate) => void,
  onStatusChange: (status: RealtimeStatus) => void,
  onMarketTick?: (tick: MarketTick) => void,
) {
  const connection = new HubConnectionBuilder()
    .withUrl(`${API_BASE_URL}/hubs/trading`)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()

  connection.on('trading.updated', onUpdate)
  if (onMarketTick) {
    connection.on('market.tick', onMarketTick)
  }
  connection.onreconnecting(() => onStatusChange('connecting'))
  connection.onreconnected(() => onStatusChange('connected'))
  connection.onclose(() => onStatusChange('disconnected'))

  return {
    async start() {
      if (connection.state !== HubConnectionState.Disconnected) return
      onStatusChange('connecting')
      await connection.start()
      onStatusChange('connected')
    },
    async stop() {
      if (connection.state === HubConnectionState.Disconnected) return
      await connection.stop()
      onStatusChange('disconnected')
    },
  }
}
