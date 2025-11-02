<template>
  <div class="payroll-wrap">
    <el-card>
      <template #header>工资计算</template>
      <el-form :inline="true" label-width="80px" size="small">
        <el-form-item label="员工">
          <el-select v-model="employeeId" filterable remote reserve-keyword :remote-method="searchEmployees" placeholder="输入姓名/编码检索" style="width:260px">
            <el-option v-for="e in employeeOptions" :key="e.value" :label="e.label" :value="e.value" />
          </el-select>
        </el-form-item>
        <el-form-item label="策略">
          <el-select v-model="policyId" filterable clearable style="width:280px" @visible-change="(v)=>{ if(v) loadPolicies() }">
            <el-option v-for="p in policies" :key="p.value" :label="p.label" :value="p.value" />
          </el-select>
        </el-form-item>
        <el-form-item label="月份">
          <el-date-picker v-model="month" type="month" value-format="YYYY-MM" style="width:160px" />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :loading="loading" @click="run">执行</el-button>
        </el-form-item>
        <el-form-item label="调试">
          <el-switch v-model="debug" />
        </el-form-item>
      </el-form>
      <el-alert v-if="error" type="error" show-icon :title="error" style="margin-bottom:8px" />
      <el-row :gutter="12" v-if="data">
        <el-col :span="12">
          <el-card shadow="never" body-style="padding:8px">
            <template #header>工资项目</template>
            <el-table :data="data?.payrollSheet || []" size="small" border>
              <el-table-column prop="itemCode" label="项目" width="160" />
              <el-table-column prop="amount" label="金额" width="120" />
              <el-table-column prop="meta.formula" label="公式/说明" />
            </el-table>
          </el-card>
        </el-col>
        <el-col :span="12">
          <el-card shadow="never" body-style="padding:8px">
            <template #header>分录草稿</template>
            <el-table :data="data?.accountingDraft || []" size="small" border>
              <el-table-column prop="accountCode" label="科目编号" width="120" />
              <el-table-column prop="accountName" label="科目名称" width="160" />
              <el-table-column prop="drcr" label="借/贷" width="80" />
              <el-table-column prop="amount" label="金额" width="120" />
              <el-table-column prop="employeeCode" label="员工编号" width="120" />
              <el-table-column prop="departmentCode" label="部门" width="120" />
            </el-table>
          </el-card>
        </el-col>
      </el-row>
      <el-card v-if="debug && data?.trace" shadow="never" body-style="padding:8px; margin-top:8px">
        <template #header>调试 Trace</template>
        <el-table :data="data.trace" size="小" border>
          <el-table-column prop="step" label="步骤" width="160" />
          <el-table-column prop="source" label="来源" width="120" />
          <el-table-column prop="item" label="项目" width="120" />
          <el-table-column prop="amount" label="金额" width="120" />
          <el-table-column prop="base" label="基数" width="120" />
          <el-table-column prop="rate" label="费率" width="120" />
          <el-table-column prop="lawVersion" label="法令版本" width="160" />
          <el-table-column prop="note" label="说明" />
        </el-table>
        <div style="margin-top:8px">
          <div style="font-weight:600;margin-bottom:4px">原始 Trace JSON</div>
          <pre style="max-height:240px;overflow:auto;background:#f7f7f7;padding:8px">{{ JSON.stringify(data.trace, null, 2) }}</pre>
        </div>
      </el-card>
    </el-card>
  </div>
</template>
<script setup lang="ts">
import { ref } from 'vue'
import api from '../api'

const employeeId = ref('')
const employeeOptions = ref<any[]>([])
const policyId = ref('')
const month = ref('')
const loading = ref(false)
const error = ref('')
const data = ref<any>(null)
const policies = ref<any[]>([])
const debug = ref(true)

function fmtDate(d:string){ try{ const t=new Date(d); if (isNaN(t.getTime())) return ''; const y=t.getFullYear(); const m=String(t.getMonth()+1).padStart(2,'0'); const dd=String(t.getDate()).padStart(2,'0'); const hh=String(t.getHours()).padStart(2,'0'); const mm=String(t.getMinutes()).padStart(2,'0'); return `${y}-${m}-${dd} ${hh}:${mm}` }catch{ return '' } }

async function loadPolicies(){
  if (policies.value.length>0) return
  try{
    const active = await api.post('/objects/payroll_policy/search', { page:1, pageSize:1, where:[{ json:'isActive', op:'eq', value:true }], orderBy:[{ field:'created_at', dir:'DESC' }] })
    const arow = (active.data?.data||[])[0]
    let all:any[]=[]
    let page=1; const pageSize=100
    while(true){
      const r = await api.post('/objects/payroll_policy/search', { page, pageSize, where:[], orderBy:[{ field:'created_at', dir:'DESC'}] })
      const list = (r.data?.data||[]) as any[]; all = all.concat(list); if (list.length < pageSize) break; page++
    }
    policies.value = all.map(x=>{
      const ver = x.payload?.version || x.version || ''
      const ts = fmtDate(x.created_at)
      const active = x.payload?.isActive===true
      return { label: `${ver}  ${ts}${active?'  [生效]':''}`, value: x.id }
    })
    if (arow) policyId.value = arow.id
    if (!policyId.value && policies.value.length>0) policyId.value = policies.value[0].value
  }catch{}
}

async function run(){
  if (!employeeId.value || !month.value){ error.value='请选择员工与月份'; return }
  loading.value=true; error.value=''; data.value=null
  try{ const r = await api.post('/operations/payroll/execute', { employeeId: employeeId.value, month: month.value, policyId: policyId.value||null, debug: debug.value }); data.value=r.data }
  catch(e:any){ error.value = e?.response?.data?.error || e?.message || '执行失败' }
  finally{ loading.value=false }
}

async function searchEmployees(query:string){
  const where:any[] = []
  const q = (query||'').trim()
  if (q){ where.push({ json:'nameKanji', op:'contains', value:q }); where.push({ json:'nameKana', op:'contains', value:q }); where.push({ field:'employee_code', op:'contains', value:q }) }
  const r = await api.post('/objects/employee/search', { page:1, pageSize:20, where, orderBy:[{ field:'created_at', dir:'DESC' }] })
  const list = (r.data?.data||[]) as any[]
  employeeOptions.value = list.map(x=>({ label: `${x.payload?.nameKanji||x.payload?.name||x.name||''} (${x.employee_code||x.payload?.code||''})`, value: x.id }))
}
</script>
<style scoped>
.payroll-wrap{ display:inline-block; width: auto; min-width: 720px; max-width: 1200px; }
.payroll-wrap :deep(.el-card){ display:inline-block; width: auto; max-width: 100%; }
</style>


