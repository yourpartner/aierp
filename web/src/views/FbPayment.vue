<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Money /></el-icon>
            <span class="page-header-title">{{ labels.title }}</span>
          </div>
        </div>
      </template>

      <!-- 筛选条件 -->
      <div class="filter-row">
        <div class="filter-item">
          <el-date-picker
            v-model="filters.dateRange"
            type="daterange"
            :start-placeholder="labels.startDate"
            :end-placeholder="labels.endDate"
            value-format="YYYY-MM-DD"
            class="filter-date-range"
          />
        </div>
        <div class="filter-actions">
          <el-button type="primary" @click="openCreateDialog">{{ labels.create }}</el-button>
        </div>
      </div>

      <!-- FB文件列表 -->
      <el-table
        :data="files"
        stripe
        border
        style="width: 100%"
        v-loading="loading"
      >
        <el-table-column prop="fileName" :label="labels.fileName" width="200" show-overflow-tooltip />
        <el-table-column prop="recordType" :label="labels.recordType" width="100" align="center" />
        <el-table-column prop="bankName" :label="labels.bank" width="120" show-overflow-tooltip />
        <el-table-column prop="branchName" :label="labels.branch" width="100" show-overflow-tooltip />
        <el-table-column prop="paymentDate" :label="labels.paymentDate" width="110" align="center" />
        <el-table-column prop="depositType" :label="labels.depositType" width="90" align="center">
          <template #default="{ row }">{{ getDepositTypeName(row.depositType) }}</template>
        </el-table-column>
        <el-table-column prop="accountNumber" :label="labels.accountNumber" width="110" align="center" />
        <el-table-column prop="accountHolder" :label="labels.accountHolder" width="140" show-overflow-tooltip />
        <el-table-column prop="totalAmount" :label="labels.totalAmount" width="130" align="right">
          <template #default="{ row }">{{ formatAmount(row.totalAmount) }}</template>
        </el-table-column>
        <el-table-column prop="status" :label="labels.status" width="90" align="center">
          <template #default="{ row }">
            <el-tag :type="row.status === 'created' ? 'info' : 'success'" size="small">{{ row.status }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="labels.actions" width="120" align="center">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click="downloadFile(row)">{{ labels.download }}</el-button>
          </template>
        </el-table-column>
      </el-table>

      <!-- 分页 -->
      <div class="page-pagination">
        <el-pagination
          background
          layout="prev, pager, next, total"
          :total="total"
          :page-size="pageSize"
          :current-page="page"
          @update:current-page="onPage"
        />
      </div>
    </el-card>

    <!-- 新规登录弹窗 -->
    <el-dialog
      v-model="createDialogVisible"
      :title="labels.createTitle"
      width="95%"
      append-to-body
      destroy-on-close
      class="create-dialog"
    >
      <!-- 筛选条件 -->
      <div class="dialog-filters">
        <div class="filter-section">
          <div class="filter-item">
            <label class="filter-label">{{ labels.accountCodes }}</label>
            <el-select
              v-model="createFilters.accountCodes"
              multiple
              filterable
              remote
              clearable
              collapse-tags
              collapse-tags-tooltip
              :remote-method="searchAccounts"
              :placeholder="labels.selectAccounts"
              class="filter-select-wide"
              @focus="() => searchAccounts('')"
            >
              <el-option v-for="a in accountOptions" :key="a.value" :label="a.label" :value="a.value" />
            </el-select>
          </div>
          <div class="filter-item">
            <label class="filter-label">{{ labels.senderBank }}</label>
            <el-select
              v-model="createFilters.senderBankCode"
              filterable
              clearable
              :placeholder="labels.selectBank"
              class="filter-select"
              @change="onSenderBankChange"
            >
              <el-option v-for="b in bankOptions" :key="b.value" :label="b.label" :value="b.value" />
            </el-select>
          </div>
          <div class="filter-item">
            <label class="filter-label">{{ labels.paymentDateLabel }}</label>
            <el-date-picker
              v-model="createFilters.paymentDate"
              type="date"
              value-format="YYYY-MM-DD"
              :placeholder="labels.selectDate"
              class="filter-date"
            />
          </div>
        </div>
        <div class="filter-section">
          <div class="filter-item">
            <label class="filter-label">{{ labels.postingDate }}</label>
            <el-date-picker
              v-model="createFilters.postingDateRange"
              type="daterange"
              :start-placeholder="labels.startDate"
              :end-placeholder="labels.endDate"
              value-format="YYYY-MM-DD"
              class="filter-date-range"
            />
          </div>
          <div class="filter-item">
            <label class="filter-label">{{ labels.dueDate }}</label>
            <el-date-picker
              v-model="createFilters.dueDateRange"
              type="daterange"
              :start-placeholder="labels.startDate"
              :end-placeholder="labels.endDate"
              value-format="YYYY-MM-DD"
              class="filter-date-range"
            />
          </div>
          <div class="filter-actions">
            <el-button type="primary" @click="loadPendingDebts">{{ labels.search }}</el-button>
          </div>
        </div>
      </div>

      <!-- 债务列表 -->
      <el-table
        ref="debtTableRef"
        :data="pendingDebts"
        stripe
        border
        style="width: 100%"
        v-loading="debtLoading"
        max-height="400"
        @selection-change="onSelectionChange"
      >
        <el-table-column type="selection" width="50" />
        <el-table-column prop="payeeName" :label="labels.payee" width="120" show-overflow-tooltip>
          <template #default="{ row }">{{ row.vendorName || row.employeeName || '-' }}</template>
        </el-table-column>
        <el-table-column prop="accountName" :label="labels.accountName" width="120" show-overflow-tooltip>
          <template #default="{ row }">{{ row.accountName ? `${row.accountCode} ${row.accountName}` : row.accountCode }}</template>
        </el-table-column>
        <el-table-column prop="dueDate" :label="labels.dueDateCol" width="110" align="center">
          <template #default="{ row }">{{ row.dueDate || '-' }}</template>
        </el-table-column>
        <el-table-column prop="residualAmount" :label="labels.amount" width="120" align="right">
          <template #default="{ row }">-{{ formatAmount(row.residualAmount || row.amount) }}</template>
        </el-table-column>
        <el-table-column prop="headerText" :label="labels.remarks" min-width="200" show-overflow-tooltip>
          <template #default="{ row }">{{ row.headerText || row.lineText || '-' }}</template>
        </el-table-column>
        <el-table-column prop="voucherNo" :label="labels.voucherNo" width="140">
          <template #default="{ row }">{{ row.voucherNo }}-{{ row.lineNo }}</template>
        </el-table-column>
      </el-table>

      <!-- AI 建议区域 -->
      <div class="ai-suggestion" v-if="aiSuggestion">
        <el-alert :title="labels.aiSuggestion" type="info" :closable="false">
          <template #default>
            <div class="ai-content">{{ aiSuggestion }}</div>
          </template>
        </el-alert>
      </div>

      <!-- 汇总和操作 -->
      <div class="dialog-footer">
        <div class="summary">
          <span>{{ labels.selectedCount }}: {{ selectedItems.length }}</span>
          <span class="summary-amount">{{ labels.totalAmountLabel }}: {{ formatAmount(selectedTotalAmount) }}</span>
        </div>
        <div class="actions">
          <el-button @click="createDialogVisible = false">{{ labels.cancel }}</el-button>
          <el-button type="primary" :disabled="selectedItems.length === 0" @click="createFbFile">
            {{ labels.createFile }}
          </el-button>
        </div>
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, nextTick } from 'vue'
import { ElMessage } from 'element-plus'
import { Money } from '@element-plus/icons-vue'
import api from '../api'
import { useI18n } from '../i18n'

const { section } = useI18n()

const labels = section(
  {
    title: '', startDate: '', endDate: '', create: '', fileName: '', recordType: '',
    bank: '', branch: '', paymentDate: '', depositType: '', accountNumber: '',
    accountHolder: '', totalAmount: '', status: '', actions: '', download: '',
    createTitle: '', accountCodes: '', selectAccounts: '', senderBank: '', selectBank: '',
    paymentDateLabel: '', selectDate: '', postingDate: '', dueDate: '', search: '',
    payee: '', accountName: '', dueDateCol: '', amount: '', remarks: '', voucherNo: '',
    selectedCount: '', totalAmountLabel: '', cancel: '', createFile: '',
    aiSuggestion: '', noData: ''
  },
  (msg) => ({
    title: msg.fbPayment?.title || '自動支払',
    startDate: msg.common?.startDate || '開始日',
    endDate: msg.common?.endDate || '終了日',
    create: msg.fbPayment?.create || '新規登録',
    fileName: msg.fbPayment?.fileName || 'FBファイル',
    recordType: msg.fbPayment?.recordType || '種別コード',
    bank: msg.fbPayment?.bank || '銀行',
    branch: msg.fbPayment?.branch || '支店',
    paymentDate: msg.fbPayment?.paymentDate || '支払日',
    depositType: msg.fbPayment?.depositType || '預金種別',
    accountNumber: msg.fbPayment?.accountNumber || '口座番号',
    accountHolder: msg.fbPayment?.accountHolder || '口座名義人',
    totalAmount: msg.fbPayment?.totalAmount || '振込金額',
    status: msg.fbPayment?.status || 'ステータス',
    actions: msg.common?.actions || 'アクション',
    download: msg.common?.download || 'ダウンロード',
    createTitle: msg.fbPayment?.createTitle || '自動支払ファイル内容提案',
    accountCodes: msg.fbPayment?.accountCodes || '勘定科目',
    selectAccounts: msg.fbPayment?.selectAccounts || '科目を選択',
    senderBank: msg.fbPayment?.senderBank || '仕向銀行',
    selectBank: msg.fbPayment?.selectBank || '銀行を選択',
    paymentDateLabel: msg.fbPayment?.paymentDateLabel || '振込日付',
    selectDate: msg.fbPayment?.selectDate || '日付を選択',
    postingDate: msg.fbPayment?.postingDate || '債務伝票転記日',
    dueDate: msg.fbPayment?.dueDate || '支払予定日',
    search: msg.common?.search || '検索',
    payee: msg.fbPayment?.payee || '支払先',
    accountName: msg.fbPayment?.accountName || '勘定科目',
    dueDateCol: msg.fbPayment?.dueDateCol || '支払予定日',
    amount: msg.fbPayment?.amount || '金額',
    remarks: msg.fbPayment?.remarks || '備考',
    voucherNo: msg.fbPayment?.voucherNo || '会計伝票番号',
    selectedCount: msg.fbPayment?.selectedCount || '選択件数',
    totalAmountLabel: msg.fbPayment?.totalAmountLabel || '合計金額',
    cancel: msg.common?.cancel || 'キャンセル',
    createFile: msg.fbPayment?.createFile || '自動支払ファイル提案',
    aiSuggestion: msg.fbPayment?.aiSuggestion || 'AI 提案',
    noData: msg.common?.noData || 'データなし'
  })
)

// 列表状态
const files = ref<any[]>([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(50)
const total = ref(0)

const filters = reactive({
  dateRange: [] as string[]
})

// 新规登录弹窗状态
const createDialogVisible = ref(false)
const debtTableRef = ref<any>(null)
const pendingDebts = ref<any[]>([])
const debtLoading = ref(false)
const selectedItems = ref<any[]>([])
const aiSuggestion = ref('')

const createFilters = reactive({
  accountCodes: [] as string[],
  senderBankCode: '',
  paymentDate: '',
  postingDateRange: [] as string[],
  dueDateRange: [] as string[]
})

// 选项数据
const accountOptions = ref<{ label: string; value: string }[]>([])
const bankOptions = ref<{ label: string; value: string; data?: any }[]>([])
const senderBankInfo = ref<any>(null)

// 计算选中总金额
const selectedTotalAmount = computed(() => {
  return selectedItems.value.reduce((sum, item) => sum + (item.residualAmount || item.amount || 0), 0)
})

// 初始化
onMounted(() => {
  // 默认日期范围：当年
  const now = new Date()
  const startOfYear = new Date(now.getFullYear(), 0, 1).toISOString().slice(0, 10)
  const today = now.toISOString().slice(0, 10)
  filters.dateRange = [startOfYear, today]
  
  // 设置默认支付日期为明天
  const tomorrow = new Date(now.getTime() + 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
  createFilters.paymentDate = tomorrow
  
  load()
  loadBankOptions()
  loadDefaultAccountCodes()
})

// 加载FB文件列表
async function load() {
  loading.value = true
  try {
    const params: any = { page: page.value, pageSize: pageSize.value }
    if (filters.dateRange?.length === 2) {
      params.startDate = filters.dateRange[0]
      params.endDate = filters.dateRange[1]
    }
    const r = await api.post('/fb-payment/files', params)
    files.value = r.data?.data || []
    total.value = r.data?.total || 0
  } catch (e) {
    console.error('加载FB文件列表失败', e)
  } finally {
    loading.value = false
  }
}

// 加载银行选项
async function loadBankOptions() {
  try {
    const r = await api.post('/objects/account/search', {
      page: 1, pageSize: 100,
      where: [{ json: 'isBank', op: 'eq', value: true }],
      orderBy: [{ field: 'account_code', dir: 'ASC' }]
    })
    bankOptions.value = (r.data?.data || []).map((a: any) => ({
      label: `${a.payload?.name || a.account_code}`,
      value: a.account_code,
      data: a.payload
    }))
  } catch (e) {
    console.error('加载银行选项失败', e)
  }
}

// 加载默认债务科目
async function loadDefaultAccountCodes() {
  try {
    const r = await api.post('/objects/account/search', {
      page: 1, pageSize: 100,
      where: [{ json: 'openItem', op: 'eq', value: true }],
      orderBy: [{ field: 'account_code', dir: 'ASC' }]
    })
    accountOptions.value = (r.data?.data || []).map((a: any) => ({
      label: `${a.payload?.name || ''} (${a.account_code})`,
      value: a.account_code
    }))
    // 默认选中所有债务科目
    createFilters.accountCodes = accountOptions.value.map(a => a.value)
  } catch (e) {
    console.error('加载债务科目失败', e)
  }
}

// 搜索科目
async function searchAccounts(query: string) {
  const where: any[] = [{ json: 'openItem', op: 'eq', value: true }]
  if (query?.trim()) {
    where.push({ json: 'name', op: 'contains', value: query.trim() })
  }
  try {
    const r = await api.post('/objects/account/search', { page: 1, pageSize: 100, where, orderBy: [{ field: 'account_code', dir: 'ASC' }] })
    accountOptions.value = (r.data?.data || []).map((a: any) => ({
      label: `${a.payload?.name || ''} (${a.account_code})`,
      value: a.account_code
    }))
  } catch (e) {
    console.error('搜索科目失败', e)
  }
}

// 选择仕向银行
function onSenderBankChange(code: string) {
  const bank = bankOptions.value.find(b => b.value === code)
  senderBankInfo.value = bank?.data || null
}

// 打开新规登录弹窗
function openCreateDialog() {
  createDialogVisible.value = true
  pendingDebts.value = []
  selectedItems.value = []
  aiSuggestion.value = ''
}

// 加载待支付债务
async function loadPendingDebts() {
  debtLoading.value = true
  try {
    const params: any = {}
    if (createFilters.accountCodes.length > 0) {
      params.accountCodes = createFilters.accountCodes
    }
    if (createFilters.postingDateRange?.length === 2) {
      params.postingDateFrom = createFilters.postingDateRange[0]
      params.postingDateTo = createFilters.postingDateRange[1]
    }
    if (createFilters.dueDateRange?.length === 2) {
      params.dueDateFrom = createFilters.dueDateRange[0]
      params.dueDateTo = createFilters.dueDateRange[1]
    }
    const r = await api.post('/fb-payment/pending-debts', params)
    pendingDebts.value = r.data?.data || []
    
    // AI 建议：检测异常
    generateAiSuggestion()
  } catch (e) {
    console.error('加载待支付债务失败', e)
  } finally {
    debtLoading.value = false
  }
}

// 生成 AI 建议
function generateAiSuggestion() {
  if (pendingDebts.value.length === 0) {
    aiSuggestion.value = ''
    return
  }
  
  const suggestions: string[] = []
  
  // 检测重复支付风险
  const payeeGroups = new Map<string, any[]>()
  pendingDebts.value.forEach(item => {
    const payee = item.vendorName || item.employeeName || 'unknown'
    if (!payeeGroups.has(payee)) payeeGroups.set(payee, [])
    payeeGroups.get(payee)!.push(item)
  })
  
  payeeGroups.forEach((items, payee) => {
    if (items.length > 3) {
      suggestions.push(`「${payee}」への支払いが ${items.length} 件あります。まとめて処理することを推奨します。`)
    }
  })
  
  // 检测大额支付
  const largePayments = pendingDebts.value.filter(item => (item.residualAmount || item.amount) > 1000000)
  if (largePayments.length > 0) {
    suggestions.push(`100万円以上の高額支払いが ${largePayments.length} 件あります。承認フローをご確認ください。`)
  }
  
  // 检测过期支付
  const today = new Date().toISOString().slice(0, 10)
  const overduePayments = pendingDebts.value.filter(item => item.dueDate && item.dueDate < today)
  if (overduePayments.length > 0) {
    suggestions.push(`支払期限を過ぎた債務が ${overduePayments.length} 件あります。優先的に処理してください。`)
  }
  
  // 检测银行信息缺失
  const missingBankInfo = pendingDebts.value.filter(item => !item.bankCode || !item.accountNumber)
  if (missingBankInfo.length > 0) {
    suggestions.push(`${missingBankInfo.length} 件の支払先で銀行口座情報が不足しています。取引先マスタをご確認ください。`)
  }
  
  aiSuggestion.value = suggestions.join(' ')
}

// 选择变更
function onSelectionChange(selection: any[]) {
  selectedItems.value = selection
}

// 创建FB文件
async function createFbFile() {
  if (selectedItems.value.length === 0) {
    ElMessage.warning('支払い項目を選択してください')
    return
  }
  
  if (!createFilters.senderBankCode) {
    ElMessage.warning('仕向銀行を選択してください')
    return
  }
  
  if (!createFilters.paymentDate) {
    ElMessage.warning('振込日付を指定してください')
    return
  }
  
  try {
    const bankInfo = senderBankInfo.value || {}
    const items = selectedItems.value.map(item => ({
      voucherId: item.voucherId,
      voucherNo: item.voucherNo,
      lineNo: item.lineNo,
      amount: item.residualAmount || item.amount,
      bankCode: item.bankCode || '',
      bankName: '', // TODO: 从银行主数据获取
      branchCode: item.branchCode || '',
      branchName: '',
      depositType: item.depositType || '1',
      accountNumber: item.accountNumber || '',
      accountHolder: item.accountHolder || item.vendorName || item.employeeName || ''
    }))
    
    const r = await api.post('/fb-payment/create', {
      paymentDate: createFilters.paymentDate,
      bankCode: createFilters.senderBankCode,
      bankName: bankInfo.name || '',
      branchCode: bankInfo.branchCode || '',
      branchName: bankInfo.branchName || '',
      depositType: bankInfo.depositType || '1',
      accountNumber: bankInfo.accountNumber || '',
      accountHolder: bankInfo.accountHolder || '',
      items
    })
    
    ElMessage.success(`FBファイルを作成しました: ${r.data?.fileName}`)
    createDialogVisible.value = false
    load()
    
    // 自动下载
    if (r.data?.id) {
      downloadFileById(r.data.id, r.data.fileName)
    }
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'FBファイルの作成に失敗しました')
  }
}

// 下载文件
async function downloadFile(row: any) {
  downloadFileById(row.id, row.fileName)
}

async function downloadFileById(id: string, fileName: string) {
  try {
    const r = await api.get(`/fb-payment/download/${id}`, { responseType: 'blob' })
    const url = window.URL.createObjectURL(new Blob([r.data]))
    const link = document.createElement('a')
    link.href = url
    link.setAttribute('download', fileName)
    document.body.appendChild(link)
    link.click()
    link.remove()
    window.URL.revokeObjectURL(url)
  } catch (e) {
    ElMessage.error('ダウンロードに失敗しました')
  }
}

// 分页
function onPage(p: number) {
  page.value = p
  load()
}

// 格式化金额
function formatAmount(val: number | string | null | undefined): string {
  if (val === null || val === undefined) return '0'
  const num = typeof val === 'string' ? parseFloat(val) : val
  if (!Number.isFinite(num)) return '0'
  return num.toLocaleString('ja-JP', { minimumFractionDigits: 0, maximumFractionDigits: 0 })
}

// 获取预金种别名称
function getDepositTypeName(type: string): string {
  const types: Record<string, string> = {
    '1': '普通',
    '2': '当座',
    '4': '貯蓄',
    '9': 'その他'
  }
  return types[type] || type || '-'
}
</script>

<style scoped>
.page {
  padding: 20px;
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #e6a23c;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.filter-row {
  display: flex;
  gap: 16px;
  margin-bottom: 20px;
  align-items: center;
  justify-content: space-between;
}

.filter-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.filter-label {
  font-size: 13px;
  color: #606266;
  white-space: nowrap;
}

.filter-date-range {
  width: 280px;
}

.filter-actions {
  display: flex;
  gap: 8px;
}

.page-pagination {
  margin-top: 16px;
  display: flex;
  justify-content: flex-end;
}

/* 弹窗样式 */
.dialog-filters {
  margin-bottom: 16px;
}

.filter-section {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  margin-bottom: 12px;
  align-items: flex-end;
}

.filter-select-wide {
  width: 400px;
}

.filter-select {
  width: 200px;
}

.filter-date {
  width: 150px;
}

.ai-suggestion {
  margin-top: 16px;
}

.ai-content {
  font-size: 13px;
  line-height: 1.6;
}

.dialog-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 20px;
  padding-top: 16px;
  border-top: 1px solid #e5e7eb;
}

.summary {
  display: flex;
  gap: 24px;
  font-size: 14px;
}

.summary-amount {
  font-weight: 600;
  color: #409eff;
}

.actions {
  display: flex;
  gap: 8px;
}

:deep(.el-table th.el-table__cell) {
  background-color: #4a6fa5;
  color: #fff;
  font-weight: 500;
}

:deep(.el-table th.el-table__cell .cell) {
  color: #fff;
}

:deep(.create-dialog .el-dialog__body) {
  padding-top: 10px;
}
</style>



























