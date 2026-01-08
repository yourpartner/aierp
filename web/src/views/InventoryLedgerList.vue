<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">在庫台帳</div>
          <div class="page-actions">
            <el-button :loading="loading" @click="load">{{ buttons.refresh }}</el-button>
          </div>
        </div>
      </template>
      <div class="filters">
        <el-input v-model="q.materialCode" placeholder="品目コード" class="filter-item" clearable />
        <el-select v-model="q.warehouseCode" placeholder="倉庫" class="filter-item" clearable>
          <el-option v-for="w in warehouses" :key="w.warehouse_code" :label="w.warehouse_name || w.warehouse_code" :value="w.warehouse_code" />
        </el-select>
        <el-select v-model="q.movementType" placeholder="移動タイプ" class="filter-item" clearable>
          <el-option label="入庫 (IN)" value="IN" />
          <el-option label="出庫 (OUT)" value="OUT" />
          <el-option label="移動 (TRANSFER)" value="TRANSFER" />
          <el-option label="棚卸増加" value="COUNT_GAIN" />
          <el-option label="棚卸減少" value="COUNT_LOSS" />
        </el-select>
        <el-date-picker v-model="q.dateRange" type="daterange" start-placeholder="開始日" end-placeholder="終了日" class="filter-item date-range" value-format="YYYY-MM-DD" />
        <el-button type="primary" :loading="loading" @click="load">検索</el-button>
      </div>
      <el-table :data="filteredRows" stripe style="width: 100%" v-loading="loading" max-height="600">
        <el-table-column prop="movement_date" label="日付" width="110" sortable>
          <template #default="{ row }">{{ formatDate(row.movement_date) }}</template>
        </el-table-column>
        <el-table-column prop="movement_type" label="タイプ" width="100">
          <template #default="{ row }">
            <el-tag :type="getTypeTagType(row.movement_type)" size="small">{{ getTypeLabel(row.movement_type) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="material_code" label="品目" min-width="180" />
        <el-table-column prop="quantity" label="数量" width="100" align="right">
          <template #default="{ row }">
            <span :class="{ 'text-success': row.movement_type === 'IN' || row.movement_type === 'COUNT_GAIN', 'text-danger': row.movement_type === 'OUT' || row.movement_type === 'COUNT_LOSS' }">
              {{ row.movement_type === 'OUT' || row.movement_type === 'COUNT_LOSS' ? '-' : '+' }}{{ row.quantity }}
            </span>
          </template>
        </el-table-column>
        <el-table-column prop="uom" label="単位" width="70" />
        <el-table-column label="出庫元" min-width="140">
          <template #default="{ row }">
            <span v-if="row.from_warehouse">{{ row.from_warehouse }}<span v-if="row.from_bin"> / {{ row.from_bin }}</span></span>
            <span v-else class="text-muted">-</span>
          </template>
        </el-table-column>
        <el-table-column label="入庫先" min-width="140">
          <template #default="{ row }">
            <span v-if="row.to_warehouse">{{ row.to_warehouse }}<span v-if="row.to_bin"> / {{ row.to_bin }}</span></span>
            <span v-else class="text-muted">-</span>
          </template>
        </el-table-column>
        <el-table-column prop="batch_no" label="ロット" min-width="120">
          <template #default="{ row }">{{ row.batch_no || '-' }}</template>
        </el-table-column>
        <el-table-column prop="status_code" label="ステータス" width="100">
          <template #default="{ row }">{{ row.status_code || '-' }}</template>
        </el-table-column>
        <el-table-column prop="created_at" label="登録日時" width="160">
          <template #default="{ row }">{{ formatDateTime(row.created_at) }}</template>
        </el-table-column>
      </el-table>
      <div class="table-footer">
        <span class="record-count">{{ filteredRows.length }} 件</span>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref, computed, onMounted } from 'vue'
import api from '../api'
import { useI18n } from '../i18n'

const { section } = useI18n()
const buttons = section({ search:'', refresh:'', close:'' }, (msg) => msg.buttons)

const q = reactive<any>({ materialCode: '', warehouseCode: '', movementType: '', dateRange: null })
const rows = ref<any[]>([])
const warehouses = ref<any[]>([])
const loading = ref(false)

const filteredRows = computed(() => {
  let result = rows.value
  if (q.materialCode) {
    result = result.filter(r => r.material_code?.toLowerCase().includes(q.materialCode.toLowerCase()))
  }
  if (q.warehouseCode) {
    result = result.filter(r => r.from_warehouse === q.warehouseCode || r.to_warehouse === q.warehouseCode)
  }
  if (q.movementType) {
    result = result.filter(r => r.movement_type === q.movementType)
  }
  if (q.dateRange && q.dateRange.length === 2) {
    const [start, end] = q.dateRange
    result = result.filter(r => {
      const d = r.movement_date
      return d >= start && d <= end
    })
  }
  return result
})

function formatDate(dateStr: string) {
  if (!dateStr) return '-'
  return dateStr.substring(0, 10)
}

function formatDateTime(dateStr: string) {
  if (!dateStr) return '-'
  const d = new Date(dateStr)
  return d.toLocaleString('ja-JP', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
}

function getTypeLabel(type: string) {
  const map: Record<string, string> = {
    'IN': '入庫',
    'OUT': '出庫',
    'TRANSFER': '移動',
    'COUNT_GAIN': '棚卸+',
    'COUNT_LOSS': '棚卸-',
    'DELIVERED': '納品'
  }
  return map[type] || type
}

function getTypeTagType(type: string) {
  const map: Record<string, string> = {
    'IN': 'success',
    'OUT': 'danger',
    'TRANSFER': 'warning',
    'COUNT_GAIN': 'success',
    'COUNT_LOSS': 'danger',
    'DELIVERED': 'info'
  }
  return map[type] || 'info'
}

async function loadWarehouses() {
  try {
    const r = await api.get('/inventory/warehouses')
    warehouses.value = Array.isArray(r.data) ? r.data : []
  } catch { /* ignore */ }
}

async function load() {
  loading.value = true
  try {
    const r = await api.get('/inventory/inventory_ledger')
    rows.value = Array.isArray(r.data) ? r.data : []
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  loadWarehouses()
  load()
})
</script>

<style scoped>
.filters {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 12px;
  align-items: center;
}

.filter-item {
  max-width: 180px;
}

.date-range {
  max-width: 280px !important;
}

.text-success {
  color: #67c23a;
  font-weight: 500;
}

.text-danger {
  color: #f56c6c;
  font-weight: 500;
}

.text-muted {
  color: #909399;
}

.table-footer {
  margin-top: 12px;
  display: flex;
  justify-content: flex-end;
}

.record-count {
  color: #909399;
  font-size: 13px;
}
</style>

