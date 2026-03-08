<template>
  <div class="page page-wide">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">入庫一覧</div>
          <div class="page-actions">
            <el-upload
              :show-file-list="false"
              :auto-upload="false"
              :on-change="onFileSelected"
              accept=".pdf,.png,.jpg,.jpeg,.gif,.webp"
              style="display:inline-block; margin-right:8px"
            >
              <el-button :loading="recognizing">
                <el-icon><Upload /></el-icon> AI識別
              </el-button>
            </el-upload>
            <el-button type="primary" @click="openCreateDialog">
              <el-icon><Plus /></el-icon> 新規入庫
            </el-button>
          </div>
        </div>
      </template>

      <!-- 搜索 -->
      <div class="search-bar">
        <el-input v-model="searchQuery" placeholder="発注番号・品目で検索..." clearable style="width:300px" @keyup.enter="loadData">
          <template #prefix><el-icon><Search /></el-icon></template>
        </el-input>
        <el-date-picker v-model="dateRange" type="daterange" start-placeholder="開始日" end-placeholder="終了日" value-format="YYYY-MM-DD" @change="loadData" />
        <el-button @click="loadData"><el-icon><Refresh /></el-icon></el-button>
      </div>

      <!-- 一覧テーブル -->
      <el-table :data="list" v-loading="loading" border stripe style="cursor:pointer" @row-click="onRowClick">
        <el-table-column label="入庫日" width="120" prop="movement_date" />
        <el-table-column label="発注番号" width="160">
          <template #default="{ row }">{{ row.reference_no || '-' }}</template>
        </el-table-column>
        <el-table-column label="倉庫" width="120">
          <template #default="{ row }">{{ row.to_warehouse || '-' }}</template>
        </el-table-column>
        <el-table-column label="棚番" width="100">
          <template #default="{ row }">{{ row.payload?.toBin || '-' }}</template>
        </el-table-column>
        <el-table-column label="品目" min-width="250">
          <template #default="{ row }">
            <div v-for="(line, i) in (row.payload?.lines || [])" :key="i" class="line-item">
              {{ materialNames[line.materialCode] || line.materialCode }} × {{ line.quantity }} {{ line.uom || '' }}
            </div>
          </template>
        </el-table-column>
        <el-table-column label="参照" width="100">
          <template #default="{ row }">
            <el-tag v-if="row.payload?.referenceType === 'purchase_order'" size="small" type="info">発注</el-tag>
            <el-tag v-else-if="row.payload?.referenceType === 'manual'" size="small">手動</el-tag>
            <el-tag v-else-if="row.payload?.referenceType === 'ai_recognize'" size="small" type="warning">AI</el-tag>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="100" fixed="right">
          <template #default="{ row }">
            <el-button type="danger" text size="small" @click.stop="deleteReceipt(row)">削除</el-button>
          </template>
        </el-table-column>
      </el-table>

      <!-- ページング -->
      <div class="pagination-bar" v-if="total > pageSize">
        <el-pagination
          v-model:current-page="page"
          :page-size="pageSize"
          :total="total"
          layout="total, prev, pager, next"
          @current-change="loadData"
        />
      </div>
    </el-card>

    <!-- 新規入庫ダイアログ -->
    <el-dialog v-model="showCreate" title="入庫登録" width="900px" :close-on-click-modal="false" destroy-on-close>
      <el-alert v-if="createErr" type="error" :title="createErr" show-icon :closable="false" style="margin-bottom:16px" />

      <el-form label-position="left" label-width="90px">
        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item label="入庫日" required>
              <el-date-picker v-model="createForm.movementDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="倉庫" required>
              <el-select v-model="createForm.toWarehouse" placeholder="倉庫" style="width:100%" @change="onWarehouseChange">
                <el-option v-for="w in warehouses" :key="w.warehouse_code" :label="w.warehouse_code + (w.payload?.name ? ' - ' + w.payload.name : '')" :value="w.warehouse_code" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="棚番">
              <el-select v-model="createForm.toBin" placeholder="棚番" style="width:100%" :loading="binsLoading">
                <el-option v-for="b in bins" :key="b.bin_code" :label="b.bin_code" :value="b.bin_code" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="発注番号">
              <el-select v-model="createForm.poNo" filterable clearable placeholder="発注番号（任意）" style="width:100%" @change="onPoSelected">
                <el-option v-for="po in poOptions" :key="po.po_no" :label="po.po_no + ' - ' + (po.payload?.partnerName || po.partner_code)" :value="po.po_no" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="備考">
              <el-input v-model="createForm.memo" placeholder="備考" />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>

      <!-- 明細 -->
      <div class="section-title">入庫明細</div>
      <el-table :data="createForm.lines" border size="small" style="margin-bottom:12px">
        <el-table-column label="品目コード" width="160">
          <template #default="{ row, $index }">
            <el-select
              v-model="row.materialCode"
              filterable
              remote
              reserve-keyword
              :remote-method="(q: string) => searchMaterials(q, $index)"
              :loading="materialLoading"
              placeholder="品目..."
              size="small"
              style="width:100%"
            >
              <el-option v-for="m in materialOptions" :key="m.code" :label="m.label" :value="m.code" />
            </el-select>
          </template>
        </el-table-column>
        <el-table-column label="品目名" min-width="180">
          <template #default="{ row }">{{ materialNames[row.materialCode] || '' }}</template>
        </el-table-column>
        <el-table-column label="数量" width="120">
          <template #default="{ row }">
            <el-input-number v-model="row.quantity" :min="0" :controls="false" size="small" style="width:100%" />
          </template>
        </el-table-column>
        <el-table-column label="単位" width="80">
          <template #default="{ row }">
            <el-input v-model="row.uom" size="small" />
          </template>
        </el-table-column>
        <el-table-column label="" width="60">
          <template #default="{ $index }">
            <el-button type="danger" text size="small" @click="createForm.lines.splice($index, 1)">
              <el-icon><Delete /></el-icon>
            </el-button>
          </template>
        </el-table-column>
      </el-table>
      <el-button size="small" @click="addLine"><el-icon><Plus /></el-icon> 行追加</el-button>

      <template #footer>
        <el-button @click="showCreate = false">キャンセル</el-button>
        <el-button type="primary" :loading="creating" @click="doCreate">登録</el-button>
      </template>
    </el-dialog>

    <!-- 詳細ダイアログ -->
    <el-dialog v-model="showDetail" title="入庫詳細" width="800px" destroy-on-close>
      <template v-if="detailData">
        <el-descriptions :column="3" border size="small">
          <el-descriptions-item label="入庫日">{{ detailData.movement_date }}</el-descriptions-item>
          <el-descriptions-item label="発注番号">{{ detailData.reference_no || '-' }}</el-descriptions-item>
          <el-descriptions-item label="倉庫">{{ detailData.to_warehouse || '-' }}</el-descriptions-item>
          <el-descriptions-item label="棚番">{{ detailData.payload?.toBin || '-' }}</el-descriptions-item>
          <el-descriptions-item label="参照タイプ">{{ detailData.payload?.referenceType || '-' }}</el-descriptions-item>
          <el-descriptions-item label="備考">{{ detailData.payload?.memo || '-' }}</el-descriptions-item>
        </el-descriptions>
        <div class="section-title" style="margin-top:16px">明細</div>
        <el-table :data="detailData.payload?.lines || []" border size="small">
          <el-table-column label="品目" min-width="200">
            <template #default="{ row }">{{ materialNames[row.materialCode] || row.materialCode }}</template>
          </el-table-column>
          <el-table-column prop="quantity" label="数量" width="120" />
          <el-table-column prop="uom" label="単位" width="80" />
        </el-table>
      </template>
      <template #footer>
        <el-button @click="showDetail = false">閉じる</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, onMounted } from 'vue'
import { Plus, Search, Refresh, Upload, Delete, Document } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '../api'

const loading = ref(false)
const list = ref<any[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = 50
const searchQuery = ref('')
const dateRange = ref<string[] | null>(null)

// material name cache
const materialNames = ref<Record<string, string>>({})

// create dialog
const showCreate = ref(false)
const creating = ref(false)
const createErr = ref('')
const recognizing = ref(false)

const createForm = reactive({
  movementDate: new Date().toISOString().slice(0, 10),
  toWarehouse: '',
  toBin: '',
  poNo: '',
  memo: '',
  lines: [] as { materialCode: string; quantity: number; uom: string }[]
})

// warehouses / bins
const warehouses = ref<any[]>([])
const bins = ref<any[]>([])
const binsLoading = ref(false)

// PO options
const poOptions = ref<any[]>([])

// material search
const materialOptions = ref<{ code: string; label: string }[]>([])
const materialLoading = ref(false)

// detail dialog
const showDetail = ref(false)
const detailData = ref<any>(null)

async function loadData() {
  loading.value = true
  try {
    const where: any[] = [{ field: 'movement_type', op: 'eq', value: 'IN' }]
    if (searchQuery.value) {
      where.push({ field: 'reference_no', op: 'contains', value: searchQuery.value })
    }
    if (dateRange.value && dateRange.value[0] && dateRange.value[1]) {
      where.push({ field: 'movement_date', op: 'gte', value: dateRange.value[0] })
      where.push({ field: 'movement_date', op: 'lte', value: dateRange.value[1] })
    }

    const resp = await api.post('/objects/inventory_movement/search', {
      where,
      orderBy: [{ field: 'movement_date', direction: 'desc' }],
      limit: pageSize,
      offset: (page.value - 1) * pageSize
    })
    list.value = resp.data?.data || []
    total.value = resp.data?.total || list.value.length

    // fetch material names
    const codes = new Set<string>()
    list.value.forEach(row => {
      (row.payload?.lines || []).forEach((l: any) => {
        if (l.materialCode && !materialNames.value[l.materialCode]) codes.add(l.materialCode)
      })
    })
    if (codes.size > 0) await fetchMaterialNames([...codes])
  } catch (e: any) {
    console.error('Failed to load goods receipts', e)
  } finally {
    loading.value = false
  }
}

async function fetchMaterialNames(codes: string[]) {
  try {
    const resp = await api.post('/objects/material/search', {
      where: [{ field: 'material_code', op: 'in', value: codes }],
      limit: codes.length
    })
    for (const m of resp.data?.data || []) {
      materialNames.value[m.material_code] = (m.payload?.name || m.payload?.materialName || m.material_code)
    }
  } catch { /* ignore */ }
}

async function loadWarehouses() {
  try {
    const resp = await api.post('/objects/warehouse/search', { limit: 200 })
    warehouses.value = resp.data?.data || []
  } catch { /* ignore */ }
}

async function loadBins(warehouseCode: string) {
  binsLoading.value = true
  try {
    const resp = await api.post('/objects/bin/search', {
      where: [{ field: 'warehouse_code', op: 'eq', value: warehouseCode }],
      limit: 200
    })
    bins.value = resp.data?.data || []
    if (bins.value.length > 0) createForm.toBin = bins.value[0].bin_code
  } catch { /* ignore */ }
  finally { binsLoading.value = false }
}

async function loadPOs() {
  try {
    const resp = await api.post('/objects/purchase_order/search', {
      where: [{ field: 'status', op: 'in', value: ['new', 'partial_received'] }],
      orderBy: [{ field: 'order_date', direction: 'desc' }],
      limit: 200
    })
    poOptions.value = resp.data?.data || []
  } catch { /* ignore */ }
}

function onWarehouseChange(val: string) {
  createForm.toBin = ''
  bins.value = []
  if (val) loadBins(val)
}

function onPoSelected(poNo: string) {
  if (!poNo) {
    createForm.lines = [{ materialCode: '', quantity: 0, uom: '' }]
    return
  }
  const po = poOptions.value.find(p => p.po_no === poNo)
  if (!po?.payload?.lines) return

  createForm.lines = po.payload.lines
    .filter((l: any) => {
      const remaining = (l.quantity || 0) - (l.receivedQuantity || 0)
      return remaining > 0 && l.status !== 'cancelled'
    })
    .map((l: any) => ({
      materialCode: l.materialCode || '',
      quantity: (l.quantity || 0) - (l.receivedQuantity || 0),
      uom: l.uom || ''
    }))

  // fetch names for these materials
  const codes = createForm.lines.map(l => l.materialCode).filter(c => c && !materialNames.value[c])
  if (codes.length > 0) fetchMaterialNames(codes)

  // also populate material names from PO lines
  po.payload.lines.forEach((l: any) => {
    if (l.materialCode && l.materialName) {
      materialNames.value[l.materialCode] = l.materialName
    }
  })
}

async function searchMaterials(query: string, _index: number) {
  if (!query || query.length < 1) return
  materialLoading.value = true
  try {
    const resp = await api.post('/objects/material/search', {
      where: [{ field: 'material_code', op: 'contains', value: query }],
      limit: 20
    })
    materialOptions.value = (resp.data?.data || []).map((m: any) => ({
      code: m.material_code,
      label: `${m.material_code} - ${m.payload?.name || m.payload?.materialName || ''}`
    }))
    // cache names
    for (const m of resp.data?.data || []) {
      materialNames.value[m.material_code] = m.payload?.name || m.payload?.materialName || m.material_code
    }
  } catch { /* ignore */ }
  finally { materialLoading.value = false }
}

function addLine() {
  createForm.lines.push({ materialCode: '', quantity: 0, uom: '' })
}

function openCreateDialog() {
  createErr.value = ''
  createForm.movementDate = new Date().toISOString().slice(0, 10)
  createForm.toWarehouse = ''
  createForm.toBin = ''
  createForm.poNo = ''
  createForm.memo = ''
  createForm.lines = [{ materialCode: '', quantity: 0, uom: '' }]
  showCreate.value = true
  loadPOs()
}

async function doCreate() {
  if (!createForm.movementDate) { createErr.value = '入庫日を選択してください'; return }
  if (!createForm.toWarehouse) { createErr.value = '倉庫を選択してください'; return }

  const validLines = createForm.lines.filter(l => l.materialCode && l.quantity > 0)
  if (validLines.length === 0) { createErr.value = '品目と数量を入力してください'; return }

  creating.value = true
  createErr.value = ''
  try {
    const selectedPo = createForm.poNo ? poOptions.value.find(p => p.po_no === createForm.poNo) : null

    const movementPayload: any = {
      movementType: 'IN',
      movementDate: createForm.movementDate,
      toWarehouse: createForm.toWarehouse,
      toBin: createForm.toBin,
      referenceNo: createForm.poNo || undefined,
      referenceType: createForm.poNo ? 'purchase_order' : 'manual',
      referenceId: selectedPo?.id || undefined,
      memo: createForm.memo || undefined,
      lines: validLines.map((l, idx) => ({
        lineNo: idx + 1,
        materialCode: l.materialCode,
        quantity: l.quantity,
        uom: l.uom
      }))
    }
    await api.post('/inventory/movements', movementPayload)

    // If linked to PO, update PO receivedQuantity
    if (selectedPo) {
      const updatedLines = selectedPo.payload.lines.map((poLine: any) => {
        const received = validLines.find(vl => vl.materialCode === poLine.materialCode)
        if (received) {
          const newReceivedQty = (poLine.receivedQuantity || 0) + received.quantity
          const newStatus = newReceivedQty >= poLine.quantity ? 'closed' : 'partial'
          return { ...poLine, receivedQuantity: newReceivedQty, status: newStatus }
        }
        return poLine
      })
      const allClosed = updatedLines.every((l: any) => l.status === 'closed' || l.status === 'cancelled')
      await api.put(`/objects/purchase_order/${selectedPo.id}`, {
        payload: { ...selectedPo.payload, lines: updatedLines, status: allClosed ? 'fully_received' : 'partial_received' }
      })
    }

    ElMessage.success('入庫登録が完了しました')
    showCreate.value = false
    loadData()
  } catch (e: any) {
    createErr.value = e?.response?.data?.error || '入庫登録に失敗しました'
  } finally {
    creating.value = false
  }
}

async function onFileSelected(file: any) {
  if (!file?.raw) return
  recognizing.value = true
  try {
    const fd = new FormData()
    fd.append('file', file.raw)
    const resp = await api.post('/vendor-invoice/recognize', fd)
    if (resp.data?.recognized && resp.data?.data) {
      applyRecognizedData(resp.data.data)
    } else {
      ElMessage.warning('認識結果が空です')
    }
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'AI認識に失敗しました')
  } finally {
    recognizing.value = false
  }
}

function applyRecognizedData(data: any) {
  // Open create dialog with recognized data
  openCreateDialog()

  if (data.items?.length > 0) {
    createForm.lines = data.items.map((item: any) => ({
      materialCode: '',
      quantity: item.quantity || 0,
      uom: ''
    }))
  }

  ElMessage.success('AI認識データを適用しました。品目コードを手動で選択してください。')
}

function onRowClick(row: any) {
  detailData.value = row
  showDetail.value = true
}

async function deleteReceipt(row: any) {
  try {
    await ElMessageBox.confirm('この入庫記録を削除しますか？在庫残高には反映されません。', '確認', { type: 'warning' })
    await api.delete(`/objects/inventory_movement/${row.id}`)
    ElMessage.success('削除しました')
    loadData()
  } catch { /* cancelled */ }
}

onMounted(() => {
  loadData()
  loadWarehouses()
})
</script>

<style scoped>
.page { padding: 0; }
.page-wide { max-width: 1400px; margin: 0 auto; }
.page-header { display: flex; align-items: center; justify-content: space-between; }
.page-header-title { font-size: 18px; font-weight: 600; }
.page-actions { display: flex; gap: 8px; }
.search-bar { display: flex; gap: 8px; margin-bottom: 16px; align-items: center; flex-wrap: wrap; }
.pagination-bar { display: flex; justify-content: flex-end; margin-top: 16px; }
.section-title { font-size: 14px; font-weight: 600; margin-bottom: 8px; color: #303133; }
.line-item { font-size: 13px; line-height: 1.6; }
</style>
