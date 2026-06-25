import type { CandlestickData, Time } from 'lightweight-charts'
import { getAuthToken } from './api'

const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/+$/, '')

export type Timeframe = '1m' | '5m' | '15m' | '1h'

export const timeframes: Timeframe[] = ['1m', '5m', '15m', '1h']

export type MarketDataResult = {
  candles: CandlestickData[]
  source: 'ibkr' | 'tastytrade' | 'fallback' | 'unknown'
  warning: string
}

type CandleResponse = {
  time: number
  open: number
  high: number
  low: number
  close: number
}

export async function getCandles(symbol: string, timeframe: Timeframe): Promise<MarketDataResult> {
  const params = new URLSearchParams({
    symbol,
    timeframe,
  })
  const controller = new AbortController()
  const timeoutId = window.setTimeout(() => controller.abort(), 12000)
  let response: Response

  try {
    response = await fetch(`${API_BASE_URL}/api/market-data/candles?${params.toString()}`, {
      headers: {
        Accept: 'application/json',
        ...authHeaders(),
      },
      signal: controller.signal,
    })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new Error('Market data request timed out')
    }

    throw error
  } finally {
    window.clearTimeout(timeoutId)
  }

  if (!response.ok) {
    throw new Error(await readMarketDataError(response))
  }

  const candles = await response.json() as CandleResponse[]

  const source = normalizeMarketDataSource(response.headers.get('X-Market-Data-Source'))
  const warning = response.headers.get('X-Market-Data-Warning') ?? ''

  return {
    candles: candles.map((candle) => ({
      time: candle.time as Time,
      open: candle.open,
      high: candle.high,
      low: candle.low,
      close: candle.close,
    })),
    source,
    warning,
  }
}

export async function startMarketDataStream(symbol: string): Promise<void> {
  const controller = new AbortController()
  const timeoutId = window.setTimeout(() => controller.abort(), 6000)

  try {
    const response = await fetch(`${API_BASE_URL}/api/market-data/stream`, {
      method: 'POST',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
        ...authHeaders(),
      },
      body: JSON.stringify({ symbol }),
      signal: controller.signal,
    })

    if (!response.ok) {
      throw new Error(`HTTP ${response.status} from market data stream API`)
    }
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new Error('Market data stream request timed out')
    }

    throw error
  } finally {
    window.clearTimeout(timeoutId)
  }
}

function authHeaders(): Record<string, string> {
  const token = getAuthToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

function normalizeMarketDataSource(value: string | null): MarketDataResult['source'] {
  if (value === 'ibkr' || value === 'tastytrade' || value === 'fallback') return value
  return 'unknown'
}

async function readMarketDataError(response: Response) {
  try {
    const payload = await response.json() as Record<string, unknown>
    const detail = typeof payload.detail === 'string' ? payload.detail : ''
    const title = typeof payload.title === 'string' ? payload.title : ''
    if (detail && title) return `${title}: ${detail}`
    if (detail) return detail
    if (title) return title
  } catch {
    // The API normally returns problem+json, but keep a readable fallback for proxies.
  }

  return `HTTP ${response.status} from market data API`
}
