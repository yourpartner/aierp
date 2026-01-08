<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ labels.title }}</div>
          <div class="page-actions">
            <el-button @click="exportReport" :disabled="reportData.length === 0">
              <el-icon><Download /></el-icon> {{ labels.export }}
            </el-button>
          </div>
        </div>
      </template>

      <!-- 筛选条件 -->
      <div class="filter-row">
        <el-date-picker
          v-model="filterDateRange"
          type="daterange"
          :start-placeholder="labels.startDate"
          :end-placeholder="labels.endDate"
          value-format="YYYY-MM-DD"
          style="width: 260px"
        />
        <el-select v-model="filterWarehouse" :placeholder="labels.warehouse" clearable style="width: 200px">
          <el-option v-for="w in warehouseOptions" :key="w.warehouse_code" :label="`${w.name} (${w.warehouse_code})`" :value="w.warehouse_code" />
        </el-select>
        <el-radio-group v-model="filterType" style="margin-left: 12px">
          <el-radio-button value="all">{{ labels.all }}</el-radio-button>
          <el-radio-button value="gain">{{ labels.gain }}</el-radio-button>
          <el-radio-button value="loss">{{ labels.loss }}</el-radio-button>
        </el-radio-group>
        <el-button type="primary" @click="loadReport">{{ labels.search }}</el-button>
      </div>

      <!-- 汇总信息 -->
      <div class="summary-row" v-if="summary">
        <el-row :gutter="16">
          <el-col :span="6">
            <el-statistic :title="labels.totalItems" :value="summary.totalCount" />
          </el-col>
          <el-col :span="6">
            <el-statistic :title="labels.gainItems" :value="summary.gainCount" class="stat-gain" />
          </el-col>
          <el-col :span="6">
            <el-statistic :title="labels.lossItems" :value="summary.lossCount" class="stat-loss" />
          </el-col>
          <el-col :span="6">
            <el-statistic :title="labels.netVariance" :value="netVariance" :precision="2" :value-style="netVarianceStyle" />
          </el-col>
        </el-row>
      </div>

      <!-- 报表数据 -->
      <el-table :data="filteredData" v-loading="loading" stripe style="width: 100%; margin-top: 20px" max-height="600">
        <el-table-column prop="count_no" :label="labels.countNo" width="140">
          <template #default="{ row }">
            <el-link type="primary" @click="viewCount(row.count_id)">{{ row.count_no }}</el-link>
          </template>
        </el-table-column>
        <el-table-column prop="count_date" :label="labels.countDate" width="110" />
        <el-table-column :label="labels.warehouse" width="140">
          <template #default="{ row }">
            {{ row.warehouse_name || row.warehouse_code }}
          </template>
        </el-table-column>
        <el-table-column :label="labels.material" min-width="200">
          <template #default="{ row }">
            <div>{{ row.material_name || row.material_code }}</div>
            <div class="text-muted">{{ row.material_code }}</div>
          </template>
        </el-table-column>
        <el-table-column prop="bin_code" :label="labels.bin" width="80" />
        <el-table-column prop="batch_no" :label="labels.batch" width="100" />
        <el-table-column prop="uom" :label="labels.uom" width="60" align="center" />
        <el-table-column prop="system_qty" :label="labels.systemQty" width="100" align="right">
          <template #default="{ row }">{{ formatNumber(row.system_qty) }}</template>
        </el-table-column>
        <el-table-column prop="actual_qty" :label="labels.actualQty" width="100" align="right">
          <template #default="{ row }">{{ formatNumber(row.actual_qty) }}</template>
        </el-table-column>
        <el-table-column :label="labels.varianceQty" width="100" align="right">
          <template #default="{ row }">
            <span :class="varianceClass(row.variance_qty)">{{ formatVariance(row.variance_qty) }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="labels.varianceType" width="80" align="center">
          <template #default="{ row }">
            <el-tag :type="row.variance_type === 'gain' ? 'success' : 'danger'" size="small">
              {{ row.variance_type === 'gain' ? labels.gain : labels.loss }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="variance_reason" :label="labels.varianceReason" min-width="150">
          <template #default="{ row }">{{ row.variance_reason || '-' }}</template>
        </el-table-column>
        <el-table-column :label="labels.status" width="90" align="center">
          <template #default="{ row }">
            <el-tag :type="statusType(row.status)" size="small">{{ statusLabel(row.status) }}</el-tag>
          </template>
        </el-table-column>
      </el-table>

      <div class="no-data" v-if="!loading && reportData.length === 0">
        {{ labels.noData }}
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { Download } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { useRouter } from 'vue-router'
import api from '../api'
import { useI18n } from '../i18n'

const router = useRouter()
const { lang } = useI18n()

// 多语言标签
const labels = computed(() => {
  const l = lang.value
  return {
    title: l === 'ja' ? '棚卸差異レポート' : l === 'en' ? 'Stock Count Variance Report' : '盘点差异报表',
    export: l === 'ja' ? 'エクスポート' : l === 'en' ? 'Export' : '导出',
    search: l === 'ja' ? '検索' : l === 'en' ? 'Search' : '查询',
    startDate: l === 'ja' ? '開始日' : l === 'en' ? 'Start Date' : '开始日期',
    endDate: l === 'ja' ? '終了日' : l === 'en' ? 'End Date' : '结束日期',
    warehouse: l === 'ja' ? '倉庫' : l === 'en' ? 'Warehouse' : '仓库',
    all: l === 'ja' ? 'すべて' : l === 'en' ? 'All' : '全部',
    gain: l === 'ja' ? '盤盈' : l === 'en' ? 'Gain' : '盘盈',
    loss: l === 'ja' ? '盤損' : l === 'en' ? 'Loss' : '盘亏',
    totalItems: l === 'ja' ? '差異件数' : l === 'en' ? 'Total Items' : '差异总数',
    gainItems: l === 'ja' ? '盤盈件数' : l === 'en' ? 'Gain Items' : '盘盈数',
    lossItems: l === 'ja' ? '盤損件数' : l === 'en' ? 'Loss Items' : '盘亏数',
    netVariance: l === 'ja' ? '純差異数量' : l === 'en' ? 'Net Variance' : '净差异数量',
    countNo: l === 'ja' ? '棚卸番号' : l === 'en' ? 'Count No.' : '盘点单号',
    countDate: l === 'ja' ? '棚卸日' : l === 'en' ? 'Count Date' : '盘点日期',
    material: l === 'ja' ? '品目' : l === 'en' ? 'Material' : '品目',
    bin: l === 'ja' ? '棚番' : l === 'en' ? 'Bin' : '棚番',
    batch: l === 'ja' ? 'ロット' : l === 'en' ? 'Batch' : '批次',
    uom: l === 'ja' ? '単位' : l === 'en' ? 'UoM' : '单位',
    systemQty: l === 'ja' ? 'システム数' : l === 'en' ? 'System Qty' : '系统数',
    actualQty: l === 'ja' ? '実数' : l === 'en' ? 'Actual Qty' : '实际数',
    varianceQty: l === 'ja' ? '差異数' : l === 'en' ? 'Variance' : '差异数',
    varianceType: l === 'ja' ? '種類' : l === 'en' ? 'Type' : '类型',
    varianceReason: l === 'ja' ? '差異理由' : l === 'en' ? 'Reason' : '差异原因',
    status: l === 'ja' ? 'ステータス' : l === 'en' ? 'Status' : '状态',
    statusDraft: l === 'ja' ? '下書き' : l === 'en' ? 'Draft' : '草稿',
    statusInProgress: l === 'ja' ? '進行中' : l === 'en' ? 'In Progress' : '进行中',
    statusCompleted: l === 'ja' ? '完了' : l === 'en' ? 'Completed' : '已完成',
    statusPosted: l === 'ja' ? '転記済み' : l === 'en' ? 'Posted' : '已过账',
    noData: l === 'ja' ? '差異データがありません' : l === 'en' ? 'No variance data' : '暂无差异数据'
  }
})

const loading = ref(false)
const reportData = ref<any[]>([])
const summary = ref<any>(null)
const filterDateRange = ref<string[]>([])
const filterWarehouse = ref('')
const filterType = ref('all')
const warehouseOptions = ref<any[]>([])

// 根据类型筛选数据
const filteredData = computed(() => {
  if (filterType.value === 'all') return reportData.value
  return reportData.value.filter(r => r.variance_type === filterType.value)
})

// 净差异
const netVariance = computed(() => {
  if (!summary.value) return 0
  return summary.value.totalGain - summary.value.totalLoss
})

const netVarianceStyle = computed(() => {
  const v = netVariance.value
  return { color: v > 0 ? '#67c23a' : v < 0 ? '#f56c6c' : '#606266' }
})

onMounted(() => {
  // 默认查询最近30天
  const today = new Date()
  const thirtyDaysAgo = new Date(today.getTime() - 30 * 24 * 60 * 60 * 1000)
  filterDateRange.value = [
    thirtyDaysAgo.toISOString().slice(0, 10),
    today.toISOString().slice(0, 10)
  ]
  loadWarehouses()
  loadReport()
})

async function loadWarehouses() {
  try {
    const r = await api.get('/inventory/warehouses')
    warehouseOptions.value = r.data || []
  } catch (e) {
    console.error('Failed to load warehouses:', e)
  }
}

async function loadReport() {
  loading.value = true
  try {
    const params = new URLSearchParams()
    if (filterDateRange.value && filterDateRange.value.length === 2) {
      params.append('fromDate', filterDateRange.value[0])
      params.append('toDate', filterDateRange.value[1])
    }
    if (filterWarehouse.value) params.append('warehouseCode', filterWarehouse.value)
    
    const r = await api.get(`/inventory/counts/report/variance?${params}`)
    reportData.value = r.data?.data || []
    summary.value = r.data?.summary || { totalGain: 0, totalLoss: 0, gainCount: 0, lossCount: 0, totalCount: 0 }
  } catch (e: any) {
    console.error('Failed to load report:', e)
    ElMessage.error('Failed to load report')
  } finally {
    loading.value = false
  }
}

function formatNumber(num: number | null | undefined) {
  if (num == null) return '-'
  return num.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 2 })
}

function formatVariance(variance: number) {
  const prefix = variance > 0 ? '+' : ''
  return prefix + formatNumber(variance)
}

function varianceClass(variance: number) {
  if (variance > 0) return 'text-success'
  if (variance < 0) return 'text-danger'
  return ''
}

function statusType(status: string) {
  switch (status) {
    case 'draft': return 'info'
    case 'in_progress': return 'warning'
    case 'completed': return 'success'
    case 'posted': return ''
    default: return 'info'
  }
}

function statusLabel(status: string) {
  switch (status) {
    case 'draft': return labels.value.statusDraft
    case 'in_progress': return labels.value.statusInProgress
    case 'completed': return labels.value.statusCompleted
    case 'posted': return labels.value.statusPosted
    default: return status
  }
}

function viewCount(countId: string) {
  router.push(`/inventory-counts?id=${countId}`)
}

function exportReport() {
  if (filteredData.value.length === 0) return

  // 生成 CSV
  const headers = [
    labels.value.countNo,
    labels.value.countDate,
    labels.value.warehouse,
    labels.value.material,
    'Material Code',
    labels.value.bin,
    labels.value.batch,
    labels.value.uom,
    labels.value.systemQty,
    labels.value.actualQty,
    labels.value.varianceQty,
    labels.value.varianceType,
    labels.value.varianceReason
  ]

  const rows = filteredData.value.map(r => [
    r.count_no,
    r.count_date,
    r.warehouse_name || r.warehouse_code,
    r.material_name || '',
    r.material_code,
    r.bin_code || '',
    r.batch_no || '',
    r.uom || '',
    r.system_qty,
    r.actual_qty,
    r.variance_qty,
    r.variance_type === 'gain' ? labels.value.gain : labels.value.loss,
    r.variance_reason || ''
  ])

  const csv = [headers, ...rows].map(row => row.map(cell => `"${cell}"`).join(',')).join('\n')
  const bom = '\uFEFF'
  const blob = new Blob([bom + csv], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `inventory_variance_report_${new Date().toISOString().slice(0, 10)}.csv`
  a.click()
  URL.revokeObjectURL(url)
}
</script>

<style scoped>
.filter-row {
  display: flex;
  gap: 12px;
  margin-bottom: 20px;
  flex-wrap: wrap;
  align-items: center;
}
.summary-row {
  padding: 20px;
  background: #f5f7fa;
  border-radius: 8px;
}
.stat-gain :deep(.el-statistic__number) {
  color: #67c23a;
}
.stat-loss :deep(.el-statistic__number) {
  color: #f56c6c;
}
.text-muted {
  color: #909399;
  font-size: 12px;
}
.text-success {
  color: #67c23a;
  font-weight: 600;
}
.text-danger {
  color: #f56c6c;
  font-weight: 600;
}
.no-data {
  text-align: center;
  color: #909399;
  padding: 40px;
}
</style>

