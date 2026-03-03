<template>
  <div class="cash-flow-page">
    <!-- 页面标题 -->
    <div class="page-title-bar">
      <h2 class="page-title">資金繰り</h2>
    </div>

    <!-- 顶部统计卡片 -->
    <div class="summary-cards">
      <el-card v-for="card in summaryCards" :key="card.label" class="summary-card" shadow="hover">
        <div class="card-icon" :style="{ background: card.bg }">
          <el-icon :size="22" :color="card.color"><component :is="card.icon" /></el-icon>
        </div>
        <div class="card-body">
          <div class="card-value">¥{{ formatAmount(card.value) }}</div>
          <div class="card-label">{{ card.label }}</div>
        </div>
      </el-card>
    </div>

    <!-- 图表区域 -->
    <el-card class="chart-card" shadow="never">
      <template #header>
        <div class="card-header">
          <span class="card-title">資金繰り推移</span>
          <div class="header-actions">
            <el-radio-group v-model="chartPeriod" size="small" @change="updateChart">
              <el-radio-button value="daily">日次</el-radio-button>
              <el-radio-button value="weekly">週次</el-radio-button>
              <el-radio-button value="monthly">月次</el-radio-button>
            </el-radio-group>
            <el-date-picker
              v-model="dateRange"
              type="daterange"
              range-separator="〜"
              start-placeholder="開始日"
              end-placeholder="終了日"
              value-format="YYYY-MM-DD"
              size="small"
              style="width: 260px"
              @change="updateChart"
            />
          </div>
        </div>
      </template>
      <div ref="chartRef" class="echarts-container"></div>
    </el-card>

    <!-- 明细表区域 -->
    <el-card class="detail-card" shadow="never">
      <template #header>
        <div class="card-header">
          <span class="card-title">入出金明細</span>
          <div class="header-actions">
            <el-select v-model="detailFilter" size="small" placeholder="種別" clearable style="width: 160px">
              <el-option label="すべて" value="" />
              <el-option label="入金のみ" value="income" />
              <el-option label="出金のみ" value="expense" />
            </el-select>
          </div>
        </div>
      </template>
      <el-table :data="filteredTableData" stripe size="small" :header-cell-style="{ background: '#f8fafc', color: '#475569', fontWeight: 600 }">
        <el-table-column label="日付" width="110" prop="date" />
        <el-table-column label="入出金種別" min-width="220">
          <template #default="{ row }">
            <el-tag :type="row.isIncome ? 'danger' : 'success'" size="small" effect="plain" style="margin-right: 6px">
              {{ row.isIncome ? '入金' : '出金' }}
            </el-tag>
            <span>{{ row.type }}</span>
          </template>
        </el-table-column>
        <el-table-column label="金額" width="160" align="right">
          <template #default="{ row }">
            <span :style="{ color: row.isIncome ? '#ef4444' : '#22c55e', fontWeight: 600, fontVariantNumeric: 'tabular-nums' }">
              {{ row.isIncome ? '+' : '-' }}¥{{ formatAmount(Math.abs(row.amount)) }}
            </span>
          </template>
        </el-table-column>
        <el-table-column label="取引先" min-width="200" prop="partner" />
        <el-table-column label="伝票番号" width="150">
          <template #default="{ row }">
            <el-link type="primary" :underline="false" style="font-size: 12px">{{ row.voucherNo }}</el-link>
          </template>
        </el-table-column>
        <el-table-column label="備考" min-width="200" prop="note" show-overflow-tooltip />
      </el-table>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick, shallowRef } from 'vue'
import { Wallet, TrendCharts, ArrowUp, ArrowDown } from '@element-plus/icons-vue'
import * as echarts from 'echarts/core'
import { BarChart, LineChart } from 'echarts/charts'
import {
  TitleComponent, TooltipComponent, LegendComponent,
  GridComponent, DataZoomComponent, MarkLineComponent
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'

echarts.use([
  BarChart, LineChart, TitleComponent, TooltipComponent,
  LegendComponent, GridComponent, DataZoomComponent,
  MarkLineComponent, CanvasRenderer
])

const chartRef = ref<HTMLElement>()
let chartInstance: echarts.ECharts | null = null
const dateRange = ref(['2025-10-01', '2026-02-28'])
const chartPeriod = ref('monthly')
const detailFilter = ref('')

const formatAmount = (n: number) => n.toLocaleString('ja-JP')

// 模拟月次数据
const monthlyData = {
  categories: ['2025/10', '2025/11', '2025/12', '2026/01', '2026/02'],
  income:     [12500000,  9800000,  15200000,  8400000,  11300000],
  expense:    [8900000,   11200000, 9600000,   10500000, 7800000],
  balance:    [48600000,  47200000, 52800000,  50700000, 54200000],
}

const summaryCards = computed(() => [
  { label: '現在残高',     value: 54200000,  icon: Wallet,      color: '#3b82f6', bg: '#eff6ff' },
  { label: '今月入金合計',  value: 11300000,  icon: ArrowDown,   color: '#ef4444', bg: '#fef2f2' },
  { label: '今月出金合計',  value: 7800000,   icon: ArrowUp,     color: '#22c55e', bg: '#f0fdf4' },
  { label: '今月収支',     value: 3500000,   icon: TrendCharts, color: '#8b5cf6', bg: '#f5f3ff' },
])

const tableData = ref([
  { date: '2026/02/28', type: '受注より入金（請求済）',     amount: 2825000,  partner: '得意先 A社',                 voucherNo: 'RC-2602-001', note: 'A社 2026年01月請求書', isIncome: true },
  { date: '2026/02/25', type: 'その他入金',              amount: 1380160,  partner: '得意先 Bホールディングス(株)', voucherNo: 'RC-2602-002', note: 'Bホールディングス(株) 2026年01月請求書', isIncome: true },
  { date: '2026/02/20', type: '発注より出金（請求済）',     amount: 1638000,  partner: '仕入先 Cソフト',             voucherNo: 'PY-2602-001', note: 'Cソフト 2026年01月請求書', isIncome: false },
  { date: '2026/02/18', type: '受注より入金（請求済）',     amount: 3200000,  partner: '得意先 D社',                 voucherNo: 'RC-2602-003', note: 'D社 2026年01月請求書', isIncome: true },
  { date: '2026/02/15', type: '発注より出金（請求済）',     amount: 2880000,  partner: '仕入先 E工業',               voucherNo: 'PY-2602-002', note: 'E工業 部品仕入れ', isIncome: false },
  { date: '2026/02/10', type: '給与支払',                amount: 1850000,  partner: '従業員',                     voucherNo: 'PY-2602-003', note: '2026年02月給与', isIncome: false },
  { date: '2026/02/08', type: '受注より入金（請求済）',     amount: 1894840,  partner: '得意先 F社',                 voucherNo: 'RC-2602-004', note: 'F社 2026年01月請求書', isIncome: true },
  { date: '2026/02/05', type: '家賃・光熱費',             amount: 432000,   partner: 'ビル管理会社',               voucherNo: 'PY-2602-004', note: '2026年02月オフィス賃料', isIncome: false },
  { date: '2026/02/03', type: '受注より入金（請求済）',     amount: 2000000,  partner: '得意先 G社',                 voucherNo: 'RC-2602-005', note: 'G社 スポット案件', isIncome: true },
  { date: '2026/02/01', type: '社会保険料',               amount: 1000000,  partner: '年金事務所',                 voucherNo: 'PY-2602-005', note: '2026年01月分社会保険料', isIncome: false },
])

const filteredTableData = computed(() => {
  if (!detailFilter.value) return tableData.value
  return tableData.value.filter(r => detailFilter.value === 'income' ? r.isIncome : !r.isIncome)
})

function buildChartOption() {
  const d = monthlyData
  return {
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'cross', crossStyle: { color: '#999' } },
      backgroundColor: 'rgba(255,255,255,0.97)',
      borderColor: '#e2e8f0',
      borderRadius: 12,
      padding: [10, 14],
      extraCssText: 'box-shadow: 0 4px 16px rgba(0,0,0,0.10);',
      textStyle: { color: '#334155', fontSize: 12 },
      formatter(params: any[]) {
        let s = `<div style="font-weight:600;margin-bottom:4px">${params[0].axisValue}</div>`
        for (const p of params) {
          const marker = `<span style="display:inline-block;width:10px;height:10px;border-radius:2px;background:${p.color};margin-right:6px"></span>`
          s += `<div style="display:flex;justify-content:space-between;gap:24px">${marker}${p.seriesName}<span style="font-weight:600;font-variant-numeric:tabular-nums">¥${Number(p.value).toLocaleString('ja-JP')}</span></div>`
        }
        return s
      }
    },
    legend: {
      top: 0,
      itemWidth: 14,
      itemHeight: 10,
      textStyle: { fontSize: 12, color: '#64748b' },
    },
    grid: { top: 40, left: 20, right: 20, bottom: 50, containLabel: true },
    dataZoom: [{ type: 'inside', start: 0, end: 100 }],
    xAxis: {
      type: 'category',
      data: d.categories,
      axisLine: { lineStyle: { color: '#e2e8f0' } },
      axisTick: { show: false },
      axisLabel: { color: '#64748b', fontSize: 11 },
    },
    yAxis: [
      {
        type: 'value',
        name: '入出金額',
        nameTextStyle: { color: '#94a3b8', fontSize: 11, padding: [0, 0, 0, 40] },
        axisLabel: { color: '#64748b', fontSize: 11, formatter: (v: number) => v >= 1e6 ? `${(v / 1e6).toFixed(0)}M` : `${(v / 1e3).toFixed(0)}K` },
        splitLine: { lineStyle: { color: '#f1f5f9', type: 'dashed' } },
        axisLine: { show: false },
        axisTick: { show: false },
      },
      {
        type: 'value',
        name: '資金残高',
        nameTextStyle: { color: '#94a3b8', fontSize: 11, padding: [0, 40, 0, 0] },
        axisLabel: { color: '#64748b', fontSize: 11, formatter: (v: number) => v >= 1e6 ? `${(v / 1e6).toFixed(0)}M` : `${(v / 1e3).toFixed(0)}K` },
        splitLine: { show: false },
        axisLine: { show: false },
        axisTick: { show: false },
      }
    ],
    series: [
      {
        name: '入金',
        type: 'bar',
        barWidth: '28%',
        itemStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{ offset: 0, color: '#fca5a5' }, { offset: 1, color: '#fecaca' }]), borderRadius: [4, 4, 0, 0] },
        data: d.income,
      },
      {
        name: '出金',
        type: 'bar',
        barWidth: '28%',
        itemStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{ offset: 0, color: '#86efac' }, { offset: 1, color: '#bbf7d0' }]), borderRadius: [4, 4, 0, 0] },
        data: d.expense,
      },
      {
        name: '資金残高',
        type: 'line',
        yAxisIndex: 1,
        smooth: true,
        symbol: 'circle',
        symbolSize: 8,
        lineStyle: { width: 3, color: '#6366f1' },
        itemStyle: { color: '#6366f1', borderWidth: 2, borderColor: '#fff' },
        areaStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{ offset: 0, color: 'rgba(99,102,241,0.15)' }, { offset: 1, color: 'rgba(99,102,241,0.01)' }]) },
        data: d.balance,
      }
    ],
    animationDuration: 800,
    animationEasing: 'cubicOut',
  }
}

function initChart() {
  if (!chartRef.value) return
  chartInstance = echarts.init(chartRef.value)
  chartInstance.setOption(buildChartOption())
}

function updateChart() {
  if (chartInstance) chartInstance.setOption(buildChartOption())
}

function handleResize() {
  chartInstance?.resize()
}

onMounted(() => {
  nextTick(initChart)
  window.addEventListener('resize', handleResize)
})

onUnmounted(() => {
  window.removeEventListener('resize', handleResize)
  chartInstance?.dispose()
})
</script>

<style scoped>
.cash-flow-page {
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 16px;
  background: #f8fafc;
  min-height: 100%;
}

.page-title-bar {
  margin-bottom: 4px;
}

.page-title {
  margin: 0;
  font-size: 20px;
  font-weight: 700;
  color: #1e293b;
}

.summary-cards {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 14px;
}

.summary-card {
  border-radius: 12px;
  border: none;
}

.summary-card :deep(.el-card__body) {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 18px 20px;
}

.card-icon {
  width: 44px;
  height: 44px;
  border-radius: 10px;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}

.card-body {
  min-width: 0;
}

.card-value {
  font-size: 20px;
  font-weight: 700;
  color: #1e293b;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
}

.card-label {
  font-size: 12px;
  color: #94a3b8;
  margin-top: 2px;
}

.chart-card,
.detail-card {
  border-radius: 12px;
  border: none;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}

.card-title {
  font-size: 15px;
  font-weight: 600;
  color: #1e293b;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 10px;
}

.echarts-container {
  width: 100%;
  height: 380px;
}

.detail-card :deep(.el-table) {
  font-size: 13px;
}
</style>
