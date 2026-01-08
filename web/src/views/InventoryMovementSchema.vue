<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryMovement }}</div>
          <div class="page-actions">
            <el-button :loading="saving" :disabled="!!errorMsg" type="primary" @click="save">{{ commonText.save }}</el-button>
          </div>
        </div>
      </template>
      <el-form :model="model" label-width="120px">
        <el-alert v-if="errorMsg" :title="errorMsg" type="error" show-icon style="margin-bottom:12px" />
        <DynamicForm v-if="!errorMsg && hasLayout" :ui="ui" :schema="schema" :model="model" />
      </el-form>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref, computed, onMounted } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'
import DynamicForm from '../components/DynamicForm.vue'
import { useI18n } from '../i18n'

const { section, lang } = useI18n()
const navText = section({ inventoryMovement:'' }, (msg) => msg.nav)
const commonText = section({ save:'', saved:'', saveFailed:'', loadFailed:'', close:'', enabled:'', disabled:'' }, (msg) => msg.common)
const schemaText = section({ create:'', refresh:'', createTitle:'', loadFailed:'', layoutMissing:'' }, (msg) => msg.schemaList)

const schema = ref<any>({})
const ui = ref<any>({ form: { layout: [] } })
const errorMsg = ref('')
const hasLayout = computed(() => {
  try { return Array.isArray((ui.value as any)?.form?.layout) && ((ui.value as any).form.layout.length > 0) } catch { return false }
})
const model = reactive<any>({ movementType: 'IN', movementDate: new Date().toISOString().slice(0, 10), lines: [] })
const saving = ref(false)

async function loadSchema() {
  try {
    const r = await api.get('/schemas/inventory_movement', { params: { lang: lang.value } })
    schema.value = r.data?.schema || {}
    ui.value = r.data?.ui || { form: { layout: [] } }
    const ok = Array.isArray((ui.value as any)?.form?.layout) && ((ui.value as any).form.layout.length > 0)
    if (!ok) errorMsg.value = schemaText.value.layoutMissing
  } catch (e: any) {
    errorMsg.value = `${schemaText.value.loadFailed}: ${e?.response?.status || e?.message || ''}`
  }
}

onMounted(loadSchema)

async function save() {
  saving.value = true
  try {
    await api.post('/objects/inventory_movement', { payload: model })
    ElMessage.success(commonText.value.saved)
  } finally {
    saving.value = false
  }
}
</script>

<style scoped>
.page.page-medium {
  max-width: 1200px;
}
</style>


