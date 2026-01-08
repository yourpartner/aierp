<template>
  <div class="payroll-history">
    <el-card class="payroll-card">
      <template #header>
        <div class="payroll-header">
          <div class="payroll-header__left">
            <el-icon class="payroll-header__icon"><Timer /></el-icon>
            <span class="payroll-header__title">給与計算履歴</span>
            <el-tag size="small" type="info">{{ pagination.total }}件</el-tag>
          </div>
        </div>
      </template>
      
      <!-- 検索条件 -->
      <div class="payroll-filters">
        <el-date-picker
          v-model="filters.month"
          type="month"
          value-format="YYYY-MM"
          clearable
          placeholder="月份"
          class="payroll-filters__month"
        />
        
        <el-select
          v-model="filters.employeeId"
          filterable
          remote
          clearable
          reserve-keyword
          :remote-method="searchEmployees"
          placeholder="社員を検索"
          class="payroll-filters__employee"
        >
          <el-option
            v-for="item in employeeOptions"
            :key="item.value"
            :label="item.label"
            :value="item.value"
          />
        </el-select>
        
        <el-select v-model="filters.runType" placeholder="モード" class="payroll-filters__type">
          <el-option label="すべて" value="all" />
          <el-option label="手動" value="manual" />
          <el-option label="自動" value="auto" />
        </el-select>
        
        <el-input v-model="filters.keyword" placeholder="キーワード" clearable class="payroll-filters__keyword">
          <template #prefix>
            <el-icon><Search /></el-icon>
          </template>
        </el-input>
        
        <el-button type="primary" :loading="loading" @click="handleSearch">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
        <el-button @click="handleReset">リセット</el-button>
      </div>
      
      <!-- データテーブル -->
      <el-table
        v-loading="loading"
        :data="entries"
        border
        stripe
        class="payroll-table"
      >
        <el-table-column prop="periodMonth" label="月份" width="100" />
        <el-table-column label="モード" width="80" align="center">
          <template #default="{ row }">
            <el-tag size="small" :type="row.runType === 'auto' ? 'success' : 'info'">
              {{ formatRunType(row.runType) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="社員" min-width="160">
          <template #default="{ row }">
            <div class="emp-name-cell">
              <div class="emp-name-main">{{ row.employeeName || '未設定' }}</div>
              <div class="emp-name-sub">{{ row.employeeCode }}</div>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="部門" min-width="140">
          <template #default="{ row }">
            {{ formatDepartment(row) }}
          </template>
        </el-table-column>
        <el-table-column label="金額" width="120" align="right">
          <template #default="{ row }">
            <span class="amount-cell">¥{{ formatAmount(row.totalAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="差異" min-width="140">
          <template #default="{ row }">
            <span v-if="formatDiff(row.diffSummary)">{{ formatDiff(row.diffSummary) }}</span>
            <span v-else class="emp-empty">—</span>
          </template>
        </el-table-column>
        <el-table-column label="伝票番号" width="120">
          <template #default="{ row }">
            <span v-if="row.voucherNo">{{ row.voucherNo }}</span>
            <span v-else class="emp-empty">未作成</span>
          </template>
        </el-table-column>
        <el-table-column label="保存日時" width="150">
          <template #default="{ row }">
            {{ formatDateTime(row.createdAt) }}
          </template>
        </el-table-column>
        <el-table-column label="" width="80" align="center" fixed="right">
          <template #default="{ row }">
            <el-button size="small" type="primary" text @click="openEntryDetail(row)">
              <el-icon><View /></el-icon>
            </el-button>
          </template>
        </el-table-column>
      </el-table>
      
      <!-- ページネーション -->
      <div class="payroll-pagination">
        <span class="payroll-pagination__info">
          {{ (pagination.page - 1) * pagination.pageSize + 1 }} - {{ Math.min(pagination.page * pagination.pageSize, pagination.total) }} / {{ pagination.total }}件
        </span>
        <el-pagination
          background
          layout="prev, pager, next"
          :current-page="pagination.page"
          :page-size="pagination.pageSize"
          :total="pagination.total"
          @current-change="handlePageChange"
        />
      </div>
    </el-card>

    <!-- 詳細ドロワー -->
    <el-drawer v-model="detailVisible" size="65%" :title="detailTitle">
      <div v-if="detailVisible" v-loading="detailLoading" class="detail-content">
        <div v-if="detailData">
          <!-- ヘッダー -->
          <div class="detail-header">
            <div>
              <div class="detail-name">{{ detailData.employeeName }} <span class="detail-code">({{ detailData.employeeCode }})</span></div>
              <div class="detail-meta">
                {{ detailData.periodMonth }} ／ {{ formatRunType(detailData.runType) }} ／ {{ formatDepartment(detailData) }}
                <template v-if="detailData?.voucherNo"> ／ 伝票: {{ detailData.voucherNo }}</template>
              </div>
            </div>
            <div class="detail-total">¥{{ formatAmount(detailData.totalAmount) }}</div>
          </div>
          
          <el-alert
            v-if="detailData?.diffSummary"
            type="info"
            show-icon
            :title="formatDiff(detailData.diffSummary)"
            style="margin-bottom:16px"
          />
          
          <!-- 勤怠サマリー -->
          <div v-if="detailData?.workHours" class="section-card" style="margin-bottom:16px">
            <div class="section-card__header">勤怠サマリー</div>
            <div class="section-card__body">
              <el-descriptions :column="4" size="small" border>
                <el-descriptions-item
                  v-for="item in workHourItems"
                  :key="item.key"
                  :label="item.label"
                >
                  {{ formatWorkHour(detailData?.workHours?.[item.key]) }}
                </el-descriptions-item>
              </el-descriptions>
            </div>
          </div>
          
          <!-- 給与項目・仕訳 -->
          <el-row :gutter="16">
            <el-col :span="12">
              <div class="section-card">
                <div class="section-card__header">給与項目</div>
                <div class="section-card__body">
                  <el-table :data="detailData?.payrollSheet || []" size="small" border>
                    <el-table-column label="項目" min-width="120">
                      <template #default="{ row }">
                        {{ row.displayName || row.itemName || row.itemCode }}
                      </template>
                    </el-table-column>
                    <el-table-column label="金額" width="100" align="right">
                      <template #default="{ row }">
                        {{ row.displayAmount || formatAmount(row.amount) }}
                      </template>
                    </el-table-column>
                  </el-table>
                </div>
              </div>
            </el-col>
            <el-col :span="12">
              <div class="section-card">
                <div class="section-card__header">会計仕訳</div>
                <div class="section-card__body">
                  <el-table :data="detailData?.accountingDraft || []" size="small" border>
                    <el-table-column prop="accountCode" label="科目コード" width="90" />
                    <el-table-column prop="accountName" label="科目名" min-width="100" />
                    <el-table-column prop="drcr" label="借/貸" width="60" align="center" />
                    <el-table-column label="金額" width="90" align="right">
                      <template #default="{ row }">
                        {{ row.displayAmount || formatAmount(row.amount) }}
                      </template>
                    </el-table-column>
                  </el-table>
                </div>
              </div>
            </el-col>
          </el-row>
          
          <!-- 計算トレース -->
          <div v-if="detailData?.trace && detailData.trace.length > 0" class="section-card" style="margin-top:16px">
            <div class="section-card__header">
              <span>計算トレース</span>
              <el-switch v-model="showTraceRaw" active-text="JSON" inactive-text="テーブル" size="small" style="margin-left:auto" />
            </div>
            <div class="section-card__body">
              <div v-if="!showTraceRaw">
                <el-table :data="detailData.trace" size="small" border max-height="300">
                  <el-table-column prop="step" label="ステップ" width="140" />
                  <el-table-column prop="source" label="ソース" width="80" />
                  <el-table-column prop="item" label="項目" width="100" />
                  <el-table-column label="金額" width="90" align="right">
                    <template #default="{ row }">
                      {{ row.amount !== undefined ? formatAmount(row.amount) : '' }}
                    </template>
                  </el-table-column>
                  <el-table-column prop="lawVersion" label="法令Ver" width="150" />
                  <el-table-column prop="note" label="備考" min-width="100" />
                </el-table>
              </div>
              <div v-else>
                <pre class="trace-json">{{ JSON.stringify(detailData.trace, null, 2) }}</pre>
              </div>
            </div>
          </div>
        </div>
      </div>
    </el-drawer>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, computed } from 'vue'
import { ElMessage } from 'element-plus'
import { Timer, Search, View } from '@element-plus/icons-vue'
import api from '../api'

const defaultMonth = new Date().toISOString().slice(0, 7)

const filters = reactive({
  month: defaultMonth,
  employeeId: '',
  runType: 'all',
  keyword: ''
})

const pagination = reactive({
  page: 1,
  pageSize: 20,
  total: 0
})
const workHourItems = [
  { key: 'totalHours', label: '総勤務時間' },
  { key: 'regularHours', label: '所定内' },
  { key: 'overtimeHours', label: '時間外' },
  { key: 'holidayHours', label: '休日労働' },
  { key: 'lateNightHours', label: '深夜労働' },
  { key: 'absenceHours', label: '不足時間' }
]

const entries = ref<any[]>([])
const loading = ref(false)
const employeeOptions = ref<any[]>([])
const detailVisible = ref(false)
const detailLoading = ref(false)
const detailData = ref<any | null>(null)
const showTraceRaw = ref(false)

const detailTitle = computed(() => {
  if (!detailData.value) return '給与明細'
  return `${detailData.value.employeeName || detailData.value.employeeCode || ''} の給与明細`
})

function formatAmount(val: number) {
  return Number(val || 0).toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 })
}

function formatRunType(type?: string) {
  if (!type) return '不明'
  if (type.toLowerCase() === 'auto') return '自動'
  if (type.toLowerCase() === 'manual') return '手動'
  return type
}

function formatDateTime(value: string) {
  if (!value) return ''
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return value
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  const hh = String(d.getHours()).padStart(2, '0')
  const mm = String(d.getMinutes()).padStart(2, '0')
  return `${y}-${m}-${day} ${hh}:${mm}`
}

function formatDepartment(row: any) {
  if (!row) return '未設定'
  const name = row.departmentName
  const code = row.departmentCode
  if (name && code) return `${name} (${code})`
  return name || code || '未設定'
}

function formatWorkHour(value?: number | null) {
  if (typeof value !== 'number' || Number.isNaN(value)) return '0h'
  return `${Number(value).toFixed(2)}h`
}

function formatDiff(diff: any) {
  if (!diff) return ''
  const difference = diff.difference ?? diff.Difference ?? 0
  const percent = diff.differencePercent ?? diff.DifferencePercent ?? null
  const text = difference ? `${difference >= 0 ? '+' : ''}${formatAmount(difference)}` : ''
  const pctText = typeof percent === 'number' ? ` (${(percent * 100).toFixed(1)}%)` : ''
  return text ? `${text}${pctText}` : ''
}

async function handleSearch() {
  pagination.page = 1
  await loadEntries()
}

function handleReset() {
  filters.month = defaultMonth
  filters.employeeId = ''
  filters.runType = 'all'
  filters.keyword = ''
  handleSearch()
}

function handlePageChange(page: number) {
  pagination.page = page
  loadEntries()
}

async function loadEntries() {
  loading.value = true
  try {
    const params: Record<string, any> = {
      page: pagination.page,
      pageSize: pagination.pageSize
    }
    if (filters.month) params.month = filters.month
    if (filters.employeeId) params.employeeId = filters.employeeId
    if (filters.runType && filters.runType !== 'all') params.runType = filters.runType
    if (filters.keyword?.trim()) params.keyword = filters.keyword.trim()
    const resp = await api.get('/payroll/run-entries', { params })
    const data = resp.data || {}
    pagination.total = data.total || 0
    entries.value = Array.isArray(data.items)
      ? data.items.map((item: any) => ({
          ...item,
          departmentName: item.departmentName || '',
          createdAt: item.createdAt,
          voucherNo: item.voucherNo || '',
          voucherId: item.voucherId || ''
        }))
      : []
    ensureEmployeeOption(filters.employeeId)
  } catch (err: any) {
    console.error(err)
    ElMessage.error('給与履歴の取得に失敗しました')
  } finally {
    loading.value = false
  }
}

function ensureEmployeeOption(id?: string) {
  if (!id) return
  if (employeeOptions.value.some((opt: any) => opt.value === id)) return
  const target = entries.value.find(item => item.employeeId === id)
  if (!target) return
  employeeOptions.value.push({
    value: target.employeeId,
    label: `${target.employeeName || ''} (${target.employeeCode || ''})`
  })
}

async function searchEmployees(query: string) {
  const q = (query || '').trim()
  const where: any[] = []
  if (q) {
    where.push({ json: 'nameKanji', op: 'contains', value: q })
    where.push({ json: 'nameKana', op: 'contains', value: q })
    where.push({ field: 'employee_code', op: 'contains', value: q })
  }
  const resp = await api.post('/objects/employee/search', {
    page: 1,
    pageSize: 20,
    where,
    orderBy: [{ field: 'created_at', dir: 'DESC' }]
  })
  const list = (resp.data?.data || []) as any[]
  employeeOptions.value = list.map(emp => ({
    value: emp.id,
    label: `${emp.payload?.nameKanji || emp.payload?.name || emp.name || ''} (${emp.employee_code || emp.payload?.code || ''})`
  }))
}

async function openEntryDetail(row: any) {
  detailVisible.value = true
  detailLoading.value = true
  showTraceRaw.value = false
  try {
    const resp = await api.get(`/payroll/runs/${row.runId}/entries/${row.entryId}`)
    const data = resp.data || {}
    detailData.value = {
      runType: data.runType || row.runType,
      periodMonth: data.periodMonth || row.periodMonth,
      employeeName: data.employeeName || row.employeeName,
      employeeCode: data.employeeCode || row.employeeCode,
      departmentCode: data.departmentCode || row.departmentCode,
      departmentName: data.departmentName || row.departmentName,
      totalAmount: data.totalAmount || row.totalAmount,
      diffSummary: data.diffSummary || row.diffSummary,
      payrollSheet: (data.payrollSheet || []).map((entry: any) => ({
        ...entry,
        displayName: entry.itemName || entry.itemCode,
        displayAmount: formatAmount(entry.amount)
      })),
      accountingDraft: (data.accountingDraft || []).map((entry: any) => ({
        ...entry,
        displayAmount: formatAmount(entry.amount)
      })),
      trace: data.trace || null,
      workHours: data.metadata?.workHours || data.metadata?.workhours || null,
      voucherNo: data.voucherNo || row.voucherNo || '',
      voucherId: data.voucherId || row.voucherId || ''
    }
  } catch (err) {
    console.error(err)
    ElMessage.error('明細の取得に失敗しました')
    detailVisible.value = false
  } finally {
    detailLoading.value = false
  }
}

onMounted(() => {
  loadEntries()
})
</script>

<style scoped>
.payroll-history {
  padding: 16px;
}

.payroll-card {
  border-radius: 12px;
  overflow: hidden;
}

.payroll-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.payroll-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.payroll-header__icon {
  font-size: 22px;
  color: #667eea;
}

.payroll-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

/* フィルター */
.payroll-filters {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
  margin-bottom: 16px;
  padding: 16px;
  background: #f8f9fc;
  border-radius: 8px;
}

.payroll-filters__month {
  width: 130px;
}

.payroll-filters__employee {
  width: 200px;
}

.payroll-filters__type {
  width: 100px;
}

.payroll-filters__keyword {
  width: 160px;
}

/* テーブル */
.payroll-table {
  border-radius: 8px;
  overflow: hidden;
}

.payroll-table :deep(.el-table__header th) {
  background: #f5f7fa;
  font-weight: 600;
  color: #303133;
}

.emp-name-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.emp-name-main {
  font-weight: 500;
  color: #303133;
}

.emp-name-sub {
  font-size: 12px;
  color: #909399;
}

.amount-cell {
  font-weight: 600;
}

.emp-empty {
  color: #c0c4cc;
}

/* ページネーション */
.payroll-pagination {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.payroll-pagination__info {
  font-size: 13px;
  color: #909399;
}

/* 詳細 */
.detail-content {
  padding: 0 8px;
}

.detail-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 16px;
  padding-bottom: 16px;
  border-bottom: 1px solid #ebeef5;
}

.detail-name {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.detail-code {
  font-weight: 400;
  color: #909399;
}

.detail-meta {
  margin-top: 6px;
  font-size: 13px;
  color: #606266;
}

.detail-total {
  font-size: 22px;
  font-weight: 700;
  color: #67c23a;
}

.section-card {
  background: #fff;
  border: 1px solid #ebeef5;
  border-radius: 6px;
  overflow: hidden;
}

.section-card__header {
  display: flex;
  align-items: center;
  padding: 10px 12px;
  background: #f5f7fa;
  font-size: 13px;
  font-weight: 600;
  color: #606266;
  border-bottom: 1px solid #ebeef5;
}

.section-card__body {
  padding: 12px;
}

.trace-json {
  max-height: 300px;
  overflow: auto;
  background: #f5f7fa;
  padding: 12px;
  font-family: monospace;
  font-size: 12px;
  line-height: 1.5;
  border-radius: 4px;
}
</style>
