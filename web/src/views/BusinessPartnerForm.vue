<template>
  <!-- 嵌入模式：不显示外层 card，直接渲染表单内容 -->
  <div v-if="isEmbedded" class="partner-form-embedded">
    <template v-if="uiError">
      <el-result icon="error" :title="commonText.loadFailed" :sub-title="uiError" />
    </template>
    <template v-else>
      <DynamicForm
        v-if="!uiLoading && !detailLoading && ui"
        :ui="ui"
        :model="form"
        :readonly="Boolean(props.readonly)"
        @action="onAction"
      />
      <el-skeleton v-else :rows="6" animated />
    </template>
    <!-- 嵌入模式下银行选择由父组件处理 -->
    <div class="form-messages">
      <span v-if="msg" class="text-success">{{ msg }}</span>
      <span v-if="err" class="text-error">{{ err }}</span>
    </div>
  </div>
  <!-- 独立页面模式：显示完整的 card -->
  <div v-else class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><OfficeBuilding /></el-icon>
            <span class="page-header-title">{{ headerTitle }}</span>
          </div>
          <div class="page-actions">
            <el-button type="primary" @click="save" :loading="saving">{{ commonText.save }}</el-button>
          </div>
        </div>
      </template>
      <template v-if="uiError">
        <el-result icon="error" :title="commonText.loadFailed" :sub-title="uiError" />
      </template>
      <template v-else>
        <DynamicForm
          v-if="!uiLoading && !detailLoading && ui"
          :ui="ui"
          :model="form"
          :readonly="Boolean(props.readonly)"
          @action="onAction"
        />
        <el-skeleton v-else :rows="6" animated />
      </template>
      <div class="form-messages">
        <span v-if="msg" class="text-success">{{ msg }}</span>
        <span v-if="err" class="text-error">{{ err }}</span>
      </div>
    </el-card>
    <!-- 银行选择弹窗放在 el-card 外面 -->
    <el-dialog v-model="showBank" :title="accountText.bankDialog" width="720px" append-to-body destroy-on-close>
      <BankBranchPicker mode="bank" @select="onPickBank" @cancel="showBank = false" />
    </el-dialog>
    <el-dialog v-model="showBranch" :title="accountText.branchDialog" width="720px" append-to-body destroy-on-close>
      <BankBranchPicker
        mode="branch"
        :bank-code="form?.bankInfo?.bankCode"
        @select="onPickBranch"
        @cancel="showBranch = false"
      />
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { OfficeBuilding } from '@element-plus/icons-vue'
import api from '../api'
import DynamicForm from '../components/DynamicForm.vue'
import BankBranchPicker from '../components/BankBranchPicker.vue'
import { useI18n } from '../i18n'

const props = defineProps<{
  partnerId?: string | null
  mode?: 'create' | 'edit'
  readonly?: boolean
}>()
const emit = defineEmits<{
  (e: 'saved', id?: string): void
  (e: 'cancel'): void
  (e: 'action', name: string): void
}>()

const { section, lang } = useI18n()
const tableLabels = section({ title:'', new:'', code:'', name:'', type:'', status:'', edit:'' }, (msg) => msg.tables.partners)
const accountText = section({ bankDialog:'', branchDialog:'', bank:'', cash:'', none:'' }, (msg) => msg.tables.accounts)
const commonText = section({ save:'', loadFailed:'', saved:'', saveFailed:'', edit:'', close:'', detail:'' }, (msg) => msg.common)
const schemaText = section({ create:'', refresh:'', createTitle:'', loadFailed:'', layoutMissing:'' }, (msg) => msg.schemaList)
const validationText = section(
  { nameRequired: '' },
  (msg) => ({
    nameRequired: msg.validation?.nameRequired || '取引先名は必須です'
  })
)

const currentId = ref<string | null>(props.partnerId ?? null)
const form = reactive<any>(createEmptyForm())
const ui = ref<any>(null)
const uiLoading = ref(false)
const uiError = ref('')
const saving = ref(false)
const detailLoading = ref(false)
const msg = ref('')
const err = ref('')
const showBank = ref(false)
const showBranch = ref(false)

const isEmbedded = computed(() => Boolean(props.partnerId))
const formMode = computed<'create' | 'edit'>(() => (currentId.value ? 'edit' : (props.mode ?? 'create')))
const headerTitle = computed(() =>
  formMode.value === 'edit'
    ? tableLabels.value.edit || '取引先編集'
    : tableLabels.value.new || '取引先登録'
)

watch(
  () => props.partnerId,
  (val) => {
    currentId.value = val ?? null
    if (currentId.value) {
      loadDetail(currentId.value)
    } else {
      resetForm()
    }
  }
)

onMounted(async () => {
  uiLoading.value = true
  uiError.value = ''
  try {
    const r = await api.get('/schemas/businesspartner', { params: { lang: lang.value } })
    ui.value = r.data?.ui || null
    if (!ui.value) {
      ui.value = {}
    }
  } catch (e: any) {
    uiError.value = e?.response?.data?.error || e?.message || schemaText.value.loadFailed
  } finally {
    uiLoading.value = false
  }
  if (currentId.value) {
    await loadDetail(currentId.value)
  }
  // partnerCode 由后端自动生成，新规时不需要预生成
})

function createEmptyForm() {
  return { flags: { customer: false, vendor: false } }
}

function resetForm() {
  const empty = createEmptyForm()
  Object.keys(form).forEach((key) => {
    delete form[key]
  })
  Object.assign(form, empty)
}

async function loadDetail(id: string) {
  detailLoading.value = true
  err.value = ''
  msg.value = ''
  try {
    const resp = await api.get(`/objects/businesspartner/${id}`)
    const raw = resp.data
    const payload = typeof raw === 'string' ? JSON.parse(raw) : (raw || {})
    resetForm()
    Object.assign(form, payload)
    if (!form.flags) {
      form.flags = { customer: false, vendor: false }
    }
  } catch (e: any) {
    err.value = e?.response?.data?.error || e?.message || commonText.value.loadFailed
  } finally {
    detailLoading.value = false
  }
}

function onAction(name: string) {
  // 嵌入模式下，将 action 发送给父组件处理
  if (isEmbedded.value) {
    if (props.readonly) return
    emit('action', name)
    return
  }
  // 独立模式下，自己处理
  if (name === 'openBankPicker') showBank.value = true
  else if (name === 'openBranchPicker' && form?.bankInfo?.bankCode) showBranch.value = true
}

function onPickBank(row: any) {
  form.bankInfo = form.bankInfo || {}
  form.bankInfo.bankCode = row.payload.bankCode
  // 显示格式：编号 名称
  form.bankInfo.bankName = `${row.payload.bankCode} ${row.payload.name}`
  delete form.bankInfo.branchCode
  delete form.bankInfo.branchName
  showBank.value = false
}

function onPickBranch(row: any) {
  form.bankInfo = form.bankInfo || {}
  form.bankInfo.branchCode = row.payload.branchCode
  // 显示格式：编号 名称
  form.bankInfo.branchName = `${row.payload.branchCode} ${row.payload.branchName}`
  showBranch.value = false
}

// 支付条件描述生成
function generatePaymentTermsDescription(): string {
  const pt = form.paymentTerms
  if (!pt || pt.cutOffDay === undefined || pt.paymentMonth === undefined || pt.paymentDay === undefined) {
    return ''
  }
  
  const cutOffLabel = pt.cutOffDay === 31 ? '月末' : `${pt.cutOffDay}日`
  const monthLabel = pt.paymentMonth === 0 ? '当月' : pt.paymentMonth === 1 ? '翌月' : '翌々月'
  const dayLabel = pt.paymentDay === 31 ? '末' : `${pt.paymentDay}日`
  
  return `${cutOffLabel}締${monthLabel}${dayLabel}払`
}

// 监听支付条件变化，自动生成描述
watch(
  () => [form.paymentTerms?.cutOffDay, form.paymentTerms?.paymentMonth, form.paymentTerms?.paymentDay],
  () => {
    if (form.paymentTerms) {
      form.paymentTerms.description = generatePaymentTermsDescription()
    }
  },
  { deep: true }
)

/** 验证必填字段 */
function validate(): boolean {
  const errors: string[] = []
  
  // 取引先名是必填（唯一的必填字段）
  if (!form.name || typeof form.name !== 'string' || !form.name.trim()) {
    errors.push(validationText.value.nameRequired)
  }
  
  if (errors.length > 0) {
    ElMessage.warning(errors.join('、'))
    return false
  }
  return true
}

async function save() {
  // 先验证必填字段
  if (!validate()) {
    return
  }
  
  saving.value = true
  msg.value = ''
  err.value = ''
  try {
    const payloadClone = JSON.parse(JSON.stringify(form))
    // partnerCode 由后端自动生成，前端不再处理
    const body = { payload: payloadClone }
    if (currentId.value) {
      await api.put(`/objects/businesspartner/${currentId.value}`, body)
    } else {
      const resp = await api.post('/objects/businesspartner', body)
      const insertedRaw = resp.data
      const inserted = typeof insertedRaw === 'string' ? JSON.parse(insertedRaw) : (insertedRaw || {})
      // 从后端返回的数据中获取自动生成的 partnerCode
      if (inserted?.payload?.partnerCode) {
        form.partnerCode = inserted.payload.partnerCode
      }
      currentId.value = inserted?.id || inserted?.payload?.id || currentId.value
    }
    msg.value = commonText.value.saved
    emit('saved', currentId.value)
  } catch (e: any) {
    err.value = e?.response?.data?.error || e?.message || commonText.value.saveFailed
    throw e // 重新抛出让调用方知道失败了
  } finally {
    saving.value = false
  }
}

// 暴露 save 函数供父组件调用
defineExpose({ save, form })
</script>

<style scoped>
.page.page-medium {
  max-width: 960px;
}

/* 标题区域样式 */
.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #409eff;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.partner-form-embedded {
  padding: 0;
}

.form-messages {
  margin-top: 18px;
  display: flex;
  gap: 14px;
  font-size: 13px;
}
</style>
