<template>
  <div class="payroll-execute">
    <el-card class="payroll-card">
      <template #header>
        <div class="payroll-header">
          <div class="payroll-header__left">
            <el-icon class="payroll-header__icon"><Wallet /></el-icon>
            <span class="payroll-header__title">給与計算</span>
            <el-tag v-if="runResult?.entries?.length" size="small" type="success">{{ runResult.entries.length }}名</el-tag>
          </div>
          <div class="payroll-header__right">
            <el-button type="primary" :loading="loading" @click="run">
              <el-icon><CaretRight /></el-icon>
              <span>実行</span>
            </el-button>
            <el-button type="success" :loading="saving" :disabled="!canSave" @click="saveResults">
              <el-icon><FolderChecked /></el-icon>
              <span>保存</span>
            </el-button>
          </div>
        </div>
      </template>
      
      <!-- 検索条件 -->
      <div class="payroll-filters">
        <div class="payroll-filters__top">
          <el-date-picker v-model="month" type="month" value-format="YYYY-MM" placeholder="対象月" class="payroll-filters__month" />
          <el-input v-model="employeeFilter" placeholder="社員を検索…" clearable class="payroll-filters__search" :prefix-icon="Search" />
          <el-button type="primary" size="small" @click="selectAllEmployees">全選択</el-button>
          <el-button type="warning" size="small" @click="selectUncalculated">未計算のみ</el-button>
          <el-button size="small" @click="clearSelection">クリア</el-button>
          <span v-if="!loadingEmployees && employeeOptions.length > 0" class="payroll-filters__count">
            在職 {{ employeeOptions.length }}名<template v-if="totalEmployeeCount > employeeOptions.length"> / 全{{ totalEmployeeCount }}名</template>
            ・選択 {{ employeeIds.length }}名
          </span>
          <div class="payroll-filters__switches">
            <el-checkbox v-model="debug">トレース</el-checkbox>
          </div>
        </div>
        <div v-loading="loadingEmployees" class="employee-listbox">
          <el-checkbox-group v-model="employeeIds">
            <div
              v-for="e in filteredEmployeeOptions"
              :key="e.value"
              class="employee-listbox__item"
              :class="{ 'employee-listbox__item--calculated': e.calculated }"
            >
              <el-checkbox :value="e.value">
                <span class="employee-listbox__label">{{ e.label }}</span>
              </el-checkbox>
              <el-tag v-if="e.calculated" size="small" type="success" effect="plain">済</el-tag>
              <el-tag v-else size="small" type="info" effect="plain">未</el-tag>
            </div>
          </el-checkbox-group>
          <div v-if="!loadingEmployees && filteredEmployeeOptions.length === 0" class="employee-listbox__empty">
            {{ employeeOptions.length === 0 ? '在職社員がありません' : '該当する社員がありません' }}
          </div>
        </div>
      </div>
      
      <!-- エラー・警告 -->
      <el-alert v-if="error" type="error" show-icon :title="error" style="margin-bottom:12px" closable @close="error=''" />
      
      <!-- 手動工時入力ダイアログ -->
      <el-dialog
        v-model="manualHoursDialog.visible"
        title="工時を手動入力"
        width="500px"
        :close-on-click-modal="false"
        class="manual-hours-dialog-wrapper"
        append-to-body
      >
        <div class="manual-hours-dialog">
          <div class="manual-hours-tip">
            <div class="manual-hours-tip__title">
              {{ manualHoursDialog.employeeName }} は時給制のため、工数データが必要です。
            </div>
            <div class="manual-hours-tip__desc">
              手動で工時を入力するか、キャンセルして工数を登録してください。
            </div>
          </div>
          <el-form label-width="100px">
            <el-form-item label="時給">
              <el-input-number
                v-model="manualHoursDialog.hourlyRate"
                :min="0"
                :precision="0"
                :controls="false"
                style="width: 150px"
              />
              <span style="margin-left: 8px">円</span>
            </el-form-item>
            <el-form-item label="総工時">
              <el-input-number
                v-model="manualHoursDialog.hours"
                :min="0"
                :max="744"
                :precision="0"
                :controls="false"
                style="width: 80px"
              />
              <span style="margin: 0 8px">時間</span>
              <el-input-number
                v-model="manualHoursDialog.minutes"
                :min="1"
                :max="59"
                :precision="0"
                :controls="false"
                style="width: 60px"
              />
              <span style="margin-left: 8px">分</span>
            </el-form-item>
            <el-form-item label="計算結果">
              <span class="manual-hours-result">¥{{ formatAmount(manualHoursDialog.hourlyRate * (manualHoursDialog.hours + (manualHoursDialog.minutes ?? 0) / 60)) }}</span>
            </el-form-item>
          </el-form>
        </div>
        <template #footer>
          <el-button @click="manualHoursDialog.visible = false">キャンセル</el-button>
          <el-button type="primary" :loading="loading" @click="submitManualHours">
            この工時で計算
          </el-button>
        </template>
      </el-dialog>
      
      <!-- 項目追加ダイアログ -->
      <el-dialog
        v-model="addItemDialog.visible"
        title="給与項目を追加"
        width="500px"
        :close-on-click-modal="false"
        append-to-body
      >
        <el-form label-width="100px">
          <el-form-item label="項目種類">
            <el-select 
              v-model="addItemDialog.itemCode" 
              placeholder="選択または入力" 
              filterable 
              allow-create 
              clearable 
              style="width: 100%"
              @change="onItemCodeChange"
            >
              <el-option-group label="収入項目">
                <el-option 
                  v-for="item in standardPayrollItems.filter(i => i.kind === 'earning')" 
                  :key="item.code" 
                  :label="item.name" 
                  :value="item.code" 
                />
              </el-option-group>
              <el-option-group label="控除項目">
                <el-option 
                  v-for="item in standardPayrollItems.filter(i => i.kind === 'deduction')" 
                  :key="item.code" 
                  :label="item.name" 
                  :value="item.code" 
                />
              </el-option-group>
            </el-select>
          </el-form-item>
          <el-form-item label="項目名">
            <el-input v-model="addItemDialog.itemName" placeholder="カスタム項目名（省略可）" />
          </el-form-item>
          <el-form-item label="区分">
            <el-radio-group v-model="addItemDialog.kind">
              <el-radio value="earning">収入（支給）</el-radio>
              <el-radio value="deduction">控除</el-radio>
            </el-radio-group>
          </el-form-item>
          <el-form-item label="金額">
            <el-input-number
              v-model="addItemDialog.amount"
              :min="0"
              :controls="false"
              style="width: 180px"
            />
            <span style="margin-left: 8px; color: #909399">円</span>
          </el-form-item>
          <el-form-item label="理由" required>
            <el-input v-model="addItemDialog.adjustmentReason" placeholder="追加理由を入力（必須）" />
          </el-form-item>
        </el-form>
        <template #footer>
          <el-button @click="addItemDialog.visible = false">キャンセル</el-button>
          <el-button type="primary" @click="confirmAddItem">追加</el-button>
        </template>
      </el-dialog>
      
      
      <!-- 結果表示 -->
      <div v-if="runResult?.entries?.length" class="results-section">
        <div class="results-info">
          <span>月: {{ runResult.month }}</span>
          <span>対象社員: {{ runResult.entries.length }}名</span>
        </div>
        
        <el-collapse v-model="activePanels">
          <el-collapse-item v-for="entry in runResult.entries" :key="entry.employeeId" :name="entry.employeeId">
            <template #title>
              <div class="entry-title">
                <span class="entry-name">{{ entry.employeeName || entry.employeeCode || entry.employeeId }}</span>
                <span class="entry-dept">{{ formatDepartment(entry) }}</span>
                <span class="entry-amount" :class="{ 'has-adjustment': hasAnyAdjustment(entry) }">
                  ¥{{ formatAmount(calculateNetAmount(entry)) }}
                  <el-tag v-if="hasAnyAdjustment(entry)" size="small" type="warning" style="margin-left: 6px">調整あり</el-tag>
                </span>
            </div>
            </template>
            
            <div class="entry-content">
            <el-alert
              v-if="entry.diffSummary"
              type="info"
              :title="formatDiff(entry.diffSummary)"
              show-icon
                style="margin-bottom:12px"
            />
            <el-alert
              v-for="warn in entry.warnings || []"
              :key="warn.code || warn.message"
              :type="warn.severity === 'error' ? 'error' : 'warning'"
              show-icon
              :title="warningDescriptions[warn.code] || warn.message || '注意事項'"
                style="margin-bottom:12px"
            />
              
              <el-row :gutter="16">
              <el-col :span="12">
                  <div class="section-card">
                    <div class="section-card__header">
                      <span>給与項目</span>
                      <el-button type="primary" size="small" text @click="openAddItemDialog(entry)">
                        <el-icon><Plus /></el-icon>
                        <span>項目追加</span>
                      </el-button>
                    </div>
                    <el-table :data="entry.payrollSheet || []" size="small" border>
                      <el-table-column label="項目" min-width="90">
                        <template #default="{ row }">
                          <span :class="{ 'manual-item': row.isManuallyAdded }">
                            {{ row.displayName || row.itemCode || row.itemName }}
                          </span>
                        </template>
                      </el-table-column>
                      <el-table-column label="計算額" width="90" align="right">
                        <template #default="{ row }">
                          <span class="calculated-amount">{{ formatAmount(row.calculatedAmount) }}</span>
                        </template>
                      </el-table-column>
                      <el-table-column label="調整額" width="100" align="right">
                        <template #default="{ row }">
                          <el-input-number
                            v-model="row.adjustment"
                            :controls="false"
                            size="small"
                            class="adjustment-input"
                            @change="onAdjustmentChange(entry, row)"
                          />
                        </template>
                      </el-table-column>
                      <el-table-column label="最終額" width="90" align="right">
                        <template #default="{ row }">
                          <span class="final-amount" :class="{ 'has-adjustment': row.adjustment !== 0 }">
                            {{ formatAmount(row.finalAmount) }}
                          </span>
                        </template>
                      </el-table-column>
                      <el-table-column label="調整理由" min-width="120">
                        <template #default="{ row }">
                          <el-input
                            v-if="row.adjustment !== 0 || row.isManuallyAdded"
                            v-model="row.adjustmentReason"
                            size="small"
                            placeholder="理由を入力"
                          />
                          <span v-else class="no-reason">—</span>
                        </template>
                      </el-table-column>
                      <el-table-column width="40" align="center">
                        <template #default="{ row, $index }">
                          <el-button
                            v-if="row.isManuallyAdded"
                            type="danger"
                            size="small"
                            text
                            circle
                            @click="removeManualItem(entry, $index)"
                          >
                            <el-icon><Delete /></el-icon>
                          </el-button>
                        </template>
                      </el-table-column>
                    </el-table>
                    <div class="payroll-summary">
                      <span class="payroll-summary__label">差引支給額:</span>
                      <span class="payroll-summary__value">{{ formatAmount(calculateNetAmount(entry)) }}</span>
                    </div>
                  </div>
              </el-col>
                <el-col :span="12">
                  <div v-if="entry.workHours" class="section-card" style="margin-bottom:12px">
                    <div class="section-card__header">勤怠サマリー</div>
                    <el-descriptions :column="4" size="small" border>
                    <el-descriptions-item
                      v-for="item in workHourItems"
                      :key="item.key"
                      :label="item.label"
                    >
                      {{ formatWorkHour(entry.workHours?.[item.key]) }}
                    </el-descriptions-item>
                  </el-descriptions>
                  </div>
                  <div class="section-card">
                    <div class="section-card__header">
                      <span>仕訳ドラフト</span>
                      <span v-if="entry._regenerating" class="journal-status">
                        <el-icon class="is-loading"><Refresh /></el-icon>
                        <span>更新中...</span>
                      </span>
                      <el-button 
                        v-else-if="entry._journalStale"
                        type="warning" 
                        size="small" 
                        text
                        @click="regenerateJournal(entry)"
                      >
                        <el-icon><Refresh /></el-icon>
                        <span>再計算</span>
                      </el-button>
                    </div>
                    <el-table :data="entry.accountingDraft || []" size="small" border :row-class-name="journalRowClass">
                      <el-table-column label="科目コード" width="160">
                        <template #default="{ row }">
                          <el-select
                            v-if="row.needsAccount"
                            v-model="row.accountCode"
                            placeholder="科目を選択"
                            filterable
                            size="small"
                            style="width: 100%"
                            @change="(val: string) => onOverrideAccountChange(entry, row, val)"
                          >
                            <el-option
                              v-for="acc in accountOptions"
                              :key="acc.code"
                              :label="`${acc.code} ${acc.name}`"
                              :value="acc.code"
                            />
                          </el-select>
                          <span v-else>{{ row.accountCode }}</span>
                        </template>
                      </el-table-column>
                      <el-table-column label="科目名" min-width="120">
                        <template #default="{ row }">
                          <span v-if="row.needsAccount" :class="{ 'needs-account': !row.accountCode }">
                            {{ row.accountCode ? accountNameMap[row.accountCode] || '' : row.accountName }}
                          </span>
                          <span v-else>{{ row.accountName }}</span>
                        </template>
                      </el-table-column>
                      <el-table-column prop="drcr" label="借/貸" width="60" align="center" />
                      <el-table-column label="金額" width="100" align="right">
                      <template #default="{ row }">
                        {{ row.displayAmount || formatAmount(row.amount) }}
                      </template>
                    </el-table-column>
                      <el-table-column prop="departmentName" label="部門" min-width="100">
                      <template #default="{ row }">
                          {{ row.departmentName || row.departmentCode || '—' }}
                      </template>
                    </el-table-column>
                  </el-table>
                  </div>
              </el-col>
            </el-row>
              
              <div v-if="debug && entry.trace" class="section-card" style="margin-top:12px">
                <div class="section-card__header">計算トレース</div>
                <el-table :data="entry.trace" size="small" border max-height="280">
                  <el-table-column prop="step" label="ステップ" width="140" />
                  <el-table-column prop="source" label="ソース" width="80" />
                  <el-table-column prop="item" label="項目" width="100" />
                  <el-table-column prop="amount" label="金額" width="90" />
                  <el-table-column prop="base" label="基数" width="90" />
                  <el-table-column prop="rate" label="料率" width="70" />
                  <el-table-column prop="lawVersion" label="法令Ver" width="130" />
                  <el-table-column prop="lawNote" label="法令備考" min-width="120" />
                  <el-table-column prop="note" label="備考" min-width="100" />
              </el-table>
              </div>
            </div>
          </el-collapse-item>
        </el-collapse>
      </div>
      <el-empty v-else description="社員を選択して「実行」をクリックしてください" :image-size="80" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Wallet, CaretRight, FolderChecked, Plus, Delete, Refresh, Search } from '@element-plus/icons-vue'
import api from '../api'

const employeeIds = ref<string[]>([])
const employeeOptions = ref<any[]>([])
const allEmployees = ref<any[]>([])
const loadingEmployees = ref(false)
const employeeFilter = ref('')
const calculatedEmployeeIds = ref<Set<string>>(new Set())
const totalEmployeeCount = ref(0)
const contractorTypeCodes = ref<Set<string>>(new Set())
const month = ref(new Date().toISOString().slice(0,7))
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const runResult = ref<any | null>(null)
const debug = ref(true)
const activePanels = ref<string[]>([])
const canSave = computed(() => !!(runResult.value && Array.isArray(runResult.value.entries) && runResult.value.entries.length > 0))
const workHourItems = [
  { key: 'totalHours', label: '総勤務' },
  { key: 'regularHours', label: '所定内' },
  { key: 'overtimeHours', label: '時間外' },
  { key: 'overtime60Hours', label: '60h超' },
  { key: 'holidayHours', label: '休日' },
  { key: 'lateNightHours', label: '深夜' },
  { key: 'absenceHours', label: '不足' }
]
const warningDescriptions: Record<string, string> = {
  workHoursMissing: '勤怠データが未登録のため、残業・控除は実績無しとして計算されています。',
  usedStandardHours: '勤怠データが未登録のため、月標準工時で計算されています。'
}

// 科目マスタ（仕訳ドラフトの未マッピング行で使用）
const accountOptions = ref<{ code: string; name: string }[]>([])
const accountNameMap = ref<Record<string, string>>({})

async function loadAccountOptions() {
  try {
    const resp = await api.post('/objects/account/search', { where: [], page: 1, pageSize: 500, orderBy: [{ field: 'account_code', dir: 'ASC' }] })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    accountOptions.value = rows.map((a: any) => ({
      code: a.account_code || a.payload?.code,
      name: a.payload?.name || a.account_code
    }))
    const map: Record<string, string> = {}
    for (const acc of accountOptions.value) { map[acc.code] = acc.name }
    accountNameMap.value = map
  } catch (e) {
    console.error('Failed to load accounts:', e)
  }
}

function journalRowClass({ row }: { row: any }) {
  return row.needsAccount ? 'needs-account-row' : ''
}

function onOverrideAccountChange(entry: any, row: any, accountCode: string) {
  row.accountName = accountNameMap.value[accountCode] || ''
  const itemCode = row.itemCode
  if (itemCode && entry.payrollSheet) {
    const sheetRow = entry.payrollSheet.find((r: any) => r.itemCode === itemCode)
    if (sheetRow) {
      sheetRow.overrideAccountCode = accountCode
    }
  }
  entry._rawAccountingDraft = (entry.accountingDraft || []).map((r: any) => {
    const { displayAmount, ...rest } = r
    return rest
  })
}

// 手動工時入力ダイアログ
const manualHoursDialog = ref({
  visible: false,
  employeeId: '',
  employeeCode: '',
  employeeName: '',
  hourlyRate: 0,
  hours: 0,
  minutes: null as number | null
})

// 項目追加ダイアログ
const addItemDialog = ref({
  visible: false,
  targetEntry: null as any,
  itemCode: '',
  itemName: '',
  amount: 0,
  kind: 'earning' as 'earning' | 'deduction',
  adjustmentReason: ''
})

// 標準給与項目リスト（kind: earning=収入, deduction=控除）
const standardPayrollItems = [
  { code: 'BONUS', name: '賞与', kind: 'earning' },
  { code: 'ALLOWANCE_SPECIAL', name: '特別手当', kind: 'earning' },
  { code: 'ALLOWANCE_HOUSING', name: '住宅手当', kind: 'earning' },
  { code: 'ALLOWANCE_FAMILY', name: '家族手当', kind: 'earning' },
  { code: 'ALLOWANCE_POSITION', name: '役職手当', kind: 'earning' },
  { code: 'NENMATSU_KANPU', name: '年末調整還付', kind: 'earning' },
  { code: 'DEDUCT_LOAN', name: '貸付金返済', kind: 'deduction' },
  { code: 'DEDUCT_ADVANCE', name: '前払金精算', kind: 'deduction' },
  { code: 'DEDUCT_OTHER', name: 'その他控除', kind: 'deduction' },
  { code: 'NENMATSU_CHOSHU', name: '年末調整徴収', kind: 'deduction' },
  { code: 'ADJUST_OTHER', name: 'その他調整', kind: 'earning' }
]

// 項目追加ダイアログを開く
function openAddItemDialog(entry: any) {
  addItemDialog.value = {
    visible: true,
    targetEntry: entry,
    itemCode: '',
    itemName: '',
    amount: 0,
    kind: 'earning',
    adjustmentReason: ''
  }
}

// 項目種類が変更された時、kindを自動設定
function onItemCodeChange(code: string) {
  const item = standardPayrollItems.find(i => i.code === code)
  if (item) {
    addItemDialog.value.kind = item.kind as 'earning' | 'deduction'
  } else {
    // カスタム項目の場合はデフォルトに戻す（ユーザーに選択させる）
    addItemDialog.value.kind = 'earning'
  }
}

// 項目を追加
function confirmAddItem() {
  const dialog = addItemDialog.value
  if (!dialog.targetEntry) return
  
  const itemDef = standardPayrollItems.find(i => i.code === dialog.itemCode)
  const itemName = dialog.itemName || itemDef?.name || dialog.itemCode
  if (!itemName) {
    ElMessage.warning('項目名を入力してください')
    return
  }
  
  if (!dialog.adjustmentReason) {
    ElMessage.warning('追加理由を入力してください')
    return
  }
  
  // 控除項目は正数で保持（ネット計算時に控除扱い）
  const finalAmount = Math.abs(dialog.amount)
  
  dialog.targetEntry.payrollSheet.push({
    itemCode: dialog.itemCode || `MANUAL_${Date.now()}`,
    itemName: itemName,
    displayName: itemName,
    calculatedAmount: 0,
    adjustment: finalAmount,
    finalAmount: finalAmount,
    adjustmentReason: dialog.adjustmentReason,
    isManuallyAdded: true,
    kind: dialog.kind
  })
  
  // 自動で分録を再計算
  triggerJournalRegeneration(dialog.targetEntry)
  
  dialog.visible = false
}

// 防抖用のタイマーMap
const debounceTimers = new Map<string, ReturnType<typeof setTimeout>>()
// 競合防止用のリクエストバージョン
const requestVersions = new Map<string, number>()

// 調整額が変更された時
function onAdjustmentChange(entry: any, row: any) {
  row.finalAmount = (row.calculatedAmount || 0) + (row.adjustment || 0)
  // 控除項目はマイナスにならないように丸める
  if (isDeductionItem(row) && row.finalAmount < 0) {
    row.finalAmount = 0
  }
  // 自動で分録を再計算（防抖）
  triggerJournalRegeneration(entry)
}

// 手動追加項目を削除
function removeManualItem(entry: any, index: number) {
  entry.payrollSheet.splice(index, 1)
  // 自動で分録を再計算
  triggerJournalRegeneration(entry)
}

// 防抖付きで分録再計算をトリガー
function triggerJournalRegeneration(entry: any) {
  const key = entry.employeeId
  
  // 既存のタイマーをキャンセル
  if (debounceTimers.has(key)) {
    clearTimeout(debounceTimers.get(key))
  }
  
  // 一時的に「計算中」マークを付ける
  entry._journalStale = true
  
  // 500ms後に再計算を実行
  const timer = setTimeout(() => {
    debounceTimers.delete(key)
    regenerateJournal(entry)
  }, 500)
  
  debounceTimers.set(key, timer)
}

// 控除項目かどうか
function isDeductionItem(row: any): boolean {
  if (row?.kind) return row.kind === 'deduction'
  const code = (row?.itemCode || '').toString().toUpperCase()
  return code === 'HEALTH_INS'
    || code === 'CARE_INS'
    || code === 'PENSION'
    || code === 'EMP_INS'
    || code === 'WHT'
    || code === 'RESIDENT_TAX'
    || code === 'ABSENCE_DEDUCT'
    || code === 'DEDUCT_LOAN'
    || code === 'DEDUCT_ADVANCE'
    || code === 'DEDUCT_OTHER'
    || code === 'NENMATSU_CHOSHU'
}

// 調整があるかどうかをチェック
function hasAnyAdjustment(entry: any) {
  if (!entry?.payrollSheet?.length) return false
  return entry.payrollSheet.some((row: any) => row.adjustment !== 0 || row.isManuallyAdded)
}

// 差引支給額を計算（控除はマイナス扱い）
function calculateNetAmount(entry: any) {
  if (!entry?.payrollSheet?.length) return 0
  return entry.payrollSheet.reduce((sum: number, row: any) => {
    const amount = Math.abs(row.finalAmount || 0)
    return isDeductionItem(row) ? sum - amount : sum + amount
  }, 0)
}

// 調整理由が必要な項目に理由が入っているかチェック
function validateAdjustmentReasons(entry: any): boolean {
  if (!entry?.payrollSheet?.length) return true
  for (const row of entry.payrollSheet) {
    if ((row.adjustment !== 0 || row.isManuallyAdded) && !row.adjustmentReason?.trim()) {
      return false
    }
  }
  return true
}

// 仕訳を再生成
async function regenerateJournal(entry: any) {
  const key = entry.employeeId
  
  // バージョン番号をインクリメント
  const currentVersion = (requestVersions.get(key) || 0) + 1
  requestVersions.set(key, currentVersion)
  
  entry._regenerating = true
  try {
    const payload = {
      payrollSheet: entry.payrollSheet.map((row: any) => ({
        itemCode: row.itemCode,
        itemName: row.itemName || row.displayName,
        amount: isDeductionItem(row) ? Math.abs(row.finalAmount || 0) : (row.finalAmount || 0),
        finalAmount: row.finalAmount,
        kind: row.kind,
        ...(row.overrideAccountCode ? { overrideAccountCode: row.overrideAccountCode } : {})
      })),
      employeeId: entry.employeeId,
      employeeCode: entry.employeeCode,
      departmentCode: entry.departmentCode,
      departmentName: entry.departmentName
    }
    
    const r = await api.post('/payroll/regenerate-journal', payload)
    
    // 競合チェック：このリクエストが最新でなければ無視
    if (requestVersions.get(key) !== currentVersion) {
      return // 新しいリクエストが発行されたので、この結果は破棄
    }
    
    // 更新会计分录
    entry.accountingDraft = (r.data?.accountingDraft || []).map((row: any) => ({
      ...row,
      displayAmount: formatAmount(row.amount)
    }))
    entry._rawAccountingDraft = r.data?.accountingDraft || []
    
    // 清除过期标记
    entry._journalStale = false
    
  } catch (e: any) {
    // 競合チェック
    if (requestVersions.get(key) !== currentVersion) {
      return
    }
    ElMessage.error(extractErrorMessage(e, '仕訳の再生成に失敗しました'))
  } finally {
    // 競合チェック：最新リクエストの場合のみフラグをクリア
    if (requestVersions.get(key) === currentVersion) {
      entry._regenerating = false
    }
  }
}

function extractErrorMessage(err: any, fallback: string) {
  if (!err) return fallback
  const resp = err?.response?.data
  if (resp) {
    if (typeof resp === 'string') return resp
    if (resp.message) return resp.message
    if (resp.payload?.message) return resp.payload.message
    const payloadErr = resp.payload?.error
    const payloadDetail = resp.payload?.detail
    if (payloadErr) return payloadDetail ? `${payloadErr}: ${payloadDetail}` : payloadErr
    const errMsg = resp.error
    const detail = resp.detail
    if (errMsg) return detail ? `${errMsg}: ${detail}` : errMsg
  }
  if (err.message) return err.message
  return fallback
}

function formatAmount(value: number){
  return Number(value || 0).toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 0 })
}

function formatDepartment(entry: any){
  if (!entry) return '未設定'
  const name = entry.departmentName
  const code = entry.departmentCode
  if (name && code) return `${name} (${code})`
  return name || code || '未設定'
}

function formatWorkHour(value?: number | null) {
  if (typeof value !== 'number' || Number.isNaN(value)) return '0h'
  const sign = value < 0 ? '-' : ''
  const totalMinutes = Math.round(Math.abs(value) * 60)
  const hours = Math.floor(totalMinutes / 60)
  const minutes = totalMinutes % 60
  if (minutes === 0) return `${sign}${hours}h`
  if (hours === 0) return `${sign}${minutes}m`
  return `${sign}${hours}h${minutes.toString().padStart(2, '0')}m`
}

function formatDiff(diff:any){
  if (!diff) return ''
  const avg = diff.previousAverage ?? diff.previousaverage ?? 0
  const difference = diff.difference ?? diff.difference ?? 0
  const percent = diff.differencePercent ?? diff.differencepercent ?? null
  const pctText = typeof percent === 'number' ? ` (${(percent * 100).toFixed(1)}%)` : ''
  return `過去平均: ¥${formatAmount(avg)} / 差分: ¥${formatAmount(difference)}${pctText}`
}

async function run(){
  if (!employeeIds.value.length || !month.value){ error.value='社員と月を選択してください'; return }
  loading.value=true; error.value=''; runResult.value=null
  manualWorkHoursMap.value = {} // 清空手动工时记录
  try{
    const payload:any = { employeeIds: employeeIds.value, month: month.value, debug: debug.value }
    const r = await api.post('/payroll/manual/run', payload)
    runResult.value = {
      ...r.data,
      entries: Array.isArray(r.data?.entries)
        ? r.data.entries.map((entry: any) => ({
            ...entry,
            departmentName: entry.departmentName || r.data.departmentName || '',
            workHours: entry.workHours || null,
            warnings: entry.warnings || [],
            _rawPayrollSheet: entry.payrollSheet || [],
            _rawAccountingDraft: entry.accountingDraft || [],
            payrollSheet: (entry.payrollSheet || []).map((row: any) => ({
              ...row,
              displayName: row.itemName || row.displayName || row.itemCode,
              calculatedAmount: row.amount,
              adjustment: 0,
              finalAmount: row.amount,
              adjustmentReason: '',
              isManuallyAdded: false
            })),
            accountingDraft: (entry.accountingDraft || []).map((row: any) => ({
              ...row,
              displayAmount: formatAmount(row.amount)
            }))
          }))
        : []
    }
    const panels = Array.isArray(r.data?.entries) ? r.data.entries.map((entry:any)=>entry.employeeId) : []
    activePanels.value = panels
  }
  catch(e:any){
    // 检查是否是时薪缺少工时的特殊错误
    const resp = e?.response?.data
    if (resp?.error === 'hourly_rate_missing_timesheet' || resp?.payload?.error === 'hourly_rate_missing_timesheet') {
      const data = resp?.payload || resp
      // 弹出手动输入工时对话框
      manualHoursDialog.value = {
        visible: true,
        employeeId: data.employeeId || '',
        employeeCode: data.employeeCode || '',
        employeeName: data.employeeName || data.employeeCode || '',
        hourlyRate: data.hourlyRate || 0,
        hours: 160, // 默认按一个月标准工时
        minutes: null
      }
    } else {
      error.value = extractErrorMessage(e, '実行に失敗しました')
    }
  }
  finally{ loading.value=false }
}

// 手动工时记录（用于重新计算时保留）
const manualWorkHoursMap = ref<Record<string, { totalHours: number; hourlyRate: number }>>({})

// 提交手动输入的工时
async function submitManualHours() {
  const dialog = manualHoursDialog.value
  const totalHours = dialog.hours + (dialog.minutes ?? 0) / 60
  if (totalHours <= 0) {
    ElMessage.warning('工時を入力してください')
    return
  }
  
  // 保存手动工时到 map
  manualWorkHoursMap.value[dialog.employeeId] = {
    totalHours: totalHours,
    hourlyRate: dialog.hourlyRate
  }
  
  // 关闭对话框
  manualHoursDialog.value.visible = false
  
  loading.value = true
  error.value = ''
  
  try {
    // 重新计算所有选中的员工，带上已输入的手动工时
    const payload: any = {
      employeeIds: employeeIds.value,
      month: month.value,
      debug: debug.value,
      manualWorkHours: manualWorkHoursMap.value
    }
    
    const r = await api.post('/payroll/manual/run', payload)
    
    // 处理结果
    runResult.value = {
      ...r.data,
      entries: Array.isArray(r.data?.entries)
        ? r.data.entries.map((entry: any) => ({
            ...entry,
            departmentName: entry.departmentName || r.data.departmentName || '',
            workHours: entry.workHours || null,
            warnings: entry.warnings || [],
            _rawPayrollSheet: entry.payrollSheet || [],
            _rawAccountingDraft: entry.accountingDraft || [],
            payrollSheet: (entry.payrollSheet || []).map((row: any) => ({
              ...row,
              displayName: row.itemName || row.displayName || row.itemCode,
              calculatedAmount: row.amount,
              adjustment: 0,
              finalAmount: row.amount,
              adjustmentReason: '',
              isManuallyAdded: false
            })),
            accountingDraft: (entry.accountingDraft || []).map((row: any) => ({
              ...row,
              displayAmount: formatAmount(row.amount)
            }))
          }))
        : []
    }
    
    // 展开所有员工的面板
    const panels = Array.isArray(r.data?.entries) ? r.data.entries.map((entry: any) => entry.employeeId) : []
    activePanels.value = panels
    
    ElMessage.success(`${runResult.value.entries.length}名の給与を計算しました`)
  } catch (e: any) {
    // 如果还有其他时薪员工需要输入工时，继续弹窗
    const resp = e?.response?.data
    if (resp?.error === 'hourly_rate_missing_timesheet' || resp?.payload?.error === 'hourly_rate_missing_timesheet') {
      const data = resp?.payload || resp
      manualHoursDialog.value = {
        visible: true,
        employeeId: data.employeeId || '',
        employeeCode: data.employeeCode || '',
        employeeName: data.employeeName || data.employeeCode || '',
        hourlyRate: data.hourlyRate || 0,
        hours: 160,
        minutes: null
      }
    } else {
      error.value = extractErrorMessage(e, '計算に失敗しました')
    }
  } finally {
    loading.value = false
  }
}

async function saveResults(){
  if (!runResult.value || !Array.isArray(runResult.value.entries) || runResult.value.entries.length === 0){ return }
  
  // 仕訳更新中・未更新チェック
  for (const entry of runResult.value.entries) {
    if (entry._regenerating) {
      const name = entry.employeeName || entry.employeeCode || entry.employeeId
      ElMessage.warning(`${name} の仕訳を更新中です。完了までお待ちください。`)
      return
    }
    if (entry._journalStale) {
      const name = entry.employeeName || entry.employeeCode || entry.employeeId
      ElMessage.warning(`${name} の仕訳が未更新です。しばらく待ってから再度保存してください。`)
      return
    }
  }
  
  // 調整理由の必須チェック
  for (const entry of runResult.value.entries) {
    if (!validateAdjustmentReasons(entry)) {
      const name = entry.employeeName || entry.employeeCode || entry.employeeId
      ElMessage.warning(`${name} の調整項目に理由が入力されていません。調整理由は必須です。`)
      return
    }
  }
  
  // 未指定科目チェック
  for (const entry of runResult.value.entries) {
    const draft = entry.accountingDraft || []
    const missing = draft.filter((r: any) => r.needsAccount && !r.accountCode)
    if (missing.length > 0) {
      const name = entry.employeeName || entry.employeeCode || entry.employeeId
      const items = missing.map((r: any) => r.itemName || r.itemCode || '不明').join('、')
      ElMessage.warning(`${name} の仕訳に科目が未選択の行があります（${items}）。科目を選択してください。`)
      return
    }
  }

  // 既存データの上書き確認：対象員工に既存データがある場合のみ確認ダイアログを表示
  const existingEmployeeIds: string[] = runResult.value.existingEmployeeIds || []
  const currentEmployeeIds = runResult.value.entries.map((e: any) => e.employeeId)
  const overlappingEmployees = currentEmployeeIds.filter((id: string) => existingEmployeeIds.includes(id))
  
  if (overlappingEmployees.length > 0) {
    // 既存データがある員工がいる場合、確認ダイアログを表示
    const overlappingNames = runResult.value.entries
      .filter((e: any) => overlappingEmployees.includes(e.employeeId))
      .map((e: any) => e.employeeName || e.employeeCode || e.employeeId)
      .join('、')
    
    try {
      await ElMessageBox.confirm(
        `以下の社員の給与データは既に保存されています。上書きしますか？\n\n${overlappingNames}`,
        '確認',
        {
          confirmButtonText: '上書きする',
          cancelButtonText: 'キャンセル',
          type: 'warning'
        }
      )
      // 用户确认覆盖
      await doSave(true)
    } catch {
      // 用户取消
    }
  } else {
    // 既存データがない場合、直接保存（overwrite=true で既存 run があっても追加可能に）
    await doSave(true)
  }
}

// 実際の保存処理
async function doSave(overwrite: boolean) {
  if (!runResult.value) return
  
  saving.value = true
  error.value = ''
  try{
    const body = {
      month: runResult.value.month || month.value,
      policyId: runResult.value.policyId || null,
      overwrite: overwrite,
      runType: 'manual',
      entries: runResult.value.entries.map((entry:any)=>{
        // 使用当前显示的 payrollSheet（可能包含调整）
        const currentPayrollSheet = entry.payrollSheet || []
        const rawAccountingDraft = entry._rawAccountingDraft || entry.accountingDraft || []
        
        // 清理 payrollSheet，保留调整信息
        const cleanPayrollSheet = currentPayrollSheet.map((row: any) => ({
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
        
        const cleanAccountingDraft = rawAccountingDraft.map((row: any) => {
          const { displayAmount, ...rest } = row
          return rest
        })
        
        // 重新计算 totalAmount（基于最终金额，控除为负）
        const totalAmount = cleanPayrollSheet.reduce((sum: number, row: any) => {
          const amount = Math.abs(row.amount || 0)
          return isDeductionItem(row) ? sum - amount : sum + amount
        }, 0)
        
        const payload:any = {
          employeeId: entry.employeeId,
          employeeCode: entry.employeeCode,
          employeeName: entry.employeeName,
          departmentCode: entry.departmentCode,
          totalAmount: totalAmount,
          payrollSheet: cleanPayrollSheet,
          accountingDraft: cleanAccountingDraft,
          diffSummary: entry.diffSummary,
          trace: debug.value ? entry.trace : null
        }
        if (entry.workHours) {
          payload.metadata = { source: 'manual', workHours: entry.workHours }
        }
        return payload
      })
    }
    const saveResp = await api.post('/payroll/manual/save', body)
    const entryCount = saveResp.data?.entryCount || body.entries.length
    const voucherCount = saveResp.data?.voucherCount ?? 0
    if (voucherCount < entryCount) {
      ElMessage.warning(`保存しました（${entryCount}名中${voucherCount}名の仕訳を生成）`)
    } else {
      ElMessage.success(`保存しました（${entryCount}名、仕訳${voucherCount}件）`)
    }
    if (runResult.value){
      runResult.value.hasExisting = true
    }
    refreshCalculatedStatus()
  }catch(e:any){
    // 409 冲突：既存データとの競合（通常は saveResults で事前確認済みなのでここには来ない）
    if (e?.response?.status === 409) {
      error.value = '保存に失敗しました。給与データが他のユーザーによって更新された可能性があります。再計算してからやり直してください。'
      return
    }
    const resp = e?.response?.data
    const errMsg = resp?.error || resp?.payload?.error
    if (typeof errMsg === 'string' && errMsg.includes('会計仕訳に有効な行')) {
      error.value = '会計仕訳に有効な行がありません。科目コード/金額を確認し、仕訳ドラフトを再計算してから保存してください。'
    } else {
      error.value = extractErrorMessage(e, '保存に失敗しました')
    }
  } finally {
    saving.value = false
  }
}

async function loadEmployeesForMonth(){
  if (!month.value) return
  loadingEmployees.value = true
  try {
    let page = 1
    const pageSize = 500
    let all: any[] = []
    while (true) {
      const r = await api.post('/objects/employee/search', { 
        page, 
        pageSize, 
        where: [], 
        orderBy: [{ field: 'employee_code', dir: 'ASC' }] 
      })
      const list = r.data?.data || []
      all = all.concat(list)
      if (list.length < pageSize) break
      page++
    }
    totalEmployeeCount.value = all.length
    const targetMonth = month.value
    const monthStart = `${targetMonth}-01`
    const monthEnd = `${targetMonth}-31`
    const contractorPatterns = ['CONTRACTOR', '個人事業主', '個人事业主', '个人事业主']
    const activeEmployees = all.filter(emp => {
      const contracts = emp.payload?.contracts || []
      if (!contracts.length) return false
      return contracts.some((c: any) => {
        const from = c.periodFrom || '1900-01-01'
        const to = c.periodTo || '9999-12-31'
        const typeCode = (c.employmentTypeCode || '').toString()
        if (contractorPatterns.some(p => typeCode === p || typeCode.includes(p))) return false
        return from <= monthEnd && to >= monthStart
      })
    })
    allEmployees.value = activeEmployees

    try {
      const calcResp = await api.get('/payroll/run-entries/calculated-employees', { params: { month: month.value } })
      calculatedEmployeeIds.value = new Set((calcResp.data || []) as string[])
    } catch { calculatedEmployeeIds.value = new Set() }

    employeeOptions.value = activeEmployees.map(x => ({
      label: `${x.payload?.nameKanji || x.payload?.name || x.name || ''} (${x.employee_code || x.payload?.code || ''})`,
      value: x.id,
      calculated: calculatedEmployeeIds.value.has(x.id)
    }))
    const validIds = new Set(activeEmployees.map(e => e.id))
    employeeIds.value = employeeIds.value.filter(id => validIds.has(id))
  } catch (e) {
    console.error('加载员工失败', e)
  } finally {
    loadingEmployees.value = false
  }
}

const filteredEmployeeOptions = computed(() => {
  const q = (employeeFilter.value || '').trim().toLowerCase()
  if (!q) return employeeOptions.value
  return employeeOptions.value.filter(e => e.label.toLowerCase().includes(q))
})

function selectAllEmployees() {
  const visible = filteredEmployeeOptions.value.map(e => e.value)
  const existing = new Set(employeeIds.value)
  visible.forEach(id => existing.add(id))
  employeeIds.value = employeeOptions.value.filter(e => existing.has(e.value)).map(e => e.value)
}

function selectUncalculated() {
  employeeIds.value = employeeOptions.value.filter(e => !e.calculated).map(e => e.value)
}

function clearSelection() {
  employeeIds.value = []
}

async function refreshCalculatedStatus() {
  if (!month.value) return
  try {
    const resp = await api.get('/payroll/run-entries/calculated-employees', { params: { month: month.value } })
    calculatedEmployeeIds.value = new Set((resp.data || []) as string[])
    employeeOptions.value = employeeOptions.value.map(e => ({
      ...e,
      calculated: calculatedEmployeeIds.value.has(e.value)
    }))
  } catch {}
}

watch(month, () => {
  loadEmployeesForMonth()
}, { immediate: false })

onMounted(() => {
  loadEmployeesForMonth()
  loadAccountOptions()
})
</script>

<style scoped>
.payroll-execute {
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
  color: #67c23a;
}

.payroll-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.payroll-header__right {
  display: flex;
  gap: 8px;
}

/* フィルター */
.payroll-filters {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-bottom: 16px;
  padding: 16px;
  background: #f8f9fc;
  border-radius: 8px;
}

.payroll-filters__top {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}

.payroll-filters__month {
  width: 140px;
}

.payroll-filters__search {
  width: 180px;
}

.payroll-filters__count {
  font-size: 12px;
  color: #909399;
  white-space: nowrap;
}

.payroll-filters__switches {
  display: flex;
  gap: 16px;
  margin-left: auto;
}

/* 社員リストボックス */
.employee-listbox {
  border: 1px solid #dcdfe6;
  border-radius: 6px;
  background: #fff;
  max-height: 220px;
  overflow-y: auto;
  padding: 4px 0;
}

.employee-listbox__item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 4px 12px;
  transition: background 0.15s;
  cursor: pointer;
}

.employee-listbox__item:hover {
  background: #f0f5ff;
}

.employee-listbox__item--calculated {
  background: #f6ffed;
}

.employee-listbox__item--calculated:hover {
  background: #e8f7df;
}

.employee-listbox__label {
  font-size: 13px;
}

.employee-listbox__empty {
  padding: 24px 0;
  text-align: center;
  color: #c0c4cc;
  font-size: 13px;
}

/* 結果 */
.results-section {
  margin-top: 8px;
}

.results-info {
  display: flex;
  gap: 16px;
  font-size: 14px;
  color: #606266;
  margin-bottom: 12px;
}

.entry-title {
  display: flex;
  align-items: center;
  gap: 16px;
  width: 100%;
  padding-right: 16px;
}

.entry-name {
  font-weight: 600;
  color: #303133;
}

.entry-dept {
  font-size: 13px;
  color: #909399;
}

.entry-amount {
  margin-left: auto;
  font-weight: 600;
  color: #67c23a;
  display: flex;
  align-items: center;
}

.entry-amount.has-adjustment {
  color: #e6a23c;
}

.entry-content {
  padding: 12px;
  background: #fafbfc;
  border-radius: 6px;
}

.section-card {
  background: #fff;
  border: 1px solid #ebeef5;
  border-radius: 6px;
  overflow: hidden;
}

.section-card__header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 10px 12px;
  background: #f5f7fa;
  font-size: 13px;
  font-weight: 600;
  color: #606266;
  border-bottom: 1px solid #ebeef5;
}

/* 給与項目テーブル */
.calculated-amount {
  color: #909399;
}

.final-amount {
  font-weight: 600;
  color: #303133;
}

.final-amount.has-adjustment {
  color: #e6a23c;
}

.manual-item {
  color: #409eff;
  font-style: italic;
}

:deep(.needs-account-row) {
  background-color: #fdf6ec !important;
}

.needs-account {
  color: #e6a23c;
  font-style: italic;
}

.no-reason {
  color: #c0c4cc;
}

.adjustment-input {
  width: 80px !important;
}

.adjustment-input :deep(.el-input__inner) {
  text-align: right;
}

/* 差引支給額 */
.payroll-summary {
  display: flex;
  justify-content: flex-end;
  align-items: center;
  gap: 12px;
  padding: 12px 16px;
  background: #f0f9eb;
  border-top: 1px solid #e1f3d8;
}

.payroll-summary__label {
  font-size: 14px;
  color: #606266;
}

.payroll-summary__value {
  font-size: 18px;
  font-weight: 700;
  color: #67c23a;
}

/* 手動工時入力ダイアログ */
.manual-hours-dialog {
  padding: 8px 0;
}

.manual-hours-result {
  font-size: 18px;
  font-weight: 600;
  color: #67c23a;
}

/* 仕訳更新中状態 */
.journal-status {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 12px;
  color: #e6a23c;
  font-weight: normal;
}
</style>

<style>
/* el-dialog は append-to-body のため、scoped 外で当てる */
.manual-hours-dialog-wrapper.el-dialog {
  width: 500px !important;
  max-width: calc(100vw - 32px) !important;
  box-sizing: border-box !important;
}

.manual-hours-dialog-wrapper .el-dialog__body {
  overflow: hidden;
  box-sizing: border-box;
}

.manual-hours-dialog-wrapper .manual-hours-tip {
  width: 100%;
  max-width: 100%;
  box-sizing: border-box;
  padding: 12px 14px;
  background: #f4f4f5;
  border: 1px solid #e4e7ed;
  border-radius: 6px;
  margin-bottom: 16px;
  color: #606266;
}

.manual-hours-dialog-wrapper .manual-hours-tip__title {
  font-weight: 600;
  color: #303133;
  margin-bottom: 6px;
  word-break: break-word;
}

.manual-hours-dialog-wrapper .manual-hours-tip__desc {
  color: #606266;
  word-break: break-word;
}
</style>
