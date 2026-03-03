<template>
  <!-- ========== ページモード ========== -->
  <div v-if="!dialogMode" class="timesheet-form">
    <el-card class="timesheet-form-card" v-loading="loading" element-loading-text="読み込み中...">
      <template #header>
        <div class="timesheet-form-header">
          <div class="timesheet-form-header__left">
            <el-icon class="timesheet-form-header__icon"><EditPen /></el-icon>
            <span class="timesheet-form-header__title">工数入力</span>
          </div>
          <div class="timesheet-form-header__right">
            <el-button type="primary" @click="saveAll" :loading="saving" :disabled="proxyNoTarget">
              <el-icon><Check /></el-icon><span>保存</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- 操作エリア -->
      <div class="timesheet-form-actions">
        <el-date-picker v-model="month" type="month" placeholder="月を選択" format="YYYY-MM" value-format="YYYY-MM" @change="buildDays" class="timesheet-form-actions__month" />
        <div class="timesheet-form-actions__legend">
          <span class="legend-item"><span class="dot weekend"></span>週末</span>
          <span class="legend-item"><span class="dot holiday"></span>祝日</span>
        </div>
        <div v-if="canManageTimesheets" class="timesheet-form-actions__proxy">
          <el-switch v-model="proxyMode" @change="onProxyModeChange" active-text="代理入力" inactive-text="" />
          <el-select v-if="proxyMode" v-model="proxyEmployeeId" filterable remote :remote-method="searchEmployees" :loading="employeeLoading" placeholder="社員を検索..." style="width: 240px" @change="onProxyEmployeeChange" clearable>
            <el-option v-for="o in employeeOptions" :key="o.value" :value="o.value" :label="o.label" />
          </el-select>
        </div>
      </div>

      <el-table :data="rows" border stripe highlight-current-row class="timesheet-form-table" :row-class-name="rowClass">
        <el-table-column label="日付" width="100">
          <template #default="{ row }">
            <div class="date-cell"><span>{{ row.date }}</span><el-tag v-if="row.isHoliday && !row.isWeekend" size="small" type="danger" effect="plain" class="holiday-tag">祝</el-tag></div>
          </template>
        </el-table-column>
        <el-table-column label="曜日" width="70">
          <template #default="{ row }"><span :class="{ 'text-red': row.isWeekend || row.isHoliday }">{{ weekdayLabel(row.date) }}</span></template>
        </el-table-column>
        <el-table-column label="開始" width="120">
          <template #default="{ row }"><el-time-select v-model="row.startTime" :start="'06:00'" :end="'23:30'" :step="'00:10'" @change="onTimePicked(row)" placeholder="時間を選択" /></template>
        </el-table-column>
        <el-table-column label="終了" width="120">
          <template #default="{ row }"><el-time-select v-model="row.endTime" :start="'06:00'" :end="'23:30'" :step="'00:10'" @change="onTimePicked(row)" placeholder="時間を選択" /></template>
        </el-table-column>
        <el-table-column label="休憩(分)" width="80">
          <template #default="{ row }"><el-input-number v-model="row.lunchMinutes" :min="0" :max="240" :controls="false" @change="recomputeRow(row)" style="width: 70px" /></template>
        </el-table-column>
        <el-table-column label="勤務時間" width="70">
          <template #default="{ row }">{{ formatHourDisplay(row.hours) }}</template>
        </el-table-column>
        <el-table-column label="残業" width="70">
          <template #default="{ row }">
            <span v-if="row.isHoliday" class="text-orange">{{ formatHourDisplay(row.hours) }}</span>
            <span v-else>{{ formatHourDisplay(row.overtime) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="作業内容" min-width="320">
          <template #default="{ row }"><el-input v-model="row.task" placeholder="作業内容" @input="row._dirty = true" /></template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>

  <!-- ========== ダイアログ埋め込みモード ========== -->
  <div v-else class="timesheet-form-dialog-body" v-loading="loading" element-loading-text="読み込み中...">
    <!-- 操作エリア -->
    <div class="timesheet-form-actions">
      <el-date-picker v-model="month" type="month" placeholder="月を選択" format="YYYY-MM" value-format="YYYY-MM" @change="buildDays" class="timesheet-form-actions__month" />
      <div class="timesheet-form-actions__legend">
        <span class="legend-item"><span class="dot weekend"></span>週末</span>
        <span class="legend-item"><span class="dot holiday"></span>祝日</span>
      </div>
      <div v-if="canManageTimesheets" class="timesheet-form-actions__proxy">
        <el-switch v-model="proxyMode" @change="onProxyModeChange" active-text="代理入力" inactive-text="" />
        <el-select v-if="proxyMode" v-model="proxyEmployeeId" filterable remote :remote-method="searchEmployees" :loading="employeeLoading" placeholder="社員を検索..." style="width: 240px" @change="onProxyEmployeeChange" clearable>
          <el-option v-for="o in employeeOptions" :key="o.value" :value="o.value" :label="o.label" />
        </el-select>
      </div>
    </div>

    <el-table :data="rows" border stripe highlight-current-row class="timesheet-form-table" :row-class-name="rowClass" max-height="460">
      <el-table-column label="日付" width="100">
        <template #default="{ row }">
          <div class="date-cell"><span>{{ row.date }}</span><el-tag v-if="row.isHoliday && !row.isWeekend" size="small" type="danger" effect="plain" class="holiday-tag">祝</el-tag></div>
        </template>
      </el-table-column>
      <el-table-column label="曜日" width="70">
        <template #default="{ row }"><span :class="{ 'text-red': row.isWeekend || row.isHoliday }">{{ weekdayLabel(row.date) }}</span></template>
      </el-table-column>
      <el-table-column label="開始" width="120">
        <template #default="{ row }"><el-time-select v-model="row.startTime" :start="'06:00'" :end="'23:30'" :step="'00:10'" @change="onTimePicked(row)" placeholder="時間を選択" /></template>
      </el-table-column>
      <el-table-column label="終了" width="120">
        <template #default="{ row }"><el-time-select v-model="row.endTime" :start="'06:00'" :end="'23:30'" :step="'00:10'" @change="onTimePicked(row)" placeholder="時間を選択" /></template>
      </el-table-column>
      <el-table-column label="休憩(分)" width="80">
        <template #default="{ row }"><el-input-number v-model="row.lunchMinutes" :min="0" :max="240" :controls="false" @change="recomputeRow(row)" style="width: 70px" /></template>
      </el-table-column>
      <el-table-column label="勤務時間" width="70">
        <template #default="{ row }">{{ formatHourDisplay(row.hours) }}</template>
      </el-table-column>
      <el-table-column label="残業" width="70">
        <template #default="{ row }">
          <span v-if="row.isHoliday" class="text-orange">{{ formatHourDisplay(row.hours) }}</span>
          <span v-else>{{ formatHourDisplay(row.overtime) }}</span>
        </template>
      </el-table-column>
      <el-table-column label="作業内容" min-width="300">
        <template #default="{ row }"><el-input v-model="row.task" placeholder="作業内容" @input="row._dirty = true" /></template>
      </el-table-column>
    </el-table>

    <!-- 保存ボタン -->
    <div class="timesheet-form-dialog-footer">
      <el-button type="primary" @click="saveAll" :loading="saving" :disabled="proxyNoTarget">
        <el-icon><Check /></el-icon>保存
      </el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'
import { EditPen, Check } from '@element-plus/icons-vue'

const props = defineProps<{ dialogMode?: boolean }>()
const emit = defineEmits<{ saved: [] }>()

type DayRow = {
  id?: string
  date: string
  startTime: string
  endTime: string
  lunchMinutes: number
  hours: number
  overtime: number
  task?: string
  status?: string
  _dirty?: boolean
  isWeekend?: boolean
  isHoliday?: boolean
}

const month = ref<string>(new Date().toISOString().slice(0, 7))
const rows = ref<DayRow[]>([])
const saving = ref(false)
const loading = ref(true)
const settings = ref<{ workdayDefaultStart?: string; workdayDefaultEnd?: string; lunchMinutes?: number }>({
  workdayDefaultStart: '09:00',
  workdayDefaultEnd: '18:00',
  lunchMinutes: 60
})
const holidaySet = ref<Set<string>>(new Set())

const canManageTimesheets = computed(() => {
  const caps = (sessionStorage.getItem('userCaps') || '').split(',').filter(Boolean)
  return caps.includes('timesheet:proxy')
})

const proxyNoTarget = computed(() => proxyMode.value && !proxyEmployeeId.value)
const proxyMode = ref(false)
const proxyEmployeeId = ref<string>('')
const proxyEmployeeName = ref<string>('')
const employeeOptions = ref<{ value: string; label: string; mappedUserId: string }[]>([])
const employeeLoading = ref(false)

function targetUserId(): string {
  if (proxyMode.value && proxyEmployeeId.value) {
    const opt = employeeOptions.value.find(e => e.value === proxyEmployeeId.value)
    return opt?.mappedUserId || proxyEmployeeId.value
  }
  return currentUserId()
}

async function searchEmployees(q: string) {
  employeeLoading.value = true
  try {
    const ym = month.value
    const asOfDate = ym ? `${ym}-01` : undefined
    const where: any[] = [{ field: '__employment_status__', op: 'eq', value: 'active' }]
    if (asOfDate) where.push({ field: '__employment_as_of__', op: 'eq', value: asOfDate })
    if (q) {
      where.push({ anyOf: [
        { json: 'nameKanji', op: 'contains', value: q },
        { json: 'nameKana', op: 'contains', value: q },
        { field: 'employee_code', op: 'contains', value: q }
      ]})
    }
    const r = await api.post('/objects/employee/search', {
      page: 1, pageSize: 200, where,
      orderBy: [{ field: 'employee_code', dir: 'ASC' }]
    })
    const list = Array.isArray(r.data?.data) ? r.data.data : []
    const options = list.map((x: any) => {
      const name = x.payload?.nameKanji || x.payload?.name || x.payload?.nameKana || ''
      return {
        value: x.id,
        label: name ? `${name} (${x.employee_code || ''})` : (x.employee_code || x.id),
        mappedUserId: ''
      }
    })
    try {
      const uRes = await api.get('/api/users')
      const users = Array.isArray(uRes.data?.users) ? uRes.data.users : []
      const empToUser = new Map<string, string>()
      for (const u of users) {
        if (u.employeeId) empToUser.set(u.employeeId, u.id)
      }
      for (const opt of options) opt.mappedUserId = empToUser.get(opt.value) || opt.value
    } catch {
      for (const opt of options) opt.mappedUserId = opt.value
    }
    employeeOptions.value = options
  } catch {
    employeeOptions.value = []
  } finally {
    employeeLoading.value = false
  }
}

function onProxyEmployeeChange(val: string) {
  const opt = employeeOptions.value.find(e => e.value === val)
  proxyEmployeeName.value = opt?.label || ''
  buildDays()
}

function onProxyModeChange(val: boolean) {
  if (!val) {
    proxyEmployeeId.value = ''
    proxyEmployeeName.value = ''
    buildDays()
  } else {
    searchEmployees('')
  }
}

function pad2(n: number) { return String(n).padStart(2, '0') }
function daysInMonth(ym: string) { const [y, m] = ym.split('-').map(Number); return new Date(y!, m!, 0).getDate() }
function parseHm(s: string) { const m = /^([0-2]\d):(\d{2})$/.exec(s || ''); if (!m) return null; const hh = Number(m[1]); const mm = Number(m[2]); if (hh > 24 || mm > 59) return null; return hh * 60 + mm }

function recomputeRow(r: DayRow) {
  const a = parseHm(r.startTime); const b = parseHm(r.endTime)
  if (a == null || b == null) { r.hours = 0; r.overtime = 0; return }
  const mins = Math.max(0, b - a - Number(r.lunchMinutes || 0))
  const h = Math.round((mins / 60) * 100) / 100
  r.hours = h
  const isHolidayOrWeekend = r.isWeekend || r.isHoliday
  const stdA = parseHm(settings.value.workdayDefaultStart || '09:00') ?? 540
  const stdB = parseHm(settings.value.workdayDefaultEnd || '18:00') ?? 1080
  const stdMins = Math.max(0, stdB - stdA - Number(settings.value.lunchMinutes || 60))
  const stdHours = Math.round((stdMins / 60) * 100) / 100
  r.overtime = Math.max(0, isHolidayOrWeekend ? h : (h - stdHours))
  r._dirty = true
}

function isWeekend(dateStr: string) { const d = new Date(dateStr); const w = d.getDay(); return w === 0 || w === 6 }

function rowClass({ row }: any) {
  if (row.isHoliday && !row.isWeekend) return 'row-holiday'
  if (row.isWeekend) return 'row-weekend'
  return ''
}

function weekdayLabel(dateStr: string) {
  const w = ['日', '月', '火', '水', '木', '金', '土']
  try { return w[new Date(dateStr).getDay()] } catch { return '' }
}

function onTimePicked(r: DayRow) { recomputeRow(r) }

function formatHourDisplay(value: number) {
  const minutes = Math.round((Number(value) || 0) * 60)
  if (!minutes) return '0H'
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  const parts: string[] = []
  if (h) parts.push(`${h}H`)
  if (m) parts.push(`${m}M`)
  return parts.join('')
}

async function loadHolidays(ym: string) {
  const from = `${ym}-01`
  const to = `${ym}-${pad2(daysInMonth(ym))}`
  try {
    const r = await api.get('/holidays', { params: { from, to } })
    const list = Array.isArray(r.data?.holidays) ? r.data.holidays : []
    holidaySet.value = new Set(list.map((h: any) => h.date))
  } catch {
    holidaySet.value = new Set()
  }
}

async function buildDays() {
  loading.value = true
  const ym = month.value

  if (proxyMode.value) {
    searchEmployees('')
    if (!proxyEmployeeId.value) {
      rows.value = []
      loading.value = false
      return
    }
  }

  await loadHolidays(ym)

  const cnt = daysInMonth(ym)
  const arr: DayRow[] = []
  for (let d = 1; d <= cnt; d++) {
    const ds = `${ym}-${pad2(d)}`
    const isWd = !isWeekend(ds)
    const isHday = holidaySet.value.has(ds)
    const isWorkDay = isWd && !isHday
    const st = isWorkDay ? (settings.value.workdayDefaultStart || '09:00') : ''
    const et = isWorkDay ? (settings.value.workdayDefaultEnd || '18:00') : ''
    const lm = Number(settings.value.lunchMinutes || 60)
    const base: DayRow = {
      date: ds, startTime: st, endTime: et, lunchMinutes: lm,
      hours: 0, overtime: 0, task: '', status: 'draft',
      isWeekend: !isWd, isHoliday: isHday
    }
    if (st && et) recomputeRow(base)
    arr.push(base)
  }
  try {
    const tuid = targetUserId()
    const tsWhere: any[] = [{ field: 'month', op: 'in', value: [ym] }]
    if (tuid) tsWhere.push({ json: 'creatorUserId', op: 'in', value: [tuid] })
    const r = await api.post('/objects/timesheet/search', {
      page: 1, pageSize: 200, where: tsWhere,
      orderBy: [{ field: 'timesheet_date', dir: 'ASC' }]
    })
    const list = Array.isArray(r.data?.data) ? r.data.data : []
    const byDate = new Map<string, any>()
    list.forEach((x: any) => byDate.set(x.timesheet_date || x.payload?.date, x))
    for (const row of arr) {
      const ex = byDate.get(row.date)
      if (ex) {
        row.id = ex.id
        row.startTime = ex.payload?.startTime || row.startTime
        row.endTime = ex.payload?.endTime || row.endTime
        row.lunchMinutes = Number(ex.payload?.lunchMinutes ?? row.lunchMinutes)
        row.hours = Number(ex.payload?.hours || 0)
        row.overtime = Number(ex.payload?.overtime || 0)
        row.task = ex.payload?.task || ''
        row.status = 'saved'
        if (!row.hours) recomputeRow(row)
      }
    }
  } catch {}
  rows.value = arr
  loading.value = false
}

function currentUserId(): string {
  try {
    const raw = sessionStorage.getItem('user')
    if (!raw) return ''
    const u = JSON.parse(raw)
    return (u?.id || u?.userId || u?.user_id || '').toString()
  } catch {
    return ''
  }
}

async function saveDirtyRows(showToast: boolean) {
  saving.value = true
  try {
    const uid = targetUserId()
    for (const r of rows.value) {
      if (!r._dirty) continue
      const payload: any = {
        date: r.date,
        startTime: r.startTime || null,
        endTime: r.endTime || null,
        lunchMinutes: r.lunchMinutes,
        hours: r.hours,
        overtime: r.overtime,
        task: r.task || '',
        status: 'saved',
        creatorUserId: uid || undefined,
        isHoliday: r.isHoliday || false
      }
      if (r.id) await api.put(`/objects/timesheet/${r.id}`, { payload })
      else {
        const res = await api.post('/objects/timesheet', { payload })
        try { r.id = res.data?.id } catch {}
      }
      r._dirty = false
      r.status = 'saved'
    }
    if (showToast) ElMessage.success('保存しました')
  } catch (e: any) {
    if (showToast) ElMessage.error(e?.response?.data?.error || '保存に失敗しました')
    throw e
  } finally {
    saving.value = false
  }
}

async function saveAll() {
  await saveDirtyRows(true)
  if (props.dialogMode) emit('saved')
}

async function loadSettings() {
  try {
    const r = await api.get('/company/settings')
    const p = r.data?.payload || r.data || {}
    settings.value = {
      workdayDefaultStart: p.workdayDefaultStart || '09:00',
      workdayDefaultEnd: p.workdayDefaultEnd || '18:00',
      lunchMinutes: Number(p.lunchMinutes || 60)
    }
  } catch {}
}

loadSettings().then(buildDays)
</script>

<style scoped>
.timesheet-form {
  padding: 16px;
  max-width: 1120px;
  margin: 0 auto;
}

.timesheet-form-card {
  border-radius: 12px;
  overflow: hidden;
}

.timesheet-form-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
}

.timesheet-form-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.timesheet-form-header__icon {
  font-size: 22px;
  color: #67c23a;
}

.timesheet-form-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.timesheet-form-header__right {
  display: flex;
  gap: 8px;
}

/* ダイアログモード */
.timesheet-form-dialog-body {
  display: flex;
  flex-direction: column;
  gap: 0;
}

.timesheet-form-dialog-footer {
  display: flex;
  gap: 10px;
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

/* 操作エリア */
.timesheet-form-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  align-items: center;
  margin-bottom: 16px;
  padding: 16px;
  background: #f8f9fc;
  border-radius: 8px;
}

.timesheet-form-actions__month {
  width: 140px;
}

.timesheet-form-actions__legend {
  display: flex;
  gap: 16px;
  font-size: 12px;
  color: #666;
}

.timesheet-form-actions__proxy {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-left: auto;
}

/* テーブル */
.timesheet-form-table {
  border-radius: 8px;
  overflow: hidden;
}

.timesheet-form-table :deep(.el-table__header th) {
  background: #f5f7fa;
  font-weight: 600;
  color: #303133;
}

@media (max-width: 768px) {
  .timesheet-form-header {
    flex-direction: column;
    align-items: flex-start;
  }
  .timesheet-form-header__right {
    width: 100%;
    flex-wrap: wrap;
  }
}
</style>

<style>
.row-weekend > td { background-color: #f5f5f5 !important; }
.row-holiday > td { background-color: #fff5f5 !important; }
.text-red { color: #f56c6c; }
.text-orange { color: #e6a23c; font-weight: 500; }
.date-cell { display: flex; align-items: center; gap: 4px; }
.holiday-tag { padding: 0 4px; height: 18px; line-height: 16px; }
.legend-item { display: flex; align-items: center; gap: 4px; }
.dot { width: 12px; height: 12px; border-radius: 2px; }
.dot.weekend { background-color: #f5f5f5; border: 1px solid #ddd; }
.dot.holiday { background-color: #fff5f5; border: 1px solid #f56c6c; }
</style>
