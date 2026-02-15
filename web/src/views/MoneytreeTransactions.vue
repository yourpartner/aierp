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
        <el-select v-model="filters.status" style="width:160px">
          <el-option label="全ステータス" value="all" />
          <el-option label="失敗" value="failed" />
          <el-option label="ルール不足" value="needs_rule" />
          <el-option label="要確認（低置信度）" value="low_confidence" />
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
        :expand-row-keys="expandedRowKeys"
        @selection-change="handleSelectionChange"
      >
        <!-- 隐藏的展开列：AI 相談内联面板 -->
        <el-table-column type="expand" width="1" class-name="hidden-expand-col">
          <template #default="{ row }">
            <div v-if="aiPanelTxId === row.id" class="ai-consult-panel-inline" :style="{ width: aiPanelWidth }">
              <div class="ai-panel-body">
                <div v-if="aiPanelMessages.length" class="ai-panel-messages">
                  <div v-for="(msg, idx) in aiPanelMessages" :key="idx" class="ai-panel-msg" :class="msg.role">
                    <div class="ai-msg-content" v-html="formatAiMessage(msg.content)"></div>
                  </div>
                </div>
                <div v-if="aiPanelStatus === 'sending'" class="ai-panel-loading">
                  <el-icon class="is-loading"><Loading /></el-icon>
                  <span>AIが記帳方法を判断しています...</span>
                </div>
                <div v-if="aiPanelStatus === 'success'" class="ai-panel-result success">
                  <el-icon><CircleCheck /></el-icon>
                  <span>{{ aiPanelResultText }}</span>
                </div>
                <div v-if="aiPanelStatus === 'error'" class="ai-panel-result error">
                  <el-icon><CircleClose /></el-icon>
                  <span>{{ aiPanelResultText }}</span>
                </div>
              </div>
              <div v-if="aiPanelStatus !== 'success'" class="ai-panel-input">
                <el-input
                  ref="aiInputRef"
                  v-model="aiPanelInput"
                  :placeholder="'AIへの返答やヒントを入力（例：消耗品費で記帳して）'"
                  :disabled="aiPanelStatus === 'sending'"
                  @keyup.enter.ctrl="sendAiConsult"
                  size="default"
                />
                <el-button
                  type="primary"
                  :loading="aiPanelStatus === 'sending'"
                  :disabled="aiPanelStatus === 'sending'"
                  @click="sendAiConsult"
                >
                  送信
                </el-button>
              </div>
            </div>
          </template>
        </el-table-column>
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
        <el-table-column label="ステータス" width="140">
          <template #default="{ row }">
            <!-- 低置信度の記帳済み：警告表示 -->
            <el-tooltip
              v-if="isLowConfidence(row)"
              :content="`置信度: ${Math.round((row.postingConfidence || 0) * 100)}% — 確認が必要です`"
              placement="top"
              :show-after="300"
              effect="light"
            >
              <el-tag size="small" type="warning" class="status-tag-with-info">
                要確認
                <el-icon style="margin-left:2px;vertical-align:middle"><InfoFilled /></el-icon>
              </el-tag>
            </el-tooltip>
            <!-- 失敗/ルール不足：エラー情報付き -->
            <el-tooltip
              v-else-if="row.postingError && (row.postingStatus === 'needs_rule' || row.postingStatus === 'failed')"
              :content="row.postingError"
              placement="top"
              :show-after="300"
              effect="light"
              :popper-style="{ maxWidth: '400px', whiteSpace: 'pre-wrap' }"
            >
              <el-tag size="small" :type="statusType(row.postingStatus)" class="status-tag-with-info">
                {{ statusLabel(row.postingStatus) }}
                <el-icon style="margin-left:2px;vertical-align:middle"><InfoFilled /></el-icon>
              </el-tag>
            </el-tooltip>
            <!-- 通常ステータス -->
            <el-tag v-else size="small" :type="statusType(row.postingStatus)">
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
        <el-table-column v-if="showAiConsultColumn" label="操作" width="180" fixed="right">
          <template #default="{ row }">
            <div v-if="canConsultAi(row)" class="ops-cell">
              <!-- 低置信度の記帳済み：確認OK / 取消 ボタン -->
              <template v-if="isLowConfidence(row)">
                <el-button type="success" size="small" link @click="confirmLowConfidence(row)">
                  <el-icon style="margin-right:2px"><CircleCheck /></el-icon>確認OK
                </el-button>
                <el-button type="primary" size="small" link @click="toggleAiPanel(row)">
                  <el-icon style="margin-right:2px"><ChatDotRound /></el-icon>修正
                </el-button>
              </template>
              <!-- 失敗/ルール不足：AI相談 / 手工記帳 -->
              <template v-else>
                <el-button type="primary" size="small" link @click="toggleAiPanel(row)">
                  <el-icon style="margin-right:2px"><ChatDotRound /></el-icon>
                  {{ aiPanelTxId === row.id ? '閉じる' : 'AI相談' }}
                </el-button>
                <el-popover
                  :ref="(el: any) => { if (el) manualPopoverRefs[row.id] = el }"
                  trigger="click"
                  placement="left-start"
                  :width="320"
                  :visible="manualPostTxId === row.id"
                >
                  <template #reference>
                    <el-button type="warning" size="small" link @click="toggleManualPost(row)">
                      <el-icon style="margin-right:2px"><Edit /></el-icon>手工
                    </el-button>
                  </template>
                  <div class="manual-post-form">
                    <div class="manual-post-label">対方科目を選択</div>
                    <el-select
                      v-model="manualPostAccount"
                      filterable
                      remote
                      reserve-keyword
                      :remote-method="searchAccounts"
                      :loading="accountSearchLoading"
                      placeholder="科目名/コードで検索"
                      size="default"
                      style="width:100%"
                      @change="onAccountSelected"
                    >
                      <el-option
                        v-for="acc in accountOptions"
                        :key="acc.code"
                        :value="acc.code"
                        :label="`${acc.code} ${acc.name}`"
                      />
                    </el-select>
                    <el-input
                      v-model="manualPostNote"
                      placeholder="メモ（任意）"
                      size="small"
                      style="margin-top:8px"
                      clearable
                    />
                    <div style="margin-top:10px;text-align:right">
                      <el-button size="small" @click="manualPostTxId = ''">取消</el-button>
                      <el-button
                        type="primary"
                        size="small"
                        :loading="manualPostLoading"
                        :disabled="!manualPostAccount"
                        @click="submitManualPost(row)"
                      >
                        記帳
                      </el-button>
                    </div>
                  </div>
                </el-popover>
              </template>
            </div>
          </template>
        </el-table-column>
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
import { CreditCard, ChatDotRound, Loading, CircleCheck, CircleClose, InfoFilled, Edit } from '@element-plus/icons-vue'
import api from '../api'
import VouchersList from './VouchersList.vue'
import {
  fetchMoneytreeTransactions,
  fetchMoneytreePostingTaskTransactions,
  runMoneytreePosting,
  fetchMoneytreeRules,
  createMoneytreeRule,
  updateMoneytreeRule,
  deleteMoneytreeRule,
  importMoneytreeTransactions,
  manualPostTransaction,
  type MoneytreeTransactionItem,
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
const showAiConsultColumn = computed(() => rows.value.some((row) => canConsultAi(row)))
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

// === 低置信度判定・確認 ===
function isLowConfidence(row: MoneytreeTransactionItem): boolean {
  return row.postingStatus === 'posted' && row.postingConfidence != null && row.postingConfidence < 0.8
}

async function confirmLowConfidence(row: MoneytreeTransactionItem) {
  try {
    await api.post(`/integrations/moneytree/transactions/${row.id}/confirm`)
    ElMessage.success('確認しました')
    const idx = rows.value.findIndex(r => r.id === row.id)
    if (idx >= 0) {
      rows.value[idx] = { ...rows.value[idx], postingConfidence: 1.0 }
    }
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || '確認に失敗しました')
  }
}

// === AI相談：内联对话面板（展开行方式） ===
function canConsultAi(row: MoneytreeTransactionItem): boolean {
  const status = (row.postingStatus || '').toLowerCase()
  // 失敗、ルール不足、低置信度の場合にAI相談/手工記帳を表示
  return status === 'needs_rule' || status === 'failed' || isLowConfidence(row)
}

const aiPanelTxId = ref<string>('')
const aiPanelRow = ref<MoneytreeTransactionItem | null>(null)
const aiPanelInput = ref('')
const aiPanelStatus = ref<'idle' | 'sending' | 'success' | 'error'>('idle')
const aiPanelResultText = ref('')
const aiPanelMessages = ref<{ role: string; content: string }[]>([])
const aiInputRef = ref<any>(null)
const aiSessionId = ref<string>('')  // 多轮对话保持同一 session
const aiIsFirstMessage = ref(true)   // 标记是否是第一条消息
// expand-row-keys 控制：只展开当前 AI 相談的行
const expandedRowKeys = computed(() => aiPanelTxId.value ? [aiPanelTxId.value] : [])
// AI 面板宽度：动态测量表格可视区域
const aiPanelWidth = ref('100%')
function measurePanelWidth() {
  const wrapper = tableRef.value?.$el?.querySelector('.el-table__body-wrapper')
  if (wrapper) {
    aiPanelWidth.value = wrapper.clientWidth + 'px'
  }
}

function toggleAiPanel(row: MoneytreeTransactionItem) {
  if (aiPanelTxId.value === row.id) {
    closeAiPanel()
  } else {
    aiPanelTxId.value = row.id
    aiPanelRow.value = row
    aiPanelInput.value = ''
    aiPanelStatus.value = 'idle'
    aiPanelResultText.value = ''
    aiPanelMessages.value = []
    aiSessionId.value = ''
    aiIsFirstMessage.value = true
    // 展开行渲染需要稍微等一下 DOM 更新
    measurePanelWidth()
    nextTick(() => {
      setTimeout(() => {
        measurePanelWidth()
        // 自動的に最初のメッセージを送信（AI が主動的にガイドする）
        sendAiConsult()
      }, 80)
    })
  }
}

function closeAiPanel() {
  aiPanelTxId.value = ''
  aiPanelRow.value = null
  aiPanelStatus.value = 'idle'
}

function buildBankMessage(row: MoneytreeTransactionItem, userHint: string): string {
  const isW = (row.withdrawalAmount ?? 0) > 0
  const amount = isW ? row.withdrawalAmount : row.depositAmount
  const typeLabel = isW ? '出金' : '入金'
  const amountFormatted = Number(amount || 0).toLocaleString()

  let msg = `以下の銀行取引を記帳してください。\n`
  msg += `取引日: ${row.transactionDate || ''}\n`
  msg += `種別: ${typeLabel}\n`
  msg += `金額: ¥${amountFormatted}\n`
  if (row.description) msg += `摘要: ${row.description}\n`
  if (row.bankName) msg += `金融機関: ${row.bankName}\n`
  if (row.accountNumber) msg += `口座番号: ${row.accountNumber}\n`
  // 自動記帳が失敗した理由を伝える（AI がより的確に質問できるように）
  if (row.postingError) msg += `\n自動記帳失敗理由: ${row.postingError}\n`
  if (userHint.trim()) msg += `\nユーザー指示: ${userHint.trim()}\n`
  msg += `\n銀行取引ID: ${row.id}`
  msg += `\n\nこの取引は自動記帳に失敗しました。ユーザーに必要な情報を確認しながら、記帳を完了してください。`
  return msg
}

async function sendAiConsult() {
  const row = aiPanelRow.value
  if (!row) return
  const userHint = aiPanelInput.value.trim()

  // 第一条消息：发送完整银行交易信息；后续消息：只发用户回复
  let message: string
  if (aiIsFirstMessage.value) {
    message = buildBankMessage(row, userHint)
  } else {
    message = userHint || '記帳を続けてください'
  }

  if (userHint) {
    aiPanelMessages.value.push({ role: 'user', content: userHint })
  }
  aiPanelInput.value = ''
  aiPanelStatus.value = 'sending'

  try {
    const payload: Record<string, any> = {
      message,
      language: 'ja',
      bankTransactionId: row.id
    }
    // 多轮对话：传递 sessionId 保持上下文
    if (aiSessionId.value) {
      payload.sessionId = aiSessionId.value
    }
    const resp = await api.post('/ai/agent/message', payload)
    const data = resp.data
    // 保存 sessionId 供后续消息使用
    if (data?.sessionId) {
      aiSessionId.value = data.sessionId
    }
    aiIsFirstMessage.value = false
    // 从响应中提取 AI 的回复
    const aiMessages: any[] = data?.messages || []
    for (const m of aiMessages) {
      if (m.role === 'assistant' && m.content) {
        aiPanelMessages.value.push({ role: 'assistant', content: m.content })
      }
    }
    // 判断是否成功创建了凭证
    const successMsg = aiMessages.find((m: any) =>
      m.role === 'assistant' && m.status === 'success' && m.content
    )
    if (successMsg) {
      aiPanelStatus.value = 'success'
      aiPanelResultText.value = successMsg.content
      // 更新本地行数据
      const idx = rows.value.findIndex(r => r.id === row.id)
      if (idx >= 0) {
        rows.value[idx] = { ...rows.value[idx], postingStatus: 'posted' }
      }
    } else {
      // AI 回复了但没有成功标记（可能是追问或错误）
      aiPanelStatus.value = 'idle'
      // AI の応答後、入力欄にフォーカス
      nextTick(() => aiInputRef.value?.focus?.())
    }
  } catch (err: any) {
    const errText = err?.response?.data?.error || err?.message || '処理に失敗しました'
    aiPanelMessages.value.push({ role: 'assistant', content: `エラー: ${errText}` })
    aiPanelStatus.value = 'error'
    aiPanelResultText.value = errText
  }
}

function formatAiMessage(content: string): string {
  // 简单的换行转 HTML
  return content.replace(/\n/g, '<br>')
}

// === 手工快速記帳 ===
const manualPostTxId = ref('')
const manualPostAccount = ref('')
const manualPostNote = ref('')
const manualPostLoading = ref(false)
const accountOptions = ref<{ code: string; name: string }[]>([])
const accountSearchLoading = ref(false)
const manualPopoverRefs: Record<string, any> = {}

function toggleManualPost(row: MoneytreeTransactionItem) {
  if (manualPostTxId.value === row.id) {
    manualPostTxId.value = ''
  } else {
    manualPostTxId.value = row.id
    manualPostAccount.value = ''
    manualPostNote.value = ''
    accountOptions.value = []
  }
}

async function searchAccounts(query: string) {
  if (!query || query.length < 1) {
    accountOptions.value = []
    return
  }
  accountSearchLoading.value = true
  try {
    const dsl = {
      filters: [
        {
          operator: 'or',
          conditions: [
            { field: 'payload.name', op: 'contains', value: query },
            { field: 'account_code', op: 'contains', value: query }
          ]
        }
      ],
      page: 1,
      pageSize: 20,
      orderBy: 'account_code',
      orderDir: 'asc'
    }
    const resp = await api.post('/objects/account/search', dsl)
    const items = resp.data?.items || resp.data?.rows || []
    accountOptions.value = items.map((item: any) => ({
      code: item.account_code || item.accountCode || '',
      name: item.payload?.name || item.name || ''
    }))
  } catch {
    accountOptions.value = []
  } finally {
    accountSearchLoading.value = false
  }
}

function onAccountSelected(_val: string) {
  // 选中科目后自动聚焦到メモ字段（可选交互优化）
}

async function submitManualPost(row: MoneytreeTransactionItem) {
  if (!manualPostAccount.value) return
  manualPostLoading.value = true
  try {
    const result = await manualPostTransaction(row.id, {
      counterpartAccountCode: manualPostAccount.value,
      note: manualPostNote.value || undefined
    })
    if (result.success) {
      ElMessage.success(`記帳完了: ${result.voucherNo}`)
      // 更新本地行数据
      const idx = rows.value.findIndex(r => r.id === row.id)
      if (idx >= 0) {
        rows.value[idx] = { ...rows.value[idx], postingStatus: 'posted', voucherNo: result.voucherNo, postingError: null }
      }
      manualPostTxId.value = ''
    } else {
      ElMessage.error(result.error || '記帳に失敗しました')
    }
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || err?.message || '記帳に失敗しました')
  } finally {
    manualPostLoading.value = false
  }
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

.status-tag-with-info {
  cursor: pointer;
}
.ops-cell {
  display: flex;
  gap: 4px;
  align-items: center;
}
.manual-post-form {
  padding: 4px 0;
}
.manual-post-label {
  font-size: 13px;
  color: #606266;
  margin-bottom: 6px;
  font-weight: 500;
}

/* === 隐藏展开列的箭头按钮和列宽（由 AI相談 按钮控制展开） === */
:deep(.hidden-expand-col) {
  width: 0 !important;
  min-width: 0 !important;
  max-width: 0 !important;
  padding: 0 !important;
  border: none !important;
  overflow: hidden;
}
:deep(.hidden-expand-col .cell) {
  display: none !important;
}
/* 展开行内容区域取消默认内边距 */
:deep(.el-table__expanded-cell) {
  padding: 0 !important;
  background: #f8fbff;
}
/* === AI 相談 内联面板（展开行内） === */
.ai-consult-panel-inline {
  border-top: 2px solid #409eff;
  background: #f8fbff;
  /* 固定在可视区域左侧，宽度由 JS 动态测量 */
  position: sticky;
  left: 0;
  box-sizing: border-box;
}
.ai-panel-body {
  padding: 10px 16px;
  min-height: 36px;
}
.ai-panel-messages {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-bottom: 10px;
}
.ai-panel-msg {
  padding: 8px 12px;
  border-radius: 8px;
  font-size: 13px;
  line-height: 1.5;
  max-width: 80%;
}
.ai-panel-msg.user {
  background: #409eff;
  color: #fff;
  align-self: flex-end;
  border-bottom-right-radius: 2px;
}
.ai-panel-msg.assistant {
  background: #fff;
  color: #303133;
  border: 1px solid #e4e7ed;
  align-self: flex-start;
  border-bottom-left-radius: 2px;
}
.ai-msg-content {
  word-break: break-word;
}
.ai-panel-loading {
  display: flex;
  align-items: center;
  gap: 8px;
  color: #909399;
  font-size: 13px;
  padding: 8px 0;
}
.ai-panel-result {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  border-radius: 6px;
  font-size: 13px;
  font-weight: 500;
}
.ai-panel-result.success {
  background: #f0f9eb;
  color: #67c23a;
  border: 1px solid #e1f3d8;
}
.ai-panel-result.error {
  background: #fef0f0;
  color: #f56c6c;
  border: 1px solid #fde2e2;
}
.ai-panel-input {
  display: flex;
  gap: 8px;
  padding: 0 16px 12px;
}
.ai-panel-input .el-input {
  flex: 1;
}
</style>


