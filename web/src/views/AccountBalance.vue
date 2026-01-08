<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Coin /></el-icon>
            <span class="page-header-title">{{ labels.title }}</span>
          </div>
        </div>
      </template>

      <!-- 筛选条件 -->
      <div class="filter-row">
        <div class="filter-item">
          <el-date-picker
            v-model="filters.year"
            type="year"
            value-format="YYYY"
            :placeholder="labels.year"
            class="filter-year"
          />
        </div>
        <div class="filter-item">
          <el-select
            v-model="filters.accountCode"
            filterable
            remote
            clearable
            :remote-method="searchAccounts"
            :placeholder="labels.account"
            class="filter-select-account"
            @focus="() => searchAccounts('')"
            @change="load"
          >
            <el-option v-for="a in accountOptions" :key="a.value" :label="a.label" :value="a.value" />
          </el-select>
        </div>
      </div>

      <!-- 余额表格 -->
      <el-table
        :data="rows"
        stripe
        border
        style="width: 100%"
        v-loading="loading"
        :row-class-name="getRowClass"
      >
        <el-table-column prop="yearMonth" :label="labels.yearMonth" width="120" align="left" />
        <el-table-column prop="drAmount" :label="labels.drAmount" width="180" align="right">
          <template #default="{ row }">
            <el-link 
              v-if="row.month > 0 && row.drAmount !== 0" 
              type="primary" 
              @click="showDetails(row, 'DR')"
            >{{ formatAmount(row.drAmount) }}</el-link>
            <span v-else>{{ formatAmount(row.drAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="crAmount" :label="labels.crAmount" width="180" align="right">
          <template #default="{ row }">
            <el-link 
              v-if="row.month > 0 && row.crAmount !== 0" 
              type="primary" 
              @click="showDetails(row, 'CR')"
            >{{ formatAmount(row.crAmount) }}</el-link>
            <span v-else>{{ formatAmount(row.crAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="monthBalance" :label="labels.monthBalance" width="180" align="right">
          <template #default="{ row }">
            <el-link 
              v-if="row.month > 0 && row.monthBalance !== 0" 
              type="primary" 
              @click="showDetails(row, 'ALL')"
            >
              <span :class="{ 'negative-balance': row.monthBalance < 0 }">{{ formatAmount(row.monthBalance) }}</span>
            </el-link>
            <span v-else :class="{ 'negative-balance': row.monthBalance < 0 }">{{ formatAmount(row.monthBalance) }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="cumulativeBalance" :label="labels.cumulativeBalance" width="180" align="right">
          <template #default="{ row }">
            <span :class="{ 'negative-balance': row.cumulativeBalance < 0 }">{{ formatAmount(row.cumulativeBalance) }}</span>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 凭证明细弹窗 -->
    <el-dialog 
      v-model="detailDialogVisible" 
      width="auto" 
      append-to-body 
      destroy-on-close
      class="voucher-detail-dialog"
    >
      <template #header></template>
      <div class="voucher-dialog-card-wrap detail-card-wrap">
        <el-card class="detail-card">
          <template #header>
            <div class="page-header">
              <div class="page-header-left">
                <el-icon class="page-header-icon" style="color: #409eff;"><Coin /></el-icon>
                <span class="page-header-title">{{ labels.detailTitle }}</span>
              </div>
            </div>
          </template>
          <el-table
            :data="detailRows"
            stripe
            border
            style="width: 100%"
            v-loading="detailLoading"
            max-height="500"
          >
            <el-table-column prop="voucherNo" :label="labels.voucherNo" width="120">
              <template #default="{ row }">
                <el-link v-if="row.voucherNo" type="primary" @click="openVoucher(row)">{{ row.voucherNo }}</el-link>
                <span v-else>-</span>
              </template>
            </el-table-column>
            <el-table-column prop="postingDate" :label="labels.postingDate" width="110" align="center">
              <template #default="{ row }">{{ row.postingDate?.slice(0, 10) || '-' }}</template>
            </el-table-column>
            <el-table-column prop="lineNo" :label="labels.lineNo" width="70" align="center" />
            <el-table-column prop="drcr" :label="labels.drcr" width="60" align="center">
              <template #default="{ row }">
                <el-tag :type="row.drcr === 'DR' ? 'danger' : 'success'" size="small">{{ row.drcr === 'DR' ? '借' : '貸' }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="amount" :label="labels.amount" width="120" align="right">
              <template #default="{ row }">
                <span :class="{ 'negative-amount': row.drcr === 'CR' }">{{ row.drcr === 'CR' ? '-' : '' }}{{ formatAmount(row.amount) }}</span>
              </template>
            </el-table-column>
            <el-table-column prop="headerText" :label="labels.headerText" min-width="180" show-overflow-tooltip />
            <el-table-column prop="customerName" :label="labels.customer" width="100" show-overflow-tooltip />
            <el-table-column prop="vendorName" :label="labels.vendor" width="100" show-overflow-tooltip />
            <el-table-column prop="departmentName" :label="labels.department" width="80" show-overflow-tooltip />
            <el-table-column prop="employeeName" :label="labels.employee" width="80" show-overflow-tooltip />
            <el-table-column prop="dueDate" :label="labels.dueDate" width="100" align="center">
              <template #default="{ row }">{{ row.dueDate || '-' }}</template>
            </el-table-column>
            <el-table-column prop="clearingStatus" :label="labels.clearingStatus" width="90" align="center">
              <template #default="{ row }">
                <el-tag v-if="row.clearingStatus === 'cleared'" type="success" size="small">{{ labels.cleared }}</el-tag>
                <el-tag v-else-if="row.clearingStatus === 'partial'" type="warning" size="small">{{ labels.partial }}</el-tag>
                <el-tag v-else-if="row.clearingStatus === 'open'" type="info" size="small">{{ labels.open }}</el-tag>
                <span v-else>-</span>
              </template>
            </el-table-column>
            <el-table-column prop="clearingVoucherNo" :label="labels.clearingVoucherNo" width="110" show-overflow-tooltip>
              <template #default="{ row }">{{ row.clearingVoucherNo || '-' }}</template>
            </el-table-column>
            <el-table-column prop="clearingDate" :label="labels.clearingDate" width="100" align="center">
              <template #default="{ row }">{{ row.clearingDate || '-' }}</template>
            </el-table-column>
          </el-table>
        </el-card>
      </div>
    </el-dialog>

    <!-- 凭证详情弹窗 -->
    <el-dialog v-model="voucherDialogVisible" width="auto" append-to-body destroy-on-close class="voucher-detail-dialog">
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VouchersList v-if="voucherDialogVisible" ref="voucherDetailRef" class="voucher-detail-embed" :allow-edit="false" />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, watch, nextTick } from 'vue'
import { Coin } from '@element-plus/icons-vue'
import api from '../api'
import { useI18n } from '../i18n'
import VouchersList from './VouchersList.vue'

const { section } = useI18n()

const labels = section(
  {
    title: '', year: '', account: '', yearMonth: '', drAmount: '', crAmount: '',
    monthBalance: '', cumulativeBalance: '', detailTitle: '', voucherNo: '', postingDate: '',
    lineNo: '', drcr: '', amount: '', headerText: '', customer: '', vendor: '',
    department: '', employee: '', dueDate: '', clearingStatus: '', clearingVoucherNo: '',
    clearingDate: '', cleared: '', partial: '', open: ''
  },
  (msg) => ({
    title: msg.accountBalance?.title || '勘定残高',
    year: msg.accountBalance?.year || '年度',
    account: msg.accountBalance?.account || '勘定科目',
    yearMonth: msg.accountBalance?.yearMonth || '年月',
    drAmount: msg.accountBalance?.drAmount || '借方発生額',
    crAmount: msg.accountBalance?.crAmount || '貸方発生額',
    monthBalance: msg.accountBalance?.monthBalance || '月度残高',
    cumulativeBalance: msg.accountBalance?.cumulativeBalance || '累計残高',
    detailTitle: msg.accountBalance?.detailTitle || '勘定明細一覧',
    voucherNo: msg.accountLedger?.voucherNo || '会計伝票',
    postingDate: msg.accountLedger?.postingDate || '転記日付',
    lineNo: msg.accountLedger?.lineNo || '行番号',
    drcr: msg.accountLedger?.drcr || '貸借',
    amount: msg.accountLedger?.amount || '金額',
    headerText: msg.accountLedger?.headerText || 'ヘッダテキスト',
    customer: msg.accountLedger?.customer || '得意先',
    vendor: msg.accountLedger?.vendor || '仕入先',
    department: msg.accountLedger?.department || '部門',
    employee: msg.accountLedger?.employee || '社員',
    dueDate: msg.accountLedger?.dueDate || '支払予定日',
    clearingStatus: msg.accountLedger?.clearingStatus || '消込ステータス',
    clearingVoucherNo: msg.accountLedger?.clearingVoucherNo || '消込伝票番号',
    clearingDate: msg.accountLedger?.clearingDate || '消込日付',
    cleared: msg.accountLedger?.cleared || '消込済',
    partial: msg.accountLedger?.partial || '一部消込',
    open: msg.accountLedger?.open || '未消込'
  })
)

// 筛选条件
const filters = reactive({
  year: new Date().getFullYear().toString(),
  accountCode: ''
})

// 科目选项
const accountOptions = ref<{ label: string; value: string }[]>([])

// 余额表格数据
const rows = ref<any[]>([])
const loading = ref(false)

// 凭证明细弹窗
const detailDialogVisible = ref(false)
const detailRows = ref<any[]>([])
const detailLoading = ref(false)

// 凭证详情弹窗
const voucherDialogVisible = ref(false)
const voucherDetailRef = ref<InstanceType<typeof VouchersList> | null>(null)

// 初始化
onMounted(() => {
  // 默认当前年度
  filters.year = new Date().getFullYear().toString()
})

// 监听年度变化
watch(() => filters.year, () => {
  if (filters.accountCode) {
    load()
  }
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
      return { label: `${code} ${name}`, value: code }
    })
  } catch (e) {
    console.error('搜索科目失败', e)
  }
}

// 加载余额数据
async function load() {
  if (!filters.accountCode) {
    rows.value = []
    return
  }

  loading.value = true
  try {
    const params = {
      year: parseInt(filters.year),
      accountCode: filters.accountCode
    }
    const r = await api.post('/reports/account-balance', params)
    rows.value = r.data?.data || []
  } catch (e) {
    console.error('加载勘定残高失败', e)
    rows.value = []
  } finally {
    loading.value = false
  }
}

// 获取行样式
function getRowClass({ row }: { row: any }) {
  if (row.month === 0) return 'carry-forward-row'
  return ''
}

// 显示凭证明细
async function showDetails(row: any, type: 'DR' | 'CR' | 'ALL') {
  if (row.month === 0) return // 年度繰越行不可点击

  detailDialogVisible.value = true
  detailLoading.value = true
  detailRows.value = []

  try {
    // 计算月份对应的日期范围
    let year = parseInt(filters.year)
    let month = row.month
    if (month > 12) {
      year += 1
      month -= 12
    }
    
    const startDate = `${year}-${String(month).padStart(2, '0')}-01`
    const lastDay = new Date(year, month, 0).getDate()
    const endDate = `${year}-${String(month).padStart(2, '0')}-${lastDay}`

    const params: any = {
      startDate,
      endDate,
      accountCodes: [filters.accountCode],
      page: 1,
      pageSize: 500
    }

    const r = await api.post('/reports/account-ledger', params)
    let data = r.data?.data || []

    // 根据类型过滤
    if (type === 'DR') {
      data = data.filter((item: any) => item.drcr === 'DR')
    } else if (type === 'CR') {
      data = data.filter((item: any) => item.drcr === 'CR')
    }

    detailRows.value = data
  } catch (e) {
    console.error('加载凭证明细失败', e)
  } finally {
    detailLoading.value = false
  }
}

// 打开凭证详情
function openVoucher(row: any) {
  const voucherNo = row.voucherNo
  const voucherId = row.voucherId
  if (!voucherNo && !voucherId) return

  voucherDialogVisible.value = true
  nextTick(() => {
    const isUuid = voucherId && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(voucherId)
    voucherDetailRef.value?.applyIntent?.({
      ...(isUuid ? { voucherId } : { voucherNo }),
      detailOnly: true
    })
  })
}

// 格式化金额
function formatAmount(val: number | string | null | undefined): string {
  if (val === null || val === undefined) return '0'
  const num = typeof val === 'string' ? parseFloat(val) : val
  if (!Number.isFinite(num)) return '0'
  return num.toLocaleString('ja-JP', { minimumFractionDigits: 0, maximumFractionDigits: 0 })
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
}

/* 卡片圆角 */
:deep(.el-card) {
  border-radius: 12px;
  overflow: hidden;
}

.filter-item {
  display: flex;
  align-items: center;
}

.filter-year {
  width: 140px;
}

.filter-select-account {
  width: 280px;
}

.negative-balance {
  color: #f56c6c;
}

.negative-amount {
  color: #f56c6c;
}

.carry-forward-row {
  background-color: #f0f9eb !important;
  font-weight: 500;
}

:deep(.el-table .carry-forward-row td) {
  background-color: #f0f9eb !important;
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

:deep(.el-table th.el-table__cell) {
  background-color: #f5f7fa !important;
  color: #303133 !important;
  font-weight: 600;
}

.detail-card-wrap {
  max-width: 95vw;
}

.detail-card {
  min-width: 1200px;
  border-radius: 12px !important;
}

.detail-card :deep(.el-card__header) {
  padding: 16px 20px;
}

.detail-card :deep(.el-card__body) {
  padding: 16px;
}
</style>

<style>
.voucher-detail-dialog {
  background: transparent !important;
  box-shadow: none !important;
}
.voucher-detail-dialog .el-dialog__header {
  display: none !important;
}
.voucher-detail-dialog .el-dialog__body {
  padding: 0 !important;
}
</style>



























