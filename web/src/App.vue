<template>
  <div class="app-shell">
    <header class="app-header">
      <div class="title">{{ t('appTitle') }}</div>
      <div class="lang-switch">
        <el-select v-model="langVal" size="small" style="width:120px" @change="onLangChange">
          <el-option v-for="item in langOptions" :key="item.value" :label="item.label" :value="item.value" />
        </el-select>
      </div>
    </header>
    <main class="app-body">
      <router-view />
    </main>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n, setLang, getLang } from './i18n'
import api from './api'

const { t } = useI18n()

const langOptions = [
  { value: 'ja', label: '日本語' },
  { value: 'en', label: 'English' },
  { value: 'zh', label: '中文' }
]

const langVal = computed({
  get: () => getLang(),
  set: (val) => setLang(val as any)
})

function onLangChange(val: string) {
  setLang(val as any)
}

const companyName = ref('')
const localizedAppTitle = computed(() => t('appTitle'))
const pageTitle = computed(() => {
  const base = localizedAppTitle.value || 'AIMate'
  const company = (companyName.value || '').trim()
  return company ? `${base} - ${company}` : base
})

watch(pageTitle, (val) => {
  if (typeof document !== 'undefined') {
    document.title = val || 'AIMate'
  }
}, { immediate: true })

async function loadCompanyName() {
  try {
    const resp = await api.post('/objects/company_setting/search', {
      page: 1,
      pageSize: 1,
      where: [],
      orderBy: [{ field: 'created_at', dir: 'DESC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    const name = rows[0]?.payload?.companyName
    companyName.value = typeof name === 'string' ? name.trim() : ''
  } catch {
    companyName.value = ''
  }
}

type CompanySettingsPayload = { companyName?: string }

function handleCompanySettingsUpdated(event: Event) {
  const detail = (event as CustomEvent<CompanySettingsPayload>).detail
  if (detail && typeof detail.companyName === 'string') {
    const value = detail.companyName.trim()
    companyName.value = value
    return
  }
  void loadCompanyName()
}

onMounted(() => {
  void loadCompanyName()
  if (typeof window !== 'undefined') {
    window.addEventListener('company-settings-updated', handleCompanySettingsUpdated as EventListener)
  }
})

onBeforeUnmount(() => {
  if (typeof window !== 'undefined') {
    window.removeEventListener('company-settings-updated', handleCompanySettingsUpdated as EventListener)
  }
})
</script>

<style>
html,body,#app{height:100%;margin:0;}
.app-shell{display:flex;flex-direction:column;height:100%;}
.app-header{display:flex;justify-content:space-between;align-items:center;padding:8px 16px;border-bottom:1px solid #e5e7eb;background:#fff;}
.app-body{flex:1;overflow:auto;background:#f5f7fb;}
.title{font-size:18px;font-weight:600;color:#111827;}
</style>
