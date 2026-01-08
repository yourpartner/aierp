<template>
  <div class="portal-invoices">
    <div class="page-header">
      <div class="header-left">
        <router-link to="/portal/dashboard" class="back-link">
          <el-icon><ArrowLeft /></el-icon>
        </router-link>
        <el-icon class="header-icon"><List /></el-icon>
        <h1>請求書</h1>
      </div>
      <el-button type="primary" @click="showCreateDialog">
        <el-icon><Plus /></el-icon>
        請求書作成
      </el-button>
    </div>

    <el-card v-loading="loading">
      <el-table :data="invoices">
        <el-table-column label="請求書番号" prop="invoiceNo" width="160" />
        <el-table-column label="請求期間" min-width="180">
          <template #default="{ row }">
            {{ formatDate(row.periodStart) }} ~ {{ formatDate(row.periodEnd) }}
          </template>
        </el-table-column>
        <el-table-column label="税抜金額" prop="subtotal" width="120" align="right">
          <template #default="{ row }">
            ¥{{ formatNumber(row.subtotal) }}
          </template>
        </el-table-column>
        <el-table-column label="消費税" prop="taxAmount" width="100" align="right">
          <template #default="{ row }">
            ¥{{ formatNumber(row.taxAmount) }}
          </template>
        </el-table-column>
        <el-table-column label="合計" prop="totalAmount" width="130" align="right">
          <template #default="{ row }">
            <span class="total-amount">¥{{ formatNumber(row.totalAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="ステータス" width="100" align="center">
          <template #default="{ row }">
            <el-tag :type="getStatusType(row.status)" size="small">
              {{ getStatusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="提出日" prop="submittedAt" width="110">
          <template #default="{ row }">
            {{ row.submittedAt ? formatDate(row.submittedAt) : '-' }}
          </template>
        </el-table-column>
      </el-table>
      <el-empty v-if="!loading && invoices.length === 0" description="請求書がありません" />
    </el-card>

    <!-- 請求書作成ダイアログ -->
    <el-dialog v-model="dialogVisible" title="請求書作成" width="550px">
      <el-form :model="form" label-position="top">
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="請求期間（開始）" required>
              <el-date-picker 
                v-model="form.periodStart" 
                type="date" 
                placeholder="開始日"
                value-format="YYYY-MM-DD"
                style="width: 100%"
              />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="請求期間（終了）" required>
              <el-date-picker 
                v-model="form.periodEnd" 
                type="date" 
                placeholder="終了日"
                value-format="YYYY-MM-DD"
                style="width: 100%"
              />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item label="勤怠データ連携">
          <el-select v-model="form.timesheetSummaryId" placeholder="関連する勤怠データを選択" style="width: 100%" clearable>
            <el-option 
              v-for="ts in availableTimesheets" 
              :key="ts.id" 
              :label="`${ts.yearMonth} - ${ts.actualHours}h`" 
              :value="ts.id" 
            />
          </el-select>
        </el-form-item>

        <el-form-item label="税抜金額" required>
          <el-input-number 
            v-model="form.subtotal" 
            :min="0" 
            :step="1000"
            style="width: 100%"
            @change="calculateTotal"
          />
        </el-form-item>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item label="消費税率">
              <el-select v-model="form.taxRate" style="width: 100%" @change="calculateTotal">
                <el-option label="10%" :value="0.10" />
                <el-option label="8%（軽減税率）" :value="0.08" />
                <el-option label="0%（非課税）" :value="0" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="消費税額">
              <el-input :value="`¥${formatNumber(taxAmount)}`" disabled />
            </el-form-item>
          </el-col>
        </el-row>

        <div class="total-display">
          <span class="total-label">請求額合計</span>
          <span class="total-value">¥{{ formatNumber(totalAmount) }}</span>
        </div>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="submitInvoice" :loading="submitting">提出</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { ArrowLeft, List, Plus } from '@element-plus/icons-vue'
import api from '../../api'

interface Invoice {
  id: string
  invoiceNo: string
  periodStart: string
  periodEnd: string
  subtotal: number
  taxAmount: number
  totalAmount: number
  status: string
  submittedAt?: string
}

const loading = ref(false)
const submitting = ref(false)
const dialogVisible = ref(false)

const invoices = ref<Invoice[]>([])
const availableTimesheets = ref<any[]>([])

const form = reactive({
  periodStart: '',
  periodEnd: '',
  subtotal: 0,
  taxRate: 0.10,
  timesheetSummaryId: ''
})

const taxAmount = computed(() => Math.floor(form.subtotal * form.taxRate))
const totalAmount = computed(() => form.subtotal + taxAmount.value)

const loadInvoices = async () => {
  loading.value = true
  try {
    const res = await api.get('/portal/invoices')
    invoices.value = res.data.data || []
  } catch (e) {
    console.error('Load invoices error:', e)
  } finally {
    loading.value = false
  }
}

const loadTimesheets = async () => {
  try {
    const res = await api.get('/portal/timesheets', { params: { year: new Date().getFullYear() } })
    availableTimesheets.value = (res.data.data || []).filter((ts: any) => ts.status === 'confirmed')
  } catch (e) {
    console.error('Load timesheets error:', e)
  }
}

const showCreateDialog = () => {
  // 默认为上月
  const lastMonth = new Date()
  lastMonth.setMonth(lastMonth.getMonth() - 1)
  const year = lastMonth.getFullYear()
  const month = lastMonth.getMonth()
  
  Object.assign(form, {
    periodStart: `${year}-${String(month + 1).padStart(2, '0')}-01`,
    periodEnd: `${year}-${String(month + 1).padStart(2, '0')}-${new Date(year, month + 1, 0).getDate()}`,
    subtotal: 0,
    taxRate: 0.10,
    timesheetSummaryId: ''
  })
  dialogVisible.value = true
}

const calculateTotal = () => {
  // computed会自动计算
}

const submitInvoice = async () => {
  if (!form.periodStart || !form.periodEnd || form.subtotal <= 0) {
    ElMessage.warning('必須項目を入力してください')
    return
  }
  submitting.value = true
  try {
    await api.post('/portal/invoices', {
      ...form,
      taxAmount: taxAmount.value,
      totalAmount: totalAmount.value
    })
    ElMessage.success('請求書を提出しました')
    dialogVisible.value = false
    await loadInvoices()
  } catch (e: any) {
    ElMessage.error('提出に失敗しました')
  } finally {
    submitting.value = false
  }
}

const formatDate = (dateStr: string) => {
  if (!dateStr) return '-'
  return new Date(dateStr).toLocaleDateString('ja-JP')
}

const formatNumber = (num: number) => {
  return new Intl.NumberFormat('ja-JP').format(num)
}

const getStatusLabel = (status: string) => {
  const map: Record<string, string> = {
    draft: '下書き',
    submitted: '審査中',
    approved: '承認済',
    rejected: '差戻し',
    paid: '支払済'
  }
  return map[status] || status
}

const getStatusType = (status: string) => {
  const map: Record<string, string> = {
    draft: 'info',
    submitted: 'warning',
    approved: 'primary',
    rejected: 'danger',
    paid: 'success'
  }
  return map[status] || 'info'
}

onMounted(() => {
  loadInvoices()
  loadTimesheets()
})
</script>

<style scoped>
.portal-invoices {
  padding: 24px;
  background: #f5f7fa;
  min-height: 100vh;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 12px;
}

.back-link {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border-radius: 8px;
  background: white;
  color: #606266;
  text-decoration: none;
}

.back-link:hover {
  background: #ecf5ff;
  color: var(--el-color-primary);
}

.header-icon {
  font-size: 24px;
  color: var(--el-color-primary);
}

.header-left h1 {
  margin: 0;
  font-size: 20px;
}

.total-amount {
  font-weight: 600;
  color: var(--el-color-primary);
}

.total-display {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 20px;
  background: linear-gradient(135deg, #667eea10 0%, #764ba210 100%);
  border: 1px solid #667eea30;
  border-radius: 8px;
  margin-top: 16px;
}

.total-label {
  font-size: 14px;
  color: #606266;
}

.total-value {
  font-size: 28px;
  font-weight: 700;
  color: var(--el-color-primary);
}
</style>

