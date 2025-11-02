<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryWarehouseNew }}</div>
          <div class="page-actions">
            <el-button @click="$router.push('/warehouses')">{{ commonText.backList }}</el-button>
          </div>
        </div>
      </template>
      <el-form :model="model" label-width="100px" style="max-width:720px">
        <el-alert v-if="errorMsg" :title="errorMsg" type="error" show-icon style="margin-bottom:12px" />
        <DynamicForm v-if="!errorMsg && hasLayout" :ui="ui" :schema="schema" :model="model" />
        <el-form-item>
          <el-button type="primary" :loading="saving" :disabled="!!errorMsg" @click="save">{{ commonText.save }}</el-button>
        </el-form-item>
      </el-form>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref, computed } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'
import DynamicForm from '../components/DynamicForm.vue'
import { useI18n } from '../i18n'

const { section, lang } = useI18n()
const navText = section({ inventoryWarehouseNew:'' }, (msg) => msg.nav)
const commonText = section({ backList:'', save:'', saved:'', loadFailed:'', saveFailed:'' }, (msg) => msg.common)
const schemaText = section({ create:'', refresh:'', createTitle:'', loadFailed:'', layoutMissing:'' }, (msg) => msg.schemaList)

const schema = ref<any>({})
const ui = ref<any>({ form: { layout: [] } })
const errorMsg = ref('')
const hasLayout = computed(() => {
  try { return Array.isArray((ui.value as any)?.form?.layout) && ((ui.value as any).form.layout.length > 0) } catch { return false }
})
const model = reactive<any>({})
const saving = ref(false)

function genWhCode() {
  const d = new Date()
  const yy = String(d.getFullYear() % 100).padStart(2, '0')
  const mm = String(d.getMonth() + 1).padStart(2, '0')
  const dd = String(d.getDate()).padStart(2, '0')
  const hh = String(d.getHours()).padStart(2, '0')
  const mi = String(d.getMinutes()).padStart(2, '0')
  const ss = String(d.getSeconds()).padStart(2, '0')
  const rnd = Math.floor(Math.random() * 100).toString().padStart(2, '0')
  return `WH${yy}${mm}${dd}${hh}${mi}${ss}${rnd}`
}

async function save() {
  saving.value = true
  try {
    if (!model.code) model.code = genWhCode()
    await api.post('/objects/warehouse', { payload: model })
    ElMessage.success(commonText.value.saved)
  } finally {
    saving.value = false
  }
}

async function loadSchema() {
  try {
    const r = await api.get('/schemas/warehouse', { params: { lang: lang.value } })
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


