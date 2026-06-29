<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import {
  CandlestickSeries,
  createChart,
  LineStyle,
  type CandlestickData,
  type IChartApi,
  type IPriceLine,
  type ISeriesApi,
} from 'lightweight-charts'
import { timeframes, type Timeframe } from '../services/market-data'
import { Activity, BarChart3, BriefcaseBusiness, CheckCircle2, Send, XCircle } from '@lucide/vue'

type ChartPriceLevel = {
  id: string
  label: string
  price: number
  color: string
  draggable?: 'stop_loss' | 'take_profit'
  style?: 'solid' | 'dashed' | 'dotted'
}

type ActiveTrade = {
  symbol: string
  direction: string
  quantity: number
  averagePrice: number
  currentPrice: number | null
  estimatedPnl: number | null
  maxProfit: number | null
  maxLoss: number | null
  stopLoss?: number | null
  takeProfit1?: number | null
  takeProfit2?: number | null
  isManaged: boolean
  workingOrders: number
  updatedAt?: string | null
}

type ChartTradeSettings = {
  contracts: number
}

type DailyPerformance = {
  date: string
  realized_pnl: number
  closed_trades: number
}

type PendingApprovalSignal = {
  id: number
  type: 'entry'
  direction: 'LONG' | 'SHORT'
  contracts: number
  entry_price: number
  stop_loss: number
  take_profit_1: number
  take_profit_2: number
  symbol: string
  status: string
  created_at: string
}

const props = defineProps<{
  activeTrade: ActiveTrade | null
  availableSymbols: string[]
  canSubmitTrade: boolean
  candles: CandlestickData[]
  chartTradeBlockedReason: string
  chartTradeMessage: string
  chartTradeSettings: ChartTradeSettings
  chartWorkingOrderCount: number
  dailyPerformance: DailyPerformance | null
  errorMessage: string
  isSubmittingTrade: boolean
  isSignalActionRunning: boolean
  isLoading: boolean
  levels: ChartPriceLevel[]
  marketDataWarning: string
  pendingApprovalSignal: PendingApprovalSignal | null
  requireTradeConfirmation: boolean
  symbol: string
  timeframe: Timeframe
}>()

const emit = defineEmits<{
  cancelOrders: []
  approveSignal: [signal: PendingApprovalSignal]
  chartTrade: [direction: 'LONG' | 'SHORT']
  closePosition: []
  flatten: []
  protectionDrag: [field: 'stop_loss' | 'take_profit', price: number]
  rejectSignal: [signal: PendingApprovalSignal]
  requireTradeConfirmationChange: [value: boolean]
  symbolChange: [symbol: string]
  tradeSettingChange: [field: 'contracts', value: number]
  timeframeChange: [timeframe: Timeframe]
}>()

const chartContainer = ref<HTMLDivElement | null>(null)
let chart: IChartApi | null = null
let candleSeries: ISeriesApi<'Candlestick'> | null = null
let priceLines: IPriceLine[] = []
let resizeObserver: ResizeObserver | null = null
let hasFitInitialData = false
let dragTarget: ChartPriceLevel['draggable'] | null = null
let dragTargetLabel = ''
let dragTargetColor = ''
let lastDragPrice: number | null = null
let previewPriceLine: IPriceLine | null = null

const latestCandle = computed(() => props.candles.at(-1))
const formattedSymbol = computed(() => props.symbol.trim().toUpperCase())
const tradePnlClass = computed(() => {
  if (props.activeTrade?.estimatedPnl == null) return ''
  return props.activeTrade.estimatedPnl >= 0 ? 'positive' : 'negative'
})
const dailyPnlClass = computed(() => {
  const pnl = props.dailyPerformance?.realized_pnl
  if (pnl == null || pnl === 0) return ''
  return pnl > 0 ? 'positive' : 'negative'
})

function formatPrice(value: number | null | undefined) {
  if (value == null) return '-'
  return value.toLocaleString('en-US', { maximumFractionDigits: 2 })
}

function formatCurrency(value: number | null | undefined) {
  if (value == null) return '-'
  return value.toLocaleString('en-US', {
    style: 'currency',
    currency: 'USD',
    maximumFractionDigits: 2,
  })
}

function setTimeframe(timeframe: Timeframe) {
  emit('timeframeChange', timeframe)
}

function setSymbol(event: Event) {
  const target = event.target as HTMLInputElement
  emit('symbolChange', target.value.trim().toUpperCase())
}

function setTradeSetting(field: 'contracts', event: Event) {
  const target = event.target as HTMLInputElement
  emit('tradeSettingChange', field, Number(target.value))
}

function submitChartTrade(direction: 'LONG' | 'SHORT') {
  if (props.isSubmittingTrade) return
  emit('chartTrade', direction)
}

function toLineStyle(style: ChartPriceLevel['style']) {
  if (style === 'dotted') return LineStyle.Dotted
  if (style === 'dashed') return LineStyle.Dashed
  return LineStyle.Solid
}

function toLineWidth(level: ChartPriceLevel) {
  if (level.label === 'Avg') return 1
  if (level.style === 'dotted') return 1
  return 2
}

function uniquePriceLevels(levels: ChartPriceLevel[]) {
  const seen = new Set<string>()
  return levels.filter((level) => {
    if (!Number.isFinite(level.price)) return false
    const key = `${level.label}:${level.price.toFixed(2)}`
    if (seen.has(key)) return false
    seen.add(key)
    return true
  })
}

function syncPriceLines() {
  if (!candleSeries) return

  for (const priceLine of priceLines) {
    candleSeries.removePriceLine(priceLine)
  }

  priceLines = uniquePriceLevels(props.levels)
    .map((level) =>
      candleSeries!.createPriceLine({
        price: level.price,
        color: level.color,
        lineWidth: toLineWidth(level),
        lineStyle: toLineStyle(level.style),
        axisLabelVisible: true,
        title: level.label,
      }),
    )
}

function removePreviewPriceLine() {
  if (!candleSeries || !previewPriceLine) return
  candleSeries.removePriceLine(previewPriceLine)
  previewPriceLine = null
}

function syncPreviewPriceLine() {
  if (!candleSeries || !dragTarget || lastDragPrice == null) return

  removePreviewPriceLine()
  previewPriceLine = candleSeries.createPriceLine({
    price: lastDragPrice,
    color: dragTargetColor || '#0f766e',
    lineWidth: 2,
    lineStyle: LineStyle.Solid,
    axisLabelVisible: true,
    title: `${dragTargetLabel} ${lastDragPrice.toFixed(2)}`,
  })
}

function getPriceFromPointer(event: MouseEvent) {
  if (!chartContainer.value || !candleSeries) return null
  const bounds = chartContainer.value.getBoundingClientRect()
  const price = candleSeries.coordinateToPrice(event.clientY - bounds.top)
  return price == null ? null : Number(price)
}

function findDraggableLevelAtPrice(price: number) {
  const draggableLevels = props.levels.filter((level) => level.draggable)
  if (!chartContainer.value || !candleSeries) return null

  for (const level of draggableLevels) {
    const coordinate = candleSeries.priceToCoordinate(level.price)
    const pointerCoordinate = candleSeries.priceToCoordinate(price)
    if (coordinate == null || pointerCoordinate == null) continue
    if (Math.abs(coordinate - pointerCoordinate) <= 10) return level
  }

  return null
}

function handlePointerDown(event: MouseEvent) {
  if (props.marketDataWarning) return

  const price = getPriceFromPointer(event)
  if (price == null) return

  const targetLevel = findDraggableLevelAtPrice(price)
  if (!targetLevel?.draggable) return

  dragTarget = targetLevel.draggable
  dragTargetLabel = targetLevel.label
  dragTargetColor = targetLevel.color
  lastDragPrice = Math.round(price * 4) / 4
  chart?.applyOptions({ handleScroll: false, handleScale: false })
  chartContainer.value?.classList.add('dragging-protection')
  syncPreviewPriceLine()
  event.preventDefault()
  event.stopPropagation()
}

function handlePointerMove(event: MouseEvent) {
  if (!dragTarget) return
  const price = getPriceFromPointer(event)
  if (price == null) return

  lastDragPrice = Math.round(price * 4) / 4
  syncPreviewPriceLine()
  event.preventDefault()
  event.stopPropagation()
}

function finishProtectionDrag(event?: MouseEvent) {
  if (dragTarget && lastDragPrice != null) {
    emit('protectionDrag', dragTarget, lastDragPrice)
  }

  removePreviewPriceLine()
  chart?.applyOptions({ handleScroll: true, handleScale: true })
  dragTarget = null
  dragTargetLabel = ''
  dragTargetColor = ''
  lastDragPrice = null
  chartContainer.value?.classList.remove('dragging-protection')
  event?.preventDefault()
  event?.stopPropagation()
}

function renderChart() {
  if (!chartContainer.value) return

  chart = createChart(chartContainer.value, {
    autoSize: true,
    layout: {
      background: { color: '#ffffff' },
      textColor: '#4b5563',
      fontFamily: 'Inter, system-ui, sans-serif',
      fontSize: 12,
    },
    grid: {
      vertLines: { color: '#edf0f2' },
      horzLines: { color: '#edf0f2' },
    },
    rightPriceScale: {
      borderColor: '#dfe5e7',
    },
    timeScale: {
      borderColor: '#dfe5e7',
      timeVisible: true,
      secondsVisible: false,
    },
    crosshair: {
      mode: 1,
    },
  })

  const series = chart.addSeries(CandlestickSeries, {
    upColor: '#13795b',
    downColor: '#b42318',
    borderUpColor: '#13795b',
    borderDownColor: '#b42318',
    wickUpColor: '#13795b',
    wickDownColor: '#b42318',
  })

  candleSeries = series
  series.setData(props.candles)
  syncPriceLines()
  fitInitialData()

  resizeObserver = new ResizeObserver(() => {
    chart?.applyOptions({ autoSize: true })
  })
  resizeObserver.observe(chartContainer.value)
  chartContainer.value.addEventListener('mousedown', handlePointerDown, true)
  window.addEventListener('mousemove', handlePointerMove)
  window.addEventListener('mouseup', finishProtectionDrag)
}

function fitInitialData() {
  if (!chart || props.candles.length === 0 || hasFitInitialData) return
  chart.timeScale().fitContent()
  hasFitInitialData = true
}

watch(
  () => props.candles,
  (candles) => {
    candleSeries?.setData(candles)
    fitInitialData()
  },
)

watch(
  () => [props.symbol, props.timeframe],
  () => {
    hasFitInitialData = false
  },
)

watch(
  () => props.levels,
  () => {
    syncPriceLines()
    syncPreviewPriceLine()
  },
  { deep: true },
)

onMounted(async () => {
  await nextTick()
  renderChart()
})

onUnmounted(() => {
  chartContainer.value?.removeEventListener('mousedown', handlePointerDown, true)
  window.removeEventListener('mousemove', handlePointerMove)
  window.removeEventListener('mouseup', finishProtectionDrag)
  resizeObserver?.disconnect()
  chart?.remove()
})
</script>

<template>
  <section class="chart-panel">
    <header class="chart-header">
      <div class="chart-title">
        <BarChart3 :size="20" />
        <div>
          <h2>{{ formattedSymbol }}</h2>
          <span>{{ candles.length }} candles from Trading Engine</span>
        </div>
      </div>

      <div class="chart-tools">
        <label class="chart-symbol-field">
          <span>Symbol</span>
          <input
            :value="symbol"
            list="chart-symbols"
            type="text"
            autocomplete="off"
            spellcheck="false"
            @change="setSymbol"
            @keyup.enter="setSymbol"
          />
          <datalist id="chart-symbols">
            <option v-for="item in availableSymbols" :key="item" :value="item" />
          </datalist>
        </label>

      </div>
    </header>

    <section class="daily-performance-strip">
      <div>
        <span>Daily P&L</span>
        <strong :class="dailyPnlClass">{{ formatCurrency(dailyPerformance?.realized_pnl) }}</strong>
      </div>
      <div>
        <span>Closed Trades</span>
        <strong>{{ dailyPerformance?.closed_trades ?? 0 }}</strong>
      </div>
      <div>
        <span>Date</span>
        <strong>{{ dailyPerformance?.date ?? '-' }}</strong>
      </div>
    </section>

    <section class="active-trade" :class="activeTrade ? activeTrade.direction.toLowerCase() : 'empty'">
      <div class="active-trade-main">
        <span>Active Position</span>
        <strong v-if="activeTrade">
          {{ activeTrade.direction }} {{ activeTrade.quantity }} {{ activeTrade.symbol }}
          <span class="ownership-badge" :class="activeTrade.isManaged ? 'managed' : 'unmanaged'">
            {{ activeTrade.isManaged ? 'Managed' : 'Unmanaged' }}
          </span>
        </strong>
        <strong v-else>No active position for {{ formattedSymbol }}</strong>
      </div>

      <div v-if="activeTrade" class="active-trade-grid">
        <div>
          <span>Avg</span>
          <strong>{{ formatPrice(activeTrade.averagePrice) }}</strong>
        </div>
        <div>
          <span>Last</span>
          <strong>{{ formatPrice(activeTrade.currentPrice) }}</strong>
        </div>
        <div>
          <span>P&L Est.</span>
          <strong :class="tradePnlClass">{{ formatCurrency(activeTrade.estimatedPnl) }}</strong>
        </div>
        <div>
          <span>Max Profit</span>
          <strong class="positive">{{ formatCurrency(activeTrade.maxProfit) }}</strong>
        </div>
        <div>
          <span>Max Loss</span>
          <strong class="negative">{{ formatCurrency(activeTrade.maxLoss) }}</strong>
        </div>
        <div>
          <span>SL</span>
          <strong>{{ formatPrice(activeTrade.stopLoss) }}</strong>
        </div>
        <div>
          <span>TP</span>
          <strong>{{ formatPrice(activeTrade.takeProfit1) }} / {{ formatPrice(activeTrade.takeProfit2) }}</strong>
        </div>
        <div>
          <span>Working Orders</span>
          <strong>{{ activeTrade.workingOrders }}</strong>
        </div>
      </div>
    </section>

    <section v-if="pendingApprovalSignal" class="pending-signal-panel">
      <div class="pending-signal-main">
        <span>Pending Signal</span>
        <strong>
          {{ pendingApprovalSignal.direction }} {{ pendingApprovalSignal.contracts }} {{ pendingApprovalSignal.symbol }}
        </strong>
      </div>
      <div class="pending-signal-details">
        <div>
          <span>Entry</span>
          <strong>{{ formatPrice(pendingApprovalSignal.entry_price) }}</strong>
        </div>
        <div>
          <span>SL</span>
          <strong>{{ formatPrice(pendingApprovalSignal.stop_loss) }}</strong>
        </div>
        <div>
          <span>TP</span>
          <strong>{{ formatPrice(pendingApprovalSignal.take_profit_1) }}</strong>
        </div>
      </div>
      <div class="pending-signal-actions">
        <button
          class="chart-trade-button buy"
          type="button"
          :disabled="isSignalActionRunning"
          @click="emit('approveSignal', pendingApprovalSignal)"
        >
          <CheckCircle2 :size="16" />
          <span>Approve</span>
        </button>
        <button
          class="chart-trade-button danger"
          type="button"
          :disabled="isSignalActionRunning"
          @click="emit('rejectSignal', pendingApprovalSignal)"
        >
          <XCircle :size="16" />
          <span>Reject</span>
        </button>
      </div>
    </section>

    <section class="chart-trade-panel">
      <div class="chart-trade-header">
        <div>
          <span>Chart Trade</span>
        </div>
        <small v-if="chartTradeBlockedReason || chartTradeMessage">{{ chartTradeBlockedReason || chartTradeMessage }}</small>
      </div>

      <div class="chart-trade-inputs">
        <label>
          <span>Contracts</span>
          <input
            :value="chartTradeSettings.contracts"
            type="number"
            min="1"
            max="100"
            @change="setTradeSetting('contracts', $event)"
          />
        </label>
        <label class="chart-confirm-toggle">
          <span>Confirm</span>
          <input
            :checked="requireTradeConfirmation"
            type="checkbox"
            @change="emit('requireTradeConfirmationChange', ($event.target as HTMLInputElement).checked)"
          />
        </label>
      </div>

      <div class="chart-trade-actions">
        <button
          class="chart-trade-button buy"
          type="button"
          :class="{ blocked: !canSubmitTrade }"
          :disabled="isSubmittingTrade"
          :title="chartTradeBlockedReason || 'Submit buy market order'"
          @click="submitChartTrade('LONG')"
        >
          <Send :size="16" />
          <span>{{ isSubmittingTrade ? 'Submitting' : 'Buy MKT' }}</span>
        </button>
        <button
          class="chart-trade-button sell"
          type="button"
          :class="{ blocked: !canSubmitTrade }"
          :disabled="isSubmittingTrade"
          :title="chartTradeBlockedReason || 'Submit sell market order'"
          @click="submitChartTrade('SHORT')"
        >
          <Send :size="16" />
          <span>{{ isSubmittingTrade ? 'Submitting' : 'Sell MKT' }}</span>
        </button>
        <button
          class="chart-trade-button secondary"
          type="button"
          :disabled="!activeTrade"
          @click="emit('closePosition')"
        >
          <BriefcaseBusiness :size="16" />
          <span>Close</span>
        </button>
        <button
          class="chart-trade-button secondary"
          type="button"
          :disabled="chartWorkingOrderCount === 0"
          @click="emit('cancelOrders')"
        >
          <XCircle :size="16" />
          <span>Cancel</span>
        </button>
        <button
          class="chart-trade-button danger"
          type="button"
          @click="emit('flatten')"
        >
          <Activity :size="16" />
          <span>Flatten</span>
        </button>
      </div>
    </section>

    <div class="chart-timeframe-bar">
      <div class="timeframe-control" aria-label="Timeframe">
        <button
          v-for="item in timeframes"
          :key="item"
          type="button"
          :class="{ active: item === timeframe }"
          @click="setTimeframe(item)"
        >
          {{ item }}
        </button>
      </div>
    </div>

    <div class="chart-body">
      <div ref="chartContainer" class="chart-surface" />
      <div v-if="marketDataWarning" class="chart-warning">
        {{ marketDataWarning }}
      </div>
      <div v-if="isLoading" class="chart-overlay">Loading candles</div>
      <div v-else-if="errorMessage && candles.length === 0" class="chart-overlay error">{{ errorMessage }}</div>
      <div v-else-if="candles.length === 0" class="chart-overlay">No candles</div>
    </div>

    <footer class="chart-footer">
      <div>
        <span>Open</span>
        <strong>{{ latestCandle?.open.toFixed(2) ?? '-' }}</strong>
      </div>
      <div>
        <span>High</span>
        <strong>{{ latestCandle?.high.toFixed(2) ?? '-' }}</strong>
      </div>
      <div>
        <span>Low</span>
        <strong>{{ latestCandle?.low.toFixed(2) ?? '-' }}</strong>
      </div>
      <div>
        <span>Close</span>
        <strong>{{ latestCandle?.close.toFixed(2) ?? '-' }}</strong>
      </div>
    </footer>
  </section>
</template>
