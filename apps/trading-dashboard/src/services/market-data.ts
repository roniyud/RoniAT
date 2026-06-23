import type { CandlestickData, Time } from 'lightweight-charts'

export type Timeframe = '1m' | '5m' | '15m' | '1h'

const timeframeMinutes: Record<Timeframe, number> = {
  '1m': 1,
  '5m': 5,
  '15m': 15,
  '1h': 60,
}

export const timeframes: Timeframe[] = ['1m', '5m', '15m', '1h']

export function getMockCandles(symbol: string, timeframe: Timeframe): CandlestickData[] {
  const intervalSeconds = timeframeMinutes[timeframe] * 60
  const candleCount = timeframe === '1h' ? 160 : 220
  const nowSeconds = Math.floor(Date.now() / 1000)
  const alignedEnd = nowSeconds - (nowSeconds % intervalSeconds)
  const seed = getSymbolSeed(symbol) + timeframeMinutes[timeframe] * 17
  const basePrice = symbol.toUpperCase().includes('MNQ') ? 30740 : 100 + (seed % 80)

  let close = basePrice
  const candles: CandlestickData[] = []

  for (let index = candleCount - 1; index >= 0; index -= 1) {
    const time = (alignedEnd - index * intervalSeconds) as Time
    const wave = Math.sin((candleCount - index + seed) / 9) * 8
    const drift = Math.cos((candleCount - index + seed) / 17) * 3
    const move = wave * 0.14 + drift * 0.18 + pseudoRandom(seed + index) * 2.6
    const open = close
    close = Math.max(1, open + move)
    const wick = 1.4 + Math.abs(pseudoRandom(seed * 3 + index) * 3.8)
    const high = Math.max(open, close) + wick
    const low = Math.min(open, close) - wick

    candles.push({
      time,
      open: roundPrice(open),
      high: roundPrice(high),
      low: roundPrice(low),
      close: roundPrice(close),
    })
  }

  return candles
}

function getSymbolSeed(symbol: string) {
  return symbol.split('').reduce((total, char) => total + char.charCodeAt(0), 0)
}

function pseudoRandom(input: number) {
  const x = Math.sin(input * 999) * 10000
  return x - Math.floor(x) - 0.5
}

function roundPrice(value: number) {
  return Math.round(value * 4) / 4
}
