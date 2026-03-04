<template>
  <div>
    <div class="page" v-if="!props.createOnly">
      <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ labels.title }}</div>
          <div class="page-actions">
            <el-button @click="openBatchDialog">一括請求書作成</el-button>
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
    <el-dialog
      v-model="createDialog.visible"
      :title="labels.createTitle"
      width="760px"
      append-to-body
      destroy-on-close
    >
      <el-form :model="createDialog.form" label-width="80px" class="inv-create-form">
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
            size="small"
            border
            style="width: 100%"
            @selection-change="onDeliveryNoteSelect"
          >
            <el-table-column type="selection" width="44" />
            <el-table-column prop="delivery_no" :label="labels.deliveryNo" width="130" />
            <el-table-column prop="delivery_date" :label="labels.deliveryDate" width="100" />
            <el-table-column prop="sales_order_no" :label="labels.salesOrder" width="130" />
            <el-table-column :label="labels.amount" width="110" align="right">
              <template #default="{ row }">¥{{ formatNumber(calculateDeliveryAmount(row)) }}</template>
            </el-table-column>
          </el-table>
          <div v-if="!deliveryNoteOptions.length && !loadingDeliveryNotes && createDialog.form.customerCode" class="inv-no-dn">
            請求対象の納品書がありません
          </div>
        </el-form-item>

        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item :label="labels.invoiceDate">
              <el-date-picker v-model="createDialog.form.invoiceDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="labels.dueDate">
              <el-date-picker v-model="createDialog.form.dueDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
              <div class="inv-help-text">{{ labels.dueDateHelp }}</div>
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item :label="labels.note">
          <el-input v-model="createDialog.form.note" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>
      <template #footer>
        <div class="dialog-footer">
          <el-button @click="createDialog.visible = false">{{ labels.cancelBtn }}</el-button>
          <el-button type="primary" :loading="createDialog.creating" @click="createInvoice">{{ labels.createBtn }}</el-button>
        </div>
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

    <!-- 一括請求書作成ダイアログ -->
    <el-dialog
      v-model="batchDialog.visible"
      title="一括請求書作成"
      width="560px"
      destroy-on-close
    >
      <div v-if="batchDialog.loading" class="batch-loading">
        <el-icon class="is-loading" style="font-size:24px;color:#409eff"><Loading /></el-icon>
        <span style="margin-left:10px;color:#606266">プレビュー取得中...</span>
      </div>
      <template v-else>
        <div class="batch-suggestion">
          <el-icon style="color:#409eff;margin-right:6px"><InfoFilled /></el-icon>
          <span>{{ batchSuggestionText }}</span>
        </div>

        <el-divider />

        <el-form label-width="80px" class="batch-form">
          <el-row :gutter="16">
            <el-col :span="10">
              <el-form-item label="対象年月">
                <el-date-picker
                  v-model="batchDialog.yearMonth"
                  type="month"
                  value-format="YYYY-MM"
                  style="width:100%"
                  @change="loadBatchPreview"
                />
              </el-form-item>
            </el-col>
            <el-col :span="14">
              <el-form-item label="請求日">
                <el-date-picker v-model="batchDialog.invoiceDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
              </el-form-item>
            </el-col>
          </el-row>
          <el-form-item label="作成対象">
            <el-radio-group v-model="batchDialog.mode">
              <el-radio value="missing_only">未作成のみ（{{ batchMissingCount }}件）</el-radio>
              <el-radio value="all">全て（既存含む, {{ batchTotalDns }}件）</el-radio>
            </el-radio-group>
          </el-form-item>
        </el-form>

        <div v-if="batchDialog.preview?.customerGroups?.length" class="batch-preview-table">
          <div class="batch-preview-title">対象顧客</div>
          <el-table :data="batchDialog.preview.customerGroups" size="small" border>
            <el-table-column label="得意先" prop="customerName" min-width="160" />
            <el-table-column label="コード" prop="customerCode" width="100" />
            <el-table-column label="未請求" prop="uninvoicedDns" width="70" align="center" />
            <el-table-column label="合計" prop="totalDns" width="70" align="center" />
          </el-table>
        </div>
        <div v-else class="batch-no-data">対象の納品書がありません</div>
      </template>
      <template #footer>
        <div class="dialog-footer">
          <el-button @click="batchDialog.visible = false">キャンセル</el-button>
          <el-button
            type="primary"
            :loading="batchDialog.running"
            :disabled="batchDialog.loading || !batchHasTarget"
            @click="runBatch"
          >実行</el-button>
        </div>
      </template>
    </el-dialog>

    <!-- 一括作成結果ダイアログ -->
    <el-dialog
      v-model="batchResult.visible"
      title="一括作成結果"
      width="640px"
      destroy-on-close
    >
      <div class="batch-result-summary">
        <el-tag type="success" size="large">成功: {{ batchResult.successCount }}件</el-tag>
        <el-tag v-if="batchResult.failCount > 0" type="danger" size="large" style="margin-left:8px">
          失敗: {{ batchResult.failCount }}件
        </el-tag>
      </div>
      <el-table :data="batchResult.items" size="small" border style="margin-top:12px">
        <el-table-column label="得意先" prop="customerName" min-width="140" />
        <el-table-column label="請求書番号" prop="invoiceNo" width="150">
          <template #default="{ row }">
            <span v-if="row.success" class="batch-inv-no">{{ row.invoiceNo }}</span>
            <el-tag v-else type="danger" size="small">失敗</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="納品書数" prop="dnCount" width="80" align="center" />
        <el-table-column label="状態/原因" min-width="200">
          <template #default="{ row }">
            <span v-if="row.success" style="color:#67c23a">
              作成完了<template v-if="row.voucherError"> <el-tag type="warning" size="small">伝票転記失敗</el-tag></template>
            </span>
            <span v-else style="color:#f56c6c;font-size:12px">{{ row.error }}</span>
          </template>
        </el-table-column>
      </el-table>
      <template #footer>
        <div class="dialog-footer">
          <el-button type="primary" @click="batchResult.visible = false">閉じる</el-button>
        </div>
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
import { Plus, Loading, InfoFilled } from '@element-plus/icons-vue'
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

// ── 一括請求書作成 ─────────────────────────────────
const prevMonth = (() => {
  const d = new Date()
  d.setMonth(d.getMonth() - 1)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`
})()

const batchDialog = reactive({
  visible: false,
  loading: false,
  running: false,
  yearMonth: prevMonth,
  invoiceDate: new Date().toISOString().slice(0, 10),
  mode: 'missing_only' as 'missing_only' | 'all',
  preview: null as any
})

const batchResult = reactive({
  visible: false,
  items: [] as any[],
  successCount: 0,
  failCount: 0
})

const batchMissingCount = computed(() => {
  if (!batchDialog.preview?.customerGroups) return 0
  return batchDialog.preview.customerGroups.reduce((s: number, g: any) => s + (g.uninvoicedDns || 0), 0)
})

const batchTotalDns = computed(() => {
  if (!batchDialog.preview?.customerGroups) return 0
  return batchDialog.preview.customerGroups.reduce((s: number, g: any) => s + (g.totalDns || 0), 0)
})

const batchHasTarget = computed(() => {
  return batchDialog.mode === 'missing_only' ? batchMissingCount.value > 0 : batchTotalDns.value > 0
})

const batchSuggestionText = computed(() => {
  if (!batchDialog.preview) return ''
  const [y, m] = batchDialog.yearMonth.split('-')
  const existing = batchDialog.preview.existingInvoiceCount || 0
  const missing = batchMissingCount.value
  const total = batchTotalDns.value
  let text = `${y}年${Number(m)}月の請求書を一括作成します。`
  text += `\n出荷済み納品書: ${total}件、未請求: ${missing}件`
  if (existing > 0) text += `\n※この月に既存の請求書が${existing}件あります。モードを確認してください。`
  return text
})

async function openBatchDialog() {
  batchDialog.loading = false
  batchDialog.preview = null
  batchDialog.visible = true
  batchDialog.mode = 'missing_only'
  batchDialog.invoiceDate = new Date().toISOString().slice(0, 10)
  await loadBatchPreview()
}

async function loadBatchPreview() {
  if (!batchDialog.yearMonth) return
  batchDialog.loading = true
  batchDialog.preview = null
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), 15000)
  try {
    const [y, m] = batchDialog.yearMonth.split('-')
    const r = await api.get(`/sales-invoices/batch-preview?year=${y}&month=${m}`, {
      signal: controller.signal
    })
    batchDialog.preview = r.data
  } catch (e: any) {
    if (e?.code === 'ERR_CANCELED' || e?.name === 'CanceledError') {
      ElMessage.error('プレビュー取得がタイムアウトしました。再度お試しください。')
    } else {
      ElMessage.error(e?.response?.data?.error || 'プレビュー取得に失敗しました')
    }
    console.error('[batch-preview]', e)
  } finally {
    clearTimeout(timer)
    batchDialog.loading = false
  }
}

async function runBatch() {
  batchDialog.running = true
  try {
    const [y, m] = batchDialog.yearMonth.split('-')
    const r = await api.post('/sales-invoices/batch-create', {
      year: Number(y),
      month: Number(m),
      mode: batchDialog.mode,
      invoiceDate: batchDialog.invoiceDate
    })
    const results: any[] = r.data?.results || []
    batchResult.items = results
    batchResult.successCount = results.filter(r => r.success).length
    batchResult.failCount = results.filter(r => !r.success).length
    batchDialog.visible = false
    batchResult.visible = true
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '一括作成に失敗しました')
  } finally {
    batchDialog.running = false
  }
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
.dialog-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.inv-create-form {
  padding-right: 4px;
}

.inv-help-text {
  font-size: 12px;
  color: #909399;
  margin-top: 4px;
  line-height: 1.4;
}

.inv-no-dn {
  font-size: 12px;
  color: #909399;
  padding: 8px 0;
  text-align: center;
}

/* 一括作成ダイアログ */
.batch-loading {
  display: flex;
  align-items: center;
  padding: 16px 0;
}

.batch-suggestion {
  display: flex;
  align-items: flex-start;
  background: #f0f7ff;
  border: 1px solid #c6e2ff;
  border-radius: 6px;
  padding: 12px 14px;
  font-size: 13px;
  color: #303133;
  white-space: pre-line;
  line-height: 1.7;
}

.batch-form {
  margin-top: 4px;
}

.batch-preview-title {
  font-size: 13px;
  font-weight: 600;
  color: #606266;
  margin-bottom: 8px;
}

.batch-preview-table {
  margin-top: 4px;
}

.batch-no-data {
  text-align: center;
  color: #909399;
  font-size: 13px;
  padding: 20px 0;
}

/* 結果ダイアログ */
.batch-result-summary {
  padding: 4px 0 8px;
}

.batch-inv-no {
  font-family: monospace;
  font-size: 13px;
  color: #67c23a;
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

