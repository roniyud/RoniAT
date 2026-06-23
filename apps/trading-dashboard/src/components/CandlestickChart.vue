<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import {
  CandlestickSeries,
  createChart,
  type CandlestickData,
  type IChartApi,
  type ISeriesApi,
} from 'lightweight-charts'
import { timeframes, type Timeframe } from '../services/market-data'
import { BarChart3 } from '@lucide/vue'

const props = defineProps<{
  candles: CandlestickData[]
  symbol: string
  timeframe: Timeframe
}>()

const emit = defineEmits<{
  timeframeChange: [timeframe: Timeframe]
}>()

const chartContainer = ref<HTMLDivElement | null>(null)
let chart: IChartApi | null = null
let candleSeries: ISeriesApi<'Candlestick'> | null = null
let resizeObserver: ResizeObserver | null = null

const latestCandle = computed(() => props.candles.at(-1))

function setTimeframe(timeframe: Timeframe) {
  emit('timeframeChange', timeframe)
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
          <h2>{{ symbol }}</h2>
          <span>{{ candles.length }} candles</span>
        </div>
      </div>

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
    </header>

    <div ref="chartContainer" class="chart-surface" />

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
