<template>
  <div class="analytics-page">
    <!-- AI 自然语言查询区域 -->
    <el-card class="ai-section">
      <template #header>
        <div class="section-header">
          <el-icon class="ai-icon"><MagicStick /></el-icon>
          <span>AI販売分析</span>
        </div>
      </template>
      <div class="ai-query-box">
        <el-input
          v-model="aiQuery"
          type="textarea"
          :rows="2"
          placeholder="自然言語で質問してください。例：「過去3ヶ月の売上トレンドを見せて」「トップ10の顧客を教えて」"
          @keydown.enter.ctrl="executeAiQuery"
        />
        <div class="query-actions">
          <el-date-picker
            v-model="dateRange"
            type="daterange"
            start-placeholder="開始日"
            end-placeholder="終了日"
            value-format="YYYY-MM-DD"
            size="small"
          />
          <el-button type="primary" :loading="aiLoading" @click="executeAiQuery">
            <el-icon><Search /></el-icon> 分析
          </el-button>
        </div>
      </div>
      
      <!-- AI 结果展示 -->
      <div v-if="aiResult" class="ai-result">
        <div v-if="aiResult.explanation" class="ai-explanation">
          <el-icon><InfoFilled /></el-icon>
          {{ aiResult.explanation }}
        </div>
        <div v-if="aiResult.echartsConfig" class="chart-container" ref="aiChartRef"></div>
        <div v-if="aiResult.data && aiResult.chartType === 'table'" class="data-table">
          <el-table :data="aiResult.data" stripe size="small" max-height="400">
            <el-table-column 
              v-for="col in aiTableColumns" 
              :key="col" 
              :prop="col" 
              :label="col"
              min-width="100"
            />
          </el-table>
        </div>
        <div v-if="aiResult.sql" class="sql-preview">
          <el-collapse>
            <el-collapse-item title="実行されたSQL" name="sql">
              <pre>{{ aiResult.sql }}</pre>
            </el-collapse-item>
          </el-collapse>
        </div>
      </div>
    </el-card>

    <!-- 传统图表区域 -->
    <div class="charts-grid">
      <!-- 概览卡片 -->
      <el-card class="overview-card">
        <template #header>
          <span>販売概要</span>
          <el-date-picker
            v-model="overviewDateRange"
            type="daterange"
            start-placeholder="開始日"
            end-placeholder="終了日"
            value-format="YYYY-MM-DD"
            size="small"
            @change="loadOverview"
          />
        </template>
        <div class="stats-row" v-loading="overviewLoading">
          <div class="stat-item">
            <div class="stat-value">{{ formatNumber(overview.orderCount) }}</div>
            <div class="stat-label">受注件数</div>
          </div>
          <div class="stat-item">
            <div class="stat-value">¥{{ formatNumber(overview.totalAmount) }}</div>
            <div class="stat-label">売上合計</div>
          </div>
          <div class="stat-item">
            <div class="stat-value">{{ formatNumber(overview.customerCount) }}</div>
            <div class="stat-label">顧客数</div>
          </div>
          <div class="stat-item">
            <div class="stat-value">¥{{ formatNumber(overview.avgOrderAmount) }}</div>
            <div class="stat-label">平均単価</div>
          </div>
        </div>
      </el-card>

      <!-- 销售趋势图 -->
      <el-card class="chart-card">
        <template #header>
          <span>売上トレンド</span>
          <el-radio-group v-model="trendGranularity" size="small" @change="loadTrend">
            <el-radio-button value="day">日</el-radio-button>
            <el-radio-button value="week">週</el-radio-button>
            <el-radio-button value="month">月</el-radio-button>
          </el-radio-group>
        </template>
        <div class="chart-container" ref="trendChartRef" v-loading="trendLoading"></div>
      </el-card>

      <!-- 客户排名 -->
      <el-card class="chart-card">
        <template #header>
          <span>顧客ランキング</span>
        </template>
        <div class="chart-container" ref="customerChartRef" v-loading="customerLoading"></div>
      </el-card>

      <!-- 商品排名 -->
      <el-card class="chart-card">
        <template #header>
          <span>商品ランキング</span>
        </template>
        <div class="chart-container" ref="productChartRef" v-loading="productLoading"></div>
      </el-card>

      <!-- 客户变化趋势 -->
      <el-card class="chart-card wide">
        <template #header>
          <span>顧客動向分析</span>
          <el-radio-group v-model="customerTrendType" size="small" @change="loadCustomerTrend">
            <el-radio-button value="rising">上昇</el-radio-button>
            <el-radio-button value="declining">下降</el-radio-button>
            <el-radio-button value="both">全て</el-radio-button>
          </el-radio-group>
        </template>
        <el-table :data="customerTrendData" stripe size="small" v-loading="customerTrendLoading">
          <el-table-column prop="customerName" label="顧客名" min-width="150" />
          <el-table-column label="前期" width="150">
            <template #default="{ row }">
              {{ row.prevOrderCount }}件 / ¥{{ formatNumber(row.prevAmount) }}
            </template>
          </el-table-column>
          <el-table-column label="今期" width="150">
            <template #default="{ row }">
              {{ row.recentOrderCount }}件 / ¥{{ formatNumber(row.recentAmount) }}
            </template>
          </el-table-column>
          <el-table-column label="変化" width="150">
            <template #default="{ row }">
              <span :class="['trend-badge', row.trend]">
                {{ row.changePercent > 0 ? '+' : '' }}{{ row.changePercent }}%
              </span>
            </template>
          </el-table-column>
        </el-table>
      </el-card>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, nextTick, watch, computed } from 'vue'
import { MagicStick, Search, InfoFilled } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import api from '../api'
import * as echarts from 'echarts/core'
import { LineChart, BarChart, PieChart } from 'echarts/charts'
import { TitleComponent, TooltipComponent, LegendComponent, GridComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'

echarts.use([LineChart, BarChart, PieChart, TitleComponent, TooltipComponent, LegendComponent, GridComponent, CanvasRenderer])

// AI 查询相关
const aiQuery = ref('')
const aiLoading = ref(false)
const aiResult = ref<any>(null)
const dateRange = ref<string[]>([])
const aiChartRef = ref<HTMLElement | null>(null)
let aiChart: echarts.ECharts | null = null

const aiTableColumns = computed(() => {
  if (!aiResult.value?.data?.[0]) return []
  return Object.keys(aiResult.value.data[0])
})

// 概览
const overviewLoading = ref(false)
const overviewDateRange = ref<string[]>([])
const overview = reactive({
  orderCount: 0,
  totalAmount: 0,
  customerCount: 0,
  avgOrderAmount: 0
})

// 趋势图
const trendLoading = ref(false)
const trendGranularity = ref('day')
const trendChartRef = ref<HTMLElement | null>(null)
let trendChart: echarts.ECharts | null = null

// 客户排名
const customerLoading = ref(false)
const customerChartRef = ref<HTMLElement | null>(null)
let customerChart: echarts.ECharts | null = null

// 商品排名
const productLoading = ref(false)
const productChartRef = ref<HTMLElement | null>(null)
let productChart: echarts.ECharts | null = null

// 客户变化
const customerTrendLoading = ref(false)
const customerTrendType = ref('both')
const customerTrendData = ref<any[]>([])

function formatNumber(val: any) {
  const num = Number(val || 0)
  if (!Number.isFinite(num)) return '0'
  return num.toLocaleString()
}

async function executeAiQuery() {
  if (!aiQuery.value.trim()) {
    ElMessage.warning('質問を入力してください')
    return
  }
  aiLoading.value = true
  aiResult.value = null
  try {
    const res = await api.post('/analytics/sales/ai-analyze', {
      query: aiQuery.value,
      dateFrom: dateRange.value?.[0],
      dateTo: dateRange.value?.[1]
    })
    aiResult.value = res.data
    
    if (res.data.success && res.data.echartsConfig && res.data.chartType !== 'table') {
      await nextTick()
      renderAiChart(res.data.echartsConfig)
    }
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '分析に失敗しました')
  } finally {
    aiLoading.value = false
  }
}

function renderAiChart(config: any) {
  if (!aiChartRef.value) return
  if (aiChart) aiChart.dispose()
  aiChart = echarts.init(aiChartRef.value)
  aiChart.setOption(config)
}

async function loadOverview() {
  overviewLoading.value = true
  try {
    const params = new URLSearchParams()
    if (overviewDateRange.value?.[0]) params.append('fromDate', overviewDateRange.value[0])
    if (overviewDateRange.value?.[1]) params.append('toDate', overviewDateRange.value[1])
    
    const res = await api.get(`/analytics/sales/overview?${params.toString()}`)
    Object.assign(overview, res.data)
  } catch (e) {
    console.error('Failed to load overview:', e)
  } finally {
    overviewLoading.value = false
  }
}

async function loadTrend() {
  trendLoading.value = true
  try {
    const params = new URLSearchParams({ granularity: trendGranularity.value })
    const res = await api.get(`/analytics/sales/trend?${params.toString()}`)
    
    await nextTick()
    if (!trendChartRef.value) return
    if (trendChart) trendChart.dispose()
    trendChart = echarts.init(trendChartRef.value)
    
    const data = res.data.data || []
    trendChart.setOption({
      tooltip: { trigger: 'axis' },
      legend: { bottom: 0 },
      xAxis: { type: 'category', data: data.map((d: any) => d.period) },
      yAxis: [
        { type: 'value', name: '件数' },
        { type: 'value', name: '売上', position: 'right' }
      ],
      series: [
        { name: '受注件数', type: 'bar', data: data.map((d: any) => d.orderCount), itemStyle: { color: '#91cc75' } },
        { name: '売上金額', type: 'line', yAxisIndex: 1, data: data.map((d: any) => d.totalAmount), smooth: true, itemStyle: { color: '#5470c6' } }
      ]
    })
  } catch (e) {
    console.error('Failed to load trend:', e)
  } finally {
    trendLoading.value = false
  }
}

async function loadCustomerRanking() {
  customerLoading.value = true
  try {
    const res = await api.get('/analytics/sales/by-customer?limit=10')
    
    await nextTick()
    if (!customerChartRef.value) return
    if (customerChart) customerChart.dispose()
    customerChart = echarts.init(customerChartRef.value)
    
    const data = res.data.data || []
    customerChart.setOption({
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      xAxis: { type: 'value' },
      yAxis: { type: 'category', data: data.map((d: any) => d.customerName).reverse() },
      series: [{
        type: 'bar',
        data: data.map((d: any) => d.totalAmount).reverse(),
        itemStyle: { color: '#5470c6' }
      }]
    })
  } catch (e) {
    console.error('Failed to load customer ranking:', e)
  } finally {
    customerLoading.value = false
  }
}

async function loadProductRanking() {
  productLoading.value = true
  try {
    const res = await api.get('/analytics/sales/by-product?limit=10')
    
    await nextTick()
    if (!productChartRef.value) return
    if (productChart) productChart.dispose()
    productChart = echarts.init(productChartRef.value)
    
    const data = res.data.data || []
    productChart.setOption({
      tooltip: { trigger: 'item', formatter: '{b}: ¥{c} ({d}%)' },
      series: [{
        type: 'pie',
        radius: ['40%', '70%'],
        center: ['50%', '50%'],
        data: data.map((d: any) => ({ name: d.materialName || d.materialCode, value: d.totalAmount })),
        label: { show: true, formatter: '{b}' }
      }]
    })
  } catch (e) {
    console.error('Failed to load product ranking:', e)
  } finally {
    productLoading.value = false
  }
}

async function loadCustomerTrend() {
  customerTrendLoading.value = true
  try {
    const res = await api.get(`/analytics/sales/customer-trend?trend=${customerTrendType.value}&limit=10`)
    customerTrendData.value = res.data.data || []
  } catch (e) {
    console.error('Failed to load customer trend:', e)
  } finally {
    customerTrendLoading.value = false
  }
}

onMounted(() => {
  loadOverview()
  loadTrend()
  loadCustomerRanking()
  loadProductRanking()
  loadCustomerTrend()
})

// 监听窗口大小变化，重新调整图表尺寸
window.addEventListener('resize', () => {
  trendChart?.resize()
  customerChart?.resize()
  productChart?.resize()
  aiChart?.resize()
})
</script>

<style scoped>
.analytics-page {
  padding: 20px;
  background: #f5f7fa;
  min-height: 100vh;
}

.ai-section {
  margin-bottom: 20px;
}

.section-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 16px;
  font-weight: 600;
}

.ai-icon {
  color: #409eff;
}

.ai-query-box {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.query-actions {
  display: flex;
  gap: 12px;
  align-items: center;
}

.ai-result {
  margin-top: 20px;
  padding-top: 20px;
  border-top: 1px solid #ebeef5;
}

.ai-explanation {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 12px;
  background: #f0f9eb;
  border-radius: 4px;
  color: #67c23a;
  margin-bottom: 16px;
}

.sql-preview {
  margin-top: 16px;
}

.sql-preview pre {
  background: #f5f7fa;
  padding: 12px;
  border-radius: 4px;
  overflow-x: auto;
  font-size: 12px;
}

.charts-grid {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 20px;
}

.overview-card {
  grid-column: span 2;
}

.chart-card {
  min-height: 360px;
}

.chart-card.wide {
  grid-column: span 2;
}

.chart-card :deep(.el-card__header) {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.chart-container {
  height: 300px;
}

.stats-row {
  display: flex;
  justify-content: space-around;
  padding: 20px 0;
}

.stat-item {
  text-align: center;
}

.stat-value {
  font-size: 28px;
  font-weight: 600;
  color: #303133;
}

.stat-label {
  font-size: 14px;
  color: #909399;
  margin-top: 4px;
}

.trend-badge {
  padding: 2px 8px;
  border-radius: 4px;
  font-weight: 600;
}

.trend-badge.rising {
  background: #f0f9eb;
  color: #67c23a;
}

.trend-badge.declining {
  background: #fef0f0;
  color: #f56c6c;
}

.trend-badge.stable {
  background: #f4f4f5;
  color: #909399;
}

.data-table {
  margin-top: 16px;
}

@media (max-width: 1200px) {
  .charts-grid {
    grid-template-columns: 1fr;
  }
  
  .overview-card,
  .chart-card.wide {
    grid-column: span 1;
  }
}
</style>

