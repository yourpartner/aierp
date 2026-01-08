<template>
  <div class="page page-wide">
    <el-card class="ledger-export-card">
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><FolderOpened /></el-icon>
            <span class="page-header-title">{{ labels.title }}</span>
          </div>
        </div>
      </template>

      <!-- 帳簿種類選択 -->
      <div class="ledger-type-section">
        <el-radio-group v-model="ledgerType" size="large" class="ledger-type-selector">
          <el-radio-button value="journal">
            <el-icon><Document /></el-icon>
            {{ labels.journalBook }}
          </el-radio-button>
          <el-radio-button value="general">
            <el-icon><Notebook /></el-icon>
            {{ labels.generalLedger }}
          </el-radio-button>
        </el-radio-group>
      </div>

      <!-- 説明 -->
      <el-alert :type="ledgerType === 'journal' ? 'info' : 'success'" :closable="false" class="ledger-description">
        <template #title>
          <span v-if="ledgerType === 'journal'">{{ labels.journalDescription }}</span>
          <span v-else>{{ labels.generalDescription }}</span>
        </template>
      </el-alert>

      <!-- フィルター条件 -->
      <div class="filter-section">
        <div class="filter-row">
          <div class="filter-item">
            <label>{{ labels.period }}</label>
            <el-date-picker
              v-model="filters.dateRange"
              type="daterange"
              range-separator="〜"
              :start-placeholder="labels.startDate"
              :end-placeholder="labels.endDate"
              value-format="YYYY-MM-DD"
              class="date-range-picker"
            />
          </div>
          
          <div class="filter-item" v-if="ledgerType === 'general'">
            <label>{{ labels.accountCode }}</label>
            <el-select
              v-model="filters.accountCode"
              filterable
              clearable
              remote
              :remote-method="searchAccounts"
              :placeholder="labels.allAccounts"
              class="account-select"
            >
              <el-option
                v-for="opt in accountOptions"
                :key="opt.value"
                :label="opt.label"
                :value="opt.value"
              />
            </el-select>
          </div>
        </div>

        <div class="action-row">
          <el-button type="primary" :icon="Search" @click="preview" :loading="loading" size="large">
            {{ labels.preview }}
          </el-button>
          <el-button type="success" :icon="Download" @click="exportCsv" :disabled="!previewData.length" size="large">
            {{ labels.exportCsv }}
          </el-button>
        </div>
      </div>

      <!-- プレビュー -->
      <div v-if="previewData.length > 0" class="preview-section">
        <div class="preview-header">
          <span class="preview-title">{{ labels.previewTitle }}</span>
          <span class="preview-count">{{ labels.recordCount }}: {{ totalCount }}</span>
        </div>

        <!-- 仕訳帳プレビュー -->
        <el-table
          v-if="ledgerType === 'journal'"
          :data="previewData"
          stripe
          border
          max-height="500"
          v-loading="loading"
          :row-class-name="getJournalRowClass"
        >
          <el-table-column prop="postingDate" :label="labels.date" width="100" fixed="left" />
          <el-table-column prop="voucherNo" :label="labels.voucherNo" width="120" />
          <el-table-column prop="summary" :label="labels.summary" width="200" show-overflow-tooltip />
          <el-table-column prop="accountCode" :label="labels.accountCode" width="100" />
          <el-table-column prop="accountName" :label="labels.accountName" width="160" show-overflow-tooltip />
          <el-table-column prop="debitAmount" :label="labels.debit" width="120" align="right">
            <template #default="{ row }">
              <span v-if="row.debitAmount" class="amount-debit">{{ formatAmount(row.debitAmount) }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="creditAmount" :label="labels.credit" width="120" align="right">
            <template #default="{ row }">
              <span v-if="row.creditAmount" class="amount-credit">{{ formatAmount(row.creditAmount) }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="description" :label="labels.lineDescription" min-width="150" show-overflow-tooltip />
          <el-table-column prop="taxCode" :label="labels.taxCode" width="80" />
        </el-table>

        <!-- 総勘定元帳プレビュー -->
        <div v-else class="general-ledger-preview">
          <div v-for="acct in previewData" :key="acct.accountCode" class="account-section">
            <div class="account-header">
              <span class="account-code">{{ acct.accountCode }}</span>
              <span class="account-name">{{ acct.accountName }}</span>
              <span class="account-category">{{ acct.category }}</span>
              <span class="account-balance">
                {{ labels.closingBalance }}: ¥{{ formatAmount(acct.closingBalance) }}
              </span>
            </div>
            <el-table
              :data="acct.entries"
              stripe
              border
              size="small"
              :row-class-name="getGeneralRowClass"
            >
              <el-table-column prop="postingDate" :label="labels.date" width="100" />
              <el-table-column prop="voucherNo" :label="labels.voucherNo" width="120">
                <template #default="{ row }">
                  <el-link v-if="row.voucherNo" type="primary" @click="openVoucher(row)">{{ row.voucherNo }}</el-link>
                  <span v-else>-</span>
                </template>
              </el-table-column>
              <el-table-column prop="summary" :label="labels.summary" width="180" show-overflow-tooltip />
              <el-table-column prop="counterAccounts" :label="labels.counterAccount" min-width="150" show-overflow-tooltip />
              <el-table-column prop="debitAmount" :label="labels.debit" width="110" align="right">
                <template #default="{ row }">
                  <span v-if="row.debitAmount" class="amount-debit">{{ formatAmount(row.debitAmount) }}</span>
                </template>
              </el-table-column>
              <el-table-column prop="creditAmount" :label="labels.credit" width="110" align="right">
                <template #default="{ row }">
                  <span v-if="row.creditAmount" class="amount-credit">{{ formatAmount(row.creditAmount) }}</span>
                </template>
              </el-table-column>
              <el-table-column prop="balance" :label="labels.balance" width="120" align="right">
                <template #default="{ row }">
                  <span :class="row.balance >= 0 ? 'balance-positive' : 'balance-negative'">
                    {{ formatAmount(row.balance) }}
                  </span>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </div>

        <!-- 合計情報 -->
        <div v-if="totals" class="totals-section">
          <div class="totals-row">
            <span class="totals-label">{{ labels.totalDebit }}:</span>
            <span class="totals-value amount-debit">¥{{ formatAmount(totals.debit || totals.periodDebit || 0) }}</span>
          </div>
          <div class="totals-row">
            <span class="totals-label">{{ labels.totalCredit }}:</span>
            <span class="totals-value amount-credit">¥{{ formatAmount(totals.credit || totals.periodCredit || 0) }}</span>
          </div>
          <div v-if="totals.isBalanced !== undefined" class="totals-row">
            <el-tag :type="totals.isBalanced ? 'success' : 'danger'" size="large">
              {{ totals.isBalanced ? labels.balanced : labels.unbalanced }}
            </el-tag>
          </div>
        </div>
      </div>

      <!-- 空状態 -->
      <el-empty v-else-if="searched && !loading" :description="labels.noData" />
    </el-card>

    <!-- 凭证详情弹窗 -->
    <el-dialog v-model="voucherDialogVisible" width="auto" append-to-body destroy-on-close class="voucher-detail-dialog">
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VouchersList 
          v-if="voucherDialogVisible" 
          ref="voucherDetailRef" 
          class="voucher-detail-embed" 
          :allow-edit="false"
          :initial-voucher-no="currentVoucherNo"
        />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed } from 'vue'
import { Document, Notebook, Download, Search, FolderOpened } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import api from '../api'
import { useI18n } from '../i18n'
import VouchersList from './VouchersList.vue'

const { section, lang } = useI18n()

const labels = section(
  {
    title: '', journalBook: '', generalLedger: '', journalDescription: '', generalDescription: '',
    period: '', startDate: '', endDate: '', accountCode: '', allAccounts: '',
    preview: '', exportCsv: '', previewTitle: '', recordCount: '',
    date: '', voucherNo: '', summary: '', accountName: '', debit: '', credit: '',
    lineDescription: '', taxCode: '', counterAccount: '', balance: '', closingBalance: '',
    totalDebit: '', totalCredit: '', balanced: '', unbalanced: '', noData: '', exportSuccess: ''
  },
  (msg) => ({
    title: msg.ledgerExport?.title || '帳簿出力',
    journalBook: msg.ledgerExport?.journalBook || '仕訳帳',
    generalLedger: msg.ledgerExport?.generalLedger || '総勘定元帳',
    journalDescription: msg.ledgerExport?.journalDescription || '仕訳帳は、すべての仕訳を日付順に記録した帳簿です。税務調査や会計監査で必要となる基本帳簿です。',
    generalDescription: msg.ledgerExport?.generalDescription || '総勘定元帳は、勘定科目ごとにすべての取引を記録し、残高を計算した帳簿です。各科目の動きを時系列で確認できます。',
    period: msg.ledgerExport?.period || '期間',
    startDate: msg.ledgerExport?.startDate || '開始日',
    endDate: msg.ledgerExport?.endDate || '終了日',
    accountCode: msg.ledgerExport?.accountCode || '勘定科目',
    allAccounts: msg.ledgerExport?.allAccounts || '全科目',
    preview: msg.ledgerExport?.preview || 'プレビュー',
    exportCsv: msg.ledgerExport?.exportCsv || 'CSV出力',
    previewTitle: msg.ledgerExport?.previewTitle || 'プレビュー',
    recordCount: msg.ledgerExport?.recordCount || '件数',
    date: msg.ledgerExport?.date || '日付',
    voucherNo: msg.ledgerExport?.voucherNo || '伝票番号',
    summary: msg.ledgerExport?.summary || '摘要',
    accountName: msg.ledgerExport?.accountName || '勘定科目名',
    debit: msg.ledgerExport?.debit || '借方',
    credit: msg.ledgerExport?.credit || '貸方',
    lineDescription: msg.ledgerExport?.lineDescription || '行摘要',
    taxCode: msg.ledgerExport?.taxCode || '税区分',
    counterAccount: msg.ledgerExport?.counterAccount || '相手科目',
    balance: msg.ledgerExport?.balance || '残高',
    closingBalance: msg.ledgerExport?.closingBalance || '期末残高',
    totalDebit: msg.ledgerExport?.totalDebit || '借方合計',
    totalCredit: msg.ledgerExport?.totalCredit || '貸方合計',
    balanced: msg.ledgerExport?.balanced || '貸借一致',
    unbalanced: msg.ledgerExport?.unbalanced || '貸借不一致',
    noData: msg.ledgerExport?.noData || '該当するデータがありません',
    exportSuccess: msg.ledgerExport?.exportSuccess || 'CSVファイルをダウンロードしました'
  })
)

// 状態
const ledgerType = ref<'journal' | 'general'>('journal')
const loading = ref(false)
const searched = ref(false)

// フィルター
const filters = reactive({
  dateRange: [] as string[],
  accountCode: ''
})

// 科目選択肢
const accountOptions = ref<{ label: string; value: string }[]>([])

// プレビューデータ
const previewData = ref<any[]>([])
const totals = ref<any>(null)

// 凭证详情弹窗
const voucherDialogVisible = ref(false)
const voucherDetailRef = ref<InstanceType<typeof VouchersList> | null>(null)
const currentVoucherNo = ref<string>('')

// 件数
const totalCount = computed(() => {
  if (ledgerType.value === 'journal') {
    return previewData.value.length
  } else {
    return previewData.value.reduce((sum, acct) => sum + (acct.entries?.length || 0), 0)
  }
})

// 科目検索
async function searchAccounts(query: string) {
  const where: any[] = []
  if (query?.trim()) {
    where.push({ json: 'name', op: 'contains', value: query.trim() })
  }
  try {
    const r = await api.post('/objects/account/search', { 
      page: 1, 
      pageSize: 100, 
      where, 
      orderBy: [{ field: 'account_code', dir: 'ASC' }] 
    })
    accountOptions.value = (r.data?.data || []).map((a: any) => {
      const code = a.account_code
      const name = a.payload?.name || code
      return { label: `${code} ${name}`, value: code }
    })
  } catch (e) {
    console.error('科目検索に失敗しました', e)
  }
}

// プレビュー
async function preview() {
  if (!filters.dateRange || filters.dateRange.length !== 2) {
    ElMessage.warning('期間を選択してください')
    return
  }

  loading.value = true
  searched.value = true
  previewData.value = []
  totals.value = null

  try {
    const params: any = {
      startDate: filters.dateRange[0],
      endDate: filters.dateRange[1]
    }

    if (ledgerType.value === 'general' && filters.accountCode) {
      params.accountCode = filters.accountCode
    }

    const endpoint = ledgerType.value === 'journal' 
      ? '/reports/journal-book' 
      : '/reports/general-ledger'

    const r = await api.post(endpoint, params)
    previewData.value = r.data?.data || []
    totals.value = r.data?.totals || null
  } catch (e) {
    console.error('プレビューの取得に失敗しました', e)
    ElMessage.error('データの取得に失敗しました')
  } finally {
    loading.value = false
  }
}

// CSV出力
async function exportCsv() {
  if (!filters.dateRange || filters.dateRange.length !== 2) {
    ElMessage.warning('期間を選択してください')
    return
  }

  loading.value = true
  try {
    const params: any = {
      startDate: filters.dateRange[0],
      endDate: filters.dateRange[1],
      format: 'csv'
    }

    if (ledgerType.value === 'general' && filters.accountCode) {
      params.accountCode = filters.accountCode
    }

    const endpoint = ledgerType.value === 'journal' 
      ? '/reports/journal-book' 
      : '/reports/general-ledger'

    const r = await api.post(endpoint, params, { responseType: 'blob' })
    
    // ダウンロード
    const blob = new Blob([r.data], { type: 'text/csv;charset=utf-8' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    const filename = ledgerType.value === 'journal' 
      ? `仕訳帳_${filters.dateRange[0]}_${filters.dateRange[1]}.csv`
      : `総勘定元帳_${filters.dateRange[0]}_${filters.dateRange[1]}.csv`
    a.download = filename
    a.click()
    URL.revokeObjectURL(url)

    ElMessage.success(labels.value.exportSuccess)
  } catch (e) {
    console.error('CSV出力に失敗しました', e)
    ElMessage.error('CSV出力に失敗しました')
  } finally {
    loading.value = false
  }
}

// 行スタイル
function getJournalRowClass({ row, rowIndex }: { row: any; rowIndex: number }) {
  // 同じ伝票番号の行をグループ化
  if (rowIndex > 0 && previewData.value[rowIndex - 1]?.voucherNo === row.voucherNo) {
    return 'same-voucher-row'
  }
  return ''
}

function getGeneralRowClass({ row }: { row: any }) {
  if (row.summary === '前期繰越') return 'carry-forward-row'
  return ''
}

// 金額フォーマット
function formatAmount(val: number | string | null | undefined): string {
  if (val === null || val === undefined) return '0'
  const num = typeof val === 'string' ? parseFloat(val) : val
  if (!Number.isFinite(num)) return '0'
  return num.toLocaleString('ja-JP', { minimumFractionDigits: 0, maximumFractionDigits: 0 })
}

// 伝票を開く
function openVoucher(row: any) {
  if (!row.voucherNo) return
  currentVoucherNo.value = row.voucherNo
  voucherDialogVisible.value = true
}
</script>

<style scoped>
.ledger-export-card {
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
  color: #67c23a;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.ledger-type-section {
  display: flex;
  justify-content: center;
  margin-bottom: 20px;
}

.ledger-type-selector {
  :deep(.el-radio-button__inner) {
    padding: 12px 32px;
    font-size: 16px;
  }
}

.ledger-description {
  margin-bottom: 24px;
}

.filter-section {
  margin-bottom: 24px;
}

.filter-row {
  display: flex;
  flex-wrap: wrap;
  gap: 24px;
  margin-bottom: 16px;
}

.filter-item {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.filter-item label {
  font-size: 14px;
  color: #606266;
  font-weight: 500;
}

.date-range-picker {
  width: 320px;
}

.account-select {
  width: 280px;
}

.action-row {
  display: flex;
  gap: 12px;
}

.preview-section {
  margin-top: 24px;
}

.preview-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
  padding-bottom: 12px;
  border-bottom: 2px solid #e4e7ed;
}

.preview-title {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.preview-count {
  color: #909399;
  font-size: 14px;
}

/* 総勘定元帳スタイル */
.general-ledger-preview {
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.account-section {
  border: 1px solid #e4e7ed;
  border-radius: 8px;
  overflow: hidden;
}

.account-header {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 12px 16px;
  background: linear-gradient(135deg, #4a6fa5 0%, #3d5a80 100%);
  color: white;
}

.account-code {
  font-weight: 600;
  font-size: 15px;
}

.account-name {
  font-weight: 600;
  font-size: 15px;
  flex: 1;
}

.account-category {
  background: rgba(255,255,255,0.2);
  padding: 2px 8px;
  border-radius: 4px;
  font-size: 12px;
}

.account-balance {
  font-weight: 600;
}

/* 金額スタイル */
.amount-debit {
  color: #409eff;
  font-weight: 500;
}

.amount-credit {
  color: #e6a23c;
  font-weight: 500;
}

.balance-positive {
  color: #67c23a;
  font-weight: 500;
}

.balance-negative {
  color: #f56c6c;
  font-weight: 500;
}

/* 合計セクション */
.totals-section {
  display: flex;
  gap: 32px;
  align-items: center;
  margin-top: 20px;
  padding: 16px 20px;
  background: #ecf5ff;
  border-radius: 8px;
}

.totals-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.totals-label {
  color: #606266;
  font-weight: 500;
}

.totals-value {
  font-size: 18px;
  font-weight: 600;
}

/* テーブルスタイル - 浅灰色风格 */
:deep(.el-table th.el-table__cell) {
  background-color: #f5f7fa !important;
  color: #303133 !important;
  font-weight: 600;
}

.same-voucher-row {
  background-color: #fafafa !important;
}

.same-voucher-row td:first-child,
.same-voucher-row td:nth-child(2),
.same-voucher-row td:nth-child(3) {
  color: transparent !important;
}

.carry-forward-row {
  background-color: #f0f9eb !important;
  font-weight: 500;
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

