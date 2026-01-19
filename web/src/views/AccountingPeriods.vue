<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Timer /></el-icon>
            <span class="page-header-title">{{ text.tables.accountingPeriods.title }}</span>
          </div>
        </div>
      </template>

      <div class="period-toolbar">
        <el-date-picker
          v-model="yearValue"
          type="year"
          format="YYYY"
          value-format="YYYY"
          class="year-picker"
          :placeholder="String(currentYear)"
        />
        <el-button :loading="loading" @click="reload">{{ text.buttons.refresh }}</el-button>
      </div>

      <div class="months-grid" v-loading="loading">
        <div v-for="entry in months" :key="entry.index" class="month-card">
          <div class="month-label">{{ monthLabel(entry.index) }}</div>
          <el-switch
            :model-value="entry.open"
            :loading="entry.loading"
            :disabled="loading"
            :active-text="text.tables.accountingPeriods.open"
            :inactive-text="text.tables.accountingPeriods.closed"
            @change="val => onToggleMonth(entry, val as boolean)"
          />
        </div>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, watch, onMounted } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'
import { Timer } from '@element-plus/icons-vue'
import { useI18n } from '../i18n'

let ensureSchemaPromise: Promise<void> | null = null
async function ensureSchema() {
  if (ensureSchemaPromise) return ensureSchemaPromise
  ensureSchemaPromise = (async () => {
    try {
      await api.get('/schemas/accounting_period', { params: { lang: lang.value } })
    } catch (err: any) {
      const status = err?.response?.status
      if (status !== 404) throw err
      const doc = {
        schema: {
          type: 'object',
          properties: {
            periodStart: { type: 'string', format: 'date' },
            periodEnd: { type: 'string', format: 'date' },
            isOpen: { type: 'boolean' }
          },
          required: ['periodStart', 'periodEnd']
        },
        ui: { form: { layout: [] } },
        query: {
          filters: ['period_start', 'period_end', 'is_open'],
          sorts: ['period_start', 'period_end']
        },
        core_fields: { coreFields: [] },
        validators: [],
        numbering: {},
        ai_hints: {}
      }
      await api.post('/schemas/accounting_period', doc)
    }
  })().finally(() => {
    ensureSchemaPromise = null
  })
  return ensureSchemaPromise
}

interface MonthEntry {
  index: number
  open: boolean
  id: string
  loading: boolean
}

const { text, lang } = useI18n()
const loading = ref(false)
const currentYear = ref(new Date().getFullYear())
const yearValue = ref(String(currentYear.value))
const months = ref<MonthEntry[]>([])
let latestLoadToken = 0

function createBlankMonths(): MonthEntry[] {
  return Array.from({ length: 12 }, (_, index) => ({ index, open: false, id: '', loading: false }))
}

function monthLabel(index: number) {
  const formatter = lang.value === 'ja' ? 'ja-JP' : 'en-US'
  const date = new Date(currentYear.value, index, 1)
  return date.toLocaleDateString(formatter, { month: 'short' })
}

function buildPeriod(year: number, monthIndex: number) {
  const mm = String(monthIndex + 1).padStart(2, '0')
  const lastDay = new Date(year, monthIndex + 1, 0).getDate()
  const dd = String(lastDay).padStart(2, '0')
  return {
    start: ${year}--01,
    end: ${year}--
  }
}

async function loadYear(year: number) {
  latestLoadToken += 1
  const token = latestLoadToken
  loading.value = true
  const draft = createBlankMonths()
  try {
    await ensureSchema()
    const start = ${year}-01-01
    const end = ${year}-12-31
    const resp = await api.post('/objects/accounting_period/search', {
      page: 1,
      pageSize: 200,
      where: [
        { field: 'period_start', op: 'gte', value: start },
        { field: 'period_start', op: 'lte', value: end }
      ],
      orderBy: [{ field: 'period_start', dir: 'ASC' }]
    })
    if (token !== latestLoadToken) return
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    rows.forEach((row: any) => {
      const payload = row.payload || {}
      const raw = payload.periodStart || row.period_start
      if (!raw) return
      const dt = new Date(raw)
      if (Number.isNaN(dt.getTime())) return
      
      // 纭繚鍙湁褰撳墠骞翠唤鐨勬暟鎹鏄犲皠
      if (dt.getFullYear() !== year) return
      
      const idx = dt.getMonth()
      const entry = draft[idx]
      if (!entry) return
      entry.open = payload.isOpen !== false && row.is_open !== false
      entry.id = row.id
    })
    months.value = draft
  } catch (err) {
    if (token === latestLoadToken) {
      months.value = draft
      console.error('load accounting periods failed', err)
      ElMessage.error(text.value.tables.accountingPeriods.loadFailed)
    }
  } finally {
    if (token === latestLoadToken) loading.value = false
  }
}

async function openMonth(entry: MonthEntry) {
  await ensureSchema()
  const { start, end } = buildPeriod(currentYear.value, entry.index)
  await api.post('/objects/accounting_period', {
    payload: { periodStart: start, periodEnd: end, isOpen: true }
  })
  await loadYear(currentYear.value)
  ElMessage.success(text.value.tables.accountingPeriods.createSuccess)
}

async function closeMonth(entry: MonthEntry) {
  if (!entry.id) return
  await ensureSchema()
  await api.delete(/objects/accounting_period/)
  await loadYear(currentYear.value)
  ElMessage.success(text.value.tables.accountingPeriods.deleteSuccess)
}

async function onToggleMonth(entry: MonthEntry, value: boolean) {
  if (entry.loading) return
  const previous = entry.open
  entry.open = value
  entry.loading = true
  try {
    if (value) {
      await openMonth(entry)
    } else {
      await closeMonth(entry)
    }
  } catch (err) {
    entry.open = previous
    console.error('toggle month failed', err)
    ElMessage.error(text.value.tables.accountingPeriods.saveFailed)
  } finally {
    entry.loading = false
  }
}

function reload() {
  loadYear(currentYear.value)
}

watch(currentYear, (year) => {
  const str = String(year)
  if (yearValue.value !== str) yearValue.value = str
  loadYear(year)
})

watch(yearValue, (val) => {
  if (!val) return
  const num = Number(val)
  if (!Number.isFinite(num)) return
  if (num !== currentYear.value) currentYear.value = num
})

onMounted(() => {
  months.value = createBlankMonths()
  loadYear(currentYear.value)
})
</script>

<style scoped>
/* 鏍囬鍖哄煙鏍峰紡 */
.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #909399;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.period-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 16px;
}
.year-picker {
  width: 160px;
}
.months-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
  gap: 16px;
}
.month-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
  border-radius: 12px;
  background: #f8fafc;
  border: 1px solid #e2e8f0;
}
.month-label {
  font-weight: 600;
  color: #1f2937;
}
</style>
