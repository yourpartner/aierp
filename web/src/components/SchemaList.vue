<template>
  <div>
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px">
      <span style="font-weight:600">{{ resolvedTitle }}</span>
      <div>
        <el-button v-if="createPath" type="primary" @click="onCreate">{{ schemaText.create }}</el-button>
        <el-button @click="load" :loading="loading">{{ schemaText.refresh }}</el-button>
      </div>
    </div>
    <el-table :data="rows" stripe style="width: 100%" v-loading="loading" @row-click="onRowClick">
      <el-table-column v-for="c in columns" :key="c" :prop="c" :label="columnLabel(c)" :min-width="minWidth(c)" />
    </el-table>
    <el-dialog v-model="creating" :title="schemaText.createTitle" width="600px" append-to-body destroy-on-close>
      <el-form :model="createModel" label-width="100px">
        <el-alert v-if="errorMsg" :title="errorMsg" type="error" show-icon style="margin-bottom:12px" />
        <DynamicForm v-if="!errorMsg && hasLayout" :ui="schemaDoc?.ui || {}" :schema="schemaDoc?.schema || {}" :model="createModel" />
      </el-form>
      <template #footer>
        <el-button @click="creating=false">{{ commonText.close }}</el-button>
        <el-button type="primary" :loading="saving" @click="save">{{ commonText.save }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { onMounted, ref, reactive, computed, watchEffect } from 'vue'
import api from '../api'
import { ElMessage } from 'element-plus'
import DynamicForm from './DynamicForm.vue'
import { useI18n } from '../i18n'

const { text, common, lang } = useI18n()
const schemaLabels = computed(() => text.value?.schemaLabels ?? {})
const defaultSchemaText = { create:'', refresh:'', createTitle:'', loadFailed:'', layoutMissing:'' }
const defaultCommonText = { close:'', save:'', saved:'', saveFailed:'' }

const schemaText = reactive({ ...defaultSchemaText })
const commonText = reactive({ ...defaultCommonText })

watchEffect(() => {
  Object.assign(schemaText, defaultSchemaText, text.value?.schemaList ?? {})
  Object.assign(commonText, defaultCommonText, common.value ?? {})
})

const emit = defineEmits<{ (e: 'row-click', row: any): void }>()
const props = defineProps<{ entity: string; title?: string; createPath?: string; createInModal?: boolean }>()
const rows = ref<any[]>([])
const loading = ref(false)
const schemaDoc = ref<any>(null)
const creating = ref(false)
const saving = ref(false)
const errorMsg = ref('')
const createModel = reactive<any>({})

const resolvedTitle = computed(() => props.title || props.entity)
const createPath = computed(() => (props.createPath === '' ? '' : (props.createPath || `/${props.entity}/new`)))
const useModal = computed(() => props.createInModal !== false)
const hasLayout = computed(() => {
  try { return Array.isArray(schemaDoc.value?.ui?.form?.layout) && (schemaDoc.value.ui.form.layout.length>0) } catch { return false }
})

const columns = computed<string[]>(() => {
  const uiCols = schemaDoc.value?.ui?.list?.columns
  if (Array.isArray(uiCols) && uiCols.length>0) return uiCols
  // fallback：用返回行的 keys（首行）
  const row = rows.value[0]
  if (row) {
    const keys = Object.keys(row)
    return keys.filter(k => !['payload'].includes(k))
  }
  return []
})
function minWidth(c:string){ return /name|summary|payload/.test(c) ? 240 : 120 }

function normalizeKey(input: string){
  return input.toLowerCase().replace(/[^a-z0-9]/g, '')
}

function columnLabel(c: string){
  const map = schemaLabels.value || {}
  const candidates: string[] = [c, normalizeKey(c)]
  if (c.includes('.')) {
    const last = c.split('.').pop() as string
    candidates.push(last)
    candidates.push(normalizeKey(last))
  }
  for (const key of candidates) {
    if (map[key] !== undefined) return map[key]
  }
  return c
}

async function load(){
  loading.value = true
  try {
    try{ const r = await api.get(`/schemas/${props.entity}`, { params: { lang: lang.value } }); schemaDoc.value = r.data || {} } catch {}
    const orderBy = (Array.isArray(schemaDoc.value?.query?.sorts) && schemaDoc.value.query.sorts.length>0)
      ? [{ field: schemaDoc.value.query.sorts[0], dir: 'ASC' }]
      : []
    const r2 = await api.post(`/objects/${props.entity}/search`, { where: [], page:1, pageSize: 200, orderBy })
    rows.value = Array.isArray(r2.data?.data) ? r2.data.data : []
  } finally { loading.value = false }
}

onMounted(load)

function onRowClick(row: any){
  emit('row-click', row)
}

async function onCreate(){
  if (!useModal.value) { (window as any).$router?.push?.(createPath.value); return }
  errorMsg.value = ''
  // 清空模型
  for (const k of Object.keys(createModel)) delete (createModel as any)[k]
  creating.value = true
  // 打开弹窗前强制刷新一次 schema，避免使用旧缓存
  try{
    const r = await api.get(`/schemas/${props.entity}`, { params: { lang: lang.value } })
    schemaDoc.value = r.data || {}
    const ok = Array.isArray(schemaDoc.value?.ui?.form?.layout) && (schemaDoc.value.ui.form.layout.length>0)
    if (!ok) errorMsg.value = schemaText.layoutMissing
  }catch(e:any){
    errorMsg.value = `${schemaText.loadFailed}: ${e?.response?.status ?? ''} ${e?.response?.data?.error || e?.message || ''}`
  }
}

async function save(){
  saving.value = true
  try{
    await api.post(`/objects/${props.entity}`, { payload: createModel })
    ElMessage.success(commonText.saved)
    creating.value = false
    await load()
  } catch(e:any){
    errorMsg.value = e?.response?.data?.error || e?.message || commonText.saveFailed
  } finally { saving.value = false }
}

defineExpose({ reload: load, rows })
</script>


