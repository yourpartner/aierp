<template>
  <div class="hr-analysis-page">
    <!-- 页面标题 -->
    <div class="page-title-bar">
      <div class="title-left">
        <el-icon :size="22" color="#8b5cf6"><User /></el-icon>
        <h2 class="page-title">人事分析Agent</h2>
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
        <span class="section-title">人事サマリー</span>
      </template>
      <div class="summary-text">{{ analysisResult.summary }}</div>
    </el-card>

    <!-- KPIダッシュボード -->
    <div class="kpi-grid" v-loading="loading">
      <!-- 人員構成 -->
      <el-card class="kpi-card" shadow="never">
        <template #header>
          <span class="section-title">人員構成</span>
        </template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">総従業員数</span>
            <span class="kpi-value">{{ kpis.headcount?.total ?? '-' }}名</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">正社員</span>
            <span class="kpi-value">{{ kpis.headcount?.fullTime ?? '-' }}名</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">契約・派遣</span>
            <span class="kpi-value">{{ kpis.headcount?.contract ?? '-' }}名</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">パート・アルバイト</span>
            <span class="kpi-value">{{ kpis.headcount?.partTime ?? '-' }}名</span>
          </div>
        </div>
        <!-- 部門別人員チャート -->
        <div ref="deptChartRef" class="mini-chart"></div>
      </el-card>

      <!-- 勤怠・労働時間 -->
      <el-card class="kpi-card" shadow="never">
        <template #header>
          <span class="section-title">勤怠・労働時間</span>
        </template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">平均残業時間</span>
            <span class="kpi-value" :class="(kpis.attendance?.avgOvertimeHours || 0) > 40 ? 'negative' : ''">
              {{ formatHours(kpis.attendance?.avgOvertimeHours) }}
            </span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">有給取得率</span>
            <span class="kpi-value" :class="(kpis.attendance?.paidLeaveRate || 0) < 50 ? 'negative' : 'positive'">
              {{ formatPct(kpis.attendance?.paidLeaveRate) }}
            </span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">平均出勤率</span>
            <span class="kpi-value">{{ formatPct(kpis.attendance?.attendanceRate) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">36協定超過者</span>
            <span class="kpi-value" :class="(kpis.attendance?.overLimitCount || 0) > 0 ? 'negative' : ''">
              {{ kpis.attendance?.overLimitCount ?? '-' }}名
            </span>
          </div>
        </div>
        <!-- 月次残業推移チャート -->
        <div ref="overtimeChartRef" class="mini-chart"></div>
      </el-card>

      <!-- 離職・採用 -->
      <el-card class="kpi-card" shadow="never">
        <template #header>
          <span class="section-title">離職・採用</span>
        </template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">年間離職率</span>
            <span class="kpi-value" :class="(kpis.turnover?.annualRate || 0) > 15 ? 'negative' : ''">
              {{ formatPct(kpis.turnover?.annualRate) }}
            </span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">平均勤続年数</span>
            <span class="kpi-value">{{ formatYears(kpis.turnover?.avgTenure) }}</span>
          </div>
        </div>
        <!-- 離職率推移チャート -->
        <div ref="turnoverChartRef" class="mini-chart"></div>
        <!-- 退職予定・リスク -->
        <div v-if="kpis.turnover?.retirementRisk?.length" class="overdue-list">
          <div class="overdue-header">退職リスク（勤続・年齢）</div>
          <el-table :data="kpis.turnover.retirementRisk" size="small" :show-header="true" stripe>
            <el-table-column prop="name" label="氏名" min-width="100" />
            <el-table-column prop="department" label="部門" width="100" />
            <el-table-column label="理由" min-width="140">
              <template #default="{ row }">{{ row.reason }}</template>
            </el-table-column>
            <el-table-column label="リスク" width="80" align="center">
              <template #default="{ row }">
                <el-tag :type="row.level === 'high' ? 'danger' : 'warning'" size="small">{{ row.level === 'high' ? '高' : '中' }}</el-tag>
              </template>
            </el-table-column>
          </el-table>
        </div>
      </el-card>

      <!-- 人件費 -->
      <el-card class="kpi-card" shadow="never">
        <template #header>
          <span class="section-title">人件費</span>
        </template>
        <div class="kpi-items">
          <div class="kpi-item">
            <span class="kpi-label">月額人件費合計</span>
            <span class="kpi-value">{{ formatYen(kpis.laborCost?.monthlyTotal) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">一人当たり人件費</span>
            <span class="kpi-value">{{ formatYen(kpis.laborCost?.perCapita) }}</span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">売上人件費率</span>
            <span class="kpi-value" :class="(kpis.laborCost?.salesToLaborRatio || 0) > 50 ? 'negative' : ''">
              {{ formatPct(kpis.laborCost?.salesToLaborRatio) }}
            </span>
          </div>
          <div class="kpi-item">
            <span class="kpi-label">前月比</span>
            <span class="kpi-value" :class="valueClass(kpis.laborCost?.momChange)">
              {{ formatPctSigned(kpis.laborCost?.momChange) }}
            </span>
          </div>
        </div>
        <!-- 人件費推移 -->
        <div v-if="kpis.laborCost?.monthlyTrend?.length" class="overdue-list">
          <div class="overdue-header">月次人件費推移</div>
          <el-table :data="kpis.laborCost.monthlyTrend" size="small" :show-header="true" stripe>
            <el-table-column prop="month" label="月" width="100" />
            <el-table-column label="人件費" align="right">
              <template #default="{ row }">{{ formatYen(row.amount) }}</template>
            </el-table-column>
            <el-table-column label="前月比" width="90" align="center">
              <template #default="{ row }">
                <span :class="valueClass(row.change)">{{ formatPctSigned(row.change) }}</span>
              </template>
            </el-table-column>
          </el-table>
        </div>
      </el-card>

      <!-- 組織健全性 -->
      <el-card class="kpi-card kpi-card-wide" shadow="never">
        <template #header>
          <span class="section-title">組織健全性スコア</span>
        </template>
        <div class="kpi-items soundness-items">
          <div class="kpi-item soundness-item">
            <div class="gauge-label">従業員満足度</div>
            <div class="gauge-value" :class="gaugeClass(kpis.health?.satisfactionScore, 70)">
              {{ kpis.health?.satisfactionScore ?? '-' }}<span class="gauge-unit">点</span>
            </div>
            <div class="gauge-hint">目安: 70点以上</div>
          </div>
          <div class="kpi-item soundness-item">
            <div class="gauge-label">管理職比率</div>
            <div class="gauge-value" :class="gaugeClass(kpis.health?.managerRatio, 10)">
              {{ formatPct(kpis.health?.managerRatio) }}
            </div>
            <div class="gauge-hint">目安: 10%以上</div>
          </div>
          <div class="kpi-item soundness-item">
            <div class="gauge-label">女性管理職比率</div>
            <div class="gauge-value" :class="gaugeClass(kpis.health?.femaleManagerRatio, 20)">
              {{ formatPct(kpis.health?.femaleManagerRatio) }}
            </div>
            <div class="gauge-hint">目安: 20%以上</div>
          </div>
          <div class="kpi-item soundness-item">
            <div class="gauge-label">平均年齢</div>
            <div class="gauge-value">
              {{ kpis.health?.avgAge ?? '-' }}<span class="gauge-unit">歳</span>
            </div>
            <div class="gauge-hint">バランス重要</div>
          </div>
        </div>
      </el-card>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'
import { User, Refresh, InfoFilled } from '@element-plus/icons-vue'
import * as echarts from 'echarts/core'
import { BarChart, LineChart, PieChart } from 'echarts/charts'
import {
  TitleComponent, TooltipComponent, LegendComponent,
  GridComponent
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'

echarts.use([
  BarChart, LineChart, PieChart, TitleComponent, TooltipComponent,
  LegendComponent, GridComponent, CanvasRenderer
])

const loading = ref(false)
const analysisPeriod = ref(new Date().toISOString().slice(0, 7))
const analysisResult = ref<any>(null)

const deptChartRef = ref<HTMLElement>()
const overtimeChartRef = ref<HTMLElement>()
const turnoverChartRef = ref<HTMLElement>()
let deptChart: echarts.ECharts | null = null
let overtimeChart: echarts.ECharts | null = null
let turnoverChart: echarts.ECharts | null = null

const kpis = computed(() => analysisResult.value?.kpis || {})
const risks = computed(() => analysisResult.value?.risks || [])

const formatYen = (v: any) => v != null ? `¥${Number(v).toLocaleString('ja-JP')}` : '-'
const formatPct = (v: any) => v != null ? `${Number(v).toFixed(1)}%` : '-'
const formatPctSigned = (v: any) => {
  if (v == null) return '-'
  const n = Number(v)
  return `${n >= 0 ? '+' : ''}${n.toFixed(1)}%`
}
const formatHours = (v: any) => v != null ? `${Number(v).toFixed(1)}h` : '-'
const formatYears = (v: any) => v != null ? `${Number(v).toFixed(1)}年` : '-'

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

async function runAnalysis() {
  loading.value = true
  try {
    // API未実装のためデモデータをロード
    await new Promise(r => setTimeout(r, 600))
    loadDemoData()
    nextTick(() => {
      renderDeptChart()
      renderOvertimeChart()
      renderTurnoverChart()
    })
  } finally {
    loading.value = false
  }
}

function loadDemoData() {
  analysisResult.value = {
    analysisDate: new Date().toISOString().split('T')[0],
    period: analysisPeriod.value.replace('-', '年') + '月',
    kpis: {
      headcount: {
        total: 48,
        fullTime: 32,
        contract: 10,
        partTime: 6,
        byDepartment: [
          { name: '営業部', count: 12 },
          { name: '開発部', count: 15 },
          { name: '管理部', count: 8 },
          { name: 'CS部', count: 7 },
          { name: '経営企画', count: 6 }
        ]
      },
      attendance: {
        avgOvertimeHours: 28.5,
        paidLeaveRate: 62.3,
        attendanceRate: 96.8,
        overLimitCount: 2,
        overtimeTrend: [
          { month: '2025-10', hours: 32.1 },
          { month: '2025-11', hours: 35.8 },
          { month: '2025-12', hours: 41.2 },
          { month: '2026-01', hours: 25.4 },
          { month: '2026-02', hours: 30.1 },
          { month: '2026-03', hours: 28.5 }
        ]
      },
      turnover: {
        annualRate: 12.5,
        avgTenure: 4.2,
        monthlyTrend: [
          { month: '2025-10', rate: 10.2, hires: 2, exits: 1 },
          { month: '2025-11', rate: 10.8, hires: 0, exits: 1 },
          { month: '2025-12', rate: 11.5, hires: 1, exits: 2 },
          { month: '2026-01', rate: 12.0, hires: 3, exits: 1 },
          { month: '2026-02', rate: 12.2, hires: 1, exits: 0 },
          { month: '2026-03', rate: 12.5, hires: 0, exits: 1 }
        ],
        retirementRisk: [
          { name: '鈴木太郎', department: '開発部', reason: '勤続8年・昇給停滞', level: 'high' },
          { name: '佐藤花子', department: '営業部', reason: '残業月50h超・3ヶ月連続', level: 'high' },
          { name: '田中一郎', department: '管理部', reason: '定年まで2年', level: 'medium' }
        ]
      },
      laborCost: {
        monthlyTotal: 18500000,
        perCapita: 385400,
        salesToLaborRatio: 38.2,
        momChange: 2.1,
        monthlyTrend: [
          { month: '2025-10', amount: 17800000, change: 1.5 },
          { month: '2025-11', amount: 17950000, change: 0.8 },
          { month: '2025-12', amount: 19200000, change: 7.0 },
          { month: '2026-01', amount: 18100000, change: -5.7 },
          { month: '2026-02', amount: 18120000, change: 0.1 },
          { month: '2026-03', amount: 18500000, change: 2.1 }
        ]
      },
      health: {
        satisfactionScore: 72,
        managerRatio: 14.6,
        femaleManagerRatio: 17.1,
        avgAge: 34.8
      }
    },
    risks: [
      {
        severity: 'high',
        category: '労務管理',
        title: '36協定超過の恐れ',
        description: '開発部の2名が月間残業時間45時間を超過しています。佐藤花子さんは3ヶ月連続で50時間超となっており、特別条項の適用回数にも注意が必要です。',
        recommendation: '該当者の業務負荷を早急に見直し、業務の再分配またはチーム増員を検討してください。'
      },
      {
        severity: 'medium',
        category: '人材定着',
        title: '離職率が上昇傾向',
        description: '年間離職率が12.5%に上昇しています（前年同期比+2.3ポイント）。特に開発部でエンジニアの退職が続いています。',
        recommendation: '退職面談の分析結果を踏まえ、報酬制度・キャリアパスの見直しを検討してください。1on1ミーティングの実施頻度も確認してください。'
      },
      {
        severity: 'low',
        category: 'ダイバーシティ',
        title: '女性管理職比率が目標未達',
        description: '女性管理職比率は17.1%で、政府目標の30%に対して未達です。管理職候補の育成計画が必要です。',
        recommendation: '女性社員向けのリーダーシップ研修やメンタリングプログラムの導入を検討してください。'
      }
    ],
    summary: '2026年3月時点の人事状況は概ね安定していますが、いくつかの課題があります。従業員48名体制で、正社員比率66.7%と健全な構成です。一方で、36協定超過の恐れがある従業員が2名おり、早急な対応が必要です。離職率は12.5%と上昇傾向にあり、特に開発部のリテンション施策が急務です。従業員満足度は72点と一定の水準を維持していますが、残業時間の適正化と有給取得率の向上が改善ポイントです。'
  }
}

function renderDeptChart() {
  if (!deptChartRef.value) return
  deptChart?.dispose()
  deptChart = echarts.init(deptChartRef.value)
  const depts = kpis.value.headcount?.byDepartment || []
  deptChart.setOption({
    tooltip: { trigger: 'item', formatter: '{b}: {c}名 ({d}%)' },
    series: [{
      type: 'pie', radius: ['40%', '70%'], center: ['50%', '50%'],
      label: { fontSize: 11, formatter: '{b}\n{c}名' },
      data: depts.map((d: any, i: number) => ({
        name: d.name, value: d.count,
        itemStyle: { color: ['#8b5cf6', '#6366f1', '#3b82f6', '#22c55e', '#f59e0b'][i % 5] }
      }))
    }]
  })
}

function renderOvertimeChart() {
  if (!overtimeChartRef.value) return
  overtimeChart?.dispose()
  overtimeChart = echarts.init(overtimeChartRef.value)
  const trend = kpis.value.attendance?.overtimeTrend || []
  overtimeChart.setOption({
    tooltip: { trigger: 'axis', formatter: (p: any) => `${p[0].axisValue}<br/>平均残業: ${p[0].value}h` },
    grid: { top: 10, left: 10, right: 10, bottom: 20, containLabel: true },
    xAxis: { type: 'category', data: trend.map((t: any) => t.month), axisLabel: { fontSize: 11, color: '#64748b' }, axisLine: { lineStyle: { color: '#e2e8f0' } } },
    yAxis: { type: 'value', axisLabel: { fontSize: 10, color: '#94a3b8', formatter: '{value}h' }, splitLine: { lineStyle: { color: '#f1f5f9', type: 'dashed' } } },
    series: [{
      type: 'bar', data: trend.map((t: any) => ({
        value: t.hours,
        itemStyle: { color: t.hours > 40 ? '#ef4444' : t.hours > 30 ? '#f59e0b' : '#8b5cf6', borderRadius: [4, 4, 0, 0] }
      })),
      barWidth: '40%',
      markLine: { silent: true, data: [{ yAxis: 45, lineStyle: { color: '#ef4444', type: 'dashed', width: 2 }, label: { formatter: '45h上限', fontSize: 10 } }] }
    }]
  })
}

function renderTurnoverChart() {
  if (!turnoverChartRef.value) return
  turnoverChart?.dispose()
  turnoverChart = echarts.init(turnoverChartRef.value)
  const trend = kpis.value.turnover?.monthlyTrend || []
  turnoverChart.setOption({
    tooltip: { trigger: 'axis', formatter: (p: any) => `${p[0].axisValue}<br/>離職率: ${p[0].value}%<br/>入社: ${p[1]?.value ?? '-'}名 / 退社: ${p[2]?.value ?? '-'}名` },
    grid: { top: 10, left: 10, right: 30, bottom: 20, containLabel: true },
    xAxis: { type: 'category', data: trend.map((t: any) => t.month), axisLabel: { fontSize: 11, color: '#64748b' }, axisLine: { lineStyle: { color: '#e2e8f0' } } },
    yAxis: [
      { type: 'value', axisLabel: { fontSize: 10, color: '#94a3b8', formatter: '{value}%' }, splitLine: { lineStyle: { color: '#f1f5f9', type: 'dashed' } } },
      { type: 'value', axisLabel: { fontSize: 10, color: '#94a3b8' }, splitLine: { show: false } }
    ],
    series: [
      { type: 'line', data: trend.map((t: any) => t.rate), smooth: true, lineStyle: { color: '#ef4444', width: 2 }, itemStyle: { color: '#ef4444' }, symbol: 'circle', symbolSize: 6 },
      { type: 'bar', yAxisIndex: 1, data: trend.map((t: any) => t.hires), barWidth: '20%', itemStyle: { color: '#22c55e', borderRadius: [4, 4, 0, 0] }, stack: 'movement' },
      { type: 'bar', yAxisIndex: 1, data: trend.map((t: any) => -t.exits), barWidth: '20%', itemStyle: { color: '#f97316', borderRadius: [0, 0, 4, 4] }, stack: 'movement' }
    ]
  })
}

function handleResize() {
  deptChart?.resize()
  overtimeChart?.resize()
  turnoverChart?.resize()
}

onMounted(() => {
  runAnalysis()
  window.addEventListener('resize', handleResize)
})

onUnmounted(() => {
  window.removeEventListener('resize', handleResize)
  deptChart?.dispose()
  overtimeChart?.dispose()
  turnoverChart?.dispose()
})
</script>

<style scoped>
.hr-analysis-page {
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
  color: #8b5cf6;
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

/* ミニチャート */
.mini-chart {
  width: 100%;
  height: 200px;
}

/* テーブルエリア */
.overdue-list {
  margin-top: 12px;
}

.overdue-header {
  font-size: 13px;
  font-weight: 600;
  color: #475569;
  margin-bottom: 6px;
}

.amount-cell {
  font-variant-numeric: tabular-nums;
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

.gauge-unit {
  font-size: 14px;
  font-weight: 400;
  margin-left: 2px;
}

.gauge-hint {
  font-size: 11px;
  color: #cbd5e1;
  margin-top: 4px;
}
</style>
