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
  stop_loss: number
  take_profit_1: number
  take_profit_2: number
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
  errorMessage: string
  isSubmittingTrade: boolean
  isLoading: boolean
  levels: ChartPriceLevel[]
  symbol: string
  timeframe: Timeframe
}>()

const emit = defineEmits<{
  cancelOrders: []
  chartTrade: [direction: 'LONG' | 'SHORT']
  closePosition: []
  flatten: []
  symbolChange: [symbol: string]
  tradeSettingChange: [field: 'contracts' | 'stop_loss' | 'take_profit_1' | 'take_profit_2', value: number]
  timeframeChange: [timeframe: Timeframe]
}>()

const chartContainer = ref<HTMLDivElement | null>(null)
let chart: IChartApi | null = null
let candleSeries: ISeriesApi<'Candlestick'> | null = null
let priceLines: IPriceLine[] = []
let resizeObserver: ResizeObserver | null = null

const latestCandle = computed(() => props.candles.at(-1))
const formattedSymbol = computed(() => props.symbol.trim().toUpperCase())
const tradePnlClass = computed(() => {
  if (props.activeTrade?.estimatedPnl == null) return ''
  return props.activeTrade.estimatedPnl >= 0 ? 'positive' : 'negative'
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

function setTradeSetting(field: 'contracts' | 'stop_loss' | 'take_profit_1' | 'take_profit_2', event: Event) {
  const target = event.target as HTMLInputElement
  emit('tradeSettingChange', field, Number(target.value))
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
  chart.timeScale().fitContent()

  resizeObserver = new ResizeObserver(() => {
    chart?.applyOptions({ autoSize: true })
  })
  resizeObserver.observe(chartContainer.value)
}

watch(
  () => props.candles,
  (candles) => {
    candleSeries?.setData(candles)
    chart?.timeScale().fitContent()
  },
)

watch(
  () => props.levels,
  () => {
    syncPriceLines()
  },
  { deep: true },
)

onMounted(async () => {
  await nextTick()
  renderChart()
})

onUnmounted(() => {
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
        <label>
          <span>SL</span>
          <input
            :value="chartTradeSettings.stop_loss"
            type="number"
            step="0.25"
            @change="setTradeSetting('stop_loss', $event)"
          />
        </label>
        <label>
          <span>TP1</span>
          <input
            :value="chartTradeSettings.take_profit_1"
            type="number"
            step="0.25"
            @change="setTradeSetting('take_profit_1', $event)"
          />
        </label>
        <label>
          <span>TP2</span>
          <input
            :value="chartTradeSettings.take_profit_2"
            type="number"
            step="0.25"
            @change="setTradeSetting('take_profit_2', $event)"
          />
        </label>
      </div>

      <div class="chart-trade-actions">
        <button
          class="chart-trade-button buy"
          type="button"
          :disabled="!canSubmitTrade"
          @click="emit('chartTrade', 'LONG')"
        >
          <Send :size="16" />
          <span>{{ isSubmittingTrade ? 'Submitting' : 'Buy MKT' }}</span>
        </button>
        <button
          class="chart-trade-button sell"
          type="button"
          :disabled="!canSubmitTrade"
          @click="emit('chartTrade', 'SHORT')"
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
