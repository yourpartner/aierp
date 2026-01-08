<template>
  <div :class="['employee-form-root', { 'employee-form-root--bare': bare }]">
    <el-card :class="['employee-form-card', { 'employee-form-card--bare': bare }]">
      <template v-if="!bare" #header>{{ title }}</template>
      <template v-if="uiError">
        <el-result icon="error" title="読み込みに失敗しました" :sub-title="uiError" />
      </template>
      <template v-else>
        <DynamicForm
          v-if="!uiLoading && ui"
          :ui="ui"
          :schema="schemaDoc?.schema"
          :array-fields="['emergencies','contracts','departments','bankAccounts','attachments']"
          :columns-override="columnsOverride"
          :disable-array-add="['attachments']"
          :model="model"
          @save="onSave"
          @action="onAction"
          @tab-change="onTabChange"
        />
        <template v-else>
          <el-skeleton :rows="6" animated />
          <div v-if="!uiLoading && !ui" style="margin-top:8px">
            <el-alert type="warning" show-icon title="有効な UI 設定が取得できませんでした" description="/schemas/employee が ui.form.layout を返しているか確認してください。" />
            <el-collapse style="margin-top:6px">
              <el-collapse-item title="API 応答を表示">
                <pre style="white-space:pre-wrap;word-break:break-all;max-height:240px;overflow:auto">{{ raw }}</pre>
              </el-collapse-item>
            </el-collapse>
          </div>
        </template>
      </template>
      <div class="employment-type-manage-link" v-if="!uiLoading && ui">
        <el-link
          v-if="activeTab === '雇用契約'"
          type="primary"
          @click="showEmploymentTypeManager = true"
        >
          雇用区分を管理
        </el-link>
      </div>
      <div style="display:flex;justify-content:flex-end;margin-top:10px" v-if="!uiLoading && ui">
        <el-button type="primary" @click="handleSave">保存</el-button>
      </div>
      
    </el-card>

    <!-- 银行/支店选择弹窗（与会计科目一致） -->
    <el-dialog v-model="showBank" title="銀行を選択" width="720px">
      <BankBranchPicker mode="bank" @select="onPickBank" @cancel="showBank=false" />
    </el-dialog>
    <el-dialog v-model="showBranch" title="支店を選択" width="720px">
      <BankBranchPicker mode="branch" @select="onPickBranch" @cancel="showBranch=false" />
    </el-dialog>
    <el-dialog v-model="showEmploymentTypeManager" title="雇用区分の管理" width="560px" append-to-body>
      <div style="margin-bottom:12px; display:flex; gap:8px; align-items:center">
        <el-input
          v-model="newEmploymentTypeLabel"
          placeholder="新しい雇用区分を入力"
          style="flex:1"
        />
        <el-button type="primary" @click="addEmploymentTypeFromManager" :disabled="!newEmploymentTypeLabel.trim()">追加</el-button>
      </div>
      <el-table :data="employmentTypeOptions" size="small" border style="width:100%" height="320px">
        <el-table-column label="名称">
          <template #default="{ row }">
            <el-input v-model="row.editLabel" placeholder="雇用区分" />
          </template>
        </el-table-column>
        <el-table-column label="操作" width="180">
          <template #default="{ row }">
            <el-button size="small" type="primary" @click="saveEmploymentTypeRow(row)">更新</el-button>
            <el-button size="small" type="danger" @click="deleteEmploymentTypeRow(row)">削除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-dialog>
  </div>
  <!-- 全局隐藏文件选择，用于附件tab的“上传附件”按钮触发 -->
  <input type="file" ref="fileInput" style="display:none" @change="onFileChosen" />
  
</template>
<script setup lang="ts">
import { ref, onMounted, defineExpose, computed, watch } from 'vue'
import { useRoute } from 'vue-router'
import api from '../api'
import DynamicForm from '../components/DynamicForm.vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import BankBranchPicker from '../components/BankBranchPicker.vue'

const props = defineProps<{ empId?: string; bare?: boolean }>()
const route = useRoute()
const id = (props.empId || (route.params as any).id) as string | undefined
const title = computed(() => (id ? '社員編集' : '社員登録'))
const bare = computed(() => props.bare === true)
const ui = ref<any>(null)
const schemaDoc = ref<any>(null)
const uiLoading = ref(false)
const uiError = ref('')
const model = ref<any>({})
const raw = ref('')
const columnsOverride = ref<Record<string, any[]>>({})
const activeTab = ref<string>('基本情報')
const showBank = ref(false)
const showBranch = ref(false)
const currentRow = ref<any>(null)
type EmploymentTypeOption = { label: string; value: string; id?: string; code?: string; editLabel?: string }

const employmentTypeOptions = ref<EmploymentTypeOption[]>([])
const employmentTypeValueSet = new Set<string>()
const employmentTypeSaving = new Set<string>()
const showEmploymentTypeManager = ref(false)
const newEmploymentTypeLabel = ref('')
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
    if (!ui.value) throw new Error('schema.ui が空です')
    if (!Array.isArray(lay) || lay.length===0) throw new Error('schema.ui.form.layout が空です')
    // 补充数组列定义，便于 DynamicForm 渲染表格列
    // 预取：部门下拉与雇佣类型下拉
    const deptOpts = await fetchDepartmentsAll()
    await fetchEmploymentTypesAll()
    augmentArrayColumns()
    ensureGenderSelect()
    transformCompensationTab()
    enhanceTopLevelFields(deptOpts, employmentTypeOptions.value)
    localizeFormTabs()
    localizeContactFields()
    enforceBasicInfoTabRules()
  }catch(e:any){ uiError.value = e?.response?.data?.error || e?.message || 'スキーマを読み込めませんでした' }
  finally { uiLoading.value = false }
}


async function loadData(){
  if (!id) return
  const r = await api.get(`/objects/employee/${id}`)
  model.value = r.data?.payload || r.data || {}
  refreshAttachmentDisplays()
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
    if (!uiError.value) uiError.value = '読み込みがタイムアウトしたか /schemas/employee が応答しません'
  }
}, 8000)

watch(() => model.value.contracts, (contracts) => {
  if (!Array.isArray(contracts)) return
  for (const item of contracts) {
    ensureEmploymentTypeOption(item?.employmentTypeCode)
  }
}, { deep: true })

// 为数组字段补充列定义（在现有 ui 上就地注入 props.columns）
function augmentArrayColumns(){
  const cfgs:{path:string, cols:any[]}[] = [
    { path:'emergencies', cols:[
      { field:'nameKanji', label:'氏名（漢字）', width:140 },
      { field:'nameKana', label:'氏名（カナ）', width:140 },
      { field:'relation', label:'続柄', width:100 },
      { field:'phone', label:'電話番号', width:140 },
      { field:'address', label:'住所' },
      { field:'note', label:'備考' }
    ]},
    { path:'contracts', cols:[
      { field:'contractType', label:'契約区分', width:120 },
      { field:'periodFrom', label:'開始日', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
      { field:'periodTo', label:'終了日', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
      { field:'note', label:'備考' }
    ]},
    // payrollItems 已由自然语言字段替代
    { path:'departments', cols:[
      { field:'departmentId', label:'部門ID/コード', width:180 },
      { field:'fromDate', label:'開始日', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
      { field:'toDate', label:'終了日', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
      { field:'position', label:'役職', width:140 }
    ]},
    { path:'bankAccounts', cols:[
      { field:'bank', label:'銀行', width:140 },
      { field:'branch', label:'支店', width:140 },
      { field:'accountType', label:'口座種別', width:120 },
      { field:'accountNo', label:'口座番号', width:160 },
      { field:'holder', label:'口座名義', width:140 },
      { field:'effectiveDate', label:'適用開始日', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' }
    ]},
    { path:'attachments', cols:[
      {
        field:'fileName',
        label:'ファイル名',
        width:260,
        widget:'label'
      },
      {
        field:'uploadedAtDisplay',
        label:'アップロード日時',
        width:200,
        widget:'label',
        props:{ placeholder:'-' }
      },
      {
        field:'size',
        label:'サイズ（バイト）',
        width:140,
        widget:'label',
        props:{ placeholder:'-' }
      },
      {
        field:'url',
        label:'URL',
        width:260,
        widget:'labelButton',
        props:{ text:'リンクを開く', action:'openAttachment', type:'primary' }
      }
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
function formatAttachmentTimestampValue(value:any) {
  if (!value) return ''
  try {
    const date = new Date(value)
    if (Number.isNaN(date.getTime())) return value
    const y = date.getFullYear()
    const m = String(date.getMonth() + 1).padStart(2, '0')
    const d = String(date.getDate()).padStart(2, '0')
    const hh = String(date.getHours()).padStart(2, '0')
    const mm = String(date.getMinutes()).padStart(2, '0')
    return `${y}-${m}-${d} ${hh}:${mm}`
  } catch {
    return value
  }
}

// 将性别渲染为下拉
function ensureGenderSelect(){
  const layout:any[] = ui.value?.form?.layout || []
  const opts = [ { label:'男性', value:'M' }, { label:'女性', value:'F' } ]
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
  const labelMap: Record<string, string> = {
    nameKanji: '氏名（漢字）',
    nameKana: '氏名（カナ）',
    gender: '性別',
    birthDate: '生年月日',
    nationality: '国籍',
    myNumber: '個人番号'
  }
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
  attachField('nameKanji', (n)=>{ n.label = labelMap.nameKanji; ensureInputWithPlaceholder(n, '氏名（漢字）') })
  // 姓名（假名）全角片假名
  attachField('nameKana', (n)=>{ n.label = labelMap.nameKana; n.props = n.props || {}; n.props.filter = 'katakana-zen'; ensurePlaceholder(n, '氏名（カナ）') })
  attachField('gender', (n)=>{
    n.label = labelMap.gender
    n.widget = 'select'
    n.props = n.props || {}
    n.props.options = [{ label:'男性', value:'M' }, { label:'女性', value:'F' }]
    n.props.placeholder = n.props.placeholder || '選択'
  })
  // 生日日期控件
  attachField('birthDate', (n)=>{ n.label = labelMap.birthDate; n.widget='date'; n.props = n.props || {}; n.props.type='date'; n.props.valueFormat='YYYY-MM-DD'; n.props.placeholder = n.props.placeholder || '日付を選択' })
  // 国籍下拉（示例国家，可替换为后端枚举）
  attachField('nationality', (n)=>{ n.label = labelMap.nationality; n.widget='select'; n.props = n.props || {}; n.props.options=[{label:'日本',value:'JP'},{label:'中国',value:'CN'},{label:'アメリカ',value:'US'}]; n.props.placeholder = n.props.placeholder || '選択' })
  attachField('myNumber', (n)=>{ n.label = labelMap.myNumber; ensurePlaceholder(n, '数字のみ（12桁）') })
  // 合同-雇佣类型：替代旧的合同类型输入
  ensurePropsColumns('contracts', [
    {
      field:'employmentTypeCode',
      label:'雇用区分',
      width:200,
      widget:'select',
      props:{
        filterable:true,
        clearable:true,
        allowCreate:true,
        defaultFirstOption:true,
        reserveKeyword:true,
        options: empTypeOpts
      }
    },
    { field:'periodFrom', label:'開始日', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
    { field:'periodTo', label:'終了日', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
    { field:'note', label:'備考' }
  ])
  // 紧急联系人关系列下拉
  ensurePropsColumns('emergencies', [
    { field:'nameKanji', label:'氏名（漢字）', width:140 },
    { field:'nameKana', label:'氏名（カナ）', width:140, inputType:'text', props:{ filter:'katakana-zen' } },
    { field:'relation', label:'続柄', width:120, widget:'select', props:{ options:[{label:'両親',value:'parent'},{label:'配偶者',value:'spouse'},{label:'子ども',value:'child'},{label:'友人',value:'friend'}] } },
    { field:'phone', label:'電話番号', width:140 },
    { field:'address', label:'住所' },
    { field:'note', label:'備考' }
  ])
  // 部门履历中的部门：恢复为静态下拉（已预加载 deptOpts）
  const deptColsLocal = [
    { field:'departmentId', label:'部門', width:240, widget:'select', props:{ filterable:true, clearable:true, options: deptOpts } },
    { field:'fromDate', label:'開始日', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
    { field:'toDate', label:'終了日', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' },
    { field:'position', label:'役職', width:140 }
  ]
  // 通过显式覆盖强制渲染
  columnsOverride.value['departments'] = deptColsLocal as any
  ensurePropsColumns('departments', deptColsLocal as any)
  // 工资项目：改为自然语言描述，不再渲染项目列表
  // 银行账户：银行/支店同列显示为“文本+按钮”
  const bankColsLocal = [
    { field:'bank', label:'銀行', width:260, widget:'labelButton', props:{ text:'選択', action:'pickBank', type:'primary' } },
    { field:'branch', label:'支店', width:260, widget:'labelButton', props:{ text:'選択', action:'pickBranch' } },
    { field:'accountType', label:'口座種別', width:120, widget:'select', props:{ options:[{label:'普通',value:'ordinary'},{label:'当座',value:'checking'}] } },
    { field:'accountNo', label:'口座番号', width:160 },
    { field:'holder', label:'口座名義', width:140, props:{ filter:'katakana-half' } },
    { field:'effectiveDate', label:'適用開始日', width:120, inputType:'date', valueFormat:'YYYY-MM-DD' }
  ]
  columnsOverride.value['bankAccounts'] = bankColsLocal as any
  ensurePropsColumns('bankAccounts', bankColsLocal as any)
}
function localizeFormTabs(){
  const layout:any[] = ui.value?.form?.layout || []
  const tabTitleMap: Record<string, string> = {
    '基本': '基本情報',
    '联络': '連絡先',
    '紧急联络': '緊急連絡先',
    '雇佣契约': '雇用契約',
    '给与': '給与',
    '所属部门': '所属部門',
    '银行账户': '銀行口座',
    '附件': '添付書類'
  }
  const walk = (nodes:any[]) => {
    for (const node of nodes) {
      if (!node) continue
      if (node.type === 'tabs' && Array.isArray(node.items)) {
        for (const item of node.items) {
          if (typeof item?.title === 'string' && tabTitleMap[item.title]) {
            item.title = tabTitleMap[item.title]
          }
          if (Array.isArray(item?.children)) {
            walk(item.children)
          }
        }
      } else if (node.type === 'section' && Array.isArray(node.layout)) {
        walk(node.layout)
      } else if (Array.isArray(node.cols)) {
        walk(node.cols)
      }
    }
  }
  walk(layout)
}
function localizeContactFields(){
  const layout:any[] = ui.value?.form?.layout || []
  const labelMap: Record<string, string> = {
    'contact.phone': '電話番号',
    'contact.email': 'メールアドレス',
    'contact.postalCode': '郵便番号',
    'contact.address': '住所',
    'contact.note': '備考'
  }
  const walk = (nodes:any[]) => {
    for (const node of nodes) {
      if (!node) continue
      if (node.field && labelMap[node.field]) {
        node.label = labelMap[node.field]
      }
      if (node.type === 'grid' && Array.isArray(node.cols)) {
        walk(node.cols)
      } else if (node.type === 'section' && Array.isArray(node.layout)) {
        walk(node.layout)
      } else if (node.type === 'tabs' && Array.isArray(node.items)) {
        for (const tab of node.items) {
          if (Array.isArray(tab?.children)) walk(tab.children)
        }
      }
    }
  }
  walk(layout)
}
function enforceBasicInfoTabRules(){
  try{
    const layout:any[] = ui.value?.form?.layout || []
    if (!Array.isArray(layout)) return
    const targetTitles = new Set(['基本','基本情報'])
    const requiredFields = new Set(['nameKanji','nameKana','gender','birthDate','nationality'])
    const handleTab = (tab:any)=>{
      if (!Array.isArray(tab?.children)) return
      const children = tab.children
      const idxKanji = children.findIndex((c:any)=>c?.field==='nameKanji')
      const idxKana = children.findIndex((c:any)=>c?.field==='nameKana')
      if (idxKanji === 0 && idxKana === 1){
        const tmp = children[0]
        children[0] = children[1]
        children[1] = tmp
      }
      for (const child of children){
        if (!child) continue
        child.props = child.props || {}
        if (requiredFields.has(child.field)){
          child.required = true
          child.props.required = true
        } else if (child.field === 'myNumber'){
          child.required = false
          delete child.props.required
        }
      }
    }
    const walk=(nodes:any[])=>{
      for (const node of nodes){
        if (!node) continue
        if (node.type==='tabs' && Array.isArray(node.items)){
          for (const tab of node.items){
            if (typeof tab?.title === 'string' && targetTitles.has(tab.title)){
              handleTab(tab)
            }
          }
        } else if (node.type==='section' && Array.isArray(node.layout)){
          walk(node.layout)
        } else if (Array.isArray(node.cols)){
          walk(node.cols)
        }
      }
    }
    walk(layout)
  }catch{}
}
function onTabChange(payload: { title?: string }) {
  if (payload?.title) {
    activeTab.value = payload.title
  }
}
function localizeArrayColumnButtons(){
  const columnTextMap: Record<string, string> = {
    emergencies: '削除',
    bankAccounts: '削除',
    attachments: '削除',
    departments: '削除',
    contracts: '削除'
  }
  for (const key of Object.keys(columnTextMap)){
    const cols = columnsOverride.value[key]
    if (Array.isArray(cols)){
      cols.forEach(col => {
        if (col.widget === 'labelButton'){
          col.props = col.props || {}
          if (!col.props.text) col.props.text = '選択'
        }
        if (!col.removeText && typeof col === 'object'){
          col.removeText = columnTextMap[key]
        }
      })
    }
  }
}
function ensurePlaceholder(node:any, text:string){
  node.props = node.props || {}
  if (!node.props.placeholder) node.props.placeholder = text
}
function ensureInputWithPlaceholder(node:any, text:string){
  node.widget = node.widget || 'input'
  ensurePlaceholder(node, text)
}
function resetEmploymentTypeOptionsFromRecords(records: any[]) {
  employmentTypeValueSet.clear()
  employmentTypeOptions.value.splice(0, employmentTypeOptions.value.length)
  const list: EmploymentTypeOption[] = []
  for (const item of records) {
    const label = (item?.payload?.name || item?.payload?.code || item?.name || item?.type_code || '').toString().trim()
    if (!label || employmentTypeValueSet.has(label)) continue
    employmentTypeValueSet.add(label)
    list.push({
      label,
      value: label,
      id: item?.id,
      code: item?.payload?.code || item?.type_code
    })
  }
  employmentTypeOptions.value.push(...list)
}

function addEmploymentTypeOptionLocal(label: string, meta?: { id?: string; code?: string }) {
  const val = (label || '').trim()
  if (!val || employmentTypeValueSet.has(val)) return
  employmentTypeValueSet.add(val)
  employmentTypeOptions.value.push({ label: val, value: val, id: meta?.id, code: meta?.code })
}
function ensureEmploymentTypeOption(value?: string) {
  const val = (value || '').trim()
  if (!val || employmentTypeValueSet.has(val)) return
  addEmploymentTypeOptionLocal(val)
  queueEmploymentTypePersist(val)
}
function queueEmploymentTypePersist(value: string) {
  if (employmentTypeSaving.has(value)) return
  employmentTypeSaving.add(value)
  persistEmploymentType(value).finally(() => employmentTypeSaving.delete(value))
}
async function persistEmploymentType(value: string) {
  const payload = {
    code: buildEmploymentTypeCode(value),
    name: value,
    isActive: true
  }
  try{
    await api.post('/objects/employment_type', { payload })
    await fetchEmploymentTypesAll()
  }catch{}
}
function buildEmploymentTypeCode(label: string) {
  const normalized = label
    .normalize?.('NFKC')
    .replace(/[\s]+/g, '_')
    .replace(/[^\w\-]/g, '_')
    .replace(/_+/g, '_')
    .replace(/^_+|_+$/g, '')
    .toUpperCase()
  if (normalized) return normalized.slice(0, 32)
  return `TYPE_${Date.now()}`
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
  let page=1
  const pageSize=1000
  let all:any[]=[]
  try{
    while(true){
      const r = await api.post('/objects/employment_type/search', { page, pageSize, where:[], orderBy:[{field:'type_code',dir:'ASC'}] })
      const list = (r.data?.data||[]) as any[]
      all = all.concat(list)
      if (list.length < pageSize) break
      page++
    }
  }catch{}
  resetEmploymentTypeOptionsFromRecords(all)
  syncEmploymentTypeEditLabels()
  return employmentTypeOptions.value
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
                label:'給与構成（自由記述）',
                widget:'textarea',
                span: 24,
                props:{ rows:8, placeholder:'例：\n- 固定月給50万円\n- 通勤手当は実費（上限2万円）\n- 厚生年金・健康保険（東京都・協会けんぽ）\n- 雇用保険は一般事業' }
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
  const requiredFields = [
    { field:'nameKanji', label:'氏名（漢字）' },
    { field:'nameKana', label:'氏名（カナ）' },
    { field:'gender', label:'性別' },
    { field:'birthDate', label:'生年月日' },
    { field:'nationality', label:'国籍' }
  ]
  for (const item of requiredFields){
    const val = (p[item.field] ?? '').toString().trim()
    if (!val) errs.push(`${item.label}は必須です`)
  }
  const myNumber = (p.myNumber||'').toString().trim()
  if (myNumber && !/^\d{12}$/.test(myNumber)) errs.push('マイナンバーは12桁の数字で入力してください')
  // 合同起止
  for (const it of (p.contracts||[])){
    const a = it.periodFrom, b = it.periodTo
    if (a && b && a > b) errs.push('契約：開始日は終了日より後にできません')
  }
  // 工资项
  for (const it of (p.payrollItems||[])){
    if (!it.startDate) errs.push('給与項目：適用開始日は必須です')
    if (it.amount!==undefined && it.amount!==null && isNaN(Number(it.amount))) errs.push('給与項目：金額は数値で入力してください')
  }
  // 部门履历
  for (const it of (p.departments||[])){
    if (!it.departmentId) errs.push('部門履歴：部門ID／コードは必須です')
    const a = it.fromDate, b = it.toDate
    if (a && b && a > b) errs.push('部門履歴：開始日は終了日より後にできません')
  }
  // 银行账户
  for (const it of (p.bankAccounts||[])){
    if (!it.accountNo) errs.push('銀行口座：口座番号は必須です')
  }
  return errs
}

async function handleSave(){
  const errs = validate()
  if (errs.length>0){ ElMessage.error(errs[0]); return }
  const payload = normalizeForSchema(model.value)
  await onSave(payload)
  ElMessage.success('保存しました')
}

function syncEmploymentTypeEditLabels() {
  employmentTypeOptions.value.forEach(opt => {
    opt.editLabel = opt.label
  })
}

async function addEmploymentTypeFromManager() {
  const label = newEmploymentTypeLabel.value.trim()
  if (!label) return
  if (employmentTypeValueSet.has(label)) {
    ElMessage.error('同じ雇用区分が既に存在します')
    return
  }
  addEmploymentTypeOptionLocal(label)
  newEmploymentTypeLabel.value = ''
  await persistEmploymentType(label)
  await fetchEmploymentTypesAll()
}

async function saveEmploymentTypeRow(row: EmploymentTypeOption) {
  const newLabel = (row.editLabel || '').trim()
  if (!newLabel) {
    ElMessage.error('名称を入力してください')
    return
  }
  if (newLabel === row.label) {
    ElMessage.success('変更はありません')
    return
  }
  if (employmentTypeValueSet.has(newLabel) && newLabel !== row.label) {
    ElMessage.error('同じ雇用区分が既に存在します')
    syncEmploymentTypeEditLabels()
    return
  }
  try {
    if (row.id) {
      await api.put(`/objects/employment_type/${row.id}`, {
        payload: {
          code: row.code || buildEmploymentTypeCode(newLabel),
          name: newLabel,
          isActive: true
        }
      })
    } else {
      await persistEmploymentType(newLabel)
    }
    replaceContractEmploymentType(row.value, newLabel)
    await fetchEmploymentTypesAll()
    ElMessage.success('更新しました')
  } catch (e:any) {
    ElMessage.error(e?.response?.data?.error || '更新に失敗しました')
  }
}

async function deleteEmploymentTypeRow(row: EmploymentTypeOption) {
  try {
    await ElMessageBox.confirm('この雇用区分を削除しますか？', '確認', { type: 'warning' })
  } catch {
    return
  }
  try {
    if (row.id) {
      await api.delete(`/objects/employment_type/${row.id}`)
    }
    removeEmploymentTypeLocal(row.value)
    replaceContractEmploymentType(row.value, '')
    await fetchEmploymentTypesAll()
    ElMessage.success('削除しました')
  } catch (e:any) {
    ElMessage.error(e?.response?.data?.error || '削除に失敗しました')
  }
}

function removeEmploymentTypeLocal(value: string) {
  const idx = employmentTypeOptions.value.findIndex(opt => opt.value === value)
  if (idx >= 0) employmentTypeOptions.value.splice(idx, 1)
  employmentTypeValueSet.delete(value)
}

function replaceContractEmploymentType(oldValue: string, newValue: string) {
  const list = model.value?.contracts
  if (!Array.isArray(list)) return
  list.forEach(item => {
    if (item?.employmentTypeCode === oldValue) {
      item.employmentTypeCode = newValue
    }
  })
}

watch(showEmploymentTypeManager, (visible) => {
  if (visible) {
    syncEmploymentTypeEditLabels()
  }
})

function triggerUpload(){
  if (!id) { ElMessage.error('先に社員情報を保存してください'); return }
  fileInput.value?.click()
}
async function onFileChosen(e: Event){
  const input = e.target as HTMLInputElement
  if (!input?.files || input.files.length===0) return
  if (!id) { ElMessage.error('先に社員情報を保存してください'); return }
  const f = input.files[0]
  try{
    const name = (f as any).name || unnamedFileLabel()
    const r = await api.post(`/employees/${id}/attachments`, f, { headers: { 'Content-Type': (f as any).type || 'application/octet-stream', 'X-File-Name': encodeURIComponent(name) } })
    // 刷新本地模型（payload）
    const payload = r.data?.payload || r.data
    if (payload && Array.isArray(payload.attachments)) {
      model.value.attachments = payload.attachments
      refreshAttachmentDisplays()
    }
    ElMessage.success('アップロードしました')
  }catch(e:any){ ElMessage.error(e?.response?.data?.error || e?.message || 'アップロードに失敗しました') }
  finally{
    if (input) input.value = ''
  }

function refreshAttachmentDisplays() {
  const list = Array.isArray(model.value?.attachments) ? model.value.attachments : []
  list.forEach((item:any) => {
    const raw = item?.uploadedAt || item?.uploaded_at
    item.uploadedAtDisplay = formatAttachmentTimestampValue(raw)
  })
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
  if (name==='openAttachment') {
    const url = payload?.row?.url
    if (url) window.open(url, '_blank')
  }
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
    if (['m','male','男','男性'].includes(g)) copy.gender = 'M'
    else if (['f','female','女','女性'].includes(g)) copy.gender = 'F'
    else copy.gender = 'O'
  }
  return copy
}
</script>

<style scoped>
.employee-form-root {
  padding: 12px;
}
.employee-form-root--bare {
  padding: 0;
}
.employee-form-card--bare {
  box-shadow: none;
  border: none;
  background: transparent;
}
.employee-form-card--bare :deep(.el-card__body) {
  padding: 0;
}
.employment-type-manage-link {
  margin-top: 8px;
}
.df-plain-input :deep(.el-input__wrapper) {
  padding: 0;
  box-shadow: none;
  border: none;
  background: transparent;
}
.df-plain-input :deep(.el-input__inner) {
  color: #606266;
  font-size: 13px;
  padding: 0;
}
</style>

