<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ labels.title }}</div>
          <div class="page-actions">
            <el-button type="primary" @click="openCreateDialog">
              <el-icon><Plus /></el-icon> {{ labels.create }}
            </el-button>
          </div>
        </div>
      </template>

      <!-- 筛选条件 -->
      <div class="filter-row">
        <el-select v-model="filterStatus" :placeholder="labels.status" clearable style="width: 150px" @change="load">
          <el-option :label="labels.statusDraft" value="draft" />
          <el-option :label="labels.statusInProgress" value="in_progress" />
          <el-option :label="labels.statusCompleted" value="completed" />
          <el-option :label="labels.statusPosted" value="posted" />
          <el-option :label="labels.statusCancelled" value="cancelled" />
        </el-select>
        <el-select v-model="filterWarehouse" :placeholder="labels.warehouse" clearable style="width: 200px" @change="load">
          <el-option v-for="w in warehouseOptions" :key="w.warehouse_code" :label="`${w.name} (${w.warehouse_code})`" :value="w.warehouse_code" />
        </el-select>
        <el-button @click="load">{{ labels.refresh }}</el-button>
      </div>

      <!-- 列表 -->
      <el-table :data="list" v-loading="loading" stripe style="width: 100%">
        <el-table-column prop="count_no" :label="labels.countNo" width="160" />
        <el-table-column prop="count_date" :label="labels.countDate" width="120" />
        <el-table-column :label="labels.warehouse" min-width="180">
          <template #default="{ row }">
            {{ row.warehouse_name || row.warehouse_code }}
          </template>
        </el-table-column>
        <el-table-column :label="labels.progress" width="120">
          <template #default="{ row }">
            {{ row.counted_count || 0 }} / {{ row.line_count || 0 }}
          </template>
        </el-table-column>
        <el-table-column :label="labels.varianceCount" width="100" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.variance_count > 0" type="warning" size="small">{{ row.variance_count }}</el-tag>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column :label="labels.status" width="100" align="center">
          <template #default="{ row }">
            <el-tag :type="statusType(row.status)" size="small">{{ statusLabel(row.status) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="labels.actions" width="280" align="center">
          <template #default="{ row }">
            <el-button size="small" @click="viewDetail(row.id)">{{ labels.view }}</el-button>
            <el-button v-if="row.status === 'draft' || row.status === 'in_progress'" size="small" type="primary" @click="editCount(row.id)">{{ labels.count }}</el-button>
            <el-button v-if="row.status === 'completed'" size="small" type="success" @click="postCount(row.id)">{{ labels.post }}</el-button>
            <el-button v-if="row.status === 'draft' || row.status === 'in_progress'" size="small" type="danger" @click="deleteCount(row.id)">{{ labels.delete }}</el-button>
          </template>
        </el-table-column>
      </el-table>

      <!-- 分页 -->
      <div class="pagination-row" v-if="total > pageSize">
        <el-pagination
          v-model:current-page="page"
          :page-size="pageSize"
          :total="total"
          layout="total, prev, pager, next"
          @current-change="load"
        />
      </div>
    </el-card>

    <!-- 创建盘点单对话框 -->
    <el-dialog v-model="createDialog.visible" :title="labels.createTitle" width="500px">
      <el-form :model="createDialog.form" label-width="100px">
        <el-form-item :label="labels.warehouse" required>
          <el-select v-model="createDialog.form.warehouseCode" :placeholder="labels.selectWarehouse" style="width: 100%">
            <el-option v-for="w in warehouseOptions" :key="w.warehouse_code" :label="`${w.name} (${w.warehouse_code})`" :value="w.warehouse_code" />
          </el-select>
        </el-form-item>
        <el-form-item :label="labels.countDate">
          <el-date-picker v-model="createDialog.form.countDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="labels.bin">
          <el-select v-model="createDialog.form.binCode" :placeholder="labels.allBins" clearable style="width: 100%">
            <el-option v-for="b in filteredBinOptions" :key="b.bin_code" :label="`${b.name} (${b.bin_code})`" :value="b.bin_code" />
          </el-select>
        </el-form-item>
        <el-form-item :label="labels.description">
          <el-input v-model="createDialog.form.description" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createDialog.visible = false">{{ labels.cancel }}</el-button>
        <el-button type="primary" :loading="createDialog.loading" @click="createCount">{{ labels.create }}</el-button>
      </template>
    </el-dialog>

    <!-- 盘点详情/录入对话框 -->
    <el-dialog v-model="detailDialog.visible" :title="detailDialog.title" width="90%" top="5vh" :close-on-click-modal="false">
      <div v-if="detailDialog.data" class="count-detail">
        <div class="count-header">
          <el-descriptions :column="4" border size="small">
            <el-descriptions-item :label="labels.countNo">{{ detailDialog.data.count_no }}</el-descriptions-item>
            <el-descriptions-item :label="labels.countDate">{{ detailDialog.data.count_date }}</el-descriptions-item>
            <el-descriptions-item :label="labels.warehouse">{{ detailDialog.data.warehouse_name || detailDialog.data.warehouse_code }}</el-descriptions-item>
            <el-descriptions-item :label="labels.status">
              <el-tag :type="statusType(detailDialog.data.status)" size="small">{{ statusLabel(detailDialog.data.status) }}</el-tag>
            </el-descriptions-item>
          </el-descriptions>
        </div>

        <div class="count-actions" v-if="detailDialog.mode === 'edit' && (detailDialog.data.status === 'draft' || detailDialog.data.status === 'in_progress')">
          <el-button type="primary" :loading="detailDialog.saving" @click="saveLines">{{ labels.save }}</el-button>
          <el-button type="warning" @click="startCount" v-if="detailDialog.data.status === 'draft'">{{ labels.startCount }}</el-button>
          <el-button type="success" @click="completeCount" v-if="detailDialog.data.status === 'in_progress'">{{ labels.complete }}</el-button>
        </div>

        <el-table :data="detailDialog.lines" v-loading="detailDialog.linesLoading" stripe style="width: 100%; margin-top: 16px" max-height="500">
          <el-table-column prop="line_no" :label="labels.lineNo" width="60" align="center" />
          <el-table-column :label="labels.material" min-width="200">
            <template #default="{ row }">
              <div>{{ row.material_name || row.material_name_lookup || row.material_code }}</div>
              <div class="text-muted">{{ row.material_code }}</div>
            </template>
          </el-table-column>
          <el-table-column prop="bin_code" :label="labels.bin" width="100" />
          <el-table-column prop="batch_no" :label="labels.batch" width="100" />
          <el-table-column prop="uom" :label="labels.uom" width="80" align="center" />
          <el-table-column prop="system_qty" :label="labels.systemQty" width="100" align="right">
            <template #default="{ row }">
              {{ formatNumber(row.system_qty) }}
            </template>
          </el-table-column>
          <el-table-column :label="labels.actualQty" width="140" align="center">
            <template #default="{ row }">
              <el-input-number
                v-if="detailDialog.mode === 'edit' && (detailDialog.data.status === 'draft' || detailDialog.data.status === 'in_progress')"
                v-model="row.actual_qty"
                :min="0"
                :precision="2"
                size="small"
                style="width: 120px"
              />
              <span v-else>{{ row.actual_qty != null ? formatNumber(row.actual_qty) : '-' }}</span>
            </template>
          </el-table-column>
          <el-table-column :label="labels.varianceQty" width="100" align="right">
            <template #default="{ row }">
              <span v-if="row.actual_qty != null" :class="varianceClass(row)">
                {{ formatVariance(row) }}
              </span>
              <span v-else>-</span>
            </template>
          </el-table-column>
          <el-table-column :label="labels.varianceReason" min-width="150">
            <template #default="{ row }">
              <el-input
                v-if="detailDialog.mode === 'edit' && (detailDialog.data.status === 'draft' || detailDialog.data.status === 'in_progress') && row.actual_qty != null && row.actual_qty !== row.system_qty"
                v-model="row.variance_reason"
                size="small"
                :placeholder="labels.enterReason"
              />
              <span v-else>{{ row.variance_reason || '-' }}</span>
            </template>
          </el-table-column>
        </el-table>

        <div class="count-summary" v-if="detailDialog.lines.length > 0">
          <el-alert type="info" :closable="false">
            {{ labels.summaryText.replace('{total}', detailDialog.lines.length.toString())
              .replace('{counted}', detailDialog.lines.filter(l => l.actual_qty != null).length.toString())
              .replace('{variance}', detailDialog.lines.filter(l => l.actual_qty != null && l.actual_qty !== l.system_qty).length.toString()) }}
          </el-alert>
        </div>
      </div>
      <template #footer>
        <el-button @click="detailDialog.visible = false">{{ labels.close }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import { Plus } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '../api'
import { useI18n } from '../i18n'

const { lang } = useI18n()

// 多语言标签
const labels = computed(() => {
  const l = lang.value
  return {
    title: l === 'ja' ? '棚卸一覧' : l === 'en' ? 'Stock Count List' : '盘点单列表',
    create: l === 'ja' ? '新規作成' : l === 'en' ? 'Create' : '新建',
    createTitle: l === 'ja' ? '棚卸作成' : l === 'en' ? 'Create Stock Count' : '创建盘点单',
    refresh: l === 'ja' ? '更新' : l === 'en' ? 'Refresh' : '刷新',
    countNo: l === 'ja' ? '棚卸番号' : l === 'en' ? 'Count No.' : '盘点单号',
    countDate: l === 'ja' ? '棚卸日' : l === 'en' ? 'Count Date' : '盘点日期',
    warehouse: l === 'ja' ? '倉庫' : l === 'en' ? 'Warehouse' : '仓库',
    bin: l === 'ja' ? '棚番' : l === 'en' ? 'Bin' : '棚番',
    allBins: l === 'ja' ? 'すべての棚番' : l === 'en' ? 'All Bins' : '所有棚番',
    selectWarehouse: l === 'ja' ? '倉庫を選択' : l === 'en' ? 'Select Warehouse' : '选择仓库',
    description: l === 'ja' ? '説明' : l === 'en' ? 'Description' : '说明',
    progress: l === 'ja' ? '進捗' : l === 'en' ? 'Progress' : '进度',
    varianceCount: l === 'ja' ? '差異数' : l === 'en' ? 'Variances' : '差异数',
    status: l === 'ja' ? 'ステータス' : l === 'en' ? 'Status' : '状态',
    statusDraft: l === 'ja' ? '下書き' : l === 'en' ? 'Draft' : '草稿',
    statusInProgress: l === 'ja' ? '進行中' : l === 'en' ? 'In Progress' : '进行中',
    statusCompleted: l === 'ja' ? '完了' : l === 'en' ? 'Completed' : '已完成',
    statusPosted: l === 'ja' ? '転記済み' : l === 'en' ? 'Posted' : '已过账',
    statusCancelled: l === 'ja' ? 'キャンセル' : l === 'en' ? 'Cancelled' : '已取消',
    actions: l === 'ja' ? '操作' : l === 'en' ? 'Actions' : '操作',
    view: l === 'ja' ? '詳細' : l === 'en' ? 'View' : '查看',
    count: l === 'ja' ? '棚卸入力' : l === 'en' ? 'Count' : '盘点录入',
    post: l === 'ja' ? '転記' : l === 'en' ? 'Post' : '过账',
    delete: l === 'ja' ? '削除' : l === 'en' ? 'Delete' : '删除',
    cancel: l === 'ja' ? 'キャンセル' : l === 'en' ? 'Cancel' : '取消',
    save: l === 'ja' ? '保存' : l === 'en' ? 'Save' : '保存',
    close: l === 'ja' ? '閉じる' : l === 'en' ? 'Close' : '关闭',
    lineNo: l === 'ja' ? '行' : l === 'en' ? 'Line' : '行号',
    material: l === 'ja' ? '品目' : l === 'en' ? 'Material' : '品目',
    batch: l === 'ja' ? 'ロット' : l === 'en' ? 'Batch' : '批次',
    uom: l === 'ja' ? '単位' : l === 'en' ? 'UoM' : '单位',
    systemQty: l === 'ja' ? 'システム数量' : l === 'en' ? 'System Qty' : '系统数量',
    actualQty: l === 'ja' ? '実数量' : l === 'en' ? 'Actual Qty' : '实际数量',
    varianceQty: l === 'ja' ? '差異' : l === 'en' ? 'Variance' : '差异',
    varianceReason: l === 'ja' ? '差異理由' : l === 'en' ? 'Reason' : '差异原因',
    enterReason: l === 'ja' ? '理由を入力' : l === 'en' ? 'Enter reason' : '输入原因',
    startCount: l === 'ja' ? '棚卸開始' : l === 'en' ? 'Start Count' : '开始盘点',
    complete: l === 'ja' ? '完了' : l === 'en' ? 'Complete' : '完成',
    summaryText: l === 'ja' ? '合計 {total} 件、盤点済み {counted} 件、差異 {variance} 件' 
      : l === 'en' ? 'Total {total} items, Counted {counted}, Variances {variance}'
      : '共 {total} 项，已盘点 {counted} 项，有差异 {variance} 项',
    confirmDelete: l === 'ja' ? 'この棚卸を削除しますか？' : l === 'en' ? 'Delete this count?' : '确定删除这个盘点单吗？',
    confirmPost: l === 'ja' ? '棚卸差異を転記しますか？在庫が調整されます。' : l === 'en' ? 'Post variances? Inventory will be adjusted.' : '确定过账盘点差异吗？库存将被调整。',
    confirmComplete: l === 'ja' ? '棚卸を完了しますか？' : l === 'en' ? 'Complete this count?' : '确定完成盘点吗？',
    createSuccess: l === 'ja' ? '棚卸が作成されました' : l === 'en' ? 'Count created' : '盘点单创建成功',
    saveSuccess: l === 'ja' ? '保存しました' : l === 'en' ? 'Saved' : '保存成功',
    postSuccess: l === 'ja' ? '転記しました' : l === 'en' ? 'Posted' : '过账成功',
    deleteSuccess: l === 'ja' ? '削除しました' : l === 'en' ? 'Deleted' : '删除成功',
  }
})

const loading = ref(false)
const list = ref<any[]>([])
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const filterStatus = ref('')
const filterWarehouse = ref('')
const warehouseOptions = ref<any[]>([])
const binOptions = ref<any[]>([])

// 创建对话框
const createDialog = reactive({
  visible: false,
  loading: false,
  form: {
    warehouseCode: '',
    countDate: new Date().toISOString().slice(0, 10),
    binCode: '',
    description: ''
  }
})

// 详情/编辑对话框
const detailDialog = reactive({
  visible: false,
  mode: 'view' as 'view' | 'edit',
  title: '',
  data: null as any,
  lines: [] as any[],
  linesLoading: false,
  saving: false
})

// 根据选择的仓库筛选棚番
const filteredBinOptions = computed(() => {
  if (!createDialog.form.warehouseCode) return []
  return binOptions.value.filter(b => b.warehouse_code === createDialog.form.warehouseCode)
})

onMounted(() => {
  load()
  loadWarehouses()
  loadBins()
})

async function load() {
  loading.value = true
  try {
    const params = new URLSearchParams()
    if (filterStatus.value) params.append('status', filterStatus.value)
    if (filterWarehouse.value) params.append('warehouseCode', filterWarehouse.value)
    const r = await api.get(`/inventory/counts?${params}`)
    list.value = r.data || []
    total.value = list.value.length
  } catch (e: any) {
    console.error('Failed to load counts:', e)
  } finally {
    loading.value = false
  }
}

async function loadWarehouses() {
  try {
    const r = await api.get('/inventory/warehouses')
    warehouseOptions.value = r.data || []
  } catch (e) {
    console.error('Failed to load warehouses:', e)
  }
}

async function loadBins() {
  try {
    const r = await api.get('/inventory/bins')
    binOptions.value = r.data || []
  } catch (e) {
    console.error('Failed to load bins:', e)
  }
}

function statusType(status: string) {
  switch (status) {
    case 'draft': return 'info'
    case 'in_progress': return 'warning'
    case 'completed': return 'success'
    case 'posted': return ''
    case 'cancelled': return 'danger'
    default: return 'info'
  }
}

function statusLabel(status: string) {
  switch (status) {
    case 'draft': return labels.value.statusDraft
    case 'in_progress': return labels.value.statusInProgress
    case 'completed': return labels.value.statusCompleted
    case 'posted': return labels.value.statusPosted
    case 'cancelled': return labels.value.statusCancelled
    default: return status
  }
}

function formatNumber(num: number | null | undefined) {
  if (num == null) return '-'
  return num.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 2 })
}

function formatVariance(row: any) {
  if (row.actual_qty == null) return '-'
  const variance = row.actual_qty - row.system_qty
  const prefix = variance > 0 ? '+' : ''
  return prefix + formatNumber(variance)
}

function varianceClass(row: any) {
  if (row.actual_qty == null) return ''
  const variance = row.actual_qty - row.system_qty
  if (variance > 0) return 'text-success'
  if (variance < 0) return 'text-danger'
  return ''
}

function openCreateDialog() {
  createDialog.form = {
    warehouseCode: '',
    countDate: new Date().toISOString().slice(0, 10),
    binCode: '',
    description: ''
  }
  createDialog.visible = true
}

async function createCount() {
  if (!createDialog.form.warehouseCode) {
    ElMessage.warning(labels.value.selectWarehouse)
    return
  }
  createDialog.loading = true
  try {
    await api.post('/inventory/counts', createDialog.form)
    ElMessage.success(labels.value.createSuccess)
    createDialog.visible = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'Failed')
  } finally {
    createDialog.loading = false
  }
}

async function viewDetail(id: string) {
  detailDialog.mode = 'view'
  await loadCountDetail(id)
}

async function editCount(id: string) {
  detailDialog.mode = 'edit'
  await loadCountDetail(id)
}

async function loadCountDetail(id: string) {
  detailDialog.visible = true
  detailDialog.linesLoading = true
  try {
    const [countRes, linesRes] = await Promise.all([
      api.get(`/inventory/counts/${id}`),
      api.get(`/inventory/counts/${id}/lines`)
    ])
    detailDialog.data = countRes.data
    detailDialog.lines = linesRes.data || []
    detailDialog.title = `${labels.value.countNo}: ${detailDialog.data.count_no}`
  } catch (e: any) {
    ElMessage.error('Failed to load detail')
    detailDialog.visible = false
  } finally {
    detailDialog.linesLoading = false
  }
}

async function saveLines() {
  detailDialog.saving = true
  try {
    const linesToSave = detailDialog.lines.map(l => ({
      id: l.id,
      actualQty: l.actual_qty,
      varianceReason: l.variance_reason
    }))
    await api.put(`/inventory/counts/${detailDialog.data.id}/lines`, { lines: linesToSave })
    ElMessage.success(labels.value.saveSuccess)
    // 重新加载
    await loadCountDetail(detailDialog.data.id)
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'Failed')
  } finally {
    detailDialog.saving = false
  }
}

async function startCount() {
  try {
    await api.put(`/inventory/counts/${detailDialog.data.id}/status`, { status: 'in_progress' })
    await loadCountDetail(detailDialog.data.id)
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'Failed')
  }
}

async function completeCount() {
  try {
    await ElMessageBox.confirm(labels.value.confirmComplete, { type: 'warning' })
    await api.put(`/inventory/counts/${detailDialog.data.id}/status`, { status: 'completed' })
    await loadCountDetail(detailDialog.data.id)
    load()
  } catch (e: any) {
    if (e !== 'cancel') ElMessage.error(e?.response?.data?.error || 'Failed')
  }
}

async function postCount(id: string) {
  try {
    await ElMessageBox.confirm(labels.value.confirmPost, { type: 'warning' })
    await api.post(`/inventory/counts/${id}/post`)
    ElMessage.success(labels.value.postSuccess)
    load()
  } catch (e: any) {
    if (e !== 'cancel') ElMessage.error(e?.response?.data?.error || 'Failed')
  }
}

async function deleteCount(id: string) {
  try {
    await ElMessageBox.confirm(labels.value.confirmDelete, { type: 'warning' })
    await api.delete(`/inventory/counts/${id}`)
    ElMessage.success(labels.value.deleteSuccess)
    load()
  } catch (e: any) {
    if (e !== 'cancel') ElMessage.error(e?.response?.data?.error || 'Failed')
  }
}
</script>

<style scoped>
.filter-row {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}
.pagination-row {
  display: flex;
  justify-content: flex-end;
  margin-top: 16px;
}
.count-header {
  margin-bottom: 16px;
}
.count-actions {
  display: flex;
  gap: 8px;
}
.count-summary {
  margin-top: 16px;
}
.text-muted {
  color: #909399;
  font-size: 12px;
}
.text-success {
  color: #67c23a;
  font-weight: 600;
}
.text-danger {
  color: #f56c6c;
  font-weight: 600;
}
</style>

