<template>
  <div class="page page-medium">
    <el-card class="material-card">
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryMaterialNew }}</div>
          <div class="page-actions">
            <el-button type="primary" :loading="saving" @click="save">{{ commonText.save }}</el-button>
            <el-button @click="goList">{{ commonText.backList }}</el-button>
          </div>
        </div>
      </template>
      <div class="material-body">
        <template v-if="uiError">
          <el-result icon="error" :title="commonText.loadFailed" :sub-title="uiError" />
        </template>
        <template v-else>
          <el-skeleton v-if="uiLoading" :rows="6" animated />
          <template v-else>
            <DynamicForm v-if="hasLayout" :ui="ui" :schema="schema" :model="model" class="material-form" />
            <el-alert v-else :title="schemaText.layoutMissing" type="warning" show-icon />
          </template>
        </template>
      </div>
      <div class="form-messages">
        <span v-if="msg" class="text-success">{{ msg }}</span>
        <span v-if="err" class="text-error">{{ err }}</span>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref, computed, onMounted } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'
import DynamicForm from '../components/DynamicForm.vue'
import { useI18n } from '../i18n'
import { useRouter } from 'vue-router'

const { section, lang } = useI18n()
const router = useRouter()
const navText = section({ inventoryMaterialNew:'' }, (msg) => msg.nav)
const commonText = section({ backList:'', save:'', saved:'', loadFailed:'', saveFailed:'', close:'' }, (msg) => msg.common)
const schemaText = section({ create:'', refresh:'', createTitle:'', loadFailed:'', layoutMissing:'' }, (msg) => msg.schemaList)

const schema = ref<any>({})
const ui = ref<any>(null)
const uiLoading = ref(false)
const uiError = ref('')
const hasLayout = computed(() => {
  try { return Array.isArray((ui.value as any)?.form?.layout) && ((ui.value as any).form.layout.length > 0) } catch { return false }
})
const model = reactive<any>({ code: '', name: '', baseUom: 'EA', batchManagement: false, spec: '', description: '' })
const saving = ref(false)
const msg = ref('')
const err = ref('')

function goList() {
  router.push('/materials')
}

async function save() {
  saving.value = true
  msg.value = ''
  err.value = ''
  try {
    const payload = JSON.parse(JSON.stringify(model))
    await api.post('/objects/material', { payload })
    msg.value = commonText.value.saved
    ElMessage.success(commonText.value.saved)
  } catch (e: any) {
    const message = e?.response?.data?.error || e?.message || commonText.value.saveFailed
    err.value = message
    ElMessage.error(message)
  } finally {
    saving.value = false
  }
}

async function loadSchema() {
  uiLoading.value = true
  uiError.value = ''
  try {
    const r = await api.get('/schemas/material', { params: { lang: lang.value } })
    schema.value = r.data?.schema || {}
    ui.value = r.data?.ui || null
  } catch (e: any) {
    uiError.value = e?.response?.data?.error || e?.message || schemaText.value.loadFailed
  } finally {
    uiLoading.value = false
  }
}

onMounted(loadSchema)
</script>

<style scoped>
.page.page-medium {
  max-width: 900px;
}

.material-card {
  display: flex;
  flex-direction: column;
}

.material-body {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.material-form {
  max-width: 880px;
}

.material-form :deep(.el-form-item) {
  margin-bottom: 16px;
}

.form-messages {
  margin-top: 18px;
  display: flex;
  gap: 14px;
  font-size: 13px;
  min-height: 18px;
}

.text-success {
  color: var(--el-color-success, #22c55e);
}

.text-error {
  color: var(--el-color-error, #ef4444);
}
</style>


