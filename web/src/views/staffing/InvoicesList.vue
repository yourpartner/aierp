<template>
  <div class="invoices-list">
    <el-card class="invoices-card">
      <template #header>
        <div class="invoices-header">
          <div class="invoices-header__left">
            <el-icon class="invoices-header__icon"><Tickets /></el-icon>
            <span class="invoices-header__title">請求書一覧</span>
            <el-tag size="small" type="info" class="invoices-header__count">{{ total }}件</el-tag>
          </div>
          <div class="invoices-header__right">
            <el-button type="primary" @click="openGenerate">
              <el-icon><Refresh /></el-icon>
              <span>請求書生成</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 検索フィルター -->
      <div class="invoices-filters">
        <el-date-picker
          v-model="selectedMonth"
          type="month"
          placeholder="請求月"
          value-format="YYYY-MM"
          format="YYYY年MM月"
          @change="load"
        />

        <el-select v-model="statusFilter" clearable placeholder="ステータス" @change="load">
          <el-option label="下書き" value="draft" />
          <el-option label="確定" value="confirmed" />
          <el-option label="発行済" value="issued" />
          <el-option label="送付済" value="sent" />
          <el-option label="入金済" value="paid" />
          <el-option label="一部入金" value="partial_paid" />
        </el-select>

        <el-select 
          v-model="clientFilter" 
          filterable 
          remote
          clearable 
          :remote-method="searchClients"
          placeholder="顧客で絞込"
        >
          <el-option v-for="opt in clientOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
        </el-select>

        <el-button type="primary" plain @click="load">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
      </div>

      <!-- 請求書テーブル -->
      <el-table 
        :data="rows" 
        border 
        stripe
        highlight-current-row
        class="invoices-table"
        v-loading="loading"
        @row-dblclick="onView"
      >
        <el-table-column type="index" width="50" align="center" />
        
        <el-table-column label="請求書番号" prop="invoiceNo" width="140">
          <template #default="{ row }">
            <span class="invoice-no">{{ row.invoiceNo }}</span>
          </template>
        </el-table-column>

        <el-table-column label="顧客" min-width="150">
          <template #default="{ row }">
            <div>
              <div class="client-name">{{ row.clientName || '-' }}</div>
              <div class="client-code" v-if="row.clientCode">{{ row.clientCode }}</div>
            </div>
          </template>
        </el-table-column>

        <el-table-column label="請求月" prop="billingYearMonth" width="100">
          <template #default="{ row }">
            {{ formatYearMonth(row.billingYearMonth) }}
          </template>
        </el-table-column>

        <el-table-column label="請求日" prop="invoiceDate" width="110">
          <template #default="{ row }">
            {{ formatDate(row.invoiceDate) }}
          </template>
        </el-table-column>

        <el-table-column label="支払期限" prop="dueDate" width="110">
          <template #default="{ row }">
            <span :class="{ 'overdue': isOverdue(row) }">{{ formatDate(row.dueDate) }}</span>
          </template>
        </el-table-column>

        <el-table-column label="税抜金額" prop="subtotal" width="120" align="right">
          <template #default="{ row }">
            ¥{{ formatNumber(row.subtotal) }}
          </template>
        </el-table-column>

        <el-table-column label="税込合計" width="130" align="right">
          <template #default="{ row }">
            <span class="total-amount">¥{{ formatNumber(row.totalAmount) }}</span>
          </template>
        </el-table-column>

        <el-table-column label="入金額" width="120" align="right">
          <template #default="{ row }">
            <span v-if="row.paidAmount > 0" :class="{ 'paid-full': row.paidAmount >= row.totalAmount }">
              ¥{{ formatNumber(row.paidAmount) }}
            </span>
            <span v-else class="unpaid">-</span>
          </template>
        </el-table-column>

        <el-table-column label="ステータス" prop="status" width="100">
          <template #default="{ row }">
            <el-tag :type="getStatusTagType(row.status)" size="small">
              {{ getStatusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="操作" width="200" fixed="right" align="center">
          <template #default="{ row }">
            <el-button link type="primary" @click="onView(row)">詳細</el-button>
            <el-button link type="success" @click="confirmInvoice(row)" v-if="row.status === 'draft'">確定</el-button>
            <el-button link type="warning" @click="issueInvoice(row)" v-if="row.status === 'confirmed'">発行</el-button>
            <el-button link type="primary" @click="openPayment(row)" v-if="['issued', 'sent', 'partial_paid'].includes(row.status)">入金</el-button>
            <el-button link type="danger" @click="cancelInvoice(row)" v-if="['draft', 'confirmed'].includes(row.status)">取消</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 詳細ダイアログ -->
    <el-dialog 
      v-model="detailDialogVisible" 
      title="請求書詳細"
      width="900px"
      destroy-on-close
    >
      <div class="invoice-detail" v-if="currentInvoice">
        <div class="invoice-header-info">
          <div class="info-row">
            <span class="label">請求書番号:</span>
            <span class="value invoice-no-large">{{ currentInvoice.invoice_no }}</span>
          </div>
          <div class="info-row">
            <span class="label">顧客:</span>
            <span class="value">{{ currentInvoice.clientName }}</span>
          </div>
          <div class="info-row">
            <span class="label">請求期間:</span>
            <span class="value">{{ formatDate(currentInvoice.billing_period_start) }} ～ {{ formatDate(currentInvoice.billing_period_end) }}</span>
          </div>
          <div class="info-row">
            <span class="label">請求日:</span>
            <span class="value">{{ formatDate(currentInvoice.invoice_date) }}</span>
          </div>
          <div class="info-row">
            <span class="label">支払期限:</span>
            <span class="value">{{ formatDate(currentInvoice.due_date) }}</span>
          </div>
        </div>

        <el-divider>明細</el-divider>

        <el-table :data="currentInvoice.lines || []" border size="small">
          <el-table-column label="No" type="index" width="50" align="center" />
          <el-table-column label="リソース" width="140">
            <template #default="{ row }">
              <div v-if="row.resourceName">
                <div>{{ row.resourceName }}</div>
                <div class="resource-code">{{ row.resourceCode }}</div>
              </div>
              <span v-else>-</span>
            </template>
          </el-table-column>
          <el-table-column label="摘要" prop="description" min-width="180" />
          <el-table-column label="数量" prop="quantity" width="80" align="right">
            <template #default="{ row }">
              {{ row.quantity }} {{ row.unit }}
            </template>
          </el-table-column>
          <el-table-column label="単価" prop="unitPrice" width="100" align="right">
            <template #default="{ row }">
              ¥{{ formatNumber(row.unitPrice) }}
            </template>
          </el-table-column>
          <el-table-column label="残業" width="90" align="right">
            <template #default="{ row }">
              <span v-if="row.overtimeAmount > 0">+¥{{ formatNumber(row.overtimeAmount) }}</span>
              <span v-else>-</span>
            </template>
          </el-table-column>
          <el-table-column label="金額" prop="lineAmount" width="120" align="right">
            <template #default="{ row }">
              ¥{{ formatNumber(row.lineAmount) }}
            </template>
          </el-table-column>
        </el-table>

        <div class="invoice-summary">
          <div class="summary-row">
            <span class="label">小計:</span>
            <span class="value">¥{{ formatNumber(currentInvoice.subtotal) }}</span>
          </div>
          <div class="summary-row">
            <span class="label">消費税 ({{ (currentInvoice.tax_rate * 100).toFixed(0) }}%):</span>
            <span class="value">¥{{ formatNumber(currentInvoice.tax_amount) }}</span>
          </div>
          <div class="summary-row total">
            <span class="label">合計:</span>
            <span class="value">¥{{ formatNumber(currentInvoice.total_amount) }}</span>
          </div>
          <div class="summary-row" v-if="currentInvoice.paid_amount > 0">
            <span class="label">入金済:</span>
            <span class="value paid">¥{{ formatNumber(currentInvoice.paid_amount) }}</span>
          </div>
        </div>
      </div>

      <template #footer>
        <el-button @click="detailDialogVisible = false">閉じる</el-button>
      </template>
    </el-dialog>

    <!-- 請求書生成ダイアログ -->
    <el-dialog v-model="generateDialogVisible" title="請求書生成" width="400px">
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
        <el-alert type="info" :closable="false">
          確定済みの勤怠データから請求書を自動生成します。<br>
          顧客ごとに1つの請求書が作成されます。
        </el-alert>
      </el-form>
      <template #footer>
        <el-button @click="generateDialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="generate" :loading="generating">生成</el-button>
      </template>
    </el-dialog>

    <!-- 入金登録ダイアログ -->
    <el-dialog v-model="paymentDialogVisible" title="入金登録" width="400px">
      <el-form label-width="100px" v-if="paymentTarget">
        <el-form-item label="請求書番号">
          <el-input :model-value="paymentTarget.invoiceNo" disabled />
        </el-form-item>
        <el-form-item label="請求金額">
          <el-input :model-value="`¥${formatNumber(paymentTarget.totalAmount)}`" disabled />
        </el-form-item>
        <el-form-item label="入金済額">
          <el-input :model-value="`¥${formatNumber(paymentTarget.paidAmount)}`" disabled />
        </el-form-item>
        <el-form-item label="残高">
          <el-input :model-value="`¥${formatNumber(paymentTarget.totalAmount - paymentTarget.paidAmount)}`" disabled />
        </el-form-item>
        <el-divider />
        <el-form-item label="入金額" required>
          <el-input-number v-model="paymentAmount" :min="1" :step="10000" style="width: 100%" />
        </el-form-item>
        <el-form-item label="入金日">
          <el-date-picker v-model="paymentDate" type="date" style="width: 100%" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="paymentDialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="recordPayment" :loading="saving">登録</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Tickets, Refresh, Search } from '@element-plus/icons-vue'
import api from '../../api'

interface InvoiceRow {
  id: string
  invoiceNo: string
  clientPartnerId: string
  billingYearMonth: string
  billingPeriodStart: string
  billingPeriodEnd: string
  subtotal: number
  taxRate: number
  taxAmount: number
  totalAmount: number
  invoiceDate: string
  dueDate: string
  status: string
  paidAmount: number
  lastPaymentDate?: string
  clientCode?: string
  clientName?: string
}

const loading = ref(false)
const rows = ref<InvoiceRow[]>([])
const total = ref(0)
const selectedMonth = ref('')
const statusFilter = ref('')
const clientFilter = ref('')
const clientOptions = ref<{ label: string; value: string }[]>([])

const detailDialogVisible = ref(false)
const currentInvoice = ref<any>(null)

const generateDialogVisible = ref(false)
const generateMonth = ref(new Date().toISOString().substring(0, 7))
const generating = ref(false)

const paymentDialogVisible = ref(false)
const paymentTarget = ref<InvoiceRow | null>(null)
const paymentAmount = ref<number>(0)
const paymentDate = ref<string>(new Date().toISOString().split('T')[0])
const saving = ref(false)

const load = async () => {
  loading.value = true
  try {
    const params: Record<string, string> = {}
    if (selectedMonth.value) params.yearMonth = selectedMonth.value
    if (statusFilter.value) params.status = statusFilter.value
    if (clientFilter.value) params.clientId = clientFilter.value
    
    const res = await api.get('/staffing/invoices', { params })
    rows.value = res.data.data || []
    total.value = res.data.total || rows.value.length
  } catch (e: any) {
    ElMessage.error(e.message || '読み込み失敗')
  } finally {
    loading.value = false
  }
}

const searchClients = async (query: string) => {
  if (!query) return
  try {
    const res = await api.get('/businesspartners', { params: { keyword: query, flag_customer: true } })
    clientOptions.value = (res.data.data || []).map((bp: any) => ({
      label: `${bp.partner_code} - ${bp.payload?.name || ''}`,
      value: bp.id
    }))
  } catch (e) {
    console.error(e)
  }
}

const onView = async (row: InvoiceRow) => {
  try {
    const res = await api.get(`/staffing/invoices/${row.id}`)
    currentInvoice.value = res.data
    detailDialogVisible.value = true
  } catch (e: any) {
    ElMessage.error('詳細取得失敗')
  }
}

const confirmInvoice = async (row: InvoiceRow) => {
  try {
    await ElMessageBox.confirm('この請求書を確定しますか？', '確認', { type: 'warning' })
    await api.post(`/staffing/invoices/${row.id}/confirm`)
    ElMessage.success('確定しました')
    load()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e.response?.data?.error || '確定失敗')
    }
  }
}

const issueInvoice = async (row: InvoiceRow) => {
  try {
    await ElMessageBox.confirm('この請求書を発行しますか？', '確認', { type: 'warning' })
    await api.post(`/staffing/invoices/${row.id}/issue`)
    ElMessage.success('発行しました')
    load()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e.response?.data?.error || '発行失敗')
    }
  }
}

const cancelInvoice = async (row: InvoiceRow) => {
  try {
    await ElMessageBox.confirm('この請求書を取り消しますか？関連する勤怠は確定済み状態に戻ります。', '確認', { type: 'warning' })
    await api.post(`/staffing/invoices/${row.id}/cancel`)
    ElMessage.success('取り消しました')
    load()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e.response?.data?.error || '取消失敗')
    }
  }
}

const openPayment = (row: InvoiceRow) => {
  paymentTarget.value = row
  paymentAmount.value = row.totalAmount - row.paidAmount
  paymentDate.value = new Date().toISOString().split('T')[0]
  paymentDialogVisible.value = true
}

const recordPayment = async () => {
  if (!paymentTarget.value || paymentAmount.value <= 0) {
    ElMessage.warning('入金額を入力してください')
    return
  }
  saving.value = true
  try {
    await api.post(`/staffing/invoices/${paymentTarget.value.id}/payment`, {
      amount: paymentAmount.value,
      paymentDate: paymentDate.value
    })
    ElMessage.success('入金を登録しました')
    paymentDialogVisible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '入金登録失敗')
  } finally {
    saving.value = false
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
    const res = await api.post('/staffing/invoices/generate', { yearMonth: generateMonth.value })
    if (res.data.generated === 0) {
      ElMessage.info('生成対象の確定済み勤怠がありません')
    } else {
      ElMessage.success(`${res.data.generated}件の請求書を生成しました`)
    }
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
  const map: Record<string, string> = {
    draft: '下書き',
    confirmed: '確定',
    issued: '発行済',
    sent: '送付済',
    paid: '入金済',
    partial_paid: '一部入金',
    overdue: '延滞',
    cancelled: 'キャンセル'
  }
  return map[status] || status
}

const getStatusTagType = (status: string) => {
  const map: Record<string, string> = {
    draft: 'info',
    confirmed: 'warning',
    issued: 'primary',
    sent: '',
    paid: 'success',
    partial_paid: 'warning',
    overdue: 'danger',
    cancelled: ''
  }
  return map[status] || 'info'
}

const isOverdue = (row: InvoiceRow) => {
  if (['paid', 'cancelled'].includes(row.status)) return false
  return new Date(row.dueDate) < new Date()
}

const formatYearMonth = (ym: string) => {
  if (!ym) return ''
  const [y, m] = ym.split('-')
  return `${y}年${m}月`
}

const formatDate = (dateStr: string) => {
  if (!dateStr) return ''
  return new Date(dateStr).toLocaleDateString('ja-JP')
}

const formatNumber = (num: number) => {
  return new Intl.NumberFormat('ja-JP').format(num || 0)
}

onMounted(() => {
  load()
})
</script>

<style scoped>
.invoices-list {
  padding: 20px;
}

.invoices-card {
  border-radius: 8px;
}

.invoices-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.invoices-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.invoices-header__icon {
  font-size: 24px;
  color: var(--el-color-primary);
}

.invoices-header__title {
  font-size: 18px;
  font-weight: 600;
}

.invoices-header__right {
  display: flex;
  gap: 10px;
}

.invoices-filters {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.invoice-no {
  font-family: monospace;
  color: var(--el-color-primary);
  font-weight: 500;
}

.client-name {
  font-weight: 500;
}

.client-code {
  font-size: 12px;
  color: #999;
}

.total-amount {
  font-weight: 700;
  color: var(--el-color-primary);
}

.overdue {
  color: var(--el-color-danger);
  font-weight: 600;
}

.paid-full {
  color: var(--el-color-success);
  font-weight: 600;
}

.unpaid {
  color: #999;
}

/* 詳細ダイアログ */
.invoice-detail {
  padding: 0 20px;
}

.invoice-header-info {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 12px;
}

.info-row {
  display: flex;
  gap: 10px;
}

.info-row .label {
  color: #999;
  min-width: 80px;
}

.info-row .value {
  font-weight: 500;
}

.invoice-no-large {
  font-family: monospace;
  font-size: 16px;
  color: var(--el-color-primary);
}

.resource-code {
  font-size: 11px;
  color: #999;
}

.invoice-summary {
  margin-top: 20px;
  text-align: right;
}

.summary-row {
  display: flex;
  justify-content: flex-end;
  gap: 20px;
  padding: 6px 0;
}

.summary-row .label {
  color: #666;
}

.summary-row .value {
  min-width: 120px;
  text-align: right;
}

.summary-row.total {
  font-size: 18px;
  font-weight: 700;
  color: var(--el-color-primary);
  border-top: 2px solid var(--el-border-color);
  padding-top: 12px;
}

.summary-row .value.paid {
  color: var(--el-color-success);
}

.el-divider {
  margin: 16px 0;
}
</style>

