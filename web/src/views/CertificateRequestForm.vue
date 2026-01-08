<template>
  <div class="certificate-form-page">
    <el-card class="certificate-card">
      <template #header>
        <div class="certificate-header">
          <div class="certificate-header__left">
            <el-icon class="certificate-header__icon"><Tickets /></el-icon>
            <span class="certificate-header__title">証明書申請</span>
          </div>
          <div class="certificate-header__right">
            <el-button type="primary" @click="submit" :loading="saving">
              <el-icon><Position /></el-icon>
              <span>申請を送信</span>
            </el-button>
          </div>
        </div>
      </template>

      <el-form
        ref="formRef"
        class="certificate-form"
        :model="form"
        :rules="rules"
        label-width="140px"
        label-suffix="："
      >
        <div class="form-grid">
          <el-form-item label="証明書の種類" prop="type">
            <el-select v-model="form.type" filterable clearable placeholder="選択してください">
              <el-option v-for="t in typeOptions" :key="t" :label="t" :value="t" />
            </el-select>
          </el-form-item>

          <el-form-item label="言語">
            <el-select v-model="form.language" placeholder="選択">
              <el-option v-for="l in languages" :key="l" :label="l" :value="l" />
            </el-select>
          </el-form-item>

          <el-form-item label="受信メールアドレス" prop="toEmail" class="span-2">
            <el-input class="email-input" v-model="form.toEmail" placeholder="someone@example.com" />
          </el-form-item>

          <el-form-item label="用途・備考" class="span-2">
            <el-input v-model="form.purpose" type="textarea" :rows="3" placeholder="用途や宛先情報などを入力してください" />
          </el-form-item>

          <el-form-item v-if="showResignReason" label="退職理由" prop="resignReason" class="span-2">
            <el-input v-model="form.resignReason" type="textarea" :rows="4" placeholder="例：キャリアアップ、家庭の事情、健康上の理由、契約満了など" />
          </el-form-item>
        </div>
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
import { Tickets, Position } from '@element-plus/icons-vue'

const form = reactive<any>({ type:'', language:'zh', purpose:'', toEmail:'', resignReason:'' })
const formRef = ref<FormInstance | null>(null)
const emailRegex = /^[\w.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$/
const rules: FormRules = {
  type: [
    { required: true, message: '証明書の種類を選択してください', trigger: ['change','blur'] }
  ],
  toEmail: [
    { required: true, message: '受信者のメールアドレスを入力してください', trigger: ['blur','change'] },
    { validator: (_r, v, cb) => { cb(emailRegex.test(String(v||'')) ? undefined : new Error('メールアドレスの形式が正しくありません')) }, trigger: ['blur','change'] }
  ],
  resignReason: [
    { validator: (_r, v, cb) => { if (showResignReason.value && !String(v||'').trim()) cb(new Error('退職理由を入力してください')); else cb() }, trigger: ['blur','change'] }
  ]
};
const saving = ref(false);
const message = ref('');
const error = ref('');
const typeOptions = ['在職証明','収入証明','退職証明'];
const languages = ['zh','ja','en'];
const showResignReason = computed(()=> /离职|退职|退職/.test(form.type||''));

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
        { field:'type', label:'種類' },
        { field:'language', label:'言語' },
        { field:'toEmail', label:'受信者' },
        { field:'resignReason', label:'退職理由' },
        { field:'purpose', label:'用途' },
        { field:'subject', label:'件名' },
        { field:'bodyText', label:'本文' }
      ]}
    }
    const hasUiResign = JSON.stringify(ui).includes('resignReason')
    if (!hasUiResign) { try{ (ui.form.layout as any[]).splice(3,0,{ field:'resignReason', label:'退職理由' }) }catch{}; needUpdate = true }
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
    if (!payload.type) throw new Error('証明書の種類を選択してください')
    await api.post('/objects/certificate_request', { payload })
    message.value = '申請を送信しました'
    form.purpose=''
  }catch(e:any){ error.value = e?.response?.data?.error || e?.message || '送信に失敗しました' }
  finally{ saving.value=false }
}
</script>

<style scoped>
.certificate-form-page {
  padding: 16px;
}

.certificate-card {
  max-width: 880px;
  margin: 0 auto;
  border-radius: 12px;
  overflow: hidden;
}

.certificate-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.certificate-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.certificate-header__icon {
  font-size: 22px;
  color: #e6a23c;
}

.certificate-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.certificate-header__right {
  display: flex;
  gap: 8px;
}

.certificate-form {
  padding-top: 8px;
}

.certificate-form .el-form-item {
  margin-bottom: 0;
}

.certificate-form :deep(.el-form-item__label) {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 24px 28px;
  padding: 16px;
  background: #f8f9fc;
  border-radius: 8px;
}

.span-2 {
  grid-column: span 2;
}

.certificate-form .email-input {
  width: 100%;
}

.form-messages {
  margin-top: 20px;
  display: flex;
  gap: 16px;
  font-size: 13px;
  letter-spacing: 0.01em;
}

@media (max-width: 960px) {
  .certificate-form-page {
    padding: 12px;
  }

  .certificate-card {
    max-width: 100%;
  }

  .certificate-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 12px;
  }

  .certificate-header__right {
    width: 100%;
  }

  .form-grid {
    grid-template-columns: 1fr;
    gap: 20px;
  }

  .span-2 {
    grid-column: 1 / -1;
  }
}
</style>
