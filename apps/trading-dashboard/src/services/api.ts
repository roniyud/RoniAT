import type { AuditLogRecord, BrokerConnectionTestResult, BrokerMode, BrokerSettings, ClosedPositionRecord, DailyPerformance, MarketOrderRequest, MarketOrderResponse, OrderRecord, PaperActionResult, PositionRecord, ProtectionUpdateRequest, ProtectionUpdateResponse, RiskSettings, TradingSignal, TradingSignalRequest } from './types'

const API_BASE_URL = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/+$/, '')
const AUTH_TOKEN_KEY = 'roniat.auth.token'
export const AUTH_EXPIRED_EVENT = 'roniat.auth.expired'

export function getAuthToken() {
  return window.localStorage.getItem(AUTH_TOKEN_KEY)
}

export function isAuthenticated() {
  return Boolean(getAuthToken())
}

export function clearAuthToken() {
  window.localStorage.removeItem(AUTH_TOKEN_KEY)
}

export function notifyAuthExpired() {
  clearAuthToken()
  window.dispatchEvent(new Event(AUTH_EXPIRED_EVENT))
}

export function onAuthExpired(handler: () => void) {
  window.addEventListener(AUTH_EXPIRED_EVENT, handler)
  return () => window.removeEventListener(AUTH_EXPIRED_EVENT, handler)
}

export async function throwApiError(response: Response, source = 'Trading Engine'): Promise<never> {
  if (response.status === 401) {
    notifyAuthExpired()
    throw new Error('Login expired. Please sign in again.')
  }

  const details = await readApiErrorDetails(response)
  throw new Error(details ? `${source}: ${details}` : `HTTP ${response.status} from ${source}`)
}

export async function login(username: string, password: string) {
  const response = await fetch(`${API_BASE_URL}/api/auth/login`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ username, password }),
  })

  if (!response.ok) {
    throw new Error(response.status === 401 ? 'Invalid username or password' : `HTTP ${response.status} from Trading Engine`)
  }

  const payload = await response.json() as { token?: string }
  if (!payload.token) {
    throw new Error('Login did not return a token')
  }

  window.localStorage.setItem(AUTH_TOKEN_KEY, payload.token)
  return payload
}

export async function logout() {
  const token = getAuthToken()
  clearAuthToken()

  if (!token) return

  await fetch(`${API_BASE_URL}/api/auth/logout`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      Authorization: `Bearer ${token}`,
    },
  }).catch(() => undefined)
}

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

export async function getClosedPositions(date: string) {
  const params = new URLSearchParams({ date })
  const positions = normalizeCollection<Record<string, unknown>>(await request<Record<string, unknown>[] | { value: Record<string, unknown>[] }>(`/api/positions/closed?${params.toString()}`))
  return positions.map(mapClosedPosition)
}

export async function getAuditLogs() {
  return normalizeCollection<AuditLogRecord>(await request<AuditLogRecord[] | { value: AuditLogRecord[] }>('/api/audit-logs'))
}

export async function getDailyPerformance() {
  return request<DailyPerformance>('/api/performance/daily')
}

export async function getBrokerMode() {
  return request<BrokerMode>('/api/broker')
}

export async function getBrokerSettings() {
  return request<BrokerSettings>('/api/broker/settings')
}

export async function updateBrokerSettings(settings: BrokerSettings) {
  const response = await fetch(`${API_BASE_URL}/api/broker/settings`, {
    method: 'PUT',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify(settings),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  return response.json() as Promise<BrokerSettings>
}

export async function testBrokerConnection() {
  const response = await fetch(`${API_BASE_URL}/api/broker/test-connection`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify({}),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  return response.json() as Promise<BrokerConnectionTestResult>
}

export async function startTastytradeOAuth() {
  const response = await fetch(`${API_BASE_URL}/api/tastytrade/oauth/start`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify({}),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  return response.json() as Promise<{ authorization_url: string; environment: string; redirect_uri: string }>
}

export async function refreshTastytradeAccessToken() {
  const response = await fetch(`${API_BASE_URL}/api/tastytrade/oauth/refresh`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify({}),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  return response.json() as Promise<{ ok: boolean; message: string; environment: string; access_token_expires_at?: string | null }>
}

export async function getRiskSettings() {
  return request<RiskSettings>('/api/risk/settings')
}

export async function updateRiskSettings(settings: RiskSettings) {
  const response = await fetch(`${API_BASE_URL}/api/risk/settings`, {
    method: 'PUT',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify(settings),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  return response.json() as Promise<RiskSettings>
}

export async function submitManualTrade(trade: TradingSignalRequest) {
  const response = await fetch(`${API_BASE_URL}/api/manual-trades`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify(trade),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  return response.json() as Promise<TradingSignal>
}

export async function submitMarketOrder(order: MarketOrderRequest) {
  const response = await fetch(`${API_BASE_URL}/api/market-orders`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify(order),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  const payload = await response.json() as Record<string, unknown>
  return {
    ok: Boolean(payload.ok ?? payload.Ok),
    status: readString(payload, 'status', 'Status'),
    message: readString(payload, 'message', 'Message'),
    order: payload.order || payload.Order ? mapOrder((payload.order ?? payload.Order) as Record<string, unknown>) : null,
    position: payload.position || payload.Position ? mapPosition((payload.position ?? payload.Position) as Record<string, unknown>) : null,
  } satisfies MarketOrderResponse
}

export async function lockTrading() {
  return postSafetyAction('/api/safety/lock')
}

export async function emergencyStop() {
  const response = await fetch(`${API_BASE_URL}/api/safety/emergency-stop`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify({}),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  const payload = await response.json() as { settings: RiskSettings; broker_result?: Record<string, unknown> }
  return payload.settings
}

export async function resumeTrading() {
  return postSafetyAction('/api/safety/resume')
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

export async function updateProtection(update: ProtectionUpdateRequest) {
  const response = await fetch(`${API_BASE_URL}/api/broker/protection`, {
    method: 'PUT',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify(update),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  const payload = await response.json() as Record<string, unknown>
  return {
    ok: Boolean(payload.ok ?? payload.Ok),
    status: readString(payload, 'status', 'Status'),
    message: readString(payload, 'message', 'Message'),
    position: payload.position || payload.Position ? mapPosition((payload.position ?? payload.Position) as Record<string, unknown>) : null,
  } satisfies ProtectionUpdateResponse
}

async function request<T>(path: string): Promise<T> {
  const response = await fetch(`${API_BASE_URL}${path}`, {
    headers: {
      Accept: 'application/json',
      ...authHeaders(),
    },
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  return response.json() as Promise<T>
}

async function postSafetyAction(path: string): Promise<RiskSettings> {
  const response = await fetch(`${API_BASE_URL}${path}`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify({}),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  return response.json() as Promise<RiskSettings>
}

async function postBrokerAction(path: string, body: Record<string, unknown>): Promise<PaperActionResult> {
  const response = await fetch(`${API_BASE_URL}${path}`, {
    method: 'POST',
    headers: {
      Accept: 'application/json',
      'Content-Type': 'application/json',
      ...authHeaders(),
    },
    body: JSON.stringify(body),
  })

  if (!response.ok) {
    await throwApiError(response)
  }

  const payload = await response.json() as Record<string, unknown>

  return {
    cancelledOrders: readNumber(payload, 'cancelledOrders', 'CancelledOrders'),
    closedPositions: readNumber(payload, 'closedPositions', 'ClosedPositions'),
  }
}

async function readApiErrorDetails(response: Response) {
  const bodyText = await response.text().catch(() => '')
  if (!bodyText) return ''

  try {
    const payload = JSON.parse(bodyText) as Record<string, unknown>
    const errors = payload.errors ?? payload.Errors
    if (Array.isArray(errors) && errors.length > 0) {
      return errors.map(String).join('; ')
    }

    const message = payload.message ?? payload.Message ?? payload.detail ?? payload.title
    return typeof message === 'string' ? message : bodyText
  } catch {
    return bodyText
  }
}

function authHeaders(): Record<string, string> {
  const token = getAuthToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
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
    isManaged: Boolean(record.isManaged ?? record.is_managed ?? record.IsManaged),
    openedAt: readOptionalString(record, 'openedAt', 'OpenedAt'),
    updatedAt: readOptionalString(record, 'updatedAt', 'UpdatedAt'),
  }
}

function mapClosedPosition(record: Record<string, unknown>): ClosedPositionRecord {
  return {
    id: readNumber(record, 'id', 'Id'),
    symbol: readString(record, 'symbol', 'Symbol'),
    direction: readString(record, 'direction', 'Direction'),
    quantity: readNumber(record, 'quantity', 'Quantity'),
    averagePrice: readNumber(record, 'averagePrice', 'AveragePrice'),
    exitPrice: readOptionalNumber(record, 'exitPrice', 'ExitPrice'),
    stopLoss: readOptionalNumber(record, 'stopLoss', 'StopLoss'),
    takeProfit1: readOptionalNumber(record, 'takeProfit1', 'TakeProfit1'),
    takeProfit2: readOptionalNumber(record, 'takeProfit2', 'TakeProfit2'),
    realizedPnl: readOptionalNumber(record, 'realizedPnl', 'RealizedPnl'),
    closeReason: readString(record, 'closeReason', 'CloseReason'),
    openedAt: readString(record, 'openedAt', 'OpenedAt'),
    closedAt: readString(record, 'closedAt', 'ClosedAt'),
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
