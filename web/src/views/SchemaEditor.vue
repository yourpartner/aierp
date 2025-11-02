<template>
  <div class="schema-editor">
    <el-card>
      <template #header>{{ navText.schemaEditor }}</template>
      <div class="schema-toolbar">
        <label>{{ editorText.entity }}</label>
        <el-select v-model="entity" filterable @change="loadSchema" style="width:220px">
          <el-option v-for="n in names" :key="n" :label="n" :value="n" />
        </el-select>
        <el-button type="primary" @click="loadSchema">{{ buttonsText.refresh }}</el-button>
        <el-button @click="saveSchema">{{ editorText.saveNew }}</el-button>
        <span v-if="saving" class="saving">{{ editorText.saving }}</span>
        <span v-if="message" class="message">{{ message }}</span>
        <span v-if="error" class="error">{{ error }}</span>
      </div>
      <div ref="editorEl" class="schema-editor__panel"></div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref } from 'vue'
import api from '../api'
import JSONEditor from 'jsoneditor'
import 'jsoneditor/dist/jsoneditor.css'
import { useI18n } from '../i18n'

const { section } = useI18n()
const navText = section({ schemaEditor:'' }, (msg) => msg.nav)
const buttonsText = section({ refresh:'', close:'', search:'' }, (msg) => msg.buttons)
const commonText = section({ loadFailed:'', saveFailed:'' }, (msg) => msg.common)
const editorText = section({ entity:'', saveNew:'', saving:'', saved:'' }, (msg) => msg.schemaEditor)

const editorEl = ref<HTMLDivElement | null>(null)
let editor: JSONEditor | null = null

const entity = ref('voucher')
const names = ref<string[]>(['voucher','businesspartner'])
const saving = ref(false)
const message = ref('')
const error = ref('')

async function loadNames() {
  try {
    const r = await api.get('/schemas')
    const arr = Array.isArray(r.data) ? r.data : []
    names.value = arr.map((x: any) => x.name)
    if (!names.value.includes(entity.value) && names.value.length) {
      entity.value = names.value[0]
    }
  } catch (e: any) {
    error.value = e?.response?.data?.error || e?.message || commonText.value.loadFailed
  }
}

async function loadSchema() {
  try {
    const r = await api.get(`/schemas/${entity.value}`)
    const json = r.data?.schema || {}
    editor?.set(json)
    message.value = ''
  } catch (e: any) {
    error.value = e?.response?.data?.error || e?.message || commonText.value.loadFailed
  }
}

async function saveSchema() {
  if (!editor) return
  saving.value = true
  error.value = ''
  message.value = ''
  try {
    const json = editor.get()
    await api.post(`/schemas/${entity.value}`, { schema: json })
    message.value = editorText.value.saved
  } catch (e: any) {
    error.value = e?.response?.data?.error || e?.message || commonText.value.saveFailed
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  editor = new JSONEditor(editorEl.value!, { mode: 'code', mainMenuBar: false, navigationBar: false })
  await loadNames()
  await loadSchema()
})

onBeforeUnmount(() => {
  if (editor) {
    editor.destroy()
    editor = null
  }
})
</script>

<style scoped>
.schema-editor { padding: 16px; max-width: 1200px; }
.schema-toolbar { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; flex-wrap: wrap; }
.schema-editor__panel { height: 70vh; border: 1px solid var(--el-border-color); border-radius: 8px; overflow: hidden; }
.saving { color: var(--el-color-primary); }
.message { color: #16a34a; }
.error { color: #dc2626; }
</style>
