<template>
  <div class="analytics-dashboard">
    <!-- ヘッダー -->
    <div class="dashboard-header">
      <div class="header-left">
        <el-icon class="header-icon"><DataAnalysis /></el-icon>
        <h1>分析ダッシュボード</h1>
      </div>
      <div class="header-right">
        <el-date-picker
          v-model="selectedMonth"
          type="month"
          placeholder="分析月"
          value-format="YYYY-MM"
          format="YYYY年MM月"
          @change="loadAll"
        />
        <el-button type="primary" plain @click="loadAll">
          <el-icon><Refresh /></el-icon>
          更新
        </el-button>
      </div>
    </div>

    <!-- KPIカード -->
    <div class="kpi-cards" v-loading="loadingDashboard">
      <div class="kpi-card">
        <div class="kpi-icon" style="background: linear-gradient(135deg, #667eea 0%, #764ba2 100%)">
          <el-icon><User /></el-icon>
        </div>
        <div class="kpi-content">
          <div class="kpi-value">{{ dashboard.totalResources || 0 }}</div>
          <div class="kpi-label">総リソース数</div>
          <div class="kpi-sub">稼働中: {{ dashboard.assignedCount || 0 }} / 待機: {{ dashboard.availableCount || 0 }}</div>
        </div>
      </div>

      <div class="kpi-card">
        <div class="kpi-icon" style="background: linear-gradient(135deg, #f093fb 0%, #f5576c 100%)">
          <el-icon><TrendCharts /></el-icon>
        </div>
        <div class="kpi-content">
          <div class="kpi-value">{{ dashboard.utilizationRate || 0 }}%</div>
          <div class="kpi-label">稼働率</div>
          <div class="kpi-sub">
            <el-progress :percentage="dashboard.utilizationRate || 0" :show-text="false" :stroke-width="6" />
          </div>
        </div>
      </div>

      <div class="kpi-card">
        <div class="kpi-icon" style="background: linear-gradient(135deg, #4facfe 0%, #00f2fe 100%)">
          <el-icon><Money /></el-icon>
        </div>
        <div class="kpi-content">
          <div class="kpi-value">¥{{ formatNumber(dashboard.monthlyBilling) }}</div>
          <div class="kpi-label">今月売上</div>
          <div class="kpi-sub">利益率: {{ dashboard.monthlyProfitRate || 0 }}%</div>
        </div>
      </div>

      <div class="kpi-card">
        <div class="kpi-icon" style="background: linear-gradient(135deg, #43e97b 0%, #38f9d7 100%)">
          <el-icon><Coin /></el-icon>
        </div>
        <div class="kpi-content">
          <div class="kpi-value">¥{{ formatNumber(dashboard.monthlyProfit) }}</div>
          <div class="kpi-label">今月利益</div>
          <div class="kpi-sub">原価: ¥{{ formatNumber(dashboard.monthlyCost) }}</div>
        </div>
      </div>

      <div class="kpi-card">
        <div class="kpi-icon" style="background: linear-gradient(135deg, #fa709a 0%, #fee140 100%)">
          <el-icon><Document /></el-icon>
        </div>
        <div class="kpi-content">
          <div class="kpi-value">{{ dashboard.activeContracts || 0 }}</div>
          <div class="kpi-label">有効契約数</div>
          <div class="kpi-sub">派遣: {{ dashboard.dispatchContracts || 0 }} / SES: {{ dashboard.sesContracts || 0 }}</div>
        </div>
      </div>

      <div class="kpi-card">
        <div class="kpi-icon" style="background: linear-gradient(135deg, #a8edea 0%, #fed6e3 100%)">
          <el-icon><Tickets /></el-icon>
        </div>
        <div class="kpi-content">
          <div class="kpi-value">¥{{ formatNumber(dashboard.unpaidAmount) }}</div>
          <div class="kpi-label">未入金額</div>
          <div class="kpi-sub">オープン案件: {{ dashboard.openProjects || 0 }}件</div>
        </div>
      </div>
    </div>

    <!-- グラフエリア -->
    <el-row :gutter="20" class="chart-row">
      <!-- 月次売上推移 -->
      <el-col :span="16">
        <el-card class="chart-card">
          <template #header>
            <span class="card-title">月次売上推移</span>
          </template>
          <div class="chart-container" v-loading="loadingRevenue">
            <div class="simple-chart" v-if="revenueData.length > 0">
              <div class="chart-bars">
                <div 
                  v-for="item in revenueData" 
                  :key="item.yearMonth" 
                  class="bar-group"
                >
                  <div class="bar-wrapper">
                    <div 
                      class="bar billing-bar" 
                      :style="{ height: getBarHeight(item.billing, maxBilling) + '%' }"
                      :title="`売上: ¥${formatNumber(item.billing)}`"
                    ></div>
                    <div 
                      class="bar profit-bar" 
                      :style="{ height: getBarHeight(item.profit, maxBilling) + '%' }"
                      :title="`利益: ¥${formatNumber(item.profit)}`"
                    ></div>
                  </div>
                  <div class="bar-label">{{ formatMonthShort(item.yearMonth) }}</div>
                </div>
              </div>
              <div class="chart-legend">
                <span class="legend-item"><span class="legend-color billing"></span>売上</span>
                <span class="legend-item"><span class="legend-color profit"></span>利益</span>
              </div>
            </div>
            <el-empty v-else description="データがありません" />
          </div>
        </el-card>
      </el-col>

      <!-- 契約タイプ別 -->
      <el-col :span="8">
        <el-card class="chart-card">
          <template #header>
            <span class="card-title">契約タイプ別</span>
          </template>
          <div class="chart-container" v-loading="loadingContractType">
            <div class="type-stats" v-if="contractTypeData.length > 0">
              <div 
                v-for="item in contractTypeData" 
                :key="item.contractType" 
                class="type-item"
              >
                <div class="type-header">
                  <el-tag :type="getContractTagType(item.contractType)" size="small">
                    {{ getContractLabel(item.contractType) }}
                  </el-tag>
                  <span class="type-count">{{ item.contractCount }}件</span>
                </div>
                <div class="type-amount">¥{{ formatNumber(item.billing) }}</div>
                <div class="type-profit">
                  利益: ¥{{ formatNumber(item.profit) }}
                  <span class="profit-rate">({{ item.profitRate }}%)</span>
                </div>
              </div>
            </div>
            <el-empty v-else description="データがありません" />
          </div>
        </el-card>
      </el-col>
    </el-row>

    <el-row :gutter="20" class="table-row">
      <!-- 顧客別売上 -->
      <el-col :span="12">
        <el-card class="table-card">
          <template #header>
            <span class="card-title">顧客別売上 TOP10</span>
          </template>
          <el-table :data="clientData.slice(0, 10)" size="small" v-loading="loadingClient">
            <el-table-column label="顧客" prop="clientName" min-width="120">
              <template #default="{ row }">
                <div>{{ row.clientName || '-' }}</div>
                <div class="sub-text">{{ row.clientCode }}</div>
              </template>
            </el-table-column>
            <el-table-column label="売上" prop="billing" width="100" align="right">
              <template #default="{ row }">
                ¥{{ formatNumber(row.billing) }}
              </template>
            </el-table-column>
            <el-table-column label="利益率" prop="profitRate" width="80" align="right">
              <template #default="{ row }">
                <span :class="getProfitClass(row.profitRate)">{{ row.profitRate }}%</span>
              </template>
            </el-table-column>
            <el-table-column label="人数" prop="resourceCount" width="60" align="center" />
          </el-table>
        </el-card>
      </el-col>

      <!-- 契約終了予定 -->
      <el-col :span="12">
        <el-card class="table-card">
          <template #header>
            <div class="card-header-with-action">
              <span class="card-title">契約終了予定（30日以内）</span>
              <el-tag type="warning" size="small">{{ expiringContracts.length }}件</el-tag>
            </div>
          </template>
          <el-table :data="expiringContracts" size="small" v-loading="loadingExpiring">
            <el-table-column label="リソース" min-width="100">
              <template #default="{ row }">
                <div>{{ row.resourceName || '-' }}</div>
                <div class="sub-text">{{ row.resourceCode }}</div>
              </template>
            </el-table-column>
            <el-table-column label="顧客" prop="clientName" width="100" />
            <el-table-column label="終了日" prop="endDate" width="100">
              <template #default="{ row }">
                <span :class="{ 'urgent': row.daysRemaining <= 7 }">
                  {{ formatDate(row.endDate) }}
                </span>
              </template>
            </el-table-column>
            <el-table-column label="残日数" prop="daysRemaining" width="70" align="center">
              <template #default="{ row }">
                <el-tag :type="row.daysRemaining <= 7 ? 'danger' : 'warning'" size="small">
                  {{ row.daysRemaining }}日
                </el-tag>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
    </el-row>

    <!-- リソース別売上 -->
    <el-card class="table-card full-width">
      <template #header>
        <span class="card-title">リソース別売上・稼働</span>
      </template>
      <el-table :data="resourceData" size="small" v-loading="loadingResource" max-height="400">
        <el-table-column label="リソース" min-width="140">
          <template #default="{ row }">
            <div>{{ row.displayName || '-' }}</div>
            <div class="sub-text">{{ row.resourceCode }}</div>
          </template>
        </el-table-column>
        <el-table-column label="タイプ" prop="resourceType" width="80">
          <template #default="{ row }">
            <el-tag :type="getResourceTagType(row.resourceType)" size="small">
              {{ getResourceLabel(row.resourceType) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="顧客" prop="clientName" width="120" />
        <el-table-column label="稼働時間" prop="actualHours" width="90" align="right">
          <template #default="{ row }">
            {{ row.actualHours }}h
          </template>
        </el-table-column>
        <el-table-column label="残業" prop="overtimeHours" width="70" align="right">
          <template #default="{ row }">
            <span v-if="row.overtimeHours > 0" class="overtime">+{{ row.overtimeHours }}h</span>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column label="売上" prop="billing" width="110" align="right">
          <template #default="{ row }">
            ¥{{ formatNumber(row.billing) }}
          </template>
        </el-table-column>
        <el-table-column label="原価" prop="cost" width="100" align="right">
          <template #default="{ row }">
            <span v-if="row.cost">¥{{ formatNumber(row.cost) }}</span>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column label="利益" prop="profit" width="100" align="right">
          <template #default="{ row }">
            <span :class="getProfitClass(row.profitRate)">¥{{ formatNumber(row.profit) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="利益率" prop="profitRate" width="80" align="right">
          <template #default="{ row }">
            <span :class="getProfitClass(row.profitRate)">{{ row.profitRate }}%</span>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { DataAnalysis, User, TrendCharts, Money, Coin, Document, Tickets, Refresh } from '@element-plus/icons-vue'
import api from '../../api'

interface DashboardData {
  totalResources: number
  assignedCount: number
  availableCount: number
  utilizationRate: number
  activeContracts: number
  dispatchContracts: number
  sesContracts: number
  monthlyBilling: number
  monthlyCost: number
  monthlyProfit: number
  monthlyProfitRate: number
  unpaidAmount: number
  openProjects: number
}

const selectedMonth = ref(new Date().toISOString().substring(0, 7))

const loadingDashboard = ref(false)
const loadingRevenue = ref(false)
const loadingClient = ref(false)
const loadingResource = ref(false)
const loadingExpiring = ref(false)
const loadingContractType = ref(false)

const dashboard = ref<Partial<DashboardData>>({})
const revenueData = ref<any[]>([])
const clientData = ref<any[]>([])
const resourceData = ref<any[]>([])
const expiringContracts = ref<any[]>([])
const contractTypeData = ref<any[]>([])

const maxBilling = computed(() => {
  if (revenueData.value.length === 0) return 1
  return Math.max(...revenueData.value.map(d => d.billing)) || 1
})

const loadDashboard = async () => {
  loadingDashboard.value = true
  try {
    const res = await api.get('/staffing/analytics/dashboard')
    dashboard.value = res.data
  } catch (e: any) {
    console.error('Dashboard load error:', e)
  } finally {
    loadingDashboard.value = false
  }
}

const loadRevenue = async () => {
  loadingRevenue.value = true
  try {
    const res = await api.get('/staffing/analytics/monthly-revenue', { params: { months: 12 } })
    revenueData.value = res.data.data || []
  } catch (e: any) {
    console.error('Revenue load error:', e)
  } finally {
    loadingRevenue.value = false
  }
}

const loadClientData = async () => {
  loadingClient.value = true
  try {
    const res = await api.get('/staffing/analytics/revenue-by-client', { params: { yearMonth: selectedMonth.value } })
    clientData.value = res.data.data || []
  } catch (e: any) {
    console.error('Client data load error:', e)
  } finally {
    loadingClient.value = false
  }
}

const loadResourceData = async () => {
  loadingResource.value = true
  try {
    const res = await api.get('/staffing/analytics/revenue-by-resource', { params: { yearMonth: selectedMonth.value } })
    resourceData.value = res.data.data || []
  } catch (e: any) {
    console.error('Resource data load error:', e)
  } finally {
    loadingResource.value = false
  }
}

const loadExpiringContracts = async () => {
  loadingExpiring.value = true
  try {
    const res = await api.get('/staffing/analytics/expiring-contracts', { params: { days: 30 } })
    expiringContracts.value = res.data.data || []
  } catch (e: any) {
    console.error('Expiring contracts load error:', e)
  } finally {
    loadingExpiring.value = false
  }
}

const loadContractTypeData = async () => {
  loadingContractType.value = true
  try {
    const res = await api.get('/staffing/analytics/contract-type-stats', { params: { yearMonth: selectedMonth.value } })
    contractTypeData.value = res.data.data || []
  } catch (e: any) {
    console.error('Contract type data load error:', e)
  } finally {
    loadingContractType.value = false
  }
}

const loadAll = () => {
  loadDashboard()
  loadRevenue()
  loadClientData()
  loadResourceData()
  loadExpiringContracts()
  loadContractTypeData()
}

const getBarHeight = (value: number, max: number) => {
  return Math.max((value / max) * 80, 2)
}

const formatMonthShort = (ym: string) => {
  if (!ym) return ''
  return ym.substring(5) + '月'
}

const formatDate = (dateStr: string) => {
  if (!dateStr) return ''
  return new Date(dateStr).toLocaleDateString('ja-JP', { month: 'short', day: 'numeric' })
}

const formatNumber = (num: number) => {
  if (num === undefined || num === null) return '0'
  if (num >= 10000000) return (num / 10000000).toFixed(1) + '千万'
  if (num >= 10000) return (num / 10000).toFixed(0) + '万'
  return new Intl.NumberFormat('ja-JP').format(num)
}

const getContractLabel = (type: string) => {
  const map: Record<string, string> = { dispatch: '派遣', ses: 'SES', contract: '請負' }
  return map[type] || type
}

const getContractTagType = (type: string) => {
  const map: Record<string, string> = { dispatch: 'warning', ses: 'primary', contract: 'success' }
  return map[type] || 'info'
}

const getResourceLabel = (type: string) => {
  const map: Record<string, string> = { employee: '自社', freelancer: '個人', bp: 'BP' }
  return map[type] || type
}

const getResourceTagType = (type: string) => {
  const map: Record<string, string> = { employee: 'primary', freelancer: 'success', bp: 'warning' }
  return map[type] || 'info'
}

const getProfitClass = (rate: number) => {
  if (rate >= 30) return 'profit-high'
  if (rate >= 20) return 'profit-mid'
  if (rate >= 10) return 'profit-low'
  return 'profit-danger'
}

onMounted(() => {
  loadAll()
})
</script>

<style scoped>
.analytics-dashboard {
  padding: 20px;
  background: #f5f7fa;
  min-height: 100vh;
}

.dashboard-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 24px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 12px;
}

.header-icon {
  font-size: 28px;
  color: var(--el-color-primary);
}

.header-left h1 {
  margin: 0;
  font-size: 24px;
  font-weight: 600;
}

.header-right {
  display: flex;
  gap: 12px;
}

/* KPIカード */
.kpi-cards {
  display: grid;
  grid-template-columns: repeat(6, 1fr);
  gap: 16px;
  margin-bottom: 24px;
}

.kpi-card {
  background: white;
  border-radius: 12px;
  padding: 20px;
  display: flex;
  gap: 16px;
  box-shadow: 0 2px 12px rgba(0, 0, 0, 0.05);
}

.kpi-icon {
  width: 48px;
  height: 48px;
  border-radius: 12px;
  display: flex;
  align-items: center;
  justify-content: center;
  color: white;
  font-size: 24px;
  flex-shrink: 0;
}

.kpi-content {
  flex: 1;
  min-width: 0;
}

.kpi-value {
  font-size: 24px;
  font-weight: 700;
  color: #303133;
  line-height: 1.2;
}

.kpi-label {
  font-size: 13px;
  color: #909399;
  margin-top: 4px;
}

.kpi-sub {
  font-size: 12px;
  color: #c0c4cc;
  margin-top: 8px;
}

/* グラフエリア */
.chart-row {
  margin-bottom: 20px;
}

.chart-card {
  height: 360px;
}

.chart-container {
  height: 280px;
  display: flex;
  align-items: center;
  justify-content: center;
}

.card-title {
  font-weight: 600;
  color: #303133;
}

/* シンプルバーチャート */
.simple-chart {
  width: 100%;
  height: 100%;
  display: flex;
  flex-direction: column;
}

.chart-bars {
  flex: 1;
  display: flex;
  align-items: flex-end;
  justify-content: space-around;
  padding: 20px 10px;
}

.bar-group {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
}

.bar-wrapper {
  display: flex;
  gap: 4px;
  height: 180px;
  align-items: flex-end;
}

.bar {
  width: 20px;
  border-radius: 4px 4px 0 0;
  transition: height 0.3s ease;
  cursor: pointer;
}

.billing-bar {
  background: linear-gradient(180deg, #409eff 0%, #79bbff 100%);
}

.profit-bar {
  background: linear-gradient(180deg, #67c23a 0%, #95d475 100%);
}

.bar-label {
  font-size: 11px;
  color: #909399;
}

.chart-legend {
  display: flex;
  justify-content: center;
  gap: 20px;
  padding: 10px;
  border-top: 1px solid #ebeef5;
}

.legend-item {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: #606266;
}

.legend-color {
  width: 12px;
  height: 12px;
  border-radius: 2px;
}

.legend-color.billing {
  background: #409eff;
}

.legend-color.profit {
  background: #67c23a;
}

/* 契約タイプ別 */
.type-stats {
  width: 100%;
  padding: 10px;
}

.type-item {
  padding: 12px;
  border-bottom: 1px solid #ebeef5;
}

.type-item:last-child {
  border-bottom: none;
}

.type-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 8px;
}

.type-count {
  font-size: 13px;
  color: #909399;
}

.type-amount {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.type-profit {
  font-size: 12px;
  color: #67c23a;
  margin-top: 4px;
}

.profit-rate {
  color: #909399;
}

/* テーブルカード */
.table-row {
  margin-bottom: 20px;
}

.table-card {
  height: 380px;
}

.table-card.full-width {
  margin-bottom: 20px;
}

.card-header-with-action {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.sub-text {
  font-size: 11px;
  color: #c0c4cc;
}

.overtime {
  color: var(--el-color-danger);
}

.urgent {
  color: var(--el-color-danger);
  font-weight: 600;
}

.profit-high {
  color: #67c23a;
  font-weight: 600;
}

.profit-mid {
  color: #409eff;
}

.profit-low {
  color: #e6a23c;
}

.profit-danger {
  color: #f56c6c;
}

@media (max-width: 1600px) {
  .kpi-cards {
    grid-template-columns: repeat(3, 1fr);
  }
}

@media (max-width: 1200px) {
  .kpi-cards {
    grid-template-columns: repeat(2, 1fr);
  }
}
</style>

