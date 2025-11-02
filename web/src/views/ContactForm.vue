<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ tableLabels.new }}</div>
          <div class="page-actions">
            <el-button type="primary" @click="save" :loading="saving">{{ commonText.save }}</el-button>
          </div>
        </div>
      </template>
      <template v-if="uiError">
        <el-result icon="error" :title="commonText.loadFailed" :sub-title="uiError" />
      </template>
      <template v-else>
        <DynamicForm v-if="!uiLoading && ui" :ui="ui" :model="form" />
        <el-skeleton v-else :rows="6" animated />
      </template>
      <div class="form-messages">
        <span v-if="msg" class="text-success">{{ msg }}</span>
        <span v-if="err" class="text-error">{{ err }}</span>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import api from '../api'
import DynamicForm from '../components/DynamicForm.vue'
import { useI18n } from '../i18n'

const { section, lang } = useI18n()
const tableLabels = section({ title:'', new:'', name:'', code:'', email:'', phone:'', status:'' }, (msg) => msg.tables.contacts)
const commonText = section({ save:'', loadFailed:'', saved:'', saveFailed:'' }, (msg) => msg.common)
const schemaText = section({ create:'', refresh:'', createTitle:'', loadFailed:'', layoutMissing:'' }, (msg) => msg.schemaList)

const form = reactive<any>({})
const ui = ref<any>(null)
const uiLoading = ref(false)
const uiError = ref('')
const saving = ref(false)
const msg = ref('')
const err = ref('')

onMounted(async () => {
  uiLoading.value = true
  uiError.value = ''
  try {
    const r = await api.get('/schemas/contact', { params: { lang: lang.value } })
    ui.value = r.data?.ui || null
    if (!ui.value || !Array.isArray(ui.value?.form?.layout) || ui.value.form.layout.length === 0) {
      uiError.value = schemaText.value.layoutMissing
    }
  } catch (e: any) {
    uiError.value = e?.response?.data?.error || e?.message || schemaText.value.loadFailed
  } finally {
    uiLoading.value = false
  }
})

async function save() {
  saving.value = true
  msg.value = ''
  err.value = ''
  try {
    const body = { payload: JSON.parse(JSON.stringify(form)) }
    await api.post('/objects/contact', body)
    msg.value = commonText.value.saved
  } catch (e: any) {
    err.value = e?.response?.data?.error || e?.message || commonText.value.saveFailed
  } finally {
    saving.value = false
  }
}
</script>

<style scoped>
.page.page-medium {
  max-width: 960px;
}

.form-messages {
  margin-top: 18px;
  display: flex;
  gap: 14px;
  font-size: 13px;
}
</style>


