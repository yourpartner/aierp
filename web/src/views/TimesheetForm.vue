<template>
  <div style="padding:12px">
    <el-card>
      <template #header>录入工时</template>
      <div style="margin-bottom:8px;display:flex;gap:8px;align-items:center">
        <el-date-picker v-model="month" type="month" placeholder="选择月份" format="YYYY-MM" value-format="YYYY-MM" @change="buildDays" />
        <el-button type="primary" @click="saveAll" :loading="saving">保存当月变更</el-button>
        <el-button @click="submitMonthForReview" :loading="reviewing">AI 审核本月</el-button>
      </div>
      <el-table :data="rows" size="small" :row-class-name="rowClass">
        <el-table-column label="日期" width="120">
          <template #default="{ row }">{{ row.date }}</template>
        </el-table-column>
        <el-table-column label="星期" width="80">
          <template #default="{ row }">{{ weekdayLabel(row.date) }}</template>
        </el-table-column>
        <el-table-column label="开始" width="120">
          <template #default="{ row }"><el-time-select v-model="row.startTime" :start="'06:00'" :end="'23:30'" :step="'00:30'" @change="onTimePicked(row)" placeholder="选择时间" /></template>
        </el-table-column>
        <el-table-column label="结束" width="120">
          <template #default="{ row }"><el-time-select v-model="row.endTime" :start="'06:00'" :end="'23:30'" :step="'00:30'" @change="onTimePicked(row)" placeholder="选择时间" /></template>
        </el-table-column>
        <el-table-column label="午休(分)" width="120">
          <template #default="{ row }"><el-input-number v-model="row.lunchMinutes" :min="0" :max="240" @change="recomputeRow(row)" /></template>
        </el-table-column>
        <el-table-column label="工时" width="100">
          <template #default="{ row }">{{ row.hours }}</template>
        </el-table-column>
        <el-table-column label="加班" width="100">
          <template #default="{ row }"><el-input-number v-model="row.overtime" :min="0" :max="24" :step="0.5" disabled /></template>
        </el-table-column>
        <el-table-column label="作业内容">
          <template #default="{ row }"><el-input v-model="row.task" placeholder="作业内容" /></template>
        </el-table-column>
        <el-table-column label="备注">
          <template #default="{ row }"><el-input v-model="row.note" placeholder="备注" /></template>
        </el-table-column>
        <el-table-column label="状态" width="120">
          <template #default="{ row }">{{ row.status || 'draft' }}</template>
        </el-table-column>
        </el-table>
    </el-card>
  </div>
</template>
<script setup lang="ts">
import { ref } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'

type DayRow = { id?: string, date: string, startTime: string, endTime: string, lunchMinutes: number, hours: number, overtime: number, task?: string, note?: string, status?: string, _dirty?: boolean }
const month = ref<string>(new Date().toISOString().slice(0,7))
const rows = ref<DayRow[]>([])
const saving = ref(false)
const reviewing = ref(false)
const settings = ref<{ workdayDefaultStart?:string, workdayDefaultEnd?:string, lunchMinutes?:number }>({ workdayDefaultStart: '09:00', workdayDefaultEnd: '18:00', lunchMinutes: 60 })

function pad2(n:number){ return String(n).padStart(2,'0') }
function daysInMonth(ym:string){ const [y,m] = ym.split('-').map(Number); return new Date(y!, m!, 0).getDate() }
function parseHm(s:string){ const m = /^([0-2]\d):(\d{2})$/.exec(s||''); if (!m) return null; const hh=Number(m[1]); const mm=Number(m[2]); if (hh>24||mm>59) return null; return hh*60+mm }
function recomputeRow(r:DayRow){
  const a = parseHm(r.startTime); const b = parseHm(r.endTime)
  if (a==null || b==null){ r.hours=0; r.overtime=0; return }
  const mins = Math.max(0, b - a - Number(r.lunchMinutes||0))
  const h = Math.round((mins/60)*100)/100
  r.hours = h
  // 加班：工作日=实际-标准(不小于0)，周末=全部算加班
  const isWd = !isWeekend(r.date)
  const stdA = parseHm(settings.value.workdayDefaultStart || '09:00') ?? 540
  const stdB = parseHm(settings.value.workdayDefaultEnd || '18:00') ?? 1080
  const stdMins = Math.max(0, stdB - stdA - Number(settings.value.lunchMinutes||60))
  const stdHours = Math.round((stdMins/60)*100)/100
  r.overtime = Math.max(0, isWd ? (h - stdHours) : h)
  r._dirty = true
}
function isWeekend(dateStr:string){ const d=new Date(dateStr); const w=d.getDay(); return w===0||w===6 }
function rowClass({ row }:any){ if (isWeekend(row.date)) return 'row-weekend'; return '' }
function weekdayLabel(dateStr:string){ const w=['日','一','二','三','四','五','六']; try{ return '周'+w[new Date(dateStr).getDay()] }catch{ return '' } }
function onTimePicked(r:DayRow){ recomputeRow(r) }

async function buildDays(){
  const ym = month.value
  const cnt = daysInMonth(ym)
  const arr:DayRow[] = []
  for (let d=1; d<=cnt; d++){
    const ds = `${ym}-${pad2(d)}`
    const isWd = !isWeekend(ds)
    const st = isWd ? (settings.value.workdayDefaultStart || '09:00') : ''
    const et = isWd ? (settings.value.workdayDefaultEnd || '18:00') : ''
    const lm = Number(settings.value.lunchMinutes || 60)
    const base:DayRow = { date: ds, startTime: st, endTime: et, lunchMinutes: lm, hours: 0, overtime: 0, task:'', note: '', status: 'draft' }
    if (st && et) recomputeRow(base)
    arr.push(base)
  }
  try{
    const r = await api.post('/objects/timesheet/search', { page:1, pageSize:200, where:[{ field:'month', op:'eq', value: ym }], orderBy:[{ field:'timesheet_date', dir:'ASC' }] })
    const list = Array.isArray(r.data?.data)? r.data.data : []
    const byDate = new Map<string, any>()
    list.forEach((x:any)=> byDate.set(x.timesheet_date || x.payload?.date, x))
    for (const row of arr){
      const ex = byDate.get(row.date)
      if (ex){
        row.id = ex.id
        row.startTime = ex.payload?.startTime || row.startTime
        row.endTime = ex.payload?.endTime || row.endTime
        row.lunchMinutes = Number(ex.payload?.lunchMinutes ?? row.lunchMinutes)
        row.hours = Number(ex.payload?.hours || 0)
        row.overtime = Number(ex.payload?.overtime || 0)
        row.task = ex.payload?.task || ''
        row.note = ex.payload?.note || ''
        row.status = ex.payload?.status || 'draft'
        // 若后端已有但行为空，按现配置重算
        if (!row.hours) recomputeRow(row)
      }
    }
  }catch{}
  rows.value = arr
}

async function saveAll(){
  saving.value = true
  try{
    for (const r of rows.value){
      if (!r._dirty) continue
      const payload:any = { date:r.date, startTime:r.startTime, endTime:r.endTime, lunchMinutes:r.lunchMinutes, hours:r.hours, overtime:r.overtime, task:r.task||'', note:r.note||'', status:r.status||'draft' }
      if (r.id) await api.put(`/objects/timesheet/${r.id}`, { payload })
      else {
        const res = await api.post('/objects/timesheet', { payload })
        try{ r.id = res.data?.id }catch{}
      }
      r._dirty = false
    }
    ElMessage.success('已保存当月变更')
  }catch(e:any){ ElMessage.error(e?.response?.data?.error||'保存失败') }
  finally{ saving.value=false }
}

async function submitMonthForReview(){
  reviewing.value = true
  try{
    const ym = month.value
    const entries = rows.value.map(r=>({ date:r.date, hours:Number(r.hours||0), note:r.note||'', projectCode:'' }))
    const body = { month: ym, entries }
    const r = await api.post('/ai/timesheet/review', body)
    if (r.data?.decision) ElMessage.success(`AI 审核：${r.data.decision}`)
  }catch(e:any){ ElMessage.error(e?.response?.data?.error||'审核失败') }
  finally{ reviewing.value=false }
}

async function loadSettings(){
  try{
    const r = await api.get('/company/settings')
    const p = r.data?.payload || r.data || {}
    settings.value = { workdayDefaultStart: p.workdayDefaultStart || '09:00', workdayDefaultEnd: p.workdayDefaultEnd || '18:00', lunchMinutes: Number(p.lunchMinutes || 60) }
  }catch{}
}

loadSettings().then(buildDays)
</script>
<style>
.row-weekend > td{ background-color:#f5f5f5 !important }
</style>


