<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryBatches }}</div>
          <div class="page-actions">
            <el-button type="primary" size="small" @click="$router.push('/batch/new')">{{ navText.inventoryBatchNew }}</el-button>
          </div>
        </div>
      </template>
      <SchemaList ref="schemaListRef" entity="batch" hide-header />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import SchemaList from '../components/SchemaList.vue'
import { useI18n } from '../i18n'

const { section } = useI18n()
const navText = section({ inventoryBatches: '', inventoryBatchNew: '' }, (msg) => msg.nav)
const schemaListRef = ref<any>(null)
</script>

<style scoped>
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.page-header-title {
  font-weight: 600;
  font-size: 15px;
  color: #303133;
}
.page-actions {
  display: flex;
  gap: 8px;
}
</style>
