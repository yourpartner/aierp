<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryBalances }}</div>
          <div class="page-actions">
            <el-button :loading="loading" @click="load">{{ buttons.search }}</el-button>
          </div>
        </div>
      </template>
      <div class="filters">
        <el-input v-model="q.materialCode" :placeholder="tableLabels.material" class="filter-item" />
        <el-input v-model="q.warehouseCode" :placeholder="tableLabels.warehouse" class="filter-item" />
        <el-input v-model="q.binCode" :placeholder="tableLabels.bin" class="filter-item" />
        <el-input v-model="q.statusCode" :placeholder="tableLabels.status" class="filter-item" />
        <el-input v-model="q.batchNo" :placeholder="tableLabels.batch" class="filter-item" />
      </div>
      <el-table :data="rows" stripe style="width: 100%" v-loading="loading">
        <el-table-column prop="material_code" :label="tableLabels.material" min-width="200" />
        <el-table-column prop="warehouse_code" :label="tableLabels.warehouse" width="160" />
        <el-table-column prop="bin_code" :label="tableLabels.bin" width="140" />
        <el-table-column prop="status_code" :label="tableLabels.status" width="140" />
        <el-table-column prop="batch_no" :label="tableLabels.batch" min-width="160" />
        <el-table-column prop="quantity" :label="tableLabels.quantity" width="120" />
      </el-table>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import api from '../api'
import { useI18n } from '../i18n'

const { section } = useI18n()
const tableLabels = section({ material:'', warehouse:'', bin:'', status:'', batch:'', quantity:'' }, (msg) => msg.tables.inventoryBalances)
const buttons = section({ search:'', refresh:'', close:'' }, (msg) => msg.buttons)
const navText = section({ inventoryBalances:'' }, (msg) => msg.nav)

const q = reactive<any>({ materialCode: '', warehouseCode: '', binCode: '', statusCode: '', batchNo: '' })
const rows = ref<any[]>([])
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    const r = await api.get('/inventory/balances/search', { params: q })
    rows.value = Array.isArray(r.data) ? r.data : []
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.filters {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 8px;
}

.filter-item {
  max-width: 200px;
}
</style>


