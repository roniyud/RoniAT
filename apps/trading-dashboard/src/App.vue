<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import {
  Activity,
  AlertTriangle,
  BarChart3,
  BriefcaseBusiness,
  CheckCircle2,
  Clock3,
  ListChecks,
  RefreshCw,
  ScrollText,
  Send,
  Server,
  Settings2,
  ShieldCheck,
  WifiOff,
} from '@lucide/vue'
import CandlestickChart from './components/CandlestickChart.vue'
import {
  cancelWorkingOrders,
  closePosition,
  emergencyStop,
  flattenPaperAccount,
  getAuditLogs,
  getAuthToken,
  getClosedPositions,
  getDailyPerformance,
  getBrokerSettings,
  getBrokerMode,
  getHealth,
  getOrders,
  getPositions,
  getRiskSettings,
  getSignals,
  login,
  lockTrading,
  logout,
  refreshTastytradeAccessToken,
  resumeTrading,
  submitMarketOrder,
  submitManualTrade,
  testBrokerConnection,
  updateProtection,
  updateBrokerSettings,
  updateRiskSettings,
} from './services/api'
import { getCandles, startMarketDataStream, type Timeframe } from './services/market-data'
import type { CandlestickData, UTCTimestamp } from 'lightweight-charts'
import { createTradingRealtimeClient, type MarketTick, type RealtimeStatus, type TradingUpdate } from './services/realtime'
import type { ApiState, AuditLogRecord, BrokerConnectionTestResult, BrokerMode, BrokerSettings, ClosedPositionRecord, DailyPerformance, IBKRSettings, MarketOrderResponse, OrderRecord, PositionRecord, RiskSettings, TastytradeSettings, TradingSignal, TradingSignalRequest } from './services/types'

const apiState = ref<ApiState>('loading')
const isAuthenticated = ref(Boolean(getAuthToken()))
const loginForm = ref({ username: 'admin', password: '' })
const isLoggingIn = ref(false)
const loginMessage = ref('')
const activeTab = ref<'chart' | 'trade' | 'signals' | 'orders' | 'positions' | 'audit' | 'settings'>('chart')
const positionsView = ref<'open' | 'closed'>('open')
const signals = ref<TradingSignal[]>([])
const orders = ref<OrderRecord[]>([])
const positions = ref<PositionRecord[]>([])
const closedPositions = ref<ClosedPositionRecord[]>([])
const closedPositionsDate = ref(getTodayDateInput())
const auditLogs = ref<AuditLogRecord[]>([])
const dailyPerformance = ref<DailyPerformance | null>(null)
const riskSettings = ref<RiskSettings | null>(null)
const riskForm = ref<RiskSettings | null>(null)
const brokerStatus = ref<BrokerMode | null>(null)
const brokerSettings = ref<BrokerSettings | null>(null)
const brokerForm = ref<BrokerSettings | null>(null)
const lastUpdated = ref<Date | null>(null)
const errorMessage = ref('')
const isRefreshing = ref(false)
const activeAction = ref('')
const isSavingRisk = ref(false)
const isSavingBroker = ref(false)
const isTestingBroker = ref(false)
const isRefreshingTastytradeToken = ref(false)
const isSubmittingTrade = ref(false)
const isSafetyActionRunning = ref(false)
const riskSaveMessage = ref('')
const brokerSaveMessage = ref('')
const brokerTestResult = ref<BrokerConnectionTestResult | null>(null)
const tradeMessage = ref('')
const selectedTimeframe = ref<Timeframe>('5m')
const selectedChartSymbol = ref('MNQ1!')
const requireChartTradeConfirmation = ref(true)
const chartCandles = ref<CandlestickData[]>([])
const chartError = ref('')
const chartMarketDataWarning = ref('')
const isChartLoading = ref(false)
const realtimeStatus = ref<RealtimeStatus>('disconnected')
const lastRealtimeEvent = ref<TradingUpdate | null>(null)
let refreshTimer: number | undefined
let chartRefreshTimer: number | undefined
let isChartRefreshInFlight = false
let realtimeClient: ReturnType<typeof createTradingRealtimeClient> | undefined

const totalOpenQuantity = computed(() => positions.value.reduce((total, position) => total + Math.abs(position.quantity), 0))
const workingOrders = computed(() => orders.value.filter((order) => order.status === 'working'))
const workingOrderCount = computed(() => workingOrders.value.length)
const filledOrderCount = computed(() => orders.value.filter((order) => order.status === 'filled').length)
const cancelledOrderCount = computed(() => orders.value.filter((order) => order.status === 'cancelled').length)
const closedPositionsTotalQuantity = computed(() => closedPositions.value.reduce((total, position) => total + Math.abs(position.quantity), 0))
const closedPositionsTotalPnl = computed(() => closedPositions.value.reduce((total, position) => total + (position.realizedPnl ?? 0), 0))
const rejectedSignalCount = computed(() => signals.value.filter((signal) => signal.status === 'rejected_by_risk').length)
const approvedSignalCount = computed(() => signals.value.length - rejectedSignalCount.value)
const safetyStatus = computed(() => {
  if (riskSettings.value?.emergency_stop_active) return 'Emergency Stop Active'
  if (riskSettings.value?.trading_locked) return 'Trading Locked'
  if (riskSettings.value?.enable_auto_trading) return 'Trading Enabled'
  return 'Auto Trading Off'
})
const safetyStatusClass = computed(() => {
  if (riskSettings.value?.emergency_stop_active) return 'emergency'
  if (riskSettings.value?.trading_locked) return 'locked'
  if (riskSettings.value?.enable_auto_trading) return 'enabled'
  return 'paused'
})
const manualTrade = ref<TradingSignalRequest>({
  type: 'entry',
  direction: 'SHORT',
  contracts: 1,
  stop_loss: 30755.5,
  take_profit_1: 30709.5,
  take_profit_2: 30686.25,
  entry_price: 30736.25,
  symbol: 'MNQ1!',
})

const chartSymbol = computed(() => selectedChartSymbol.value.trim().toUpperCase() || 'MNQ1!')
const latestChartPrice = computed(() => chartCandles.value.at(-1)?.close ?? null)
const availableChartSymbols = computed(() => {
  const symbols = new Set<string>()
  const candidates = [
    chartSymbol.value,
    manualTrade.value.symbol,
    ...(riskSettings.value?.allowed_symbols ?? []),
    ...signals.value.map((signal) => signal.symbol),
    ...orders.value.map((order) => order.symbol),
    ...positions.value.map((position) => position.symbol),
  ]

  for (const symbol of candidates) {
    const normalized = normalizeSymbol(symbol)
    if (normalized) symbols.add(normalized)
  }

  if (!symbols.size) symbols.add(chartSymbol.value)
  return [...symbols].sort()
})
const chartLevels = computed(() => {
  const symbol = chartSymbol.value
  const levels: { id: string; label: string; price: number; color: string; draggable?: 'stop_loss' | 'take_profit'; style?: 'solid' | 'dashed' | 'dotted' }[] = []
  const symbolPosition = positions.value.find((position) => normalizeSymbol(position.symbol) === symbol)
  const symbolOrders = orders.value.filter((order) => normalizeSymbol(order.symbol) === symbol && order.status === 'working')
  const hasPositionProtection = symbolPosition?.stopLoss != null
    || symbolPosition?.takeProfit1 != null
    || symbolPosition?.takeProfit2 != null

  if (symbolPosition) {
    levels.push({
      id: `position-${symbolPosition.id}-avg`,
      label: 'Avg',
      price: symbolPosition.averagePrice,
      color: '#7c3aed',
      style: 'solid',
    })

    if (symbolPosition.stopLoss != null) {
      levels.push({ id: `position-${symbolPosition.id}-sl`, label: 'SL', price: symbolPosition.stopLoss, color: '#b42318', draggable: 'stop_loss', style: 'dotted' })
    }
    if (symbolPosition.takeProfit1 != null) {
      levels.push({ id: `position-${symbolPosition.id}-tp1`, label: 'TP', price: symbolPosition.takeProfit1, color: '#13795b', draggable: 'take_profit', style: 'dotted' })
    }
    if (symbolPosition.takeProfit2 != null) {
      levels.push({ id: `position-${symbolPosition.id}-tp2`, label: 'Pos TP2', price: symbolPosition.takeProfit2, color: '#0f766e', style: 'dotted' })
    }
  }

  for (const order of symbolOrders) {
    if (hasPositionProtection && isProtectionOrder(order.orderType)) continue
    const price = order.price ?? order.stopPrice
    if (price == null) continue
    levels.push({
      id: `order-${order.id}`,
      label: `${order.orderType} ${order.direction}`,
      price,
      color: '#b45309',
      style: 'dashed',
    })
  }

  return levels.filter((level) => Number.isFinite(level.price))
})
const activeChartTrade = computed(() => {
  const symbol = chartSymbol.value
  const position = positions.value.find((item) => normalizeSymbol(item.symbol) === symbol)
  if (!position) return null

  const currentPrice = latestChartPrice.value
  const pointValue = getPointValue(symbol)
  const directionMultiplier = position.direction.toUpperCase() === 'SHORT' ? -1 : 1
  const estimatedPnl = currentPrice == null
    ? null
    : (currentPrice - position.averagePrice) * directionMultiplier * position.quantity * pointValue
  const positionOrders = workingOrders.value.filter((order) => normalizeSymbol(order.symbol) === symbol)

  return {
    symbol,
    direction: position.direction,
    quantity: position.quantity,
    averagePrice: position.averagePrice,
    currentPrice,
    estimatedPnl,
    maxProfit: calculatePositionMaxProfit(position),
    maxLoss: calculatePositionMaxLoss(position),
    stopLoss: position.stopLoss,
    takeProfit1: position.takeProfit1,
    takeProfit2: position.takeProfit2,
    workingOrders: positionOrders.length,
    updatedAt: position.updatedAt,
  }
})
const chartWorkingOrderCount = computed(() => workingOrders.value.filter((order) => normalizeSymbol(order.symbol) === chartSymbol.value).length)
const isChartSymbolAllowed = computed(() => {
  const allowedSymbols = riskSettings.value?.allowed_symbols
    .map((symbol) => normalizeSymbol(symbol))
    .filter(Boolean) ?? []

  return allowedSymbols.length === 0 || allowedSymbols.includes(chartSymbol.value)
})
const chartTradeBlockedReason = computed(() => {
  if (riskSettings.value?.emergency_stop_active) return 'Emergency stop is active'
  if (riskSettings.value?.trading_locked) return 'Trading is locked'
  if (!riskSettings.value?.enable_auto_trading) return 'Auto trading is off'
  if (!brokerStatus.value?.configured) return 'Broker is not configured'
  if (!brokerStatus.value?.enabled) return 'Broker is disabled'
  if (!brokerStatus.value?.connected) return 'Broker is disconnected'
  if (brokerStatus.value?.read_only) return 'RoniAT order lock is on'
  if (chartMarketDataWarning.value) return 'Chart is using simulated fallback data; chart trading is disabled'
  if (!isChartSymbolAllowed.value) return `${chartSymbol.value} is not in allowed symbols`
  if (latestChartPrice.value == null) return 'Waiting for chart price'
  return ''
})
const canSubmitChartTrade = computed(() => !chartTradeBlockedReason.value && !isSubmittingTrade.value)

function normalizeSymbol(symbol?: string | null) {
  return symbol?.trim().toUpperCase() ?? ''
}

function isProtectionOrder(orderType: string) {
  const normalized = orderType.toLowerCase()
  return normalized.includes('stop_loss') || normalized.includes('take_profit')
}

function getPointValue(symbol: string) {
  const normalized = normalizeSymbol(symbol).replace('1!', '')
  if (normalized === 'MNQ') return 2
  if (normalized === 'NQ') return 20
  if (normalized === 'MES') return 5
  if (normalized === 'ES') return 50
  return 1
}

function getTodayDateInput() {
  const now = new Date()
  const year = now.getFullYear()
  const month = String(now.getMonth() + 1).padStart(2, '0')
  const day = String(now.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

function calculatePositionMaxLoss(position: PositionRecord) {
  if (position.stopLoss == null || position.quantity <= 0) return null

  const stopDistance = Math.abs(position.averagePrice - position.stopLoss)
  if (!Number.isFinite(stopDistance) || stopDistance <= 0) return null

  return stopDistance * Math.abs(position.quantity) * getPointValue(position.symbol)
}

function calculatePositionMaxProfit(position: PositionRecord) {
  if (position.takeProfit1 == null || position.quantity <= 0) return null

  const profitDistance = Math.abs(position.takeProfit1 - position.averagePrice)
  if (!Number.isFinite(profitDistance) || profitDistance <= 0) return null

  return profitDistance * Math.abs(position.quantity) * getPointValue(position.symbol)
}

async function refreshData() {
  isRefreshing.value = true
  errorMessage.value = ''

  try {
    await getHealth()
    const [nextSignals, nextOrders, nextPositions, nextClosedPositions, nextAuditLogs, nextDailyPerformance, nextRiskSettings, nextBrokerMode, nextBrokerSettings] = await Promise.all([
      getSignals(),
      getOrders(),
      getPositions(),
      getClosedPositions(closedPositionsDate.value),
      getAuditLogs(),
      getDailyPerformance(),
      getRiskSettings(),
      getBrokerMode(),
      getBrokerSettings(),
    ])

    signals.value = nextSignals
    orders.value = nextOrders
    positions.value = nextPositions
    closedPositions.value = nextClosedPositions
    auditLogs.value = nextAuditLogs
    dailyPerformance.value = nextDailyPerformance
    riskSettings.value = nextRiskSettings
    brokerStatus.value = nextBrokerMode
    brokerSettings.value = nextBrokerSettings
    if (!riskForm.value) {
      riskForm.value = { ...nextRiskSettings, allowed_symbols: [...nextRiskSettings.allowed_symbols] }
    }
    if (!brokerForm.value) {
      brokerForm.value = cloneBrokerSettings(nextBrokerSettings)
    }
    lastUpdated.value = new Date()
    apiState.value = 'online'
  } catch (error) {
    apiState.value = 'offline'
    errorMessage.value = error instanceof Error ? error.message : 'Trading Engine is unavailable'
  } finally {
    isRefreshing.value = false
  }
}

async function refreshCandles(showLoading = true) {
  if (isChartRefreshInFlight) return

  isChartRefreshInFlight = true
  if (showLoading) {
    isChartLoading.value = true
  }
  chartError.value = ''
  chartMarketDataWarning.value = ''

  try {
    const result = await getCandles(chartSymbol.value, selectedTimeframe.value)
    chartCandles.value = result.candles
    chartMarketDataWarning.value = result.source === 'fallback'
      ? `Simulated fallback candles. Market data failed: ${result.warning || 'historical data unavailable'}`
      : ''
  } catch (error) {
    chartCandles.value = []
    chartMarketDataWarning.value = ''
    chartError.value = error instanceof Error ? error.message : 'Market data unavailable'
  } finally {
    isChartRefreshInFlight = false
    if (showLoading) {
      isChartLoading.value = false
    }
  }
}

async function ensureChartTickStream() {
  try {
    await startMarketDataStream(chartSymbol.value)
  } catch (error) {
    console.warn('Market data stream failed', error)
  }
}

function getChartRefreshIntervalMs(timeframe: Timeframe) {
  return timeframe === '1h' ? 30000 : 5000
}

function getTimeframeSeconds(timeframe: Timeframe) {
  if (timeframe === '1m') return 60
  if (timeframe === '5m') return 300
  if (timeframe === '15m') return 900
  return 3600
}

function applyMarketTick(tick: MarketTick) {
  if (normalizeSymbol(tick.symbol) !== chartSymbol.value || !Number.isFinite(tick.price) || tick.price <= 0) {
    return
  }

  const intervalSeconds = getTimeframeSeconds(selectedTimeframe.value)
  const candleTime = Math.floor(tick.time / intervalSeconds) * intervalSeconds as UTCTimestamp
  const latestCandle = chartCandles.value.at(-1)

  if (!latestCandle || typeof latestCandle.time !== 'number') {
    refreshCandles(false)
    return
  }

  if (candleTime < latestCandle.time) {
    return
  }

  if (candleTime > latestCandle.time) {
    chartCandles.value = [
      ...chartCandles.value.slice(-299),
      {
        time: candleTime,
        open: tick.price,
        high: tick.price,
        low: tick.price,
        close: tick.price,
      },
    ]
    return
  }

  chartCandles.value = [
    ...chartCandles.value.slice(0, -1),
    {
      ...latestCandle,
      high: Math.max(latestCandle.high, tick.price),
      low: Math.min(latestCandle.low, tick.price),
      close: tick.price,
    },
  ]
}

function restartChartRefreshTimer() {
  if (chartRefreshTimer) {
    window.clearInterval(chartRefreshTimer)
  }

  chartRefreshTimer = window.setInterval(() => {
    if (activeTab.value === 'chart') {
      refreshCandles(false)
    }
  }, getChartRefreshIntervalMs(selectedTimeframe.value))
}

async function runAction(actionKey: string, confirmation: string, action: () => Promise<unknown>, requireConfirmation = true) {
  if (requireConfirmation && !window.confirm(confirmation)) return

  activeAction.value = actionKey
  errorMessage.value = ''

  try {
    await action()
    await refreshData()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Action failed'
  } finally {
    activeAction.value = ''
  }
}

async function handleCancelWorkingOrders(symbol?: string) {
  const label = symbol ? `for ${symbol}` : 'for all symbols'
  await runAction(
    `cancel-${symbol || 'all'}`,
    `Cancel all working broker orders ${label}?`,
    () => cancelWorkingOrders(symbol),
    requireChartTradeConfirmation.value,
  )
}

async function handleClosePosition(symbol: string) {
  await runAction(
    `close-${symbol}`,
    `Close the broker position for ${symbol}?`,
    () => closePosition(symbol),
    requireChartTradeConfirmation.value,
  )
}

async function handleFlatten() {
  await runAction(
    'flatten',
    'Flatten all broker positions and cancel all working broker orders?',
    () => flattenPaperAccount(),
    requireChartTradeConfirmation.value,
  )
}

async function handleProtectionDrag(field: 'stop_loss' | 'take_profit', price: number) {
  if (chartMarketDataWarning.value) {
    tradeMessage.value = 'Protection drag is disabled while chart uses simulated fallback data'
    return
  }

  const label = field === 'stop_loss' ? 'SL' : 'TP'
  if (requireChartTradeConfirmation.value && !window.confirm(`Update ${label} for ${chartSymbol.value} to ${formatPrice(price)}? This will replace the working broker protection orders.`)) {
    tradeMessage.value = `${label} update cancelled`
    return
  }

  activeAction.value = `protection-${field}`
  errorMessage.value = ''
  tradeMessage.value = ''

  try {
    const result = await updateProtection({
      symbol: chartSymbol.value,
      stop_loss: field === 'stop_loss' ? price : undefined,
      take_profit: field === 'take_profit' ? price : undefined,
    })

    tradeMessage.value = result.ok
      ? `${label} updated to ${formatPrice(price)}`
      : `Protection update failed: ${result.message}`
    await refreshData()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Protection update failed'
  } finally {
    activeAction.value = ''
  }
}

async function handleSaveRiskSettings() {
  if (!riskForm.value) return

  isSavingRisk.value = true
  riskSaveMessage.value = ''
  errorMessage.value = ''

  try {
    const saved = await updateRiskSettings({
      ...riskForm.value,
      allowed_symbols: normalizeSymbols(symbolsInput.value),
      max_contracts_per_signal: Number(riskForm.value.max_contracts_per_signal),
      max_loss_per_trade: Number(riskForm.value.max_loss_per_trade),
      max_daily_loss: Number(riskForm.value.max_daily_loss),
      max_entry_price_deviation_points: Number(riskForm.value.max_entry_price_deviation_points),
      chart_market_protection_distance_points: Number(riskForm.value.chart_market_protection_distance_points),
      duplicate_window_seconds: Number(riskForm.value.duplicate_window_seconds),
    })

    riskSettings.value = saved
    riskForm.value = { ...saved, allowed_symbols: [...saved.allowed_symbols] }
    riskSaveMessage.value = 'Risk settings saved'
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Risk settings update failed'
  } finally {
    isSavingRisk.value = false
  }
}

async function handleSaveBrokerSettings() {
  if (!brokerForm.value) return

  isSavingBroker.value = true
  brokerSaveMessage.value = ''
  errorMessage.value = ''

  try {
    const saved = await updateBrokerSettings(normalizeBrokerSettings(brokerForm.value))
    brokerSettings.value = saved
    brokerForm.value = cloneBrokerSettings(saved)
    brokerSaveMessage.value = 'Broker settings saved'
    await refreshData()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Broker settings update failed'
  } finally {
    isSavingBroker.value = false
  }
}

async function handleTestBrokerConnection() {
  if (!brokerForm.value) return

  isTestingBroker.value = true
  brokerSaveMessage.value = ''
  errorMessage.value = ''

  try {
    const saved = await updateBrokerSettings(normalizeBrokerSettings(brokerForm.value))
    brokerSettings.value = saved
    brokerForm.value = cloneBrokerSettings(saved)
    brokerTestResult.value = await testBrokerConnection()
    brokerSaveMessage.value = brokerTestResult.value.ok
      ? 'Connection test succeeded'
      : `Connection test failed: ${brokerTestResult.value.message}`
    await refreshData()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Broker connection test failed'
  } finally {
    isTestingBroker.value = false
  }
}

async function handleRefreshTastytradeToken() {
  if (!brokerForm.value) return

  isRefreshingTastytradeToken.value = true
  brokerSaveMessage.value = ''
  errorMessage.value = ''

  try {
    brokerForm.value.mode = 'Tastytrade'
    const saved = await updateBrokerSettings(normalizeBrokerSettings(brokerForm.value))
    brokerSettings.value = saved
    brokerForm.value = cloneBrokerSettings(saved)
    const result = await refreshTastytradeAccessToken()
    const latestBrokerSettings = await getBrokerSettings()
    brokerSettings.value = latestBrokerSettings
    brokerForm.value = cloneBrokerSettings(latestBrokerSettings)
    brokerSaveMessage.value = result.access_token_expires_at
      ? `${result.message}. Expires at ${formatDateTime(result.access_token_expires_at)}`
      : result.message
    await refreshData()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Tastytrade token refresh failed'
  } finally {
    isRefreshingTastytradeToken.value = false
  }
}

async function handleSubmitManualTrade() {
  isSubmittingTrade.value = true
  tradeMessage.value = ''
  errorMessage.value = ''

  try {
    const result = await submitManualTrade({
      ...manualTrade.value,
      symbol: manualTrade.value.symbol.trim().toUpperCase(),
      contracts: Number(manualTrade.value.contracts),
      entry_price: Number(manualTrade.value.entry_price),
      stop_loss: Number(manualTrade.value.stop_loss),
      take_profit_1: Number(manualTrade.value.take_profit_1),
      take_profit_2: Number(manualTrade.value.take_profit_2),
    })

    tradeMessage.value = formatTradeSubmissionMessage(result, 'manual form')
    await refreshData()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Manual trade failed'
  } finally {
    isSubmittingTrade.value = false
  }
}

async function handleSubmitChartTrade(direction: 'LONG' | 'SHORT') {
  if (chartTradeBlockedReason.value) {
    tradeMessage.value = chartTradeBlockedReason.value
    errorMessage.value = chartTradeBlockedReason.value
    return
  }

  const entryPrice = latestChartPrice.value
  if (entryPrice == null) {
    errorMessage.value = 'Chart price is unavailable'
    return
  }

  const protectionDistance = getChartMarketProtectionDistance()
  const trade = {
    type: 'entry' as const,
    direction,
    contracts: Number(manualTrade.value.contracts),
    symbol: chartSymbol.value,
    reference_price: Number(entryPrice),
    attach_protection: true,
    protection_distance: protectionDistance,
  }

  if (requireChartTradeConfirmation.value && !window.confirm(`Submit ${direction} market order for ${trade.contracts} ${trade.symbol} at reference price ${formatPrice(trade.reference_price)} with ${formatPrice(protectionDistance)} point TP/SL?`)) {
    return
  }

  isSubmittingTrade.value = true
  tradeMessage.value = ''
  errorMessage.value = ''

  try {
    const result = await submitMarketOrder(trade)
    manualTrade.value = {
      ...manualTrade.value,
      direction,
      contracts: trade.contracts,
      entry_price: trade.reference_price,
      symbol: trade.symbol,
    }
    tradeMessage.value = formatMarketOrderSubmissionMessage(result)
    await refreshData()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Chart trade failed'
  } finally {
    isSubmittingTrade.value = false
  }
}

function getChartMarketProtectionDistance() {
  const distance = Number(riskSettings.value?.chart_market_protection_distance_points ?? riskForm.value?.chart_market_protection_distance_points ?? 100)
  return Number.isFinite(distance) && distance > 0 ? distance : 100
}

function formatTradeSubmissionMessage(result: TradingSignal, source: string) {
  const status = result.status || 'unknown'

  if (status === 'rejected_by_risk') return `Rejected by risk from ${source} as signal ${result.id}`
  if (status === 'broker_blocked') return `Broker blocked ${source} signal ${result.id}`
  if (status.includes('rejected') || status.includes('failed')) return `Rejected from ${source} as signal ${result.id}: ${status}`
  if (status.includes('opened') || status.includes('filled')) return `Position opened from ${source} as signal ${result.id}`
  if (status.includes('submitted') || status.includes('working')) return `Submitted to broker from ${source} as signal ${result.id}`

  return `Signal ${result.id} from ${source}: ${status}`
}

function formatMarketOrderSubmissionMessage(result: MarketOrderResponse) {
  if (result.ok) {
    return result.message || `Market order ${result.status}`
  }

  if (result.status === 'rejected_by_risk') {
    return `Market order rejected: ${result.message}`
  }

  if (result.status === 'broker_blocked') {
    return `Broker blocked market order: ${result.message}`
  }

  return `Market order failed: ${result.message || result.status}`
}

function updateChartTradeSetting(field: 'contracts', value: number) {
  manualTrade.value = {
    ...manualTrade.value,
    symbol: chartSymbol.value,
    entry_price: latestChartPrice.value ?? manualTrade.value.entry_price,
    [field]: value,
  }
}

async function handleLockTrading() {
  if (!window.confirm('Lock trading and turn off auto trading?')) return

  await runSafetyAction(() => lockTrading())
}

async function handleEmergencyStop() {
  if (!window.confirm('Emergency Stop will close positions, cancel working orders, lock trading, and disable auto trading. Continue?')) return

  await runSafetyAction(() => emergencyStop())
}

async function handleResumeTrading() {
  if (!window.confirm('Resume trading and enable auto trading?')) return

  await runSafetyAction(() => resumeTrading())
}

async function runSafetyAction(action: () => Promise<RiskSettings>) {
  isSafetyActionRunning.value = true
  errorMessage.value = ''

  try {
    const saved = await action()
    riskSettings.value = saved
    riskForm.value = { ...saved, allowed_symbols: [...saved.allowed_symbols] }
    await refreshData()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : 'Safety action failed'
  } finally {
    isSafetyActionRunning.value = false
  }
}

function formatPrice(value: number | null | undefined) {
  if (value === null || value === undefined) return '-'
  return new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 }).format(value)
}

function formatCurrency(value: number | null | undefined) {
  if (value === null || value === undefined) return '-'
  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    maximumFractionDigits: 2,
  }).format(value)
}

function formatTime(value: string | null | undefined) {
  if (!value) return '-'
  return new Intl.DateTimeFormat('en-US', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).format(new Date(value))
}

function formatDateTime(value: string | null | undefined) {
  if (!value) return '-'
  return new Intl.DateTimeFormat('en-US', {
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(value))
}

function formatCloseReason(value: string) {
  return value
    .split('_')
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ')
}

function getWorkingOrdersForSymbol(symbol: string) {
  return workingOrders.value.filter((order) => order.symbol === symbol)
}

function getStatusClass(status: string) {
  return status.replace(/[^a-z0-9]+/gi, '-').toLowerCase()
}

function getActionClass(action: string) {
  return action.replace(/[^a-z0-9]+/gi, '-').toLowerCase()
}

const symbolsInput = computed({
  get() {
    return riskForm.value?.allowed_symbols.join(', ') || ''
  },
  set(value: string) {
    if (!riskForm.value) return
    riskForm.value.allowed_symbols = normalizeSymbols(value)
  },
})

function normalizeSymbols(value: string) {
  return value
    .split(',')
    .map((symbol) => symbol.trim().toUpperCase())
    .filter(Boolean)
}

function cloneBrokerSettings(settings: BrokerSettings): BrokerSettings {
  return {
    mode: settings.mode,
    ibkr_environment: settings.ibkr_environment,
    tastytrade_environment: settings.tastytrade_environment,
    ibkr_paper: { ...settings.ibkr_paper },
    ibkr_live: { ...settings.ibkr_live },
    tastytrade_sandbox: { ...settings.tastytrade_sandbox },
    tastytrade_live: { ...settings.tastytrade_live },
  }
}

function normalizeBrokerSettings(settings: BrokerSettings): BrokerSettings {
  return {
    mode: settings.mode,
    ibkr_environment: settings.ibkr_environment,
    tastytrade_environment: settings.tastytrade_environment,
    ibkr_paper: normalizeIBKRSettings(settings.ibkr_paper),
    ibkr_live: normalizeIBKRSettings(settings.ibkr_live),
    tastytrade_sandbox: normalizeTastytradeSettings(settings.tastytrade_sandbox, true),
    tastytrade_live: normalizeTastytradeSettings(settings.tastytrade_live, false),
  }
}

function normalizeIBKRSettings(settings: IBKRSettings): IBKRSettings {
  return {
    host: settings.host.trim() || '127.0.0.1',
    port: Number(settings.port),
    client_id: Number(settings.client_id),
    account: settings.account.trim(),
    enabled: settings.enabled,
    read_only: settings.read_only,
  }
}

function normalizeTastytradeSettings(settings: TastytradeSettings, sandboxDefaults: boolean): TastytradeSettings {
  const apiBaseUrl = settings.api_base_url.trim() || (sandboxDefaults ? 'https://api.cert.tastyworks.com' : 'https://api.tastyworks.com')
  return {
    api_base_url: apiBaseUrl,
    streamer_base_url: settings.streamer_base_url.trim() || (sandboxDefaults ? 'wss://streamer.cert.tastyworks.com' : 'wss://streamer.tastyworks.com'),
    authorization_url: settings.authorization_url.trim() || `${apiBaseUrl}/oauth/authorize`,
    token_url: settings.token_url.trim() || `${apiBaseUrl}/oauth/token`,
    client_id: settings.client_id.trim(),
    client_secret: settings.client_secret,
    redirect_uri: settings.redirect_uri.trim() || 'http://localhost:3001/api/tastytrade/oauth/callback',
    username: settings.username.trim(),
    password: settings.password,
    access_token: settings.access_token.trim(),
    refresh_token: settings.refresh_token.trim(),
    access_token_expires_at: settings.access_token_expires_at ?? null,
    account_number: settings.account_number.trim(),
    enabled: settings.enabled,
    read_only: settings.read_only,
  }
}

async function handleLogin() {
  isLoggingIn.value = true
  loginMessage.value = ''
  errorMessage.value = ''

  try {
    await login(loginForm.value.username.trim(), loginForm.value.password)
    isAuthenticated.value = true
    loginForm.value.password = ''
    startDashboard()
  } catch (error) {
    loginMessage.value = error instanceof Error ? error.message : 'Login failed'
  } finally {
    isLoggingIn.value = false
  }
}

async function handleLogout() {
  await logout()
  stopDashboard()
  isAuthenticated.value = false
  apiState.value = 'offline'
  realtimeStatus.value = 'disconnected'
}

function startDashboard() {
  stopDashboard()
  refreshData()
  refreshCandles()
  ensureChartTickStream()
  refreshTimer = window.setInterval(refreshData, 5000)
  restartChartRefreshTimer()
  realtimeClient = createTradingRealtimeClient(
    (update) => {
      lastRealtimeEvent.value = update
      refreshData()
      if (activeTab.value === 'chart') {
        refreshCandles(false)
      }
    },
    (status) => {
      realtimeStatus.value = status
    },
    applyMarketTick,
  )
  realtimeClient.start().catch((error) => {
    realtimeStatus.value = 'disconnected'
    console.warn('SignalR connection failed', error)
  })
}

function stopDashboard() {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
    refreshTimer = undefined
  }
  if (chartRefreshTimer) {
    window.clearInterval(chartRefreshTimer)
    chartRefreshTimer = undefined
  }
  realtimeClient?.stop()
  realtimeClient = undefined
}

onMounted(() => {
  if (isAuthenticated.value) {
    startDashboard()
  } else {
    apiState.value = 'offline'
  }
})

onUnmounted(() => {
  stopDashboard()
})

watch([chartSymbol, selectedTimeframe], () => {
  if (!isAuthenticated.value) return
  ensureChartTickStream()
  refreshCandles()
  restartChartRefreshTimer()
})

watch(activeTab, (tab) => {
  if (isAuthenticated.value && tab === 'chart') {
    ensureChartTickStream()
    refreshCandles(false)
  }
})

watch(closedPositionsDate, () => {
  if (isAuthenticated.value) {
    refreshData()
  }
})
</script>

<template>
  <main class="app-shell">
    <header class="topbar">
      <div class="brand-block">
        <div class="brand-mark">
          <BarChart3 :size="22" />
        </div>
        <div>
          <h1>RoniAT</h1>
          <div class="status-line" :class="apiState">
            <CheckCircle2 v-if="apiState === 'online'" :size="16" />
            <WifiOff v-else-if="apiState === 'offline'" :size="16" />
            <Server v-else :size="16" />
            <span>{{ apiState === 'online' ? 'Trading Engine Online' : apiState === 'offline' ? 'Trading Engine Offline' : 'Connecting' }}</span>
          </div>
          <div class="status-line realtime" :class="realtimeStatus">
            <CheckCircle2 v-if="realtimeStatus === 'connected'" :size="16" />
            <WifiOff v-else-if="realtimeStatus === 'disconnected'" :size="16" />
            <Server v-else :size="16" />
            <span>{{ realtimeStatus === 'connected' ? 'Realtime Connected' : realtimeStatus === 'connecting' ? 'Realtime Connecting' : 'Realtime Offline' }}</span>
          </div>
        </div>
      </div>

      <div class="topbar-actions">
        <button v-if="isAuthenticated" class="action-button secondary" type="button" @click="handleLogout">
          <ShieldCheck :size="16" />
          <span>Logout</span>
        </button>
        <button v-if="isAuthenticated" class="icon-button" type="button" title="Refresh" :disabled="isRefreshing" @click="refreshData">
          <RefreshCw :size="20" :class="{ spinning: isRefreshing }" />
        </button>
      </div>
    </header>

    <section v-if="!isAuthenticated" class="login-panel">
      <form class="login-form" @submit.prevent="handleLogin">
        <div>
          <h2>Dashboard Login</h2>
          <p>Sign in before opening trading controls.</p>
        </div>
        <label>
          <span>Username</span>
          <input v-model="loginForm.username" type="text" autocomplete="username" />
        </label>
        <label>
          <span>Password</span>
          <input v-model="loginForm.password" type="password" autocomplete="current-password" />
        </label>
        <button class="action-button secondary" type="submit" :disabled="isLoggingIn">
          <ShieldCheck :size="16" />
          <span>{{ isLoggingIn ? 'Signing in' : 'Sign In' }}</span>
        </button>
        <span v-if="loginMessage" class="login-message">{{ loginMessage }}</span>
      </form>
    </section>

    <template v-else>
    <section v-if="errorMessage" class="alert-strip">
      <AlertTriangle :size="18" />
      <span>{{ errorMessage }}</span>
    </section>

    <section class="safety-strip" :class="safetyStatusClass">
      <div>
        <ShieldCheck :size="18" />
        <strong>{{ safetyStatus }}</strong>
      </div>
      <div class="safety-actions">
        <button
          class="action-button secondary"
          type="button"
          :disabled="isSafetyActionRunning || riskSettings?.trading_locked"
          @click="handleLockTrading"
        >
          <ShieldCheck :size="16" />
          <span>Lock</span>
        </button>
        <button
          class="action-button danger"
          type="button"
          :disabled="isSafetyActionRunning"
          @click="handleEmergencyStop"
        >
          <AlertTriangle :size="16" />
          <span>Emergency Stop</span>
        </button>
        <button
          class="action-button secondary"
          type="button"
          :disabled="isSafetyActionRunning || (!riskSettings?.trading_locked && riskSettings?.enable_auto_trading && !riskSettings?.emergency_stop_active)"
          @click="handleResumeTrading"
        >
          <CheckCircle2 :size="16" />
          <span>Resume</span>
        </button>
      </div>
    </section>

    <section class="metrics-grid" aria-label="Trading overview">
      <article class="metric-tile">
        <ListChecks :size="20" />
        <div>
          <span>Total Signals</span>
          <strong>{{ signals.length }}</strong>
        </div>
      </article>
      <article class="metric-tile">
        <BriefcaseBusiness :size="20" />
        <div>
          <span>Open Position Qty</span>
          <strong>{{ totalOpenQuantity }}</strong>
        </div>
      </article>
      <article class="metric-tile">
        <Activity :size="20" />
        <div>
          <span>Working Orders</span>
          <strong>{{ workingOrderCount }}</strong>
        </div>
      </article>
      <article class="metric-tile">
        <ShieldCheck :size="20" />
        <div>
          <span>Auto Trading</span>
          <strong>{{ riskSettings?.enable_auto_trading && !riskSettings?.trading_locked ? 'On' : 'Off' }}</strong>
        </div>
      </article>
      <article class="metric-tile">
        <ScrollText :size="20" />
        <div>
          <span>Audit Events</span>
          <strong>{{ auditLogs.length }}</strong>
        </div>
      </article>
      <article class="metric-tile">
        <Clock3 :size="20" />
        <div>
          <span>{{ lastRealtimeEvent ? lastRealtimeEvent.event_type : 'Updated' }}</span>
          <strong>{{ lastUpdated ? formatTime(lastUpdated.toISOString()) : '-' }}</strong>
        </div>
      </article>
    </section>

    <nav class="tabbar" aria-label="Dashboard views">
      <button type="button" :class="{ active: activeTab === 'chart' }" @click="activeTab = 'chart'">
        <BarChart3 :size="18" />
        <span>Chart</span>
      </button>
      <button type="button" :class="{ active: activeTab === 'trade' }" @click="activeTab = 'trade'">
        <Send :size="18" />
        <span>Trade</span>
      </button>
      <button type="button" :class="{ active: activeTab === 'signals' }" @click="activeTab = 'signals'">
        <ListChecks :size="18" />
        <span>Signals</span>
      </button>
      <button type="button" :class="{ active: activeTab === 'orders' }" @click="activeTab = 'orders'">
        <Activity :size="18" />
        <span>Orders</span>
      </button>
      <button type="button" :class="{ active: activeTab === 'positions' }" @click="activeTab = 'positions'">
        <BriefcaseBusiness :size="18" />
        <span>Positions</span>
      </button>
      <button type="button" :class="{ active: activeTab === 'audit' }" @click="activeTab = 'audit'">
        <ScrollText :size="18" />
        <span>Audit</span>
      </button>
      <button type="button" :class="{ active: activeTab === 'settings' }" @click="activeTab = 'settings'">
        <Settings2 :size="18" />
        <span>Settings</span>
      </button>
    </nav>

    <section class="workspace">
      <CandlestickChart
        v-if="activeTab === 'chart'"
        :available-symbols="availableChartSymbols"
        :active-trade="activeChartTrade"
        :can-submit-trade="canSubmitChartTrade"
        :candles="chartCandles"
        :chart-trade-blocked-reason="chartTradeBlockedReason"
        :chart-trade-message="tradeMessage"
        :chart-trade-settings="manualTrade"
        :chart-working-order-count="chartWorkingOrderCount"
        :daily-performance="dailyPerformance"
        :error-message="chartError"
        :market-data-warning="chartMarketDataWarning"
        :is-submitting-trade="isSubmittingTrade"
        :is-loading="isChartLoading"
        :levels="chartLevels"
        :require-trade-confirmation="requireChartTradeConfirmation"
        :symbol="chartSymbol"
        :timeframe="selectedTimeframe"
        @cancel-orders="handleCancelWorkingOrders(chartSymbol)"
        @chart-trade="handleSubmitChartTrade"
        @close-position="handleClosePosition(chartSymbol)"
        @flatten="handleFlatten"
        @protection-drag="handleProtectionDrag"
        @require-trade-confirmation-change="requireChartTradeConfirmation = $event"
        @symbol-change="selectedChartSymbol = $event"
        @trade-setting-change="updateChartTradeSetting"
        @timeframe-change="selectedTimeframe = $event"
      />

      <div v-if="activeTab === 'trade'" class="data-panel trade-panel">
        <div class="panel-header">
          <div>
            <h2>Manual Trade</h2>
            <p class="panel-subtitle">Broker {{ brokerStatus?.mode || 'Paper' }} / {{ safetyStatus }}</p>
          </div>
          <span class="status-pill" :class="brokerStatus?.mode.toLowerCase() || 'paper'">{{ brokerStatus?.mode || 'Paper' }}</span>
        </div>

        <form class="trade-form" @submit.prevent="handleSubmitManualTrade">
          <div class="side-control">
            <button
              type="button"
              :class="{ active: manualTrade.direction === 'LONG' }"
              @click="manualTrade.direction = 'LONG'"
            >
              LONG
            </button>
            <button
              type="button"
              :class="{ active: manualTrade.direction === 'SHORT' }"
              @click="manualTrade.direction = 'SHORT'"
            >
              SHORT
            </button>
          </div>

          <div class="settings-grid">
            <label>
              <span>Symbol</span>
              <input v-model="manualTrade.symbol" type="text" autocomplete="off" spellcheck="false" />
            </label>
            <label>
              <span>Contracts</span>
              <input v-model.number="manualTrade.contracts" type="number" min="1" max="100" />
            </label>
            <label>
              <span>Entry Price</span>
              <input v-model.number="manualTrade.entry_price" type="number" step="0.25" />
            </label>
            <label>
              <span>Stop Loss</span>
              <input v-model.number="manualTrade.stop_loss" type="number" step="0.25" />
            </label>
            <label>
              <span>Take Profit 1</span>
              <input v-model.number="manualTrade.take_profit_1" type="number" step="0.25" />
            </label>
            <label>
              <span>Take Profit 2</span>
              <input v-model.number="manualTrade.take_profit_2" type="number" step="0.25" />
            </label>
          </div>

          <div class="settings-actions">
            <span class="save-message">{{ tradeMessage }}</span>
            <button
              class="action-button secondary"
              type="submit"
              :disabled="isSubmittingTrade || Boolean(riskSettings?.trading_locked) || Boolean(riskSettings?.emergency_stop_active) || !riskSettings?.enable_auto_trading"
            >
              <Send :size="16" />
              <span>{{ isSubmittingTrade ? 'Submitting' : 'Submit Trade' }}</span>
            </button>
          </div>
        </form>
      </div>

      <div v-if="activeTab === 'signals'" class="data-panel">
        <div class="panel-header">
          <div>
            <h2>Total Signals</h2>
            <p class="panel-subtitle">{{ approvedSignalCount }} approved / {{ rejectedSignalCount }} rejected by risk</p>
          </div>
          <span class="count-pill">{{ signals.length }}</span>
        </div>

        <div v-if="signals.length === 0" class="empty-state">No signals</div>
        <div v-if="signals.length !== 0" class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Time</th>
                <th>Symbol</th>
                <th>Side</th>
                <th>Qty</th>
                <th>Entry</th>
                <th>SL</th>
                <th>TP1</th>
                <th>TP2</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="signal in signals" :key="signal.id">
                <td>{{ formatDateTime(signal.created_at) }}</td>
                <td class="symbol-cell">{{ signal.symbol }}</td>
                <td>
                  <span class="side-pill" :class="signal.direction.toLowerCase()">{{ signal.direction }}</span>
                </td>
                <td>{{ signal.contracts }}</td>
                <td>{{ formatPrice(signal.entry_price) }}</td>
                <td>{{ formatPrice(signal.stop_loss) }}</td>
                <td>{{ formatPrice(signal.take_profit_1) }}</td>
                <td>{{ formatPrice(signal.take_profit_2) }}</td>
                <td>
                  <span class="status-pill" :class="getStatusClass(signal.status)">{{ signal.status }}</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div v-if="activeTab === 'orders'" class="data-panel">
        <div class="panel-header">
          <div>
            <h2>Orders</h2>
            <p class="panel-subtitle">{{ workingOrderCount }} working / {{ filledOrderCount }} filled / {{ cancelledOrderCount }} cancelled</p>
          </div>
          <div class="panel-actions">
            <button
              class="action-button secondary"
              type="button"
              :disabled="Boolean(activeAction)"
              @click="handleCancelWorkingOrders()"
            >
              <Activity :size="16" />
              <span>{{ activeAction === 'cancel-all' ? 'Cancelling' : 'Cancel Working' }}</span>
            </button>
            <span class="count-pill">{{ orders.length }}</span>
          </div>
        </div>

        <div v-if="orders.length === 0" class="empty-state">No orders</div>
        <div v-if="orders.length !== 0" class="status-legend">
          <span><strong>{{ workingOrderCount }}</strong> active protection/exit orders</span>
          <span><strong>{{ filledOrderCount }}</strong> filled orders</span>
          <span><strong>{{ cancelledOrderCount }}</strong> cancelled orders</span>
        </div>
        <div v-if="orders.length !== 0" class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Created</th>
                <th>Symbol</th>
                <th>Side</th>
                <th>Type</th>
                <th>Qty</th>
                <th>Price</th>
                <th>Stop</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="order in orders" :key="order.id">
                <td>{{ formatDateTime(order.createdAt) }}</td>
                <td class="symbol-cell">{{ order.symbol }}</td>
                <td>{{ order.direction }}</td>
                <td>{{ order.orderType }}</td>
                <td>{{ order.quantity }}</td>
                <td>{{ formatPrice(order.price) }}</td>
                <td>{{ formatPrice(order.stopPrice) }}</td>
                <td>{{ order.status }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div v-if="activeTab === 'positions'" class="data-panel">
        <div class="panel-header">
          <div>
            <h2>Positions</h2>
            <p class="panel-subtitle">{{ positions.length }} open / {{ closedPositions.length }} closed for selected day</p>
          </div>
          <div class="panel-actions">
            <button
              v-if="positionsView === 'open'"
              class="action-button danger"
              type="button"
              :disabled="Boolean(activeAction) || positions.length === 0"
              @click="handleFlatten"
            >
              <AlertTriangle :size="16" />
              <span>{{ activeAction === 'flatten' ? 'Flattening' : 'Flatten' }}</span>
            </button>
            <span class="count-pill">{{ positionsView === 'open' ? positions.length : closedPositions.length }}</span>
          </div>
        </div>

        <div class="position-view-tabs" role="tablist" aria-label="Position views">
          <button
            type="button"
            role="tab"
            :aria-selected="positionsView === 'open'"
            :class="{ active: positionsView === 'open' }"
            @click="positionsView = 'open'"
          >
            Open Positions
            <span>{{ positions.length }}</span>
          </button>
          <button
            type="button"
            role="tab"
            :aria-selected="positionsView === 'closed'"
            :class="{ active: positionsView === 'closed' }"
            @click="positionsView = 'closed'"
          >
            Closed Positions
            <span>{{ closedPositions.length }}</span>
          </button>
        </div>

        <div v-if="positionsView === 'open' && positions.length === 0" class="empty-state">No positions</div>
        <div v-if="positionsView === 'open' && positions.length !== 0" class="position-list">
          <article v-for="position in positions" :key="position.id" class="position-row">
            <div>
              <strong>{{ position.symbol }}</strong>
              <span>{{ position.direction }} / open qty {{ position.quantity }}</span>
              <span>{{ position.direction }} · {{ position.quantity }}</span>
            </div>
            <div>
              <span>Avg Entry</span>
              <strong>{{ formatPrice(position.averagePrice) }}</strong>
            </div>
            <div>
              <span>Stop Loss</span>
              <strong>{{ formatPrice(position.stopLoss) }}</strong>
            </div>
            <div>
              <span>Max Loss</span>
              <strong class="negative">{{ formatCurrency(calculatePositionMaxLoss(position)) }}</strong>
            </div>
            <div>
              <span>Take Profits</span>
              <strong>{{ formatPrice(position.takeProfit1) }} / {{ formatPrice(position.takeProfit2) }}</strong>
            </div>
            <div>
              <span>Working Orders</span>
              <strong>{{ getWorkingOrdersForSymbol(position.symbol).length }}</strong>
            </div>
            <div class="position-actions">
              <button
                class="action-button secondary"
                type="button"
                :disabled="Boolean(activeAction)"
                @click="handleCancelWorkingOrders(position.symbol)"
              >
                <Activity :size="16" />
                <span>Cancel Orders</span>
              </button>
              <button
                class="action-button danger"
                type="button"
                :disabled="Boolean(activeAction)"
                @click="handleClosePosition(position.symbol)"
              >
                <BriefcaseBusiness :size="16" />
                <span>{{ activeAction === `close-${position.symbol}` ? 'Closing' : 'Close Position' }}</span>
              </button>
            </div>
          </article>
        </div>

        <div v-if="positionsView === 'closed'" class="closed-positions-panel">
          <div class="panel-header compact">
            <div>
              <h3>Closed Positions</h3>
              <p class="panel-subtitle">Filtered by close date</p>
            </div>
            <div class="closed-total-inline">
              <div>
                <span>Closed Trades</span>
                <strong>{{ closedPositions.length }}</strong>
              </div>
              <div>
                <span>Total Contracts</span>
                <strong>{{ closedPositionsTotalQuantity }}</strong>
              </div>
              <div>
                <span>Total P&L</span>
                <strong :class="closedPositionsTotalPnl >= 0 ? 'positive' : 'negative'">
                  {{ formatCurrency(closedPositionsTotalPnl) }}
                </strong>
              </div>
            </div>
            <div class="panel-actions">
              <label class="date-filter">
                <span>Day</span>
                <input v-model="closedPositionsDate" type="date" />
              </label>
              <span class="count-pill">{{ closedPositions.length }}</span>
            </div>
          </div>

          <div v-if="closedPositions.length === 0" class="empty-state">No closed positions for this day</div>
          <div v-if="closedPositions.length !== 0" class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Closed</th>
                  <th>Symbol</th>
                  <th>Side</th>
                  <th>Qty</th>
                  <th>Entry</th>
                  <th>Exit</th>
                  <th>SL</th>
                  <th>TP</th>
                  <th>P&L</th>
                  <th>Reason</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="position in closedPositions" :key="position.id">
                  <td>{{ formatDateTime(position.closedAt) }}</td>
                  <td class="symbol-cell">{{ position.symbol }}</td>
                  <td>
                    <span class="side-pill" :class="position.direction.toLowerCase()">{{ position.direction }}</span>
                  </td>
                  <td>{{ position.quantity }}</td>
                  <td>{{ formatPrice(position.averagePrice) }}</td>
                  <td>{{ formatPrice(position.exitPrice) }}</td>
                  <td>{{ formatPrice(position.stopLoss) }}</td>
                  <td>{{ formatPrice(position.takeProfit1) }}</td>
                  <td>
                    <strong :class="(position.realizedPnl ?? 0) >= 0 ? 'positive' : 'negative'">
                      {{ formatCurrency(position.realizedPnl) }}
                    </strong>
                  </td>
                  <td>{{ formatCloseReason(position.closeReason) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </div>

      <div v-if="activeTab === 'audit'" class="data-panel">
        <div class="panel-header">
          <div>
            <h2>Audit Log</h2>
            <p class="panel-subtitle">Last {{ auditLogs.length }} engine events</p>
          </div>
          <span class="count-pill">{{ auditLogs.length }}</span>
        </div>

        <div v-if="auditLogs.length === 0" class="empty-state">No audit events</div>
        <div v-else class="audit-list">
          <article v-for="event in auditLogs" :key="event.id" class="audit-row">
            <div>
              <span class="status-pill audit-action" :class="getActionClass(event.action)">{{ event.action }}</span>
              <strong>{{ event.details }}</strong>
            </div>
            <time>{{ formatDateTime(event.created_at) }}</time>
          </article>
        </div>
      </div>

      <div v-if="activeTab === 'settings'" class="data-panel settings-panel">
        <div class="panel-header">
          <div>
            <h2>Settings</h2>
            <p class="panel-subtitle">Broker {{ brokerStatus?.mode || 'Paper' }} {{ brokerStatus?.environment || '' }} / Symbols {{ riskSettings?.allowed_symbols.join(', ') || '-' }}</p>
          </div>
          <span class="status-pill" :class="riskSettings?.enable_auto_trading ? 'paper-position-opened' : 'rejected-by-risk'">
            {{ riskSettings?.enable_auto_trading ? 'Auto On' : 'Auto Off' }}
          </span>
          <span class="status-pill" :class="riskSettings?.stop_loss_failsafe_enabled ? 'paper-position-opened' : 'rejected-by-risk'">
            {{ riskSettings?.stop_loss_failsafe_enabled ? 'Failsafe On' : 'Failsafe Off' }}
          </span>
        </div>

        <form v-if="brokerForm" class="settings-form broker-settings-form" @submit.prevent="handleSaveBrokerSettings">
          <div class="broker-status-card">
            <div>
              <span>Broker Status</span>
              <strong>{{ brokerTestResult?.message || brokerStatus?.message || 'Paper broker active' }}</strong>
              <small v-if="brokerTestResult" class="broker-connection-details">
                API {{ brokerTestResult.handshake_ok ? 'handshake ok' : 'handshake missing' }}
                <template v-if="brokerTestResult.server_version"> / server {{ brokerTestResult.server_version }}</template>
                / selected {{ brokerTestResult.selected_account || '-' }}
                / accounts {{ brokerTestResult.managed_accounts.length ? brokerTestResult.managed_accounts.join(', ') : '-' }}
              </small>
            </div>
            <div class="broker-status-flags">
              <span class="status-pill" :class="brokerStatus?.configured ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ brokerStatus?.configured ? 'Configured' : 'Not Configured' }}
              </span>
              <span class="status-pill" :class="brokerStatus?.enabled ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ brokerStatus?.enabled ? 'Enabled' : 'Disabled' }}
              </span>
              <span class="status-pill" :class="brokerStatus?.connected ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ brokerStatus?.connected ? 'Connected' : 'Disconnected' }}
              </span>
              <span v-if="brokerTestResult" class="status-pill" :class="brokerTestResult.handshake_ok ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ brokerTestResult.handshake_ok ? 'Handshake OK' : 'Handshake Failed' }}
              </span>
              <span v-if="brokerTestResult" class="status-pill" :class="brokerTestResult.account_verified ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ brokerTestResult.account_verified ? 'Account Verified' : 'Account Missing' }}
              </span>
              <span v-if="brokerStatus?.read_only" class="status-pill risk-settings-updated">RoniAT Order Lock</span>
            </div>
          </div>

          <div class="settings-section-title">
            <h3>Broker Mode</h3>
          </div>

          <div class="broker-card-grid" aria-label="Broker mode">
            <button
              class="broker-choice-card"
              type="button"
              :class="{ active: brokerForm.mode === 'Paper' }"
              @click="brokerForm.mode = 'Paper'"
            >
              <span>
                <strong>Paper</strong>
                <small>Local simulator</small>
              </span>
              <span class="status-pill paper-position-opened">Ready</span>
            </button>
            <button
              class="broker-choice-card"
              type="button"
              :class="{ active: brokerForm.mode === 'IBKR' }"
              @click="brokerForm.mode = 'IBKR'"
            >
              <span>
                <strong>IBKR</strong>
                <small>{{ brokerForm.ibkr_environment }} / {{ brokerForm.ibkr_environment === 'Live' ? brokerForm.ibkr_live.account || 'No account' : brokerForm.ibkr_paper.account || 'No account' }}</small>
              </span>
              <span class="status-pill" :class="(brokerForm.ibkr_environment === 'Live' ? brokerForm.ibkr_live.enabled : brokerForm.ibkr_paper.enabled) ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ (brokerForm.ibkr_environment === 'Live' ? brokerForm.ibkr_live.enabled : brokerForm.ibkr_paper.enabled) ? 'Enabled' : 'Disabled' }}
              </span>
            </button>
            <button
              class="broker-choice-card"
              type="button"
              :class="{ active: brokerForm.mode === 'Tastytrade' }"
              @click="brokerForm.mode = 'Tastytrade'"
            >
              <span>
                <strong>Tastytrade</strong>
                <small>{{ brokerForm.tastytrade_environment }} / {{ brokerForm.tastytrade_environment === 'Live' ? brokerForm.tastytrade_live.account_number || 'No account' : brokerForm.tastytrade_sandbox.account_number || 'No account' }}</small>
              </span>
              <span class="status-pill" :class="(brokerForm.tastytrade_environment === 'Live' ? brokerForm.tastytrade_live.enabled : brokerForm.tastytrade_sandbox.enabled) ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ (brokerForm.tastytrade_environment === 'Live' ? brokerForm.tastytrade_live.enabled : brokerForm.tastytrade_sandbox.enabled) ? 'Enabled' : 'Disabled' }}
              </span>
            </button>
          </div>

          <div v-if="brokerForm.mode === 'IBKR'" class="broker-environment-tabs" role="tablist" aria-label="IBKR environment settings">
            <button
              type="button"
              role="tab"
              :aria-selected="brokerForm.ibkr_environment === 'Paper'"
              :class="{ active: brokerForm.ibkr_environment === 'Paper' }"
              @click="brokerForm.ibkr_environment = 'Paper'"
            >
              Paper
            </button>
            <button
              type="button"
              role="tab"
              :aria-selected="brokerForm.ibkr_environment === 'Live'"
              :class="{ active: brokerForm.ibkr_environment === 'Live' }"
              @click="brokerForm.ibkr_environment = 'Live'"
            >
              Live
            </button>
          </div>

          <div v-else-if="brokerForm.mode === 'Tastytrade'" class="broker-environment-tabs" role="tablist" aria-label="Tastytrade environment settings">
            <button
              type="button"
              role="tab"
              :aria-selected="brokerForm.tastytrade_environment === 'Sandbox'"
              :class="{ active: brokerForm.tastytrade_environment === 'Sandbox' }"
              @click="brokerForm.tastytrade_environment = 'Sandbox'"
            >
              Sandbox
            </button>
            <button
              type="button"
              role="tab"
              :aria-selected="brokerForm.tastytrade_environment === 'Live'"
              :class="{ active: brokerForm.tastytrade_environment === 'Live' }"
              @click="brokerForm.tastytrade_environment = 'Live'"
            >
              Live
            </button>
          </div>

          <section v-if="brokerForm.mode === 'Paper'" class="broker-environment-panel" role="tabpanel">
            <div class="broker-environment-heading">
              <div>
                <span>Selected Broker</span>
                <strong>Paper Simulator</strong>
              </div>
              <span class="status-pill paper-position-opened">Active</span>
            </div>

            <div class="broker-note">
              Paper mode uses the local simulated broker. It does not require external API credentials and does not connect to a real brokerage account.
            </div>
          </section>

          <section v-else-if="brokerForm.mode === 'IBKR' && brokerForm.ibkr_environment === 'Paper'" class="broker-environment-panel" role="tabpanel">
            <div class="broker-environment-heading">
              <div>
                <span>Selected Environment</span>
                <strong>IBKR Paper</strong>
              </div>
              <span class="status-pill" :class="brokerForm.ibkr_paper.enabled ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ brokerForm.ibkr_paper.enabled ? 'Enabled' : 'Disabled' }}
              </span>
            </div>

            <div class="settings-grid">
              <label>
                <span>Gateway Host</span>
                <input v-model="brokerForm.ibkr_paper.host" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Gateway Port</span>
                <input v-model.number="brokerForm.ibkr_paper.port" type="number" min="1" max="65535" />
              </label>
              <label>
                <span>Client ID</span>
                <input v-model.number="brokerForm.ibkr_paper.client_id" type="number" min="1" />
              </label>
              <label>
                <span>Paper Account</span>
                <input v-model="brokerForm.ibkr_paper.account" type="text" autocomplete="off" spellcheck="false" />
              </label>
            </div>

            <label class="toggle-row">
              <span>
                <strong>Enable IBKR Paper</strong>
                <small>{{ brokerForm.ibkr_paper.enabled ? 'Enabled' : 'Disabled' }}</small>
              </span>
              <input v-model="brokerForm.ibkr_paper.enabled" type="checkbox" />
            </label>

            <label class="toggle-row">
              <span>
                <strong>RoniAT Paper Order Lock</strong>
                <small>{{ brokerForm.ibkr_paper.read_only ? 'Blocks order placement from RoniAT' : 'RoniAT can send Paper orders' }}</small>
              </span>
              <input v-model="brokerForm.ibkr_paper.read_only" type="checkbox" />
            </label>
          </section>

          <section v-else-if="brokerForm.mode === 'IBKR'" class="broker-environment-panel live" role="tabpanel">
            <div class="broker-environment-heading">
              <div>
                <span>Selected Environment</span>
                <strong>IBKR Live</strong>
              </div>
              <span class="status-pill" :class="brokerForm.ibkr_live.enabled ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ brokerForm.ibkr_live.enabled ? 'Enabled' : 'Disabled' }}
              </span>
            </div>

            <div class="settings-grid">
              <label>
                <span>Gateway Host</span>
                <input v-model="brokerForm.ibkr_live.host" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Gateway Port</span>
                <input v-model.number="brokerForm.ibkr_live.port" type="number" min="1" max="65535" />
              </label>
              <label>
                <span>Client ID</span>
                <input v-model.number="brokerForm.ibkr_live.client_id" type="number" min="1" />
              </label>
              <label>
                <span>Live Account</span>
                <input v-model="brokerForm.ibkr_live.account" type="text" autocomplete="off" spellcheck="false" />
              </label>
            </div>

            <label class="toggle-row">
              <span>
                <strong>Enable IBKR Live</strong>
                <small>{{ brokerForm.ibkr_live.enabled ? 'Enabled' : 'Disabled' }}</small>
              </span>
              <input v-model="brokerForm.ibkr_live.enabled" type="checkbox" />
            </label>

            <label class="toggle-row">
              <span>
                <strong>Live Read Only</strong>
                <small>{{ brokerForm.ibkr_live.read_only ? 'Read only' : 'Order capable later' }}</small>
              </span>
              <input v-model="brokerForm.ibkr_live.read_only" type="checkbox" />
            </label>
          </section>

          <section v-else-if="brokerForm.mode === 'Tastytrade' && brokerForm.tastytrade_environment === 'Sandbox'" class="broker-environment-panel" role="tabpanel">
            <div class="broker-environment-heading">
              <div>
                <span>Selected Environment</span>
                <strong>Tastytrade Sandbox</strong>
              </div>
              <span class="status-pill" :class="brokerForm.tastytrade_sandbox.enabled ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ brokerForm.tastytrade_sandbox.enabled ? 'Enabled' : 'Disabled' }}
              </span>
            </div>

            <div class="settings-grid">
              <label>
                <span>API Base URL</span>
                <input v-model="brokerForm.tastytrade_sandbox.api_base_url" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Streamer URL</span>
                <input v-model="brokerForm.tastytrade_sandbox.streamer_base_url" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Authorization URL</span>
                <input v-model="brokerForm.tastytrade_sandbox.authorization_url" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Token URL</span>
                <input v-model="brokerForm.tastytrade_sandbox.token_url" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Client ID</span>
                <input v-model="brokerForm.tastytrade_sandbox.client_id" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Client Secret</span>
                <input v-model="brokerForm.tastytrade_sandbox.client_secret" type="password" autocomplete="new-password" />
              </label>
              <label class="wide-field">
                <span>Redirect URI</span>
                <input v-model="brokerForm.tastytrade_sandbox.redirect_uri" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>OAuth Access Token</span>
                <input v-model="brokerForm.tastytrade_sandbox.access_token" type="password" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>OAuth Refresh Token</span>
                <input v-model="brokerForm.tastytrade_sandbox.refresh_token" type="password" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Account Number</span>
                <input v-model="brokerForm.tastytrade_sandbox.account_number" type="text" autocomplete="off" spellcheck="false" />
              </label>
            </div>

            <button class="action-button secondary" type="button" :disabled="isSavingBroker || isRefreshingTastytradeToken" @click="handleRefreshTastytradeToken">
              <Server :size="16" />
              <span>{{ isRefreshingTastytradeToken ? 'Refreshing Token' : 'Refresh Access Token' }}</span>
            </button>

            <label class="toggle-row">
              <span>
                <strong>Enable Tastytrade Sandbox</strong>
                <small>{{ brokerForm.tastytrade_sandbox.enabled ? 'Enabled' : 'Disabled' }}</small>
              </span>
              <input v-model="brokerForm.tastytrade_sandbox.enabled" type="checkbox" />
            </label>

            <label class="toggle-row">
              <span>
                <strong>Sandbox Read Only</strong>
                <small>{{ brokerForm.tastytrade_sandbox.read_only ? 'Blocks order placement from RoniAT' : 'Order capable later' }}</small>
              </span>
              <input v-model="brokerForm.tastytrade_sandbox.read_only" type="checkbox" />
            </label>
          </section>

          <section v-else class="broker-environment-panel live" role="tabpanel">
            <div class="broker-environment-heading">
              <div>
                <span>Selected Environment</span>
                <strong>Tastytrade Live</strong>
              </div>
              <span class="status-pill" :class="brokerForm.tastytrade_live.enabled ? 'paper-position-opened' : 'rejected-by-risk'">
                {{ brokerForm.tastytrade_live.enabled ? 'Enabled' : 'Disabled' }}
              </span>
            </div>

            <div class="settings-grid">
              <label>
                <span>API Base URL</span>
                <input v-model="brokerForm.tastytrade_live.api_base_url" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Streamer URL</span>
                <input v-model="brokerForm.tastytrade_live.streamer_base_url" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Authorization URL</span>
                <input v-model="brokerForm.tastytrade_live.authorization_url" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Token URL</span>
                <input v-model="brokerForm.tastytrade_live.token_url" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Client ID</span>
                <input v-model="brokerForm.tastytrade_live.client_id" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Client Secret</span>
                <input v-model="brokerForm.tastytrade_live.client_secret" type="password" autocomplete="new-password" />
              </label>
              <label class="wide-field">
                <span>Redirect URI</span>
                <input v-model="brokerForm.tastytrade_live.redirect_uri" type="text" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>OAuth Access Token</span>
                <input v-model="brokerForm.tastytrade_live.access_token" type="password" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>OAuth Refresh Token</span>
                <input v-model="brokerForm.tastytrade_live.refresh_token" type="password" autocomplete="off" spellcheck="false" />
              </label>
              <label>
                <span>Account Number</span>
                <input v-model="brokerForm.tastytrade_live.account_number" type="text" autocomplete="off" spellcheck="false" />
              </label>
            </div>

            <button class="action-button secondary" type="button" :disabled="isSavingBroker || isRefreshingTastytradeToken" @click="handleRefreshTastytradeToken">
              <Server :size="16" />
              <span>{{ isRefreshingTastytradeToken ? 'Refreshing Token' : 'Refresh Access Token' }}</span>
            </button>

            <label class="toggle-row">
              <span>
                <strong>Enable Tastytrade Live</strong>
                <small>{{ brokerForm.tastytrade_live.enabled ? 'Enabled' : 'Disabled' }}</small>
              </span>
              <input v-model="brokerForm.tastytrade_live.enabled" type="checkbox" />
            </label>

            <label class="toggle-row">
              <span>
                <strong>Live Read Only</strong>
                <small>{{ brokerForm.tastytrade_live.read_only ? 'Read only' : 'Order capable later' }}</small>
              </span>
              <input v-model="brokerForm.tastytrade_live.read_only" type="checkbox" />
            </label>
          </section>

          <div class="settings-actions">
            <span class="save-message">{{ brokerSaveMessage }}</span>
            <button class="action-button secondary" type="button" :disabled="isSavingBroker || isTestingBroker" @click="handleTestBrokerConnection">
              <Server :size="16" />
              <span>{{ isTestingBroker ? 'Testing' : 'Test Connection' }}</span>
            </button>
            <button class="action-button secondary" type="submit" :disabled="isSavingBroker">
              <Server :size="16" />
              <span>{{ isSavingBroker ? 'Saving' : 'Save Broker Settings' }}</span>
            </button>
          </div>
        </form>

        <form v-if="riskForm" class="settings-form" @submit.prevent="handleSaveRiskSettings">
          <div class="settings-section-title">
            <h3>Risk Control</h3>
          </div>

          <label class="toggle-row">
            <span>
              <strong>Auto Trading</strong>
              <small>{{ riskForm.enable_auto_trading ? 'Enabled' : 'Disabled' }}</small>
            </span>
            <input v-model="riskForm.enable_auto_trading" type="checkbox" />
          </label>

          <label class="toggle-row">
            <span>
              <strong>Test Mode</strong>
              <small>{{ riskForm.test_mode ? 'Signals become market orders with 100 point SL/TP' : 'Disabled' }}</small>
            </span>
            <input v-model="riskForm.test_mode" type="checkbox" />
          </label>

          <label class="toggle-row">
            <span>
              <strong>Reject Duplicate Signals</strong>
              <small>{{ riskForm.reject_duplicate_signals ? 'Enabled' : 'Disabled' }}</small>
            </span>
            <input v-model="riskForm.reject_duplicate_signals" type="checkbox" />
          </label>

          <label class="toggle-row">
            <span>
              <strong>Ignore TP2</strong>
              <small>{{ riskForm.ignore_tp2 ? 'TP2 ignored by default' : 'TP2 active' }}</small>
            </span>
            <input v-model="riskForm.ignore_tp2" type="checkbox" />
          </label>

          <label class="toggle-row">
            <span>
              <strong>Allow Position Stacking</strong>
              <small>{{ riskForm.allow_position_stacking ? 'Enabled' : 'Blocked' }}</small>
            </span>
            <input v-model="riskForm.allow_position_stacking" type="checkbox" />
          </label>

          <label class="toggle-row">
            <span>
              <strong>Trading Lock</strong>
              <small>{{ riskForm.trading_locked ? 'Locked' : 'Unlocked' }}</small>
            </span>
            <input v-model="riskForm.trading_locked" type="checkbox" />
          </label>

          <label class="toggle-row">
            <span>
              <strong>Emergency Stop Active</strong>
              <small>{{ riskForm.emergency_stop_active ? 'Active' : 'Inactive' }}</small>
            </span>
            <input v-model="riskForm.emergency_stop_active" type="checkbox" />
          </label>

          <div class="settings-section-title">
            <h3>Stop Loss Failsafe</h3>
          </div>

          <label class="toggle-row">
            <span>
              <strong>Enable SL Failsafe</strong>
              <small>{{ riskForm.stop_loss_failsafe_enabled ? 'Forced close if SL is crossed and position remains open' : 'Disabled' }}</small>
            </span>
            <input v-model="riskForm.stop_loss_failsafe_enabled" type="checkbox" />
          </label>

          <div class="settings-grid">
            <label>
              <span>Max Contracts</span>
              <input v-model.number="riskForm.max_contracts_per_signal" type="number" min="1" max="100" />
            </label>
            <label>
              <span>Max Loss Per Trade</span>
              <input v-model.number="riskForm.max_loss_per_trade" type="number" min="0" step="1" />
            </label>
            <label>
              <span>Max Daily Loss</span>
              <input v-model.number="riskForm.max_daily_loss" type="number" min="0" step="1" />
            </label>
            <label>
              <span>Max Entry Distance Points</span>
              <input v-model.number="riskForm.max_entry_price_deviation_points" type="number" min="0" step="0.25" />
            </label>
            <label>
              <span>Chart Market TP/SL Distance</span>
              <input v-model.number="riskForm.chart_market_protection_distance_points" type="number" min="0.25" max="10000" step="0.25" />
            </label>
            <label>
              <span>Duplicate Window Seconds</span>
              <input v-model.number="riskForm.duplicate_window_seconds" type="number" min="1" max="3600" />
            </label>
            <label>
              <span>Failsafe Poll Seconds</span>
              <input v-model.number="riskForm.stop_loss_failsafe_poll_seconds" type="number" min="1" max="30" />
            </label>
            <label>
              <span>Failsafe Confirm Seconds</span>
              <input v-model.number="riskForm.stop_loss_failsafe_confirm_seconds" type="number" min="0" max="60" />
            </label>
            <label>
              <span>Failsafe Cooldown Seconds</span>
              <input v-model.number="riskForm.stop_loss_failsafe_cooldown_seconds" type="number" min="5" max="300" />
            </label>
            <label class="wide-field">
              <span>Allowed Symbols</span>
              <input v-model="symbolsInput" type="text" autocomplete="off" spellcheck="false" />
            </label>
          </div>

          <div class="settings-actions">
            <span class="save-message">{{ riskSaveMessage }}</span>
            <button class="action-button secondary" type="submit" :disabled="isSavingRisk">
              <ShieldCheck :size="16" />
              <span>{{ isSavingRisk ? 'Saving' : 'Save Risk Settings' }}</span>
            </button>
          </div>
        </form>

        <div v-else class="empty-state">Loading settings</div>
      </div>
    </section>
    </template>
  </main>
</template>
