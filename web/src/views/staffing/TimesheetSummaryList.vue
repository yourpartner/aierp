<template>
  <div class="timesheet-summary-list">
    <el-card class="summary-card">
      <template #header>
        <div class="summary-header">
          <div class="summary-header__left">
            <el-icon class="summary-header__icon"><Clock /></el-icon>
            <span class="summary-header__title">勤怠集計</span>
            <el-tag size="small" type="info" class="summary-header__count">{{ total }}件</el-tag>
          </div>
          <div class="summary-header__right">
            <el-button type="primary" @click="openGenerate">
              <el-icon><Refresh /></el-icon>
              <span>月次生成</span>
            </el-button>
            <el-button type="success" @click="confirmAll" :disabled="!hasOpenItems">
              <el-icon><Check /></el-icon>
              <span>一括確定</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 検索フィルター -->
      <div class="summary-filters">
        <el-date-picker
          v-model="selectedMonth"
          type="month"
          placeholder="対象月"
          value-format="YYYY-MM"
          format="YYYY年MM月"
          @change="load"
        />

        <el-select v-model="statusFilter" clearable placeholder="ステータス" @change="load">
          <el-option label="未確定" value="open" />
          <el-option label="確定済" value="confirmed" />
          <el-option label="請求済" value="invoiced" />
        </el-select>

        <el-button type="primary" plain @click="load">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
      </div>

      <!-- 勤怠サマリーテーブル -->
      <el-table 
        :data="rows" 
        border 
        stripe
        highlight-current-row
        class="summary-table"
        v-loading="loading"
        @row-dblclick="onEdit"
      >
        <el-table-column type="index" width="50" align="center" />
        
        <el-table-column label="対象月" prop="yearMonth" width="100">
          <template #default="{ row }">
            <span class="year-month">{{ formatYearMonth(row.yearMonth) }}</span>
          </template>
        </el-table-column>

        <el-table-column label="リソース" min-width="140">
          <template #default="{ row }">
            <div v-if="row.resourceName">
              <div class="resource-name">{{ row.resourceName }}</div>
              <div class="resource-code">{{ row.resourceCode }}</div>
            </div>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="顧客" prop="clientName" width="120">
          <template #default="{ row }">
            {{ row.clientName || '-' }}
          </template>
        </el-table-column>

        <el-table-column label="契約番号" prop="contractNo" width="100">
          <template #default="{ row }">
            <span class="contract-no">{{ row.contractNo }}</span>
          </template>
        </el-table-column>

        <el-table-column label="所定時間" prop="scheduledHours" width="90" align="right">
          <template #default="{ row }">
            {{ row.scheduledHours || '-' }}h
          </template>
        </el-table-column>

        <el-table-column label="実績時間" prop="actualHours" width="90" align="right">
          <template #default="{ row }">
            <span :class="{ 'over-hours': row.actualHours > row.scheduledHours }">
              {{ row.actualHours || '-' }}h
            </span>
          </template>
        </el-table-column>

        <el-table-column label="残業" prop="overtimeHours" width="70" align="right">
          <template #default="{ row }">
            <span v-if="row.overtimeHours > 0" class="overtime">+{{ row.overtimeHours }}h</span>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="精算時間" prop="settlementHours" width="90" align="right">
          <template #default="{ row }">
            {{ row.settlementHours || '-' }}h
          </template>
        </el-table-column>

        <el-table-column label="請求金額" width="130" align="right">
          <template #default="{ row }">
            <span class="amount">¥{{ formatNumber(row.totalBillingAmount) }}</span>
          </template>
        </el-table-column>

        <el-table-column label="原価" width="110" align="right">
          <template #default="{ row }">
            <span v-if="row.totalCostAmount">¥{{ formatNumber(row.totalCostAmount) }}</span>
            <span v-else>-</span>
          </template>
        </el-table-column>

        <el-table-column label="ステータス" prop="status" width="90">
          <template #default="{ row }">
            <el-tag :type="getStatusTagType(row.status)" size="small">
              {{ getStatusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="操作" width="150" fixed="right" align="center">
          <template #default="{ row }">
            <el-button link type="primary" @click="onEdit(row)" v-if="row.status === 'open'">編集</el-button>
            <el-button link type="success" @click="confirmOne(row)" v-if="row.status === 'open'">確定</el-button>
            <el-button link type="info" @click="onEdit(row)" v-if="row.status !== 'open'">詳細</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 編集ダイアログ -->
    <el-dialog 
      v-model="dialogVisible" 
      :title="editMode ? '勤怠編集' : '勤怠詳細'"
      width="700px"
      destroy-on-close
    >
      <el-form :model="form" label-width="130px" label-position="right">
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="対象月">
              <el-input v-model="form.yearMonth" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="契約番号">
              <el-input v-model="form.contractNo" disabled />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="リソース">
              <el-input v-model="form.resourceName" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="顧客">
              <el-input v-model="form.clientName" disabled />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>時間入力</el-divider>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="所定時間">
              <el-input-number v-model="form.scheduledHours" :min="0" :max="300" :precision="1" :disabled="!editMode" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="実績時間">
              <el-input-number v-model="form.actualHours" :min="0" :max="400" :precision="1" :disabled="!editMode" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="残業時間">
              <el-input-number v-model="form.overtimeHours" :min="0" :max="100" :precision="1" :disabled="!editMode" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>精算計算</el-divider>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="精算方式">
              <el-input v-model="form.settlementType" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="精算下限">
              <el-input :model-value="form.settlementLowerHours ? `${form.settlementLowerHours}h` : '-'" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="精算上限">
              <el-input :model-value="form.settlementUpperHours ? `${form.settlementUpperHours}h` : '-'" disabled />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="控除時間">
              <el-input :model-value="`${form.deductionHours || 0}h`" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="超過時間">
              <el-input :model-value="`${form.excessHours || 0}h`" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="精算時間">
              <el-input :model-value="`${form.settlementHours || 0}h`" disabled />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider>金額</el-divider>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="調整金額">
              <el-input-number v-model="form.adjustmentAmount" :step="1000" :disabled="!editMode" controls-position="right" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="基本金額">
              <el-input :model-value="`¥${formatNumber(form.baseAmount || 0)}`" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="残業金額">
              <el-input :model-value="`¥${formatNumber(form.overtimeAmount || 0)}`" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="請求総額">
              <el-input :model-value="`¥${formatNumber(form.totalBillingAmount || 0)}`" disabled class="total-amount" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="備考">
          <el-input v-model="form.notes" type="textarea" :rows="2" :disabled="!editMode" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="dialogVisible = false">閉じる</el-button>
        <el-button type="primary" @click="save" :loading="saving" v-if="editMode">保存・再計算</el-button>
      </template>
    </el-dialog>

    <!-- 月次生成ダイアログ -->
    <el-dialog v-model="generateDialogVisible" title="月次勤怠生成" width="400px">
      <el-form label-width="100px">
        <el-form-item label="対象月">
          <el-date-picker
            v-model="generateMonth"
            type="month"
            placeholder="対象月を選択"
            value-format="YYYY-MM"
            format="YYYY年MM月"
            style="width: 100%"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="generateDialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="generate" :loading="generating">生成</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, computed } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Clock, Refresh, Check, Search } from '@element-plus/icons-vue'
import api from '../../api'

interface SummaryRow {
  id: string
  contractId: string
  resourceId?: string
  yearMonth: string
  scheduledHours?: number
  actualHours?: number
  overtimeHours: number
  billableHours?: number
  settlementHours?: number
  deductionHours: number
  excessHours: number
  baseAmount?: number
  overtimeAmount: number
  adjustmentAmount: number
  totalBillingAmount?: number
  totalCostAmount?: number
  status: string
  confirmedAt?: string
  contractNo: string
  billingRate: number
  billingRateType: string
  settlementType: string
  settlementLowerHours?: number
  settlementUpperHours?: number
  resourceCode?: string
  resourceName?: string
  clientName?: string
}

const loading = ref(false)
const rows = ref<SummaryRow[]>([])
const total = ref(0)
const selectedMonth = ref(new Date().toISOString().substring(0, 7))
const statusFilter = ref('')

const dialogVisible = ref(false)
const editMode = ref(false)
const saving = ref(false)

const generateDialogVisible = ref(false)
const generateMonth = ref(new Date().toISOString().substring(0, 7))
const generating = ref(false)

const form = reactive({
  id: '',
  yearMonth: '',
  contractNo: '',
  resourceName: '',
  clientName: '',
  scheduledHours: null as number | null,
  actualHours: null as number | null,
  overtimeHours: 0,
  settlementType: '',
  settlementLowerHours: null as number | null,
  settlementUpperHours: null as number | null,
  deductionHours: 0,
  excessHours: 0,
  settlementHours: null as number | null,
  baseAmount: null as number | null,
  overtimeAmount: 0,
  adjustmentAmount: 0,
  totalBillingAmount: null as number | null,
  notes: ''
})

const hasOpenItems = computed(() => rows.value.some(r => r.status === 'open'))

const load = async () => {
  loading.value = true
  try {
    const params: Record<string, string> = {}
    if (selectedMonth.value) params.yearMonth = selectedMonth.value
    if (statusFilter.value) params.status = statusFilter.value
    
    const res = await api.get('/staffing/timesheets', { params })
    rows.value = res.data.data || []
    total.value = res.data.total || rows.value.length
  } catch (e: any) {
    ElMessage.error(e.message || '読み込み失敗')
  } finally {
    loading.value = false
  }
}

const onEdit = (row: SummaryRow) => {
  editMode.value = row.status === 'open'
  Object.assign(form, {
    id: row.id,
    yearMonth: row.yearMonth,
    contractNo: row.contractNo,
    resourceName: row.resourceName || '',
    clientName: row.clientName || '',
    scheduledHours: row.scheduledHours,
    actualHours: row.actualHours,
    overtimeHours: row.overtimeHours || 0,
    settlementType: row.settlementType === 'range' ? '幅精算' : row.settlementType === 'fixed' ? '固定' : '実精算',
    settlementLowerHours: row.settlementLowerHours,
    settlementUpperHours: row.settlementUpperHours,
    deductionHours: row.deductionHours || 0,
    excessHours: row.excessHours || 0,
    settlementHours: row.settlementHours,
    baseAmount: row.baseAmount,
    overtimeAmount: row.overtimeAmount || 0,
    adjustmentAmount: row.adjustmentAmount || 0,
    totalBillingAmount: row.totalBillingAmount,
    notes: ''
  })
  dialogVisible.value = true
}

const save = async () => {
  saving.value = true
  try {
    const payload = {
      scheduledHours: form.scheduledHours,
      actualHours: form.actualHours,
      overtimeHours: form.overtimeHours,
      adjustmentAmount: form.adjustmentAmount,
      notes: form.notes || null
    }
    
    const res = await api.put(`/staffing/timesheets/${form.id}`, payload)
    ElMessage.success(`更新しました（請求額: ¥${formatNumber(res.data.totalBillingAmount)}）`)
    dialogVisible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message || '保存失敗')
  } finally {
    saving.value = false
  }
}

const confirmOne = async (row: SummaryRow) => {
  try {
    await ElMessageBox.confirm('この勤怠を確定しますか？確定後は編集できません。', '確認', { type: 'warning' })
    await api.post(`/staffing/timesheets/${row.id}/confirm`)
    ElMessage.success('確定しました')
    load()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e.response?.data?.error || '確定失敗')
    }
  }
}

const confirmAll = async () => {
  if (!selectedMonth.value) {
    ElMessage.warning('対象月を選択してください')
    return
  }
  try {
    await ElMessageBox.confirm(`${formatYearMonth(selectedMonth.value)}の全ての勤怠を一括確定しますか？`, '確認', { type: 'warning' })
    const res = await api.post('/staffing/timesheets/confirm-all', { yearMonth: selectedMonth.value })
    ElMessage.success(`${res.data.confirmed}件を確定しました`)
    load()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e.response?.data?.error || '一括確定失敗')
    }
  }
}

const openGenerate = () => {
  generateMonth.value = selectedMonth.value || new Date().toISOString().substring(0, 7)
  generateDialogVisible.value = true
}

const generate = async () => {
  if (!generateMonth.value) {
    ElMessage.warning('対象月を選択してください')
    return
  }
  generating.value = true
  try {
    const res = await api.post('/staffing/timesheets/generate', { yearMonth: generateMonth.value })
    ElMessage.success(`${res.data.generated}件を生成しました（${res.data.skipped}件はスキップ）`)
    generateDialogVisible.value = false
    selectedMonth.value = generateMonth.value
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '生成失敗')
  } finally {
    generating.value = false
  }
}

const getStatusLabel = (status: string) => {
  const map: Record<string, string> = { open: '未確定', confirmed: '確定済', invoiced: '請求済' }
  return map[status] || status
}

const getStatusTagType = (status: string) => {
  const map: Record<string, string> = { open: 'warning', confirmed: 'success', invoiced: 'primary' }
  return map[status] || 'info'
}

const formatYearMonth = (ym: string) => {
  if (!ym) return ''
  const [y, m] = ym.split('-')
  return `${y}年${m}月`
}

const formatNumber = (num: number) => {
  return new Intl.NumberFormat('ja-JP').format(num || 0)
}

onMounted(() => {
  load()
})
</script>

<style scoped>
.timesheet-summary-list {
  padding: 20px;
}

.summary-card {
  border-radius: 8px;
}

.summary-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.summary-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.summary-header__icon {
  font-size: 24px;
  color: var(--el-color-primary);
}

.summary-header__title {
  font-size: 18px;
  font-weight: 600;
}

.summary-header__right {
  display: flex;
  gap: 10px;
}

.summary-filters {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.year-month {
  font-weight: 500;
}

.contract-no {
  font-family: monospace;
  color: var(--el-color-primary);
}

.resource-name {
  font-weight: 500;
}

.resource-code {
  font-size: 12px;
  color: #999;
}

.over-hours {
  color: var(--el-color-warning);
  font-weight: 600;
}

.overtime {
  color: var(--el-color-danger);
  font-weight: 600;
}

.amount {
  font-weight: 600;
}

.total-amount :deep(.el-input__inner) {
  font-weight: 700;
  color: var(--el-color-primary);
}

.el-divider {
  margin: 16px 0;
}
</style>

