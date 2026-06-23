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
  Server,
  Settings2,
  ShieldCheck,
  WifiOff,
} from '@lucide/vue'
import CandlestickChart from './components/CandlestickChart.vue'
import {
  cancelWorkingOrders,
  closePosition,
  flattenPaperAccount,
  getAuditLogs,
  getBrokerMode,
  getHealth,
  getOrders,
  getPositions,
  getRiskSettings,
  getSignals,
  updateRiskSettings,
} from './services/api'
import { getCandles, type Timeframe } from './services/market-data'
import type { CandlestickData } from 'lightweight-charts'
import { createTradingRealtimeClient, type RealtimeStatus, type TradingUpdate } from './services/realtime'
import type { ApiState, AuditLogRecord, OrderRecord, PositionRecord, RiskSettings, TradingSignal } from './services/types'

const apiState = ref<ApiState>('loading')
const activeTab = ref<'chart' | 'signals' | 'orders' | 'positions' | 'audit' | 'settings'>('chart')
const signals = ref<TradingSignal[]>([])
const orders = ref<OrderRecord[]>([])
const positions = ref<PositionRecord[]>([])
const auditLogs = ref<AuditLogRecord[]>([])
const riskSettings = ref<RiskSettings | null>(null)
const riskForm = ref<RiskSettings | null>(null)
const brokerMode = ref('Paper')
const lastUpdated = ref<Date | null>(null)
const errorMessage = ref('')
const isRefreshing = ref(false)
const activeAction = ref('')
const isSavingRisk = ref(false)
const riskSaveMessage = ref('')
const selectedTimeframe = ref<Timeframe>('5m')
const chartCandles = ref<CandlestickData[]>([])
const chartError = ref('')
const isChartLoading = ref(false)
const realtimeStatus = ref<RealtimeStatus>('disconnected')
const lastRealtimeEvent = ref<TradingUpdate | null>(null)
let refreshTimer: number | undefined
let realtimeClient: ReturnType<typeof createTradingRealtimeClient> | undefined

const totalOpenQuantity = computed(() => positions.value.reduce((total, position) => total + Math.abs(position.quantity), 0))
const workingOrders = computed(() => orders.value.filter((order) => order.status === 'working'))
const workingOrderCount = computed(() => workingOrders.value.length)
const filledOrderCount = computed(() => orders.value.filter((order) => order.status === 'filled').length)
const cancelledOrderCount = computed(() => orders.value.filter((order) => order.status === 'cancelled').length)
const rejectedSignalCount = computed(() => signals.value.filter((signal) => signal.status === 'rejected_by_risk').length)
const approvedSignalCount = computed(() => signals.value.length - rejectedSignalCount.value)
const chartSymbol = computed(() => signals.value[0]?.symbol || positions.value[0]?.symbol || 'MNQ1!')

async function refreshData() {
  isRefreshing.value = true
  errorMessage.value = ''

  try {
    await getHealth()
    const [nextSignals, nextOrders, nextPositions, nextAuditLogs, nextRiskSettings, nextBrokerMode] = await Promise.all([
      getSignals(),
      getOrders(),
      getPositions(),
      getAuditLogs(),
      getRiskSettings(),
      getBrokerMode(),
    ])

    signals.value = nextSignals
    orders.value = nextOrders
    positions.value = nextPositions
    auditLogs.value = nextAuditLogs
    riskSettings.value = nextRiskSettings
    brokerMode.value = nextBrokerMode.mode
    if (!riskForm.value) {
      riskForm.value = { ...nextRiskSettings, allowed_symbols: [...nextRiskSettings.allowed_symbols] }
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

async function refreshCandles() {
  isChartLoading.value = true
  chartError.value = ''

  try {
    chartCandles.value = await getCandles(chartSymbol.value, selectedTimeframe.value)
  } catch (error) {
    chartError.value = error instanceof Error ? error.message : 'Market data unavailable'
  } finally {
    isChartLoading.value = false
  }
}

async function runAction(actionKey: string, confirmation: string, action: () => Promise<unknown>) {
  if (!window.confirm(confirmation)) return

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
    `Cancel all working paper orders ${label}?`,
    () => cancelWorkingOrders(symbol),
  )
}

async function handleClosePosition(symbol: string) {
  await runAction(
    `close-${symbol}`,
    `Close the paper position for ${symbol}?`,
    () => closePosition(symbol),
  )
}

async function handleFlatten() {
  await runAction(
    'flatten',
    'Flatten all paper positions and cancel all working paper orders?',
    () => flattenPaperAccount(),
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

onMounted(() => {
  refreshData()
  refreshCandles()
  refreshTimer = window.setInterval(refreshData, 5000)
  realtimeClient = createTradingRealtimeClient(
    (update) => {
      lastRealtimeEvent.value = update
      refreshData()
    },
    (status) => {
      realtimeStatus.value = status
    },
  )
  realtimeClient.start().catch((error) => {
    realtimeStatus.value = 'disconnected'
    console.warn('SignalR connection failed', error)
  })
})

onUnmounted(() => {
  if (refreshTimer) window.clearInterval(refreshTimer)
  realtimeClient?.stop()
})

watch([chartSymbol, selectedTimeframe], () => {
  refreshCandles()
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
          <strong>{{ riskSettings?.enable_auto_trading ? 'On' : 'Off' }}</strong>
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
        :candles="chartCandles"
        :error-message="chartError"
        :is-loading="isChartLoading"
        :symbol="chartSymbol"
        :timeframe="selectedTimeframe"
        @timeframe-change="selectedTimeframe = $event"
      />

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
            <h2>Risk Control</h2>
            <p class="panel-subtitle">Broker {{ brokerMode }} / Symbols {{ riskSettings?.allowed_symbols.join(', ') || '-' }}</p>
          </div>
          <span class="status-pill" :class="riskSettings?.enable_auto_trading ? 'paper-position-opened' : 'rejected-by-risk'">
            {{ riskSettings?.enable_auto_trading ? 'Auto On' : 'Auto Off' }}
          </span>
        </div>

        <form v-if="riskForm" class="settings-form" @submit.prevent="handleSaveRiskSettings">
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
