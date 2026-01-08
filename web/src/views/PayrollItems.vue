<template>
  <div style="padding:12px">
    <div class="embed-header">
      <div class="embed-card-title">工资项目</div>
      <div class="embed-card-actions">
        <el-button size="small" type="primary" @click="openNew">新建</el-button>
        <el-button size="small" @click="load">刷新</el-button>
      </div>
    </div>
    <el-table :data="rows" size="small" border>
      <el-table-column prop="payload.code" label="代码" width="140" />
      <el-table-column prop="payload.name" label="名称" />
      <el-table-column prop="payload.kind" label="类别" width="140" />
      <el-table-column prop="payload.accountCode" label="会计科目" width="160" />
      <el-table-column prop="payload.isActive" label="启用" width="120">
        <template #default="{ row }">{{ row.payload?.isActive ? '是' : '否' }}</template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="show" title="新建工资项目" width="600px">
      <el-form label-width="80px">
        <el-form-item label="名称"><el-input v-model="form.name" /></el-form-item>
        <el-form-item label="类别"><el-select v-model="form.kind" style="width:100%"><el-option label="earning" value="earning"/><el-option label="deduction" value="deduction"/><el-option label="employer_cost" value="employer_cost"/></el-select></el-form-item>
        <el-form-item label="科目">
          <el-select v-model="form.accountCode" filterable remote reserve-keyword :remote-method="searchAccounts" placeholder="输入科目名/编号检索" style="width:100%">
            <el-option v-for="a in accountOptions" :key="a.value" :label="a.label" :value="a.value" />
          </el-select>
        </el-form-item>
        <el-form-item label="启用"><el-switch v-model="form.isActive" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="show=false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { ref } from 'vue'
import api from '../api'
const rows = ref<any[]>([])
const show = ref(false)
const saving = ref(false)
const form = ref<any>({ name:'', kind:'earning', accountCode:'', isActive:true })
const accountOptions = ref<any[]>([])
async function load(){
  const r = await api.post('/objects/payroll_item/search', { page:1, pageSize:200, where:[], orderBy:[{field:'item_code',dir:'ASC'}] })
  rows.value = r.data?.data || []
}
function openNew(){ form.value={ name:'', kind:'earning', accountCode:'', isActive:true }; show.value=true }
async function save(){
  if (!form.value.name) return
  saving.value=true
  try{ await api.post('/objects/payroll_item', { payload:{ ...form.value } }); show.value=false; await load() } finally { saving.value=false }
}
async function searchAccounts(query:string){
  const q = (query||'').trim()
  const where:any[] = []
  if (q){ where.push({ json:'name', op:'contains', value:q }); where.push({ field:'account_code', op:'contains', value:q }) }
  const r = await api.post('/objects/account/search', { page:1, pageSize:50, where, orderBy:[{ field:'account_code', dir:'ASC' }] })
  const list = (r.data?.data||[]) as any[]
  accountOptions.value = list.map(x=>({ label: `${x.payload?.name||x.name||''} (${x.account_code||x.payload?.code||''})`, value: x.account_code||x.payload?.code }))
}
load()
</script>


