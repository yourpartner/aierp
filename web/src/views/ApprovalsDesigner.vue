<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">审批规则设计器</div>
          <div class="page-actions">
            <el-button size="small" type="primary" :loading="saving" @click="save">保存</el-button>
            <el-button size="small" @click="load">重新加载</el-button>
          </div>
        </div>
      </template>

      <el-form label-width="120px">
        <el-form-item label="目标实体">
          <el-select v-model="entity" @change="load">
            <el-option label="証明書申請 (certificate_request)" value="certificate_request" />
            <el-option label="勤怠月次提出 (timesheet_submission)" value="timesheet_submission" />
          </el-select>
        </el-form-item>
        <el-divider>自動承認（免承認）</el-divider>
        <el-form-item label="有効">
          <el-switch v-model="auto.enabled" />
        </el-form-item>
        <el-form-item label="自然语言描述">
          <el-input
            v-model="auto.nl"
            type="textarea"
            :rows="4"
            placeholder="例：残業が0で、欠勤時間が8時間以下なら自動承認（顧客ごとに調整）"
          />
          <div style="margin-top:6px;color:#909399;font-size:12px">
            解析できる表現（簡易）：残業(=0) / 欠勤(<=X時間)
          </div>
        </el-form-item>
        <el-form-item label="ルール（簡易）">
          <div style="display:flex;flex-direction:column;gap:8px;width:100%">
            <el-checkbox v-model="auto.rule.requireOvertimeZero">残業が0の場合</el-checkbox>
            <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
              <span>欠勤時間 ≤</span>
              <el-input-number v-model="auto.rule.maxAbsenceHours" :min="0" :max="300" :controls="false" />
              <span>時間</span>
            </div>
            <div style="color:#606266;font-size:12px">
              プレビュー：{{ autoPreview }}
            </div>
            <div style="display:flex;gap:8px;align-items:center">
              <el-button size="small" @click="parseAutoNl" :disabled="!auto.nl.trim()">自然语言から解析</el-button>
              <el-button size="small" @click="syncAutoNl">ルールから自然语言を生成</el-button>
            </div>
          </div>
        </el-form-item>
        <el-form-item label="自然语言描述">
          <el-input v-model="nl" type="textarea" :rows="8" placeholder="例如：员工提交后，先由部门经理审批，再由HR负责人最终审批；每一步需要邮件通知。" />
        </el-form-item>
        <el-form-item>
          <el-button type="success" :loading="compiling" @click="compile" :disabled="!nl.trim()">
            <el-icon class="el-icon--left"><MagicStick /></el-icon>
            AI 编译规则
          </el-button>
          <span v-if="compiling" style="margin-left: 12px; color: #909399;">正在调用 AI 生成审批规则...</span>
        </el-form-item>
      </el-form>

      <el-divider>AI 输出 JSON（可编辑）</el-divider>
      <el-input v-model="jsonText" type="textarea" :rows="14" placeholder="approval JSON" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { MagicStick } from '@element-plus/icons-vue'
import api from '../api'
import { useI18n } from '../i18n'

const entity = ref('certificate_request')
const nl = ref('')
const auto = ref({
  enabled: false,
  nl: '',
  rule: {
    requireOvertimeZero: true,
    maxAbsenceHours: 8
  }
})
const compiling = ref(false)
const saving = ref(false)
const jsonText = ref('')
const { lang } = useI18n()

function normalizeText(v: any){ return (v ?? '').toString().trim() }
const autoPreview = computed(() => {
  if (!auto.value.enabled) return '自動承認：無効'
  const parts: string[] = []
  if (auto.value.rule.requireOvertimeZero) parts.push('残業が0')
  parts.push(`欠勤時間が${Number(auto.value.rule.maxAbsenceHours || 0)}時間以下`)
  return `自動承認：${parts.join(' かつ ')}`
})

function syncAutoNl(){
  auto.value.nl = autoPreview.value.replace(/^自動承認：/, '')
}

function parseAutoNl(){
  const t = normalizeText(auto.value.nl)
  if (!t) return
  // very small deterministic parser (JP/CN keywords)
  const hasOvertimeZero = /残業|加班/.test(t) && (/(=|＝|为|是)?\s*0/.test(t) || /なし|无/.test(t))
  if (hasOvertimeZero) auto.value.rule.requireOvertimeZero = true
  const m = t.match(/欠勤|缺勤|欠課|absen(?:ce)?\s*(?:時間|hours?)?\s*(?:<=|≤|小于等于|以下)\s*(\d+(?:\.\d+)?)/i)
    || t.match(/(\d+(?:\.\d+)?)\s*(?:時間|hours?)\s*(?:以下|以内)/i)
  if (m && m[1]) {
    const v = Number(m[1])
    if (!isNaN(v)) auto.value.rule.maxAbsenceHours = v
  }
  auto.value.enabled = true
  syncAutoNl()
}

async function load(){
  try{
    const r = await api.get(`/schemas/${entity.value}`, { params: { lang: lang.value } })
    const doc = r.data || {}
    // 新结构：ui.approval = { nl, rules }
    const apObj = doc?.ui?.approval
    if (apObj){
      nl.value = String(apObj.nl || '')
      try{ jsonText.value = apObj.rules ? JSON.stringify(apObj.rules, null, 2) : '' }catch{ jsonText.value = '' }
      const aa = apObj.autoApprove
      if (aa){
        auto.value.enabled = !!aa.enabled
        auto.value.nl = String(aa.nl || '')
        auto.value.rule.requireOvertimeZero = aa?.rule?.requireOvertimeZero !== false
        const maxAbs = Number(aa?.rule?.maxAbsenceHours ?? 8)
        auto.value.rule.maxAbsenceHours = isNaN(maxAbs) ? 8 : maxAbs
      } else {
        auto.value = { enabled: false, nl: '', rule: { requireOvertimeZero: true, maxAbsenceHours: 8 } }
      }
    } else {
      // 兼容历史：schema.approval（仅规则）
      const ap = doc?.schema?.approval ?? doc?.approval
      if (ap){ try{ jsonText.value = JSON.stringify(ap, null, 2) }catch{ jsonText.value = '' } }
      else { jsonText.value = '' }
      nl.value = ''
      auto.value = { enabled: false, nl: '', rule: { requireOvertimeZero: true, maxAbsenceHours: 8 } }
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
    next.ui.approval = {
      nl: nl.value || '',
      rules: rules || {},
      autoApprove: {
        enabled: !!auto.value.enabled,
        nl: auto.value.nl || '',
        rule: {
          requireOvertimeZero: auto.value.rule.requireOvertimeZero !== false,
          maxAbsenceHours: Number(auto.value.rule.maxAbsenceHours ?? 8)
        }
      }
    }
    await api.post(`/schemas/${entity.value}`, next)
    await load()
  }finally{ saving.value=false }
}
</script>

<style scoped>
.page.page-medium { max-width: 1000px; }
.editor{ width:100%; border-radius:12px; border:1px solid #e5e7eb; min-height:420px; padding:16px; background:#f8fafc; }
</style>


