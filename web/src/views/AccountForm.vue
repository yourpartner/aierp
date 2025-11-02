<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ title }}</div>
          <div class="page-actions">
            <el-button type="primary" :loading="saving" @click="submit">{{ commonText.save }}</el-button>
            <el-button @click="backToList">{{ commonText.backList }}</el-button>
          </div>
        </div>
      </template>

      <DynamicForm v-if="ui" :ui="tunedUi" :model="form" @action="onAction" />

      

      <!-- 银行/支店选择弹窗 -->
      <el-dialog v-model="showBank" title="选择银行" width="720px">
        <BankBranchPicker mode="bank" @select="onPickBank" @cancel="showBank=false" />
      </el-dialog>
      <el-dialog v-model="showBranch" title="选择支店" width="720px">
        <BankBranchPicker mode="branch" :bank-code="form?.bankInfo?.bankCode" @select="onPickBranch" @cancel="showBranch=false" />
      </el-dialog>

      <div class="msgs">
        <span v-if="message" class="ok">{{ message }}</span>
        <span v-if="error" class="err">{{ error }}</span>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref, onMounted, watch, computed, inject } from 'vue'
import api from '../api'
import DynamicForm from '../components/DynamicForm.vue'
import BankBranchPicker from '../components/BankBranchPicker.vue'
import { getLang, useI18n } from '../i18n'
import { useRouter } from 'vue-router'

const form = reactive<any>({})
const saving = ref(false)
const message = ref('')
const error = ref('')
const ui = ref<any>(null)
const router = useRouter()
const openEmbed = inject<(key: string, payload?: any) => void>('chatkitOpenEmbed', undefined)
const { section, text } = useI18n()
const commonText = section({ save:'', saved:'', saveFailed:'', backList:'' }, (msg) => msg.common)
const title = computed(() => text.value?.nav?.accountNew || text.value?.tables?.accounts?.new || '科目登録')
// 调整 UI：移除银行/支店名称的可编辑输入框；保留 schema 中的按钮与只读文本
const tunedUi = computed(() => {
  const u = JSON.parse(JSON.stringify(ui.value || {}))
  function walk(blocks: any[]) {
    if (!Array.isArray(blocks)) return
    for (const b of blocks) {
      if (b?.type === 'grid' && Array.isArray(b.cols)) {
        b.cols = b.cols.filter((c:any) => {
          const isBankField = c?.field === 'bankInfo.bankName' || c?.field === 'bankInfo.branchName'
          const isReadonlyWidget = c?.widget === 'text' || c?.widget === 'button'
          // 仅移除可编辑输入框，保留按钮与只读文本
          if (isBankField && !isReadonlyWidget && (c?.widget === 'input' || !c?.widget)) return false
          return true
        })
      } else if (b?.type === 'section' && Array.isArray(b.layout)) {
        walk(b.layout)
      }
    }
  }
  if (u?.form?.layout) walk(u.form.layout)
  return u
})
const showBank = ref(false)
const showBranch = ref(false)

async function submit() {
  saving.value = true
  message.value = ''
  error.value = ''
  try {
    const body = { payload: { ...form } }
    await api.post('/objects/account', body)
    message.value = commonText.value.saved || '保存成功'
  } catch (e:any) {
    error.value = e?.response?.data?.error || e?.message || commonText.value.saveFailed || '保存失败'
  } finally {
    saving.value = false
  }
}

function backToList(){
  if (openEmbed){
    openEmbed('accounts.list')
    return
  }
  router.push('/accounts')
}

onMounted(async () => {
  try {
    const r = await api.get('/schemas/account', { params: { lang: getLang(), _: Date.now() } })
    ui.value = r.data?.ui || null
    // 初始化默认值
    if (form.code === undefined) form.code = ''
    if (form.name === undefined) form.name = ''
    if (form.category === undefined) form.category = 'PL'
    if (form.openItem === undefined) form.openItem = false
    if (form.openItemBaseline === undefined) form.openItemBaseline = null
    if (form.isBank === undefined) form.isBank = false
    if (form.isCash === undefined) form.isCash = false
  } catch {}
})

// 互斥与联动清理
watch(() => form.isBank, (v:boolean) => {
  if (v) {
    form.isCash = false
    form.bankInfo = form.bankInfo || {}
  } else {
    // 关闭银行科目时清空银行信息
    delete form.bankInfo
  }
})
watch(() => form.isCash, (v:boolean) => {
  if (v) {
    form.isBank = false
  } else {
    // 关闭现金科目时清空币种
    if (form.cashCurrency) delete form.cashCurrency
  }
})

// 供 UI 绑定的工具：通过按钮打开选择器
function openBankPicker(){ showBank.value = true }
function openBranchPicker(){ if (form?.bankInfo?.bankCode) showBranch.value = true }
function onPickBank(row:any){
  form.bankInfo = form.bankInfo || {}
  form.bankInfo.bankCode = row.payload.bankCode
  form.bankInfo.bankName = row.payload.name
  // 切换银行时清空既有支店
  delete form.bankInfo.branchCode
  delete form.bankInfo.branchName
  showBank.value = false
}
function onPickBranch(row:any){
  form.bankInfo = form.bankInfo || {}
  form.bankInfo.branchCode = row.payload.branchCode
  form.bankInfo.branchName = row.payload.branchName
  showBranch.value = false
}

// DynamicForm schema-action 统一入口
function onAction(name:string){
  if (name === 'openBankPicker') return openBankPicker()
  if (name === 'openBranchPicker') return openBranchPicker()
}

// 暴露通用意图填充方法（被 ChatKit 调用）
function applyIntent(payload:any){
  if (!payload) return
  if (typeof payload.code === 'string') form.code = payload.code
  if (typeof payload.name === 'string') form.name = payload.name
}
defineExpose({ applyIntent })
</script>

<style scoped>

</style>


