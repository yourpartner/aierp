<template>
  <div class="page">
    <el-card>
      <template #header>
        <span>{{ navText.inventoryStatuses }}</span>
      </template>
      <SchemaList entity="stock_status" :title="navText.inventoryStatuses" :create-in-modal="true" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import SchemaList from '../components/SchemaList.vue'
import { useI18n } from '../i18n'

const { section } = useI18n()
const navText = section({ inventoryStatuses:'' }, (msg) => msg.nav)
</script>


