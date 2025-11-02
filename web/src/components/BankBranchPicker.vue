<template>
  <div>
    <el-input v-model="keyword" placeholder="按名称或编号检索" @input="onSearch" clearable style="margin-bottom:8px" />
    <el-table :data="rows" height="260" @row-click="rowClick">
      <el-table-column prop="payload.bankCode" label="银行代码" width="120" />
      <el-table-column prop="payload.name" label="银行名称" min-width="180" />
      <el-table-column v-if="mode==='branch'" prop="payload.branchCode" label="支店代码" width="120" />
      <el-table-column v-if="mode==='branch'" prop="payload.branchName" label="支店名称" min-width="180" />
    </el-table>
    <div style="margin-top:8px;display:flex;gap:8px;justify-content:flex-end">
      <el-pagination layout="prev, pager, next" :page-size="pageSize" :total="total" :current-page="page" @current-change="p=>{page=p;load()}" />
      <el-button @click="$emit('cancel')">取消</el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import api from '../api'

const props = defineProps<{ mode: 'bank' | 'branch'; bankCode?: string }>()
const emit = defineEmits(['select','cancel'])

const keyword = ref('')
const rows = ref<any[]>([])
const page = ref(1)
const pageSize = ref(10)
const total = ref(0)

async function load(){
  const where:any[] = []
  if (keyword.value) {
    where.push({ json: props.mode==='bank' ? 'payload.name' : 'payload.branchName', op:'contains', value: keyword.value })
  }
  if (props.mode==='branch' && props.bankCode) {
    // 允许以银行代码过滤支店列表，使用 contains 便于输入部分代码
    where.push({ json: 'payload.bankCode', op: 'contains', value: props.bankCode })
  }
  const r = await api.post(`/objects/${props.mode}/search`, { page: page.value, pageSize: pageSize.value, where, orderBy: [] })
  const data = r.data?.data || []
  rows.value = data
  total.value = data.length
}

function onSearch(){
  page.value = 1
  load()
}

function rowClick(row:any){
  emit('select', row)
}

watch(() => props.bankCode, () => { if (props.mode==='branch') { keyword.value=''; page.value=1; load() } })
load()
</script>


