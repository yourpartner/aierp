<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ title }}</div>
          <div class="page-actions">
            <!-- actions are handled inside AccountEditor for consistent UX -->
            <el-button @click="backToList">{{ commonText.backList }}</el-button>
          </div>
        </div>
      </template>

      <AccountEditor mode="create" @saved="onSaved" @cancel="backToList" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, inject } from 'vue'
import AccountEditor from '../components/AccountEditor.vue'
import { getLang, useI18n } from '../i18n'
import { useRouter } from 'vue-router'
const router = useRouter()
const openEmbed = inject<(key: string, payload?: any) => void>('chatkitOpenEmbed', undefined)
const { section, text, lang } = useI18n()
const commonText = section({ save:'', saved:'', saveFailed:'', backList:'' }, (msg) => msg.common)
const accountsText = computed(() => text.value?.tables?.accounts || {})
const title = computed(() => text.value?.nav?.accountNew || accountsText.value?.new || '科目登録')
function backToList(){
  if (openEmbed){
    openEmbed('accounts.list')
    return
  }
  router.push('/accounts')
}

function onSaved() {
  // after creating an account, return to list for a consistent flow
  backToList()
}
</script>

<style scoped>

</style>


