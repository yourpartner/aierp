<template>
  <div class="page page-wide">
    <el-card class="trial-balance-card">
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><DataAnalysis /></el-icon>
            <span class="page-header-title">{{ labels.title }}</span>
            <el-tag v-if="rows.length > 0" size="small" type="info">{{ rows.length }}件</el-tag>
          </div>
          <div class="page-header-actions">
            <el-button type="primary" :icon="Download" @click="exportExcel" :disabled="rows.length === 0">
              {{ labels.exportExcel }}
            </el-button>
          </div>
        </div>
      </template>

      <!-- 筛选条件 -->
      <div class="filter-row">
        <!-- 期間選択方式 -->
        <el-radio-group v-model="periodType" class="period-type-switch">
          <el-radio-button value="year">{{ labels.yearMode }}</el-radio-button>
          <el-radio-button value="month">{{ labels.monthMode }}</el-radio-button>
          <el-radio-button value="range">{{ labels.rangeMode }}</el-radio-button>
        </el-radio-group>

        <!-- 年度選択 -->
        <el-date-picker
          v-if="periodType === 'year'"
          v-model="filters.year"
          type="year"
          value-format="YYYY"
          :placeholder="labels.year"
          class="filter-year"
        />

        <!-- 年月選択 -->
        <el-date-picker
          v-if="periodType === 'month'"
          v-model="filters.yearMonth"
          type="month"
          value-format="YYYY-MM"
          :placeholder="labels.yearMonth"
          class="filter-month"
        />

        <!-- 期間指定 -->
        <template v-if="periodType === 'range'">
          <el-date-picker
            v-model="filters.periodStart"
            type="date"
            value-format="YYYY-MM-DD"
            :placeholder="labels.periodStart"
            class="filter-date"
          />
          <span class="filter-separator">〜</span>
          <el-date-picker
            v-model="filters.periodEnd"
            type="date"
            value-format="YYYY-MM-DD"
            :placeholder="labels.periodEnd"
            class="filter-date"
          />
        </template>

        <!-- 科目カテゴリ -->
        <el-select v-model="filters.category" :placeholder="labels.category" class="filter-select" clearable>
          <el-option value="ALL" :label="labels.categoryAll" />
          <el-option value="BS" :label="labels.categoryBS" />
          <el-option value="PL" :label="labels.categoryPL" />
        </el-select>

        <!-- オプション -->
        <el-checkbox v-model="filters.showZeroBalance">{{ labels.showZeroBalance }}</el-checkbox>

        <el-button type="primary" @click="load" :loading="loading">{{ labels.query }}</el-button>
      </div>

      <!-- 期間表示 -->
      <div v-if="period" class="period-display">
        {{ labels.periodLabel }}: {{ period.start }} 〜 {{ period.end }}
        <span class="account-count">({{ labels.accountCount }}: {{ rows.length }})</span>
      </div>

      <!-- 試算表テーブル -->
      <el-table
        :data="tableData"
        stripe
        border
        style="width: 100%"
        v-loading="loading"
        :row-class-name="getRowClass"
        show-summary
        :summary-method="getSummaries"
        max-height="calc(100vh - 300px)"
      >
        <el-table-column prop="accountCode" :label="labels.accountCode" width="100" fixed="left" />
        <el-table-column prop="accountName" :label="labels.accountName" min-width="180" fixed="left" show-overflow-tooltip />
        
        <!-- 前期繰越 -->
        <el-table-column :label="labels.openingBalance" align="center">
          <el-table-column prop="openingDrBalance" :label="labels.debit" width="130" align="right">
            <template #default="{ row }">
              <span v-if="row.openingDrBalance">{{ formatAmount(row.openingDrBalance) }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="openingCrBalance" :label="labels.credit" width="130" align="right">
            <template #default="{ row }">
              <span v-if="row.openingCrBalance">{{ formatAmount(row.openingCrBalance) }}</span>
            </template>
          </el-table-column>
        </el-table-column>

        <!-- 当期発生額 -->
        <el-table-column :label="labels.periodAmount" align="center">
          <el-table-column prop="periodDr" :label="labels.debit" width="130" align="right">
            <template #default="{ row }">
              <el-link 
                v-if="row.periodDr && row.accountCode !== 'TOTAL'" 
                type="primary" 
                @click="showDetails(row, 'DR')"
              >{{ formatAmount(row.periodDr) }}</el-link>
              <span v-else-if="row.periodDr">{{ formatAmount(row.periodDr) }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="periodCr" :label="labels.credit" width="130" align="right">
            <template #default="{ row }">
              <el-link 
                v-if="row.periodCr && row.accountCode !== 'TOTAL'" 
                type="primary" 
                @click="showDetails(row, 'CR')"
              >{{ formatAmount(row.periodCr) }}</el-link>
              <span v-else-if="row.periodCr">{{ formatAmount(row.periodCr) }}</span>
            </template>
          </el-table-column>
        </el-table-column>

        <!-- 期末残高 -->
        <el-table-column :label="labels.closingBalance" align="center">
          <el-table-column prop="closingDrBalance" :label="labels.debit" width="130" align="right">
            <template #default="{ row }">
              <span v-if="row.closingDrBalance">{{ formatAmount(row.closingDrBalance) }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="closingCrBalance" :label="labels.credit" width="130" align="right">
            <template #default="{ row }">
              <span v-if="row.closingCrBalance">{{ formatAmount(row.closingCrBalance) }}</span>
            </template>
          </el-table-column>
        </el-table-column>

        <el-table-column prop="category" :label="labels.categoryCol" width="60" align="center" />
      </el-table>

      <!-- 貸借バランスチェック -->
      <div v-if="totals" class="balance-check" :class="{ 'is-balanced': totals.isBalanced, 'is-unbalanced': !totals.isBalanced }">
        <el-icon v-if="totals.isBalanced"><CircleCheck /></el-icon>
        <el-icon v-else><WarningFilled /></el-icon>
        <span v-if="totals.isBalanced">{{ labels.balanced }}</span>
        <span v-else>{{ labels.unbalanced }}: ¥{{ formatAmount(Math.abs(totals.periodDiff)) }}</span>
      </div>
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
                <el-icon class="page-header-icon" style="color: #409eff;"><DataAnalysis /></el-icon>
                <span class="page-header-title">{{ detailTitle }}</span>
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
            <el-table-column prop="headerText" :label="labels.summary" min-width="180" show-overflow-tooltip />
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
        <VouchersList 
          v-if="voucherDialogVisible" 
          ref="voucherDetailRef" 
          class="voucher-detail-embed" 
          :allow-edit="false"
          :initial-voucher-id="currentVoucherId"
          :initial-voucher-no="currentVoucherNo"
        />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed } from 'vue'
import { Download, CircleCheck, WarningFilled, DataAnalysis } from '@element-plus/icons-vue'
import api from '../api'
import { useI18n } from '../i18n'
import VouchersList from './VouchersList.vue'

const { section, lang } = useI18n()

const labels = section(
  {
    title: '', exportExcel: '', yearMode: '', monthMode: '', rangeMode: '',
    year: '', yearMonth: '', periodStart: '', periodEnd: '', category: '',
    categoryAll: '', categoryBS: '', categoryPL: '', showZeroBalance: '',
    query: '', periodLabel: '', accountCount: '',
    accountCode: '', accountName: '', openingBalance: '', periodAmount: '',
    closingBalance: '', debit: '', credit: '', categoryCol: '',
    balanced: '', unbalanced: '', voucherNo: '', postingDate: '', drcr: '',
    amount: '', summary: '', lineNo: '', customer: '', vendor: '', department: '',
    employee: '', dueDate: '', clearingStatus: '', clearingVoucherNo: '', clearingDate: '',
    cleared: '', partial: '', open: ''
  },
  (msg) => ({
    title: msg.trialBalance?.title || '合計残高試算表',
    exportExcel: msg.trialBalance?.exportExcel || 'Excel出力',
    yearMode: msg.trialBalance?.yearMode || '年度',
    monthMode: msg.trialBalance?.monthMode || '月次',
    rangeMode: msg.trialBalance?.rangeMode || '期間',
    year: msg.trialBalance?.year || '年度選択',
    yearMonth: msg.trialBalance?.yearMonth || '年月選択',
    periodStart: msg.trialBalance?.periodStart || '開始日',
    periodEnd: msg.trialBalance?.periodEnd || '終了日',
    category: msg.trialBalance?.category || '科目区分',
    categoryAll: msg.trialBalance?.categoryAll || '全科目',
    categoryBS: msg.trialBalance?.categoryBS || '貸借対照表（BS）',
    categoryPL: msg.trialBalance?.categoryPL || '損益計算書（PL）',
    showZeroBalance: msg.trialBalance?.showZeroBalance || '残高ゼロを表示',
    query: msg.trialBalance?.query || '表示',
    periodLabel: msg.trialBalance?.periodLabel || '対象期間',
    accountCount: msg.trialBalance?.accountCount || '科目数',
    accountCode: msg.trialBalance?.accountCode || '科目コード',
    accountName: msg.trialBalance?.accountName || '科目名',
    openingBalance: msg.trialBalance?.openingBalance || '前期繰越',
    periodAmount: msg.trialBalance?.periodAmount || '当期発生額',
    closingBalance: msg.trialBalance?.closingBalance || '期末残高',
    debit: msg.trialBalance?.debit || '借方',
    credit: msg.trialBalance?.credit || '貸方',
    categoryCol: msg.trialBalance?.categoryCol || '区分',
    balanced: msg.trialBalance?.balanced || '貸借一致',
    unbalanced: msg.trialBalance?.unbalanced || '貸借不一致',
    voucherNo: msg.accountLedger?.voucherNo || '伝票番号',
    postingDate: msg.accountLedger?.postingDate || '転記日付',
    drcr: msg.accountLedger?.drcr || '貸借',
    amount: msg.accountLedger?.amount || '金額',
    summary: msg.accountLedger?.headerText || '摘要',
    lineNo: msg.accountBalance?.lineNo || '行',
    customer: msg.accountBalance?.customer || '得意先',
    vendor: msg.accountBalance?.vendor || '仕入先',
    department: msg.accountBalance?.department || '部門',
    employee: msg.accountBalance?.employee || '社員',
    dueDate: msg.accountBalance?.dueDate || '支払予定日',
    clearingStatus: msg.accountBalance?.clearingStatus || '消込状態',
    clearingVoucherNo: msg.accountBalance?.clearingVoucherNo || '消込伝票番号',
    clearingDate: msg.accountBalance?.clearingDate || '消込日付',
    cleared: msg.accountBalance?.cleared || '消込済',
    partial: msg.accountBalance?.partial || '一部消込',
    open: msg.accountBalance?.open || '未消込'
  })
)

// 期間タイプ
const periodType = ref<'year' | 'month' | 'range'>('month')

// フィルター
const filters = reactive({
  year: new Date().getFullYear().toString(),
  yearMonth: `${new Date().getFullYear()}-${String(new Date().getMonth() + 1).padStart(2, '0')}`,
  periodStart: '',
  periodEnd: '',
  category: 'ALL',
  showZeroBalance: false
})

// データ
const loading = ref(false)
const rows = ref<any[]>([])
const totals = ref<any>(null)
const period = ref<{ start: string; end: string } | null>(null)

// 明細ダイアログ
const detailDialogVisible = ref(false)
const detailTitle = ref('')
const detailRows = ref<any[]>([])
const detailLoading = ref(false)

// 凭证详情弹窗
const voucherDialogVisible = ref(false)
const voucherDetailRef = ref<InstanceType<typeof VouchersList> | null>(null)
const currentVoucherId = ref<string>('')
const currentVoucherNo = ref<string>('')

// テーブルデータ（合計行を追加）
const tableData = computed(() => rows.value)

// データ読み込み
async function load() {
  loading.value = true
  try {
    const params: any = {
      showZeroBalance: filters.showZeroBalance,
      accountCategory: filters.category || 'ALL'
    }

    if (periodType.value === 'year') {
      params.year = parseInt(filters.year)
    } else if (periodType.value === 'month') {
      const [y, m] = filters.yearMonth.split('-')
      params.year = parseInt(y)
      params.month = parseInt(m)
    } else {
      params.periodStart = filters.periodStart
      params.periodEnd = filters.periodEnd
    }

    const r = await api.post('/reports/trial-balance', params)
    rows.value = r.data?.data || []
    totals.value = r.data?.totals || null
    period.value = r.data?.period || null
  } catch (e) {
    console.error('試算表の取得に失敗しました', e)
    rows.value = []
    totals.value = null
    period.value = null
  } finally {
    loading.value = false
  }
}

// 行スタイル
function getRowClass({ row }: { row: any }) {
  if (row.accountCode === 'TOTAL') return 'total-row'
  if (row.category === 'BS') return 'bs-row'
  if (row.category === 'PL') return 'pl-row'
  return ''
}

// 合計行
function getSummaries({ columns }: { columns: any[] }) {
  const sums: string[] = []
  columns.forEach((column, index) => {
    if (index === 0) {
      sums[index] = labels.value.title.includes('合計') ? '合計' : 'Total'
      return
    }
    if (index === 1) {
      sums[index] = ''
      return
    }
    
    if (!totals.value) {
      sums[index] = ''
      return
    }

    switch (column.property) {
      case 'openingDrBalance':
        sums[index] = formatAmount(totals.value.openingDr)
        break
      case 'openingCrBalance':
        sums[index] = formatAmount(totals.value.openingCr)
        break
      case 'periodDr':
        sums[index] = formatAmount(totals.value.periodDr)
        break
      case 'periodCr':
        sums[index] = formatAmount(totals.value.periodCr)
        break
      case 'closingDrBalance':
        sums[index] = formatAmount(totals.value.closingDr)
        break
      case 'closingCrBalance':
        sums[index] = formatAmount(totals.value.closingCr)
        break
      default:
        sums[index] = ''
    }
  })
  return sums
}

// 明細表示
async function showDetails(row: any, drcr: 'DR' | 'CR') {
  if (!period.value) return
  
  detailTitle.value = `${row.accountCode} ${row.accountName} - ${drcr === 'DR' ? labels.value.debit : labels.value.credit}明細`
  detailDialogVisible.value = true
  detailLoading.value = true
  detailRows.value = []

  try {
    const params = {
      startDate: period.value.start,
      endDate: period.value.end,
      accountCodes: [row.accountCode],
      page: 1,
      pageSize: 500
    }
    const r = await api.post('/reports/account-ledger', params)
    let data = r.data?.data || []
    data = data.filter((item: any) => item.drcr === drcr)
    detailRows.value = data
  } catch (e) {
    console.error('明細の取得に失敗しました', e)
  } finally {
    detailLoading.value = false
  }
}

// 伝票を開く
function openVoucher(row: any) {
  currentVoucherId.value = row.voucherId || ''
  currentVoucherNo.value = row.voucherNo || ''
  voucherDialogVisible.value = true
}

// Excel出力
function exportExcel() {
  if (rows.value.length === 0) return

  // CSV形式でエクスポート
  const headers = [
    labels.value.accountCode,
    labels.value.accountName,
    labels.value.categoryCol,
    `${labels.value.openingBalance}(${labels.value.debit})`,
    `${labels.value.openingBalance}(${labels.value.credit})`,
    `${labels.value.periodAmount}(${labels.value.debit})`,
    `${labels.value.periodAmount}(${labels.value.credit})`,
    `${labels.value.closingBalance}(${labels.value.debit})`,
    `${labels.value.closingBalance}(${labels.value.credit})`
  ]

  const csvRows = [headers.join(',')]
  
  rows.value.forEach(row => {
    csvRows.push([
      row.accountCode,
      `"${row.accountName}"`,
      row.category,
      row.openingDrBalance || 0,
      row.openingCrBalance || 0,
      row.periodDr || 0,
      row.periodCr || 0,
      row.closingDrBalance || 0,
      row.closingCrBalance || 0
    ].join(','))
  })

  // 合計行
  if (totals.value) {
    csvRows.push([
      '合計',
      '',
      '',
      totals.value.openingDr,
      totals.value.openingCr,
      totals.value.periodDr,
      totals.value.periodCr,
      totals.value.closingDr,
      totals.value.closingCr
    ].join(','))
  }

  const blob = new Blob(['\uFEFF' + csvRows.join('\n')], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `trial_balance_${period.value?.start || 'unknown'}.csv`
  a.click()
  URL.revokeObjectURL(url)
}

// 金額フォーマット
function formatAmount(val: number | string | null | undefined): string {
  if (val === null || val === undefined || val === 0) return ''
  const num = typeof val === 'string' ? parseFloat(val) : val
  if (!Number.isFinite(num) || num === 0) return ''
  return num.toLocaleString('ja-JP', { minimumFractionDigits: 0, maximumFractionDigits: 0 })
}
</script>

<style scoped>
.trial-balance-card {
  min-height: 600px;
  border-radius: 12px !important;
  overflow: hidden;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #409eff;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.page-header-actions {
  display: flex;
  gap: 12px;
}

.filter-row {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-bottom: 16px;
  align-items: center;
}

.period-type-switch {
  margin-right: 8px;
}

.filter-year {
  width: 120px;
}

.filter-month {
  width: 140px;
}

.filter-date {
  width: 150px;
}

.filter-separator {
  color: #909399;
}

.filter-select {
  width: 180px;
}

.period-display {
  margin-bottom: 12px;
  padding: 8px 12px;
  background: #f5f7fa;
  border-radius: 4px;
  font-size: 14px;
  color: #606266;
}

.account-count {
  margin-left: 16px;
  color: #909399;
}

.balance-check {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 16px;
  padding: 12px 16px;
  border-radius: 8px;
  font-weight: 500;
}

.balance-check.is-balanced {
  background: #f0f9eb;
  color: #67c23a;
}

.balance-check.is-unbalanced {
  background: #fef0f0;
  color: #f56c6c;
}

/* テーブルスタイル - 浅灰色风格 */
:deep(.el-table th.el-table__cell) {
  background-color: #f5f7fa !important;
  color: #303133 !important;
  font-weight: 600;
}

:deep(.el-table__header th) {
  background-color: #f5f7fa !important;
}

:deep(.el-table__footer-wrapper td) {
  background-color: #e8f4ff !important;
  font-weight: 600;
}

.bs-row {
  background-color: #fafafa !important;
}

.pl-row {
  background-color: #fffef5 !important;
}

.total-row {
  background-color: #e8f4ff !important;
  font-weight: 600;
}

/* 明細弹窗样式 */
.voucher-dialog-card-wrap {
  min-width: 800px;
  max-width: 1200px;
  max-height: 80vh;
  overflow: auto;
}

.detail-card-wrap {
  max-width: 95vw;
}

.detail-card {
  min-width: 1200px;
  border-radius: 12px !important;
}

.negative-amount {
  color: #f56c6c;
}

.detail-card :deep(.el-card__header) {
  padding: 16px 20px;
}

.detail-card :deep(.el-card__body) {
  padding: 16px;
}

.voucher-detail-embed {
  padding: 0;
}

:deep(.voucher-detail-embed .el-card) {
  box-shadow: none;
  border: none;
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

