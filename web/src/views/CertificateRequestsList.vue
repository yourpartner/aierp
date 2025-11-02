<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">证明申请清单</div>
          <div class="page-actions">
            <el-button type="primary" @click="$router.push('/cert/request')">新建申请</el按钮>
          </div>
        </div>
      </template>
      <el-table :data="rows" border size="small" style="width:100%">
        <el-table-column type="index" width="60" />
        <el-table-column v-for="(c,i) in columns" :key="i" :label="c.label || c.field || c.json" :prop="c.field" :width="c.width">
          <template #default="{ row }">{{ cellValue(row, c) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="200">
          <template #default="{ row }">
            <el-button v-for="(a,idx) in actionsToRender()" :key="idx" size="small" :type="a.type || 'primary'" @click="onAction(a, row)">{{ a.text || a.label || a.name }}</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import api from '../api'
import { getLang } from '../i18n'

const rows = reactive<any[]>([])
const columns = reactive<any[]>([])
const actions = reactive<any[]>([])
const loading = ref(false)

async function load(){
  loading.value = true
  try{
    // 先给出一组默认列，避免 UI 配置缺失时整列消失
    // 不再使用默认列：严格依赖 schema
    columns.splice(0, columns.length)

    // 1) 读取 schema，按 ui.list 覆盖默认列
    const s = await api.get('/schemas/certificate_request', { params: { lang: getLang() } })
    const ui = (s.data?.ui || {})
    const listCfg = ui?.list || {}
    const cols = Array.isArray(listCfg.columns) ? listCfg.columns : []
    const normalized = normalizeColumns(cols)
  // 兜底：若 schema 未配置 purpose 列，则追加一列（紧随 type 后）
  const hasPurpose = normalized.some((c:any)=> (c.json||c.field)==='purpose')
  if (!hasPurpose){
    const idx = normalized.findIndex((c:any)=> (c.json||c.field)==='type')
    const col = { json:'purpose', label:'用途与备注' }
    if (idx>=0) { normalized.splice(idx+1,0,col) } else { normalized.push(col) }
  }
    columns.splice(0, columns.length, ...normalized)
    const acts = Array.isArray(listCfg.actions) ? listCfg.actions : []
    actions.splice(0, actions.length, ...normalizeActions(acts))
    // 若 actions 未配置，下面会兜底
    if (actions.length===0){
      actions.push({ name:'downloadPdf', text:'下载PDF', type:'primary' })
    }

    // 2) 拉取数据
    rows.splice(0, rows.length)
    const r = await api.post('/objects/certificate_request/search', { page:1, pageSize:100, where: [], orderBy:[{ field:'created_at', dir:'DESC' }] })
    const data = (r.data?.data||[]) as any[]
    for (const x of data) rows.push(x)
  } finally { loading.value = false }
}

function normalizeColumns(cols:any[]){
  const coreFields = new Set(['id','created_at','updated_at','company_code'])
  return (cols||[])
    .map((c:any)=>{
      if (typeof c === 'string'){
        if (coreFields.has(c)) return { field: c, label: c }
        return { json: c, label: c }
      }
      const x:any = { ...c }
      // 兼容多种命名：name/path/key => json；col/prop => field
      if (!x.field && (x.col || x.prop)) x.field = x.col || x.prop
      if (!x.field && !x.json && (x.name || x.path || x.key)) x.json = x.name || x.path || x.key
      return x
    })
    .filter((c:any)=> !!(c && (c.field || c.json)))
}
function normalizeActions(acts:any[]){
  return acts.map(a=>({ ...a }))
}
function actionsToRender(){
  // 始终至少渲染一个“下载PDF”按钮（schema 未配置时的兜底）
  return actions.length>0 ? actions : [{ name:'downloadPdf', text:'下载PDF', type:'primary' }]
}
function getByPath(obj:any, path:string){
  try{ return String(path).split('.').reduce((o:any,k:string)=> (o? o[k]: undefined), obj) }catch{ return undefined }
}
function cellValue(row:any, col:any){
  if (!col) return ''
  if (col.json){
    // json 路径默认从 payload 读取
    const p = row?.payload || {}
    return getByPath(p, col.json) ?? ''
  }
  if (col.field){
    // 支持直接 field 或 payload.xx 形式
    const v = getByPath(row, col.field)
    if (col.field === 'created_at' && v){
      try{ const d = new Date(v); if (!isNaN(d.getTime())) return d.toISOString().slice(0,10) }catch{}
    }
    return v ?? ''
  }
  return ''
}
async function onAction(a:any, row:any){
  const name = a?.name || a?.action
  if (!name) return
  if (name === 'downloadPdf') return downloadPdf(row)
}
async function downloadPdf(row:any){
  const id = row?.id; if (!id) return
  const resp = await api.get(`/operations/certificate_request/${id}/pdf`, { responseType: 'blob' })
  const blob = new Blob([resp.data], { type: 'application/pdf' })
  const fn = (row?.payload?.pdf?.filename) || 'certificate.pdf'
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = fn
  document.body.appendChild(a)
  a.click()
  URL.revokeObjectURL(a.href)
  document.body.removeChild(a)
}

onMounted(load)
</script>

<style scoped>
.page.page-medium { max-width: 1000px; }
.filters{ display:flex; gap:8px; margin-bottom:8px }
</style>


