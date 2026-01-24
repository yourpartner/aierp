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
          <el-select
            v-model="employeeIds"
            multiple
            filterable
            reserve-keyword
          placeholder="社員を選択"
            collapse-tags
          collapse-tags-tooltip
          :loading="loadingEmployees"
          class="payroll-filters__employee"
          >
            <el-option v-for="e in employeeOptions" :key="e.value" :label="e.label" :value="e.value" />
          </el-select>
        <el-button type="primary" text size="small" @click="selectAllEmployees">全選択</el-button>
        
        <el-date-picker v-model="month" type="month" value-format="YYYY-MM" placeholder="対象月" class="payroll-filters__month" />
        
        <div class="payroll-filters__switches">
          <el-checkbox v-model="overwrite">上書き</el-checkbox>
          <el-checkbox v-model="debug">デバッグ</el-checkbox>
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
                v-model="manualHoursDialog.totalHours"
                :min="0"
                :max="744"
                :precision="2"
                :step="0.5"
                style="width: 150px"
              />
              <span style="margin-left: 8px">時間</span>
            </el-form-item>
            <el-form-item label="計算結果">
              <span class="manual-hours-result">¥{{ formatAmount(manualHoursDialog.hourlyRate * manualHoursDialog.totalHours) }}</span>
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
      <el-alert
        v-if="runResult?.hasExisting && !overwrite"
        type="warning"
        show-icon
        title="同じ月の給与結果が既に保存されています。上書きする場合は「上書き」をオンにしてください。"
        style="margin-bottom:12px"
      />
      
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
                <span class="entry-amount">¥{{ formatAmount(entry.totalAmount) }}</span>
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
              type="warning"
              show-icon
              :title="warningDescriptions[warn.code] || warn.message || '注意事項'"
                style="margin-bottom:12px"
            />
              
              <el-row :gutter="16">
              <el-col :span="8">
                  <div class="section-card">
                    <div class="section-card__header">給与項目</div>
                    <el-table :data="entry.payrollSheet || []" size="small" border>
                      <el-table-column label="項目" min-width="100">
                      <template #default="{ row }">
                        {{ row.displayName || row.itemCode || row.itemName }}
                      </template>
                    </el-table-column>
                    <el-table-column label="金額" width="100" align="right">
                      <template #default="{ row }">
                        {{ formatAmount(row.amount) }}
                      </template>
                    </el-table-column>
                  </el-table>
                  </div>
              </el-col>
                <el-col :span="16">
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
                    <div class="section-card__header">仕訳ドラフト</div>
                    <el-table :data="entry.accountingDraft || []" size="small" border>
                      <el-table-column prop="accountCode" label="科目コード" width="100" />
                      <el-table-column prop="accountName" label="科目名" min-width="120" />
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
                <div class="section-card__header">デバッグトレース</div>
                <el-table :data="entry.trace" size="small" border max-height="280">
                  <el-table-column prop="step" label="ステップ" width="140" />
                  <el-table-column prop="source" label="ソース" width="80" />
                  <el-table-column prop="item" label="項目" width="100" />
                  <el-table-column prop="amount" label="金額" width="90" />
                  <el-table-column prop="base" label="基数" width="90" />
                  <el-table-column prop="rate" label="料率" width="70" />
                  <el-table-column prop="lawVersion" label="法令Ver" width="130" />
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
import { ElMessage } from 'element-plus'
import { Wallet, CaretRight, FolderChecked } from '@element-plus/icons-vue'
import api from '../api'

const employeeIds = ref<string[]>([])
const employeeOptions = ref<any[]>([])
const allEmployees = ref<any[]>([])
const loadingEmployees = ref(false)
const contractorTypeCodes = ref<Set<string>>(new Set()) // 个人事业主的雇用类型代码/名称集合
const month = ref(new Date().toISOString().slice(0,7))
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const runResult = ref<any | null>(null)
const debug = ref(true)
const overwrite = ref(false)
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

// 手動工時入力ダイアログ
const manualHoursDialog = ref({
  visible: false,
  employeeId: '',
  employeeCode: '',
  employeeName: '',
  hourlyRate: 0,
  totalHours: 0
})

function extractErrorMessage(err: any, fallback: string) {
  if (!err) return fallback
  const resp = err?.response?.data
  if (resp) {
    if (typeof resp === 'string') return resp
    if (resp.message) return resp.message
    if (resp.payload?.message) return resp.payload.message
    if (resp.payload?.error) return resp.payload.error
    if (resp.error) return resp.error
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
              displayAmount: formatAmount(row.amount)
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
        totalHours: 160 // 默认按一个月标准工时
      }
    } else {
      error.value = extractErrorMessage(e, '実行に失敗しました')
    }
  }
  finally{ loading.value=false }
}

// 提交手动输入的工时
async function submitManualHours() {
  const dialog = manualHoursDialog.value
  if (dialog.totalHours <= 0) {
    ElMessage.warning('工時を入力してください')
    return
  }
  
  loading.value = true
  error.value = ''
  
  try {
    // 使用手动工时重新计算该员工的工资
    const payload: any = {
      employeeIds: [dialog.employeeId],
      month: month.value,
      debug: debug.value,
      manualWorkHours: {
        [dialog.employeeId]: {
          totalHours: dialog.totalHours,
          hourlyRate: dialog.hourlyRate
        }
      }
    }
    
    const r = await api.post('/payroll/manual/run', payload)
    
    // 关闭对话框
    manualHoursDialog.value.visible = false
    
    // 处理结果
    const newEntries = Array.isArray(r.data?.entries)
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
            displayAmount: formatAmount(row.amount)
          })),
          accountingDraft: (entry.accountingDraft || []).map((row: any) => ({
            ...row,
            displayAmount: formatAmount(row.amount)
          }))
        }))
      : []
    
    // 如果已有结果，合并新结果；否则直接设置
    if (runResult.value?.entries?.length) {
      // 替换或添加该员工的结果
      const existingIdx = runResult.value.entries.findIndex((e: any) => e.employeeId === dialog.employeeId)
      if (existingIdx >= 0) {
        runResult.value.entries[existingIdx] = newEntries[0]
      } else {
        runResult.value.entries.push(...newEntries)
      }
    } else {
      runResult.value = {
        ...r.data,
        entries: newEntries
      }
    }
    
    // 展开该员工的面板
    if (!activePanels.value.includes(dialog.employeeId)) {
      activePanels.value.push(dialog.employeeId)
    }
    
    ElMessage.success('計算完了')
  } catch (e: any) {
    error.value = extractErrorMessage(e, '計算に失敗しました')
  } finally {
    loading.value = false
  }
}

async function saveResults(){
  if (!runResult.value || !Array.isArray(runResult.value.entries) || runResult.value.entries.length === 0){ return }
  saving.value = true
  error.value = ''
  try{
    const body = {
      month: runResult.value.month || month.value,
      policyId: runResult.value.policyId || null,
      overwrite: overwrite.value,
      runType: 'manual',
      entries: runResult.value.entries.map((entry:any)=>{
        const rawPayrollSheet = entry._rawPayrollSheet || entry.payrollSheet || []
        const rawAccountingDraft = entry._rawAccountingDraft || entry.accountingDraft || []
        const cleanPayrollSheet = rawPayrollSheet.map((row: any) => {
          const { displayName, displayAmount, ...rest } = row
          return rest
        })
        const cleanAccountingDraft = rawAccountingDraft.map((row: any) => {
          const { displayAmount, ...rest } = row
          return rest
        })
        const payload:any = {
          employeeId: entry.employeeId,
          employeeCode: entry.employeeCode,
          employeeName: entry.employeeName,
          departmentCode: entry.departmentCode,
          totalAmount: entry.totalAmount,
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
    await api.post('/payroll/manual/save', body)
    ElMessage.success('保存しました')
    overwrite.value = false
    if (runResult.value){
      runResult.value.hasExisting = true
    }
  }catch(e:any){
    error.value = extractErrorMessage(e, '保存に失敗しました')
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
    const targetMonth = month.value
    const monthStart = `${targetMonth}-01`
    const monthEnd = `${targetMonth}-31`
    // 筛选当月在职的员工，排除个人事業主
    // 个人事业主走请求书流程，不走工资计算
    const contractorPatterns = ['CONTRACTOR', '個人事業主', '個人事业主', '个人事业主']
    const activeEmployees = all.filter(emp => {
      const contracts = emp.payload?.contracts || []
      if (!contracts.length) return false
      // 检查是否有覆盖目标月份的非个人事业主契约
      return contracts.some((c: any) => {
        const from = c.periodFrom || '1900-01-01'
        const to = c.periodTo || '9999-12-31'
        const typeCode = (c.employmentTypeCode || '').toString()
        // 排除个人事业主（可能存储的是代码或名称）
        if (contractorPatterns.some(p => typeCode === p || typeCode.includes(p))) return false
        return from <= monthEnd && to >= monthStart
      })
    })
    allEmployees.value = activeEmployees
    employeeOptions.value = activeEmployees.map(x => ({
      label: `${x.payload?.nameKanji || x.payload?.name || x.name || ''} (${x.employee_code || x.payload?.code || ''})`,
        value: x.id
    }))
    const validIds = new Set(activeEmployees.map(e => e.id))
    employeeIds.value = employeeIds.value.filter(id => validIds.has(id))
  } catch (e) {
    console.error('加载员工失败', e)
  } finally {
    loadingEmployees.value = false
  }
}

function selectAllEmployees() {
  employeeIds.value = employeeOptions.value.map(e => e.value)
}

watch(month, () => {
  loadEmployeesForMonth()
}, { immediate: false })

onMounted(() => {
  loadEmployeesForMonth()
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
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
  margin-bottom: 16px;
  padding: 16px;
  background: #f8f9fc;
  border-radius: 8px;
}

.payroll-filters__employee {
  width: 300px;
}

.payroll-filters__month {
  width: 130px;
}

.payroll-filters__switches {
  display: flex;
  gap: 16px;
  margin-left: auto;
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
  padding: 10px 12px;
  background: #f5f7fa;
  font-size: 13px;
  font-weight: 600;
  color: #606266;
  border-bottom: 1px solid #ebeef5;
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
