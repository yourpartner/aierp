<template>
  <div style="padding:12px">
    <el-card>
      <template #header>策略编写器（自然语言 → 规则草案）</template>
      <el-form label-width="96px">
        <el-form-item label="公司描述/规则">
          <el-input v-model="nl" type="textarea" :rows="10" placeholder="用日语或中文描述工资计算逻辑、雇佣类型与会计映射..." />
        </el-form-item>
        <!-- 策略不需要名称与编号，统一由后端生成版本/代码，前端移除输入 -->
        <div style="display:flex;gap:8px;justify-content:flex-end">
          <el-button type="primary" :loading="loading" @click="compile">生成规则草案</el-button>
        <el-button :disabled="!result" :loading="saving" @click="savePolicy">保存为策略</el-button>
        <el-button :disabled="!result" :loading="savingEmp" @click="saveEmployment">写入雇佣类型</el-button>
        </div>
      </el-form>
      <el-divider>AI 输出</el-divider>
      <el-skeleton :rows="6" animated v-if="loading" />
      <div v-else>
        <el-alert v-if="error" type="error" show-icon :title="error" />
        <div v-if="result">
          <div style="margin:8px 0; font-weight:600">规则 DSL（可编辑）</div>
          <el-input v-model="dslText" type="textarea" :rows="14" placeholder="在此编辑/粘贴 DSL JSON，保存时将写入策略" />
          <el-collapse style="margin-top:8px">
            <el-collapse-item title="解释">
              <pre style="white-space:pre-wrap;word-break:break-all">{{ result.explanation }}</pre>
            </el-collapse-item>
          </el-collapse>
        </div>
      </div>
    </el-card>
  </div>
</template>
<script setup lang="ts">
import { ref, onMounted } from 'vue'
import api from '../api'
const nl = ref('')
const loading = ref(false)
const error = ref('')
const result = ref<any>(null)
const dslText = ref('')
const policyCode = ref('')
const policyName = ref('')
const saving = ref(false)
const savingEmp = ref(false)
// 进入页面默认加载“当前生效策略”
onMounted(async () => {
  try{
    let r = await api.post('/objects/payroll_policy/search', { page:1, pageSize:1, where:[{ json:'isActive', op:'eq', value:true }] })
    let row = (r.data?.data||[])[0]
    if (row){
      const pl = row.payload || {}
      const dsl = pl.dsl || { rules: pl.rules||[], employmentTypes: pl.employmentTypes||[], payrollItems: pl.payrollItems||[], hints: pl.hints||[] }
      result.value = { dsl, explanation: `当前生效策略：${pl.code||row.policy_code||''} / ${pl.version||row.version||''}` }
      try{ dslText.value = JSON.stringify(dsl, null, 2) }catch{ dslText.value = '' }
      policyCode.value = pl.code || ''
      policyName.value = pl.name || ''
      nl.value = pl.nlText || ''
    } else {
      // 兜底：取最近创建的一条策略展示
      r = await api.post('/objects/payroll_policy/search', { page:1, pageSize:1, where:[], orderBy:[{ field:'created_at', dir:'DESC' }] })
      row = (r.data?.data||[])[0]
      if (row){
        const pl = row.payload || {}
        const dsl = pl.dsl || { rules: pl.rules||[], employmentTypes: pl.employmentTypes||[], payrollItems: pl.payrollItems||[], hints: pl.hints||[] }
        result.value = { dsl, explanation: `最近策略：${pl.code||row.policy_code||''} / ${pl.version||row.version||''}` }
        try{ dslText.value = JSON.stringify(dsl, null, 2) }catch{ dslText.value = '' }
        policyCode.value = pl.code || ''
        policyName.value = pl.name || ''
        nl.value = pl.nlText || ''
      }
    }
  }catch{}
})
async function compile(){
  loading.value = true; error.value=''; result.value=null
  try{
    const r = await api.post('/ai/payroll/compile', { nlText: nl.value })
    result.value = r.data
    try{ dslText.value = JSON.stringify(result.value?.dsl||{}, null, 2) }catch{ dslText.value='' }
  }catch(e:any){ error.value = e?.response?.data?.error || e?.message || '生成失败' }
  finally{ loading.value=false }
}
async function savePolicy(){
  if (!result.value){ error.value = '请先生成规则'; return }
  saving.value = true; error.value=''
  try{
    let dslObj:any = null
    try{ dslObj = dslText.value ? JSON.parse(dslText.value) : (result.value?.dsl||{}) }catch(parseErr){ error.value = 'DSL 不是有效的 JSON'; saving.value=false; return }
    const payload:any = {
      // 由后端自动生成 code/version；前端不再传递
      isActive: true,
      nlText: nl.value || undefined,
      dsl: dslObj || undefined,
      rules: Array.isArray(dslObj?.rules) ? dslObj.rules : (result.value?.dsl?.rules ?? [])
    }
    await api.post('/objects/payroll_policy', { payload })
  }catch(e:any){ error.value = e?.response?.data?.error || e?.message || '保存失败' }
  finally{ saving.value=false }
}

async function saveEmployment(){
  if (!result.value){ error.value='请先生成规则草案'; return }
  savingEmp.value=true; error.value=''
  try{
    // 约定：dsl 中包含 employmentTypes 与 payrollItems 草案；若无则忽略
    const et = Array.isArray(result.value?.dsl?.employmentTypes) ? result.value.dsl.employmentTypes : []
    for (const t of et){
      const payload:any = { code: t.code, name: t.name, isActive: t.isActive!==false }
      await api.post('/objects/employment_type', { payload }).catch(()=>{})
    }
  }catch(e:any){ error.value = e?.response?.data?.error || e?.message || '写入失败' }
  finally{ savingEmp.value=false }
}
</script>


