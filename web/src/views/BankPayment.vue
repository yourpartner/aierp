<template>
  <div style="padding:16px">
    <el-card>
      <template #header>银行出金</template>
      <DynamicForm v-if="opUi" :ui="opUi" :model="form" />

      <div style="margin:8px 0; display:flex; gap:8px; align-items:center;">
        <el-select v-model="filters.partnerId" filterable remote :remote-method="searchPartners" reserve-keyword placeholder="选择供应商" style="width:260px" @change="onPartnerChange">
          <el-option v-for="p in partnerOptions" :key="p.value" :label="p.label" :value="p.value" />
        </el-select>
        <el-input v-model="filters.account" placeholder="科目(含)" style="width:160px" />
        <el-button @click="loadOpenItems" :loading="loading">查询未清项</el-button>
      </div>

      <el-table :data="openItems" style="width:100%" height="300" @selection-change="sel=>selected=sel" show-summary :summary-method="summary">
        <el-table-column type="selection" width="40" />
        <el-table-column prop="account_code" label="科目" width="120" />
        <el-table-column prop="partner_id" label="对象" width="160" />
        <el-table-column prop="currency" label="币种" width="80" />
        <el-table-column prop="residual_amount" label="未清余额" width="120" />
        <el-table-column label="本次出金" width="140">
          <template #default="{ row }">
            <el-input-number v-model="row.apply" :min="0" :max="row.residual_amount" :step="1" />
          </template>
        </el-table-column>
      </el-table>

      <div style="margin-top:12px; display:flex; align-items:center; gap:12px;">
        <el-button type="primary" @click="submit" :loading="saving">执行出金</el-button>
        <span v-if="msg" style="color:#1a73e8">{{ msg }}</span>
        <span v-if="err" style="color:#d93025">{{ err }}</span>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import api from '../api'
import DynamicForm from '../components/DynamicForm.vue'

const opUi = ref<any>(null)
const form = ref<any>({ header:{ postingDate: new Date().toISOString().slice(0,10), currency:'JPY' }, allocations: [] })
const filters = ref<any>({ partnerId:'', account:'' })
const partnerOptions = ref<{label:string,value:string}[]>([])
const openItems = ref<any[]>([])
const selected = ref<any[]>([])
const loading = ref(false)
const saving = ref(false)
const msg = ref('')
const err = ref('')

async function ensureUi(){
  if (!opUi.value){
    const r = await api.get('/schemas/bank_payment')
    opUi.value = r.data?.ui
  }
}

function summary({ columns, data }: any){
  const sums:any[] = []
  columns.forEach((col:any, i:number) => {
    if (i === 0) { sums[i] = '合计'; return }
    if (col.property === 'residual_amount'){
      sums[i] = data.reduce((a:number,b:any)=> a + (Number(b.residual_amount)||0), 0)
    } else if (col.property === undefined) {
      sums[i] = ''
    }
  })
  return sums
}

async function loadOpenItems(){
  await ensureUi()
  loading.value = true
  try {
    const where:any[] = []
    if (filters.value.partnerId) where.push({ field:'partner_id', op:'eq', value: filters.value.partnerId })
    if (filters.value.account) where.push({ field:'account_code', op:'contains', value: filters.value.account })
    const r = await api.post('/objects/openitem/search', { page:1, pageSize:1000, where, orderBy: [] })
    openItems.value = (r.data?.data||[]).map((x:any)=> ({ ...x, apply: 0 }))
  } finally { loading.value = false }
}

async function searchPartners(query:string){
  const where:any[] = [{ field:'flag_vendor', op:'eq', value:true }]
  if (query && query.trim()) where.push({ json:'name', op:'contains', value: query.trim() })
  const r = await api.post('/objects/businesspartner/search', { page:1, pageSize:50, where, orderBy:[] })
  const arr:any[] = r.data?.data || []
  partnerOptions.value = arr.map((p:any)=>({ label:`${p.payload?.name||p.name} (${p.partner_code})`, value:p.partner_code }))
}

function onPartnerChange(){ loadOpenItems() }

async function submit(){
  err.value = ''; msg.value = ''
  const allocs = openItems.value.filter(x => x.apply>0).map((x:any)=> ({ openItemId: x.id, applyAmount: x.apply }))
  if (allocs.length === 0){ err.value='请选择要出金的未清项并输入金额'; return }
  saving.value = true
  try {
    const body = { header: form.value.header, allocations: allocs }
    const r = await api.post('/operations/bank-payment/allocate', body)
    msg.value = `出金完成：金额 ${r.data?.amount}`
    await loadOpenItems()
  } catch (e:any) {
    err.value = e?.response?.data?.error || e?.message || '提交失败'
  } finally { saving.value = false }
}

</script>


