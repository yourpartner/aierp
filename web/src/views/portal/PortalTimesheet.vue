<template>
  <div class="portal-timesheet">
    <div class="page-header">
      <div class="header-left">
        <router-link to="/portal/dashboard" class="back-link">
          <el-icon><ArrowLeft /></el-icon>
        </router-link>
        <el-icon class="header-icon"><Calendar /></el-icon>
        <h1>勤怠入力</h1>
      </div>
      <div class="header-right">
        <el-select v-model="selectedYear" style="width: 100px" @change="loadTimesheets">
          <el-option v-for="y in years" :key="y" :label="`${y}年`" :value="y" />
        </el-select>
      </div>
    </div>

    <!-- タブ切り替え -->
    <el-tabs v-model="activeTab" class="main-tabs">
      <!-- 月別一覧タブ（既存） -->
      <el-tab-pane label="月別一覧" name="monthly">
        <el-card v-loading="loading">
          <el-table :data="timesheets">
            <el-table-column label="年月" prop="yearMonth" width="120">
              <template #default="{ row }">
                {{ formatYearMonth(row.yearMonth) }}
              </template>
            </el-table-column>
            <el-table-column label="顧客" prop="clientName" min-width="150" />
            <el-table-column label="所定時間" prop="scheduledHours" width="100" align="right">
              <template #default="{ row }">{{ row.scheduledHours }}h</template>
            </el-table-column>
            <el-table-column label="実労働" prop="actualHours" width="100" align="right">
              <template #default="{ row }">
                <span class="actual-hours">{{ row.actualHours }}h</span>
              </template>
            </el-table-column>
            <el-table-column label="残業" prop="overtimeHours" width="80" align="right">
              <template #default="{ row }">
                <span v-if="row.overtimeHours > 0" class="overtime">+{{ row.overtimeHours }}h</span>
                <span v-else>-</span>
              </template>
            </el-table-column>
            <el-table-column label="ステータス" width="120" align="center">
              <template #default="{ row }">
                <el-tag :type="getStatusType(row.status)" size="small">
                  {{ getStatusLabel(row.status) }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="操作" width="200" align="center">
              <template #default="{ row }">
                <el-button 
                  v-if="row.status === 'open' || row.status === 'not_submitted'" 
                  size="small" type="primary" @click="editTimesheet(row)">入力</el-button>
                <el-button 
                  v-if="row.status === 'open' || row.status === 'not_submitted'"
                  size="small" @click="openDailyView(row.yearMonth)">日別</el-button>
                <el-button 
                  v-if="row.status === 'open'" 
                  size="small" type="success" @click="submitTimesheet(row)">提出</el-button>
                <el-button 
                  v-if="row.status === 'submitted' || row.status === 'confirmed'" 
                  size="small" link @click="viewTimesheet(row)">詳細</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-tab-pane>

      <!-- 日別入力タブ（新規） -->
      <el-tab-pane label="日別入力" name="daily">
        <el-card class="daily-view-card">
          <!-- 月選択 + 週切替 -->
          <div class="daily-toolbar">
            <div class="month-nav">
              <el-button :icon="ArrowLeft" circle size="small" @click="prevMonth" />
              <span class="current-month">{{ formatYearMonth(dailyMonth) }}</span>
              <el-button :icon="ArrowRight" circle size="small" @click="nextMonth" />
            </div>
            <div class="daily-actions">
              <el-button size="small" @click="copyFromLastMonth" :loading="copyingLastMonth">
                <el-icon><DocumentCopy /></el-icon>
                先月からコピー
              </el-button>
              <el-button type="primary" plain size="small" @click="showUploadDialog = true">
                <el-icon><Upload /></el-icon>
                ファイルアップロード
              </el-button>
              <el-button type="success" size="small" @click="saveDailyEntries" :loading="savingDaily" 
                :disabled="!hasUnsavedChanges">
                <el-icon><Check /></el-icon>
                保存
              </el-button>
            </div>
          </div>

          <!-- 日別データテーブル -->
          <div class="daily-table-wrapper" v-loading="loadingDaily">
            <table class="daily-table">
              <thead>
                <tr>
                  <th class="col-date">日付</th>
                  <th class="col-day">曜日</th>
                  <th class="col-time">出勤</th>
                  <th class="col-time">退勤</th>
                  <th class="col-break">休憩</th>
                  <th class="col-hours">通常</th>
                  <th class="col-hours">残業</th>
                  <th class="col-note">備考</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="day in dailyEntries" :key="day.date"
                    :class="{ 'weekend': day.isWeekend, 'holiday': day.isHoliday, 'has-data': day.startTime }">
                  <td class="col-date">{{ day.dateLabel }}</td>
                  <td class="col-day" :class="{ 'weekend-text': day.isWeekend }">{{ day.dayLabel }}</td>
                  <td class="col-time">
                    <el-time-picker v-model="day.startTime" format="HH:mm" :clearable="true"
                      placeholder="--:--" size="small" style="width: 90px"
                      @change="onTimeChanged(day)" :disabled="day.isWeekend && !day.isHoliday" />
                  </td>
                  <td class="col-time">
                    <el-time-picker v-model="day.endTime" format="HH:mm" :clearable="true"
                      placeholder="--:--" size="small" style="width: 90px"
                      @change="onTimeChanged(day)" :disabled="day.isWeekend && !day.isHoliday" />
                  </td>
                  <td class="col-break">
                    <el-input-number v-model="day.breakMinutes" :min="0" :max="180" :step="15"
                      size="small" style="width: 70px" controls-position="right"
                      @change="onTimeChanged(day)" />
                  </td>
                  <td class="col-hours">
                    <span class="hours-value">{{ day.regularHours?.toFixed(1) || '0.0' }}</span>
                  </td>
                  <td class="col-hours">
                    <span :class="{ 'overtime': day.overtimeHours > 0 }">
                      {{ day.overtimeHours > 0 ? `+${day.overtimeHours.toFixed(1)}` : '-' }}
                    </span>
                  </td>
                  <td class="col-note">
                    <el-input v-model="day.notes" size="small" placeholder="" style="width: 100%" />
                  </td>
                </tr>
              </tbody>
              <tfoot>
                <tr class="summary-row">
                  <td colspan="5" class="summary-label">合計</td>
                  <td class="col-hours"><strong>{{ totalRegular.toFixed(1) }}h</strong></td>
                  <td class="col-hours"><strong class="overtime">{{ totalOvertime > 0 ? `+${totalOvertime.toFixed(1)}h` : '-' }}</strong></td>
                  <td></td>
                </tr>
              </tfoot>
            </table>
          </div>

          <!-- AI解析結果バナー -->
          <div v-if="aiParseResult" class="ai-parse-banner">
            <div class="ai-banner-header">
              <el-icon><MagicStick /></el-icon>
              <span>AI解析結果 (信頼度: {{ (aiParseResult.confidence * 100).toFixed(0) }}%)</span>
              <span class="ai-summary">{{ aiParseResult.summary }}</span>
            </div>
            <div v-if="aiParseResult.warnings?.length" class="ai-warnings">
              <el-icon><Warning /></el-icon>
              <span v-for="(w, i) in aiParseResult.warnings" :key="i">{{ w }}</span>
            </div>
            <div class="ai-banner-actions">
              <el-button type="primary" size="small" @click="applyAiResult" :loading="applyingAi">
                データを反映
              </el-button>
              <el-button size="small" @click="aiParseResult = null">閉じる</el-button>
            </div>
          </div>
        </el-card>
      </el-tab-pane>
    </el-tabs>

    <!-- 入力ダイアログ（既存） -->
    <el-dialog v-model="dialogVisible" title="勤怠入力" width="500px">
      <el-form :model="form" label-position="top">
        <el-form-item label="年月">
          <el-input :value="formatYearMonth(form.yearMonth)" disabled />
        </el-form-item>
        <el-form-item label="契約" v-if="activeContracts.length > 1">
          <el-select v-model="form.contractId" style="width: 100%">
            <el-option v-for="c in activeContracts" :key="c.id" 
              :label="`${c.contractNo} - ${c.clientName}`" :value="c.id" />
          </el-select>
        </el-form-item>
        <el-row :gutter="16">
          <el-col :span="8">
            <el-form-item label="所定時間">
              <el-input-number v-model="form.scheduledHours" :min="0" :max="250" :step="0.5" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="実労働時間">
              <el-input-number v-model="form.actualHours" :min="0" :max="300" :step="0.5" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="残業時間">
              <el-input-number v-model="form.overtimeHours" :min="0" :max="100" :step="0.5" style="width: 100%" />
            </el-form-item>
          </el-col>
        </el-row>
        <div class="form-hint">
          <el-icon><InfoFilled /></el-icon>
          週次の実績を月末にまとめて入力してください
        </div>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="saveTimesheet" :loading="saving">保存</el-button>
      </template>
    </el-dialog>

    <!-- ファイルアップロードダイアログ -->
    <el-dialog v-model="showUploadDialog" title="勤怠ファイルアップロード" width="500px">
      <div class="upload-area">
        <el-upload
          ref="uploadRef"
          :auto-upload="false"
          :limit="1"
          :on-change="onFileChange"
          accept=".xlsx,.xls,.csv,.jpg,.jpeg,.png,.pdf"
          drag
        >
          <el-icon class="upload-icon"><Upload /></el-icon>
          <div class="upload-text">
            ファイルをドラッグするか、クリックして選択
          </div>
          <div class="upload-hint">
            対応形式: Excel (.xlsx/.xls), CSV, 画像 (jpg/png), PDF
          </div>
        </el-upload>
      </div>
      <template #footer>
        <el-button @click="showUploadDialog = false">キャンセル</el-button>
        <el-button type="primary" @click="uploadAndParse" :loading="uploading" :disabled="!selectedFile">
          <el-icon><MagicStick /></el-icon>
          AI解析
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowLeft, ArrowRight, Calendar, InfoFilled, Upload, Check, MagicStick, Warning, DocumentCopy } from '@element-plus/icons-vue'
import api from '../../api'

// ==================== Types ====================

interface Timesheet {
  id?: string
  yearMonth: string
  contractId?: string
  clientName?: string
  scheduledHours: number
  actualHours: number
  overtimeHours: number
  status: string
}

interface DailyEntry {
  date: string          // YYYY-MM-DD
  dateLabel: string     // MM/DD
  dayLabel: string      // 月, 火, ...
  isWeekend: boolean
  isHoliday: boolean
  startTime: Date | null
  endTime: Date | null
  breakMinutes: number
  regularHours: number
  overtimeHours: number
  notes: string
  id?: string
  dirty?: boolean
}

interface AiParseResult {
  uploadId: string
  success: boolean
  entries: any[]
  confidence: number
  warnings: string[]
  summary: string
}

// ==================== State ====================

const activeTab = ref('monthly')
const loading = ref(false)
const saving = ref(false)
const dialogVisible = ref(false)

const currentYear = new Date().getFullYear()
const selectedYear = ref(currentYear)
const years = Array.from({ length: 5 }, (_, i) => currentYear - i)

const timesheets = ref<Timesheet[]>([])
const activeContracts = ref<any[]>([])

const form = reactive({
  id: '', yearMonth: '', contractId: '',
  scheduledHours: 160, actualHours: 0, overtimeHours: 0
})

// Daily view state
const dailyMonth = ref(new Date().toISOString().substring(0, 7))
const dailyEntries = ref<DailyEntry[]>([])
const loadingDaily = ref(false)
const savingDaily = ref(false)

// Upload state
const showUploadDialog = ref(false)
const selectedFile = ref<File | null>(null)
const uploading = ref(false)
const uploadRef = ref()

// Copy from last month
const copyingLastMonth = ref(false)

// AI parse
const aiParseResult = ref<AiParseResult | null>(null)
const applyingAi = ref(false)

// ==================== Computed ====================

const totalRegular = computed(() => dailyEntries.value.reduce((sum, d) => sum + (d.regularHours || 0), 0))
const totalOvertime = computed(() => dailyEntries.value.reduce((sum, d) => sum + (d.overtimeHours || 0), 0))
const hasUnsavedChanges = computed(() => dailyEntries.value.some(d => d.dirty))

// ==================== Monthly tab (existing logic) ====================

const loadTimesheets = async () => {
  loading.value = true
  try {
    const res = await api.get('/portal/timesheets', { params: { year: selectedYear.value } })
    timesheets.value = res.data.data || []
    
    const currentMonth = new Date().toISOString().substring(0, 7)
    if (!timesheets.value.find(t => t.yearMonth === currentMonth)) {
      timesheets.value.unshift({
        yearMonth: currentMonth, scheduledHours: 0, actualHours: 0, overtimeHours: 0, status: 'not_submitted'
      })
    }
  } catch (e) {
    console.error('Load timesheets error:', e)
  } finally {
    loading.value = false
  }
}

const loadContracts = async () => {
  try {
    const res = await api.get('/portal/dashboard')
    activeContracts.value = res.data.activeContracts || []
  } catch { /* ignore */ }
}

const editTimesheet = (row: Timesheet) => {
  Object.assign(form, {
    id: row.id || '', yearMonth: row.yearMonth,
    contractId: row.contractId || (activeContracts.value[0]?.id || ''),
    scheduledHours: row.scheduledHours || 160,
    actualHours: row.actualHours || 0,
    overtimeHours: row.overtimeHours || 0
  })
  dialogVisible.value = true
}

const viewTimesheet = (row: Timesheet) => {
  ElMessageBox.alert(
    `所定: ${row.scheduledHours}h / 実労働: ${row.actualHours}h / 残業: ${row.overtimeHours}h`,
    `${formatYearMonth(row.yearMonth)} 勤怠詳細`
  )
}

const saveTimesheet = async () => {
  saving.value = true
  try {
    await api.post('/portal/timesheets', form)
    ElMessage.success('保存しました')
    dialogVisible.value = false
    await loadTimesheets()
  } catch {
    ElMessage.error('保存に失敗しました')
  } finally {
    saving.value = false
  }
}

const submitTimesheet = async (row: Timesheet) => {
  if (!row.id) {
    ElMessage.warning('まず勤怠を入力してください')
    return
  }
  try {
    await ElMessageBox.confirm('勤怠を提出すると修正できなくなります。提出しますか？', '確認')
    await api.post(`/portal/timesheets/${row.id}/submit`)
    ElMessage.success('提出しました')
    await loadTimesheets()
  } catch (e: any) {
    if (e !== 'cancel') ElMessage.error('提出に失敗しました')
  }
}

// ==================== Daily view ====================

const openDailyView = (ym: string) => {
  dailyMonth.value = ym
  activeTab.value = 'daily'
  loadDailyEntries()
}

const prevMonth = () => {
  const d = new Date(dailyMonth.value + '-01')
  d.setMonth(d.getMonth() - 1)
  dailyMonth.value = d.toISOString().substring(0, 7)
  loadDailyEntries()
}

const nextMonth = () => {
  const d = new Date(dailyMonth.value + '-01')
  d.setMonth(d.getMonth() + 1)
  dailyMonth.value = d.toISOString().substring(0, 7)
  loadDailyEntries()
}

const generateEmptyDays = (ym: string): DailyEntry[] => {
  const [year, month] = ym.split('-').map(Number)
  const daysInMonth = new Date(year, month, 0).getDate()
  const dayNames = ['日', '月', '火', '水', '木', '金', '土']
  const entries: DailyEntry[] = []

  for (let d = 1; d <= daysInMonth; d++) {
    const date = new Date(year, month - 1, d)
    const dayOfWeek = date.getDay()
    entries.push({
      date: `${year}-${String(month).padStart(2, '0')}-${String(d).padStart(2, '0')}`,
      dateLabel: `${month}/${d}`,
      dayLabel: dayNames[dayOfWeek],
      isWeekend: dayOfWeek === 0 || dayOfWeek === 6,
      isHoliday: false,
      startTime: null,
      endTime: null,
      breakMinutes: 60,
      regularHours: 0,
      overtimeHours: 0,
      notes: '',
      dirty: false
    })
  }
  return entries
}

const loadDailyEntries = async () => {
  loadingDaily.value = true
  try {
    const res = await api.get('/portal/timesheet-daily', { params: { month: dailyMonth.value } })
    const serverData: any[] = res.data.data || []
    
    // 生成全月空行
    const entries = generateEmptyDays(dailyMonth.value)
    
    // 合并服务器数据
    for (const sd of serverData) {
      const entry = entries.find(e => e.date === sd.entryDate)
      if (entry) {
        entry.id = sd.id
        entry.startTime = sd.startTime ? parseTimeToDate(sd.startTime) : null
        entry.endTime = sd.endTime ? parseTimeToDate(sd.endTime) : null
        entry.breakMinutes = sd.breakMinutes || 60
        entry.regularHours = sd.regularHours || 0
        entry.overtimeHours = sd.overtimeHours || 0
        entry.isHoliday = sd.isHoliday || false
        entry.notes = sd.notes || ''
      }
    }
    
    dailyEntries.value = entries
  } catch (e) {
    console.error('Load daily entries error:', e)
    dailyEntries.value = generateEmptyDays(dailyMonth.value)
  } finally {
    loadingDaily.value = false
  }
}

const parseTimeToDate = (timeStr: string): Date => {
  const [h, m] = timeStr.split(':').map(Number)
  const d = new Date()
  d.setHours(h, m, 0, 0)
  return d
}

const formatTimeFromDate = (d: Date | null): string | null => {
  if (!d) return null
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

const onTimeChanged = (day: DailyEntry) => {
  day.dirty = true
  if (day.startTime && day.endTime) {
    const start = day.startTime instanceof Date ? day.startTime : new Date(day.startTime)
    const end = day.endTime instanceof Date ? day.endTime : new Date(day.endTime)
    const workMinutes = (end.getTime() - start.getTime()) / 60000 - day.breakMinutes
    if (workMinutes > 0) {
      day.regularHours = Math.min(workMinutes / 60, 8)
      day.overtimeHours = Math.max(workMinutes / 60 - 8, 0)
    } else {
      day.regularHours = 0
      day.overtimeHours = 0
    }
  }
}

const saveDailyEntries = async () => {
  const dirtyEntries = dailyEntries.value.filter(d => d.dirty && d.startTime)
  if (dirtyEntries.length === 0) {
    ElMessage.info('変更がありません')
    return
  }

  savingDaily.value = true
  try {
    const payload = dirtyEntries.map(d => ({
      entryDate: d.date,
      startTime: formatTimeFromDate(d.startTime),
      endTime: formatTimeFromDate(d.endTime),
      breakMinutes: d.breakMinutes,
      regularHours: d.regularHours,
      overtimeHours: d.overtimeHours,
      isHoliday: d.isHoliday,
      notes: d.notes || undefined
    }))

    await api.post('/portal/timesheet-daily', payload)
    ElMessage.success(`${dirtyEntries.length}件の勤怠を保存しました`)
    
    // Mark all as clean
    for (const d of dirtyEntries) d.dirty = false
    
    // Reload
    await loadDailyEntries()
  } catch (e: any) {
    ElMessage.error('保存に失敗しました')
    console.error(e)
  } finally {
    savingDaily.value = false
  }
}

// ==================== Copy from last month ====================

const copyFromLastMonth = async () => {
  try {
    await ElMessageBox.confirm(
      '先月のデータを今月にコピーします。既存のデータは上書きされません。よろしいですか？',
      '確認'
    )
  } catch { return }

  copyingLastMonth.value = true
  try {
    const res = await api.post('/portal/timesheet-daily/copy-from-previous', {
      targetMonth: dailyMonth.value
    })
    ElMessage.success(`${res.data.copied}件のデータをコピーしました`)
    await loadDailyEntries()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || 'コピーに失敗しました')
  } finally {
    copyingLastMonth.value = false
  }
}

// ==================== File Upload ====================

const onFileChange = (uploadFile: any) => {
  selectedFile.value = uploadFile.raw
}

const uploadAndParse = async () => {
  if (!selectedFile.value) return
  
  uploading.value = true
  try {
    const formData = new FormData()
    formData.append('file', selectedFile.value)
    
    const res = await api.post('/portal/timesheet-upload', formData, {
      headers: { 'Content-Type': 'multipart/form-data' }
    })
    
    if (res.data.success) {
      aiParseResult.value = {
        uploadId: res.data.uploadId,
        success: res.data.success,
        entries: res.data.entries,
        confidence: res.data.confidence,
        warnings: res.data.warnings || [],
        summary: res.data.summary || `${res.data.entries?.length || 0}件のデータを検出`
      }
      showUploadDialog.value = false
      selectedFile.value = null
      ElMessage.success('AI解析完了！結果を確認してください。')
    } else {
      ElMessage.error(res.data.error || '解析に失敗しました')
    }
  } catch (e: any) {
    ElMessage.error('アップロードに失敗しました')
    console.error(e)
  } finally {
    uploading.value = false
  }
}

const applyAiResult = async () => {
  if (!aiParseResult.value) return
  
  applyingAi.value = true
  try {
    const res = await api.post(`/portal/timesheet-upload/${aiParseResult.value.uploadId}/apply`)
    ElMessage.success(`${res.data.imported}件のデータを反映しました`)
    aiParseResult.value = null
    await loadDailyEntries()
  } catch (e: any) {
    ElMessage.error('反映に失敗しました')
  } finally {
    applyingAi.value = false
  }
}

// ==================== Helpers ====================

const formatYearMonth = (ym: string) => {
  if (!ym) return ''
  const [y, m] = ym.split('-')
  return `${y}年${parseInt(m)}月`
}

const getStatusLabel = (status: string) => {
  const map: Record<string, string> = {
    not_submitted: '未入力', open: '入力中', submitted: '承認待ち', confirmed: '承認済み'
  }
  return map[status] || status
}

const getStatusType = (status: string) => {
  const map: Record<string, string> = {
    not_submitted: 'info', open: 'warning', submitted: 'primary', confirmed: 'success'
  }
  return map[status] || 'info'
}

// ==================== Lifecycle ====================

watch(activeTab, (val) => {
  if (val === 'daily' && dailyEntries.value.length === 0) {
    loadDailyEntries()
  }
})

onMounted(() => {
  loadTimesheets()
  loadContracts()
})
</script>

<style scoped>
.portal-timesheet {
  padding: 24px;
  background: #f5f7fa;
  min-height: 100vh;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 12px;
}

.back-link {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  border-radius: 8px;
  background: white;
  color: #606266;
  text-decoration: none;
  transition: all 0.2s;
}

.back-link:hover {
  background: #ecf5ff;
  color: var(--el-color-primary);
}

.header-icon {
  font-size: 24px;
  color: var(--el-color-primary);
}

.header-left h1 {
  margin: 0;
  font-size: 20px;
}

.main-tabs {
  background: transparent;
}

.main-tabs :deep(.el-tabs__header) {
  background: white;
  border-radius: 8px 8px 0 0;
  padding: 0 16px;
  margin-bottom: 0;
}

/* ===== Daily View ===== */

.daily-view-card {
  border-radius: 0 0 8px 8px;
}

.daily-toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
}

.month-nav {
  display: flex;
  align-items: center;
  gap: 12px;
}

.current-month {
  font-size: 16px;
  font-weight: 600;
  min-width: 120px;
  text-align: center;
}

.daily-actions {
  display: flex;
  gap: 8px;
}

.daily-table-wrapper {
  overflow-x: auto;
}

.daily-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
}

.daily-table th {
  background: #fafafa;
  padding: 8px 6px;
  text-align: center;
  font-weight: 600;
  color: #606266;
  border-bottom: 2px solid #ebeef5;
  white-space: nowrap;
}

.daily-table td {
  padding: 4px 6px;
  border-bottom: 1px solid #f0f2f5;
  text-align: center;
  vertical-align: middle;
}

.daily-table tr:hover td {
  background: #f5f7fa;
}

.daily-table tr.weekend td {
  background: #fef0f0;
}

.daily-table tr.weekend:hover td {
  background: #fde2e2;
}

.daily-table tr.has-data td {
  background: #f0f9eb;
}

.daily-table tr.has-data.weekend td {
  background: #fef5e7;
}

.col-date { width: 60px; font-weight: 500; }
.col-day { width: 40px; }
.col-time { width: 100px; }
.col-break { width: 80px; }
.col-hours { width: 60px; }
.col-note { min-width: 120px; }

.weekend-text { color: #f56c6c; font-weight: 600; }

.hours-value { color: var(--el-color-primary); font-weight: 500; }

.actual-hours { font-weight: 600; color: var(--el-color-primary); }

.overtime { color: var(--el-color-danger); font-weight: 500; }

.summary-row td {
  background: #fafafa !important;
  border-top: 2px solid #dcdfe6;
  padding: 10px 6px;
}

.summary-label { text-align: right; padding-right: 12px !important; font-weight: 600; }

/* ===== AI Parse Banner ===== */

.ai-parse-banner {
  margin-top: 16px;
  padding: 16px;
  background: linear-gradient(135deg, #ecf5ff, #f0f9eb);
  border-radius: 8px;
  border: 1px solid #b3d8ff;
}

.ai-banner-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 600;
  color: var(--el-color-primary);
  margin-bottom: 8px;
}

.ai-summary {
  font-weight: 400;
  color: #606266;
  margin-left: 8px;
}

.ai-warnings {
  display: flex;
  align-items: center;
  gap: 6px;
  color: #e6a23c;
  font-size: 12px;
  margin-bottom: 8px;
}

.ai-banner-actions {
  display: flex;
  gap: 8px;
}

/* ===== Upload Dialog ===== */

.upload-area {
  padding: 20px 0;
}

.upload-icon {
  font-size: 48px;
  color: #c0c4cc;
  margin-bottom: 12px;
}

.upload-text {
  font-size: 14px;
  color: #606266;
}

.upload-hint {
  font-size: 12px;
  color: #909399;
  margin-top: 6px;
}

/* ===== Form Hint ===== */

.form-hint {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 12px 16px;
  background: #f0f9eb;
  border-radius: 6px;
  font-size: 13px;
  color: #67c23a;
}

/* ===== Responsive ===== */

@media (max-width: 768px) {
  .portal-timesheet { padding: 12px; }
  .daily-toolbar { flex-direction: column; gap: 12px; }
  .daily-actions { width: 100%; justify-content: flex-end; }
}
</style>
