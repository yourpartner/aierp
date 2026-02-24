<template>
  <div class="payroll-history">
    <el-card class="payroll-card">
      <template #header>
        <div class="payroll-header">
          <div class="payroll-header__left">
            <el-icon class="payroll-header__icon"><Timer /></el-icon>
            <span class="payroll-header__title">給与計算履歴</span>
            <el-tag size="small" type="info">{{ pagination.total }}件</el-tag>
          </div>
        </div>
      </template>
      
      <!-- 検索条件 -->
      <div class="payroll-filters">
        <el-date-picker
          v-model="filters.month"
          type="month"
          value-format="YYYY-MM"
          clearable
          placeholder="月份"
          class="payroll-filters__month"
        />
        
        <el-select
          v-model="filters.employeeId"
          filterable
          clearable
          placeholder="社員を検索"
          class="payroll-filters__employee"
        >
          <el-option
            v-for="item in employeeOptions"
            :key="item.value"
            :label="item.label"
            :value="item.value"
          />
        </el-select>
        
        <el-select v-model="filters.runType" placeholder="モード" class="payroll-filters__type">
          <el-option label="すべて" value="all" />
          <el-option label="手動" value="manual" />
          <el-option label="自動" value="auto" />
        </el-select>
        
        <el-input v-model="filters.keyword" placeholder="キーワード" clearable class="payroll-filters__keyword">
          <template #prefix>
            <el-icon><Search /></el-icon>
          </template>
        </el-input>
        
        <el-button type="primary" :loading="loading" @click="handleSearch">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
        <el-button @click="handleReset">リセット</el-button>
        <el-button type="success" :loading="exporting" @click="handleExport">
          <el-icon><Download /></el-icon>
          Excel
        </el-button>
      </div>
      
      <!-- データテーブル -->
      <el-table
        v-loading="loading"
        :data="entries"
        border
        stripe
        class="payroll-table"
      >
        <el-table-column prop="periodMonth" label="月份" width="100" />
        <el-table-column label="モード" width="80" align="center">
          <template #default="{ row }">
            <el-tag size="small" :type="row.runType === 'auto' ? 'success' : 'info'">
              {{ formatRunType(row.runType) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="社員" min-width="160">
          <template #default="{ row }">
            <div class="emp-name-cell">
              <div class="emp-name-main">{{ row.employeeName || '未設定' }}</div>
              <div class="emp-name-sub">{{ row.employeeCode }}</div>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="部門" min-width="140">
          <template #default="{ row }">
            {{ formatDepartment(row) }}
          </template>
        </el-table-column>
        <el-table-column label="金額" width="120" align="right">
          <template #default="{ row }">
            <span class="amount-cell">¥{{ formatAmount(row.totalAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="差異" min-width="140">
          <template #default="{ row }">
            <span v-if="formatDiff(row.diffSummary)">{{ formatDiff(row.diffSummary) }}</span>
            <span v-else class="emp-empty">—</span>
          </template>
        </el-table-column>
        <el-table-column label="伝票番号" width="120">
          <template #default="{ row }">
            <el-link v-if="row.voucherNo" type="primary" @click="openVoucherDialog(row)">{{ row.voucherNo }}</el-link>
            <span v-else class="emp-empty">未作成</span>
          </template>
        </el-table-column>
        <el-table-column label="保存日時" width="150">
          <template #default="{ row }">
            {{ formatDateTime(row.createdAt) }}
          </template>
        </el-table-column>
        <el-table-column label="" width="80" align="center" fixed="right">
          <template #default="{ row }">
            <el-button size="small" type="primary" text @click="openEntryDetail(row)">
              <el-icon><View /></el-icon>
            </el-button>
          </template>
        </el-table-column>
      </el-table>
      
      <!-- ページネーション -->
      <div class="payroll-pagination">
        <span class="payroll-pagination__info">
          {{ (pagination.page - 1) * pagination.pageSize + 1 }} - {{ Math.min(pagination.page * pagination.pageSize, pagination.total) }} / {{ pagination.total }}件
        </span>
        <el-pagination
          background
          layout="prev, pager, next"
          :current-page="pagination.page"
          :page-size="pagination.pageSize"
          :total="pagination.total"
          @current-change="handlePageChange"
        />
      </div>
    </el-card>

    <!-- 凭证详情弹窗 -->
    <el-dialog v-model="voucherDialogVisible" width="auto" append-to-body destroy-on-close class="voucher-detail-dialog">
      <template #header></template>
      <div>
        <VouchersList
          v-if="voucherDialogVisible"
          :allow-edit="false"
          :initial-voucher-id="voucherDialogVoucherId || undefined"
          :initial-voucher-no="voucherDialogVoucherNo || undefined"
        />
      </div>
    </el-dialog>

    <!-- 詳細ドロワー -->
    <el-drawer v-model="detailVisible" size="65%" :title="detailTitle">
      <div v-if="detailVisible" v-loading="detailLoading" class="detail-content">
        <div v-if="detailData">
          <!-- ヘッダー -->
          <div class="detail-header">
            <div>
              <div class="detail-name">{{ detailData.employeeName }} <span class="detail-code">({{ detailData.employeeCode }})</span></div>
              <div class="detail-meta">
                {{ detailData.periodMonth }} ／ {{ formatRunType(detailData.runType) }} ／ {{ formatDepartment(detailData) }}
                <template v-if="detailData?.voucherNo"> ／ 伝票: {{ detailData.voucherNo }}</template>
              </div>
            </div>
            <div style="display:flex;align-items:center;gap:12px">
              <div class="detail-total">¥{{ formatAmount(editing ? editNetAmount : detailData.totalAmount) }}</div>
              <el-button v-if="detailData.canEdit && !editing" type="primary" size="small" @click="startEditing">
                <el-icon><Edit /></el-icon> 編集
              </el-button>
              <el-tag v-if="detailData.canEdit === false" type="info" size="small">支払済み</el-tag>
            </div>
          </div>

          <!-- 編集モード操作バー -->
          <div v-if="editing" style="display:flex;gap:8px;margin-bottom:12px">
            <el-button type="primary" :loading="editSaving" @click="saveEdit">保存</el-button>
            <el-button @click="cancelEditing">キャンセル</el-button>
            <el-tag v-if="editing" type="warning" size="small" style="margin-left:auto">編集中</el-tag>
          </div>
          
          <el-alert
            v-if="detailData?.diffSummary"
            type="info"
            show-icon
            :title="formatDiff(detailData.diffSummary)"
            style="margin-bottom:16px"
          />
          
          <!-- 勤怠サマリー -->
          <div v-if="detailData?.workHours" class="section-card" style="margin-bottom:16px">
            <div class="section-card__header">勤怠サマリー</div>
            <div class="section-card__body">
              <el-descriptions :column="4" size="small" border>
                <el-descriptions-item
                  v-for="item in workHourItems"
                  :key="item.key"
                  :label="item.label"
                >
                  {{ formatWorkHour(detailData?.workHours?.[item.key]) }}
                </el-descriptions-item>
              </el-descriptions>
            </div>
          </div>
          
          <!-- 給与項目・仕訳 -->
          <el-row :gutter="16">
            <el-col :span="12">
              <div class="section-card">
                <div class="section-card__header">
                  <span>給与項目</span>
                  <el-button v-if="editing" type="primary" size="small" text style="margin-left:auto" @click="openAddItemDialog">
                    <el-icon><Plus /></el-icon> 項目追加
                  </el-button>
                </div>
                <div class="section-card__body">
                  <el-table :data="editing ? editPayrollSheet : (detailData?.payrollSheet || [])" size="small" border>
                    <el-table-column label="項目" min-width="100">
                      <template #default="{ row }">
                        <span :class="{ 'manual-item': row.isManuallyAdded }">
                          {{ row.displayName || row.itemName || row.itemCode }}
                        </span>
                      </template>
                    </el-table-column>
                    <el-table-column label="金額" width="110" align="right">
                      <template #default="{ row }">
                        <template v-if="editing">
                          <el-input-number
                            v-model="row.finalAmount"
                            size="small"
                            :controls="false"
                            style="width:90px"
                            @change="onEditAmountChange"
                          />
                        </template>
                        <template v-else>
                          {{ row.displayAmount || formatAmount(row.amount || row.finalAmount) }}
                        </template>
                      </template>
                    </el-table-column>
                    <el-table-column label="備考" min-width="100">
                      <template #default="{ row }">
                        <el-input v-if="editing && (row.isManuallyAdded || row.adjustment !== 0 || row.finalAmount !== row.calculatedAmount)" v-model="row.adjustmentReason" size="small" placeholder="理由" />
                        <span v-else>{{ row.adjustmentReason || row.note || row.memo || '' }}</span>
                      </template>
                    </el-table-column>
                    <el-table-column v-if="editing" width="40" align="center">
                      <template #default="{ row, $index }">
                        <el-button v-if="row.isManuallyAdded" type="danger" size="small" text circle @click="removeEditItem($index)">
                          <el-icon><Delete /></el-icon>
                        </el-button>
                      </template>
                    </el-table-column>
                  </el-table>
                </div>
              </div>
            </el-col>
            <el-col :span="12">
              <div class="section-card">
                <div class="section-card__header">
                  <span>会計仕訳</span>
                  <span v-if="editing && editJournalLoading" style="margin-left:auto;font-size:12px;color:#909399">
                    <el-icon class="is-loading"><Refresh /></el-icon> 更新中...
                  </span>
                </div>
                <div class="section-card__body">
                  <el-table :data="editing ? editAccountingDraft : (detailData?.accountingDraft || [])" size="small" border>
                    <el-table-column label="科目コード" width="140">
                      <template #default="{ row }">
                        <el-select
                          v-if="editing && row.needsAccount"
                          v-model="row.accountCode"
                          placeholder="科目を選択"
                          filterable
                          size="small"
                          style="width:100%"
                          @change="(val: string) => onEditAccountChange(row, val)"
                        >
                          <el-option v-for="acc in accountOptions" :key="acc.code" :label="`${acc.code} ${acc.name}`" :value="acc.code" />
                        </el-select>
                        <span v-else>{{ row.accountCode }}</span>
                      </template>
                    </el-table-column>
                    <el-table-column label="科目名" min-width="100">
                      <template #default="{ row }">
                        <span v-if="row.needsAccount">{{ row.accountCode ? (accountNameMap[row.accountCode] || '') : row.accountName }}</span>
                        <span v-else>{{ row.accountName }}</span>
                      </template>
                    </el-table-column>
                    <el-table-column prop="drcr" label="借/貸" width="60" align="center" />
                    <el-table-column label="金額" width="90" align="right">
                      <template #default="{ row }">
                        {{ formatAmount(row.amount) }}
                      </template>
                    </el-table-column>
                  </el-table>
                </div>
              </div>
            </el-col>
          </el-row>
          
          <!-- 計算トレース -->
          <div v-if="detailData?.trace && detailData.trace.length > 0" class="section-card" style="margin-top:16px">
            <div class="section-card__header">
              <span>計算トレース</span>
              <el-switch v-model="showTraceRaw" active-text="JSON" inactive-text="テーブル" size="small" style="margin-left:auto" />
            </div>
            <div class="section-card__body">
              <div v-if="!showTraceRaw">
                <el-table :data="detailData.trace" size="small" border max-height="300">
                  <el-table-column prop="step" label="ステップ" width="140" />
                  <el-table-column prop="source" label="ソース" width="80" />
                  <el-table-column prop="item" label="項目" width="100" />
                  <el-table-column label="金額" width="90" align="right">
                    <template #default="{ row }">
                      {{ row.amount !== undefined ? formatAmount(row.amount) : '' }}
                    </template>
                  </el-table-column>
                  <el-table-column prop="lawVersion" label="法令Ver" width="150" />
                  <el-table-column prop="lawNote" label="法令備考" min-width="120" />
                  <el-table-column prop="note" label="備考" min-width="100" />
                </el-table>
              </div>
              <div v-else>
                <pre class="trace-json">{{ JSON.stringify(detailData.trace, null, 2) }}</pre>
              </div>
            </div>
          </div>
        </div>
      </div>
    </el-drawer>

    <!-- 項目追加ダイアログ（編集モード用） -->
    <el-dialog v-model="addItemDialogVisible" title="給与項目を追加" width="420px" :close-on-click-modal="false" append-to-body>
      <el-form label-width="80px">
        <el-form-item label="項目">
          <el-select v-model="addItemForm.itemCode" placeholder="選択" filterable allow-create clearable style="width:100%" @change="onAddItemCodeChange">
            <el-option-group label="収入項目">
              <el-option v-for="item in standardPayrollItems.filter(i => i.kind === 'earning')" :key="item.code" :label="item.name" :value="item.code" />
            </el-option-group>
            <el-option-group label="控除項目">
              <el-option v-for="item in standardPayrollItems.filter(i => i.kind === 'deduction')" :key="item.code" :label="item.name" :value="item.code" />
            </el-option-group>
          </el-select>
        </el-form-item>
        <el-form-item label="項目名">
          <el-input v-model="addItemForm.itemName" placeholder="カスタム名（省略可）" />
        </el-form-item>
        <el-form-item label="区分">
          <el-radio-group v-model="addItemForm.kind">
            <el-radio value="earning">収入</el-radio>
            <el-radio value="deduction">控除</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="金額">
          <el-input-number v-model="addItemForm.amount" :min="0" :controls="false" style="width:160px" /> 円
        </el-form-item>
        <el-form-item label="理由">
          <el-input v-model="addItemForm.reason" placeholder="追加理由（必須）" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="addItemDialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="confirmAddItem">追加</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, computed } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Timer, Search, View, Edit, Plus, Delete, Refresh, Download } from '@element-plus/icons-vue'
import api from '../api'
import VouchersList from './VouchersList.vue'

const defaultMonth = new Date().toISOString().slice(0, 7)

const filters = reactive({
  month: defaultMonth,
  employeeId: '',
  runType: 'all',
  keyword: ''
})

const pagination = reactive({
  page: 1,
  pageSize: 20,
  total: 0
})
const workHourItems = [
  { key: 'totalHours', label: '総勤務時間' },
  { key: 'regularHours', label: '所定内' },
  { key: 'overtimeHours', label: '時間外' },
  { key: 'holidayHours', label: '休日労働' },
  { key: 'lateNightHours', label: '深夜労働' },
  { key: 'absenceHours', label: '不足時間' }
]

const entries = ref<any[]>([])
const loading = ref(false)
const employeeOptions = ref<any[]>([])
const detailVisible = ref(false)
const detailLoading = ref(false)
const detailData = ref<any | null>(null)
const showTraceRaw = ref(false)

// 凭证详情弹窗
const voucherDialogVisible = ref(false)
const voucherDialogVoucherId = ref<string>('')
const voucherDialogVoucherNo = ref<string>('')

function openVoucherDialog(row: any) {
  const voucherNo = row.voucherNo
  const voucherId = row.voucherId
  if (!voucherNo && !voucherId) return
  voucherDialogVoucherId.value = voucherId || ''
  voucherDialogVoucherNo.value = voucherNo || ''
  voucherDialogVisible.value = true
}

const detailTitle = computed(() => {
  if (!detailData.value) return '給与明細'
  return `${detailData.value.employeeName || detailData.value.employeeCode || ''} の給与明細`
})

function formatAmount(val: number) {
  return Number(val || 0).toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 })
}

function formatRunType(type?: string) {
  if (!type) return '不明'
  if (type.toLowerCase() === 'auto') return '自動'
  if (type.toLowerCase() === 'manual') return '手動'
  return type
}

function formatDateTime(value: string) {
  if (!value) return ''
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return value
  const y = d.getFullYear()
  const m = String(d.getMonth() + 1).padStart(2, '0')
  const day = String(d.getDate()).padStart(2, '0')
  const hh = String(d.getHours()).padStart(2, '0')
  const mm = String(d.getMinutes()).padStart(2, '0')
  return `${y}-${m}-${day} ${hh}:${mm}`
}

function formatDepartment(row: any) {
  if (!row) return '未設定'
  const name = row.departmentName
  const code = row.departmentCode
  if (name && code) return `${name} (${code})`
  return name || code || '未設定'
}

function formatWorkHour(value?: number | null) {
  if (typeof value !== 'number' || Number.isNaN(value)) return '0h'
  return `${Number(value).toFixed(2)}h`
}

function formatDiff(diff: any) {
  if (!diff) return ''
  const difference = diff.difference ?? diff.Difference ?? 0
  const percent = diff.differencePercent ?? diff.DifferencePercent ?? null
  const text = difference ? `${difference >= 0 ? '+' : ''}${formatAmount(difference)}` : ''
  const pctText = typeof percent === 'number' ? ` (${(percent * 100).toFixed(1)}%)` : ''
  return text ? `${text}${pctText}` : ''
}

async function handleSearch() {
  pagination.page = 1
  await loadEntries()
}

function handleReset() {
  filters.month = defaultMonth
  filters.employeeId = ''
  filters.runType = 'all'
  filters.keyword = ''
  handleSearch()
}

function handlePageChange(page: number) {
  pagination.page = page
  loadEntries()
}

async function loadEntries() {
  loading.value = true
  try {
    const params: Record<string, any> = {
      page: pagination.page,
      pageSize: pagination.pageSize
    }
    if (filters.month) params.month = filters.month
    if (filters.employeeId) params.employeeId = filters.employeeId
    if (filters.runType && filters.runType !== 'all') params.runType = filters.runType
    if (filters.keyword?.trim()) params.keyword = filters.keyword.trim()
    const resp = await api.get('/payroll/run-entries', { params })
    const data = resp.data || {}
    pagination.total = data.total || 0
    entries.value = Array.isArray(data.items)
      ? data.items.map((item: any) => ({
          ...item,
          departmentName: item.departmentName || '',
          createdAt: item.createdAt,
          voucherNo: item.voucherNo || '',
          voucherId: item.voucherId || ''
        }))
      : []
  } catch (err: any) {
    console.error(err)
    ElMessage.error('給与履歴の取得に失敗しました')
  } finally {
    loading.value = false
  }
}


async function loadEmployees() {
  try {
    const resp = await api.post('/objects/employee/search', {
      page: 1, pageSize: 0, orderBy: [{ field: 'employee_code', dir: 'ASC' }]
    })
    employeeOptions.value = ((resp.data?.data || []) as any[]).map(emp => ({
      value: emp.id,
      label: `${emp.payload?.nameKanji || emp.payload?.name || emp.name || ''} (${emp.employee_code || emp.payload?.code || ''})`
    }))
  } catch { employeeOptions.value = [] }
}

async function openEntryDetail(row: any) {
  detailVisible.value = true
  detailLoading.value = true
  showTraceRaw.value = false
  editing.value = false
  try {
    const resp = await api.get(`/payroll/runs/${row.runId}/entries/${row.entryId}`)
    const data = resp.data || {}
    detailData.value = {
      runId: data.runId || row.runId,
      entryId: data.entryId || row.entryId,
      runType: data.runType || row.runType,
      periodMonth: data.periodMonth || row.periodMonth,
      employeeId: data.employeeId || row.employeeId,
      employeeName: data.employeeName || row.employeeName,
      employeeCode: data.employeeCode || row.employeeCode,
      departmentCode: data.departmentCode || row.departmentCode,
      departmentName: data.departmentName || row.departmentName,
      totalAmount: data.totalAmount || row.totalAmount,
      diffSummary: data.diffSummary || row.diffSummary,
      canEdit: data.canEdit ?? false,
      payrollSheet: (data.payrollSheet || []).map((entry: any) => ({
        ...entry,
        displayName: entry.itemName || entry.itemCode,
        displayAmount: formatAmount(entry.amount)
      })),
      accountingDraft: (data.accountingDraft || []).map((entry: any) => ({
        ...entry,
        displayAmount: formatAmount(entry.amount)
      })),
      trace: data.trace || null,
      workHours: data.metadata?.workHours || data.metadata?.workhours || null,
      voucherNo: data.voucherNo || row.voucherNo || '',
      voucherId: data.voucherId || row.voucherId || '',
      metadata: data.metadata || null
    }
  } catch (err) {
    console.error(err)
    ElMessage.error('明細の取得に失敗しました')
    detailVisible.value = false
  } finally {
    detailLoading.value = false
  }
}

// ========== 編集モード ==========
const editing = ref(false)
const editSaving = ref(false)
const editPayrollSheet = ref<any[]>([])
const editAccountingDraft = ref<any[]>([])
const editJournalLoading = ref(false)
const accountOptions = ref<any[]>([])
const accountNameMap = ref<Record<string, string>>({})

const standardPayrollItems = [
  { code: 'BONUS', name: '賞与', kind: 'earning' },
  { code: 'ALLOWANCE_SPECIAL', name: '特別手当', kind: 'earning' },
  { code: 'ALLOWANCE_HOUSING', name: '住宅手当', kind: 'earning' },
  { code: 'ALLOWANCE_FAMILY', name: '家族手当', kind: 'earning' },
  { code: 'ADJUST_OTHER', name: 'その他調整', kind: 'earning' },
  { code: 'DEDUCT_LOAN', name: '貸付金返済', kind: 'deduction' },
  { code: 'DEDUCT_ADVANCE', name: '前払金精算', kind: 'deduction' },
  { code: 'DEDUCT_OTHER', name: 'その他控除', kind: 'deduction' },
]

function isDeductionItem(row: any): boolean {
  if (row?.kind) return row.kind === 'deduction'
  const code = (row?.itemCode || '').toString().toUpperCase()
  return code === 'HEALTH_INS' || code === 'CARE_INS' || code === 'PENSION'
    || code === 'EMP_INS' || code === 'WHT' || code === 'RESIDENT_TAX'
    || code === 'ABSENCE_DEDUCT' || code === 'DEDUCT_LOAN'
    || code === 'DEDUCT_ADVANCE' || code === 'DEDUCT_OTHER'
    || code === 'NENMATSU_CHOSHU'
}

const editNetAmount = computed(() => {
  return editPayrollSheet.value.reduce((sum: number, row: any) => {
    const amt = Math.abs(row.finalAmount || row.amount || 0)
    return isDeductionItem(row) ? sum - amt : sum + amt
  }, 0)
})

async function loadAccountOptions() {
  if (accountOptions.value.length > 0) return
  try {
    const r = await api.post('/objects/account/search', { page: 1, pageSize: 500, orderBy: [{ field: 'account_code', dir: 'ASC' }] })
    const items = (r.data?.data || []) as any[]
    accountOptions.value = items.map(a => ({ code: a.account_code, name: a.name || a.payload?.name || '' }))
    accountNameMap.value = Object.fromEntries(accountOptions.value.map(a => [a.code, a.name]))
  } catch {}
}

function startEditing() {
  if (!detailData.value) return
  loadAccountOptions()
  editPayrollSheet.value = JSON.parse(JSON.stringify(detailData.value.payrollSheet)).map((row: any) => ({
    ...row,
    calculatedAmount: row.calculatedAmount ?? row.amount ?? 0,
    finalAmount: row.finalAmount ?? row.amount ?? 0,
    adjustment: row.adjustment ?? 0,
    adjustmentReason: row.adjustmentReason || '',
    isManuallyAdded: row.isManuallyAdded || false,
    kind: row.kind || (isDeductionItem(row) ? 'deduction' : 'earning')
  }))
  editAccountingDraft.value = JSON.parse(JSON.stringify(detailData.value.accountingDraft))
  editing.value = true
}

function cancelEditing() {
  editing.value = false
  editPayrollSheet.value = []
  editAccountingDraft.value = []
}

let regenTimer: ReturnType<typeof setTimeout> | null = null
function onEditAmountChange() {
  if (regenTimer) clearTimeout(regenTimer)
  regenTimer = setTimeout(() => regenerateEditJournal(), 600)
}

async function regenerateEditJournal() {
  if (!detailData.value) return
  editJournalLoading.value = true
  try {
    const payload = {
      payrollSheet: editPayrollSheet.value.map((row: any) => ({
        itemCode: row.itemCode,
        itemName: row.itemName || row.displayName,
        amount: isDeductionItem(row) ? Math.abs(row.finalAmount || 0) : (row.finalAmount || 0),
        finalAmount: row.finalAmount,
        kind: row.kind,
        ...(row.overrideAccountCode ? { overrideAccountCode: row.overrideAccountCode } : {})
      })),
      employeeId: detailData.value.employeeId,
      employeeCode: detailData.value.employeeCode,
      departmentCode: detailData.value.departmentCode,
      departmentName: detailData.value.departmentName
    }
    const r = await api.post('/payroll/regenerate-journal', payload)
    editAccountingDraft.value = r.data?.accountingDraft || []
  } catch (e: any) {
    console.error(e)
  } finally {
    editJournalLoading.value = false
  }
}

function onEditAccountChange(row: any, val: string) {
  row.accountName = accountNameMap.value[val] || ''
  const itemCode = row.itemCode
  if (itemCode) {
    const sheetRow = editPayrollSheet.value.find((r: any) => r.itemCode === itemCode)
    if (sheetRow) sheetRow.overrideAccountCode = val
  }
}

function removeEditItem(index: number) {
  editPayrollSheet.value.splice(index, 1)
  onEditAmountChange()
}

// 項目追加ダイアログ
const addItemDialogVisible = ref(false)
const addItemForm = reactive({ itemCode: '', itemName: '', kind: 'earning' as string, amount: 0, reason: '' })

function openAddItemDialog() {
  Object.assign(addItemForm, { itemCode: '', itemName: '', kind: 'earning', amount: 0, reason: '' })
  addItemDialogVisible.value = true
}

function onAddItemCodeChange(code: string) {
  const item = standardPayrollItems.find(i => i.code === code)
  if (item) addItemForm.kind = item.kind
}

function confirmAddItem() {
  if (!addItemForm.reason?.trim()) { ElMessage.warning('理由を入力してください'); return }
  const itemDef = standardPayrollItems.find(i => i.code === addItemForm.itemCode)
  const itemName = addItemForm.itemName || itemDef?.name || addItemForm.itemCode
  if (!itemName) { ElMessage.warning('項目名を入力してください'); return }
  const finalAmount = Math.abs(addItemForm.amount)
  editPayrollSheet.value.push({
    itemCode: addItemForm.itemCode || `MANUAL_${Date.now()}`,
    itemName, displayName: itemName,
    calculatedAmount: 0, adjustment: finalAmount, finalAmount,
    adjustmentReason: addItemForm.reason, isManuallyAdded: true, kind: addItemForm.kind
  })
  addItemDialogVisible.value = false
  onEditAmountChange()
}

async function saveEdit() {
  if (!detailData.value) return
  for (const row of editPayrollSheet.value) {
    if ((row.isManuallyAdded || row.finalAmount !== row.calculatedAmount) && !row.adjustmentReason?.trim()) {
      ElMessage.warning('調整理由が入力されていない項目があります')
      return
    }
  }
  // Check needsAccount rows have account selected
  for (const row of editAccountingDraft.value) {
    if (row.needsAccount && !row.accountCode) {
      ElMessage.warning('科目が未選択の仕訳行があります')
      return
    }
  }

  editSaving.value = true
  try {
    const cleanSheet = editPayrollSheet.value.map((row: any) => ({
      itemCode: row.itemCode,
      itemName: row.itemName || row.displayName,
      amount: isDeductionItem(row) ? Math.abs(row.finalAmount || 0) : (row.finalAmount || 0),
      calculatedAmount: row.calculatedAmount,
      adjustment: row.adjustment || 0,
      adjustmentReason: row.adjustmentReason || '',
      isManuallyAdded: row.isManuallyAdded || false,
      kind: row.kind,
      ...(row.overrideAccountCode ? { overrideAccountCode: row.overrideAccountCode } : {})
    }))
    const cleanDraft = editAccountingDraft.value.map((r: any) => {
      const { displayAmount, ...rest } = r
      return rest
    })
    const totalAmount = cleanSheet.reduce((sum: number, row: any) => {
      const amt = Math.abs(row.amount || 0)
      return isDeductionItem(row) ? sum - amt : sum + amt
    }, 0)

    const saveResp = await api.post('/payroll/manual/save', {
      month: detailData.value.periodMonth,
      overwrite: true,
      runType: 'manual',
      entries: [{
        employeeId: detailData.value.employeeId,
        employeeCode: detailData.value.employeeCode,
        employeeName: detailData.value.employeeName,
        departmentCode: detailData.value.departmentCode,
        totalAmount,
        payrollSheet: cleanSheet,
        accountingDraft: cleanDraft,
        diffSummary: detailData.value.diffSummary,
        trace: detailData.value.trace,
        metadata: detailData.value.metadata
      }]
    })
    ElMessage.success('保存しました')
    editing.value = false
    const result = saveResp.data || {}
    const newRunId = result.runId || detailData.value.runId
    const newEntryId = result.entryIdsByEmployeeId?.[detailData.value.employeeId] || detailData.value.entryId
    await openEntryDetail({
      runId: newRunId, entryId: newEntryId,
      ...detailData.value
    })
    loadEntries()
  } catch (e: any) {
    console.error(e)
    const msg = e?.response?.data?.error || e?.response?.data?.detail || e?.message || '保存に失敗しました'
    ElMessage.error(msg)
  } finally {
    editSaving.value = false
  }
}

const exporting = ref(false)
async function handleExport() {
  exporting.value = true
  try {
    const params: Record<string, any> = {}
    if (filters.month) params.month = filters.month
    const resp = await api.get('/payroll/run-entries/export', { params, responseType: 'blob' })
    const blob = new Blob([resp.data], { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })
    const url = window.URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `payroll_${filters.month || 'all'}.xlsx`
    document.body.appendChild(a)
    a.click()
    document.body.removeChild(a)
    window.URL.revokeObjectURL(url)
  } catch (err: any) {
    console.error(err)
    ElMessage.error('Excelエクスポートに失敗しました')
  } finally {
    exporting.value = false
  }
}

onMounted(() => {
  loadEmployees()
  loadEntries()
})
</script>

<style scoped>
.payroll-history {
  padding: 16px;
}

.payroll-card {
  border-radius: 12px;
  overflow: hidden;
}

.payroll-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.payroll-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.payroll-header__icon {
  font-size: 22px;
  color: #667eea;
}

.payroll-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

/* フィルター */
.payroll-filters {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
  margin-bottom: 16px;
  padding: 16px;
  background: #f8f9fc;
  border-radius: 8px;
}

.payroll-filters__month {
  width: 130px;
}

.payroll-filters__employee {
  width: 200px;
}

.payroll-filters__type {
  width: 100px;
}

.payroll-filters__keyword {
  width: 160px;
}

/* テーブル */
.payroll-table {
  border-radius: 8px;
  overflow: hidden;
}

.payroll-table :deep(.el-table__header th) {
  background: #f5f7fa;
  font-weight: 600;
  color: #303133;
}

.emp-name-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.emp-name-main {
  font-weight: 500;
  color: #303133;
}

.emp-name-sub {
  font-size: 12px;
  color: #909399;
}

.amount-cell {
  font-weight: 600;
}

.emp-empty {
  color: #c0c4cc;
}

/* ページネーション */
.payroll-pagination {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.payroll-pagination__info {
  font-size: 13px;
  color: #909399;
}

/* 詳細 */
.detail-content {
  padding: 0 8px;
}

.detail-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 16px;
  padding-bottom: 16px;
  border-bottom: 1px solid #ebeef5;
}

.detail-name {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.detail-code {
  font-weight: 400;
  color: #909399;
}

.detail-meta {
  margin-top: 6px;
  font-size: 13px;
  color: #606266;
}

.detail-total {
  font-size: 22px;
  font-weight: 700;
  color: #67c23a;
}

.section-card {
  background: #fff;
  border: 1px solid #ebeef5;
  border-radius: 6px;
  overflow: hidden;
}

.section-card__header {
  display: flex;
  align-items: center;
  padding: 10px 12px;
  background: #f5f7fa;
  font-size: 13px;
  font-weight: 600;
  color: #606266;
  border-bottom: 1px solid #ebeef5;
}

.section-card__body {
  padding: 12px;
}

.trace-json {
  max-height: 300px;
  overflow: auto;
  background: #f5f7fa;
  padding: 12px;
  font-family: monospace;
  font-size: 12px;
  line-height: 1.5;
  border-radius: 4px;
}

.manual-item {
  color: #e6a23c;
  font-weight: 500;
}
</style>
