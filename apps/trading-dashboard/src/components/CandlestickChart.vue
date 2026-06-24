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
import { Activity, BarChart3, BriefcaseBusiness, Send, XCircle } from '@lucide/vue'

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
  stopLoss?: number | null
  takeProfit1?: number | null
  takeProfit2?: number | null
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
  isLoading: boolean
  levels: ChartPriceLevel[]
  marketDataWarning: string
  requireTradeConfirmation: boolean
  symbol: string
  timeframe: Timeframe
}>()

const emit = defineEmits<{
  cancelOrders: []
  chartTrade: [direction: 'LONG' | 'SHORT']
  closePosition: []
  flatten: []
  protectionDrag: [field: 'stop_loss' | 'take_profit', price: number]
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

function syncPriceLines() {
  if (!candleSeries) return

  for (const priceLine of priceLines) {
    candleSeries.removePriceLine(priceLine)
  }

  priceLines = props.levels
    .filter((level) => Number.isFinite(level.price))
    .map((level) =>
      candleSeries!.createPriceLine({
        price: level.price,
        color: level.color,
        lineWidth: 2,
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
    lineWidth: 3,
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
    </header>

    <section class="active-trade" :class="activeTrade ? activeTrade.direction.toLowerCase() : 'empty'">
      <div class="active-trade-main">
        <span>Active Position</span>
        <strong v-if="activeTrade">
          {{ activeTrade.direction }} {{ activeTrade.quantity }} {{ activeTrade.symbol }}
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

    <section class="chart-trade-panel">
      <div class="chart-trade-header">
        <div>
          <span>Chart Trade</span>
          <strong>{{ formattedSymbol }}</strong>
        </div>
        <small>{{ chartTradeBlockedReason || chartTradeMessage || 'Ready' }}</small>
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

    <div v-if="levels.length" class="chart-levels" aria-label="Chart levels">
      <span v-for="level in levels" :key="level.id" class="level-chip">
        <i :style="{ backgroundColor: level.color }" />
        {{ level.label }}
        <strong>{{ level.price.toFixed(2) }}</strong>
      </span>
    </div>

    <div class="chart-body">
      <div ref="chartContainer" class="chart-surface" />
      <div v-if="marketDataWarning" class="chart-warning">
        {{ marketDataWarning }}
      </div>
      <div v-if="isLoading" class="chart-overlay">Loading candles</div>
      <div v-else-if="errorMessage" class="chart-overlay error">{{ errorMessage }}</div>
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
