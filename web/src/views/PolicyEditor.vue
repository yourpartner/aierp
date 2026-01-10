<template>
  <div class="policy-editor">
    <el-card class="policy-card">
      <template #header>
        <div class="policy-header">
          <div class="policy-header__left">
            <el-icon class="policy-header__icon"><Setting /></el-icon>
            <span class="policy-header__title">給与ポリシーエディタ</span>
          </div>
          <div class="policy-header__right">
            <el-button type="primary" :loading="loading" @click="compile">
              <el-icon><MagicStick /></el-icon>
              <span>ルール草案を生成</span>
            </el-button>
            <el-button :disabled="!result" :loading="saving" @click="savePolicy">
              <el-icon><FolderChecked /></el-icon>
              <span>ポリシーとして保存</span>
            </el-button>
            <el-button :disabled="!result" :loading="savingEmp" @click="saveEmployment">
              <el-icon><User /></el-icon>
              <span>雇用区分へ反映</span>
            </el-button>
          </div>
        </div>
      </template>
      
      <div class="policy-content">
        <div class="input-section">
          <div class="section-label">会社ルール記述</div>
          <el-input 
            v-model="nl" 
            type="textarea" 
            :rows="8" 
            placeholder="給与計算ロジック・雇用区分・会計マッピングを日本語で記述してください..."
          />
        </div>
      </div>
      
      <el-divider />
      
      <div class="output-section">
        <div class="section-label">AI 出力</div>
        <el-skeleton :rows="6" animated v-if="loading" />
        <template v-else>
          <el-alert v-if="error" type="error" show-icon :title="error" style="margin-bottom:12px" />
          <div v-if="result">
            <div class="dsl-section">
              <div class="section-sublabel">ルール DSL（編集可）</div>
              <el-input 
                v-model="dslText" 
                type="textarea" 
                :rows="12" 
                placeholder="ここで DSL JSON を編集／貼り付けると保存時に反映されます"
              />
            </div>
            <el-collapse style="margin-top:12px">
              <el-collapse-item title="解説を表示">
                <pre class="explanation-text">{{ result.explanation }}</pre>
              </el-collapse-item>
            </el-collapse>
          </div>
          <el-empty v-else description="ルール記述を入力し「ルール草案を生成」をクリックしてください" :image-size="80" />
        </template>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { Setting, MagicStick, FolderChecked, User } from '@element-plus/icons-vue'
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

onMounted(async () => {
  try{
    let r = await api.post('/objects/payroll_policy/search', { page:1, pageSize:1, where:[{ json:'isActive', op:'eq', value:true }] })
    let row = (r.data?.data||[])[0]
      if (row){
        const pl = row.payload || {}
        const dsl = pl.dsl || { rules: pl.rules||[], employmentTypes: pl.employmentTypes||[], hints: pl.hints||[] }
        result.value = { dsl, explanation: `現在有効なポリシー：${pl.code||row.policy_code||''} / ${pl.version||row.version||''}` }
      try{ dslText.value = JSON.stringify(dsl, null, 2) }catch{ dslText.value = '' }
      policyCode.value = pl.code || ''
      policyName.value = pl.name || ''
      nl.value = pl.nlText || ''
    } else {
      r = await api.post('/objects/payroll_policy/search', { page:1, pageSize:1, where:[], orderBy:[{ field:'created_at', dir:'DESC' }] })
      row = (r.data?.data||[])[0]
      if (row){
        const pl = row.payload || {}
        const dsl = pl.dsl || { rules: pl.rules||[], employmentTypes: pl.employmentTypes||[], hints: pl.hints||[] }
        result.value = { dsl, explanation: `直近のポリシー：${pl.code||row.policy_code||''} / ${pl.version||row.version||''}` }
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
  }catch(e:any){ error.value = e?.response?.data?.error || e?.message || '生成に失敗しました' }
  finally{ loading.value=false }
}

async function savePolicy(){
  if (!result.value){ error.value = '先にルールを生成してください'; return }
  saving.value = true; error.value=''
  try{
    let dslObj:any = null
    try{ dslObj = dslText.value ? JSON.parse(dslText.value) : (result.value?.dsl||{}) }catch(parseErr){ error.value = 'DSL は正しい JSON ではありません'; saving.value=false; return }
    const payload:any = {
      isActive: true,
      nlText: nl.value || undefined,
      dsl: dslObj || undefined,
      rules: Array.isArray(dslObj?.rules) ? dslObj.rules : (result.value?.dsl?.rules ?? [])
    }
    await api.post('/objects/payroll_policy', { payload })
  }catch(e:any){ error.value = e?.response?.data?.error || e?.message || '保存に失敗しました' }
  finally{ saving.value=false }
}

async function saveEmployment(){
  if (!result.value){ error.value='先にルール草案を生成してください'; return }
  savingEmp.value=true; error.value=''
  try{
    const et = Array.isArray(result.value?.dsl?.employmentTypes) ? result.value.dsl.employmentTypes : []
    for (const t of et){
      const payload:any = { code: t.code, name: t.name, isActive: t.isActive!==false }
      await api.post('/objects/employment_type', { payload }).catch(()=>{})
    }
  }catch(e:any){ error.value = e?.response?.data?.error || e?.message || '書き込みに失敗しました' }
  finally{ savingEmp.value=false }
}
</script>

<style scoped>
.policy-editor {
  padding: 16px;
}

.policy-card {
  border-radius: 12px;
  overflow: hidden;
}

.policy-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.policy-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.policy-header__icon {
  font-size: 22px;
  color: #667eea;
}

.policy-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.policy-header__right {
  display: flex;
  gap: 8px;
}

.policy-content {
  margin-bottom: 8px;
}

.section-label {
  font-size: 14px;
  font-weight: 600;
  color: #303133;
  margin-bottom: 8px;
}

.section-sublabel {
  font-size: 13px;
  font-weight: 500;
  color: #606266;
  margin-bottom: 6px;
}

.dsl-section {
  margin-top: 12px;
}

.explanation-text {
  white-space: pre-wrap;
  word-break: break-all;
  font-size: 13px;
  line-height: 1.6;
  color: #606266;
  margin: 0;
  padding: 12px;
  background: #f8f9fc;
  border-radius: 6px;
}

.output-section {
  min-height: 200px;
}
</style>
