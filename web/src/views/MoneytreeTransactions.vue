<template>
  <div style="padding:16px">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><CreditCard /></el-icon>
            <span class="page-header-title">銀行明細</span>
          </div>
        </div>
      </template>
      <div class="filters">
        <el-date-picker
          v-model="filters.dateRange"
          type="daterange"
          range-separator="〜"
          start-placeholder="開始日"
          end-placeholder="終了日"
          value-format="YYYY-MM-DDTHH:mm:ss[Z]"
          style="width: 260px"
        />
        <el-select v-model="filters.type" style="width:120px">
          <el-option label="全て" value="all" />
          <el-option label="入金" value="deposit" />
          <el-option label="出金" value="withdrawal" />
        </el-select>
        <el-select v-model="filters.status" style="width:140px">
          <el-option label="全ステータス" value="all" />
          <el-option label="失敗" value="failed" />
          <el-option label="ルール不足" value="needs_rule" />
          <el-option label="重複疑い" value="duplicate_suspected" />
          <el-option label="記帳済み" value="posted" />
          <el-option label="紐付け済み" value="linked" />
          <el-option label="未マッチ" value="unmatched" />
          <el-option label="保留" value="pending" />
        </el-select>
        <el-input
          v-model="filters.keyword"
          placeholder="摘要キーワード"
          clearable
          style="width:320px"
          @keyup.enter="onSearch"
        />
        <el-button type="primary" @click="onSearch">検索</el-button>
        <el-button @click="resetFilters">リセット</el-button>
        <div class="op-actions">
          <span v-if="selectedRows.length > 0 && !isTaskMode" class="selection-info">
            <el-tag type="info" size="small">{{ selectedRows.length }}件選択中</el-tag>
            <el-button size="small" text @click="clearSelection">選択解除</el-button>
          </span>
          <el-button v-if="!isTaskMode" type="success" :disabled="!hasPendingSelection" @click="simulateSelected">
            記帳ルールシミュレーション
          </el-button>
          <el-button
            v-if="!isTaskMode"
            type="warning"
            :disabled="!hasPendingSelection"
            :loading="runPostingLoading"
            @click="runPostingSelected"
          >
            自動記帳
          </el-button>
          <el-button
            v-if="!isTaskMode"
            type="danger"
            :disabled="!hasLinkedSelection"
            :loading="unlinkLoading"
            @click="unlinkSelected"
          >
            紐付け解除
          </el-button>
          <el-button v-if="!isTaskMode" type="primary" @click="openSyncDialog">銀行連携</el-button>
          <el-button v-if="!isTaskMode" @click="openRulesDialog">DSLルール管理</el-button>
        </div>
      </div>

      <el-table
        ref="tableRef"
        :data="rows"
        v-loading="loading"
        stripe
        border
        size="small"
        class="mt-table"
        row-key="id"
        @selection-change="handleSelectionChange"
      >
        <el-table-column v-if="!isTaskMode" type="selection" width="40" :selectable="isRowSelectable" reserve-selection />
        <el-table-column prop="transactionDate" label="取引日" width="110" />
        <el-table-column v-if="showBankColumn" prop="bankName" label="金融機関" width="140" show-overflow-tooltip />
        <el-table-column v-if="showAccountColumn" label="口座" width="110">
          <template #default="{ row }">
            <div class="account-cell">
              <span class="account-name">{{ row.accountName || '-' }}</span>
              <span v-if="row.accountNumber" class="account-number">{{ row.accountNumber }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="入金" width="120">
          <template #default="{ row }">
            <span v-if="row.depositAmount" class="amount-positive">{{ formatNumber(row.depositAmount) }}</span>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column label="出金" width="120">
          <template #default="{ row }">
            <span v-if="row.withdrawalAmount" class="amount-negative">{{ formatNumber(row.withdrawalAmount) }}</span>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column label="残高" width="130">
          <template #default="{ row }">
            <span v-if="row.balance !== null && row.balance !== undefined">
              {{ formatNumber(row.balance) }}
            </span>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column v-if="showCurrencyColumn" prop="currency" label="通貨" width="80" />
        <el-table-column v-if="showDescriptionColumn" prop="description" label="摘要" min-width="200" show-overflow-tooltip />
        <el-table-column label="ステータス" width="110">
          <template #default="{ row }">
            <el-tag size="small" :type="statusType(row.postingStatus)">
              {{ statusLabel(row.postingStatus) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column v-if="showVoucherColumn" label="伝票番号" width="130">
          <template #default="{ row }">
            <el-link v-if="row.voucherNo" type="primary" @click="openVoucherDetail(row)">{{ row.voucherNo }}</el-link>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column prop="ruleTitle" label="適用ルール" width="160" show-overflow-tooltip />
        <el-table-column v-if="showErrorColumn" prop="postingError" label="エラー内容" min-width="200" show-overflow-tooltip />
        <el-table-column prop="importedAt" label="取込日時" width="160" />
      </el-table>

      <div style="margin-top:12px; display:flex; justify-content:flex-end;">
        <el-pagination
          background
          layout="prev, pager, next, jumper, sizes, total"
          :current-page="page"
          :page-size="pageSize"
          :total="total"
          :page-sizes="[10, 20, 50, 100]"
          @current-change="handlePageChange"
          @size-change="handleSizeChange"
        />
      </div>
    </el-card>

    <el-dialog
      v-model="simulateDialogVisible"
      title="ルール照合シミュレーション"
      class="simulate-dialog"
      append-to-body
      destroy-on-close
      align-center
      width="75%"
      :style="{ maxWidth: '1100px' }"
    >
      <el-alert
        v-if="!simulateSelectionSnapshot.length"
        type="info"
        title="一覧から明細を選択してください"
        show-icon
        style="margin-bottom:12px"
      />
      <div class="sim-table-wrapper" v-else>
        <el-table
          :data="simulateResults"
          v-loading="simulateLoading"
          border
        >
          <el-table-column label="摘要 / 金額" min-width="260">
            <template #default="{ row }">
              <div class="sim-desc">{{ getSimDescription(row.transactionId) }}</div>
              <div class="sim-amount">{{ getSimAmount(row.transactionId) }}</div>
            </template>
          </el-table-column>
          <el-table-column prop="status" label="ステータス" width="100" />
          <el-table-column prop="ruleTitle" label="適用ルール" width="200" show-overflow-tooltip />
          <el-table-column label="仕訳科目" width="220">
            <template #default="{ row }">
              <div>借方：{{ formatAccountLabel(row.debitAccount, row.debitAccountName) }}</div>
              <div>貸方：{{ formatAccountLabel(row.creditAccount, row.creditAccountName) }}</div>
            </template>
          </el-table-column>
          <el-table-column prop="wouldClearOpenItem" label="消込可" width="80">
            <template #default="{ row }">
              <el-tag size="small" :type="row.wouldClearOpenItem ? 'success' : 'info'">
                {{ row.wouldClearOpenItem ? 'はい' : 'いいえ' }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="message" label="メモ" min-width="200" show-overflow-tooltip />
        </el-table>
      </div>
      <template #footer>
        <span class="dialog-footer">
          <el-button @click="simulateDialogVisible = false">閉じる</el-button>
        </span>
      </template>
    </el-dialog>

    <el-dialog v-model="rulesDialogVisible" title="自動記帳ルール" width="815px">
      <div class="rules-toolbar">
        <el-button type="primary" @click="startCreateRule">ルール追加</el-button>
      </div>
      <el-table :data="rules" v-loading="rulesLoading" border max-height="360">
        <el-table-column type="expand">
          <template #default="{ row }">
            <div class="rule-json">
              <div>
                <strong>Matcher</strong>
                <pre>{{ formatJson(row.matcher) }}</pre>
              </div>
              <div>
                <strong>Action</strong>
                <pre>{{ formatJson(row.action) }}</pre>
              </div>
            </div>
          </template>
        </el-table-column>
        <el-table-column prop="title" label="ルール名" min-width="200" show-overflow-tooltip />
        <el-table-column prop="priority" label="優先度" width="90" />
        <el-table-column label="状態" width="100">
          <template #default="{ row }">
            <el-tag :type="row.isActive ? 'success' : 'info'" size="small">
              {{ row.isActive ? '有効' : '無効' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="updatedAt" label="更新日時" width="150">
          <template #default="{ row }">
            {{ formatDateTime(row.updatedAt) }}
          </template>
        </el-table-column>
        <el-table-column label="操作" width="140">
          <template #default="{ row }">
            <el-button link type="primary" @click="handleEditRule(row)">編集</el-button>
            <el-popconfirm title="このルールを削除しますか？" @confirm="handleDeleteRule(row)">
              <template #reference>
                <el-button link type="danger">削除</el-button>
              </template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </el-dialog>

    <el-dialog
      v-model="ruleFormDialogVisible"
      :title="editingRuleId ? 'ルール編集' : 'ルール追加'"
      width="720px"
      class="rule-form-dialog"
      append-to-body
    >
      <el-form label-width="100px">
        <el-form-item label="名称">
          <el-input v-model="ruleForm.title" placeholder="ルール名" />
        </el-form-item>
        <el-form-item label="説明">
          <el-input
            v-model="ruleForm.description"
            type="textarea"
            :rows="4"
            placeholder="自然言語でルール内容を記述してください（例：BOOKING.COMからの入金は...）"
          />
        </el-form-item>
        <el-form-item label="優先度">
          <el-input-number v-model="ruleForm.priority" :min="1" :max="999" />
        </el-form-item>
        <el-form-item label="有効化">
          <el-switch v-model="ruleForm.isActive" />
        </el-form-item>
        <el-form-item label="Matcher JSON">
          <el-input
            v-model="ruleForm.matcherText"
            type="textarea"
            :rows="6"
            placeholder='例 {"descriptionContains":["振込"]}'
          />
        </el-form-item>
        <el-form-item label="Action JSON">
          <el-input
            v-model="ruleForm.actionText"
            type="textarea"
            :rows="6"
            placeholder='例 {"debitAccount":"1111","creditAccount":"2222"}'
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <span class="dialog-footer">
          <el-button @click="ruleFormDialogVisible = false">キャンセル</el-button>
          <el-button type="primary" :loading="ruleFormLoading" @click="submitRule">
            保存
          </el-button>
        </span>
      </template>
    </el-dialog>

    <el-dialog
      v-model="syncDialogVisible"
      title="銀行明細連携"
      width="420px"
      append-to-body
    >
      <div class="sync-desc">
        指定期間の銀行残高明細を取得して自動取込します。既に登録済みの明細は重複しません。
      </div>
      <el-date-picker
        v-model="syncRange"
        type="daterange"
        range-separator="〜"
        start-placeholder="開始日"
        end-placeholder="終了日"
        value-format="YYYY-MM-DDTHH:mm:ss[Z]"
        unlink-panels
        style="width: 100%; margin-top: 12px;"
      />
      <div style="margin-top: 16px;">
        <div style="margin-bottom: 8px; font-weight: 500;">インポートモード</div>
        <el-radio-group v-model="syncImportMode">
          <el-radio value="normal">通常モード</el-radio>
          <el-radio value="history">履歴インポート</el-radio>
        </el-radio-group>
        <div class="import-mode-hint">
          <template v-if="syncImportMode === 'normal'">
            自動記帳ルールに従って仕訳を生成します。
          </template>
          <template v-else>
            既存の会計伝票との紐付けのみ行います。新規仕訳は生成しません。
          </template>
        </div>
      </div>
      <template #footer>
        <span class="dialog-footer">
          <el-button @click="syncDialogVisible = false" :disabled="syncLoading">キャンセル</el-button>
          <el-button type="primary" :loading="syncLoading" @click="runMoneytreeSync">実行</el-button>
        </span>
      </template>
    </el-dialog>

    <el-dialog
      v-model="voucherDialogVisible"
      width="auto"
      append-to-body
      destroy-on-close
      class="voucher-detail-dialog"
    >
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VouchersList
          v-if="voucherDialogVisible"
          ref="voucherDetailRef"
          class="voucher-detail-embed"
          :allow-edit="true"
          @deleted="onVoucherDeleted"
          @reversed="onVoucherReversed"
        />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed, nextTick } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { CreditCard } from '@element-plus/icons-vue'
import VouchersList from './VouchersList.vue'
import {
  fetchMoneytreeTransactions,
  fetchMoneytreePostingTaskTransactions,
  simulateMoneytreePosting,
  runMoneytreePosting,
  fetchMoneytreeRules,
  createMoneytreeRule,
  updateMoneytreeRule,
  deleteMoneytreeRule,
  importMoneytreeTransactions,
  type MoneytreeTransactionItem,
  type MoneytreeSimulationResult,
  type MoneytreeRule
} from '../api/moneytree'

type DateRange = [Date, Date]

const tableRef = ref<any>(null)
const rows = ref<MoneytreeTransactionItem[]>([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const selectedRows = ref<MoneytreeTransactionItem[]>([])
const simulateDialogVisible = ref(false)
const simulateLoading = ref(false)
const simulateResults = ref<MoneytreeSimulationResult[]>([])
const simulateSelectionSnapshot = ref<MoneytreeTransactionItem[]>([])
const runPostingLoading = ref(false)
const unlinkLoading = ref(false)
const syncDialogVisible = ref(false)
const syncLoading = ref(false)
const rulesDialogVisible = ref(false)
const rulesLoading = ref(false)
const includeInactiveRules = ref(false)
const rules = ref<MoneytreeRule[]>([])
const ruleFormDialogVisible = ref(false)
const ruleFormLoading = ref(false)
const editingRuleId = ref<string | null>(null)
const ruleForm = ref(defaultRuleForm())
const showCurrencyColumn = computed(() => rows.value.some((row) => !!(row.currency && row.currency.trim())))
const showAccountColumn = computed(() => rows.value.some((row) => row.accountName || row.accountNumber))
const showVoucherColumn = computed(() => rows.value.some((row) => !!row.voucherNo))
const showErrorColumn = computed(() => rows.value.some((row) => !!(row.postingError && row.postingError.trim())))
const showBankColumn = computed(() => rows.value.some((row) => row.bankName))
const showDescriptionColumn = computed(() => rows.value.some((row) => row.description))
const voucherDialogVisible = ref(false)
const voucherDetailRef = ref<InstanceType<typeof VouchersList> | null>(null)
const syncRange = ref<DateRange | null>(null)
const syncImportMode = ref<'normal' | 'history'>('normal')

const filters = ref({
  dateRange: getDefaultRange(),
  type: 'all',
  status: 'all',
  keyword: ''
})

const taskApprovalId = ref<string>('')
const isTaskMode = computed(() => !!taskApprovalId.value)

function getDefaultRange(): DateRange | null {
  // 不设置默认日期范围，显示所有数据（分页查询不会影响性能）
  return null
}

function getCurrentMonthRange(): DateRange {
  const now = new Date()
  const first = new Date(now.getFullYear(), now.getMonth(), 1)
  return [first, now]
}

async function loadData() {
  loading.value = true
  try {
    const params: Record<string, any> = {
      page: page.value,
      pageSize: pageSize.value,
      type: filters.value.type,
      status: filters.value.status
    }
    if (filters.value.dateRange && filters.value.dateRange.length === 2) {
      // dateRange 可能是 Date 对象或 ISO 格式的字符串（取决于用户是否手动选择）
      const start = filters.value.dateRange[0]
      const end = filters.value.dateRange[1]
      params.startDate = typeof start === 'string' ? start : start.toISOString()
      params.endDate = typeof end === 'string' ? end : end.toISOString()
    }
    if (filters.value.keyword && filters.value.keyword.trim()) {
      params.keyword = filters.value.keyword.trim()
    }
    const data = isTaskMode.value
      ? await fetchMoneytreePostingTaskTransactions(taskApprovalId.value, params)
      : await fetchMoneytreeTransactions(params)
    rows.value = data.items ?? []
    total.value = data.total ?? 0
    // 不再清空 selectedRows，因为使用了 reserve-selection 保持跨分页选择
  } catch (err: any) {
    const msg = err?.response?.data?.error || err?.message || '読み込みに失敗しました。'
    ElMessage.error(msg)
  } finally {
    loading.value = false
  }
}

function onSearch() {
  page.value = 1
  loadData()
}

function resetFilters() {
  filters.value = {
    dateRange: getDefaultRange(),
    type: 'all',
    status: 'all',
    keyword: ''
  }
  page.value = 1
  loadData()
}

function handlePageChange(val: number) {
  page.value = val
  loadData()
}

function handleSizeChange(val: number) {
  pageSize.value = val
  page.value = 1
  loadData()
}

function openSyncDialog() {
  syncRange.value = getCurrentMonthRange()
  syncDialogVisible.value = true
}

function normalizeDate(value: Date | string) {
  return value instanceof Date ? value : new Date(value)
}

function formatIso(date: Date | string) {
  return normalizeDate(date).toISOString()
}

async function runMoneytreeSync() {
  if (!syncRange.value || syncRange.value.length !== 2) {
    ElMessage.warning('期間を選択してください。')
    return
  }
  const [start, end] = syncRange.value
  syncLoading.value = true
  try {
    const result = await importMoneytreeTransactions({
      startDate: formatIso(start),
      endDate: formatIso(end),
      importMode: syncImportMode.value
    })
    
    // 根据导入模式显示不同的成功消息
    if (syncImportMode.value === 'history') {
      const linkedCount = result?.linkedRows ?? 0
      const totalCount = result?.insertedRows ?? 0
      ElMessage.success(`履歴インポート完了：${totalCount}件中${linkedCount}件を既存伝票に紐付けました。`)
    } else {
    ElMessage.success('銀行明細の連携が完了しました。')
    }
    
    syncDialogVisible.value = false
    syncImportMode.value = 'normal' // 重置为默认模式
    await loadData()
  } catch (err: any) {
    const msg = err?.response?.data?.error || err?.message || '銀行連携に失敗しました。'
    ElMessage.error(msg)
  } finally {
    syncLoading.value = false
  }
}

function handleSelectionChange(selection: MoneytreeTransactionItem[]) {
  selectedRows.value = [...selection]
}

// 所有行都可选择（解除关联功能需要选择已处理的行）
function isRowSelectable(_row: MoneytreeTransactionItem) {
  return true
}

// 判断选中的行中是否有可进行自动记帳的（未处理的）
const hasPendingSelection = computed(() => {
  return selectedRows.value.some(row => {
    const status = (row.postingStatus || '').toLowerCase()
    return status !== 'posted' && status !== 'linked'
  })
})

// 判断选中的行中是否有已关联的（可解除关联的）
const hasLinkedSelection = computed(() => {
  return selectedRows.value.some(row => {
    const status = (row.postingStatus || '').toLowerCase()
    return status === 'posted' || status === 'linked'
  })
})

// 清除所有选择（包括跨分页的选择）
function clearSelection() {
  selectedRows.value = []
  tableRef.value?.clearSelection()
}

function openVoucherDetail(row: MoneytreeTransactionItem) {
  if (!row?.voucherNo) return
  voucherDialogVisible.value = true
  nextTick(() => {
    voucherDetailRef.value?.applyIntent?.({
      voucherNo: row.voucherNo,
      // In task-review mode, allow user to edit the voucher if needed.
      detailOnly: !isTaskMode.value
    })
  })
}

// 凭证删除后关闭弹窗并刷新银行明细列表
function onVoucherDeleted(_voucherId: string) {
  voucherDialogVisible.value = false
  // 刷新列表以更新银行明细状态
  loadData()
}

// 凭证冲销后关闭弹窗并刷新银行明细列表
function onVoucherReversed(_voucherId: string, _reversalVoucherId: string) {
  voucherDialogVisible.value = false
  // 刷新列表以更新银行明细状态
  loadData()
}

// ChatKit modal payload hook
function applyIntent(payload: any) {
  const mode = payload?.mode || payload?.context
  const approvalTaskId = payload?.approvalTaskId || payload?.taskId || payload?.id
  if (mode === 'task' && typeof approvalTaskId === 'string' && approvalTaskId.trim()) {
    taskApprovalId.value = approvalTaskId.trim()
    // 在任务面板查看时，默认显示全部（否则像 posted=1, failed=0 会看到空白）
    filters.value.status = 'all'
    // Do not restrict by date in task-review mode (the run defines the dataset)
    ;(filters.value as any).dateRange = null
    page.value = 1
    loadData()
    return
  }
}

defineExpose({ applyIntent })

async function simulateSelected() {
  // 只处理未记帳的记录
  const pendingRows = selectedRows.value.filter(row => {
    const status = (row.postingStatus || '').toLowerCase()
    return status !== 'posted' && status !== 'linked'
  })
  if (!pendingRows.length) {
    ElMessage.warning('対象の明細を選択してください。')
    return
  }
  simulateSelectionSnapshot.value = [...pendingRows]
  const ids = simulateSelectionSnapshot.value.map((item) => item.id)
  simulateDialogVisible.value = true
  simulateLoading.value = true
  try {
    const res = await simulateMoneytreePosting(ids)
    simulateResults.value = res.items ?? []
    if (!res.items?.length) {
      ElMessage.info('シミュレーション結果がありません。')
    }
  } catch (err: any) {
    const msg = err?.response?.data?.error || err?.message || 'シミュレーションに失敗しました。'
    ElMessage.error(msg)
  } finally {
    simulateLoading.value = false
  }
}

async function runPostingSelected() {
  // 只处理未记帳的记录
  const pendingRows = selectedRows.value.filter(row => {
    const status = (row.postingStatus || '').toLowerCase()
    return status !== 'posted' && status !== 'linked'
  })
  if (!pendingRows.length) {
    ElMessage.warning('対象の明細を選択してください。')
    return
  }
  const ids = pendingRows.map((item) => item.id)
  
  runPostingLoading.value = true
  try {
    const result = await runMoneytreePosting({ ids })
    const successCount = result.posted || 0
    const failCount = result.failed || 0
    const mergedCount = result.merged || 0
    
    // 使用后端返回的详细结果更新本地数据（不改变行位置）
    if (result.items && Array.isArray(result.items)) {
      for (const item of result.items) {
        const idx = rows.value.findIndex((r) => r.id === item.id)
        if (idx >= 0) {
          rows.value[idx] = {
            ...rows.value[idx],
            postingStatus: item.status,
            postingError: item.error || null,
            voucherNo: item.voucherNo || rows.value[idx].voucherNo
          }
        }
      }
    }
    
    // 清除选中状态（包括跨分页的选择）
    selectedRows.value = []
    tableRef.value?.clearSelection()
    
    // 显示结果消息
    let msg = `記帳完了: ${successCount}件成功`
    if (mergedCount > 0) msg += `, ${mergedCount}件合算`
    if (failCount > 0) msg += `, ${failCount}件失敗`
    ElMessage.success(msg)
  } catch (err: any) {
    const msg = err?.response?.data?.error || err?.message || '実行に失敗しました。'
    ElMessage.error(msg)
  } finally {
    runPostingLoading.value = false
  }
}

async function unlinkSelected() {
  // 只处理已关联的记录
  const linkedRows = selectedRows.value.filter(row => {
    const status = (row.postingStatus || '').toLowerCase()
    return status === 'posted' || status === 'linked'
  })
  
  if (!linkedRows.length) {
    ElMessage.warning('紐付け解除対象がありません。')
    return
  }
  
  try {
    await ElMessageBox.confirm(
      `${linkedRows.length}件の明細の紐付けを解除しますか？\n※関連する伝票は削除されません。`,
      '紐付け解除確認',
      {
        confirmButtonText: '解除',
        cancelButtonText: 'キャンセル',
        type: 'warning'
      }
    )
  } catch {
    return // キャンセル
  }
  
  const ids = linkedRows.map((item) => item.id)
  unlinkLoading.value = true
  
  try {
    const res = await api.post('/integrations/moneytree/transactions/unlink', { ids })
    const unlinkedCount = res.data?.unlinkedCount || 0
    
    // ローカルデータを更新
    for (const id of ids) {
      const idx = rows.value.findIndex((r) => r.id === id)
      if (idx >= 0) {
        rows.value[idx] = {
          ...rows.value[idx],
          postingStatus: 'pending',
          postingError: null,
          voucherNo: null
        }
      }
    }
    
    // 選択解除
    selectedRows.value = []
    tableRef.value?.clearSelection()
    
    ElMessage.success(`${unlinkedCount}件の紐付けを解除しました。`)
  } catch (err: any) {
    const msg = err?.response?.data?.error || err?.message || '紐付け解除に失敗しました。'
    ElMessage.error(msg)
  } finally {
    unlinkLoading.value = false
  }
}

function formatNumber(val: number) {
  return Number(val).toLocaleString('ja-JP', { minimumFractionDigits: 0, maximumFractionDigits: 2 })
}

function findSimRow(id: string) {
  return simulateSelectionSnapshot.value.find((item) => item.id === id)
}

function getSimDescription(id: string) {
  return findSimRow(id)?.description || '-'
}

function getSimAmount(id: string) {
  const row = findSimRow(id)
  if (!row) return '-'
  const amount = row.depositAmount ?? (row.withdrawalAmount ? -row.withdrawalAmount : 0)
  if (!amount) return '-'
  const sign = amount >= 0 ? '+' : '-'
  return `${sign}${formatNumber(Math.abs(amount))} ${row.currency || ''}`.trim()
}

function formatAccountLabel(code?: string | null, name?: string | null) {
  if (!code) return '-'
  return name ? `${code} (${name})` : code
}

function statusType(status?: string | null) {
  switch ((status || '').toLowerCase()) {
    case 'posted':
      return 'success'
    case 'linked':
      return 'success'
    case 'failed':
      return 'danger'
    case 'needs_rule':
      return 'warning'
    case 'duplicate_suspected':
      return 'warning'
    case 'unmatched':
      return 'warning'
    default:
      return 'info'
  }
}

function statusLabel(status?: string | null) {
  switch ((status || '').toLowerCase()) {
    case 'posted':
      return '仕訳済み'
    case 'linked':
      return '紐付け済み'
    case 'failed':
      return '失敗'
    case 'needs_rule':
      return '要ルール'
    case 'skipped':
      return 'スキップ'
    case 'duplicate_suspected':
      return '重複疑い'
    case 'unmatched':
      return '未マッチ'
    default:
      return '未処理'
  }
}

onMounted(() => {
  loadData()
})

function defaultRuleForm() {
  return {
    title: '',
    description: '',
    priority: 100,
    isActive: true,
    matcherText: '{\n}',
    actionText: '{\n}'
  }
}

function openRulesDialog() {
  rulesDialogVisible.value = true
  loadRules()
}

async function loadRules() {
  if (!rulesDialogVisible.value) return
  rulesLoading.value = true
  try {
    rules.value = await fetchMoneytreeRules(includeInactiveRules.value)
  } catch (err: any) {
    const msg = err?.response?.data?.error || err?.message || 'ルールの読み込みに失敗しました。'
    ElMessage.error(msg)
  } finally {
    rulesLoading.value = false
  }
}

function startCreateRule() {
  editingRuleId.value = null
  ruleForm.value = defaultRuleForm()
  ruleFormDialogVisible.value = true
}

function handleEditRule(rule: MoneytreeRule) {
  editingRuleId.value = rule.id
  ruleForm.value = {
    title: rule.title,
    description: rule.description ?? '',
    priority: rule.priority,
    isActive: rule.isActive,
    matcherText: formatJson(rule.matcher),
    actionText: formatJson(rule.action)
  }
  ruleFormDialogVisible.value = true
}

async function submitRule() {
  if (!ruleForm.value.title?.trim()) {
    ElMessage.error('名称を入力してください。')
    return
  }
  let matcherObj: Record<string, unknown>
  let actionObj: Record<string, unknown>
  try {
    matcherObj = JSON.parse(ruleForm.value.matcherText || '{}')
  } catch {
    ElMessage.error('Matcher JSON が正しくありません。')
    return
  }
  try {
    actionObj = JSON.parse(ruleForm.value.actionText || '{}')
  } catch {
    ElMessage.error('Action JSON が正しくありません。')
    return
  }
  const payload = {
    title: ruleForm.value.title.trim(),
    description: ruleForm.value.description?.trim() || null,
    priority: ruleForm.value.priority,
    matcher: matcherObj,
    action: actionObj,
    isActive: ruleForm.value.isActive
  }
  ruleFormLoading.value = true
  try {
    if (editingRuleId.value) {
      await updateMoneytreeRule(editingRuleId.value, payload)
      ElMessage.success('ルールを更新しました。')
    } else {
      await createMoneytreeRule(payload)
      ElMessage.success('ルールを作成しました。')
    }
    ruleFormDialogVisible.value = false
    ruleForm.value = defaultRuleForm()
    await loadRules()
  } catch (err: any) {
    const msg = err?.response?.data?.error || err?.message || '保存に失敗しました。'
    ElMessage.error(msg)
  } finally {
    ruleFormLoading.value = false
  }
}

async function handleDeleteRule(rule: MoneytreeRule) {
  try {
    await ElMessageBox.confirm(`ルール「${rule.title}」を削除しますか？`, '確認', {
      type: 'warning'
    })
    await deleteMoneytreeRule(rule.id)
    ElMessage.success('削除しました。')
    await loadRules()
  } catch (err) {
    if (err !== 'cancel') {
      const msg = (err as any)?.response?.data?.error || (err as any)?.message || '削除に失敗しました。'
      ElMessage.error(msg)
    }
  }
}

function formatJson(value: unknown) {
  try {
    return JSON.stringify(value ?? {}, null, 2)
  } catch {
    return String(value ?? '')
  }
}

function formatDateTime(value?: string | null) {
  if (!value) return '-'
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return value
  const yyyy = d.getFullYear()
  const mm = String(d.getMonth() + 1).padStart(2, '0')
  const dd = String(d.getDate()).padStart(2, '0')
  const hh = String(d.getHours()).padStart(2, '0')
  const mi = String(d.getMinutes()).padStart(2, '0')
  return `${yyyy}-${mm}-${dd} ${hh}:${mi}`
}
</script>

<style scoped>
/* 标题区域样式 */
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

.filters {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
  margin-bottom: 12px;
}
.op-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}
.selection-info {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-right: 8px;
  padding: 4px 8px;
  background: #f0f9eb;
  border-radius: 4px;
}
:deep(.mt-table .cell) {
  padding: 6px 8px;
  line-height: 18px;
}
.amount-positive {
  color: #1a73e8;
}
.amount-negative {
  color: #d93025;
}
:deep(.mt-table .cell) {
  padding: 6px 8px;
  line-height: 18px;
}
.sim-desc {
  font-weight: 600;
  color: #303133;
}
.sim-amount {
  font-size: 13px;
  color: #606266;
}
:deep(.simulate-dialog .el-dialog__header) {
  padding: 12px 20px;
}
:deep(.simulate-dialog .el-dialog__body) {
  padding: 0 28px 12px;
}
:deep(.simulate-dialog .el-dialog__footer) {
  padding: 12px 20px 20px;
}
:deep(.simulate-dialog .el-table__header-wrapper),
:deep(.simulate-dialog .el-table__body-wrapper) {
  overflow: visible !important;
}
:deep(.simulate-dialog .el-table__cell) {
  white-space: normal;
}
.voucher-detail-embed :deep(.page-toolbar),
.voucher-detail-embed :deep(.page-pagination) {
  display: none;
}
.voucher-detail-embed :deep(.page > .el-card),
.voucher-detail-embed :deep(.voucher-detail-only > .el-card),
.voucher-detail-embed :deep(.voucher-detail-only .detail-card) {
  border: none;
  box-shadow: none;
}
.sim-table-wrapper {
  display: flex;
  justify-content: center;
  padding: 0;
  margin: 0 0 12px 0;
}
.sim-table-wrapper .el-table {
  width: auto;
  min-width: 720px;
  margin: 0;
}
.rules-toolbar {
  display: flex;
  gap: 12px;
  align-items: center;
  margin-bottom: 12px;
}
.rule-json {
  display: flex;
  gap: 16px;
  flex-wrap: wrap;
}
.rule-json pre {
  background: #f5f7fa;
  border-radius: 4px;
  padding: 8px;
  max-height: 200px;
  overflow: auto;
  font-size: 12px;
  min-width: 260px;
}
.rule-form-dialog :deep(.el-dialog__body) {
  max-height: 70vh;
  overflow-y: auto;
}
.rule-form-dialog :deep(.el-textarea__inner) {
  font-family: Consolas, 'SFMono-Regular', 'Roboto Mono', monospace;
  word-break: break-all;
}
.sync-desc {
  font-size: 13px;
  line-height: 1.6;
  color: #606266;
  margin: 0 0 8px 0;
}
.import-mode-hint {
  font-size: 12px;
  color: #909399;
  margin-top: 8px;
  line-height: 1.5;
}
.account-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  line-height: 1.1;
}
.account-name {
  font-size: 12px;
  color: #303133;
}
.account-number {
  font-size: 12px;
  color: #666;
}
</style>


