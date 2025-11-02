<template>
  <div style="padding:12px">
    <el-card>
      <template #header>
        <div style="display:flex;justify-content:space-between;align-items:center">
          <div>员工清单</div>
          <div style="display:flex;gap:8px;align-items:center">
            <el-input v-model="keyword" placeholder="姓名/编码" size="small" style="width:220px" />
            <el-select v-model="deptId" filterable remote clearable reserve-keyword placeholder="所属部门" size="small" style="width:260px"
              :remote-method="searchDepartments" :loading="deptLoading">
              <el-option v-for="opt in deptOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
            </el-select>
            <el-button size="small" @click="load">搜索</el-button>
          </div>
        </div>
  <el-dialog v-model="showEdit" title="编辑员工" width="80%" append-to-body destroy-on-close>
    <EmployeeForm v-if="showEdit" :emp-id="editId" />
  </el-dialog>
      </template>
      <el-table :data="rows" size="small" border @row-dblclick="onEdit">
        <el-table-column type="index" width="60" />
        <el-table-column label="员工编码" prop="employee_code" width="140" />
        <el-table-column label="姓名（汉字）" prop="nameKanji" />
        <el-table-column label="姓名（假名）" prop="nameKana" />
        <el-table-column label="主所属部门" prop="primary_department_id" width="220" />
        <el-table-column label="操作" width="120">
          <template #default="{ row }">
            <el-button size="small" @click="openEdit(row.id)">编辑</el-button>
          </template>
        </el-table-column>
      </el-table>
      <div style="display:flex;justify-content:flex-end;margin-top:8px">
        <el-pagination layout="prev, pager, next" :page-size="pageSize" :total="total" @current-change="p=>{page=p;load()}" />
      </div>
    </el-card>
  </div>
  
</template>
<script setup lang="ts">
import { ref, onMounted } from 'vue'
import EmployeeForm from './EmployeeForm.vue'
const showEdit = ref(false)
const editId = ref<string>('')
import api from '../api'

const rows = ref<any[]>([])
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const keyword = ref('')
const deptId = ref<string | undefined>()
const deptOptions = ref<{label:string,value:string}[]>([])
const deptLoading = ref(false)

async function load(){
  const where:any[] = []
  const k = keyword.value.trim()
  if (k){ where.push({ json:'nameKanji', op:'contains', value:k }); where.push({ json:'nameKana', op:'contains', value:k }) }
  if (deptId.value){ where.push({ field:'primary_department_id', op:'eq', value: deptId.value }) }
  const r = await api.post('/objects/employee/search', { page:page.value, pageSize:pageSize.value, where, orderBy:[{field:'employee_code',dir:'ASC'}] })
  rows.value = r.data?.data || []
  total.value = r.data?.total ?? (rows.value.length < pageSize.value && page.value===1 ? rows.value.length : 0)
}

async function searchDepartments(query:string){
  deptLoading.value = true
  try{
    const where:any[] = []
    const q = (query||'').trim()
    if (q){
      where.push({ json:'name', op:'contains', value:q })
      where.push({ field:'department_code', op:'contains', value:q })
    }
    const r = await api.post('/objects/department/search', { page:1, pageSize:20, where, orderBy:[{field:'department_code',dir:'ASC'}] })
    const list = (r.data?.data || []) as any[]
    deptOptions.value = list.map(x=>{
      const name = x.name ?? x.payload?.name ?? ''
      const code = x.department_code ?? x.code ?? ''
      const id = x.id
      return { label: name ? `${name} (${code})` : `${code}`, value: id }
    })
  } finally {
    deptLoading.value = false
  }
}

onMounted(load)

function openEdit(id:string){ editId.value = id; showEdit.value = true }
function onEdit(row:any){ openEdit(row.id) }
</script>


