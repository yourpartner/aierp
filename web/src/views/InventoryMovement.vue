<template>
  <div class="page page-medium">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">在庫移動登録</div>
          <div class="page-actions">
            <el-button :loading="saving" type="primary" @click="save">登録</el-button>
          </div>
        </div>
      </template>

      <el-alert v-if="errorMsg" :title="errorMsg" type="error" show-icon :closable="false" style="margin-bottom:12px" />

      <el-form label-position="left" label-width="100px" style="padding: 0 8px;">
        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="移動タイプ" required>
              <el-select v-model="model.movementType" style="width:100%" @change="onTypeChange">
                <el-option label="入庫 (IN)" value="IN" />
                <el-option label="出庫 (OUT)" value="OUT" />
                <el-option label="移動 (TRANSFER)" value="TRANSFER" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="移動日" required>
              <el-date-picker v-model="model.movementDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="参照番号">
              <el-select
                v-model="model.referenceNo"
                filterable
                clearable
                placeholder="参照を選択..."
                style="width:100%"
                :loading="refLoading"
                @change="onRefChange"
              >
                <el-option
                  v-for="r in refOptions"
                  :key="r.value"
                  :label="r.label"
                  :value="r.value"
                />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <!-- 出庫元 -->
        <el-row :gutter="20" v-if="model.movementType === 'OUT' || model.movementType === 'TRANSFER'">
          <el-col :span="12">
            <el-form-item label="出庫倉庫">
              <el-select v-model="model.fromWarehouse" style="width:100%" placeholder="倉庫を選択" clearable>
                <el-option v-for="w in warehouses" :key="w.warehouse_code" :label="w.payload?.name ? w.payload.name + ' (' + w.warehouse_code + ')' : w.warehouse_code" :value="w.warehouse_code" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="出庫棚番">
              <el-select v-model="model.fromBin" style="width:100%" placeholder="棚番を選択" clearable>
                <el-option v-for="b in allBins.filter(x => x.warehouse_code === model.fromWarehouse)" :key="b.bin_code" :label="b.bin_code" :value="b.bin_code" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <!-- 入庫先 -->
        <el-row :gutter="20" v-if="model.movementType === 'IN' || model.movementType === 'TRANSFER'">
          <el-col :span="12">
            <el-form-item label="入庫倉庫">
              <el-select v-model="model.toWarehouse" style="width:100%" placeholder="倉庫を選択" clearable>
                <el-option v-for="w in warehouses" :key="w.warehouse_code" :label="w.payload?.name ? w.payload.name + ' (' + w.warehouse_code + ')' : w.warehouse_code" :value="w.warehouse_code" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="入庫棚番">
              <el-select v-model="model.toBin" style="width:100%" placeholder="棚番を選択" clearable>
                <el-option v-for="b in allBins.filter(x => x.warehouse_code === model.toWarehouse)" :key="b.bin_code" :label="b.bin_code" :value="b.bin_code" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>

      <!-- 明細 -->
      <div class="section-title" style="padding: 0 8px;">明細</div>
      <el-table :data="model.lines" border size="small" style="margin: 0 8px 12px 8px; width: calc(100% - 16px);">
        <el-table-column label="行" width="60" align="center">
          <template #default="{ row }">{{ row.lineNo }}</template>
        </el-table-column>
        <el-table-column label="品目" min-width="250">
          <template #default="{ row }">
            <el-select
              v-model="row.materialCode"
              filterable
              remote
              reserve-keyword
              :remote-method="(q: string) => searchMaterials(q)"
              :loading="materialLoading"
              placeholder="品目を選択..."
              size="small"
              style="width:100%"
            >
              <el-option v-for="m in materialOptions" :key="m.code" :label="m.label" :value="m.code" />
            </el-select>
          </template>
        </el-table-column>
        <el-table-column label="数量" width="110">
          <template #default="{ row }">
            <el-input-number v-model="row.quantity" :min="0" :controls="false" size="small" style="width:100%" />
          </template>
        </el-table-column>
        <el-table-column label="単位" width="80">
          <template #default="{ row }">
            <el-input v-model="row.uom" size="small" style="width:100%" />
          </template>
        </el-table-column>
        <el-table-column label="ロット番号" width="140">
          <template #default="{ row }">
            <el-input v-model="row.batchNo" size="small" placeholder="任意" style="width:100%" />
          </template>
        </el-table-column>
        <el-table-column label="" width="50">
          <template #default="{ $index }">
            <el-button type="danger" text size="small" @click="model.lines.splice($index, 1)">
              <el-icon><Delete /></el-icon>
            </el-button>
          </template>
        </el-table-column>
      </el-table>
      <div style="padding: 0 8px;">
        <el-button size="small" @click="addLine"><el-icon><Plus /></el-icon> 行を追加</el-button>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted, watch } from 'vue'
import { Plus, Delete } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import api from '../api'

const saving = ref(false)
const errorMsg = ref('')
const refLoading = ref(false)
const materialLoading = ref(false)

const model = reactive({
  movementType: 'IN',
  movementDate: new Date().toISOString().slice(0, 10),
  referenceNo: '',
  fromWarehouse: '',
  fromBin: '',
  toWarehouse: '',
  toBin: '',
  lines: [] as any[]
})

const warehouses = ref<any[]>([])
const allBins = ref<any[]>([])
const refOptions = ref<{ label: string; value: string; data?: any }[]>([])
const materialOptions = ref<{ code: string; label: string }[]>([])

// Reference data cache
const refDataCache = ref<Record<string, any>>({})

async function loadWarehouses() {
  try {
    const resp = await api.post('/objects/warehouse/search', { limit: 200 })
    warehouses.value = resp.data?.data || []
  } catch { /* ignore */ }
}

async function loadAllBins() {
  try {
    const resp = await api.post('/objects/bin/search', { limit: 500 })
    allBins.value = resp.data?.data || []
  } catch { /* ignore */ }
}

async function loadRefOptions() {
  refLoading.value = true
  refOptions.value = []
  refDataCache.value = {}
  try {
    if (model.movementType === 'IN') {
      // Load POs not fully received
      const resp = await api.post('/objects/purchase_order/search', {
        where: [{ field: 'status', op: 'in', value: ['new', 'partial_received'] }],
        orderBy: [{ field: 'order_date', direction: 'desc' }],
        limit: 200
      })
      const pos = resp.data?.data || []
      refOptions.value = pos.map((po: any) => ({
        label: `${po.po_no} (${po.order_date || ''} - ${po.partner_code || ''})`,
        value: po.po_no,
        data: po
      }))
      for (const po of pos) {
        refDataCache.value[po.po_no] = po
      }
    } else if (model.movementType === 'OUT') {
      // Load SOs not fully shipped
      const resp = await api.post('/objects/sales_order/search', {
        where: [{ field: 'status', op: 'in', value: ['new', 'confirmed', 'partial_shipped'] }],
        orderBy: [{ field: 'order_date', direction: 'desc' }],
        limit: 200
      })
      const sos = resp.data?.data || []
      refOptions.value = sos.map((so: any) => ({
        label: `${so.order_no || so.payload?.orderNo || so.id?.slice(0,8)} (${so.order_date || ''} - ${so.partner_code || ''})`,
        value: so.order_no || so.payload?.orderNo || so.id,
        data: so
      }))
      for (const so of sos) {
        const key = so.order_no || so.payload?.orderNo || so.id
        refDataCache.value[key] = so
      }
    }
    // TRANSFER: no reference
  } catch { /* ignore */ }
  finally { refLoading.value = false }
}

function onTypeChange() {
  model.referenceNo = ''
  model.fromWarehouse = ''
  model.fromBin = ''
  model.toWarehouse = ''
  model.toBin = ''
  model.lines = []
  loadRefOptions()
}

function onRefChange(val: string) {
  if (!val) return
  const data = refDataCache.value[val]
  if (!data?.payload?.lines) return

  // Auto-populate lines from PO/SO
  const lines: any[] = []
  for (const line of data.payload.lines) {
    if (line.status === 'cancelled') continue
    const ordered = line.quantity || 0
    const fulfilled = line.receivedQuantity || line.shippedQuantity || 0
    const remaining = ordered - fulfilled
    if (remaining <= 0) continue

    lines.push({
      lineNo: lines.length + 1,
      materialCode: line.materialCode || '',
      quantity: remaining,
      uom: line.uom || '',
      batchNo: ''
    })

    // Pre-populate material options for display
    if (line.materialCode && line.materialName) {
      const exists = materialOptions.value.find(m => m.code === line.materialCode)
      if (!exists) {
        materialOptions.value.push({
          code: line.materialCode,
          label: `${line.materialCode} - ${line.materialName}`
        })
      }
    }
  }
  model.lines = lines
}

async function searchMaterials(query: string) {
  if (!query || query.length < 1) return
  materialLoading.value = true
  try {
    const resp = await api.post('/objects/material/search', {
      where: [{ field: 'name', op: 'contains', value: query }],
      limit: 20
    })
    const results = (resp.data?.data || []).map((m: any) => ({
      code: m.material_code,
      label: `${m.material_code} - ${m.payload?.name || m.payload?.materialName || ''}`
    }))
    // Also search by code
    const resp2 = await api.post('/objects/material/search', {
      where: [{ field: 'material_code', op: 'contains', value: query }],
      limit: 20
    })
    for (const m of resp2.data?.data || []) {
      if (!results.find((r: any) => r.code === m.material_code)) {
        results.push({
          code: m.material_code,
          label: `${m.material_code} - ${m.payload?.name || m.payload?.materialName || ''}`
        })
      }
    }
    materialOptions.value = results
  } catch { /* ignore */ }
  finally { materialLoading.value = false }
}

function addLine() {
  const maxLineNo = model.lines.reduce((max: number, l: any) => Math.max(max, l.lineNo || 0), 0)
  model.lines.push({
    lineNo: maxLineNo + 1,
    materialCode: '',
    quantity: 0,
    uom: '',
    batchNo: ''
  })
}

async function save() {
  if (!model.movementType) { errorMsg.value = '移動タイプを選択してください'; return }
  if (!model.movementDate) { errorMsg.value = '移動日を選択してください'; return }
  const validLines = model.lines.filter(l => l.materialCode && l.quantity > 0)
  if (validLines.length === 0) { errorMsg.value = '品目と数量を入力してください'; return }

  saving.value = true
  errorMsg.value = ''
  try {
    await api.post('/inventory/movements', {
      movementType: model.movementType,
      movementDate: model.movementDate,
      referenceNo: model.referenceNo || undefined,
      fromWarehouse: model.fromWarehouse || undefined,
      fromBin: model.fromBin || undefined,
      toWarehouse: model.toWarehouse || undefined,
      toBin: model.toBin || undefined,
      lines: validLines.map((l, idx) => ({
        lineNo: idx + 1,
        materialCode: l.materialCode,
        quantity: l.quantity,
        uom: l.uom,
        batchNo: l.batchNo || undefined,
        fromWarehouse: model.fromWarehouse || undefined,
        fromBin: model.fromBin || undefined,
        toWarehouse: model.toWarehouse || undefined,
        toBin: model.toBin || undefined
      }))
    })
    ElMessage.success('在庫移動を登録しました')
    // Reset form
    model.referenceNo = ''
    model.lines = []
  } catch (e: any) {
    errorMsg.value = e?.response?.data?.error || '登録に失敗しました'
  } finally {
    saving.value = false
  }
}

onMounted(() => {
  loadWarehouses()
  loadAllBins()
  loadRefOptions()
})
</script>

<style scoped>
.page.page-medium { max-width: 1200px; }
.page-header { display: flex; align-items: center; justify-content: space-between; }
.page-header-title { font-size: 18px; font-weight: 600; }
.page-actions { display: flex; gap: 8px; }
.section-title { font-size: 14px; font-weight: 600; margin-bottom: 8px; color: #303133; }
</style>
