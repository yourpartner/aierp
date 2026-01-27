<template>
  <div class="resident-tax-list">
    <el-card class="main-card">
      <template #header>
        <div class="card-header">
          <div class="header-left">
            <el-icon class="header-icon"><Money /></el-icon>
            <span class="header-title">住民税管理</span>
            <el-tag size="small" type="info" class="header-count">{{ summary.registeredCount }}/{{ summary.totalEmployees }}名</el-tag>
          </div>
          <div class="header-right">
            <el-button type="primary" @click="showUploadDialog = true">
              <el-icon><Upload /></el-icon>
              <span>税単読取</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 统计卡片 -->
      <div class="summary-cards">
        <div class="summary-card">
          <div class="summary-label">年度</div>
          <div class="summary-value">{{ fiscalYear }}年度</div>
          <div class="summary-sub">{{ fiscalYear }}年6月 ~ {{ fiscalYear + 1 }}年5月</div>
        </div>
        <div class="summary-card">
          <div class="summary-label">登録済</div>
          <div class="summary-value">{{ summary.registeredCount }}名</div>
          <div class="summary-sub">未登録: {{ summary.unregisteredCount }}名</div>
        </div>
        <div class="summary-card">
          <div class="summary-label">年税額合計</div>
          <div class="summary-value">¥{{ formatNumber(summary.totalAnnualAmount) }}</div>
          <div class="summary-sub">月平均: ¥{{ formatNumber(Math.round(summary.totalAnnualAmount / 12)) }}</div>
        </div>
        <div class="summary-card current-month">
          <div class="summary-label">{{ currentMonthLabel }}</div>
          <div class="summary-value">¥{{ formatNumber(currentMonthTotal) }}</div>
          <div class="summary-sub">控除予定額</div>
        </div>
      </div>

      <!-- 筛选 -->
      <div class="filters">
        <el-select v-model="fiscalYear" placeholder="年度" class="filter-year" @change="load">
          <el-option v-for="y in yearOptions" :key="y" :label="`${y}年度`" :value="y" />
        </el-select>

        <el-select 
          v-model="employeeFilter" 
          filterable 
          clearable 
          remote 
          reserve-keyword
          placeholder="社員で絞込" 
          class="filter-employee"
          :remote-method="searchEmployees"
          :loading="employeeLoading"
          @change="load"
        >
          <el-option v-for="emp in employeeOptions" :key="emp.value" :label="emp.label" :value="emp.value" />
        </el-select>

        <el-button @click="load">
          <el-icon><Refresh /></el-icon>
        </el-button>
      </div>

      <!-- 数据表格 -->
      <el-table :data="rows" border stripe highlight-current-row class="data-table" @row-click="onRowClick">
        <el-table-column type="index" width="50" align="center" />
        
        <el-table-column label="社員" min-width="150">
          <template #default="{ row }">
            <div class="emp-cell">
              <span class="emp-code">{{ row.employeeCode }}</span>
              <span class="emp-name">{{ row.employeeName }}</span>
            </div>
          </template>
        </el-table-column>

        <el-table-column label="市区町村" width="140">
          <template #default="{ row }">
            <span>{{ row.municipalityName || '—' }}</span>
          </template>
        </el-table-column>

        <el-table-column label="年税額" width="110" align="right">
          <template #default="{ row }">
            <span class="amount">¥{{ formatNumber(row.annualAmount) }}</span>
          </template>
        </el-table-column>

        <!-- 月別控除額 -->
        <el-table-column label="6月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(6)">{{ formatNumber(row.juneAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="7月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(7)">{{ formatNumber(row.julyAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="8月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(8)">{{ formatNumber(row.augustAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="9月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(9)">{{ formatNumber(row.septemberAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="10月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(10)">{{ formatNumber(row.octoberAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="11月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(11)">{{ formatNumber(row.novemberAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="12月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(12)">{{ formatNumber(row.decemberAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="1月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(1)">{{ formatNumber(row.januaryAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="2月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(2)">{{ formatNumber(row.februaryAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="3月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(3)">{{ formatNumber(row.marchAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="4月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(4)">{{ formatNumber(row.aprilAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="5月" width="80" align="right">
          <template #default="{ row }">
            <span :class="getMonthClass(5)">{{ formatNumber(row.mayAmount) }}</span>
          </template>
        </el-table-column>

        <el-table-column label="" width="70" align="center" fixed="right">
          <template #default="{ row }">
            <el-button type="danger" text size="small" @click.stop="confirmDelete(row)">
              <el-icon><Delete /></el-icon>
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <!-- 分页 -->
      <div class="pagination">
        <span class="pagination-info">{{ (page - 1) * pageSize + 1 }} - {{ Math.min(page * pageSize, total) }} / {{ total }}件</span>
        <el-pagination 
          layout="prev, pager, next" 
          :page-size="pageSize" 
          :total="total" 
          :current-page="page"
          @current-change="onPageChange" 
        />
      </div>
    </el-card>

    <!-- 图片上传对话框 -->
    <el-dialog v-model="showUploadDialog" title="住民税税単読取" width="700px" destroy-on-close>
      <div class="upload-dialog">
        <el-upload
          ref="uploadRef"
          v-model:file-list="fileList"
          drag
          :auto-upload="false"
          :limit="5"
          accept="image/*"
          :on-change="onFileChange"
          list-type="picture"
        >
          <el-icon class="el-icon--upload"><UploadFilled /></el-icon>
          <div class="el-upload__text">税単画像をドラッグ または <em>クリックして選択</em></div>
          <template #tip>
            <div class="el-upload__tip">特別徴収税額通知書の画像をアップロードしてください（複数可）</div>
          </template>
        </el-upload>

        <div v-if="parseResult" class="parse-result">
          <el-divider>読取結果</el-divider>
          
          <el-alert v-if="parseResult.warnings?.length" type="warning" :closable="false" class="result-warning">
            <template #title>
              <div v-for="(w, i) in parseResult.warnings" :key="i">{{ w }}</div>
            </template>
          </el-alert>

          <div v-for="(entry, idx) in parseResult.entries" :key="idx" class="result-entry">
            <div class="entry-header">
              <span class="entry-title">{{ entry.employeeName }}</span>
              <el-tag v-if="entry.employeeId" type="success" size="small">マッチ済</el-tag>
              <el-tag v-else type="warning" size="small">未マッチ</el-tag>
              <el-tag type="info" size="small">{{ entry.fiscalYear }}年度</el-tag>
            </div>

            <div v-if="!entry.employeeId" class="entry-match">
              <span>社員を選択:</span>
              <el-select v-model="entry.selectedEmployeeId" filterable placeholder="社員を選択" size="small">
                <el-option v-for="emp in allEmployees" :key="emp.id" :label="`${emp.code} ${emp.nameKanji}`" :value="emp.id" />
              </el-select>
            </div>

            <div class="entry-amounts">
              <div class="amount-item"><span>年税額:</span><span class="val">¥{{ formatNumber(entry.annualAmount) }}</span></div>
              <div class="amount-item"><span>6月:</span><span class="val">{{ formatNumber(entry.juneAmount) }}</span></div>
              <div class="amount-item"><span>7月~:</span><span class="val">{{ formatNumber(entry.julyAmount) }}</span></div>
            </div>

            <div class="entry-status">
              <el-tag v-if="entry.saveStatus === 'saved'" type="success">保存済み</el-tag>
              <el-tag v-else-if="entry.saveStatus === 'duplicate'" type="warning">重複（スキップ）</el-tag>
              <el-tag v-else-if="entry.saveStatus === 'error'" type="danger">エラー</el-tag>
            </div>
          </div>
        </div>
      </div>

      <template #footer>
        <el-button @click="showUploadDialog = false">キャンセル</el-button>
        <el-button v-if="!parseResult" type="primary" :loading="parsing" @click="parseImages">
          <el-icon><Search /></el-icon>
          読取開始
        </el-button>
        <el-button v-else-if="!parseResult.autoSaved" type="primary" :loading="saving" @click="saveParseResult">
          <el-icon><Check /></el-icon>
          保存
        </el-button>
      </template>
    </el-dialog>

    <!-- 编辑对话框 -->
    <el-dialog v-model="showEditDialog" title="住民税編集" width="600px" destroy-on-close>
      <el-form v-if="editForm" label-width="100px">
        <el-form-item label="社員">
          <span>{{ editForm.employeeCode }} {{ editForm.employeeName }}</span>
        </el-form-item>
        <el-form-item label="年度">
          <span>{{ editForm.fiscalYear }}年度</span>
        </el-form-item>
        <el-form-item label="市区町村">
          <el-input v-model="editForm.municipalityName" placeholder="市区町村名" />
        </el-form-item>
        <el-form-item label="年税額">
          <el-input-number v-model="editForm.annualAmount" :min="0" :step="1000" />
        </el-form-item>
        <el-divider>月別控除額</el-divider>
        <div class="month-inputs">
          <el-form-item label="6月"><el-input-number v-model="editForm.juneAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="7月"><el-input-number v-model="editForm.julyAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="8月"><el-input-number v-model="editForm.augustAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="9月"><el-input-number v-model="editForm.septemberAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="10月"><el-input-number v-model="editForm.octoberAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="11月"><el-input-number v-model="editForm.novemberAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="12月"><el-input-number v-model="editForm.decemberAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="1月"><el-input-number v-model="editForm.januaryAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="2月"><el-input-number v-model="editForm.februaryAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="3月"><el-input-number v-model="editForm.marchAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="4月"><el-input-number v-model="editForm.aprilAmount" :min="0" size="small" /></el-form-item>
          <el-form-item label="5月"><el-input-number v-model="editForm.mayAmount" :min="0" size="small" /></el-form-item>
        </div>
      </el-form>

      <template #footer>
        <el-button @click="showEditDialog = false">キャンセル</el-button>
        <el-button type="primary" :loading="saving" @click="saveEdit">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage, ElMessageBox, type UploadFile } from 'element-plus'
import { Money, Upload, Refresh, Delete, UploadFilled, Search, Check } from '@element-plus/icons-vue'
import api from '../api'

interface ResidentTaxRecord {
  id: string
  employeeId: string
  employeeCode: string
  employeeName: string
  fiscalYear: number
  municipalityCode: string | null
  municipalityName: string | null
  annualAmount: number
  juneAmount: number
  julyAmount: number
  augustAmount: number
  septemberAmount: number
  octoberAmount: number
  novemberAmount: number
  decemberAmount: number
  januaryAmount: number
  februaryAmount: number
  marchAmount: number
  aprilAmount: number
  mayAmount: number
  status: string
}

interface ParseResultEntry {
  employeeId: string | null
  employeeCode: string | null
  employeeName: string
  fiscalYear: number
  municipalityCode: string | null
  municipalityName: string | null
  annualAmount: number
  juneAmount: number
  julyAmount: number
  augustAmount: number
  septemberAmount: number
  octoberAmount: number
  novemberAmount: number
  decemberAmount: number
  januaryAmount: number
  februaryAmount: number
  marchAmount: number
  aprilAmount: number
  mayAmount: number
  confidence: number
  matchReason: string
  selectedEmployeeId?: string
  saveStatus?: 'saved' | 'duplicate' | 'error'
}

const rows = ref<ResidentTaxRecord[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(50)

const now = new Date()
const currentMonth = now.getMonth() + 1
const fiscalYear = ref(currentMonth >= 6 ? now.getFullYear() : now.getFullYear() - 1)

const yearOptions = computed(() => {
  const years = []
  for (let y = now.getFullYear() + 1; y >= now.getFullYear() - 5; y--) years.push(y)
  return years
})

const summary = ref({
  fiscalYear: fiscalYear.value,
  totalEmployees: 0,
  registeredCount: 0,
  unregisteredCount: 0,
  totalAnnualAmount: 0,
  byMonth: {} as Record<string, number>
})

const employeeFilter = ref<string>('')
const employeeOptions = ref<{ label: string; value: string }[]>([])
const employeeLoading = ref(false)
const allEmployees = ref<{ id: string; code: string; nameKanji: string; nameKana: string }[]>([])

const showUploadDialog = ref(false)
const showEditDialog = ref(false)
const fileList = ref<UploadFile[]>([])
const parsing = ref(false)
const saving = ref(false)
const parseResult = ref<{
  success: boolean
  entries: ParseResultEntry[]
  warnings?: string[]
  autoSaved?: boolean
  savedCount?: number
  duplicateCount?: number
  errorCount?: number
} | null>(null)
const editForm = ref<ResidentTaxRecord | null>(null)

const currentMonthLabel = computed(() => {
  const monthNames = ['1月', '2月', '3月', '4月', '5月', '6月', '7月', '8月', '9月', '10月', '11月', '12月']
  return monthNames[currentMonth - 1]
})

const currentMonthTotal = computed(() => {
  const monthKey = ['january', 'february', 'march', 'april', 'may', 'june', 'july', 'august', 'september', 'october', 'november', 'december'][currentMonth - 1]
  return summary.value.byMonth[monthKey] || 0
})

function formatNumber(n: number): string {
  return n?.toLocaleString() || '0'
}

function getMonthClass(month: number): string {
  if (month === currentMonth) return 'current-month-value'
  // 判断是否已过（住民税年度从6月开始）
  const fyStart = fiscalYear.value
  const monthInFy = month >= 6 ? month - 6 : month + 6
  const currentInFy = currentMonth >= 6 ? currentMonth - 6 : currentMonth + 6
  const currentFy = currentMonth >= 6 ? now.getFullYear() : now.getFullYear() - 1
  if (currentFy > fyStart || (currentFy === fyStart && currentInFy > monthInFy)) {
    return 'past-month-value'
  }
  return ''
}

async function load() {
  try {
    const params: any = { fiscalYear: fiscalYear.value, page: page.value, pageSize: pageSize.value }
    if (employeeFilter.value) params.employeeId = employeeFilter.value

    const [listRes, summaryRes] = await Promise.all([
      api.get('/resident-tax', { params }),
      api.get('/resident-tax/summary', { params: { fiscalYear: fiscalYear.value } })
    ])

    rows.value = listRes.data?.data || []
    total.value = listRes.data?.total || 0
    summary.value = summaryRes.data || summary.value
  } catch (e) {
    console.error('Failed to load resident tax data', e)
  }
}

async function loadAllEmployees() {
  try {
    const res = await api.post('/objects/employee/search', {
      page: 1,
      pageSize: 500,
      where: [],
      orderBy: [{ field: 'employee_code', dir: 'ASC' }]
    })
    allEmployees.value = (res.data?.data || []).map((e: any) => ({
      id: e.id,
      code: e.employee_code || e.payload?.code || '',
      nameKanji: e.payload?.nameKanji || e.payload?.name || '',
      nameKana: e.payload?.nameKana || ''
    }))
  } catch { /* ignore */ }
}

async function searchEmployees(query: string) {
  if (!query) {
    employeeOptions.value = []
    return
  }
  employeeLoading.value = true
  try {
    const res = await api.post('/objects/employee/search', {
      page: 1,
      pageSize: 20,
      where: [{
        anyOf: [
          { json: 'nameKanji', op: 'contains', value: query },
          { json: 'nameKana', op: 'contains', value: query },
          { json: 'code', op: 'contains', value: query }
        ]
      }]
    })
    employeeOptions.value = (res.data?.data || []).map((e: any) => ({
      label: `${e.employee_code || ''} ${e.payload?.nameKanji || ''}`,
      value: e.id
    }))
  } finally {
    employeeLoading.value = false
  }
}

function onPageChange(p: number) {
  page.value = p
  load()
}

function onFileChange(file: UploadFile) {
  // 重置解析结果
  parseResult.value = null
}

async function parseImages() {
  if (fileList.value.length === 0) {
    ElMessage.warning('画像を選択してください')
    return
  }

  parsing.value = true
  try {
    const formData = new FormData()
    for (const file of fileList.value) {
      if (file.raw) formData.append('files', file.raw)
    }
    formData.append('autoSave', 'true')

    const res = await api.post('/resident-tax/parse-image', formData, {
      headers: { 'Content-Type': 'multipart/form-data' }
    })

    if (res.data?.success && res.data.entries?.length > 0) {
      parseResult.value = res.data
      ElMessage.success(`${res.data.entries.length}件のデータを読み取りました`)
    } else {
      ElMessage.warning('読取結果がありません')
      parseResult.value = { success: false, entries: [], warnings: ['読取結果がありません'] }
    }
  } catch (e: any) {
    ElMessage.error('読取に失敗しました: ' + (e.message || ''))
  } finally {
    parsing.value = false
  }
}

async function saveParseResult() {
  if (!parseResult.value?.entries?.length) return

  saving.value = true
  let savedCount = 0
  let duplicateCount = 0
  let errorCount = 0

  try {
    for (const entry of parseResult.value.entries) {
      const employeeId = entry.employeeId || entry.selectedEmployeeId
      if (!employeeId) {
        entry.saveStatus = 'error'
        errorCount++
        continue
      }

      try {
        await api.post('/resident-tax', {
          employeeId,
          fiscalYear: entry.fiscalYear,
          municipalityCode: entry.municipalityCode,
          municipalityName: entry.municipalityName,
          annualAmount: entry.annualAmount,
          juneAmount: entry.juneAmount,
          julyAmount: entry.julyAmount,
          augustAmount: entry.augustAmount,
          septemberAmount: entry.septemberAmount,
          octoberAmount: entry.octoberAmount,
          novemberAmount: entry.novemberAmount,
          decemberAmount: entry.decemberAmount,
          januaryAmount: entry.januaryAmount,
          februaryAmount: entry.februaryAmount,
          marchAmount: entry.marchAmount,
          aprilAmount: entry.aprilAmount,
          mayAmount: entry.mayAmount
        })
        entry.saveStatus = 'saved'
        savedCount++
      } catch (e: any) {
        if (e.response?.status === 409) {
          entry.saveStatus = 'duplicate'
          duplicateCount++
        } else {
          entry.saveStatus = 'error'
          errorCount++
        }
      }
    }

    if (savedCount > 0) {
      ElMessage.success(`${savedCount}件を保存しました` + (duplicateCount > 0 ? `（${duplicateCount}件は重複のためスキップ）` : ''))
      await load()
    } else if (duplicateCount > 0) {
      ElMessage.warning(`全て重複データでした（${duplicateCount}件）`)
    } else {
      ElMessage.error('保存に失敗しました')
    }
  } finally {
    saving.value = false
  }
}

function onRowClick(row: ResidentTaxRecord) {
  editForm.value = { ...row }
  showEditDialog.value = true
}

async function saveEdit() {
  if (!editForm.value) return
  saving.value = true
  try {
    await api.put(`/resident-tax/${editForm.value.id}`, {
      municipalityCode: editForm.value.municipalityCode,
      municipalityName: editForm.value.municipalityName,
      annualAmount: editForm.value.annualAmount,
      juneAmount: editForm.value.juneAmount,
      julyAmount: editForm.value.julyAmount,
      augustAmount: editForm.value.augustAmount,
      septemberAmount: editForm.value.septemberAmount,
      octoberAmount: editForm.value.octoberAmount,
      novemberAmount: editForm.value.novemberAmount,
      decemberAmount: editForm.value.decemberAmount,
      januaryAmount: editForm.value.januaryAmount,
      februaryAmount: editForm.value.februaryAmount,
      marchAmount: editForm.value.marchAmount,
      aprilAmount: editForm.value.aprilAmount,
      mayAmount: editForm.value.mayAmount
    })
    ElMessage.success('保存しました')
    showEditDialog.value = false
    await load()
  } catch (e: any) {
    ElMessage.error('保存に失敗しました: ' + (e.message || ''))
  } finally {
    saving.value = false
  }
}

async function confirmDelete(row: ResidentTaxRecord) {
  try {
    await ElMessageBox.confirm(
      `${row.employeeName} の ${row.fiscalYear}年度住民税データを削除しますか？`,
      '確認',
      { type: 'warning' }
    )
    await api.delete(`/resident-tax/${row.id}`)
    ElMessage.success('削除しました')
    await load()
  } catch { /* cancelled */ }
}

onMounted(async () => {
  await Promise.all([load(), loadAllEmployees()])
})
</script>

<style scoped>
.resident-tax-list {
  padding: 16px;
}

.main-card {
  border-radius: 12px;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.header-icon {
  font-size: 22px;
  color: #667eea;
}

.header-title {
  font-size: 18px;
  font-weight: 600;
}

.header-count {
  font-weight: 500;
}

/* Summary cards */
.summary-cards {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 16px;
  margin-bottom: 20px;
}

.summary-card {
  padding: 16px;
  background: linear-gradient(135deg, #f5f7fa 0%, #e4e7ed 100%);
  border-radius: 10px;
  text-align: center;
}

.summary-card.current-month {
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  color: white;
}

.summary-label {
  font-size: 12px;
  color: #909399;
  margin-bottom: 4px;
}

.summary-card.current-month .summary-label {
  color: rgba(255, 255, 255, 0.8);
}

.summary-value {
  font-size: 24px;
  font-weight: 600;
  color: #303133;
}

.summary-card.current-month .summary-value {
  color: white;
}

.summary-sub {
  font-size: 11px;
  color: #909399;
  margin-top: 4px;
}

.summary-card.current-month .summary-sub {
  color: rgba(255, 255, 255, 0.7);
}

/* Filters */
.filters {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
  padding: 12px;
  background: #f8f9fc;
  border-radius: 8px;
}

.filter-year {
  width: 130px;
}

.filter-employee {
  width: 200px;
}

/* Table */
.data-table {
  border-radius: 8px;
  cursor: pointer;
}

.emp-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.emp-code {
  font-size: 12px;
  color: #909399;
}

.emp-name {
  font-weight: 500;
}

.amount {
  font-weight: 500;
  color: #303133;
}

.current-month-value {
  font-weight: 600;
  color: #667eea;
  background: rgba(102, 126, 234, 0.1);
  padding: 2px 6px;
  border-radius: 4px;
}

.past-month-value {
  color: #c0c4cc;
}

/* Pagination */
.pagination {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.pagination-info {
  font-size: 13px;
  color: #909399;
}

/* Upload dialog */
.upload-dialog {
  min-height: 200px;
}

.parse-result {
  margin-top: 20px;
}

.result-warning {
  margin-bottom: 16px;
}

.result-entry {
  padding: 16px;
  background: #f5f7fa;
  border-radius: 8px;
  margin-bottom: 12px;
}

.entry-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
}

.entry-title {
  font-weight: 600;
  font-size: 16px;
}

.entry-match {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
  padding: 8px;
  background: #fff3cd;
  border-radius: 4px;
}

.entry-amounts {
  display: flex;
  gap: 20px;
  flex-wrap: wrap;
}

.amount-item {
  display: flex;
  gap: 4px;
}

.amount-item .val {
  font-weight: 500;
}

.entry-status {
  margin-top: 8px;
}

/* Edit dialog */
.month-inputs {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 8px;
}

.month-inputs .el-form-item {
  margin-bottom: 8px;
}

@media (max-width: 1400px) {
  .summary-cards {
    grid-template-columns: repeat(2, 1fr);
  }
}

@media (max-width: 768px) {
  .summary-cards {
    grid-template-columns: 1fr;
  }
  .filters {
    flex-direction: column;
  }
  .filter-year,
  .filter-employee {
    width: 100%;
  }
  .month-inputs {
    grid-template-columns: repeat(2, 1fr);
  }
}
</style>
