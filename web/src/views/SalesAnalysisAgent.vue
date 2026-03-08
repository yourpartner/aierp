<template>
  <div class="sales-analysis-page">
    <!-- 页面标题 -->
    <div class="page-title-bar">
      <div class="title-left">
        <el-icon :size="22" color="#0ea5e9"><TrendCharts /></el-icon>
        <h2 class="page-title">受発注分析Agent</h2>
      </div>
      <div class="title-actions">
        <el-date-picker
          v-model="analysisPeriod"
          type="month"
          value-format="YYYY-MM"
          placeholder="分析対象月"
          size="small"
          style="width: 160px"
          @change="runAnalysis"
        />
        <el-button type="primary" size="small" @click="runAnalysis" :loading="loading">
          <el-icon><Refresh /></el-icon>
          分析実行
        </el-button>
      </div>
    </div>

    <!-- リスクアラート -->
    <div v-if="risks.length" class="risk-alerts">
      <div v-for="(risk, i) in risks" :key="i" class="risk-alert" :class="'risk-' + risk.severity">
        <div class="risk-header">
          <el-tag :type="severityType(risk.severity)" size="small" effect="dark">{{ severityLabel(risk.severity) }}</el-tag>
          <span class="risk-category">{{ risk.category }}</span>
        </div>
        <div class="risk-title">{{ risk.title }}</div>
        <div class="risk-desc">{{ risk.description }}</div>
        <div class="risk-action"><el-icon><InfoFilled /></el-icon> {{ risk.recommendation }}</div>
      </div>
    </div>
    <el-empty v-else-if="!loading && analysisResult" description="リスクは検出されませんでした" :image-size="60" />

    <!-- サマリー -->
    <el-card v-if="analysisResult?.summary" class="summary-card" shadow="never">
      <template #header><span class="section-title">受発注サマリー</span></template>
      <div class="summary-text">{{ analysisResult.summary }}</div>
    </el-card>

    <!-- KPIダッシュボード -->
    <div class="kpi-grid" v-loading="loading">
      <!-- 受注状況 -->
      <el-card class="kpi-card" shadow="never">
        <template #header><span class="section-title">受注状況</span></template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">当月受注件数</span>
            <span class="kpi-value">{{ kpis.orders?.monthlyCount ?? '-' }}件</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">当月受注金額</span>
            <span class="kpi-value">{{ formatYen(kpis.orders?.monthlyAmount) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">平均受注単価</span>
            <span class="kpi-value">{{ formatYen(kpis.orders?.avgOrderValue) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">前月比</span>
            <span class="kpi-value" :class="valueClass(kpis.orders?.momChange)">{{ formatPctSigned(kpis.orders?.momChange) }}</span>
          </div>
        </div>
        <!-- 受注推移チャート -->
        <div ref="orderTrendChartRef" class="mini-chart"></div>
      </el-card>

      <!-- 発注・仕入状況 -->
      <el-card class="kpi-card" shadow="never">
        <template #header><span class="section-title">発注・仕入状況</span></template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">当月発注件数</span>
            <span class="kpi-value">{{ kpis.purchasing?.monthlyCount ?? '-' }}件</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">当月仕入金額</span>
            <span class="kpi-value">{{ formatYen(kpis.purchasing?.monthlyAmount) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">未入庫件数</span>
            <span class="kpi-value" :class="(kpis.purchasing?.pendingReceipts || 0) > 10 ? 'negative' : ''">{{ kpis.purchasing?.pendingReceipts ?? '-' }}件</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">仕入先集中度</span>
            <span class="kpi-value" :class="(kpis.purchasing?.vendorConcentration || 0) > 60 ? 'negative' : ''">{{ formatPct(kpis.purchasing?.vendorConcentration) }}</span>
          </div>
        </div>
        <!-- 仕入先別チャート -->
        <div ref="vendorChartRef" class="mini-chart"></div>
      </el-card>

      <!-- 納品・出荷 -->
      <el-card class="kpi-card" shadow="never">
        <template #header><span class="section-title">納品・出荷</span></template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">納品率</span>
            <span class="kpi-value" :class="(kpis.delivery?.fulfillmentRate || 0) < 90 ? 'negative' : 'positive'">{{ formatPct(kpis.delivery?.fulfillmentRate) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">平均納品リードタイム</span>
            <span class="kpi-value">{{ kpis.delivery?.avgLeadTimeDays ?? '-' }}日</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">納期遅延件数</span>
            <span class="kpi-value" :class="(kpis.delivery?.delayedCount || 0) > 0 ? 'negative' : ''">{{ kpis.delivery?.delayedCount ?? '-' }}件</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">返品率</span>
            <span class="kpi-value" :class="(kpis.delivery?.returnRate || 0) > 3 ? 'negative' : ''">{{ formatPct(kpis.delivery?.returnRate) }}</span>
          </div>
        </div>
        <!-- 納品リードタイム推移 -->
        <div ref="leadTimeChartRef" class="mini-chart"></div>
        <!-- 遅延案件 -->
        <div v-if="kpis.delivery?.delayedOrders?.length" class="overdue-list">
          <div class="overdue-header">納期遅延案件</div>
          <el-table :data="kpis.delivery.delayedOrders" size="small" :show-header="true" stripe>
            <el-table-column prop="orderNo" label="受注番号" width="120" />
            <el-table-column prop="customer" label="顧客" min-width="120" />
            <el-table-column label="金額" width="120" align="right">
              <template #default="{ row }"><span class="amount-cell">{{ formatYen(row.amount) }}</span></template>
            </el-table-column>
            <el-table-column label="遅延" width="80" align="center">
              <template #default="{ row }"><el-tag type="danger" size="small">{{ row.delayDays }}日</el-tag></template>
            </el-table-column>
          </el-table>
        </div>
      </el-card>

      <!-- 顧客分析 -->
      <el-card class="kpi-card" shadow="never">
        <template #header><span class="section-title">顧客分析</span></template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">アクティブ顧客数</span>
            <span class="kpi-value">{{ kpis.customers?.activeCount ?? '-' }}社</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">新規顧客（当月）</span>
            <span class="kpi-value positive">{{ kpis.customers?.newCount ?? '-' }}社</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">リピート率</span>
            <span class="kpi-value">{{ formatPct(kpis.customers?.repeatRate) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">顧客単価（平均）</span>
            <span class="kpi-value">{{ formatYen(kpis.customers?.avgRevenue) }}</span>
          </div>
        </div>
        <!-- TOP顧客 -->
        <div v-if="kpis.customers?.topCustomers?.length" class="overdue-list">
          <div class="overdue-header">売上TOP5顧客</div>
          <el-table :data="kpis.customers.topCustomers" size="small" :show-header="true" stripe>
            <el-table-column prop="name" label="顧客" min-width="140" />
            <el-table-column label="売上" width="130" align="right">
              <template #default="{ row }"><span class="amount-cell">{{ formatYen(row.revenue) }}</span></template>
            </el-table-column>
            <el-table-column label="構成比" width="80" align="center">
              <template #default="{ row }">{{ formatPct(row.share) }}</template>
            </el-table-column>
          </el-table>
        </div>
      </el-card>

      <!-- パフォーマンス指標 -->
      <el-card class="kpi-card kpi-card-wide" shadow="never">
        <template #header><span class="section-title">受発注パフォーマンス</span></template>
        <div class="kpi-items soundness-items">
          <div class="kpi-item soundness-item">
            <div class="gauge-label">受注達成率</div>
            <div class="gauge-value" :class="gaugeClass(kpis.performance?.orderAchievementRate, 80)">{{ formatPct(kpis.performance?.orderAchievementRate) }}</div>
            <div class="gauge-hint">目標: 80%以上</div>
          </div>
          <div class="kpi-item soundness-item">
            <div class="gauge-label">粗利率</div>
            <div class="gauge-value" :class="gaugeClass(kpis.performance?.grossMargin, 25)">{{ formatPct(kpis.performance?.grossMargin) }}</div>
            <div class="gauge-hint">目標: 25%以上</div>
          </div>
          <div class="kpi-item soundness-item">
            <div class="gauge-label">在庫回転率</div>
            <div class="gauge-value" :class="gaugeClass(kpis.performance?.inventoryTurnover, 6)">{{ kpis.performance?.inventoryTurnover?.toFixed(1) ?? '-' }}<span class="gauge-unit">回</span></div>
            <div class="gauge-hint">目標: 6回以上</div>
          </div>
          <div class="kpi-item soundness-item">
            <div class="gauge-label">受注→出荷サイクル</div>
            <div class="gauge-value" :class="(kpis.performance?.orderToShipDays || 99) <= 5 ? 'positive' : 'negative'">{{ kpis.performance?.orderToShipDays ?? '-' }}<span class="gauge-unit">日</span></div>
            <div class="gauge-hint">目標: 5日以内</div>
          </div>
        </div>
      </el-card>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'
import { TrendCharts, Refresh, InfoFilled } from '@element-plus/icons-vue'
import * as echarts from 'echarts/core'
import { BarChart, LineChart, PieChart } from 'echarts/charts'
import { TitleComponent, TooltipComponent, LegendComponent, GridComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'

echarts.use([BarChart, LineChart, PieChart, TitleComponent, TooltipComponent, LegendComponent, GridComponent, CanvasRenderer])

const loading = ref(false)
const analysisPeriod = ref(new Date().toISOString().slice(0, 7))
const analysisResult = ref<any>(null)

const orderTrendChartRef = ref<HTMLElement>()
const vendorChartRef = ref<HTMLElement>()
const leadTimeChartRef = ref<HTMLElement>()
let orderTrendChart: echarts.ECharts | null = null
let vendorChart: echarts.ECharts | null = null
let leadTimeChart: echarts.ECharts | null = null

const kpis = computed(() => analysisResult.value?.kpis || {})
const risks = computed(() => analysisResult.value?.risks || [])

const formatYen = (v: any) => v != null ? `¥${Number(v).toLocaleString('ja-JP')}` : '-'
const formatPct = (v: any) => v != null ? `${Number(v).toFixed(1)}%` : '-'
const formatPctSigned = (v: any) => {
  if (v == null) return '-'
  const n = Number(v)
  return `${n >= 0 ? '+' : ''}${n.toFixed(1)}%`
}
const valueClass = (v: any) => { if (v == null) return ''; return Number(v) >= 0 ? 'positive' : 'negative' }
const gaugeClass = (v: any, threshold: number) => { if (v == null) return ''; return Number(v) >= threshold ? 'positive' : 'negative' }
const severityType = (s: string) => s === 'high' ? 'danger' : s === 'medium' ? 'warning' : 'info'
const severityLabel = (s: string) => s === 'high' ? '高' : s === 'medium' ? '中' : '低'

async function runAnalysis() {
  loading.value = true
  try {
    await new Promise(r => setTimeout(r, 600))
    loadDemoData()
    nextTick(() => { renderOrderTrendChart(); renderVendorChart(); renderLeadTimeChart() })
  } finally { loading.value = false }
}

function loadDemoData() {
  analysisResult.value = {
    analysisDate: new Date().toISOString().split('T')[0],
    period: analysisPeriod.value.replace('-', '年') + '月',
    kpis: {
      orders: {
        monthlyCount: 38,
        monthlyAmount: 24800000,
        avgOrderValue: 652600,
        momChange: 12.3,
        trend: [
          { month: '2025-10', count: 32, amount: 19500000 },
          { month: '2025-11', count: 28, amount: 17200000 },
          { month: '2025-12', count: 45, amount: 31000000 },
          { month: '2026-01', count: 25, amount: 15800000 },
          { month: '2026-02', count: 34, amount: 22100000 },
          { month: '2026-03', count: 38, amount: 24800000 }
        ]
      },
      purchasing: {
        monthlyCount: 22,
        monthlyAmount: 16200000,
        pendingReceipts: 8,
        vendorConcentration: 42.5,
        topVendors: [
          { name: 'A商事', amount: 6880000 },
          { name: 'B工業', amount: 3240000 },
          { name: 'C電子', amount: 2590000 },
          { name: 'D物産', amount: 1940000 },
          { name: 'その他', amount: 1550000 }
        ]
      },
      delivery: {
        fulfillmentRate: 92.1,
        avgLeadTimeDays: 4.2,
        delayedCount: 3,
        returnRate: 1.8,
        leadTimeTrend: [
          { month: '2025-10', days: 3.8 },
          { month: '2025-11', days: 4.1 },
          { month: '2025-12', days: 5.5 },
          { month: '2026-01', days: 3.5 },
          { month: '2026-02', days: 3.9 },
          { month: '2026-03', days: 4.2 }
        ],
        delayedOrders: [
          { orderNo: 'SO-2026-0312', customer: '株式会社ベース', amount: 1850000, delayDays: 5 },
          { orderNo: 'SO-2026-0298', customer: 'テック商事', amount: 920000, delayDays: 3 },
          { orderNo: 'SO-2026-0285', customer: '山田建設', amount: 3200000, delayDays: 8 }
        ]
      },
      customers: {
        activeCount: 24,
        newCount: 3,
        repeatRate: 78.5,
        avgRevenue: 1033000,
        topCustomers: [
          { name: '株式会社ベース', revenue: 5200000, share: 21.0 },
          { name: 'テック商事', revenue: 3800000, share: 15.3 },
          { name: '山田建設', revenue: 3200000, share: 12.9 },
          { name: 'グリーンエナジー', revenue: 2600000, share: 10.5 },
          { name: 'サクラ電機', revenue: 2100000, share: 8.5 }
        ]
      },
      performance: {
        orderAchievementRate: 88.5,
        grossMargin: 31.2,
        inventoryTurnover: 7.8,
        orderToShipDays: 4.2
      }
    },
    risks: [
      {
        severity: 'high',
        category: '納品管理',
        title: '山田建設向け納期8日遅延',
        description: 'SO-2026-0285（¥3,200,000）が納期から8日遅延しています。原因は仕入先からの入庫遅れで、顧客への影響が懸念されます。',
        recommendation: '仕入先に状況確認の上、顧客に納期回答を行ってください。代替品の手配も検討してください。'
      },
      {
        severity: 'medium',
        category: '仕入管理',
        title: '未入庫発注8件の滞留',
        description: '発注済みで未入庫の案件が8件あります。うち3件は予定納期を超過しています。入庫遅延が受注の出荷に影響する可能性があります。',
        recommendation: '未入庫案件の仕入先に催促を行い、入庫予定日を再確認してください。'
      },
      {
        severity: 'low',
        category: '顧客分析',
        title: '売上の顧客集中リスク',
        description: 'TOP3顧客で売上の49.2%を占めています。特定顧客への依存度が高く、取引停止時のリスクがあります。',
        recommendation: '新規顧客の開拓を継続し、売上の分散化を図ってください。月3社の新規目標維持が望ましいです。'
      }
    ],
    summary: '2026年3月の受発注状況は前月比+12.3%と回復傾向にあります。受注38件（¥24,800,000）、発注22件（¥16,200,000）で粗利率31.2%を確保しています。納品率92.1%は目標の95%に未達で、特に山田建設向けの8日遅延は早急な対応が必要です。在庫回転率7.8回と効率的な在庫運用ができていますが、未入庫8件の管理を強化し、受注→出荷サイクルの短縮に取り組む必要があります。'
  }
}

function renderOrderTrendChart() {
  if (!orderTrendChartRef.value) return
  orderTrendChart?.dispose()
  orderTrendChart = echarts.init(orderTrendChartRef.value)
  const trend = kpis.value.orders?.trend || []
  orderTrendChart.setOption({
    tooltip: { trigger: 'axis', formatter: (p: any) => `${p[0].axisValue}<br/>受注: ¥${Number(p[0].value).toLocaleString()}<br/>件数: ${p[1]?.value ?? '-'}件` },
    grid: { top: 10, left: 10, right: 30, bottom: 20, containLabel: true },
    xAxis: { type: 'category', data: trend.map((t: any) => t.month), axisLabel: { fontSize: 11, color: '#64748b' }, axisLine: { lineStyle: { color: '#e2e8f0' } } },
    yAxis: [
      { type: 'value', axisLabel: { fontSize: 10, color: '#94a3b8', formatter: (v: number) => `${(v / 1e6).toFixed(0)}M` }, splitLine: { lineStyle: { color: '#f1f5f9', type: 'dashed' } } },
      { type: 'value', axisLabel: { fontSize: 10, color: '#94a3b8' }, splitLine: { show: false } }
    ],
    series: [
      { type: 'bar', data: trend.map((t: any) => t.amount), barWidth: '40%', itemStyle: { color: '#0ea5e9', borderRadius: [4, 4, 0, 0] } },
      { type: 'line', yAxisIndex: 1, data: trend.map((t: any) => t.count), smooth: true, lineStyle: { color: '#f59e0b', width: 2 }, itemStyle: { color: '#f59e0b' }, symbol: 'circle', symbolSize: 6 }
    ]
  })
}

function renderVendorChart() {
  if (!vendorChartRef.value) return
  vendorChart?.dispose()
  vendorChart = echarts.init(vendorChartRef.value)
  const vendors = kpis.value.purchasing?.topVendors || []
  vendorChart.setOption({
    tooltip: { trigger: 'item', formatter: '{b}: ¥{c}' },
    series: [{
      type: 'pie', radius: ['40%', '70%'], center: ['50%', '50%'],
      label: { fontSize: 11, formatter: '{b}\n{d}%' },
      data: vendors.map((v: any, i: number) => ({
        name: v.name, value: v.amount,
        itemStyle: { color: ['#0ea5e9', '#6366f1', '#8b5cf6', '#22c55e', '#94a3b8'][i % 5] }
      }))
    }]
  })
}

function renderLeadTimeChart() {
  if (!leadTimeChartRef.value) return
  leadTimeChart?.dispose()
  leadTimeChart = echarts.init(leadTimeChartRef.value)
  const trend = kpis.value.delivery?.leadTimeTrend || []
  leadTimeChart.setOption({
    tooltip: { trigger: 'axis', formatter: (p: any) => `${p[0].axisValue}<br/>リードタイム: ${p[0].value}日` },
    grid: { top: 10, left: 10, right: 10, bottom: 20, containLabel: true },
    xAxis: { type: 'category', data: trend.map((t: any) => t.month), axisLabel: { fontSize: 11, color: '#64748b' }, axisLine: { lineStyle: { color: '#e2e8f0' } } },
    yAxis: { type: 'value', axisLabel: { fontSize: 10, color: '#94a3b8', formatter: '{value}日' }, splitLine: { lineStyle: { color: '#f1f5f9', type: 'dashed' } }, min: 0 },
    series: [{
      type: 'line', data: trend.map((t: any) => t.days), smooth: true, symbol: 'circle', symbolSize: 8,
      lineStyle: { width: 3, color: '#0ea5e9' },
      itemStyle: { color: '#0ea5e9', borderWidth: 2, borderColor: '#fff' },
      areaStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{ offset: 0, color: 'rgba(14,165,233,0.2)' }, { offset: 1, color: 'rgba(14,165,233,0.02)' }]) },
      markLine: { silent: true, data: [{ yAxis: 5, lineStyle: { color: '#ef4444', type: 'dashed', width: 2 }, label: { formatter: '5日目標', fontSize: 10 } }] }
    }]
  })
}

function handleResize() { orderTrendChart?.resize(); vendorChart?.resize(); leadTimeChart?.resize() }

onMounted(() => { runAnalysis(); window.addEventListener('resize', handleResize) })
onUnmounted(() => { window.removeEventListener('resize', handleResize); orderTrendChart?.dispose(); vendorChart?.dispose(); leadTimeChart?.dispose() })
</script>

<style scoped>
.sales-analysis-page { padding: 20px; display: flex; flex-direction: column; gap: 16px; background: #f8fafc; min-height: 100%; }
.page-title-bar { display: flex; justify-content: space-between; align-items: center; margin-bottom: 4px; }
.title-left { display: flex; align-items: center; gap: 10px; }
.page-title { margin: 0; font-size: 20px; font-weight: 700; color: #1e293b; }
.title-actions { display: flex; align-items: center; gap: 10px; }

.risk-alerts { display: flex; flex-direction: column; gap: 10px; }
.risk-alert { padding: 14px 18px; border-radius: 10px; border-left: 4px solid; }
.risk-high { background: #fef2f2; border-left-color: #ef4444; }
.risk-medium { background: #fffbeb; border-left-color: #f59e0b; }
.risk-low { background: #f0f9ff; border-left-color: #3b82f6; }
.risk-header { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; }
.risk-category { font-size: 12px; color: #64748b; }
.risk-title { font-size: 14px; font-weight: 600; color: #1e293b; margin-bottom: 4px; }
.risk-desc { font-size: 13px; color: #475569; margin-bottom: 6px; line-height: 1.5; }
.risk-action { font-size: 12px; color: #0ea5e9; display: flex; align-items: flex-start; gap: 4px; line-height: 1.5; }

.summary-card { border-radius: 12px; border: none; }
.summary-text { font-size: 14px; line-height: 1.8; color: #334155; }

.kpi-grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 16px; }
.kpi-card { border-radius: 12px; border: none; }
.kpi-card-wide { grid-column: span 2; }
.section-title { font-size: 15px; font-weight: 600; color: #1e293b; }

.kpi-items { display: grid; grid-template-columns: repeat(2, 1fr); gap: 12px; margin-bottom: 12px; }
.soundness-items { grid-template-columns: repeat(4, 1fr); }
.kpi-item { display: flex; flex-direction: column; gap: 2px; }
.kpi-label { font-size: 12px; color: #94a3b8; }
.kpi-value { font-size: 18px; font-weight: 700; color: #1e293b; font-variant-numeric: tabular-nums; }
.kpi-value.positive { color: #22c55e; }
.kpi-value.negative { color: #ef4444; }

.mini-chart { width: 100%; height: 200px; }
.overdue-list { margin-top: 12px; }
.overdue-header { font-size: 13px; font-weight: 600; color: #475569; margin-bottom: 6px; }
.amount-cell { font-variant-numeric: tabular-nums; }

.soundness-item { text-align: center; padding: 12px; background: #f8fafc; border-radius: 10px; }
.gauge-label { font-size: 13px; color: #64748b; margin-bottom: 6px; }
.gauge-value { font-size: 24px; font-weight: 700; font-variant-numeric: tabular-nums; }
.gauge-value.positive { color: #22c55e; }
.gauge-value.negative { color: #ef4444; }
.gauge-unit { font-size: 14px; font-weight: 400; margin-left: 2px; }
.gauge-hint { font-size: 11px; color: #cbd5e1; margin-top: 4px; }
</style>
