<template>
  <div class="page page-wide">
    <el-card class="closing-card">
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Calendar /></el-icon>
            <span class="page-header-title">{{ text.tables.monthlyClosing.title }}</span>
          </div>
          <div class="page-header-actions">
            <el-date-picker
              v-model="yearValue"
              type="year"
              format="YYYY"
              value-format="YYYY"
              class="year-picker"
              :placeholder="String(currentYear)"
            />
            <el-button :loading="loading" @click="loadData">{{ text.buttons.refresh }}</el-button>
          </div>
        </div>
      </template>

      <!-- 月份选择网格 -->
      <div class="months-grid" v-loading="loading">
        <div
          v-for="entry in months"
          :key="entry.yearMonth"
          class="month-card"
          :class="{ 'is-selected': selectedMonth === entry.yearMonth, [`status-${entry.status}`]: true }"
          @click="selectMonth(entry.yearMonth)"
        >
          <div class="month-label">{{ entry.label }}</div>
          <div class="month-status">
            <el-tag :type="statusTagType(entry.status)" size="small">{{ statusText(entry.status) }}</el-tag>
          </div>
        </div>
      </div>

      <!-- 选中月份的详情 -->
      <div v-if="selectedMonth" class="closing-detail">
        <el-divider>{{ selectedMonth }} {{ text.tables.monthlyClosing.detail }}</el-divider>
        
        <!-- 未开始时显示开始按钮 -->
        <div v-if="!closingData" class="closing-actions">
          <el-button type="primary" @click="startClosing" :loading="actionLoading">
            {{ text.tables.monthlyClosing.start }}
          </el-button>
        </div>

        <!-- 已开始显示详细信息 -->
        <div v-else>
          <!-- 状态和进度 -->
          <div class="status-bar">
            <el-steps :active="statusStep" align-center finish-status="success">
              <el-step :title="text.tables.monthlyClosing.steps.checking" />
              <el-step :title="text.tables.monthlyClosing.steps.adjusting" />
              <el-step :title="text.tables.monthlyClosing.steps.approval" />
              <el-step :title="text.tables.monthlyClosing.steps.closed" />
            </el-steps>
          </div>

          <!-- チェック項目 -->
          <el-card shadow="never" class="check-section">
            <template #header>
              <div class="section-header">
                <span>{{ text.tables.monthlyClosing.checkItems }}</span>
                <el-button
                  type="primary"
                  size="small"
                  :loading="checkLoading"
                  :disabled="closingData?.status === 'closed'"
                  @click="runAllChecks"
                >
                  {{ text.tables.monthlyClosing.runChecks }}
                </el-button>
              </div>
            </template>
            <el-table :data="checkResults" stripe size="small">
              <el-table-column prop="itemKey" :label="text.tables.monthlyClosing.checkItem" width="180">
                <template #default="{ row }">
                  {{ getCheckItemName(row.itemKey) }}
                </template>
              </el-table-column>
              <el-table-column prop="status" :label="text.tables.monthlyClosing.checkStatus" width="120">
                <template #default="{ row }">
                  <el-tag :type="checkStatusType(row.status)" size="small">
                    {{ checkStatusText(row.status) }}
                  </el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="message" :label="text.tables.monthlyClosing.checkMessage" />
              <el-table-column :label="text.tables.monthlyClosing.checkAction" width="200">
                <template #default="{ row }">
                  <div class="check-actions">
                    <!-- 手动确认按钮 -->
                    <el-button
                      v-if="isManualCheck(row.itemKey) && row.status === 'pending'"
                      size="small"
                      @click="openManualCheckDialog(row)"
                      :disabled="closingData?.status === 'closed'"
                    >
                      {{ text.tables.monthlyClosing.confirm }}
                    </el-button>
                    
                    <!-- 查看详情按钮 -->
                    <el-button
                      v-if="hasDetailAction(row)"
                      size="small"
                      type="primary"
                      link
                      @click="viewCheckDetail(row)"
                    >
                      詳細
                    </el-button>
                    
                    <!-- 跳转操作按钮 -->
                    <el-button
                      v-if="hasNavigateAction(row)"
                      size="small"
                      type="warning"
                      link
                      @click="navigateToAction(row)"
                    >
                      {{ getActionLabel(row.itemKey) }}
                    </el-button>
                  </div>
                </template>
              </el-table-column>
            </el-table>
          </el-card>

          <!-- 消費税集計 -->
          <el-card shadow="never" class="tax-section" v-if="closingData?.consumptionTaxSummary">
            <template #header>
              <span>{{ text.tables.monthlyClosing.taxSummary }}</span>
            </template>
            <div class="tax-grid">
              <div class="tax-item">
                <div class="tax-label">{{ text.tables.monthlyClosing.outputTax }}</div>
                <div class="tax-value">¥{{ formatNumber(taxSummary.totalOutputTax) }}</div>
              </div>
              <div class="tax-item">
                <div class="tax-label">{{ text.tables.monthlyClosing.inputTax }}</div>
                <div class="tax-value">¥{{ formatNumber(taxSummary.totalInputTax) }}</div>
              </div>
              <div class="tax-item highlight">
                <div class="tax-label">{{ text.tables.monthlyClosing.netTax }}</div>
                <div class="tax-value">¥{{ formatNumber(taxSummary.netTax) }} ({{ taxSummary.direction }})</div>
              </div>
            </div>
          </el-card>

          <!-- アクションボタン -->
          <div class="closing-actions" v-if="closingData?.status !== 'closed'">
            <el-button @click="calculateTaxSummary" :loading="actionLoading">
              {{ text.tables.monthlyClosing.calcTax }}
            </el-button>
            <el-button
              type="warning"
              @click="submitForApproval"
              :loading="actionLoading"
              :disabled="!canSubmitApproval"
            >
              {{ text.tables.monthlyClosing.submitApproval }}
            </el-button>
            <el-button
              type="success"
              @click="closeMonth"
              :loading="actionLoading"
              :disabled="!canClose"
            >
              {{ text.tables.monthlyClosing.close }}
            </el-button>
          </div>

          <!-- 締め済み表示 -->
          <div v-else class="closed-info">
            <el-alert type="success" :closable="false">
              <template #title>
                {{ text.tables.monthlyClosing.closedMessage }}
                <span v-if="closingData.closedAt">
                  ({{ formatDateTime(closingData.closedAt) }})
                </span>
              </template>
            </el-alert>
            <el-button
              type="danger"
              plain
              @click="reopenDialog = true"
              :loading="actionLoading"
              style="margin-top: 12px"
            >
              {{ text.tables.monthlyClosing.reopen }}
            </el-button>
          </div>
        </div>
      </div>
    </el-card>

    <!-- 手動チェック確認ダイアログ -->
    <el-dialog v-model="manualCheckDialog" :title="text.tables.monthlyClosing.manualCheck" width="480px">
      <el-form label-position="top">
        <el-form-item :label="text.tables.monthlyClosing.checkItem">
          {{ getCheckItemName(currentCheckItem?.itemKey || '') }}
        </el-form-item>
        <el-form-item :label="text.tables.monthlyClosing.checkResult">
          <el-radio-group v-model="manualCheckStatus">
            <el-radio value="passed">{{ text.tables.monthlyClosing.statusPassed }}</el-radio>
            <el-radio value="warning">{{ text.tables.monthlyClosing.statusWarning }}</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item :label="text.tables.monthlyClosing.comment">
          <el-input v-model="manualCheckComment" type="textarea" :rows="3" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="manualCheckDialog = false">{{ text.buttons.cancel }}</el-button>
        <el-button type="primary" @click="confirmManualCheck" :loading="actionLoading">
          {{ text.buttons.confirm }}
        </el-button>
      </template>
    </el-dialog>

    <!-- チェック詳細ダイアログ -->
    <el-dialog v-model="detailDialog" :title="detailTitle" width="1100px" :style="{ maxWidth: '95vw' }" destroy-on-close>
      <div v-loading="detailLoading">
        <el-alert v-if="detailErrorMessage" type="error" :closable="false" :title="detailErrorMessage" style="margin-bottom:10px" />
        <div v-if="detailData.length" style="max-width:100%; overflow-x:auto;">
          <el-table :data="detailData" stripe size="small" max-height="400" style="width:100%">
            <el-table-column v-for="col in detailColumns" :key="col.prop" :prop="col.prop" :label="col.label" :width="col.width" show-overflow-tooltip>
              <template #default="{ row }">
                <el-link
                  v-if="col.prop === 'docNo' && (row.voucherId || row.voucherNo)"
                  type="primary"
                  @click="openVoucherDetail(row)"
                >
                  {{ row.docNo }}
                </el-link>
                <span v-else-if="col.prop === 'docNo'">{{ row.docNo }}</span>
                <span v-else-if="col.prop === 'error'">{{ row.error || '' }}</span>
                <span v-else>{{ row[col.prop] ?? '' }}</span>
              </template>
            </el-table-column>
        </el-table>
        </div>
        <el-empty v-else :description="'データがありません'" />
      </div>
      <template #footer>
        <el-button @click="detailDialog = false">閉じる</el-button>
        <el-button v-if="detailNavigatePath" type="primary" @click="navigateFromDetail">
          {{ detailNavigateLabel }}
        </el-button>
      </template>
    </el-dialog>

    <!-- 伝票詳細（参照） -->
    <el-dialog v-model="voucherDialogVisible" width="auto" append-to-body destroy-on-close class="voucher-detail-dialog">
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VouchersList v-if="voucherDialogVisible" ref="voucherDetailRef" class="voucher-detail-embed" :allow-edit="false" />
      </div>
    </el-dialog>

    <!-- 消込管理ダイアログ（ページ遷移せずに月結画面内で処理） -->
    <el-dialog
      v-model="clearingDialogVisible"
      :title="clearingDialogTitle"
      width="95%"
      append-to-body
      destroy-on-close
    >
      <div v-if="clearingDialogMode === 'receivable'" style="padding: 12px 4px">
        <el-alert
          type="info"
          :closable="false"
          title="現在、応収（AR）の消込管理画面は未実装です。"
        />
        <div style="margin-top: 8px; color: #6b7280; font-size: 13px">
          月結チェックの「詳細」から対象伝票を確認し、必要に応じて仕訳を修正してください。
        </div>
      </div>
      <BankPayment v-else class="clearing-embed" />
      <template #footer>
        <el-button @click="clearingDialogVisible = false">{{ text.buttons.close }}</el-button>
      </template>
    </el-dialog>

    <!-- 再開ダイアログ -->
    <el-dialog v-model="reopenDialog" :title="text.tables.monthlyClosing.reopen" width="480px">
      <el-form label-position="top">
        <el-form-item :label="text.tables.monthlyClosing.reopenReason" required>
          <el-input v-model="reopenReason" type="textarea" :rows="3" :placeholder="text.tables.monthlyClosing.reopenReasonPlaceholder" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="reopenDialog = false">{{ text.buttons.cancel }}</el-button>
        <el-button type="danger" @click="reopenMonth" :loading="actionLoading" :disabled="!reopenReason.trim()">
          {{ text.tables.monthlyClosing.confirmReopen }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted, nextTick } from 'vue'
import api from '../api'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Calendar } from '@element-plus/icons-vue'
import { useI18n } from '../i18n'
import BankPayment from './BankPayment.vue'
import VouchersList from './VouchersList.vue'

const { text, lang } = useI18n()

// 基本状态
const loading = ref(false)
const actionLoading = ref(false)
const checkLoading = ref(false)
const currentYear = ref(new Date().getFullYear())
const yearValue = ref(String(currentYear.value))
const selectedMonth = ref<string | null>(null)
const closingData = ref<any>(null)
const checkItems = ref<any[]>([])
const checkResults = ref<any[]>([])

// 对话框状态
const manualCheckDialog = ref(false)
const currentCheckItem = ref<any>(null)
const manualCheckStatus = ref('passed')
const manualCheckComment = ref('')
const reopenDialog = ref(false)
const reopenReason = ref('')

// 详情对话框状态
const detailDialog = ref(false)
const detailLoading = ref(false)
const detailTitle = ref('')
const detailData = ref<any[]>([])
const detailColumns = ref<{ prop: string; label: string; width?: number }[]>([])
const detailNavigatePath = ref('')
const detailNavigateLabel = ref('')
const detailErrorMessage = ref('')

// 伝票詳細弹窗（只读）
const voucherDialogVisible = ref(false)
const voucherDetailRef = ref<InstanceType<typeof VouchersList> | null>(null)

// 消込管理弹窗
const clearingDialogVisible = ref(false)
const clearingDialogMode = ref<'payable' | 'receivable'>('payable')
const clearingDialogTitle = computed(() => (clearingDialogMode.value === 'receivable' ? '消込管理（売掛）' : '消込管理（買掛）'))

// 月份列表
interface MonthEntry {
  yearMonth: string
  label: string
  status: string
}
const months = ref<MonthEntry[]>([])

// 消费税摘要
const taxSummary = computed(() => {
  const data = closingData.value?.consumptionTaxSummary
  if (!data) return { totalOutputTax: 0, totalInputTax: 0, netTax: 0, direction: '納付' }
  return typeof data === 'string' ? JSON.parse(data) : data
})

// 状态步骤
const statusStep = computed(() => {
  const status = closingData.value?.status
  switch (status) {
    case 'open': return 0
    case 'checking': return 0
    case 'adjusting': return 1
    case 'pending_approval': return 2
    case 'closed': return 4
    case 'reopened': return 1
    default: return 0
  }
})

// 能否提交审批
const canSubmitApproval = computed(() => {
  const status = closingData.value?.status
  return status === 'adjusting' || status === 'reopened'
})

// 能否关闭
const canClose = computed(() => {
  const status = closingData.value?.status
  return status === 'adjusting' || status === 'pending_approval' || status === 'reopened'
})

// 加载年度数据
async function loadData() {
  loading.value = true
  try {
    // 获取本年度各月的月结状态
    const resp = await api.get('/monthly-closing', { params: { year: currentYear.value, limit: 12 } })
    const data = Array.isArray(resp.data) ? resp.data : []
    
    // 构建月份列表
    const list: MonthEntry[] = []
    for (let m = 1; m <= 12; m++) {
      const ym = `${currentYear.value}-${String(m).padStart(2, '0')}`
      const existing = data.find((d: any) => d.yearMonth === ym)
      list.push({
        yearMonth: ym,
        label: monthLabel(m - 1),
        status: existing?.status || 'open'
      })
    }
    months.value = list

    // 加载检查项目
    await loadCheckItems()

    // 如果有选中的月份，重新加载详情
    if (selectedMonth.value) {
      await loadClosingDetail(selectedMonth.value)
    }
  } catch (err) {
    console.error('load data failed', err)
    ElMessage.error(text.value.tables.monthlyClosing.loadFailed)
  } finally {
    loading.value = false
  }
}

// 加载检查项目
async function loadCheckItems() {
  try {
    const resp = await api.get('/monthly-closing/check-items')
    const items = Array.isArray(resp.data) ? resp.data : []
    // ar_uncleared は ar_overdue（应收逾期检查）に統合
    checkItems.value = items.filter((x: any) => x?.itemKey !== 'ar_uncleared')
  } catch (err) {
    console.error('load check items failed', err)
  }
}

// 加载月结详情
async function loadClosingDetail(yearMonth: string) {
  try {
    const resp = await api.get(`/monthly-closing/${yearMonth}`)
    closingData.value = resp.data
    const results = resp.data?.checkResults || []
    // ar_uncleared は ar_overdue（应收逾期检查）に統合
    checkResults.value = (Array.isArray(results) ? results : []).filter((x: any) => x?.itemKey !== 'ar_uncleared')
  } catch (err: any) {
    if (err?.response?.status === 404) {
      closingData.value = null
      checkResults.value = []
    } else {
      console.error('load closing detail failed', err)
    }
  }
}

// 选择月份
function selectMonth(yearMonth: string) {
  selectedMonth.value = yearMonth
  loadClosingDetail(yearMonth)
}

// 开始月结
async function startClosing() {
  if (!selectedMonth.value) return
  actionLoading.value = true
  try {
    const resp = await api.post('/monthly-closing/start', { yearMonth: selectedMonth.value })
    closingData.value = resp.data
    const results = resp.data?.checkResults || []
    checkResults.value = (Array.isArray(results) ? results : []).filter((x: any) => x?.itemKey !== 'ar_uncleared')
    ElMessage.success(text.value.tables.monthlyClosing.startSuccess)
    await loadData()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || text.value.tables.monthlyClosing.startFailed)
  } finally {
    actionLoading.value = false
  }
}

// 运行所有检查
async function runAllChecks() {
  if (!selectedMonth.value) return
  checkLoading.value = true
  try {
    const resp = await api.post(`/monthly-closing/${selectedMonth.value}/check`)
    const results = resp.data?.results || []
    checkResults.value = (Array.isArray(results) ? results : []).filter((x: any) => x?.itemKey !== 'ar_uncleared')
    ElMessage.success(text.value.tables.monthlyClosing.checkSuccess)
    await loadClosingDetail(selectedMonth.value)
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || text.value.tables.monthlyClosing.checkFailed)
  } finally {
    checkLoading.value = false
  }
}

// 打开手动检查对话框
function openManualCheckDialog(item: any) {
  currentCheckItem.value = item
  manualCheckStatus.value = 'passed'
  manualCheckComment.value = ''
  manualCheckDialog.value = true
}

// 确认手动检查
async function confirmManualCheck() {
  if (!selectedMonth.value || !currentCheckItem.value) return
  actionLoading.value = true
  try {
    await api.put(`/monthly-closing/${selectedMonth.value}/check/${currentCheckItem.value.itemKey}`, {
      status: manualCheckStatus.value,
      comment: manualCheckComment.value
    })
    ElMessage.success(text.value.tables.monthlyClosing.checkConfirmed)
    manualCheckDialog.value = false
    await loadClosingDetail(selectedMonth.value)
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || text.value.tables.monthlyClosing.checkFailed)
  } finally {
    actionLoading.value = false
  }
}

// 计算消费税
async function calculateTaxSummary() {
  if (!selectedMonth.value) return
  actionLoading.value = true
  try {
    await api.post(`/monthly-closing/${selectedMonth.value}/tax-summary`)
    ElMessage.success(text.value.tables.monthlyClosing.taxCalcSuccess)
    await loadClosingDetail(selectedMonth.value)
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || text.value.tables.monthlyClosing.taxCalcFailed)
  } finally {
    actionLoading.value = false
  }
}

// 提交审批
async function submitForApproval() {
  if (!selectedMonth.value) return
  actionLoading.value = true
  try {
    await api.post(`/monthly-closing/${selectedMonth.value}/submit-approval`, { submittedBy: 'user' })
    ElMessage.success(text.value.tables.monthlyClosing.submitSuccess)
    await loadClosingDetail(selectedMonth.value)
    await loadData()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || text.value.tables.monthlyClosing.submitFailed)
  } finally {
    actionLoading.value = false
  }
}

// 关闭月份
async function closeMonth() {
  if (!selectedMonth.value) return
  
  try {
    await ElMessageBox.confirm(
      text.value.tables.monthlyClosing.closeConfirmMessage,
      text.value.tables.monthlyClosing.closeConfirmTitle,
      { type: 'warning' }
    )
  } catch {
    return
  }

  actionLoading.value = true
  try {
    await api.post(`/monthly-closing/${selectedMonth.value}/close`, { closedBy: 'user' })
    ElMessage.success(text.value.tables.monthlyClosing.closeSuccess)
    await loadClosingDetail(selectedMonth.value)
    await loadData()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || text.value.tables.monthlyClosing.closeFailed)
  } finally {
    actionLoading.value = false
  }
}

// 重开月份
async function reopenMonth() {
  if (!selectedMonth.value || !reopenReason.value.trim()) return
  actionLoading.value = true
  try {
    await api.post(`/monthly-closing/${selectedMonth.value}/reopen`, {
      reopenedBy: 'user',
      reason: reopenReason.value
    })
    ElMessage.success(text.value.tables.monthlyClosing.reopenSuccess)
    reopenDialog.value = false
    reopenReason.value = ''
    await loadClosingDetail(selectedMonth.value)
    await loadData()
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || text.value.tables.monthlyClosing.reopenFailed)
  } finally {
    actionLoading.value = false
  }
}

// 辅助函数
function monthLabel(index: number) {
  const formatter = lang.value === 'ja' ? 'ja-JP' : lang.value === 'zh' ? 'zh-CN' : 'en-US'
  const date = new Date(currentYear.value, index, 1)
  return date.toLocaleDateString(formatter, { month: 'short' })
}

function statusTagType(status: string) {
  switch (status) {
    case 'closed': return 'success'
    case 'checking':
    case 'adjusting': return 'warning'
    case 'pending_approval': return 'info'
    case 'reopened': return 'danger'
    default: return 'info'
  }
}

function statusText(status: string) {
  const map: Record<string, string> = {
    open: text.value.tables.monthlyClosing.statusOpen,
    checking: text.value.tables.monthlyClosing.statusChecking,
    adjusting: text.value.tables.monthlyClosing.statusAdjusting,
    pending_approval: text.value.tables.monthlyClosing.statusPendingApproval,
    closed: text.value.tables.monthlyClosing.statusClosed,
    reopened: text.value.tables.monthlyClosing.statusReopened
  }
  return map[status] || status
}

function checkStatusType(status: string) {
  switch (status) {
    case 'passed': return 'success'
    case 'warning': return 'warning'
    case 'failed': return 'danger'
    case 'info': return 'info'
    default: return ''
  }
}

function checkStatusText(status: string) {
  const map: Record<string, string> = {
    passed: text.value.tables.monthlyClosing.statusPassed,
    warning: text.value.tables.monthlyClosing.statusWarning,
    failed: text.value.tables.monthlyClosing.statusFailed,
    info: text.value.tables.monthlyClosing.statusInfo,
    pending: text.value.tables.monthlyClosing.statusPending,
    skipped: text.value.tables.monthlyClosing.statusSkipped
  }
  return map[status] || status
}

function getCheckItemName(itemKey: string) {
  if (itemKey === 'ar_overdue') return lang.value === 'zh' ? '应收账款逾期检查' : '売掛金逾期チェック'
  const item = checkItems.value.find((i: any) => i.itemKey === itemKey)
  if (!item) return itemKey
  if (lang.value === 'ja') return item.itemNameJa || item.itemNameEn || itemKey
  if (lang.value === 'zh') return item.itemNameZh || item.itemNameJa || itemKey
  return item.itemNameEn || item.itemNameJa || itemKey
}

function isManualCheck(itemKey: string) {
  const item = checkItems.value.find((i: any) => i.itemKey === itemKey)
  return item?.checkType === 'manual'
}

// 检查是否有详情可查看
function hasDetailAction(row: any) {
  const itemsWithDetail = ['ar_overdue', 'ap_uncleared', 'ap_overdue', 'bank_unposted', 'balance_check']
  return itemsWithDetail.includes(row.itemKey) && (row.status === 'warning' || row.status === 'failed' || row.status === 'info')
}

// 检查是否有跳转操作
function hasNavigateAction(row: any) {
  // 应收逾期检查只需看详细明细，不提供“消込管理”跳转/按钮
  const itemsWithNav = ['depreciation', 'bank_unposted', 'payroll_posted', 'ap_uncleared', 'ap_overdue']
  return itemsWithNav.includes(row.itemKey) && row.status !== 'passed'
}

// 获取操作按钮标签
function getActionLabel(itemKey: string): string {
  const labels: Record<string, string> = {
    ap_uncleared: '消込管理',
    ap_overdue: '消込管理',
    depreciation: '償却実行',
    bank_unposted: '銀行明細',
    payroll_posted: '給与計算',
    balance_check: '仕訳確認'
  }
  return labels[itemKey] || '対応'
}

// 查看检查详情
async function viewCheckDetail(row: any) {
  detailLoading.value = true
  detailDialog.value = true
  detailData.value = []
  detailErrorMessage.value = ''
  detailTitle.value = getCheckItemName(row.itemKey) + ' 詳細'
  
  try {
    // 无 fallback：应收逾期明细必须以服务端返回为准
    // 为避免历史缓存，打开“詳細”时对 ar_overdue 强制刷新一次单项 check
    if (row?.itemKey && selectedMonth.value && row.itemKey === 'ar_overdue') {
      const rr = await api.post(`/monthly-closing/${selectedMonth.value}/check/${row.itemKey}`)
      if (rr?.data?.resultData) row.resultData = rr.data.resultData
    } else if (row?.itemKey && selectedMonth.value && (!row.resultData || !row.resultData.items)) {
      // 其他项保持：缺明细才刷新
      try {
        const rr = await api.post(`/monthly-closing/${selectedMonth.value}/check/${row.itemKey}`)
        if (rr?.data?.resultData) row.resultData = rr.data.resultData
      } catch {}
    }

    const getPeriodEnd = () => {
      const ym = String(selectedMonth.value || '').trim()
      const m = ym.match(/^(\d{4})-(\d{2})$/)
      if (!m) return new Date()
      const y = Number(m[1])
      const mm = Number(m[2])
      // JS: month is 0-based; day 0 means last day of previous month -> last day of target month
      return new Date(y, mm, 0, 23, 59, 59, 999)
    }
    const daysBetween = (a: Date, b: Date) => {
      const ms = a.getTime() - b.getTime()
      return Math.floor(ms / (24 * 60 * 60 * 1000))
    }
    const formatYen = (n: any) => {
      const num = Number(n)
      if (!Number.isFinite(num)) return '¥0'
      return `¥${num.toLocaleString('ja-JP', { maximumFractionDigits: 0 })}`
    }

    const fetchOpenItems = async (opts: { accountPrefix: string; endDate: Date; limit?: number }) => {
      const endYmd = opts.endDate.toISOString().slice(0, 10)
      const where: any[] = [
        { field: 'account_code', op: 'startsWith', value: opts.accountPrefix },
        { field: 'residual_amount', op: 'gt', value: 0 },
        { field: 'doc_date', op: 'lte', value: endYmd }
      ]
      const resp = await api.post('/objects/openitem/search', {
        page: 1,
        pageSize: opts.limit ?? 50,
        where,
        orderBy: [{ field: 'doc_date', dir: 'ASC' }]
      })
      return Array.isArray(resp.data?.data) ? resp.data.data : []
    }

    const enrichOpenItems = async (items: any[]) => {
      const voucherIds = Array.from(new Set(items.map((x: any) => String(x?.voucher_id || x?.voucherId || '').trim()).filter(Boolean)))
      const partnerCodes = Array.from(new Set(items.map((x: any) => String(x?.partner_id || x?.partnerId || '').trim()).filter(Boolean)))

      const voucherNoMap = new Map<string, string>()
      if (voucherIds.length) {
        try {
          const vRes = await api.post('/objects/voucher/search', {
            page: 1,
            pageSize: voucherIds.length,
            where: [{ field: 'id', op: 'in', value: voucherIds }]
          })
          for (const v of (vRes.data?.data || [])) {
            const header = v?.payload?.header || {}
            const no = String(header.voucherNo || v.voucher_no || '').trim()
            if (v?.id) voucherNoMap.set(String(v.id), no)
          }
        } catch {}
      }

      const partnerNameMap = new Map<string, string>()
      if (partnerCodes.length) {
        try {
          const pRes = await api.post('/objects/businesspartner/search', {
            page: 1,
            pageSize: Math.min(500, partnerCodes.length),
            where: [{ field: 'partner_code', op: 'in', value: partnerCodes }]
          })
          for (const p of (pRes.data?.data || [])) {
            const code = String(p?.partner_code || '').trim()
            const name = String(p?.payload?.name || p?.name || '').trim()
            if (code) partnerNameMap.set(code, name || code)
          }
        } catch {}
      }

      const periodEnd = getPeriodEnd()
      return items.map((it: any) => {
        const voucherId = String(it?.voucher_id || it?.voucherId || '').trim()
        const voucherNo = voucherNoMap.get(voucherId) || ''
        const partnerCode = String(it?.partner_id || it?.partnerId || '').trim()
        const partnerName = partnerNameMap.get(partnerCode) || partnerCode || '-'
        const docDateStr = String(it?.doc_date || it?.docDate || '').slice(0, 10)
        const docDate = docDateStr ? new Date(docDateStr + 'T00:00:00') : null
        const overdueDays = docDate ? Math.max(0, daysBetween(periodEnd, docDate)) : 0
        return {
          docNo: voucherNo || (voucherId ? voucherId.slice(0, 8) + '…' : '-'),
          partnerName,
          docDate: docDateStr || '-',
          residualAmount: formatYen(it?.residual_amount ?? it?.residualAmount ?? 0),
          daysPastDue: overdueDays ? String(overdueDays) : '-'
        }
      })
    }

    // 根据检查项类型设置列和获取数据
    switch (row.itemKey) {
      case 'ar_overdue':
        detailColumns.value = [
          { prop: 'docNo', label: '伝票番号', width: 120 },
          { prop: 'partnerName', label: '取引先', width: 150 },
          { prop: 'docDate', label: '伝票日付', width: 100 },
          { prop: 'dueDate', label: '支払期限', width: 100 },
          { prop: 'termDays', label: '条件日数', width: 90 },
          { prop: 'residualAmount', label: '残額', width: 120 },
          { prop: 'overdueDays', label: '逾期日数', width: 80 },
          { prop: 'error', label: 'エラー', width: 260 }
        ]
        detailNavigatePath.value = ''
        detailNavigateLabel.value = ''
        if (!row.resultData || !Array.isArray(row.resultData.items)) {
          detailErrorMessage.value = 'エラー：サーバーが逾期明細（resultData.items）を返していません。先に「全チェック実行」を行ってください。'
          detailData.value = []
          break
        }
        // 仅展示后端返回（不做 fallback 推算）
        const okRows = row.resultData.items.map((it: any) => {
          const voucherNo = String(it?.voucherNo || '').trim()
          const voucherId = String(it?.voucherId || '').trim()
          const partnerName = String(it?.partnerName || '').trim()
          const partnerId = String(it?.partnerId || '').trim()
          const dueDate = String(it?.dueDate || '').slice(0, 10)
          const docDate = String(it?.docDate || '').slice(0, 10)
          const termDays = it?.termDays
          const overdueDays = it?.overdueDays
          return {
            voucherId,
            voucherNo,
            docNo: voucherNo || `【伝票番号未設定】${voucherId ? voucherId.slice(0, 8) + '…' : ''}`,
            partnerName: partnerName || `【取引先未設定】${partnerId ? partnerId.slice(0, 8) + '…' : ''}`,
            docDate: docDate || '-',
            dueDate: dueDate || '【支払期限未設定】',
            termDays: (termDays === '' || termDays === null || typeof termDays === 'undefined') ? '-' : String(termDays),
            residualAmount: typeof it?.residualAmount === 'string' ? it.residualAmount : formatYen(it?.residualAmount ?? 0),
            overdueDays: (overdueDays === '' || overdueDays === null || typeof overdueDays === 'undefined') ? '-' : String(overdueDays),
            error: String(it?.error || '').trim()
          }
        })
        const missing = Array.isArray(row.resultData?.missingDueDateItems) ? row.resultData.missingDueDateItems : []
        const missingRows = missing.map((it: any) => {
          const voucherNo = String(it?.voucherNo || '').trim()
          const voucherId = String(it?.voucherId || '').trim()
          const partnerName = String(it?.partnerName || '').trim()
          const partnerId = String(it?.partnerId || '').trim()
          const docDate = String(it?.docDate || '').slice(0, 10)
          return {
            voucherId,
            voucherNo,
            docNo: voucherNo || `【伝票番号未設定】${voucherId ? voucherId.slice(0, 8) + '…' : ''}`,
            partnerName: partnerName || `【取引先未設定】${partnerId ? partnerId.slice(0, 8) + '…' : ''}`,
            docDate: docDate || '-',
            dueDate: '【支払期限未設定】',
            termDays: '-',
            residualAmount: typeof it?.residualAmount === 'string' ? it.residualAmount : formatYen(it?.residualAmount ?? 0),
            overdueDays: '-',
            error: String(it?.error || '支払期限（dueDate）が未設定のため逾期判定できません').trim() || '支払期限（dueDate）が未設定のため逾期判定できません'
          }
        })
        detailData.value = [...okRows, ...missingRows]
        break
        
      case 'ap_uncleared':
      case 'ap_overdue':
        detailColumns.value = [
          { prop: 'docNo', label: '伝票番号', width: 120 },
          { prop: 'partnerName', label: '取引先', width: 150 },
          { prop: 'docDate', label: '伝票日付', width: 100 },
          { prop: 'residualAmount', label: '残額', width: 120 },
          { prop: 'daysPastDue', label: '経過日数', width: 80 }
        ]
        detailNavigatePath.value = '/financial/open-items?type=payable'
        detailNavigateLabel.value = '消込管理へ'
        {
          const periodEnd = getPeriodEnd()
          const endDate = row.itemKey === 'ap_overdue'
            ? new Date(periodEnd.getFullYear(), periodEnd.getMonth(), periodEnd.getDate() - 30, 23, 59, 59, 999)
            : periodEnd
          const items = await fetchOpenItems({ accountPrefix: '21', endDate, limit: 50 })
          detailData.value = await enrichOpenItems(items)
        }
        break
        
      case 'bank_unposted':
        detailColumns.value = [
          { prop: 'transactionDate', label: '取引日', width: 100 },
          { prop: 'description', label: '摘要', width: 200 },
          { prop: 'amount', label: '金額', width: 120 },
          { prop: 'status', label: 'ステータス', width: 100 }
        ]
        detailNavigatePath.value = '/moneytree-transactions'
        detailNavigateLabel.value = '銀行明細へ'
        // 获取未记账银行明细
        try {
          const resp = await api.get('/moneytree-transactions', { params: { status: 'pending', limit: 50 } })
          detailData.value = (resp.data?.items || []).map((item: any) => ({
            transactionDate: item.transactionDate,
            description: item.description || '-',
            amount: `¥${((item.depositAmount || 0) - (item.withdrawalAmount || 0)).toLocaleString()}`,
            status: item.postingStatus || 'pending'
          }))
        } catch {
          detailData.value = []
        }
        break
        
      case 'balance_check':
        detailColumns.value = [
          { prop: 'label', label: '項目', width: 150 },
          { prop: 'value', label: '金額', width: 150 }
        ]
        detailNavigatePath.value = '/vouchers'
        detailNavigateLabel.value = '仕訳一覧へ'
        // 从resultData获取借贷差额信息
        const rd = row.resultData || {}
        detailData.value = [
          { label: '借方合計', value: `¥${(rd.totalDebit || 0).toLocaleString()}` },
          { label: '貸方合計', value: `¥${(rd.totalCredit || 0).toLocaleString()}` },
          { label: '差額', value: `¥${Math.abs(rd.difference || 0).toLocaleString()}` }
        ]
        break
        
      default:
        detailColumns.value = []
        detailData.value = []
        detailNavigatePath.value = ''
        detailNavigateLabel.value = ''
    }
  } catch (err) {
    console.error('Failed to load detail:', err)
  } finally {
    detailLoading.value = false
  }
}

// 跳转到操作页面
function navigateToAction(row: any) {
  // NOTE: 用户期望不跳转页面，改为弹窗处理（避免跳到未配置路由导致空白）
  if (row.itemKey === 'ar_uncleared' || row.itemKey === 'ar_overdue') {
    clearingDialogMode.value = 'receivable'
    clearingDialogVisible.value = true
    return
  }
  if (row.itemKey === 'ap_uncleared' || row.itemKey === 'ap_overdue') {
    clearingDialogMode.value = 'payable'
    clearingDialogVisible.value = true
    return
  }
  const routes: Record<string, string> = {
    depreciation: '/fixed-assets?action=depreciation',
    bank_unposted: '/moneytree-transactions',
    payroll_posted: '/payroll',
    balance_check: '/vouchers'
  }
  const path = routes[row.itemKey]
  if (path) window.location.href = path
}

// 从详情对话框跳转
function navigateFromDetail() {
  // 详情弹窗里的“消込管理へ”同样不做页面跳转，改为打开弹窗
  if (detailNavigatePath.value.includes('/financial/open-items?type=receivable')) {
    detailDialog.value = false
    clearingDialogMode.value = 'receivable'
    clearingDialogVisible.value = true
    return
  }
  if (detailNavigatePath.value.includes('/financial/open-items?type=payable')) {
  detailDialog.value = false
    clearingDialogMode.value = 'payable'
    clearingDialogVisible.value = true
    return
  }
  if (detailNavigatePath.value) window.location.href = detailNavigatePath.value
  detailDialog.value = false
}

function openVoucherDetail(row: any) {
  const voucherId = String(row?.voucherId || '').trim()
  const voucherNo = String(row?.voucherNo || '').trim()
  if (!voucherId && !voucherNo) return
  voucherDialogVisible.value = true
  nextTick(() => {
    const isUuid = voucherId && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(voucherId)
    voucherDetailRef.value?.applyIntent?.({
      ...(isUuid ? { voucherId } : { voucherNo: voucherNo || voucherId }),
      detailOnly: true
    })
  })
}

function formatNumber(val: number | undefined) {
  if (val === undefined || val === null) return '0'
  return val.toLocaleString()
}

function formatDateTime(dt: string | Date) {
  if (!dt) return ''
  const date = typeof dt === 'string' ? new Date(dt) : dt
  return date.toLocaleString(lang.value === 'ja' ? 'ja-JP' : lang.value === 'zh' ? 'zh-CN' : 'en-US')
}

// 监听年份变化
watch(currentYear, (year) => {
  const str = String(year)
  if (yearValue.value !== str) yearValue.value = str
  selectedMonth.value = null
  closingData.value = null
  checkResults.value = []
  loadData()
})

watch(yearValue, (val) => {
  if (!val) return
  const num = Number(val)
  if (!Number.isFinite(num)) return
  if (num !== currentYear.value) currentYear.value = num
})

onMounted(() => {
  loadData()
})
</script>

<style scoped>
.closing-card {
  min-height: 600px;
}
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

/* 标题区域样式 */
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
.page-header-actions {
  display: flex;
  gap: 12px;
  align-items: center;
}
.year-picker {
  width: 120px;
}
.months-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
  gap: 12px;
  margin-bottom: 24px;
}
.month-card {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 16px;
  border-radius: 12px;
  background: #f8fafc;
  border: 2px solid #e2e8f0;
  cursor: pointer;
  transition: all 0.2s;
}
.month-card:hover {
  border-color: #409eff;
  background: #ecf5ff;
}
.month-card.is-selected {
  border-color: #409eff;
  background: #ecf5ff;
  box-shadow: 0 0 0 3px rgba(64, 158, 255, 0.2);
}
.month-card.status-closed {
  background: #f0f9eb;
  border-color: #67c23a;
}
.month-card.status-checking,
.month-card.status-adjusting {
  background: #fdf6ec;
  border-color: #e6a23c;
}
.month-label {
  font-weight: 600;
  color: #1f2937;
  font-size: 16px;
}
.closing-detail {
  margin-top: 16px;
}
.status-bar {
  margin-bottom: 24px;
}
.section-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.check-section {
  margin-bottom: 16px;
}
.tax-section {
  margin-bottom: 16px;
}
.tax-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 24px;
}
.tax-item {
  text-align: center;
}
.tax-label {
  font-size: 14px;
  color: #6b7280;
  margin-bottom: 8px;
}
.tax-value {
  font-size: 20px;
  font-weight: 600;
  color: #1f2937;
}
.tax-item.highlight .tax-value {
  color: #409eff;
}
.closing-actions {
  display: flex;
  gap: 12px;
  justify-content: center;
  margin-top: 24px;
}
.closed-info {
  margin-top: 24px;
  text-align: center;
}
.check-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

/* 嵌入弹窗时，给子页面一点呼吸空间 */
:deep(.clearing-embed) {
  padding: 0;
}

/* 伝票詳細弹窗样式（与其他页面一致） */
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

