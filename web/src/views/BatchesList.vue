<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryBatches }}</div>
          <div class="page-actions">
            <el-button type="primary" size="small" @click="$router.push('/batch/new')">{{ navText.inventoryBatchNew }}</el-button>
            <el-button size="small" @click="loadData" :loading="loading">更新</el-button>
          </div>
        </div>
      </template>

      <el-table :data="rows" stripe v-loading="loading" border>
        <el-table-column label="品目コード" prop="material_code" width="160" />
        <el-table-column label="品目名" min-width="200">
          <template #default="{ row }">{{ materialNames[row.material_code] || '-' }}</template>
        </el-table-column>
        <el-table-column label="Lot番号" prop="batch_no" width="180" />
        <el-table-column label="在庫数量" width="120" align="right">
          <template #default="{ row }">
            <span v-if="stockQty[row.material_code + '|' + row.batch_no] !== undefined">
              {{ stockQty[row.material_code + '|' + row.batch_no] }}
            </span>
            <span v-else style="color:#c0c4cc">-</span>
          </template>
        </el-table-column>
        <el-table-column label="製造日" prop="mfg_date" width="130" />
        <el-table-column label="有効期限" prop="exp_date" width="130" />
      </el-table>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import api from '../api'
import { useI18n } from '../i18n'

const { section } = useI18n()
const navText = section({ inventoryBatches: '', inventoryBatchNew: '' }, (msg) => msg.nav)

const loading = ref(false)
const rows = ref<any[]>([])
const materialNames = ref<Record<string, string>>({})
const stockQty = ref<Record<string, number>>({})

async function loadData() {
  loading.value = true
  try {
    const resp = await api.post('/objects/batch/search', { where: [], page: 1, pageSize: 500 })
    rows.value = resp.data?.data || []

    // Fetch material names
    const codes = [...new Set(rows.value.map((r: any) => r.material_code).filter(Boolean))]
    if (codes.length > 0) {
      const mResp = await api.post('/objects/material/search', {
        where: [{ field: 'material_code', op: 'in', value: codes }],
        limit: codes.length
      })
      for (const m of mResp.data?.data || []) {
        materialNames.value[m.material_code] = m.payload?.name || m.payload?.materialName || m.material_code
      }
    }

    // Fetch stock quantities per material+batch
    await loadStockQuantities()
  } finally {
    loading.value = false
  }
}

async function loadStockQuantities() {
  try {
    const resp = await api.post('/objects/inventory_balance/search', {
      where: [],
      limit: 2000
    })
    const balances = resp.data?.data || []
    const qtyMap: Record<string, number> = {}
    for (const b of balances) {
      const key = (b.material_code || '') + '|' + (b.batch_no || '')
      qtyMap[key] = (qtyMap[key] || 0) + (Number(b.quantity) || 0)
    }
    stockQty.value = qtyMap
  } catch { /* ignore */ }
}

onMounted(loadData)
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
