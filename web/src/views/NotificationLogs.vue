<template>
  <div class="page">
    <el-card>
      <SchemaList entity="notification_log" :title="navText.notifLogs" :create-path="''" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import SchemaList from '../components/SchemaList.vue'
import { useI18n } from '../i18n'

const { section } = useI18n()
const navText = section({ notifLogs:'' }, (msg) => msg.nav)
</script>


