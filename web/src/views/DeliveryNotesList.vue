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
        <el-select v-model="filterStatus" :placeholder="labels.status" clearable style="width: 140px" @change="load">
          <el-option :label="labels.statusConfirmed" value="confirmed" />
          <el-option :label="labels.statusShipped" value="shipped" />
          <el-option :label="labels.statusDelivered" value="delivered" />
          <el-option :label="labels.statusCancelled" value="cancelled" />
        </el-select>
        <el-date-picker
          v-model="filterDateRange"
          type="daterange"
          :start-placeholder="labels.startDate"
          :end-placeholder="labels.endDate"
          value-format="YYYY-MM-DD"
          style="width: 260px"
          @change="load"
        />
        <el-button @click="load">{{ labels.refresh }}</el-button>
      </div>

      <!-- 列表 -->
      <el-table :data="list" v-loading="loading" stripe style="width: 100%">
        <el-table-column prop="delivery_no" :label="labels.deliveryNo" width="150">
          <template #default="{ row }">
            <el-link type="primary" @click="viewDetail(row.id)">{{ row.delivery_no }}</el-link>
          </template>
        </el-table-column>
        <el-table-column prop="delivery_date" :label="labels.deliveryDate" width="110" />
        <el-table-column :label="labels.customer" min-width="180">
          <template #default="{ row }">
            {{ row.customer_name || row.customer_code || '-' }}
          </template>
        </el-table-column>
        <el-table-column prop="sales_order_no" :label="labels.salesOrder" width="140">
          <template #default="{ row }">
            {{ row.sales_order_no || '-' }}
          </template>
        </el-table-column>
        <el-table-column :label="labels.warehouse" width="140">
          <template #default="{ row }">
            {{ row.warehouse_name || row.warehouse_code }}
          </template>
        </el-table-column>
        <el-table-column :label="labels.lineCount" width="80" align="center">
          <template #default="{ row }">
            {{ row.line_count || 0 }}
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
            <el-button v-if="row.status === 'confirmed'" size="small" type="success" @click="shipNote(row.id)">{{ labels.ship }}</el-button>
            <el-button v-if="row.status === 'shipped'" size="small" type="warning" @click="deliverNote(row.id)">{{ labels.deliver }}</el-button>
            <el-button v-if="row.status === 'confirmed'" size="small" type="danger" @click="cancelNote(row.id)">{{ labels.cancel }}</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 创建纳品书对话框 -->
    <el-dialog v-model="createDialog.visible" :title="labels.createTitle" width="500px">
      <el-form :model="createDialog.form" label-width="100px">
        <el-form-item :label="labels.warehouse" required>
          <el-select v-model="createDialog.form.warehouseCode" :placeholder="labels.selectWarehouse" style="width: 100%">
            <el-option v-for="w in warehouseOptions" :key="w.warehouse_code" :label="`${w.name} (${w.warehouse_code})`" :value="w.warehouse_code" />
          </el-select>
        </el-form-item>
        <el-form-item :label="labels.deliveryDate">
          <el-date-picker v-model="createDialog.form.deliveryDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="labels.customer">
          <el-select 
            v-model="createDialog.form.customerId" 
            :placeholder="labels.selectCustomer" 
            filterable 
            remote 
            :remote-method="searchCustomers"
            clearable 
            style="width: 100%"
            @change="onCustomerChange"
          >
            <el-option v-for="c in customerOptions" :key="c.id" :label="`${c.name} (${c.code})`" :value="c.id" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createDialog.visible = false">{{ labels.cancelBtn }}</el-button>
        <el-button type="primary" :loading="createDialog.loading" @click="createNote">{{ labels.create }}</el-button>
      </template>
    </el-dialog>

    <!-- 详情对话框 -->
    <el-dialog v-model="detailDialog.visible" :title="detailDialog.title" width="fit-content" :style="{ minWidth: '800px', maxWidth: '95vw' }" top="5vh">
      <div v-if="detailDialog.data" class="delivery-detail">
        <div class="detail-header">
          <el-descriptions :column="4" border size="small">
            <el-descriptions-item :label="labels.deliveryNo">{{ detailDialog.data.delivery_no }}</el-descriptions-item>
            <el-descriptions-item :label="labels.deliveryDate">{{ detailDialog.data.delivery_date }}</el-descriptions-item>
            <el-descriptions-item :label="labels.customer">{{ detailDialog.data.customer_name || detailDialog.data.customer_code || '-' }}</el-descriptions-item>
            <el-descriptions-item :label="labels.status">
              <el-tag :type="statusType(detailDialog.data.status)" size="small">{{ statusLabel(detailDialog.data.status) }}</el-tag>
            </el-descriptions-item>
            <el-descriptions-item :label="labels.salesOrder">{{ detailDialog.data.sales_order_no || '-' }}</el-descriptions-item>
            <el-descriptions-item :label="labels.warehouse">{{ detailDialog.data.warehouse_name || detailDialog.data.warehouse_code }}</el-descriptions-item>
            <el-descriptions-item :label="labels.shippingAddress" :span="2">{{ detailDialog.data.shipping_address || '-' }}</el-descriptions-item>
          </el-descriptions>
        </div>

        <div class="detail-actions" v-if="detailDialog.data.status === 'draft'">
          <el-button type="primary" :loading="detailDialog.saving" @click="saveLines">{{ labels.save }}</el-button>
        </div>

        <el-table :data="detailDialog.lines" v-loading="detailDialog.linesLoading" stripe style="width: 100%; margin-top: 16px" max-height="400">
          <el-table-column prop="lineNo" :label="labels.lineNo" width="60" align="center" />
          <el-table-column :label="labels.material" min-width="200">
            <template #default="{ row }">
              <div>{{ row.materialName || row.materialCode }}</div>
              <div class="text-muted">{{ row.materialCode }}</div>
            </template>
          </el-table-column>
          <el-table-column prop="uom" :label="labels.uom" width="80" align="center" />
          <el-table-column :label="labels.orderedQty" width="100" align="right">
            <template #default="{ row }">{{ formatNumber(row.orderedQty) }}</template>
          </el-table-column>
          <el-table-column :label="labels.previouslyDelivered" width="100" align="right">
            <template #default="{ row }">{{ formatNumber(row.previouslyDeliveredQty) }}</template>
          </el-table-column>
          <el-table-column :label="labels.deliveryQty" width="120" align="center">
            <template #default="{ row }">
              <el-input-number
                v-if="detailDialog.data.status === 'draft'"
                v-model="row.deliveryQty"
                :min="0"
                :precision="2"
                size="small"
                style="width: 100px"
              />
              <span v-else>{{ formatNumber(row.deliveryQty) }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="binCode" :label="labels.bin" width="100">
            <template #default="{ row }">
              <el-input
                v-if="detailDialog.data.status === 'draft'"
                v-model="row.binCode"
                size="small"
              />
              <span v-else>{{ row.binCode || '-' }}</span>
            </template>
          </el-table-column>
          <el-table-column prop="batchNo" :label="labels.batch" width="100">
            <template #default="{ row }">
              <el-input
                v-if="detailDialog.data.status === 'draft'"
                v-model="row.batchNo"
                size="small"
              />
              <span v-else>{{ row.batchNo || '-' }}</span>
            </template>
          </el-table-column>
        </el-table>
      </div>
      <template #footer>
        <el-button @click="detailDialog.visible = false">{{ labels.close }}</el-button>
        <el-button v-if="detailDialog.data?.status === 'confirmed'" type="success" @click="shipNote(detailDialog.data.id)">{{ labels.ship }}</el-button>
        <el-button v-if="detailDialog.data?.status === 'shipped'" type="warning" @click="deliverNote(detailDialog.data.id)">{{ labels.deliver }}</el-button>
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

const labels = computed(() => {
  const l = lang.value
  return {
    title: l === 'ja' ? '納品書一覧' : l === 'en' ? 'Delivery Notes' : '纳品书列表',
    create: l === 'ja' ? '新規作成' : l === 'en' ? 'Create' : '新建',
    createTitle: l === 'ja' ? '納品書作成' : l === 'en' ? 'Create Delivery Note' : '创建纳品书',
    refresh: l === 'ja' ? '更新' : l === 'en' ? 'Refresh' : '刷新',
    deliveryNo: l === 'ja' ? '納品書番号' : l === 'en' ? 'Delivery No.' : '纳品单号',
    deliveryDate: l === 'ja' ? '納品日' : l === 'en' ? 'Delivery Date' : '纳品日期',
    customer: l === 'ja' ? '顧客' : l === 'en' ? 'Customer' : '客户',
    selectCustomer: l === 'ja' ? '顧客を選択' : l === 'en' ? 'Select Customer' : '选择客户',
    salesOrder: l === 'ja' ? '受注番号' : l === 'en' ? 'Sales Order' : '销售订单',
    warehouse: l === 'ja' ? '倉庫' : l === 'en' ? 'Warehouse' : '仓库',
    selectWarehouse: l === 'ja' ? '倉庫を選択' : l === 'en' ? 'Select Warehouse' : '选择仓库',
    lineCount: l === 'ja' ? '明細数' : l === 'en' ? 'Lines' : '明细数',
    status: l === 'ja' ? 'ステータス' : l === 'en' ? 'Status' : '状态',
    statusDraft: l === 'ja' ? '下書き' : l === 'en' ? 'Draft' : '草稿',
    statusConfirmed: l === 'ja' ? '出荷待ち' : l === 'en' ? 'Pending Ship' : '待出货',
    statusShipped: l === 'ja' ? '出荷済み' : l === 'en' ? 'Shipped' : '已出货',
    statusDelivered: l === 'ja' ? '納品完了' : l === 'en' ? 'Delivered' : '已送达',
    statusCancelled: l === 'ja' ? 'キャンセル' : l === 'en' ? 'Cancelled' : '已取消',
    actions: l === 'ja' ? '操作' : l === 'en' ? 'Actions' : '操作',
    view: l === 'ja' ? '詳細' : l === 'en' ? 'View' : '查看',
    confirm: l === 'ja' ? '確認' : l === 'en' ? 'Confirm' : '确认',
    ship: l === 'ja' ? '出荷' : l === 'en' ? 'Ship' : '出货',
    deliver: l === 'ja' ? '納品完了' : l === 'en' ? 'Deliver' : '送达',
    cancel: l === 'ja' ? 'キャンセル' : l === 'en' ? 'Cancel' : '取消',
    cancelBtn: l === 'ja' ? '閉じる' : l === 'en' ? 'Close' : '关闭',
    save: l === 'ja' ? '保存' : l === 'en' ? 'Save' : '保存',
    close: l === 'ja' ? '閉じる' : l === 'en' ? 'Close' : '关闭',
    lineNo: l === 'ja' ? '行' : l === 'en' ? 'Line' : '行号',
    material: l === 'ja' ? '品目' : l === 'en' ? 'Material' : '品目',
    uom: l === 'ja' ? '単位' : l === 'en' ? 'UoM' : '单位',
    orderedQty: l === 'ja' ? '受注数' : l === 'en' ? 'Ordered' : '订单数',
    previouslyDelivered: l === 'ja' ? '既納品数' : l === 'en' ? 'Delivered' : '已纳品',
    deliveryQty: l === 'ja' ? '今回納品数' : l === 'en' ? 'This Delivery' : '本次纳品',
    bin: l === 'ja' ? '棚番' : l === 'en' ? 'Bin' : '棚番',
    batch: l === 'ja' ? 'ロット' : l === 'en' ? 'Batch' : '批次',
    shippingAddress: l === 'ja' ? '配送先' : l === 'en' ? 'Shipping Address' : '配送地址',
    startDate: l === 'ja' ? '開始日' : l === 'en' ? 'Start Date' : '开始日期',
    endDate: l === 'ja' ? '終了日' : l === 'en' ? 'End Date' : '结束日期',
    confirmShip: l === 'ja' ? '出荷しますか？在庫が減少します。' : l === 'en' ? 'Ship this delivery? Inventory will be reduced.' : '确定出货吗？库存将会减少。',
    confirmDeliver: l === 'ja' ? '納品完了しますか？' : l === 'en' ? 'Mark as delivered?' : '确定标记为已送达吗？',
    confirmCancel: l === 'ja' ? 'キャンセルしますか？' : l === 'en' ? 'Cancel this delivery note?' : '确定取消这个纳品书吗？'
  }
})

const loading = ref(false)
const list = ref<any[]>([])
const filterStatus = ref('')
const filterDateRange = ref<string[]>([])
const warehouseOptions = ref<any[]>([])
const customerOptions = ref<any[]>([])

const createDialog = reactive({
  visible: false,
  loading: false,
  form: {
    warehouseCode: '',
    deliveryDate: new Date().toISOString().slice(0, 10),
    customerId: '',
    customerCode: '',
    customerName: ''
  }
})

const detailDialog = reactive({
  visible: false,
  title: '',
  data: null as any,
  lines: [] as any[],
  linesLoading: false,
  saving: false
})

onMounted(() => {
  load()
  loadWarehouses()
})

async function load() {
  loading.value = true
  try {
    const params = new URLSearchParams()
    if (filterStatus.value) params.append('status', filterStatus.value)
    if (filterDateRange.value && filterDateRange.value.length === 2) {
      params.append('fromDate', filterDateRange.value[0])
      params.append('toDate', filterDateRange.value[1])
    }
    const r = await api.get(`/delivery-notes?${params}`)
    list.value = r.data || []
  } catch (e: any) {
    console.error('Failed to load delivery notes:', e)
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

async function searchCustomers(query: string) {
  if (!query || query.length < 1) {
    customerOptions.value = []
    return
  }
  try {
    const r = await api.post('/objects/businesspartner/search', {
      page: 1, pageSize: 20,
      where: [
        { anyOf: [
          { json: 'name', op: 'contains', value: query },
          { json: 'partnerCode', op: 'contains', value: query }
        ]}
      ],
      orderBy: []
    })
    customerOptions.value = (r.data?.data || []).map((p: any) => ({
      id: p.id,
      code: p.payload?.partnerCode || p.payload?.code,
      name: p.payload?.name || p.name
    }))
  } catch (e) {
    console.error('Failed to search customers:', e)
  }
}

function onCustomerChange(customerId: string) {
  const customer = customerOptions.value.find(c => c.id === customerId)
  if (customer) {
    createDialog.form.customerCode = customer.code
    createDialog.form.customerName = customer.name
  }
}

function statusType(status: string) {
  switch (status) {
    case 'draft': return 'info'
    case 'confirmed': return 'warning'
    case 'shipped': return ''
    case 'delivered': return 'success'
    case 'cancelled': return 'danger'
    default: return 'info'
  }
}

function statusLabel(status: string) {
  switch (status) {
    case 'draft': return labels.value.statusDraft
    case 'confirmed': return labels.value.statusConfirmed
    case 'shipped': return labels.value.statusShipped
    case 'delivered': return labels.value.statusDelivered
    case 'cancelled': return labels.value.statusCancelled
    default: return status
  }
}

function formatNumber(num: number | null | undefined) {
  if (num == null) return '-'
  return num.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 2 })
}

function openCreateDialog() {
  createDialog.form = {
    warehouseCode: '',
    deliveryDate: new Date().toISOString().slice(0, 10),
    customerId: '',
    customerCode: '',
    customerName: ''
  }
  createDialog.visible = true
}

async function createNote() {
  if (!createDialog.form.warehouseCode) {
    ElMessage.warning(labels.value.selectWarehouse)
    return
  }
  createDialog.loading = true
  try {
    await api.post('/delivery-notes', createDialog.form)
    ElMessage.success('Created')
    createDialog.visible = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'Failed')
  } finally {
    createDialog.loading = false
  }
}

async function viewDetail(id: string) {
  detailDialog.visible = true
  detailDialog.linesLoading = true
  try {
    const noteRes = await api.get(`/delivery-notes/${id}`)
    const data = noteRes.data
    // 适配 schema/payload 结构
    detailDialog.data = {
      id: data.id,
      delivery_no: data.delivery_no || data.payload?.header?.deliveryNo,
      delivery_date: data.delivery_date || data.payload?.header?.deliveryDate,
      customer_name: data.customer_name || data.payload?.header?.customerName,
      customer_code: data.customer_code || data.payload?.header?.customerCode,
      sales_order_no: data.sales_order_no || data.payload?.header?.salesOrderNo,
      warehouse_code: data.payload?.header?.warehouseCode,
      warehouse_name: data.warehouse_name,
      shipping_address: data.payload?.header?.shippingAddress,
      status: data.status || data.payload?.header?.status
    }
    // 明细从 payload.lines 获取
    detailDialog.lines = data.payload?.lines || []
    detailDialog.title = `${labels.value.deliveryNo}: ${detailDialog.data.delivery_no}`
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
    // 直接发送 lines 数组（schema 格式）
    const linesToSave = detailDialog.lines.map((l: any, idx: number) => ({
      lineNo: l.lineNo || idx + 1,
      salesOrderLineId: l.salesOrderLineId,
      materialCode: l.materialCode,
      materialName: l.materialName,
      orderedQty: l.orderedQty,
      previouslyDeliveredQty: l.previouslyDeliveredQty,
      deliveryQty: l.deliveryQty,
      uom: l.uom,
      binCode: l.binCode,
      batchNo: l.batchNo,
      unitPrice: l.unitPrice,
      amount: l.amount
    }))
    await api.put(`/delivery-notes/${detailDialog.data.id}/lines`, { lines: linesToSave })
    ElMessage.success(labels.value.save)
    await viewDetail(detailDialog.data.id)
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'Failed')
  } finally {
    detailDialog.saving = false
  }
}

async function confirmNote(id: string) {
  try {
    await api.post(`/delivery-notes/${id}/confirm`)
    ElMessage.success('Confirmed')
    if (detailDialog.visible && detailDialog.data?.id === id) {
      await viewDetail(id)
    }
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'Failed')
  }
}

async function shipNote(id: string) {
  try {
    await ElMessageBox.confirm(labels.value.confirmShip, { type: 'warning' })
    await api.post(`/delivery-notes/${id}/ship`)
    ElMessage.success('Shipped')
    if (detailDialog.visible && detailDialog.data?.id === id) {
      await viewDetail(id)
    }
    load()
  } catch (e: any) {
    if (e !== 'cancel') ElMessage.error(e?.response?.data?.error || 'Failed')
  }
}

async function deliverNote(id: string) {
  try {
    await ElMessageBox.confirm(labels.value.confirmDeliver, { type: 'info' })
    await api.post(`/delivery-notes/${id}/deliver`)
    ElMessage.success('Delivered')
    if (detailDialog.visible && detailDialog.data?.id === id) {
      await viewDetail(id)
    }
    load()
  } catch (e: any) {
    if (e !== 'cancel') ElMessage.error(e?.response?.data?.error || 'Failed')
  }
}

async function cancelNote(id: string) {
  try {
    await ElMessageBox.confirm(labels.value.confirmCancel, { type: 'warning' })
    await api.post(`/delivery-notes/${id}/cancel`)
    ElMessage.success('Cancelled')
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
.detail-header {
  margin-bottom: 16px;
}
.detail-actions {
  display: flex;
  gap: 8px;
}
.text-muted {
  color: #909399;
  font-size: 12px;
}
</style>
