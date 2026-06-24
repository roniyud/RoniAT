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
  getBrokerSettings,
  getBrokerMode,
  getHealth,
  getOrders,
  getPositions,
  getRiskSettings,
  getSignals,
  lockTrading,
  resumeTrading,
  submitMarketOrder,
  submitManualTrade,
  testBrokerConnection,
  updateBrokerSettings,
  updateRiskSettings,
} from './services/api'
import { getCandles, type Timeframe } from './services/market-data'
import type { CandlestickData, UTCTimestamp } from 'lightweight-charts'
import { createTradingRealtimeClient, type MarketTick, type RealtimeStatus, type TradingUpdate } from './services/realtime'
import type { ApiState, AuditLogRecord, BrokerConnectionTestResult, BrokerMode, BrokerSettings, IBKRSettings, MarketOrderResponse, OrderRecord, PositionRecord, RiskSettings, TradingSignal, TradingSignalRequest } from './services/types'

const apiState = ref<ApiState>('loading')
const activeTab = ref<'chart' | 'trade' | 'signals' | 'orders' | 'positions' | 'audit' | 'settings'>('chart')
const signals = ref<TradingSignal[]>([])
const orders = ref<OrderRecord[]>([])
const positions = ref<PositionRecord[]>([])
const auditLogs = ref<AuditLogRecord[]>([])
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
  const levels: { id: string; label: string; price: number; color: string; style?: 'solid' | 'dashed' | 'dotted' }[] = []
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
      levels.push({ id: `position-${symbolPosition.id}-sl`, label: 'Pos SL', price: symbolPosition.stopLoss, color: '#b42318', style: 'dotted' })
    }
    if (symbolPosition.takeProfit1 != null) {
      levels.push({ id: `position-${symbolPosition.id}-tp1`, label: 'Pos TP1', price: symbolPosition.takeProfit1, color: '#13795b', style: 'dotted' })
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

async function refreshData() {
  isRefreshing.value = true
  errorMessage.value = ''

  try {
    await getHealth()
    const [nextSignals, nextOrders, nextPositions, nextAuditLogs, nextRiskSettings, nextBrokerMode, nextBrokerSettings] = await Promise.all([
      getSignals(),
      getOrders(),
      getPositions(),
      getAuditLogs(),
      getRiskSettings(),
      getBrokerMode(),
      getBrokerSettings(),
    ])

    signals.value = nextSignals
    orders.value = nextOrders
    positions.value = nextPositions
    auditLogs.value = nextAuditLogs
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

  try {
    chartCandles.value = await getCandles(chartSymbol.value, selectedTimeframe.value)
  } catch (error) {
    chartError.value = error instanceof Error ? error.message : 'Market data unavailable'
  } finally {
    isChartRefreshInFlight = false
    if (showLoading) {
      isChartLoading.value = false
    }
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

  const trade = {
    type: 'entry' as const,
    direction,
    contracts: Number(manualTrade.value.contracts),
    symbol: chartSymbol.value,
    reference_price: Number(entryPrice),
    attach_protection: true,
    protection_distance: 100,
  }

  if (requireChartTradeConfirmation.value && !window.confirm(`Submit ${direction} market order for ${trade.contracts} ${trade.symbol} at reference price ${formatPrice(trade.reference_price)} with 100 point TP/SL?`)) {
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
    ibkr_paper: { ...settings.ibkr_paper },
    ibkr_live: { ...settings.ibkr_live },
  }
}

function normalizeBrokerSettings(settings: BrokerSettings): BrokerSettings {
  return {
    mode: settings.mode,
    ibkr_environment: settings.ibkr_environment,
    ibkr_paper: normalizeIBKRSettings(settings.ibkr_paper),
    ibkr_live: normalizeIBKRSettings(settings.ibkr_live),
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

onMounted(() => {
  refreshData()
  refreshCandles()
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
})

onUnmounted(() => {
  if (refreshTimer) window.clearInterval(refreshTimer)
  if (chartRefreshTimer) window.clearInterval(chartRefreshTimer)
  realtimeClient?.stop()
})

watch([chartSymbol, selectedTimeframe], () => {
  refreshCandles()
  restartChartRefreshTimer()
})

watch(activeTab, (tab) => {
  if (tab === 'chart') {
    refreshCandles(false)
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

      <button class="icon-button" type="button" title="Refresh" :disabled="isRefreshing" @click="refreshData">
        <RefreshCw :size="20" :class="{ spinning: isRefreshing }" />
      </button>
    </header>

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
        <Activity :size="20" />
        <div>
          <span>Working Orders</span>
          <strong>{{ workingOrderCount }}</strong>
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
        :error-message="chartError"
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
          <h2>Open Positions</h2>
          <div class="panel-actions">
            <button
              class="action-button danger"
              type="button"
              :disabled="Boolean(activeAction) || positions.length === 0"
              @click="handleFlatten"
            >
              <AlertTriangle :size="16" />
              <span>{{ activeAction === 'flatten' ? 'Flattening' : 'Flatten' }}</span>
            </button>
            <span class="count-pill">{{ positions.length }}</span>
          </div>
        </div>

        <div v-if="positions.length === 0" class="empty-state">No positions</div>
        <div v-else class="position-list">
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

          <div class="side-control">
            <button
              type="button"
              :class="{ active: brokerForm.mode === 'Paper' }"
              @click="brokerForm.mode = 'Paper'"
            >
              PAPER
            </button>
            <button
              type="button"
              :class="{ active: brokerForm.mode === 'IBKR' }"
              @click="brokerForm.mode = 'IBKR'"
            >
              IBKR
            </button>
          </div>

          <div class="side-control">
            <button
              type="button"
              :class="{ active: brokerForm.ibkr_environment === 'Paper' }"
              @click="brokerForm.ibkr_environment = 'Paper'"
            >
              IBKR PAPER
            </button>
            <button
              type="button"
              :class="{ active: brokerForm.ibkr_environment === 'Live' }"
              @click="brokerForm.ibkr_environment = 'Live'"
            >
              IBKR LIVE
            </button>
          </div>

          <div class="settings-grid">
            <label>
              <span>Paper Gateway Host</span>
              <input v-model="brokerForm.ibkr_paper.host" type="text" autocomplete="off" spellcheck="false" />
            </label>
            <label>
              <span>Paper Gateway Port</span>
              <input v-model.number="brokerForm.ibkr_paper.port" type="number" min="1" max="65535" />
            </label>
            <label>
              <span>Paper Client ID</span>
              <input v-model.number="brokerForm.ibkr_paper.client_id" type="number" min="1" />
            </label>
            <label>
              <span>Paper Account</span>
              <input v-model="brokerForm.ibkr_paper.account" type="text" autocomplete="off" spellcheck="false" />
            </label>
          </div>

          <div class="settings-grid">
            <label>
              <span>Live Gateway Host</span>
              <input v-model="brokerForm.ibkr_live.host" type="text" autocomplete="off" spellcheck="false" />
            </label>
            <label>
              <span>Live Gateway Port</span>
              <input v-model.number="brokerForm.ibkr_live.port" type="number" min="1" max="65535" />
            </label>
            <label>
              <span>Live Client ID</span>
              <input v-model.number="brokerForm.ibkr_live.client_id" type="number" min="1" />
            </label>
            <label>
              <span>Live Account</span>
              <input v-model="brokerForm.ibkr_live.account" type="text" autocomplete="off" spellcheck="false" />
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
              <strong>Reject Duplicate Signals</strong>
              <small>{{ riskForm.reject_duplicate_signals ? 'Enabled' : 'Disabled' }}</small>
            </span>
            <input v-model="riskForm.reject_duplicate_signals" type="checkbox" />
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

          <div class="settings-grid">
            <label>
              <span>Max Contracts</span>
              <input v-model.number="riskForm.max_contracts_per_signal" type="number" min="1" max="100" />
            </label>
            <label>
              <span>Duplicate Window Seconds</span>
              <input v-model.number="riskForm.duplicate_window_seconds" type="number" min="1" max="3600" />
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
  </main>
</template>
