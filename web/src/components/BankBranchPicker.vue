<template>
  <div class="bank-branch-picker">
    <el-input v-model="keyword" placeholder="名称またはコードで検索" @input="onSearch" clearable class="picker-input" />
    <el-table :data="rows" height="300" @row-click="rowClick" highlight-current-row class="picker-table">
      <template v-if="mode==='bank'">
        <el-table-column prop="payload.bankCode" label="銀行コード" width="100" />
        <el-table-column prop="payload.name" label="銀行名" />
      </template>
      <template v-else>
        <el-table-column prop="payload.branchCode" label="支店コード" width="100" />
        <el-table-column prop="payload.branchName" label="支店名" />
      </template>
    </el-table>
    <div class="picker-footer">
      <el-pagination layout="prev, pager, next" :page-size="pageSize" :total="total" :current-page="page" @current-change="p=>{page=p;load()}" small />
      <el-button @click="$emit('cancel')">キャンセル</el-button>
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
    // 搜索时同时匹配名称和代码
    where.push({ 
      or: [
        { json: props.mode==='bank' ? 'payload.name' : 'payload.branchName', op:'contains', value: keyword.value },
        { json: props.mode==='bank' ? 'payload.bankCode' : 'payload.branchCode', op:'contains', value: keyword.value }
      ]
    })
  }
  if (props.mode==='branch' && props.bankCode) {
    // 按银行代码精确过滤支店
    where.push({ json: 'payload.bankCode', op: 'eq', value: props.bankCode })
  }
  const r = await api.post(`/objects/${props.mode}/search`, { page: page.value, pageSize: pageSize.value, where, orderBy: [] })
  const data = r.data?.data || []
  rows.value = data
  // 使用后端返回的总记录数
  total.value = Number(r.data?.total) || data.length
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

<style scoped>
.bank-branch-picker {
  width: 100%;
  box-sizing: border-box;
}
.picker-input {
  width: 100%;
  margin-bottom: 8px;
}
.picker-table {
  width: 100%;
}
.picker-footer {
  margin-top: 8px;
  display: flex;
  gap: 8px;
  justify-content: flex-end;
  align-items: center;
}
.bank-branch-picker :deep(.el-table__row) {
  cursor: pointer;
}
.bank-branch-picker :deep(.el-table__row:hover) {
  background-color: var(--el-fill-color-light);
}
</style>
