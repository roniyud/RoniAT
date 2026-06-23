import type { OrderRecord, PaperActionResult, PositionRecord, TradingSignal } from './types'

const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/+$/, '')

export async function getHealth() {
  return request<{ status: string; service: string }>('/health')
}

export async function getSignals() {
  return normalizeCollection<TradingSignal>(await request<TradingSignal[] | { value: TradingSignal[] }>('/api/signals'))
}

export async function getOrders() {
  const orders = normalizeCollection<Record<string, unknown>>(await request<Record<string, unknown>[] | { value: Record<string, unknown>[] }>('/api/orders'))
  return orders.map(mapOrder)
}

export async function getPositions() {
  const positions = normalizeCollection<Record<string, unknown>>(await request<Record<string, unknown>[] | { value: Record<string, unknown>[] }>('/api/positions'))
  return positions.map(mapPosition)
}

export async function cancelWorkingOrders(symbol?: string) {
  return postBrokerAction('/api/broker/orders/cancel-working', { symbol })
}

export async function closePosition(symbol: string) {
  return postBrokerAction('/api/broker/positions/close', { symbol })
}

export async function flattenPaperAccount() {
  return postBrokerAction('/api/broker/flatten', {})
}

async function request<T>(path: string): Promise<T> {
  const response = await fetch(`${API_BASE_URL}${path}`, {
    headers: {
      Accept: 'application/json',
    },
  })

  if (!response.ok) {
    throw new Error(`HTTP ${response.status} from Trading Engine`)
  }

  return response.json() as Promise<T>
}

async function postBrokerAction(path: string, body: Record<string, unknown>): Promise<PaperActionResult> {
  const response = await fetch(`${API_BASE_URL}${path}`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(body),
  })

  if (!response.ok) {
    throw new Error(`HTTP ${response.status} from Trading Engine`)
  }

  const payload = await response.json() as Record<string, unknown>

  return {
    cancelledOrders: readNumber(payload, 'cancelledOrders', 'CancelledOrders'),
    closedPositions: readNumber(payload, 'closedPositions', 'ClosedPositions'),
  }
}

function normalizeCollection<T>(payload: T[] | { value: T[] }) {
  return Array.isArray(payload) ? payload : payload.value
}

function mapOrder(record: Record<string, unknown>): OrderRecord {
  return {
    id: readNumber(record, 'id', 'Id'),
    signalId: readOptionalNumber(record, 'signalId', 'SignalId'),
    brokerOrderId: readOptionalString(record, 'brokerOrderId', 'BrokerOrderId'),
    symbol: readString(record, 'symbol', 'Symbol'),
    direction: readString(record, 'direction', 'Direction'),
    orderType: readString(record, 'orderType', 'OrderType'),
    quantity: readNumber(record, 'quantity', 'Quantity'),
    price: readOptionalNumber(record, 'price', 'Price'),
    stopPrice: readOptionalNumber(record, 'stopPrice', 'StopPrice'),
    status: readString(record, 'status', 'Status'),
    createdAt: readOptionalString(record, 'createdAt', 'CreatedAt'),
    updatedAt: readOptionalString(record, 'updatedAt', 'UpdatedAt'),
  }
}

function mapPosition(record: Record<string, unknown>): PositionRecord {
  return {
    id: readNumber(record, 'id', 'Id'),
    symbol: readString(record, 'symbol', 'Symbol'),
    direction: readString(record, 'direction', 'Direction'),
    quantity: readNumber(record, 'quantity', 'Quantity'),
    averagePrice: readNumber(record, 'averagePrice', 'AveragePrice'),
    stopLoss: readOptionalNumber(record, 'stopLoss', 'StopLoss'),
    takeProfit1: readOptionalNumber(record, 'takeProfit1', 'TakeProfit1'),
    takeProfit2: readOptionalNumber(record, 'takeProfit2', 'TakeProfit2'),
    openedAt: readOptionalString(record, 'openedAt', 'OpenedAt'),
    updatedAt: readOptionalString(record, 'updatedAt', 'UpdatedAt'),
  }
}

function readNumber(record: Record<string, unknown>, camelKey: string, pascalKey: string) {
  const value = record[camelKey] ?? record[pascalKey]
  return Number(value ?? 0)
}

function readOptionalNumber(record: Record<string, unknown>, camelKey: string, pascalKey: string) {
  const value = record[camelKey] ?? record[pascalKey]
  return value === null || value === undefined ? null : Number(value)
}

function readString(record: Record<string, unknown>, camelKey: string, pascalKey: string) {
  const value = record[camelKey] ?? record[pascalKey]
  return typeof value === 'string' ? value : ''
}

function readOptionalString(record: Record<string, unknown>, camelKey: string, pascalKey: string) {
  const value = record[camelKey] ?? record[pascalKey]
  return typeof value === 'string' ? value : null
}
