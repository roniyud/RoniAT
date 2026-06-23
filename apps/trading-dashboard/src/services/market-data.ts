import type { CandlestickData, Time } from 'lightweight-charts'

const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/+$/, '')

export type Timeframe = '1m' | '5m' | '15m' | '1h'

export const timeframes: Timeframe[] = ['1m', '5m', '15m', '1h']

type CandleResponse = {
  time: number
  open: number
  high: number
  low: number
  close: number
}

export async function getCandles(symbol: string, timeframe: Timeframe): Promise<CandlestickData[]> {
  const params = new URLSearchParams({
    symbol,
    timeframe,
  })
  const response = await fetch(`${API_BASE_URL}/api/market-data/candles?${params.toString()}`, {
    headers: {
      Accept: 'application/json',
    },
  })

  if (!response.ok) {
    throw new Error(`HTTP ${response.status} from market data API`)
  }

  const candles = await response.json() as CandleResponse[]

  return candles.map((candle) => ({
    time: candle.time as Time,
    open: candle.open,
    high: candle.high,
    low: candle.low,
    close: candle.close,
  }))
}
