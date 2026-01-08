<template>
  <div>
    <div class="page" v-if="!props.createOnly">
      <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ labels.title }}</div>
          <div class="page-actions">
            <el-button type="primary" @click="openCreateDialog">
              <el-icon><Plus /></el-icon> {{ labels.create }}
            </el-button>
          </div>
        </div>
      </template>

      <!-- 筛选条件 -->
      <div class="filter-row">
        <el-select v-model="filterStatus" :placeholder="labels.status" clearable style="width: 140px" @change="load">
          <el-option :label="labels.statusDraft" value="draft" />
          <el-option :label="labels.statusIssued" value="issued" />
          <el-option :label="labels.statusPaid" value="paid" />
          <el-option :label="labels.statusCancelled" value="cancelled" />
        </el-select>
        <el-date-picker
          v-model="filterDateRange"
          type="daterange"
          :start-placeholder="labels.startDate"
          :end-placeholder="labels.endDate"
          value-format="YYYY-MM-DD"
          style="width: 260px"
          @change="load"
        />
        <el-button @click="load">{{ labels.refresh }}</el-button>
      </div>

      <!-- 列表 -->
      <el-table :data="list" v-loading="loading" stripe style="width: 100%">
        <el-table-column prop="invoice_no" :label="labels.invoiceNo" width="160">
          <template #default="{ row }">
            <el-link type="primary" @click="viewDetail(row.id)">{{ row.invoice_no }}</el-link>
          </template>
        </el-table-column>
        <el-table-column prop="invoice_date" :label="labels.invoiceDate" width="110" />
        <el-table-column prop="due_date" :label="labels.dueDate" width="110">
          <template #default="{ row }">
            <span :class="{ 'overdue': isOverdue(row) }">{{ row.due_date || '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="labels.customer" min-width="180">
          <template #default="{ row }">
            {{ row.customer_name || row.customer_code || '-' }}
          </template>
        </el-table-column>
        <el-table-column prop="amount_total" :label="labels.amount" width="140" align="right">
          <template #default="{ row }">
            ¥{{ formatNumber(row.amount_total) }}
          </template>
        </el-table-column>
        <el-table-column :label="labels.status" width="100" align="center">
          <template #default="{ row }">
            <el-tag :type="statusType(row.status)" size="small">{{ statusLabel(row.status) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="labels.actions" width="320" align="center">
          <template #default="{ row }">
            <el-button size="small" @click="viewDetail(row.id)">{{ labels.view }}</el-button>
            <el-button v-if="row.status === 'draft'" size="small" type="primary" @click="issueAndPost(row.id)">発行・記帳</el-button>
            <el-button v-if="hasVoucherError(row)" size="small" type="warning" @click="retryVoucher(row.id)">会計伝票転記</el-button>
            <el-button v-if="row.status === 'draft' || row.status === 'issued'" size="small" type="danger" @click="cancelInvoice(row.id)">{{ labels.cancel }}</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>

  <!-- 创建请求书对话框 -->
    <el-dialog v-model="createDialog.visible" width="800px" append-to-body>
      <template #header>
        <div class="dialog-header">
          <span class="dialog-title">{{ labels.createTitle }}</span>
        </div>
      </template>
      <el-form :model="createDialog.form" label-width="120px">
        <el-form-item :label="labels.customer" required>
          <el-select 
            v-model="createDialog.form.customerCode" 
            :placeholder="labels.selectCustomer" 
            filterable
            clearable 
            style="width: 100%"
            @change="loadShippedDeliveryNotes"
          >
            <el-option 
              v-for="c in customerOptions" 
              :key="c.partner_code" 
              :label="`${c.name} (${c.partner_code})`" 
              :value="c.partner_code" 
            />
          </el-select>
        </el-form-item>
        
        <el-form-item :label="labels.deliveryNotes" required>
          <el-table 
            ref="deliveryNoteTable"
            :data="deliveryNoteOptions" 
            v-loading="loadingDeliveryNotes"
            style="width: 100%"
            @selection-change="onDeliveryNoteSelect"
          >
            <el-table-column type="selection" width="50" />
            <el-table-column prop="delivery_no" :label="labels.deliveryNo" width="140" />
            <el-table-column prop="delivery_date" :label="labels.deliveryDate" width="110" />
            <el-table-column prop="sales_order_no" :label="labels.salesOrder" width="140" />
            <el-table-column :label="labels.amount" width="120" align="right">
              <template #default="{ row }">
                ¥{{ formatNumber(calculateDeliveryAmount(row)) }}
              </template>
            </el-table-column>
          </el-table>
        </el-form-item>

        <el-form-item :label="labels.invoiceDate">
          <el-date-picker v-model="createDialog.form.invoiceDate" type="date" value-format="YYYY-MM-DD" />
        </el-form-item>

        <el-form-item :label="labels.dueDate">
          <el-date-picker v-model="createDialog.form.dueDate" type="date" value-format="YYYY-MM-DD" />
          <span class="el-form-item__help">{{ labels.dueDateHelp }}</span>
        </el-form-item>

        <el-form-item :label="labels.note">
          <el-input v-model="createDialog.form.note" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createDialog.visible = false">{{ labels.cancelBtn }}</el-button>
        <el-button type="primary" :loading="createDialog.creating" @click="createInvoice">{{ labels.createBtn }}</el-button>
      </template>
    </el-dialog>

    <!-- 详情对话框 -->
    <el-dialog v-model="detailDialog.visible" :title="labels.detailTitle" width="1000px" destroy-on-close>
      <el-skeleton :loading="detailDialog.loading" :rows="8" animated />
      <template v-if="!detailDialog.loading && detailDialog.data">
        <el-descriptions :column="4" border size="small">
          <el-descriptions-item :label="labels.invoiceNo">{{ detailDialog.data.invoice_no }}</el-descriptions-item>
          <el-descriptions-item :label="labels.invoiceDate">{{ detailDialog.data.invoice_date }}</el-descriptions-item>
          <el-descriptions-item :label="labels.dueDate">{{ detailDialog.data.due_date }}</el-descriptions-item>
          <el-descriptions-item :label="labels.status">
            <el-tag :type="statusType(detailDialog.data.status)" size="small">{{ statusLabel(detailDialog.data.status) }}</el-tag>
          </el-descriptions-item>
          <el-descriptions-item :label="labels.customer" :span="2">
            {{ detailDialog.data.customer_name }} ({{ detailDialog.data.customer_code }})
          </el-descriptions-item>
          <el-descriptions-item :label="labels.taxAmount">¥{{ formatNumber(detailDialog.data.tax_amount) }}</el-descriptions-item>
          <el-descriptions-item :label="labels.amount">
            <span class="amount-total">¥{{ formatNumber(detailDialog.data.amount_total) }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="会計伝票" :span="4">
            <template v-if="detailDialog.data.payload?.header?.voucherNo">
              <el-link type="primary" @click="openVoucherDetail(detailDialog.data.payload.header.voucherId, detailDialog.data.payload.header.voucherNo)">
                {{ detailDialog.data.payload.header.voucherNo }}
              </el-link>
            </template>
            <template v-else-if="detailDialog.data.payload?.header?.voucherError">
              <el-tag type="danger" size="small">転記失敗</el-tag>
              <span style="margin-left: 8px; color: #f56c6c; font-size: 12px;">{{ detailDialog.data.payload.header.voucherError }}</span>
            </template>
            <template v-else>
              <span style="color: #909399;">-</span>
            </template>
          </el-descriptions-item>
        </el-descriptions>

        <h4 style="margin-top: 16px; margin-bottom: 8px;">{{ labels.lines }}</h4>
        <el-table :data="detailLines" stripe size="small" style="width: 100%">
          <el-table-column prop="lineNo" label="#" width="40" />
          <el-table-column prop="deliveryNo" :label="labels.deliveryNo" width="130" />
          <el-table-column prop="materialCode" :label="labels.materialCode" width="110" />
          <el-table-column prop="materialName" :label="labels.materialName" min-width="140" />
          <el-table-column prop="quantity" :label="labels.quantity" width="70" align="right" />
          <el-table-column prop="uom" :label="labels.uom" width="50" />
          <el-table-column prop="unitPrice" :label="labels.unitPrice" width="80" align="right">
            <template #default="{ row }">¥{{ formatNumber(row.unitPrice) }}</template>
          </el-table-column>
          <el-table-column prop="amount" :label="labels.lineAmount" width="90" align="right">
            <template #default="{ row }">¥{{ formatNumber(row.amount) }}</template>
          </el-table-column>
          <el-table-column prop="taxRate" :label="labels.taxRate" width="50" align="center">
            <template #default="{ row }">{{ row.taxRate }}%</template>
          </el-table-column>
          <el-table-column prop="taxAmount" :label="labels.lineTax" width="70" align="right">
            <template #default="{ row }">¥{{ formatNumber(row.taxAmount) }}</template>
          </el-table-column>
        </el-table>
      </template>
      <template #footer>
        <el-button @click="detailDialog.visible = false">{{ labels.close }}</el-button>
      </template>
    </el-dialog>

    <!-- 取消对话框 -->
    <el-dialog v-model="cancelDialog.visible" :title="labels.cancelTitle" width="400px">
      <el-form :model="cancelDialog.form" label-width="80px">
        <el-form-item :label="labels.reason">
          <el-input v-model="cancelDialog.form.reason" type="textarea" :rows="3" :placeholder="labels.reasonPlaceholder" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="cancelDialog.visible = false">{{ labels.cancelBtn }}</el-button>
        <el-button type="danger" :loading="cancelDialog.cancelling" @click="confirmCancel">{{ labels.confirmCancel }}</el-button>
      </template>
    </el-dialog>

    <!-- 会計伝票弹窗 -->
    <el-dialog 
      v-model="voucherDialog.visible" 
      width="auto" 
      destroy-on-close
      append-to-body
      class="voucher-detail-dialog"
    >
      <template #header></template>
      <!-- 统一复用 VouchersList 的标准详情弹窗样式：不在此处包 wrapper / 不传 embed class -->
      <div>
        <VouchersList
          v-if="voucherDialog.visible && (voucherDialog.voucherId || voucherDialog.voucherNo)"
          :allow-edit="false"
          :initial-voucher-id="voucherDialog.voucherId || undefined"
          :initial-voucher-no="voucherDialog.voucherNo || undefined"
        />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { Plus } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '../api'
import VouchersList from './VouchersList.vue'

const props = defineProps<{
  autoOpenCreate?: boolean
  createOnly?: boolean  // 只显示创建弹窗模式
}>()

// 多语言标签
const labels = {
  title: '請求書一覧',
  create: '請求書作成',
  createTitle: '請求書作成',
  invoiceNo: '請求書番号',
  invoiceDate: '請求日',
  dueDate: '支払期限',
  dueDateHelp: '未指定の場合は顧客の支払条件から自動計算',
  customer: '顧客',
  selectCustomer: '顧客を選択',
  deliveryNotes: '納品書',
  deliveryNo: '納品書番号',
  deliveryDate: '納品日',
  salesOrder: '受注番号',
  amount: '合計金額',
  taxAmount: '消費税',
  status: 'ステータス',
  statusDraft: '下書き',
  statusIssued: '請求済',
  statusPaid: '入金済',
  statusCancelled: 'キャンセル',
  actions: '操作',
  view: '詳細',
  issue: '発行',
  markPaid: '入金済',
  cancel: 'キャンセル',
  refresh: '更新',
  startDate: '開始日',
  endDate: '終了日',
  note: '備考',
  createBtn: '作成',
  cancelBtn: 'キャンセル',
  detailTitle: '請求書詳細',
  lines: '明細',
  materialCode: '品目コード',
  materialName: '品目名',
  quantity: '数量',
  uom: '単位',
  unitPrice: '単価',
  lineAmount: '金額',
  taxRate: '税率',
  lineTax: '税額',
  close: '閉じる',
  cancelTitle: '請求書キャンセル',
  reason: '理由',
  reasonPlaceholder: 'キャンセル理由を入力',
  confirmCancel: 'キャンセル実行'
}

const loading = ref(false)
const list = ref<any[]>([])
const filterStatus = ref('')
const filterDateRange = ref<string[]>([])

const createDialog = reactive({
  visible: false,
  creating: false,
  form: {
    customerCode: '',
    invoiceDate: new Date().toISOString().slice(0, 10),
    dueDate: '',
    note: '',
    deliveryNoteIds: [] as string[]
  }
})

const detailDialog = reactive({
  visible: false,
  loading: false,
  data: null as any
})

const cancelDialog = reactive({
  visible: false,
  cancelling: false,
  invoiceId: '',
  form: {
    reason: ''
  }
})

// 会計伝票弹窗
const voucherDialog = reactive({
  visible: false,
  voucherId: '',
  voucherNo: ''
})

const customerOptions = ref<any[]>([])
const deliveryNoteOptions = ref<any[]>([])
const loadingDeliveryNotes = ref(false)

const detailLines = computed(() => {
  if (!detailDialog.data?.payload?.lines) return []
  return detailDialog.data.payload.lines
})

async function load() {
  loading.value = true
  try {
    const params = new URLSearchParams()
    if (filterStatus.value) params.append('status', filterStatus.value)
    if (filterDateRange.value?.length === 2) {
      params.append('fromDate', filterDateRange.value[0])
      params.append('toDate', filterDateRange.value[1])
    }
    const res = await api.get(`/sales-invoices?${params.toString()}`)
    list.value = res.data || []
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '読み込みに失敗しました')
  } finally {
    loading.value = false
  }
}

function statusType(status: string) {
  switch (status) {
    case 'draft': return 'info'
    case 'issued': return 'primary'
    case 'paid': return 'success'
    case 'cancelled': return 'danger'
    default: return 'info'
  }
}

function statusLabel(status: string) {
  switch (status) {
    case 'draft': return labels.statusDraft
    case 'issued': return labels.statusIssued
    case 'paid': return labels.statusPaid
    case 'cancelled': return labels.statusCancelled
    default: return status
  }
}

function isOverdue(row: any) {
  if (row.status !== 'issued') return false
  if (!row.due_date) return false
  return new Date(row.due_date) < new Date()
}

function formatNumber(val: any) {
  const num = Number(val || 0)
  if (!Number.isFinite(num)) return val ?? ''
  return num.toLocaleString()
}

function calculateDeliveryAmount(row: any) {
  const lines = row.payload?.lines || []
  return lines.reduce((sum: number, line: any) => {
    const qty = Number(line.deliveryQty || 0)
    const price = Number(line.unitPrice || 0)
    const taxRate = Number(line.taxRate || 10)
    const amount = qty * price
    const tax = Math.round(amount * taxRate / 100)
    return sum + amount + tax
  }, 0)
}

async function openCreateDialog() {
  createDialog.form = {
    customerCode: '',
    invoiceDate: new Date().toISOString().slice(0, 10),
    dueDate: '',
    note: '',
    deliveryNoteIds: []
  }
  deliveryNoteOptions.value = []
  createDialog.visible = true
  // 加载客户列表
  await loadCustomers()
}

async function loadCustomers() {
  try {
    const res = await api.post('/objects/businesspartner/search', {
      page: 1,
      pageSize: 100,
      where: [
        { field: 'flag_customer', op: 'eq', value: true }
      ],
      orderBy: [{ field: 'name', direction: 'asc' }]
    })
    customerOptions.value = res.data?.data || []
  } catch (e) {
    console.error('Failed to load customers:', e)
  }
}

async function loadShippedDeliveryNotes() {
  if (!createDialog.form.customerCode) {
    deliveryNoteOptions.value = []
    return
  }
  loadingDeliveryNotes.value = true
  try {
    // 只加载已出库且未被请求的纳品书
    const res = await api.get(`/delivery-notes?status=shipped&customerCode=${createDialog.form.customerCode}&excludeInvoiced=true`)
    deliveryNoteOptions.value = res.data || []
  } catch (e) {
    console.error('Failed to load delivery notes:', e)
    deliveryNoteOptions.value = []
  } finally {
    loadingDeliveryNotes.value = false
  }
}

function onDeliveryNoteSelect(selected: any[]) {
  createDialog.form.deliveryNoteIds = selected.map((r: any) => r.id)
}

async function createInvoice() {
  if (!createDialog.form.customerCode) {
    ElMessage.warning('顧客を選択してください')
    return
  }
  if (createDialog.form.deliveryNoteIds.length === 0) {
    ElMessage.warning('納品書を選択してください')
    return
  }
  createDialog.creating = true
  try {
    const res = await api.post('/sales-invoices/from-delivery-notes', {
      deliveryNoteIds: createDialog.form.deliveryNoteIds,
      invoiceDate: createDialog.form.invoiceDate,
      dueDate: createDialog.form.dueDate || undefined,
      note: createDialog.form.note || undefined
    })
    ElMessage.success(`請求書 ${res.data.invoiceNo} を作成しました`)
    createDialog.visible = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '作成に失敗しました')
  } finally {
    createDialog.creating = false
  }
}

async function viewDetail(id: string) {
  detailDialog.visible = true
  detailDialog.loading = true
  detailDialog.data = null
  try {
    const res = await api.get(`/sales-invoices/${id}`)
    detailDialog.data = res.data
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '取得に失敗しました')
  } finally {
    detailDialog.loading = false
  }
}

function hasVoucherError(row: any) {
  const header = row.payload?.header || {}
  return header.voucherError && !header.voucherNo
}

async function issueAndPost(id: string) {
  try {
    await ElMessageBox.confirm('請求書を発行し、会計伝票を作成しますか？', '確認', { type: 'info' })
    const res = await api.post(`/sales-invoices/${id}/issue-and-post`)
    if (res.data.voucherNo) {
      ElMessage.success(`発行しました。会計伝票 ${res.data.voucherNo} を作成しました`)
    } else if (res.data.voucherError) {
      ElMessage.warning(`発行しましたが、会計伝票の作成に失敗しました: ${res.data.voucherError}`)
    } else {
      ElMessage.success('発行しました')
    }
    load()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e?.response?.data?.detail || e?.response?.data?.error || '発行に失敗しました')
    }
  }
}

async function retryVoucher(id: string) {
  try {
    await ElMessageBox.confirm('会計伝票を転記しますか？', '確認', { type: 'info' })
    const res = await api.post(`/sales-invoices/${id}/retry-voucher`)
    ElMessage.success(`会計伝票 ${res.data.voucherNo} を作成しました`)
    load()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e?.response?.data?.detail || e?.response?.data?.error || '転記に失敗しました')
    }
  }
}

async function markPaid(id: string) {
  try {
    await ElMessageBox.confirm('入金済みにしますか？', '確認', { type: 'info' })
    await api.post(`/sales-invoices/${id}/mark-paid`)
    ElMessage.success('入金済みに更新しました')
    load()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e?.response?.data?.error || '更新に失敗しました')
    }
  }
}

function cancelInvoice(id: string) {
  cancelDialog.invoiceId = id
  cancelDialog.form.reason = ''
  cancelDialog.visible = true
}

async function confirmCancel() {
  cancelDialog.cancelling = true
  try {
    await api.post(`/sales-invoices/${cancelDialog.invoiceId}/cancel`, {
      reason: cancelDialog.form.reason
    })
    ElMessage.success('キャンセルしました')
    cancelDialog.visible = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'キャンセルに失敗しました')
  } finally {
    cancelDialog.cancelling = false
  }
}

function openVoucherDetail(voucherId?: string, voucherNo?: string) {
  if (!voucherId && !voucherNo) return
  voucherDialog.voucherId = voucherId || ''
  voucherDialog.voucherNo = voucherNo || ''
  voucherDialog.visible = true
}

onMounted(() => {
  load()
  if (props.autoOpenCreate) {
    openCreateDialog()
  }
})
</script>

<style scoped>
.page {
  padding: 20px;
}
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.page-header-title {
  font-size: 18px;
  font-weight: 600;
}
.filter-row {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
}
.overdue {
  color: #f56c6c;
  font-weight: 600;
}
.amount-total {
  font-size: 18px;
  font-weight: 600;
  color: #409eff;
}
.el-form-item__help {
  font-size: 12px;
  color: #909399;
  margin-left: 8px;
}
.dialog-header {
  display: flex;
  align-items: center;
}
.dialog-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}
.voucher-dialog-card-wrap {
  min-width: 600px;
  max-width: 90vw;
  max-height: 80vh;
  overflow: auto;
}

.voucher-detail-embed {
  padding: 0;
}

:deep(.voucher-detail-embed .el-card) {
  box-shadow: none;
  border: none;
}

:deep(.voucher-detail-embed .el-card__header) {
  padding: 12px 16px;
  border-bottom: 1px solid #e4e7ed;
}

:deep(.voucher-detail-embed .el-card__body) {
  padding: 16px;
}
</style>

