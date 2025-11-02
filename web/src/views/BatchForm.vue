<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryBatchNew }}</div>
          <div class="page-actions">
            <el-button @click="$router.push('/batches')">{{ commonText.backList }}</el-button>
          </div>
        </div>
      </template>
      <el-form :model="model" label-width="120px" style="max-width:720px">
        <el-alert v-if="errorMsg" :title="errorMsg" type="error" show-icon style="margin-bottom:12px" />
        <DynamicForm v-if="!errorMsg" :ui="ui" :schema="schema" :model="model" />
        <el-form-item>
          <el-button type="primary" :loading="saving" :disabled="!!errorMsg" @click="save">{{ commonText.save }}</el-button>
        </el-form-item>
      </el-form>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'
import DynamicForm from '../components/DynamicForm.vue'
import { useI18n } from '../i18n'

const { section, lang } = useI18n()
const navText = section({ inventoryBatchNew:'' }, (msg) => msg.nav)
const commonText = section({ backList:'', save:'', saved:'', loadFailed:'', saveFailed:'' }, (msg) => msg.common)
const schemaText = section({ create:'', refresh:'', createTitle:'', loadFailed:'', layoutMissing:'' }, (msg) => msg.schemaList)

const schema = ref<any>({})
const ui = ref<any>({ form: { layout: [] } })
const model = reactive<any>({})
const saving = ref(false)
const errorMsg = ref('')

async function save() {
  saving.value = true
  try {
    await api.post('/objects/batch', { payload: model })
    ElMessage.success(commonText.value.saved)
  } finally {
    saving.value = false
  }
}

async function loadSchema() {
  try {
    const r = await api.get('/schemas/batch', { params: { lang: lang.value } })
    schema.value = r.data?.schema || {}
    ui.value = r.data?.ui || { form: { layout: [] } }
    const ok = Array.isArray((ui.value as any)?.form?.layout) && ((ui.value as any).form.layout.length > 0)
    if (!ok) errorMsg.value = schemaText.value.layoutMissing
  } catch (e: any) {
    errorMsg.value = `${schemaText.value.loadFailed}: ${e?.message || ''}`
  }
}

loadSchema()
</script>

<style scoped>
.page.page-medium {
  max-width: 900px;
}
</style>


