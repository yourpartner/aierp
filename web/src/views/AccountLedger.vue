<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Document /></el-icon>
            <span class="page-header-title">{{ labels.title }}</span>
            <el-tag v-if="rows.length > 0" size="small" type="info">{{ rows.length }}件</el-tag>
          </div>
        </div>
      </template>

      <!-- 筛选条件 -->
      <div class="filter-grid">
        <div class="filter-row">
          <div class="filter-item">
            <label class="filter-label">{{ labels.period }}</label>
            <el-date-picker
              v-model="filters.dateRange"
              type="daterange"
              :start-placeholder="labels.startDate"
              :end-placeholder="labels.endDate"
              value-format="YYYY-MM-DD"
              class="filter-date-range"
            />
          </div>
          <div class="filter-item">
            <label class="filter-label">{{ labels.account }}</label>
            <el-select
              v-model="filters.accountCodes"
              multiple
              filterable
              remote
              clearable
              collapse-tags
              collapse-tags-tooltip
              :remote-method="searchAccounts"
              :placeholder="labels.account"
              class="filter-select-account"
              @focus="() => searchAccounts('')"
            >
              <el-option v-for="a in accountOptions" :key="a.value" :label="a.label" :value="a.value" />
            </el-select>
          </div>
          <div class="filter-item">
            <label class="filter-label">{{ labels.search }}</label>
            <el-input v-model="filters.keyword" :placeholder="labels.searchPlaceholder" clearable class="filter-input" />
          </div>
        </div>
        <div class="filter-row">
          <div class="filter-item">
            <label class="filter-label">{{ labels.customer }}</label>
            <el-select
              v-model="filters.customerId"
              filterable
              remote
              clearable
              :remote-method="searchCustomers"
              :placeholder="labels.customer"
              class="filter-select"
              @focus="() => searchCustomers('')"
            >
              <el-option v-for="p in customerOptions" :key="p.value" :label="p.label" :value="p.value" />
            </el-select>
          </div>
          <div class="filter-item">
            <label class="filter-label">{{ labels.vendor }}</label>
            <el-select
              v-model="filters.vendorId"
              filterable
              remote
              clearable
              :remote-method="searchVendors"
              :placeholder="labels.vendor"
              class="filter-select"
              @focus="() => searchVendors('')"
            >
              <el-option v-for="p in vendorOptions" :key="p.value" :label="p.label" :value="p.value" />
            </el-select>
          </div>
          <div class="filter-item">
            <label class="filter-label">{{ labels.employee }}</label>
            <el-select
              v-model="filters.employeeId"
              filterable
              remote
              clearable
              :remote-method="searchEmployees"
              :placeholder="labels.employee"
              class="filter-select"
              @focus="() => searchEmployees('')"
            >
              <el-option v-for="e in employeeOptions" :key="e.value" :label="e.label" :value="e.value" />
            </el-select>
          </div>
          <div class="filter-actions">
            <el-button type="primary" @click="load" :loading="loading">{{ labels.searchBtn }}</el-button>
            <el-button @click="reset">{{ labels.resetBtn }}</el-button>
            <el-button type="success" @click="downloadExcel" :disabled="rows.length === 0">
              <el-icon><Download /></el-icon>
              {{ labels.download }}
            </el-button>
          </div>
        </div>
      </div>

      <!-- 数据表格 -->
      <el-table
        :data="rows"
        stripe
        border
        style="width: 100%"
        v-loading="loading"
        :default-sort="{ prop: 'postingDate', order: 'ascending' }"
        @sort-change="onSortChange"
      >
        <el-table-column prop="fiscalYear" :label="labels.fiscalYear" width="70" align="center" sortable="custom" />
        <el-table-column prop="fiscalMonth" :label="labels.fiscalMonth" width="70" align="center" sortable="custom" />
        <el-table-column prop="postingDate" :label="labels.postingDate" width="110" align="center" sortable="custom">
          <template #default="{ row }">{{ row.postingDate?.slice(0, 10) || '-' }}</template>
        </el-table-column>
        <el-table-column prop="voucherNo" :label="labels.voucherNo" width="130" sortable="custom">
          <template #default="{ row }">
            <el-link v-if="row.voucherNo" type="primary" @click="openVoucher(row)">{{ row.voucherNo }}</el-link>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column prop="lineNo" :label="labels.lineNo" width="70" align="center" />
        <el-table-column prop="drcr" :label="labels.drcr" width="60" align="center">
          <template #default="{ row }">
            <el-tag :type="row.drcr === 'DR' ? 'danger' : 'success'" size="small">{{ row.drcr === 'DR' ? '借' : '貸' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="accountCode" :label="labels.accountCode" width="160">
          <template #default="{ row }">
            <span>{{ row.accountName ? `${row.accountName} (${row.accountCode})` : row.accountCode }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="amount" :label="labels.amount" width="120" align="right" sortable="custom">
          <template #default="{ row }">{{ formatAmount(row.amount) }}</template>
        </el-table-column>
        <el-table-column prop="balance" :label="labels.balance" width="120" align="right">
          <template #default="{ row }">
            <span :class="{ 'negative-balance': row.balance < 0 }">{{ formatAmount(row.balance) }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="headerText" :label="labels.headerText" min-width="160" show-overflow-tooltip />
        <el-table-column prop="lineText" :label="labels.lineText" min-width="140" show-overflow-tooltip />
        <el-table-column prop="customerName" :label="labels.customer" width="120" show-overflow-tooltip />
        <el-table-column prop="vendorName" :label="labels.vendor" width="120" show-overflow-tooltip />
        <el-table-column prop="departmentName" :label="labels.department" width="100" show-overflow-tooltip />
        <el-table-column prop="employeeName" :label="labels.employeeName" width="100" show-overflow-tooltip />
        <el-table-column prop="dueDate" :label="labels.dueDate" width="110" align="center">
          <template #default="{ row }">{{ row.dueDate || '-' }}</template>
        </el-table-column>
        <el-table-column prop="clearingStatus" :label="labels.clearingStatus" width="100" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.clearingStatus === 'cleared'" type="success" size="small">{{ labels.cleared }}</el-tag>
            <el-tag v-else-if="row.clearingStatus === 'partial'" type="warning" size="small">{{ labels.partial }}</el-tag>
            <el-tag v-else-if="row.clearingStatus === 'open'" type="info" size="small">{{ labels.open }}</el-tag>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column prop="clearingVoucherNo" :label="labels.clearingVoucherNo" width="130" show-overflow-tooltip>
          <template #default="{ row }">{{ row.clearingVoucherNo || '-' }}</template>
        </el-table-column>
        <el-table-column prop="clearingDate" :label="labels.clearingDate" width="110" align="center">
          <template #default="{ row }">{{ row.clearingDate || '-' }}</template>
        </el-table-column>
        <el-table-column prop="updatedAt" :label="labels.updatedAt" width="140" align="center">
          <template #default="{ row }">{{ row.updatedAt || '-' }}</template>
        </el-table-column>
        <el-table-column prop="createdBy" :label="labels.createdBy" width="100" show-overflow-tooltip>
          <template #default="{ row }">{{ displayCreatedBy(row.createdBy) }}</template>
        </el-table-column>
      </el-table>

      <!-- 分页 -->
      <div class="page-pagination">
        <el-pagination
          background
          layout="prev, pager, next, sizes, total"
          :total="total"
          :page-size="pageSize"
          :current-page="page"
          :page-sizes="[50, 100, 200, 500]"
          @update:page-size="onPageSize"
          @update:current-page="onPage"
        />
      </div>
    </el-card>

    <!-- 凭证详情弹窗 -->
    <el-dialog v-model="voucherDialogVisible" width="auto" append-to-body destroy-on-close class="voucher-detail-dialog">
      <template #header></template>
      <!-- 这里不做 embed 样式覆盖，完全复用 VouchersList 的标准详情显示 -->
      <div>
        <VouchersList
          v-if="voucherDialogVisible"
          :allow-edit="false"
          :initial-voucher-id="voucherDialogVoucherId || undefined"
          :initial-voucher-no="voucherDialogVoucherNo || undefined"
        />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { Download, Document } from '@element-plus/icons-vue'
import * as XLSX from 'xlsx'
import api from '../api'
import { useI18n } from '../i18n'
import VouchersList from './VouchersList.vue'

const { section } = useI18n()

const labels = section(
  {
    title: '', period: '', startDate: '', endDate: '', account: '', search: '', searchPlaceholder: '',
    customer: '', vendor: '', employee: '', searchBtn: '', resetBtn: '', download: '',
    fiscalYear: '', fiscalMonth: '', postingDate: '', voucherNo: '', lineNo: '', drcr: '',
    accountCode: '', amount: '', balance: '', headerText: '', lineText: '', noData: '',
    department: '', employeeName: '', dueDate: '', clearingStatus: '', clearingVoucherNo: '',
    clearingDate: '', updatedAt: '', createdBy: '', cleared: '', partial: '', open: ''
  },
  (msg) => ({
    title: msg.accountLedger?.title || '勘定明細一覧',
    period: msg.accountLedger?.period || '期間',
    startDate: msg.accountLedger?.startDate || '開始日',
    endDate: msg.accountLedger?.endDate || '終了日',
    account: msg.accountLedger?.account || '勘定科目',
    search: msg.accountLedger?.search || '検索',
    searchPlaceholder: msg.accountLedger?.searchPlaceholder || 'テキスト',
    customer: msg.accountLedger?.customer || '得意先',
    vendor: msg.accountLedger?.vendor || '仕入先',
    employee: msg.accountLedger?.employee || '社員',
    searchBtn: msg.accountLedger?.searchBtn || '検索',
    resetBtn: msg.accountLedger?.resetBtn || 'リセット',
    download: msg.accountLedger?.download || '明細一覧ダウンロード',
    fiscalYear: msg.accountLedger?.fiscalYear || '年度',
    fiscalMonth: msg.accountLedger?.fiscalMonth || '月度',
    postingDate: msg.accountLedger?.postingDate || '転記日付',
    voucherNo: msg.accountLedger?.voucherNo || '会計伝票',
    lineNo: msg.accountLedger?.lineNo || '行番号',
    drcr: msg.accountLedger?.drcr || '貸借',
    accountCode: msg.accountLedger?.accountCode || '勘定科目',
    amount: msg.accountLedger?.amount || '金額',
    balance: msg.accountLedger?.balance || '残高',
    headerText: msg.accountLedger?.headerText || 'ヘッダテキスト',
    lineText: msg.accountLedger?.lineText || '明細テキスト',
    noData: msg.accountLedger?.noData || 'データなし',
    department: msg.accountLedger?.department || '部門',
    employeeName: msg.accountLedger?.employeeName || '従業員',
    dueDate: msg.accountLedger?.dueDate || '支払予定日',
    clearingStatus: msg.accountLedger?.clearingStatus || '消込ステータス',
    clearingVoucherNo: msg.accountLedger?.clearingVoucherNo || '消込伝票番号',
    clearingDate: msg.accountLedger?.clearingDate || '消込日付',
    updatedAt: msg.accountLedger?.updatedAt || '更新日付',
    createdBy: msg.accountLedger?.createdBy || '登録者',
    cleared: msg.accountLedger?.cleared || '消込済',
    partial: msg.accountLedger?.partial || '一部消込',
    open: msg.accountLedger?.open || '未消込'
  })
)

// 筛选条件
const filters = reactive({
  dateRange: [] as string[],
  accountCodes: [] as string[],
  keyword: '',
  customerId: '',
  vendorId: '',
  employeeId: ''
})

// 选项数据
const accountOptions = ref<{ label: string; value: string }[]>([])
const customerOptions = ref<{ label: string; value: string }[]>([])
const vendorOptions = ref<{ label: string; value: string }[]>([])
const employeeOptions = ref<{ label: string; value: string }[]>([])

// 表格数据
const rows = ref<any[]>([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(100)
const total = ref(0)
const sortField = ref('postingDate')
const sortOrder = ref<'ASC' | 'DESC'>('ASC')

// 缓存
const accountCache = new Map<string, string>()
const partnerCache = new Map<string, { name: string; type: string }>()

// 用户缓存：用于把 createdBy(Guid) 映射到 登录ID(code) + 姓名(name)
const userMap = reactive<Record<string, { employeeCode?: string; name?: string }>>({})
const userIndexLoaded = ref(false)
const userIndexLoading = ref(false)
const userFetchInFlight = new Set<string>()

function isUuid(val: string) {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test((val || '').trim())
}

function formatUserDisplay(user: { employeeCode?: string; name?: string } | undefined) {
  if (!user) return ''
  const code = (user.employeeCode || '').trim()
  const name = (user.name || '').trim()
  if (code && name) return `${code} ${name}`
  return code || name || ''
}

async function loadUsersIndex() {
  if (userIndexLoaded.value || userIndexLoading.value) return
  userIndexLoading.value = true
  try {
    const res = await api.get('/api/users', { params: { offset: 0, limit: 2000 } })
    const users = Array.isArray(res.data?.users) ? res.data.users : []
    users.forEach((u: any) => {
      const id = String(u?.id || '').trim()
      if (!id) return
      userMap[id] = {
        employeeCode: String(u?.employeeCode || '').trim(),
        name: String(u?.name || '').trim()
      }
    })
    userIndexLoaded.value = true
  } catch (e) {
    // 静默失败：不影响主流程，只是无法把 GUID 显示为用户信息
    console.warn('加载用户索引失败', e)
  } finally {
    userIndexLoading.value = false
  }
}

async function ensureUsers(ids: string[]) {
  const unique = Array.from(new Set((ids || []).map((x) => String(x || '').trim()).filter((x) => isUuid(x))))
  const targets = unique.filter((id) => !userMap[id] && !userFetchInFlight.has(id))
  if (targets.length === 0) return
  targets.forEach((id) => userFetchInFlight.add(id))
  await Promise.all(
    targets.map(async (id) => {
      try {
        const res = await api.get(`/api/users/${id}`)
        const u = res.data || {}
        userMap[id] = {
          employeeCode: String(u?.employeeCode || '').trim(),
          name: String(u?.name || '').trim()
        }
      } catch {
        // ignore
      } finally {
        userFetchInFlight.delete(id)
      }
    })
  )
}

function displayCreatedBy(val: any) {
  const raw = String(val ?? '').trim()
  if (!raw) return '-'
  if (!isUuid(raw)) return raw
  const display = formatUserDisplay(userMap[raw])
  if (display) return display
  // 触发一次后台补全
  void ensureUsers([raw])
  return raw
}

// 凭证弹窗
const voucherDialogVisible = ref(false)
const voucherDialogVoucherId = ref<string>('')
const voucherDialogVoucherNo = ref<string>('')

// 初始化日期范围（当月）
onMounted(() => {
  const now = new Date()
  const year = now.getFullYear()
  const month = now.getMonth()
  const firstDay = new Date(year, month, 1).toISOString().slice(0, 10)
  const lastDay = new Date(year, month + 1, 0).toISOString().slice(0, 10)
  filters.dateRange = [firstDay, lastDay]
  // 预加载用户索引，避免 createdBy 暂时显示为 GUID
  void loadUsersIndex()
})

// 搜索科目
async function searchAccounts(query: string) {
  const where: any[] = []
  if (query?.trim()) {
    where.push({ json: 'name', op: 'contains', value: query.trim() })
  }
  try {
    const r = await api.post('/objects/account/search', { page: 1, pageSize: 100, where, orderBy: [{ field: 'account_code', dir: 'ASC' }] })
    accountOptions.value = (r.data?.data || []).map((a: any) => {
      const code = a.account_code
      const name = a.payload?.name || code
      accountCache.set(code, name)
      return { label: `${name} (${code})`, value: code }
    })
  } catch (e) {
    console.error('搜索科目失败', e)
  }
}

// 搜索得意先
async function searchCustomers(query: string) {
  const where: any[] = [{ field: 'flag_customer', op: 'eq', value: true }]
  if (query?.trim()) {
    where.push({ json: 'name', op: 'contains', value: query.trim() })
  }
  try {
    const r = await api.post('/objects/businesspartner/search', { page: 1, pageSize: 50, where, orderBy: [] })
    customerOptions.value = (r.data?.data || []).map((p: any) => {
      const code = p.partner_code
      const name = p.payload?.name || code
      partnerCache.set(code, { name, type: 'customer' })
      return { label: `${name} (${code})`, value: code }
    })
  } catch (e) {
    console.error('搜索得意先失败', e)
  }
}

// 搜索仕入先
async function searchVendors(query: string) {
  const where: any[] = [{ field: 'flag_vendor', op: 'eq', value: true }]
  if (query?.trim()) {
    where.push({ json: 'name', op: 'contains', value: query.trim() })
  }
  try {
    const r = await api.post('/objects/businesspartner/search', { page: 1, pageSize: 50, where, orderBy: [] })
    vendorOptions.value = (r.data?.data || []).map((p: any) => {
      const code = p.partner_code
      const name = p.payload?.name || code
      partnerCache.set(code, { name, type: 'vendor' })
      return { label: `${name} (${code})`, value: code }
    })
  } catch (e) {
    console.error('搜索仕入先失败', e)
  }
}

// 搜索社员
async function searchEmployees(query: string) {
  const where: any[] = []
  if (query?.trim()) {
    where.push({
      anyOf: [
        { json: 'nameKanji', op: 'contains', value: query.trim() },
        { json: 'nameKana', op: 'contains', value: query.trim() },
        { field: 'employee_code', op: 'contains', value: query.trim() }
      ]
    })
  }
  try {
    const r = await api.post('/objects/employee/search', { page: 1, pageSize: 50, where, orderBy: [{ field: 'employee_code', dir: 'ASC' }] })
    employeeOptions.value = (r.data?.data || []).map((e: any) => {
      const id = e.id
      const code = e.employee_code || ''
      const name = e.payload?.nameKanji || e.payload?.name || ''
      const label = name && code ? `${name} (${code})` : name || code || id
      return { label, value: id }
    })
  } catch (e) {
    console.error('搜索社员失败', e)
  }
}

// 加载数据
async function load() {
  loading.value = true
  try {
    // 构建查询条件
    const params: any = {
      page: page.value,
      pageSize: pageSize.value,
      sortField: sortField.value,
      sortOrder: sortOrder.value
    }

    if (filters.dateRange?.length === 2) {
      params.startDate = filters.dateRange[0]
      params.endDate = filters.dateRange[1]
    }
    if (filters.accountCodes?.length > 0) {
      params.accountCodes = filters.accountCodes
    }
    if (filters.keyword?.trim()) {
      params.keyword = filters.keyword.trim()
    }
    if (filters.customerId) {
      params.customerId = filters.customerId
    }
    if (filters.vendorId) {
      params.vendorId = filters.vendorId
    }
    if (filters.employeeId) {
      params.employeeId = filters.employeeId
    }

    const r = await api.post('/reports/account-ledger', params)
    const data = r.data?.data || []
    total.value = r.data?.total || data.length

    // 预取创建人信息（不阻塞主流程）
    try {
      const ids = data.map((x: any) => String(x?.createdBy ?? '').trim()).filter((x: string) => isUuid(x))
      void ensureUsers(ids)
    } catch {}

    // 处理数据，补充科目名称和取引先名称
    rows.value = data.map((item: any) => {
      const accountName = accountCache.get(item.accountCode) || ''
      const customerInfo = item.customerId ? partnerCache.get(item.customerId) : null
      const vendorInfo = item.vendorId ? partnerCache.get(item.vendorId) : null

      return {
        ...item,
        accountName,
        customerName: customerInfo?.name || item.customerName || '',
        vendorName: vendorInfo?.name || item.vendorName || ''
      }
    })
  } catch (e) {
    console.error('加载勘定明細失败', e)
    rows.value = []
    total.value = 0
  } finally {
    loading.value = false
  }
}

// 重置筛选
function reset() {
  const now = new Date()
  const year = now.getFullYear()
  const month = now.getMonth()
  filters.dateRange = [
    new Date(year, month, 1).toISOString().slice(0, 10),
    new Date(year, month + 1, 0).toISOString().slice(0, 10)
  ]
  filters.accountCodes = []
  filters.keyword = ''
  filters.customerId = ''
  filters.vendorId = ''
  filters.employeeId = ''
  page.value = 1
  rows.value = []
  total.value = 0
}

// 排序变化
function onSortChange({ prop, order }: { prop: string; order: string | null }) {
  sortField.value = prop || 'postingDate'
  sortOrder.value = order === 'descending' ? 'DESC' : 'ASC'
  load()
}

// 分页变化
function onPage(p: number) {
  page.value = p
  load()
}

function onPageSize(s: number) {
  pageSize.value = s
  page.value = 1
  load()
}

// 格式化金额
function formatAmount(val: number | string | null | undefined): string {
  if (val === null || val === undefined) return '-'
  const num = typeof val === 'string' ? parseFloat(val) : val
  if (!Number.isFinite(num)) return '-'
  return num.toLocaleString('ja-JP', { minimumFractionDigits: 0, maximumFractionDigits: 0 })
}

// 打开凭证详情
function openVoucher(row: any) {
  const voucherNo = row.voucherNo
  const voucherId = row.voucherId
  if (!voucherNo && !voucherId) return

  voucherDialogVoucherId.value = voucherId || ''
  voucherDialogVoucherNo.value = voucherNo || ''
  voucherDialogVisible.value = true
}

// 下载 Excel
function downloadExcel() {
  if (rows.value.length === 0) return

  // 构建表头和数据（直接使用画面显示的内容）
  const headers = [
    labels.value.fiscalYear,
    labels.value.fiscalMonth,
    labels.value.postingDate,
    labels.value.voucherNo,
    labels.value.lineNo,
    labels.value.drcr,
    labels.value.accountCode,
    labels.value.amount,
    labels.value.balance,
    labels.value.headerText,
    labels.value.lineText,
    labels.value.customer,
    labels.value.vendor,
    labels.value.department,
    labels.value.employeeName,
    labels.value.dueDate,
    labels.value.clearingStatus,
    labels.value.clearingVoucherNo,
    labels.value.clearingDate,
    labels.value.updatedAt,
    labels.value.createdBy
  ]

  const clearingStatusText = (status: string | null) => {
    if (status === 'cleared') return labels.value.cleared
    if (status === 'partial') return labels.value.partial
    if (status === 'open') return labels.value.open
    return ''
  }

  const data = rows.value.map(row => [
    row.fiscalYear || '',
    row.fiscalMonth || '',
    row.postingDate?.slice(0, 10) || '',
    row.voucherNo || '',
    row.lineNo || '',
    row.drcr === 'DR' ? '借方' : '貸方',
    row.accountName ? `${row.accountName} (${row.accountCode})` : row.accountCode || '',
    row.amount || 0,
    row.balance || 0,
    row.headerText || '',
    row.lineText || '',
    row.customerName || '',
    row.vendorName || '',
    row.departmentName || '',
    row.employeeName || '',
    row.dueDate || '',
    clearingStatusText(row.clearingStatus),
    row.clearingVoucherNo || '',
    row.clearingDate || '',
    row.updatedAt || '',
    displayCreatedBy(row.createdBy) === '-' ? '' : displayCreatedBy(row.createdBy)
  ])

  // 创建工作簿
  const worksheet = XLSX.utils.aoa_to_sheet([headers, ...data])

  // 设置列宽
  worksheet['!cols'] = [
    { wch: 8 },   // 年度
    { wch: 8 },   // 月度
    { wch: 12 },  // 転記日付
    { wch: 15 },  // 会計伝票
    { wch: 8 },   // 行番号
    { wch: 8 },   // 貸借
    { wch: 25 },  // 勘定科目
    { wch: 15 },  // 金額
    { wch: 15 },  // 残高
    { wch: 30 },  // ヘッダテキスト
    { wch: 25 },  // 明細テキスト
    { wch: 15 },  // 得意先
    { wch: 15 },  // 仕入先
    { wch: 12 },  // 部門
    { wch: 12 },  // 従業員
    { wch: 12 },  // 支払予定日
    { wch: 12 },  // 消込ステータス
    { wch: 15 },  // 消込伝票番号
    { wch: 12 },  // 消込日付
    { wch: 16 },  // 更新日付
    { wch: 12 }   // 登録者
  ]

  const workbook = XLSX.utils.book_new()
  XLSX.utils.book_append_sheet(workbook, worksheet, '勘定明細')

  // 生成文件名
  const dateStr = filters.dateRange?.length === 2 ? `${filters.dateRange[0]}_${filters.dateRange[1]}` : new Date().toISOString().slice(0, 10)
  const fileName = `勘定明細一覧_${dateStr}.xlsx`

  XLSX.writeFile(workbook, fileName)
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
  color: #909399;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.filter-grid {
  margin-bottom: 20px;
}

.filter-grid {
  margin-bottom: 20px;
}

.filter-row {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  margin-bottom: 12px;
  align-items: flex-end;
}

/* 卡片圆角 */
:deep(.el-card) {
  border-radius: 12px;
  overflow: hidden;
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

.filter-select-account {
  width: 240px;
}

.filter-select {
  width: 180px;
}

.filter-input {
  width: 180px;
}

.filter-actions {
  display: flex;
  gap: 8px;
  margin-left: auto;
}

.page-pagination {
  margin-top: 16px;
  display: flex;
  justify-content: flex-end;
}

.negative-balance {
  color: #f56c6c;
}

/* 凭证弹窗样式 */
.voucher-dialog-card-wrap {
  min-width: 800px;
  max-width: 1200px;
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

:deep(.el-table .cell) {
  padding: 0 8px;
}

:deep(.el-table th.el-table__cell) {
  background-color: #f5f7fa !important;
  color: #303133 !important;
  font-weight: 600;
}
</style>


