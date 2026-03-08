<template>
  <div class="financial-analysis-page">
    <!-- 页面标题 -->
    <div class="page-title-bar">
      <div class="title-left">
        <el-icon :size="22" color="#6366f1"><DataAnalysis /></el-icon>
        <h2 class="page-title">財務分析Agent</h2>
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
      <div
        v-for="(risk, i) in risks"
        :key="i"
        class="risk-alert"
        :class="'risk-' + risk.severity"
      >
        <div class="risk-header">
          <el-tag :type="severityType(risk.severity)" size="small" effect="dark">
            {{ severityLabel(risk.severity) }}
          </el-tag>
          <span class="risk-category">{{ risk.category }}</span>
        </div>
        <div class="risk-title">{{ risk.title }}</div>
        <div class="risk-desc">{{ risk.description }}</div>
        <div class="risk-action">
          <el-icon><InfoFilled /></el-icon>
          {{ risk.recommendation }}
        </div>
      </div>
    </div>
    <el-empty v-else-if="!loading && analysisResult" description="リスクは検出されませんでした" :image-size="60" />

    <!-- サマリー -->
    <el-card v-if="analysisResult?.summary" class="summary-card" shadow="never">
      <template #header>
        <span class="section-title">経営サマリー</span>
      </template>
      <div class="summary-text">{{ analysisResult.summary }}</div>
    </el-card>

    <!-- KPIダッシュボード -->
    <div class="kpi-grid" v-loading="loading">
      <!-- 資金繰り・キャッシュフロー -->
      <el-card class="kpi-card" shadow="never">
        <template #header>
          <div class="card-header">
            <span class="section-title">資金繰り・キャッシュフロー</span>
          </div>
        </template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">現預金残高</span>
            <span class="kpi-value">{{ formatYen(kpis.cashFlow?.currentBalance) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">月次キャッシュフロー</span>
            <span class="kpi-value" :class="valueClass(kpis.cashFlow?.monthlyCashFlow)">
              {{ formatYenSigned(kpis.cashFlow?.monthlyCashFlow) }}
            </span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">運転資金回転日数</span>
            <span class="kpi-value">{{ formatDays(kpis.cashFlow?.workingCapitalDays) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">手元流動性比率</span>
            <span class="kpi-value" :class="kpis.cashFlow?.liquidityRatio < 1 ? 'negative' : ''">
              {{ formatRatio(kpis.cashFlow?.liquidityRatio) }}ヶ月分
            </span>
          </div>
        </div>
        <!-- 資金繰り予測チャート -->
        <div ref="cashFlowChartRef" class="mini-chart"></div>
      </el-card>

      <!-- 収益性 -->
      <el-card class="kpi-card" shadow="never">
        <template #header>
          <span class="section-title">収益性</span>
        </template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">売上総利益率</span>
            <span class="kpi-value">{{ formatPct(kpis.profitability?.grossProfitMargin) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">営業利益率</span>
            <span class="kpi-value" :class="valueClass(kpis.profitability?.operatingProfitMargin)">
              {{ formatPct(kpis.profitability?.operatingProfitMargin) }}
            </span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">経費率</span>
            <span class="kpi-value">{{ formatPct(kpis.profitability?.expenseRatio) }}</span>
          </div>
        </div>
        <!-- 月次売上推移チャート -->
        <div ref="salesChartRef" class="mini-chart"></div>
      </el-card>

      <!-- 債権管理 -->
      <el-card class="kpi-card" shadow="never">
        <template #header>
          <span class="section-title">債権管理</span>
        </template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">売掛金回転期間</span>
            <span class="kpi-value">{{ formatDays(kpis.receivables?.turnoverDays) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">回収率</span>
            <span class="kpi-value">{{ formatPct(kpis.receivables?.collectionRate) }}</span>
          </div>
        </div>
        <!-- 売掛金年齢分析チャート -->
        <div ref="agingChartRef" class="mini-chart"></div>
        <!-- 長期滞留債権 -->
        <div v-if="kpis.receivables?.overdueItems?.length" class="overdue-list">
          <div class="overdue-header">長期滞留債権</div>
          <el-table :data="kpis.receivables.overdueItems" size="small" :show-header="true" stripe>
            <el-table-column prop="partner" label="取引先" min-width="120" />
            <el-table-column label="金額" width="130" align="right">
              <template #default="{ row }">
                <span class="amount-cell">{{ formatYen(row.amount) }}</span>
              </template>
            </el-table-column>
            <el-table-column label="超過日数" width="90" align="center">
              <template #default="{ row }">
                <el-tag type="danger" size="small">{{ row.overdueDays }}日</el-tag>
              </template>
            </el-table-column>
          </el-table>
        </div>
      </el-card>

      <!-- 債務管理 -->
      <el-card class="kpi-card" shadow="never">
        <template #header>
          <span class="section-title">債務管理</span>
        </template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">買掛金回転期間</span>
            <span class="kpi-value">{{ formatDays(kpis.payables?.turnoverDays) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">支払集中度リスク</span>
            <span class="kpi-value" :class="(kpis.payables?.concentrationRisk || 0) > 0.5 ? 'negative' : ''">
              {{ formatPct(kpis.payables?.concentrationRisk ? kpis.payables.concentrationRisk * 100 : 0) }}
            </span>
          </div>
        </div>
        <!-- 今後の支払予定 -->
        <div v-if="kpis.payables?.upcomingPayments?.length" class="overdue-list">
          <div class="overdue-header">今後の支払予定</div>
          <el-table :data="kpis.payables.upcomingPayments" size="small" :show-header="true" stripe>
            <el-table-column prop="date" label="支払日" width="110" />
            <el-table-column label="金額" align="right">
              <template #default="{ row }">
                <span class="amount-cell">{{ formatYen(row.amount) }}</span>
              </template>
            </el-table-column>
          </el-table>
        </div>
      </el-card>

      <!-- 財務健全性 -->
      <el-card class="kpi-card kpi-card-wide" shadow="never">
        <template #header>
          <span class="section-title">財務健全性</span>
        </template>
        <div class="kpi-items soundness-items">
          <div class="kpi-item soundness-item">
            <div class="gauge-label">流動比率</div>
            <div class="gauge-value" :class="gaugeClass(kpis.soundness?.currentRatio, 150)">
              {{ formatPct(kpis.soundness?.currentRatio) }}
            </div>
            <div class="gauge-hint">目安: 150%以上</div>
          </div>
          <div class="kpi-item soundness-item">
            <div class="gauge-label">当座比率</div>
            <div class="gauge-value" :class="gaugeClass(kpis.soundness?.quickRatio, 100)">
              {{ formatPct(kpis.soundness?.quickRatio) }}
            </div>
            <div class="gauge-hint">目安: 100%以上</div>
          </div>
          <div class="kpi-item soundness-item">
            <div class="gauge-label">自己資本比率</div>
            <div class="gauge-value" :class="gaugeClass(kpis.soundness?.equityRatio, 30)">
              {{ formatPct(kpis.soundness?.equityRatio) }}
            </div>
            <div class="gauge-hint">目安: 30%以上</div>
          </div>
          <div class="kpi-item soundness-item">
            <div class="gauge-label">負債比率</div>
            <div class="gauge-value" :class="(kpis.soundness?.debtRatio || 0) > 200 ? 'negative' : 'positive'">
              {{ formatPct(kpis.soundness?.debtRatio) }}
            </div>
            <div class="gauge-hint">目安: 200%以下</div>
          </div>
        </div>
      </el-card>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'
import { DataAnalysis, Refresh, InfoFilled } from '@element-plus/icons-vue'
import * as echarts from 'echarts/core'
import { BarChart, LineChart, PieChart } from 'echarts/charts'
import {
  TitleComponent, TooltipComponent, LegendComponent,
  GridComponent
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import api from '../../api'

echarts.use([
  BarChart, LineChart, PieChart, TitleComponent, TooltipComponent,
  LegendComponent, GridComponent, CanvasRenderer
])

const loading = ref(false)
const analysisPeriod = ref(new Date().toISOString().slice(0, 7))
const analysisResult = ref<any>(null)

const cashFlowChartRef = ref<HTMLElement>()
const salesChartRef = ref<HTMLElement>()
const agingChartRef = ref<HTMLElement>()
let cashFlowChart: echarts.ECharts | null = null
let salesChart: echarts.ECharts | null = null
let agingChart: echarts.ECharts | null = null

const kpis = computed(() => analysisResult.value?.kpis || {})
const risks = computed(() => analysisResult.value?.risks || [])

// 格式化
const formatYen = (v: any) => v != null ? `¥${Number(v).toLocaleString('ja-JP')}` : '-'
const formatYenSigned = (v: any) => {
  if (v == null) return '-'
  const n = Number(v)
  return `${n >= 0 ? '+' : ''}¥${n.toLocaleString('ja-JP')}`
}
const formatPct = (v: any) => v != null ? `${Number(v).toFixed(1)}%` : '-'
const formatDays = (v: any) => v != null ? `${Number(v).toFixed(0)}日` : '-'
const formatRatio = (v: any) => v != null ? Number(v).toFixed(1) : '-'

const valueClass = (v: any) => {
  if (v == null) return ''
  return Number(v) >= 0 ? 'positive' : 'negative'
}

const gaugeClass = (v: any, threshold: number) => {
  if (v == null) return ''
  return Number(v) >= threshold ? 'positive' : 'negative'
}

const severityType = (s: string) => {
  if (s === 'high') return 'danger'
  if (s === 'medium') return 'warning'
  return 'info'
}

const severityLabel = (s: string) => {
  if (s === 'high') return '高'
  if (s === 'medium') return '中'
  return '低'
}

// 分析実行
async function runAnalysis() {
  loading.value = true
  try {
    const resp = await api.get('/finance/analysis', {
      params: { period: analysisPeriod.value }
    })
    analysisResult.value = resp.data
    nextTick(() => {
      renderCashFlowChart()
      renderSalesChart()
      renderAgingChart()
    })
  } catch (e: any) {
    // デモデータをロード
    loadDemoData()
    nextTick(() => {
      renderCashFlowChart()
      renderSalesChart()
      renderAgingChart()
    })
  } finally {
    loading.value = false
  }
}

// デモデータ（APIが未実装の場合）
function loadDemoData() {
  analysisResult.value = {
    analysisDate: new Date().toISOString().split('T')[0],
    period: analysisPeriod.value.replace('-', '年') + '月',
    kpis: {
      cashFlow: {
        currentBalance: 54200000,
        monthlyCashFlow: 3500000,
        forecast3m: [
          { month: '2026-04', projected: 51800000 },
          { month: '2026-05', projected: 49200000 },
          { month: '2026-06', projected: 52600000 }
        ],
        workingCapitalDays: 45,
        liquidityRatio: 2.8
      },
      profitability: {
        grossProfitMargin: 32.5,
        operatingProfitMargin: 8.2,
        monthlySalesTrend: [
          { month: '2025-10', amount: 12500000, momChange: 5.2 },
          { month: '2025-11', amount: 9800000, momChange: -21.6 },
          { month: '2025-12', amount: 15200000, momChange: 55.1 },
          { month: '2026-01', amount: 8400000, momChange: -44.7 },
          { month: '2026-02', amount: 11300000, momChange: 34.5 },
          { month: '2026-03', amount: 13100000, momChange: 15.9 }
        ],
        expenseRatio: 24.3
      },
      receivables: {
        turnoverDays: 52,
        agingDistribution: { within30: 8500000, '31to60': 3200000, '61to90': 1800000, over90: 950000 },
        overdueItems: [
          { partner: 'D社', amount: 950000, overdueDays: 95 },
          { partner: 'F社', amount: 1200000, overdueDays: 72 },
          { partner: 'H社', amount: 600000, overdueDays: 63 }
        ],
        collectionRate: 93.4
      },
      payables: {
        turnoverDays: 38,
        upcomingPayments: [
          { date: '2026-03-15', amount: 2880000 },
          { date: '2026-03-20', amount: 1850000 },
          { date: '2026-03-25', amount: 3200000 },
          { date: '2026-03-31', amount: 1432000 }
        ],
        concentrationRisk: 0.35
      },
      soundness: {
        currentRatio: 185.2,
        quickRatio: 142.8,
        equityRatio: 42.5,
        debtRatio: 135.3
      }
    },
    risks: [
      {
        severity: 'medium',
        category: '債権管理',
        title: '長期滞留債権の増加',
        description: '90日超の長期滞留債権が売掛金総額の6.6%（¥950,000）を占めています。D社の未回収が95日超過しています。',
        recommendation: 'D社へ優先的に督促を行い、回収計画を策定してください。必要に応じて与信限度額の見直しを検討してください。'
      },
      {
        severity: 'low',
        category: '収益性',
        title: '月次売上の変動が大きい',
        description: '直近6ヶ月の売上は前月比-44.7%〜+55.1%と大きく変動しています。安定的な収益基盤の構築が課題です。',
        recommendation: 'リカーリング収益（月額契約等）の比率を高める施策を検討してください。'
      },
      {
        severity: 'medium',
        category: '債務管理',
        title: '月末の支払集中',
        description: '3月25日〜31日に支払が集中しています（合計¥4,632,000）。資金残高は十分ですが、一時的な流動性低下に注意が必要です。',
        recommendation: '支払日の分散化を仕入先と交渉するか、支払スケジュールを調整してください。'
      }
    ],
    summary: '2026年3月時点の財務状況は概ね健全です。現預金残高¥54,200,000、流動比率185.2%と十分な流動性を確保しています。ただし、D社向け債権が95日超過しており、早急な督促対応が必要です。また月次売上の変動幅が大きいため、安定収益源の確保が中期的な課題となります。'
  }
}

// チャート描画
function renderCashFlowChart() {
  if (!cashFlowChartRef.value) return
  cashFlowChart?.dispose()
  cashFlowChart = echarts.init(cashFlowChartRef.value)
  const forecast = kpis.value.cashFlow?.forecast3m || []
  const currentMonth = analysisPeriod.value
  const currentBalance = kpis.value.cashFlow?.currentBalance || 0
  const months = [currentMonth, ...forecast.map((f: any) => f.month)]
  const values = [currentBalance, ...forecast.map((f: any) => f.projected)]
  cashFlowChart.setOption({
    tooltip: { trigger: 'axis', formatter: (p: any) => `${p[0].axisValue}<br/>残高予測: ¥${Number(p[0].value).toLocaleString()}` },
    grid: { top: 10, left: 10, right: 10, bottom: 20, containLabel: true },
    xAxis: { type: 'category', data: months, axisLabel: { fontSize: 11, color: '#64748b' }, axisLine: { lineStyle: { color: '#e2e8f0' } } },
    yAxis: { type: 'value', axisLabel: { fontSize: 10, color: '#94a3b8', formatter: (v: number) => `${(v / 1e6).toFixed(0)}M` }, splitLine: { lineStyle: { color: '#f1f5f9', type: 'dashed' } } },
    series: [{
      type: 'line', data: values, smooth: true, symbol: 'circle', symbolSize: 8,
      lineStyle: { width: 3, color: '#6366f1' },
      itemStyle: { color: '#6366f1', borderWidth: 2, borderColor: '#fff' },
      areaStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{ offset: 0, color: 'rgba(99,102,241,0.2)' }, { offset: 1, color: 'rgba(99,102,241,0.02)' }]) },
      markLine: { silent: true, data: [{ yAxis: kpis.value.cashFlow?.currentBalance, lineStyle: { color: '#cbd5e1', type: 'dashed' } }] }
    }]
  })
}

function renderSalesChart() {
  if (!salesChartRef.value) return
  salesChart?.dispose()
  salesChart = echarts.init(salesChartRef.value)
  const trend = kpis.value.profitability?.monthlySalesTrend || []
  salesChart.setOption({
    tooltip: { trigger: 'axis', formatter: (p: any) => `${p[0].axisValue}<br/>売上: ¥${Number(p[0].value).toLocaleString()}<br/>前月比: ${p[1]?.value ?? '-'}%` },
    grid: { top: 10, left: 10, right: 30, bottom: 20, containLabel: true },
    xAxis: { type: 'category', data: trend.map((t: any) => t.month), axisLabel: { fontSize: 11, color: '#64748b' }, axisLine: { lineStyle: { color: '#e2e8f0' } } },
    yAxis: [
      { type: 'value', axisLabel: { fontSize: 10, color: '#94a3b8', formatter: (v: number) => `${(v / 1e6).toFixed(0)}M` }, splitLine: { lineStyle: { color: '#f1f5f9', type: 'dashed' } } },
      { type: 'value', axisLabel: { fontSize: 10, color: '#94a3b8', formatter: (v: number) => `${v}%` }, splitLine: { show: false } }
    ],
    series: [
      { type: 'bar', data: trend.map((t: any) => t.amount), barWidth: '40%', itemStyle: { color: '#6366f1', borderRadius: [4, 4, 0, 0] } },
      { type: 'line', yAxisIndex: 1, data: trend.map((t: any) => t.momChange), smooth: true, lineStyle: { color: '#f59e0b', width: 2 }, itemStyle: { color: '#f59e0b' }, symbol: 'circle', symbolSize: 6 }
    ]
  })
}

function renderAgingChart() {
  if (!agingChartRef.value) return
  agingChart?.dispose()
  agingChart = echarts.init(agingChartRef.value)
  const aging = kpis.value.receivables?.agingDistribution
  if (!aging) return
  agingChart.setOption({
    tooltip: { trigger: 'item', formatter: '{b}: ¥{c}' },
    series: [{
      type: 'pie', radius: ['40%', '70%'], center: ['50%', '50%'],
      label: { fontSize: 11, formatter: '{b}\n{d}%' },
      data: [
        { name: '30日以内', value: aging.within30 || 0, itemStyle: { color: '#22c55e' } },
        { name: '31-60日', value: aging['31to60'] || 0, itemStyle: { color: '#f59e0b' } },
        { name: '61-90日', value: aging['61to90'] || 0, itemStyle: { color: '#f97316' } },
        { name: '90日超', value: aging.over90 || 0, itemStyle: { color: '#ef4444' } }
      ]
    }]
  })
}

function handleResize() {
  cashFlowChart?.resize()
  salesChart?.resize()
  agingChart?.resize()
}

onMounted(() => {
  runAnalysis()
  window.addEventListener('resize', handleResize)
})

onUnmounted(() => {
  window.removeEventListener('resize', handleResize)
  cashFlowChart?.dispose()
  salesChart?.dispose()
  agingChart?.dispose()
})
</script>

<style scoped>
.financial-analysis-page {
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 16px;
  background: #f8fafc;
  min-height: 100%;
}

.page-title-bar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 4px;
}

.title-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-title {
  margin: 0;
  font-size: 20px;
  font-weight: 700;
  color: #1e293b;
}

.title-actions {
  display: flex;
  align-items: center;
  gap: 10px;
}

/* リスクアラート */
.risk-alerts {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.risk-alert {
  padding: 14px 18px;
  border-radius: 10px;
  border-left: 4px solid;
}

.risk-high {
  background: #fef2f2;
  border-left-color: #ef4444;
}

.risk-medium {
  background: #fffbeb;
  border-left-color: #f59e0b;
}

.risk-low {
  background: #f0f9ff;
  border-left-color: #3b82f6;
}

.risk-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
}

.risk-category {
  font-size: 12px;
  color: #64748b;
}

.risk-title {
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
  margin-bottom: 4px;
}

.risk-desc {
  font-size: 13px;
  color: #475569;
  margin-bottom: 6px;
  line-height: 1.5;
}

.risk-action {
  font-size: 12px;
  color: #6366f1;
  display: flex;
  align-items: flex-start;
  gap: 4px;
  line-height: 1.5;
}

/* サマリー */
.summary-card {
  border-radius: 12px;
  border: none;
}

.summary-text {
  font-size: 14px;
  line-height: 1.8;
  color: #334155;
}

/* KPIグリッド */
.kpi-grid {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 16px;
}

.kpi-card {
  border-radius: 12px;
  border: none;
}

.kpi-card-wide {
  grid-column: span 2;
}

.section-title {
  font-size: 15px;
  font-weight: 600;
  color: #1e293b;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

/* KPI項目 */
.kpi-items {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 12px;
  margin-bottom: 12px;
}

.soundness-items {
  grid-template-columns: repeat(4, 1fr);
}

.kpi-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.kpi-label {
  font-size: 12px;
  color: #94a3b8;
}

.kpi-value {
  font-size: 18px;
  font-weight: 700;
  color: #1e293b;
  font-variant-numeric: tabular-nums;
}

.kpi-value.positive {
  color: #22c55e;
}

.kpi-value.negative {
  color: #ef4444;
}

/* 健全性ゲージ */
.soundness-item {
  text-align: center;
  padding: 12px;
  background: #f8fafc;
  border-radius: 10px;
}

.gauge-label {
  font-size: 13px;
  color: #64748b;
  margin-bottom: 6px;
}

.gauge-value {
  font-size: 24px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}

.gauge-value.positive {
  color: #22c55e;
}

.gauge-value.negative {
  color: #ef4444;
}

.gauge-hint {
  font-size: 11px;
  color: #cbd5e1;
  margin-top: 4px;
}

/* ミニチャート */
.mini-chart {
  width: 100%;
  height: 200px;
}

/* 長期滞留リスト */
.overdue-list {
  margin-top: 12px;
}

.overdue-header {
  font-size: 13px;
  font-weight: 600;
  color: #475569;
  margin-bottom: 8px;
}

.amount-cell {
  font-variant-numeric: tabular-nums;
  font-weight: 500;
}

@media (max-width: 900px) {
  .kpi-grid {
    grid-template-columns: 1fr;
  }
  .kpi-card-wide {
    grid-column: span 1;
  }
  .soundness-items {
    grid-template-columns: repeat(2, 1fr);
  }
}
</style>
