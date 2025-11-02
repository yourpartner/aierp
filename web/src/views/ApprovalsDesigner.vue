<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">审批规则设计器</div>
          <div class="page-actions">
            <el-button size="small" type="primary" :loading="saving" @click="save">保存</el按钮>
            <el-button size="small" @click="load">重新加载</el按钮>
          </div>
        </div>
      </template>

      <el-form label-width="120px">
        <el-form-item label="目标实体">
          <el-select v-model="entity">
            <el-option label="certificate_request" value="certificate_request" />
          </el-select>
        </el-form-item>
        <el-form-item label="自然语言描述">
          <el-input v-model="nl" type="textarea" :rows="8" placeholder="例如：员工提交后，先由部门经理审批，再由HR负责人最终审批；每一步需要邮件通知。" />
        </el-form-item>
      </el-form>

      <el-divider>AI 输出 JSON（可编辑）</el-divider>
      <el-input v-model="jsonText" type="textarea" :rows="14" placeholder="approval JSON" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import api from '../api'
import { useI18n } from '../i18n'

const entity = ref('certificate_request')
const nl = ref('')
const compiling = ref(false)
const saving = ref(false)
const jsonText = ref('')
const { lang } = useI18n()

async function load(){
  try{
    const r = await api.get(`/schemas/${entity.value}`, { params: { lang: lang.value } })
    const doc = r.data || {}
    // 新结构：ui.approval = { nl, rules }
    const apObj = doc?.ui?.approval
    if (apObj){
      nl.value = String(apObj.nl || '')
      try{ jsonText.value = apObj.rules ? JSON.stringify(apObj.rules, null, 2) : '' }catch{ jsonText.value = '' }
    } else {
      // 兼容历史：schema.approval（仅规则）
      const ap = doc?.schema?.approval ?? doc?.approval
      if (ap){ try{ jsonText.value = JSON.stringify(ap, null, 2) }catch{ jsonText.value = '' } }
      else { jsonText.value = '' }
      nl.value = ''
    }
  }catch{ jsonText.value = ''; nl.value = '' }
}

onMounted(load)

async function compile(){
  compiling.value=true
  try{ const r = await api.post('/ai/approvals/compile', { nlText: nl.value }); jsonText.value = typeof r.data==='string'? r.data : JSON.stringify(r.data, null, 2) }catch(e:any){ jsonText.value = e?.response?.data?.error || e?.message || '' }
  finally{ compiling.value=false }
}

async function save(){
  if (!jsonText.value.trim()) return
  saving.value=true
  try{
    let rules:any=null; try{ rules = JSON.parse(jsonText.value) }catch{ rules = null }
    const schemaRes = await api.get(`/schemas/${entity.value}`, { params: { lang: lang.value } })
    const doc = schemaRes.data || {}
    // 将自然语言与规则写入 ui.approval
    const next:any = {
      schema: doc.schema ?? { type:'object', properties:{} },
      ui: doc.ui ?? {},
      query: doc.query ?? {},
      core_fields: doc.core_fields ?? {},
      validators: doc.validators ?? [],
      numbering: doc.numbering ?? {},
      ai_hints: doc.ai_hints ?? {}
    }
    if (!next.ui) next.ui = {}
    next.ui.approval = { nl: nl.value || '', rules: rules || {} }
    await api.post(`/schemas/${entity.value}`, next)
    await load()
  }finally{ saving.value=false }
}
</script>

<style scoped>
.page.page-medium { max-width: 1000px; }
.editor{ width:100%; border-radius:12px; border:1px solid #e5e7eb; min-height:420px; padding:16px; background:#f8fafc; }
</style>


