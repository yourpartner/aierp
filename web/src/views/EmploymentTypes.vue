<template>
  <div style="padding:12px">
    <div class="embed-header">
      <div class="embed-card-title">雇佣类型</div>
      <div class="embed-card-actions">
        <el-button size="small" type="primary" @click="openNew">新建</el-button>
        <el-button size="small" @click="load">刷新</el-button>
      </div>
    </div>
    <el-table :data="rows" size="small" border>
      <el-table-column prop="payload.code" label="代码" width="160" />
      <el-table-column prop="payload.name" label="名称" />
      <el-table-column prop="payload.isActive" label="启用" width="120">
        <template #default="{ row }">{{ row.payload?.isActive ? '是' : '否' }}</template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="show" title="新建雇佣类型" width="520px">
      <el-form label-width="80px">
        <el-form-item label="代码"><el-input v-model="form.code" /></el-form-item>
        <el-form-item label="名称"><el-input v-model="form.name" /></el-form-item>
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
const form = ref<any>({ code:'', name:'', isActive:true })
async function load(){
  const r = await api.post('/objects/employment_type/search', { page:1, pageSize:200, where:[], orderBy:[{field:'type_code',dir:'ASC'}] })
  rows.value = r.data?.data || []
}
function openNew(){ form.value={ code:'', name:'', isActive:true }; show.value=true }
async function save(){
  if (!form.value.code || !form.value.name) return
  saving.value=true
  try{ await api.post('/objects/employment_type', { payload:{ ...form.value } }); show.value=false; await load() } finally { saving.value=false }
}
load()
</script>


