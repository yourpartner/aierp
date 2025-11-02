<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryBinNew }}</div>
          <div class="page-actions">
            <el-button @click="$router.push('/bins')">{{ commonText.backList }}</el-button>
          </div>
        </div>
      </template>
      <el-form :model="model" label-width="120px" style="max-width:720px">
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
const navText = section({ inventoryBinNew:'' }, (msg) => msg.nav)
const commonText = section({ backList:'', save:'', saved:'', loadFailed:'', saveFailed:'' }, (msg) => msg.common)
const schemaText = section({ create:'', refresh:'', createTitle:'', loadFailed:'', layoutMissing:'' }, (msg) => msg.schemaList)
const tableLabels = section({ title:'', new:'', code:'', name:'', warehouse:'', status:'' }, (msg) => msg.tables.inventoryBins)

const schema = ref<any>({})
const ui = ref<any>({ form: { layout: [] } })
const errorMsg = ref('')
const hasLayout = computed(() => {
  try { return Array.isArray((ui.value as any)?.form?.layout) && ((ui.value as any).form.layout.length > 0) } catch { return false }
})
const model = reactive<any>({})
const saving = ref(false)

async function save() {
  saving.value = true
  try {
    await api.post('/objects/bin', { payload: model })
    ElMessage.success(commonText.value.saved)
  } finally {
    saving.value = false
  }
}

async function loadWarehousesOptions() {
  try {
    const resp = await api.get('/inventory/warehouses')
    const rows = Array.isArray(resp.data) ? resp.data : []
    const opts = rows.map((row: any) => {
      try {
        const code = row?.warehouse_code || row?.payload?.code || ''
        const name = row?.name || row?.payload?.name || code
        return { label: `${name} (${code})`, value: code }
      } catch {
        return null
      }
    }).filter(Boolean)
    const blocks = (ui.value?.form?.layout || []) as any[]
    for (const b of blocks) {
      if (b?.type === 'grid' && Array.isArray(b.cols)) {
        for (const c of b.cols) {
          if (c.field === 'warehouseCode') {
            c.widget = 'select'
            c.props = Object.assign({}, c.props || {}, { options: opts })
          }
        }
      }
    }
  } catch {}
}

async function loadSchema() {
  try {
    const r = await api.get('/schemas/bin', { params: { lang: lang.value } })
    schema.value = r.data?.schema || {}
    ui.value = r.data?.ui || { form: { layout: [] } }
    const ok = Array.isArray((ui.value as any)?.form?.layout) && ((ui.value as any).form.layout.length > 0)
    if (!ok) errorMsg.value = schemaText.value.layoutMissing
    await loadWarehousesOptions()
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

.msgs {
  margin-top: 8px;
}

.err {
  color: #d93025;
}
</style>


