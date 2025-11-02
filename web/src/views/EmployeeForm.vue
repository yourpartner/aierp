<template>
  <div style="padding:12px">
    <el-card>
      <template #header>{{ title }}</template>
      <template v-if="uiError">
        <el-result icon="error" title="加载失败" :sub-title="uiError" />
      </template>
      <template v-else>
        <DynamicForm v-if="!uiLoading && ui" :ui="ui" :schema="schemaDoc?.schema" :array-fields="['emergencies','contracts','departments','bankAccounts','attachments']" :columns-override="columnsOverride" :model="model" @save="onSave" @action="onAction" />
        <template v-else>
          <el-skeleton :rows="6" animated />
          <div v-if="!uiLoading && !ui" style="margin-top:8px">
            <el-alert type="warning" show-icon title="未获得有效的 UI 配置" description="请检查 /schemas/employee 是否返回 ui.form.layout。" />
            <el-collapse style="margin-top:6px">
              <el-collapse-item title="查看接口原始响应">
                <pre style="white-space:pre-wrap;word-break:break-all;max-height:240px;overflow:auto">{{ raw }}</pre>
              </el-collapse-item>
            </el-collapse>
          </div>
        </template>
      </template>
      <div style="display:flex;justify-content:flex-end;margin-top:10px" v-if="!uiLoading && ui">
        <el-button type="primary" @click="handleSave">保存</el-button>
      </div>
      
    </el-card>

    <!-- 银行/支店选择弹窗（与会计科目一致） -->
    <el-dialog v-model="showBank" title="选择银行" width="720px">
      <BankBranchPicker mode="bank" @select="onPickBank" @cancel="showBank=false" />
    </el-dialog>
    <el-dialog v-model="showBranch" title="选择支店" width="720px">
      <BankBranchPicker mode="branch" @select="onPickBranch" @cancel="showBranch=false" />
    </el-dialog>
  </div>
  <!-- 全局隐藏文件选择，用于附件tab的“上传附件”按钮触发 -->
  <input type="file" ref="fileInput" style="display:none" @change="onFileChosen" />
  
</template>
<script setup lang="ts">
import { ref, onMounted, defineExpose, computed } from 'vue'
import { useRoute } from 'vue-router'
import api from '../api'
import DynamicForm from '../components/DynamicForm.vue'
import { ElMessage } from 'element-plus'
import BankBranchPicker from '../components/BankBranchPicker.vue'

const props = defineProps<{ empId?: string }>()
const route = useRoute()
const id = (props.empId || (route.params as any).id) as string | undefined
const title = id ? '编辑员工' : '新建员工'
const ui = ref<any>(null)
const schemaDoc = ref<any>(null)
const uiLoading = ref(false)
const uiError = ref('')
const model = ref<any>({})
const raw = ref('')
const columnsOverride = ref<Record<string, any[]>>({})
const showBank = ref(false)
const showBranch = ref(false)
const currentRow = ref<any>(null)
// 旧工资预览相关状态删除，避免引用被保留
const previewMonth = ref<string>('')
const policies = ref<any[]>([])
const policyId = ref<string>('')
const fileInput = ref<HTMLInputElement|null>(null)

async function loadUi(){
  uiLoading.value = true
  uiError.value = ''
  try{
    const r = await api.get('/schemas/employee')
    schemaDoc.value = r.data || null
    ui.value = r.data?.ui || null
    try{ raw.value = JSON.stringify(r.data,null,2) }catch{}
    const lay = ui.value?.form?.layout
    if (!ui.value) throw new Error('schema.ui 为空')
    if (!Array.isArray(lay) || lay.length===0) throw new Error('schema.ui.form.layout 为空')
    // 补充数组列定义，便于 DynamicForm 渲染表格列
    // 预取：部门下拉与雇佣类型下拉
    const deptOpts = await fetchDepartmentsAll()
    const empTypeOpts = await fetchEmploymentTypesAll()
    augmentArrayColumns()
    ensureGenderSelect()
    transformCompensationTab()
    enhanceTopLevelFields(deptOpts, empTypeOpts)
  }catch(e:any){ uiError.value = e?.response?.data?.error || e?.message || '无法加载 Schema' }
  finally { uiLoading.value = false }
}


async function loadData(){
  if (!id) return
  const r = await api.get(`/objects/employee/${id}`)
  model.value = r.data?.payload || r.data || {}
}

// 初始化新建时的数组字段，确保可直接新增行
function initArrays(){
  const arrFields = ['emergencies','contracts','departments','bankAccounts','attachments']
  for (const f of arrFields){ if (!Array.isArray((model.value as any)[f])) (model.value as any)[f] = [] }
  if (typeof (model.value as any).nlPayrollDescription !== 'string') (model.value as any).nlPayrollDescription = ''
  // 便于用户直接选择：若所属部门为空，默认先添加一行
  try { if (Array.isArray((model.value as any).departments) && (model.value as any).departments.length===0) (model.value as any).departments.push({}) } catch {}
}

async function onSave(payload:any){
  if (id) await api.put(`/objects/employee/${id}`, { payload })
  else await api.post('/objects/employee', { payload })
}

function applyIntent(p:any){ Object.assign(model.value, p||{}) }
defineExpose({ applyIntent })

onMounted(async()=>{ await loadUi(); await loadData(); initArrays();
  try{ (window as any).__EMP_MODEL = model.value; (window as any).__EMP_COLS = columnsOverride.value; (window as any).EMP_MODEL = model.value; (window as any).EMP_COLS = columnsOverride.value }catch{}
})

// 加载超时保护：8 秒仍未完成则给出提示，避免只见边框
setTimeout(()=>{
  if (uiLoading.value) {
    uiLoading.value = false
    if (!uiError.value) uiError.value = '加载超时或 /schemas/employee 无响应'
  }
}, 8000)

// 为数组字段补充列定义（在现有 ui 上就地注入 props.columns）
function augmentArrayColumns(){
  const cfgs:{path:string, cols:any[]}[] = [
    { path:'emergencies', cols:[
      { field:'nameKanji', label:'姓名（汉字）', width:140 },
      { field:'nameKana', label:'姓名（假名）', width:140 },
      { field:'relation', label:'关系', width:100 },
      { field:'phone', label:'电话', width:140 },
      { field:'address', label:'地址' },
      { field:'note', label:'备注' }
    ]},
    { path:'contracts', cols:[
      { field:'contractType', label:'合同类型', width:120 },
      { field:'periodFrom', label:'开始日期', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
      { field:'periodTo', label:'结束日期', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
      { field:'note', label:'备注' }
    ]},
    // payrollItems 已由自然语言字段替代
    { path:'departments', cols:[
      { field:'departmentId', label:'部门ID/编码', width:180 },
      { field:'fromDate', label:'开始日期', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
      { field:'toDate', label:'结束日期', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
      { field:'position', label:'职位', width:140 }
    ]},
    { path:'bankAccounts', cols:[
      { field:'bank', label:'银行', width:140 },
      { field:'branch', label:'支行', width:140 },
      { field:'accountType', label:'账户类型', width:120 },
      { field:'accountNo', label:'账号', width:160 },
      { field:'holder', label:'户名', width:140 },
      { field:'effectiveDate', label:'生效日期', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' }
    ]},
    { path:'attachments', cols:[
      { field:'fileName', label:'文件名', width:260 },
      { field:'uploadedAt', label:'上传时间', width:180 },
      { field:'size', label:'大小(Bytes)', width:140 },
      { field:'url', label:'URL', width:260 }
    ]}
  ]
  for (const c of cfgs){ ensurePropsColumns(c.path, c.cols) }
}

function ensurePropsColumns(path:string, columns:any[]){
  const layout:any[] = ui.value?.form?.layout || []
  const attach = (node:any) => {
    node.props = node.props || {}
    node.props.columns = columns
  }
  const scan = (arr:any[]) => {
    for (const b of arr){
      if (b?.type==='grid' && Array.isArray(b.cols)){
        const hit = b.cols.find((x:any)=> x.field===path)
        if (hit){ attach(hit); return true }
      } else if (b?.type==='section' && Array.isArray(b.layout)){
        if (scan(b.layout)) return true
      } else if (b?.type==='tabs' && Array.isArray(b.items)){
        for (const t of b.items){ if (Array.isArray(t.children)){ const hit = t.children.find((c:any)=> c.field===path); if (hit){ attach(hit); return true } } }
      }
    }
    return false
  }
  scan(layout)
}

// 将性别渲染为下拉
function ensureGenderSelect(){
  const layout:any[] = ui.value?.form?.layout || []
  const opts = [ { label:'男', value:'M' }, { label:'女', value:'F' } ]
  const walk=(arr:any[]):boolean=>{
    for (const b of arr){
      if (b?.type==='grid' && Array.isArray(b.cols)){
        const hit=b.cols.find((c:any)=>c.field==='gender')
        if (hit){ hit.widget='select'; hit.props = hit.props || {}; hit.props.options = opts; return true }
      } else if (b?.type==='section' && Array.isArray(b.layout)){
        if (walk(b.layout)) return true
      } else if (b?.type==='tabs' && Array.isArray(b.items)){
        for (const t of b.items){ if (Array.isArray(t.children)){ const hit=t.children.find((c:any)=>c.field==='gender'); if (hit){ hit.widget='select'; hit.props = hit.props || {}; hit.props.options = opts; return true } } }
      }
    }
    return false
  }
  walk(layout)
}
// 顶层字段增强：包含远程部门下拉、银行/支店按钮选择
function enhanceTopLevelFields(deptOpts:any[]=[], empTypeOpts:any[]=[]){
  const layout:any[] = ui.value?.form?.layout || []
  const attachField = (field:string, cb:(node:any)=>void)=>{
    const walk=(arr:any[]):boolean=>{
      for(const b of arr){
        if (b?.type==='grid' && Array.isArray(b.cols)){
          const hit = b.cols.find((c:any)=> c.field===field)
          if (hit){ cb(hit); return true }
        } else if (b?.type==='section' && Array.isArray(b.layout)){
          if (walk(b.layout)) return true
        } else if (b?.type==='tabs' && Array.isArray(b.items)){
          for(const t of b.items){ if (Array.isArray(t.children)){ const hit=t.children.find((c:any)=>c.field===field); if (hit){ cb(hit); return true } } }
        }
      }
      return false
    }
    walk(layout)
  }
  // 姓名（假名）全角片假名
  attachField('nameKana', (n)=>{ n.props = n.props || {}; n.props.filter = 'katakana-zen' })
  // 生日日期控件
  attachField('birthDate', (n)=>{ n.widget='date'; n.props = n.props || {}; n.props.type='date'; n.props.valueFormat='YYYY-MM-DD' })
  // 国籍下拉（示例国家，可替换为后端枚举）
  attachField('nationality', (n)=>{ n.widget='select'; n.props = n.props || {}; n.props.options=[{label:'日本',value:'JP'},{label:'中国',value:'CN'},{label:'美国',value:'US'}] })
  // 合同-雇佣类型：替代旧的合同类型输入
  ensurePropsColumns('contracts', [
    { field:'employmentTypeCode', label:'雇佣类型', width:200, widget:'select', props:{ filterable:true, clearable:true, options: empTypeOpts } },
    { field:'periodFrom', label:'开始日期', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
    { field:'periodTo', label:'结束日期', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
    { field:'note', label:'备注' }
  ])
  // 紧急联系人关系列下拉
  ensurePropsColumns('emergencies', [
    { field:'nameKanji', label:'姓名（汉字）', width:140 },
    { field:'nameKana', label:'姓名（假名）', width:140, inputType:'text', props:{ filter:'katakana-zen' } },
    { field:'relation', label:'关系', width:120, widget:'select', props:{ options:[{label:'父母',value:'parent'},{label:'配偶',value:'spouse'},{label:'子女',value:'child'},{label:'朋友',value:'friend'}] } },
    { field:'phone', label:'电话', width:140 },
    { field:'address', label:'地址' },
    { field:'note', label:'备注' }
  ])
  // 部门履历中的部门：恢复为静态下拉（已预加载 deptOpts）
  const deptColsLocal = [
    { field:'departmentId', label:'部门', width:240, widget:'select', props:{ filterable:true, clearable:true, options: deptOpts } },
    { field:'fromDate', label:'开始日期', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
    { field:'toDate', label:'结束日期', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
    { field:'position', label:'职位', width:140 }
  ]
  // 通过显式覆盖强制渲染
  columnsOverride.value['departments'] = deptColsLocal as any
  ensurePropsColumns('departments', deptColsLocal as any)
  // 工资项目：改为自然语言描述，不再渲染项目列表
  // 银行账户：银行/支店同列显示为“文本+按钮”
  const bankColsLocal = [
    { field:'bank', label:'银行', width:260, widget:'labelButton', props:{ text:'选择', action:'pickBank', type:'primary' } },
    { field:'branch', label:'支店', width:260, widget:'labelButton', props:{ text:'选择', action:'pickBranch' } },
    { field:'accountType', label:'账户类型', width:120, widget:'select', props:{ options:[{label:'普通',value:'ordinary'},{label:'当座',value:'checking'}] } },
    { field:'accountNo', label:'账号', width:160 },
    { field:'holder', label:'户名', width:140, props:{ filter:'katakana-half' } },
    { field:'effectiveDate', label:'生效日期', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' }
  ]
  columnsOverride.value['bankAccounts'] = bankColsLocal as any
  ensurePropsColumns('bankAccounts', bankColsLocal as any)
}
// 远程方法：部门/银行/支店
async function searchDepartments(query:string, done?:(opts:any[])=>void){
  const where:any[] = []
  const q = (query||'').trim()
  if (q){ where.push({ json:'name', op:'contains', value:q }); where.push({ field:'department_code', op:'contains', value:q }) }
  const r = await api.post('/objects/department/search', { page:1, pageSize:20, where, orderBy:[{field:'department_code',dir:'ASC'}] })
  const list = (r.data?.data||[]) as any[]
  const opts = list.map(x=>({ label: `${x.name||x.payload?.name||''} (${x.department_code||x.payload?.code||''})`, value: x.id }))
  if (done) done(opts)
  return opts
}
// 全量获取下拉选项
async function fetchDepartmentsAll(){
  let page=1; const pageSize=1000; let all:any[]=[]
  while(true){
    const r = await api.post('/objects/department/search', { page, pageSize, where:[], orderBy:[{field:'level',dir:'ASC'},{field:'order',dir:'ASC'},{field:'department_code',dir:'ASC'}] })
    const list = (r.data?.data||[]) as any[]; all = all.concat(list); if (list.length < pageSize) break; page++
  }
  return all.map(x=>{
    const name = x.name||x.payload?.name||''
    const code = x.department_code||x.payload?.code||''
    const lvlRaw = (typeof x.level==='number'? x.level : (typeof x.payload?.level==='number'? x.payload.level : undefined))
    const lvl = typeof lvlRaw === 'number' ? lvlRaw : (()=>{
      const p = (x.path || x.payload?.path || '') as string
      if (!p) return 0
      const segs = p.split('/').filter(Boolean)
      return segs.length
    })()
    const indent = lvl>0 ? '　'.repeat(lvl) : '' // 全角空格缩进
    return { label: `${indent}${name} (${code})`, value: x.id }
  })
}
async function fetchEmploymentTypesAll(){
  let page=1; const pageSize=1000; let all:any[]=[]
  while(true){
    const r = await api.post('/objects/employment_type/search', { page, pageSize, where:[], orderBy:[{field:'type_code',dir:'ASC'}] })
    const list = (r.data?.data||[]) as any[]; all = all.concat(list); if (list.length < pageSize) break; page++
  }
  return all.map(x=>({ label: `${x.payload?.name||x.name||x.payload?.code||''}`, value: x.payload?.code || x.type_code }))
}
// payrollItems 相关下拉已不再需要

// 将“给与/工资”tab切换为自然语言多行输入
function transformCompensationTab(){
  try{
    const layout:any[] = ui.value?.form?.layout || []
    const replaceInTabs=(arr:any[])=>{
      for (const node of arr){
        if (node?.type==='tabs' && Array.isArray(node.items)){
          for (const tab of node.items){
            if (!Array.isArray(tab.children)) continue
            const idx = tab.children.findIndex((c:any)=> c && c.field==='payrollItems')
            if (idx>=0){
              tab.children.splice(idx, 1, {
                field:'nlPayrollDescription',
                label:'工资构成（自然语言）',
                widget:'textarea',
                span: 24,
                props:{ rows:8, placeholder:'例：\n- 固定月薪50万円\n- 通勤手当は実費（上限2万円）\n- 厚生年金・健康保険（東京都・協会けんぽ）\n- 雇用保険は一般事業' }
              })
              return true
            }
          }
        } else if (node?.type==='section' && Array.isArray(node.layout)){
          if (replaceInTabs(node.layout)) return true
        }
      }
      return false
    }
    replaceInTabs(layout)
  }catch{}
}
async function fetchBanksAll(){
  let page=1; const pageSize=1000; let all:any[]=[]
  while(true){
    const r = await api.post('/objects/bank/search', { page, pageSize, where:[], orderBy:[{field:'bank_code',dir:'ASC'}] })
    const list = (r.data?.data||[]) as any[]; all = all.concat(list); if (list.length < pageSize) break; page++
  }
  return all.map(x=>({ label: `${x.name||x.payload?.name||''} (${x.bank_code||x.payload?.bankCode||''})`, value: x.payload?.name||x.name }))
}
async function fetchBranchesAll(){
  let page=1; const pageSize=1000; let all:any[]=[]
  while(true){
    const r = await api.post('/objects/branch/search', { page, pageSize, where:[], orderBy:[{field:'bank_code',dir:'ASC'}] })
    const list = (r.data?.data||[]) as any[]; all = all.concat(list); if (list.length < pageSize) break; page++
  }
  return all.map(x=>({ label: `${x.payload?.name||x.name||''}`, value: x.payload?.name||x.name }))
}
async function searchBanks(query:string, done?:(opts:any[])=>void){
  const where:any[] = []
  const q = (query||'').trim()
  if (q){ where.push({ json:'name', op:'contains', value:q }); where.push({ field:'bank_code', op:'contains', value:q }) }
  const r = await api.post('/objects/bank/search', { page:1, pageSize:20, where, orderBy:[{field:'bank_code',dir:'ASC'}] })
  const list = (r.data?.data||[]) as any[]
  const opts = list.map(x=>({ label: `${x.name||x.payload?.name||''} (${x.bank_code||x.payload?.bankCode||''})`, value: x.payload?.name||x.name }))
  if (done) done(opts)
  return opts
}
async function searchBranches(query:string, done?:(opts:any[])=>void){
  const where:any[] = []
  const q = (query||'').trim()
  if (q){ where.push({ json:'name', op:'contains', value:q }); where.push({ field:'bank_code', op:'contains', value:q }) }
  const r = await api.post('/objects/branch/search', { page:1, pageSize:20, where, orderBy:[{field:'bank_code',dir:'ASC'}] })
  const list = (r.data?.data||[]) as any[]
  const opts = list.map(x=>({ label: `${x.payload?.name||x.name||''}`, value: x.payload?.name||x.name }))
  if (done) done(opts)
  return opts
}

// 简单前端校验：返回错误列表
function validate(): string[] {
  const errs:string[] = []
  const p = model.value || {}
  const nameKanji = (p.nameKanji||'').toString().trim()
  const nameKana = (p.nameKana||'').toString().trim()
  if (!nameKanji && !nameKana) errs.push('姓名（汉字/假名）至少填写一项')
  const myNumber = (p.myNumber||'').toString().trim()
  if (myNumber && !/^\d{12}$/.test(myNumber)) errs.push('MyNumber 格式应为 12 位数字')
  // 合同起止
  for (const it of (p.contracts||[])){
    const a = it.periodFrom, b = it.periodTo
    if (a && b && a > b) errs.push('合同：开始日期不得晚于结束日期')
  }
  // 工资项
  for (const it of (p.payrollItems||[])){
    if (!it.startDate) errs.push('工资项：生效日期必填')
    if (it.amount!==undefined && it.amount!==null && isNaN(Number(it.amount))) errs.push('工资项：金额必须是数字')
  }
  // 部门履历
  for (const it of (p.departments||[])){
    if (!it.departmentId) errs.push('部门履历：部门ID/编码必填')
    const a = it.fromDate, b = it.toDate
    if (a && b && a > b) errs.push('部门履历：开始日期不得晚于结束日期')
  }
  // 银行账户
  for (const it of (p.bankAccounts||[])){
    if (!it.accountNo) errs.push('银行账户：账号必填')
  }
  return errs
}

async function handleSave(){
  const errs = validate()
  if (errs.length>0){ ElMessage.error(errs[0]); return }
  const payload = normalizeForSchema(model.value)
  await onSave(payload)
  ElMessage.success('保存成功')
}

function triggerUpload(){
  if (!id) { ElMessage.error('请先保存员工'); return }
  fileInput.value?.click()
}
async function onFileChosen(e: Event){
  const input = e.target as HTMLInputElement
  if (!input?.files || input.files.length===0) return
  if (!id) { ElMessage.error('请先保存员工'); return }
  const f = input.files[0]
  try{
    const name = (f as any).name || 'upload.bin'
    const r = await api.post(`/employees/${id}/attachments`, f, { headers: { 'Content-Type': (f as any).type || 'application/octet-stream', 'X-File-Name': encodeURIComponent(name) } })
    // 刷新本地模型（payload）
    const payload = r.data?.payload || r.data
    if (payload && Array.isArray(payload.attachments)) model.value.attachments = payload.attachments
    ElMessage.success('上传成功')
  }catch(e:any){ ElMessage.error(e?.response?.data?.error || e?.message || '上传失败') }
  finally{
    if (input) input.value = ''
  }
}

// 工资预览：可在需要时调用（留接口，后续加到单独页签）
async function loadPolicies(){
  if (policies.value.length>0) return
  try{
    let page=1; const pageSize=100; let all:any[]=[]
    while(true){
      const r = await api.post('/objects/payroll_policy/search', { page, pageSize, where:[], orderBy:[{ field:'policy_code', dir:'ASC'}] })
      const list = (r.data?.data||[]) as any[]; all = all.concat(list); if (list.length < pageSize) break; page++
    }
    policies.value = all.map(x=>({ label: `${x.payload?.name||x.name||x.payload?.code}`, value: x.id }))
  }catch{}
}

// 数组按钮事件：弹出银行/支店选择器
function onAction(name:string, payload:any){
  if (name==='pickBank') { currentRow.value = payload?.row || null; showBank.value = !!currentRow.value }
  if (name==='pickBranch') { currentRow.value = payload?.row || null; showBranch.value = !!currentRow.value }
  const p = String(payload?.arrayPath||'').split('.').pop()
  if (name==='__upload' && p==='attachments') { triggerUpload() }
}
function onPickBank(row:any){
  if (!currentRow.value) return
  currentRow.value.bank = row?.payload?.name || row?.payload?.bankName || row?.name || ''
  // 切换银行后清空支店
  currentRow.value.branch = ''
  showBank.value = false
}
function onPickBranch(row:any){
  if (!currentRow.value) return
  currentRow.value.branch = row?.payload?.branchName || row?.payload?.name || row?.branchName || ''
  showBranch.value = false
}

// 归一化日期格式为 YYYY-MM-DD，避免后端 format:date 校验失败
function normalizeForSchema(src:any){
  const copy = JSON.parse(JSON.stringify(src||{}))
  const toYmd = (v:any)=>{
    if (!v || typeof v!=='string') return v
    const m = v.trim().match(/^(\d{4})[\/.\-](\d{1,2})[\/.\-](\d{1,2})$/)
    if (!m) return v
    const mm = m[2].padStart(2,'0'); const dd = m[3].padStart(2,'0')
    return `${m[1]}-${mm}-${dd}`
  }
  // 标量日期
  if (copy.birthDate) copy.birthDate = toYmd(copy.birthDate)
  // 合同
  if (Array.isArray(copy.contracts)) for (const it of copy.contracts){ it.periodFrom = toYmd(it.periodFrom); it.periodTo = toYmd(it.periodTo) }
  // 部门履历
  if (Array.isArray(copy.departments)) for (const it of copy.departments){ it.fromDate = toYmd(it.fromDate); it.toDate = toYmd(it.toDate) }
  // 工资项改为自然语言字段，无需标准化数组
  // 银行账户
  if (Array.isArray(copy.bankAccounts)) for (const it of copy.bankAccounts){ it.effectiveDate = toYmd(it.effectiveDate) }
  // 附件
  if (Array.isArray(copy.attachments)) for (const it of copy.attachments){ it.uploadedAt = toYmd(it.uploadedAt) }
  // 生成员工编码（若未填写）
  if (!copy.code || typeof copy.code !== 'string' || !copy.code.trim()){
    const ts = Date.now().toString().slice(-8)
    copy.code = `E${ts}`
  }
  // 性别枚举规范化（若存在）
  if (copy.gender){
    const g = String(copy.gender).toLowerCase()
    if (g==='m' || g==='male' || g==='男') copy.gender = 'M'
    else if (g==='f' || g==='female' || g==='女') copy.gender = 'F'
    else copy.gender = 'O'
  }
  return copy
}
</script>


