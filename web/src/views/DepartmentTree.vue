<template>
  <div style="padding:12px; display:flex; gap:12px; align-items:flex-start">
    <el-card style="flex:1">
      <template #header>
        <div style="display:flex;justify-content:space-between;align-items:center">
          <div>部门层级</div>
          <div style="display:flex;gap:8px">
            <el-button size="small" type="primary" @click="openCreate">新增部门</el-button>
            <el-button size="small" @click="load">刷新</el-button>
          </div>
        </div>
      </template>
      <el-input v-model="keyword" placeholder="搜索部门名称或编码" size="small" style="width:260px;margin-bottom:8px" @input="filterTree" />
      <el-tree
        ref="treeRef"
        :data="treeData"
        node-key="id"
        :props="{ label: 'label', children: 'children' }"
        draggable
        :allow-drop="allowDrop"
        @node-drop="onDrop"
        highlight-current
        default-expand-all
      />
    </el-card>
    <el-dialog v-model="showCreate" title="新增部门" width="480px" append-to-body>
      <el-form :model="createForm" label-width="90px">
        <el-form-item label="编码">
          <el-input v-model="createForm.code" maxlength="50" />
        </el-form-item>
        <el-form-item label="名称">
          <el-input v-model="createForm.name" maxlength="200" />
        </el-form-item>
        <el-form-item label="上级部门">
          <el-select v-model="createForm.parentCode" filterable remote clearable reserve-keyword placeholder="可输入名称/编码模糊搜索" :remote-method="searchParents" :loading="parentLoading" style="width:100%">
            <el-option v-for="opt in parentOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showCreate=false">取消</el-button>
        <el-button type="primary" @click="createDepartment">保存</el-button>
      </template>
    </el-dialog>
    <el-card style="width:420px">
      <template #header>详情</template>
      <div v-if="current">
        <div style="margin:6px 0">编码：{{ current.code }}</div>
        <div style="margin:6px 0">名称：{{ current.name }}</div>
        <div style="margin:6px 0">上级：{{ current.parentCode || '—' }}</div>
        <div style="margin:6px 0">层级：{{ current.level }}</div>
        <div style="margin:6px 0">顺序：{{ current.order }}</div>
      </div>
      <div v-else style="color:#6b7280">（请选择左侧部门）</div>
    </el-card>
  </div>
</template>
<script setup lang="ts">
import { ref, reactive, onMounted, nextTick } from 'vue'
import api from '../api'

const treeRef = ref()
const rows = ref<any[]>([])
const treeData = ref<any[]>([])
const current = ref<any>(null)
const keyword = ref('')
const showCreate = ref(false)
const createForm = reactive<{ code:string; name:string; parentCode?:string | null }>({ code:'', name:'', parentCode: undefined })
const parentOptions = ref<{label:string,value:string}[]>([])
const parentLoading = ref(false)

async function load(){
  await ensureDepartmentSchema()
  const r = await api.post('/objects/department/search', { page:1, pageSize:1000, where:[], orderBy:[{field:'department_code',dir:'ASC'}] })
  rows.value = r.data?.data || []
  buildTree()
}

async function ensureDepartmentSchema(){
  try{
    await api.get('/schemas/department')
  }catch{
    const schema:any = { type:'object', properties:{ code:{type:'string'}, name:{type:'string'}, parentCode:{type:['string','null']}, path:{type:'string'}, level:{type:'integer'}, order:{type:'integer'}, status:{type:'string', enum:['active','inactive'], default:'active'} }, required:['code','name'] }
    const query:any = { filters:['department_code','name','parentCode','path','level','order','status'], sorts:['department_code','name','level','order'] }
    const ui:any = { list:{ columns:['department_code','name','parentCode','level','order','status'] }, form:{ layout:[{ type:'section', title:'基本信息', layout:[{ type:'grid', cols:[ { field:'code', label:'部门编码', span:8 }, { field:'name', label:'部门名称', span:8 }, { field:'parentCode', label:'上级部门', span:8 }, { field:'status', label:'状态', span:6, widget:'select', props:{ options:[{label:'有效', value:'active'},{label:'无效', value:'inactive'}] } } ] }]}] } }
    try{ await api.post('/schemas/department', { schema, ui, query }) }catch{}
  }
}

function buildTree(){
  const list = rows.value.map((x:any)=>({
    id: x.id,
    code: x.department_code || x.payload?.code,
    name: x.name || x.payload?.name,
    parentCode: x.parentCode || x.payload?.parentCode || null,
    path: x.payload?.path || '',
    level: x.payload?.level || 0,
    order: x.payload?.order || 0,
    label: `${x.name || x.payload?.name} (${x.department_code || x.payload?.code})`,
    children: [] as any[]
  }))
  const codeToNode = new Map(list.map((n:any)=>[n.code,n]))
  const roots:any[] = []
  for (const n of list){
    if (n.parentCode && codeToNode.has(n.parentCode)) codeToNode.get(n.parentCode).children.push(n)
    else roots.push(n)
  }
  treeData.value = roots
}

function allowDrop(drag:any, drop:any, type:string){
  // 禁止拖到自己或子孙
  const dragPath = drag.data.path || ''
  const dropPath = drop.data.path || ''
  if (dropPath.startsWith(dragPath)) return false
  return type !== 'prev' // 简化：允许内部或之后
}

async function onDrop(drag:any, drop:any, type:any){
  const newParentCode = type==='inner' ? (drop.data.code||null) : (drop.parent?.data?.code || null)
  const newOrder = undefined // 可扩展：根据位置传序号
  await api.post('/operations/department/reparent', { departmentId: drag.data.id, newParentCode, newOrder })
  await load();
  await nextTick();
  const node = (treeRef.value as any).getNode(drag.data.id)
  if (node) (treeRef.value as any).setCurrentKey(drag.data.id)
}

function filterTree(){
  const q = keyword.value.trim().toLowerCase()
  if (!q){ buildTree(); return }
  const filtered = rows.value.filter((x:any)=>{
    const code = (x.department_code || x.payload?.code || '').toLowerCase()
    const name = (x.name || x.payload?.name || '').toLowerCase()
    return code.includes(q) || name.includes(q)
  })
  const old = rows.value
  rows.value = filtered; buildTree(); rows.value = old
}

onMounted(load)

function openCreate(){
  // 若选中当前节点，默认其为上级
  createForm.code=''
  createForm.name=''
  createForm.parentCode = current.value?.code || null
  showCreate.value = true
}

async function searchParents(query:string){
  parentLoading.value = true
  try{
    const where:any[] = []
    const q = (query||'').trim()
    if (q){ where.push({ json:'name', op:'contains', value:q }); where.push({ field:'department_code', op:'contains', value:q }) }
    const r = await api.post('/objects/department/search', { page:1, pageSize:20, where, orderBy:[{field:'department_code',dir:'ASC'}] })
    const list = (r.data?.data || []) as any[]
    parentOptions.value = list.map(x=>{
      const name = x.name ?? x.payload?.name ?? ''
      const code = x.department_code ?? x.payload?.code ?? ''
      return { label: name ? `${name} (${code})` : `${code}`, value: code }
    })
  } finally { parentLoading.value = false }
}

async function createDepartment(){
  const code = createForm.code?.trim()
  const name = createForm.name?.trim()
  if (!code || !name) return
  const payload:any = { code, name }
  if (createForm.parentCode) payload.parentCode = createForm.parentCode
  // 初始化 path/level，便于树和拖拽逻辑
  payload.path = createForm.parentCode ? `${createForm.parentCode}/${code}` : code
  payload.level = (payload.path.split('/').length)
  await api.post('/objects/department', { payload })
  showCreate.value = false
  await load()
  await nextTick()
}
</script>


