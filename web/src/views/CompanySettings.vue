<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">会社設定</div>
          <div class="page-actions">
            <el-button type="primary" :loading="saving" @click="save">保存</el-button>
          </div>
        </div>
      </template>

      <div v-if="ui && schema">
        <el-form label-width="150px" label-position="left" style="margin-bottom:16px">
          <el-form-item label="社印">
            <div style="display:flex; align-items:center; gap:12px; flex-wrap:wrap">
              <input type="file" accept="image/*" @change="onPickSeal" />
              <div style="display:flex; align-items:center; gap:8px">
                <span>サイズ(pt)</span>
                <el-input-number v-model="model.seal.size" :min="0" :step="1" />
              </div>
              <img v-if="sealPreview" :src="sealPreview" alt="seal" style="height:64px; border:1px solid #eee; padding:2px; border-radius:4px" />
            </div>
          </el-form-item>
          <el-form-item label="決算月" required>
            <el-select v-model="model.fiscalYearEndMonth" placeholder="決算月を選択" style="width: 160px">
              <el-option v-for="m in 12" :key="m" :label="`${m}月`" :value="m" />
            </el-select>
            <div class="tax-hint">※事業年度の最終月を指定してください（例：12月決算の場合は12）</div>
          </el-form-item>
          <el-form-item label="消費税免税期間">
            <div class="tax-period">
              <el-date-picker
                v-model="model.taxExemptFrom"
                type="date"
                value-format="YYYY-MM-DD"
                placeholder="開始日"
                style="width: 160px"
              />
              <span class="tax-period-sep">～</span>
              <el-date-picker
                v-model="model.taxExemptTo"
                type="date"
                value-format="YYYY-MM-DD"
                placeholder="終了日"
                style="width: 160px"
              />
            </div>
          </el-form-item>
          <el-form-item label="進項税計上科目">
            <el-select
              v-model="model.inputTaxAccountCode"
              filterable
              remote
              reserve-keyword
              :remote-method="searchTaxAccounts"
              :loading="taxAccountLoading"
              clearable
              placeholder="科目コードまたは名称で検索"
              style="width:300px"
            >
              <el-option v-for="opt in taxAccountOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
            </el-select>
          </el-form-item>
          <el-form-item label="売上税計上科目">
            <el-select
              v-model="model.outputTaxAccountCode"
              filterable
              remote
              reserve-keyword
              :remote-method="searchTaxAccounts"
              :loading="taxAccountLoading"
              clearable
              placeholder="科目コードまたは名称で検索"
              style="width:300px"
            >
              <el-option v-for="opt in taxAccountOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
            </el-select>
            <div class="tax-hint">※進項税→仮払消費税、売上税→仮受消費税など、仕訳に使用する科目を指定してください。</div>
          </el-form-item>
          <el-form-item label="銀行手数料科目">
            <el-select
              v-model="model.bankFeeAccountCode"
              filterable
              remote
              reserve-keyword
              :remote-method="searchTaxAccounts"
              :loading="taxAccountLoading"
              clearable
              placeholder="科目コードまたは名称で検索"
              style="width:300px"
            >
              <el-option v-for="opt in taxAccountOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
            </el-select>
            <div class="tax-hint">※入金配分時に銀行手数料を計上する科目を指定してください。</div>
          </el-form-item>
        </el-form>
        <DynamicForm :ui="finalUi" :schema="schema" :model="model" />
      </div>
      <div v-else style="color:#9ca3af">スキーマを読み込み中...</div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, computed } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'
import DynamicForm from '../components/DynamicForm.vue'
import { useI18n, getLang } from '../i18n'

const ENTITY = 'company_setting'
const model = reactive<any>({ taxExemptFrom: '', taxExemptTo: '', inputTaxAccountCode: '', outputTaxAccountCode: '', bankFeeAccountCode: '', fiscalYearEndMonth: 12 })
const saving = ref(false)
const ui = ref<any>(null)
function ensureSealDefaults(){
  try{
    if (!model.seal || typeof model.seal !== 'object') (model as any).seal = {}
    if (typeof model.seal.size !== 'number') model.seal.size = 56.7
  }catch{}
}
ensureSealDefaults()
function ensureTaxExemptDefaults(){
  if (model.taxExemptFrom == null) model.taxExemptFrom = ''
  if (model.taxExemptTo == null) model.taxExemptTo = ''
}
ensureTaxExemptDefaults()
function ensureTaxAccountDefaults(){
  if (model.inputTaxAccountCode == null) model.inputTaxAccountCode = ''
  if (model.outputTaxAccountCode == null) model.outputTaxAccountCode = ''
}
ensureTaxAccountDefaults()
function ensureBankFeeAccountDefaults(){
  if (model.bankFeeAccountCode == null) model.bankFeeAccountCode = ''
}
ensureBankFeeAccountDefaults()
function ensureFiscalYearEndMonthDefaults(){
  if (model.fiscalYearEndMonth == null || model.fiscalYearEndMonth < 1 || model.fiscalYearEndMonth > 12) {
    model.fiscalYearEndMonth = 12 // 默认12月决算
  }
}
ensureFiscalYearEndMonthDefaults()
const cleanedUi = computed(()=>{
  const src = ui.value
  if (!src) return null
  try{
    const u = JSON.parse(JSON.stringify(src))
    const layout = u?.form?.layout
    if (!Array.isArray(layout)) return src
    let totalCols = 0
    for (const blk of layout){
      if (blk?.cols && Array.isArray(blk.cols)){
        blk.cols = blk.cols.filter((c:any)=>{
          const f = c?.field||''
          if (f.startsWith('seal.')) return f==='seal.size'
          return true
        })
        totalCols += blk.cols.length
      }
    }
    // 若清理后导致无任何可见列，则退回原始 ui，避免“空表单”
    if (totalCols===0) return src
    return u
  }catch{ return src }
})
const finalUi = computed(()=>{
  const base = cleanedUi.value || ui.value
  // 若无 form/layout，则基于 schema 自动生成一份简单布局
  const hasLayout = !!(base && base.form && Array.isArray(base.form.layout) && base.form.layout.length>0)
  if (hasLayout) return base
  try{
    let s:any = schema.value || {}
    if (typeof s === 'string') { try { s = JSON.parse(s) } catch { s = {} } }
    const props = s.properties || {}
    const cols:any[] = []
    for (const k of Object.keys(props)){
      if (k==='seal') continue
      cols.push({ field:k, label:k, span:6, props: props[k]?.type==='number' ? { type:'number' } : {} })
    }
    if (cols.length===0) return base
    return { form: { layout: [ { type:'grid', cols } ] } }
  }catch{ return base }
})
const sealPreview = ref<string>('')
const schema = ref<any>(null)
const currentId = ref<string>('')
const taxAccountOptions = ref<{ label: string; value: string }[]>([])
const taxAccountLoading = ref(false)

async function ensureAccountOption(code: string) {
  if (!code) return
  if (taxAccountOptions.value.some(opt => opt.value === code)) return
  try {
    taxAccountLoading.value = true
    const resp = await api.post('/objects/account/search', {
      page: 1,
      pageSize: 1,
      where: [{ field: 'account_code', op: 'eq', value: code }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    rows.forEach((row: any) => {
      const label = `${row.payload?.name || ''} (${row.account_code})`
      const value = row.account_code
      if (value && !taxAccountOptions.value.some(opt => opt.value === value)) {
        taxAccountOptions.value.push({ label, value })
      }
    })
  } catch (err) {
    console.error('ensureAccountOption failed', err)
  } finally {
    taxAccountLoading.value = false
  }
}

async function searchTaxAccounts(query: string) {
  taxAccountLoading.value = true
  try {
    const q = (query || '').trim()
    const where: any[] = []
    if (q) {
      where.push({ json: 'name', op: 'contains', value: q })
      where.push({ field: 'account_code', op: 'contains', value: q })
    }
    const resp = await api.post('/objects/account/search', {
      page: 1,
      pageSize: 50,
      where,
      orderBy: [{ field: 'account_code', dir: 'ASC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    taxAccountOptions.value = rows.map((row: any) => ({
      label: `${row.payload?.name || ''} (${row.account_code})`,
      value: row.account_code
    }))
  } catch (err) {
    console.error('searchTaxAccounts failed', err)
  } finally {
    taxAccountLoading.value = false
  }
}

async function ensureSchema(){
  try{
    const lang = getLang()
    const r = await api.get(`/schemas/${ENTITY}`, { params: { lang } })
    const doc = r.data || {}
    ui.value = doc.ui || {}
    schema.value = doc.schema || { type:'object', properties:{} }
    ensureSealDefaults()
    ensureTaxExemptDefaults()
    ensureTaxAccountDefaults()
    ensureBankFeeAccountDefaults()
    ensureFiscalYearEndMonthDefaults()
  }catch(e:any){
    // 后端未注册 schema：自动注册一次（全局 schema 或公司级均可复用）
    const doc = {
      schema: {
        type:'object',
        properties:{
          companyName:{ type:'string' },
          companyAddress:{ type:'string' },
          companyRep:{ type:'string' },
          workdayDefaultStart:{ type:'string', pattern:'^\\d{2}:\\d{2}$' },
          workdayDefaultEnd:{ type:'string', pattern:'^\\d{2}:\\d{2}$' },
          lunchMinutes:{ type:'number', minimum:0, maximum:240 }
        },
        required:[]
      },
      ui: { form: { layout: [ { type:'grid', cols:[
        { field:'companyName', label:'会社名', span:12 },
        { field:'companyAddress', label:'所在地', span:12 },
        { field:'companyRep', label:'代表者', span:6 },
        { field:'workdayDefaultStart', label:'始業(HH:mm)', span:6 },
        { field:'workdayDefaultEnd', label:'終業(HH:mm)', span:6 },
        { field:'lunchMinutes', label:'休憩(分)', span:6, props:{ type:'number' } }
      ] } ] } },
      query: { filters:['created_at'], sorts:['created_at'] },
      core_fields: { coreFields: [] },
      validators: [], numbering: {}, ai_hints: {}
    } as any
    await api.post(`/schemas/${ENTITY}`, doc)
    // 重新获取
    const lang = getLang()
    const r2 = await api.get(`/schemas/${ENTITY}`, { params: { lang } })
    const d2 = r2.data || {}
    ui.value = d2.ui || {}
    schema.value = d2.schema || { type:'object', properties:{} }
    ensureTaxExemptDefaults()
    ensureTaxAccountDefaults()
    ensureBankFeeAccountDefaults()
    ensureFiscalYearEndMonthDefaults()
  }
}

async function load(){
  await ensureSchema()
  try{
    // 取本公司唯一一条设置（若多条，取最新）
    const r = await api.post(`/objects/${ENTITY}/search`, { page:1, pageSize:1, where:[], orderBy:[{ field:'created_at', dir:'DESC' }] })
    const rows:any[] = r.data?.data || []
    if (rows.length>0){
      currentId.value = rows[0].id
      const payload = rows[0].payload || {}
      Object.assign(model, payload)
      ensureSealDefaults()
      ensureTaxExemptDefaults()
      ensureTaxAccountDefaults()
      ensureBankFeeAccountDefaults()
      ensureFiscalYearEndMonthDefaults()
      await Promise.all([
        ensureAccountOption(model.inputTaxAccountCode),
        ensureAccountOption(model.outputTaxAccountCode),
        ensureAccountOption(model.bankFeeAccountCode)
      ])
    } else {
      // 默认值
      Object.assign(model, { workdayDefaultStart:'09:00', workdayDefaultEnd:'18:00', lunchMinutes:60, taxExemptFrom:'', taxExemptTo:'', inputTaxAccountCode:'', outputTaxAccountCode:'', bankFeeAccountCode:'', fiscalYearEndMonth: 12 })
      currentId.value = ''
      ensureSealDefaults()
      ensureTaxExemptDefaults()
      ensureTaxAccountDefaults()
      ensureBankFeeAccountDefaults()
      ensureFiscalYearEndMonthDefaults()
    }
  }catch(e:any){ console.error(e?.response?.data || e) }
}

async function save(){
  saving.value = true
  try{
    // company_setting 采用 UPSERT：统一 POST 到 /objects/company_setting
    await api.post(`/objects/${ENTITY}`, { payload: model })
    ElMessage.success('保存しました')
    if (typeof window !== 'undefined') {
      const companyName = typeof model.companyName === 'string' ? model.companyName.trim() : ''
      window.dispatchEvent(new CustomEvent('company-settings-updated', { detail: { companyName } }))
    }
  }catch(e:any){
    console.error(e?.response?.data||e)
    ElMessage.error(e?.response?.data?.error || e.message || '保存に失敗しました')
  }finally{ saving.value = false }
}

onMounted(load)

function onPickSeal(e: Event){
  const input = e.target as HTMLInputElement
  const file = input.files && input.files[0]
  if (!file) return
  const reader = new FileReader()
  reader.onload = () => {
    const dataUrl = String(reader.result||'')
    const idx = dataUrl.indexOf(',')
    const base64 = idx>=0 ? dataUrl.substring(idx+1) : dataUrl
    const format = (file.type||'image/png').split('/')[1] || 'png'
    if (!model.seal) model.seal = {}
    model.seal.format = format
    model.seal.plainBase64 = base64
    if (typeof model.seal.size !== 'number') model.seal.size = 56.7
    sealPreview.value = dataUrl
    ElMessage.success('社印を選択しました。保存後に暗号化して保存します。')
  }
  reader.readAsDataURL(file)
}
</script>

<style scoped>
.page.page-medium { max-width: 900px; }
.form-row{ display:flex; gap:16px }
.form-row .el-form-item{ flex:1 }
.tax-period{ display:flex; align-items:center; gap:8px; flex-wrap:wrap }
.tax-period-sep{ color:#6b7280 }
.tax-hint{ font-size:12px; color:#6b7280; margin-left:12px }
</style>


