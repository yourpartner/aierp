<template>
  <div class="page page-narrow">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">证明申请</div>
          <div class="page-actions">
            <el-button type="primary" @click="submit" :loading="saving">提交申请</el-button>
          </div>
        </div>
      </template>

      <el-form ref="formRef" :model="form" :rules="rules" label-width="120px">
        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item label="证明类型" prop="type">
              <el-select class="type-select" v-model="form.type" filterable clearable placeholder="请选择或输入">
                <el-option v-for="t in typeOptions" :key="t" :label="t" :value="t" />
              </el-select>
              <el-input v-model="customType" placeholder="自定义类型" class="ml8 custom-type" />
              <el-button size="small" class="ml8" @click="useCustom">使用自定义</el-button>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="用途与备注">
              <el-input v-model="form.purpose" type="textarea" :rows="3" placeholder="填写用途、收件人信息或其他说明" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="12">
          <el-col :span="8">
            <el-form-item label="语言">
              <el-select v-model="form.language" placeholder="选择">
                <el-option v-for="l in languages" :key="l" :label="l" :value="l" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="16">
            <el-form-item label="收件邮箱" prop="toEmail">
              <el-input class="email-input" v-model="form.toEmail" placeholder="someone@example.com" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-form-item v-if="showResignReason" label="退职理由" prop="resignReason">
          <el-input v-model="form.resignReason" type="textarea" :rows="4" placeholder="例：个人职业发展、家庭原因、身体原因、合同期满等" />
        </el-form-item>
      </el-form>

      <div class="form-messages">
        <span v-if="message" class="text-success">{{ message }}</span>
        <span v-if="error" class="text-error">{{ error }}</span>
      </div>
    </el-card>
  </div>
  
</template>

<script setup lang="ts">
import { reactive, ref, onMounted, computed } from 'vue'
import type { FormInstance, FormRules } from 'element-plus'
import api from '../api'
import { getLang } from '../i18n'

const form = reactive<any>({ type:'', language:'zh', purpose:'', toEmail:'', resignReason:'' })
const formRef = ref<FormInstance | null>(null)
const emailRegex = /^[\w.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$/
const rules: FormRules = {
  type: [
    { required: true, message: '请选择或填写证明类型', trigger: ['change','blur'] }
  ],
  toEmail: [
    { required: true, message: '请输入收件人邮箱', trigger: ['blur','change'] },
    { validator: (_r, v, cb) => { cb(emailRegex.test(String(v||'')) ? undefined : new Error('邮箱格式不正确')) }, trigger: ['blur','change'] }
  ],
  resignReason: [
    { validator: (_r, v, cb) => { if (showResignReason.value && !String(v||'').trim()) cb(new Error('请填写退职理由')); else cb() }, trigger: ['blur','change'] }
  ]
};
const saving = ref(false);
const message = ref('');
const error = ref('');
const typeOptions = ['在职证明','收入证明','离职证明'];
const languages = ['zh','ja','en'];
const customType = ref('');
const showResignReason = computed(()=> /离职|退职|退職/.test(form.type||''));

function useCustom(){ if (customType.value && customType.value.trim()) form.type = customType.value.trim() }

async function ensureSchema(){
  try{
    const r = await api.get('/schemas/certificate_request', { params: { lang: getLang() } })
    const doc:any = r.data || {}
    let needUpdate = false
    const schema:any = doc.schema || {
      type:'object',
      properties:{
        type:{ type:'string' },
        language:{ type:'string' },
        purpose:{ type:'string' },
        toEmail:{ type:'string', format:'email' },
        subject:{ type:'string' },
        bodyText:{ type:'string' },
        status:{ type:'string', enum:['pending','approved','rejected'], default:'pending' }
      },
      required:['type']
    }
    if (!schema.properties.resignReason) { schema.properties.resignReason = { type:'string' } ; needUpdate = true }
    const ui:any = doc.ui || {
      list:{ columns:['created_at','status','type'] },
      form:{ layout:[
        { field:'type', label:'类型' },
        { field:'language', label:'语言' },
        { field:'toEmail', label:'收件人' },
        { field:'resignReason', label:'退职理由' },
        { field:'purpose', label:'用途' },
        { field:'subject', label:'主题' },
        { field:'bodyText', label:'正文' }
      ]}
    }
    const hasUiResign = JSON.stringify(ui).includes('resignReason')
    if (!hasUiResign) { try{ (ui.form.layout as any[]).splice(3,0,{ field:'resignReason', label:'退职理由' }) }catch{}; needUpdate = true }
    // 确保列表包含 purpose 列（显示用途与备注）
    try {
      const listCfg: any = (ui.list = ui.list || {});
      const cols: any[] = Array.isArray(listCfg.columns) ? [...listCfg.columns] : [];
      if (!cols.includes('purpose')) {
        const idx = cols.indexOf('type');
        if (idx >= 0) {
          cols.splice(idx + 1, 0, 'purpose');
        } else {
          cols.push('purpose');
        }
        listCfg.columns = cols;
        needUpdate = true;
      }
    } catch {}
    const query:any = doc.query || { filters:['status','created_at','type'], sorts:['created_at'] }
    if (needUpdate || !doc.schema) await api.post('/schemas/certificate_request', { schema, ui, query })
  }catch{}
}

onMounted(()=>{ ensureSchema().catch(()=>{}) })

async function submit(){
  message.value=''; error.value='';
  // 先前端校验
  const ok = await new Promise<boolean>(resolve => { formRef.value?.validate((valid)=> resolve(!!valid)) })
  if (!ok) return
  saving.value=true
  try{
    await ensureSchema()
    const payload:any = { type: form.type, language: form.language, purpose: form.purpose, toEmail: form.toEmail, resignReason: form.resignReason }
    if (!payload.type) throw new Error('请选择或填写证明类型')
    await api.post('/objects/certificate_request', { payload })
    message.value = '已提交申请'
    form.purpose=''
  }catch(e:any){ error.value = e?.response?.data?.error || e?.message || '提交失败' }
  finally{ saving.value=false }
}
</script>

<style scoped>
.page.page-narrow { max-width: 820px; }
.form-messages { margin-top: 18px; display:flex; gap: 14px; font-size: 13px; }
.ml8 { margin-left: 8px; }
.w200 { width: 200px; }
.type-select { width: 260px; }
.custom-type { width: 200px; }
.email-input { width: 100%; }
</style>


