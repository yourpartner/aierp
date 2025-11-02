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
import { computed } from 'vue'
import { useI18n, setLang, getLang } from './i18n'

const { t } = useI18n()

const langOptions = [
  { value: 'ja', label: '日本語' },
  { value: 'en', label: 'English' }
]

const langVal = computed({
  get: () => getLang(),
  set: (val) => setLang(val as any)
})

function onLangChange(val: string) {
  setLang(val as any)
}
</script>

<style>
html,body,#app{height:100%;margin:0;}
.app-shell{display:flex;flex-direction:column;height:100%;}
.app-header{display:flex;justify-content:space-between;align-items:center;padding:8px 16px;border-bottom:1px solid #e5e7eb;background:#fff;}
.app-body{flex:1;overflow:auto;background:#f5f7fb;}
.title{font-size:18px;font-weight:600;color:#111827;}
</style>
