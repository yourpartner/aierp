<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ tableLabels.title }}</div>
          <div class="page-actions">
            <el-button type="primary" @click="$router.push('/crm/sales-order/new')">{{ tableLabels.new }}</el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" stripe style="width: 100%" v-loading="loading">
        <el-table-column prop="so_no" :label="tableLabels.number" width="160" />
        <el-table-column prop="partner_code" :label="tableLabels.customer" width="160" />
        <el-table-column prop="amount_total" :label="tableLabels.amount" width="120">
          <template #default="{ row }">{{ formatAmount(row.amount_total) }}</template>
        </el-table-column>
        <el-table-column prop="order_date" :label="tableLabels.issueDate" width="110" />
        <el-table-column prop="status" :label="tableLabels.status" width="120">
          <template #default="{ row }">
            <el-tag :type="getStatusType(row.status)" size="small">{{ getStatusLabel(row.status) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="進捗" width="180">
          <template #default="{ row }">
            <div class="lifecycle-progress">
              <el-progress 
                :percentage="getLifecycleProgress(row.so_no)" 
                :stroke-width="6"
                :show-text="false"
                :color="getProgressColor(getLifecycleProgress(row.so_no))"
              />
              <span class="lifecycle-stages">{{ getLifecycleStages(row.so_no) }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column :label="tableLabels.actions" width="140">
          <template #default="{ row }">
            <el-button size="small" @click="openDetail(row)">{{ tableLabels.view || '詳細' }}</el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-dialog
        v-model="detailVisible"
        :title="tableLabels.detailTitle"
        width="fit-content"
        :style="{ maxWidth: '95vw' }"
        destroy-on-close
        class="detail-dialog"
      >
        <el-skeleton :loading="detailLoading" :rows="6" animated />
        <template v-if="!detailLoading">
          <div v-if="detail?.error" class="detail-error">{{ detail.error }}</div>
          <div v-else-if="detail" class="detail-content">
            <div class="so-detail-layout">
              <!-- 左侧：订单详情 -->
              <div class="so-detail-main">
                <el-descriptions :column="2" border size="small">
                  <el-descriptions-item :label="tableLabels.number">{{ detail?.soNo || detail?.so_no }}</el-descriptions-item>
                  <el-descriptions-item :label="tableLabels.issueDate">{{ detail?.orderDate || detail?.order_date }}</el-descriptions-item>
                  <el-descriptions-item :label="tableLabels.customer">
                    {{ detail?.partnerName || detail?.partner_name || detail?.partnerCode || detail?.partner_code }}
                  </el-descriptions-item>
                  <el-descriptions-item :label="tableLabels.status">
                    <el-tag :type="getStatusType(detail?.status)" size="small">{{ getStatusLabel(detail?.status) }}</el-tag>
                  </el-descriptions-item>
                </el-descriptions>

                <el-divider content-position="left">明細</el-divider>
                <el-table :data="detailLines(detail)" size="small" stripe border class="detail-table">
                  <el-table-column label="#" width="40" align="center">
                    <template #default="{ $index }">{{ $index + 1 }}</template>
                  </el-table-column>
                  <el-table-column label="品目" min-width="200">
                    <template #default="{ row }">{{ row.materialName || row.materialCode }}</template>
                  </el-table-column>
                  <el-table-column label="数量" width="80" align="right">
                    <template #default="{ row }">{{ row.quantity }} {{ row.uom || '' }}</template>
                  </el-table-column>
                  <el-table-column label="単価" width="80" align="right">
                    <template #default="{ row }">{{ formatAmount(row.unitPrice) }}</template>
                  </el-table-column>
                  <el-table-column label="金額" width="90" align="right">
                    <template #default="{ row }">{{ formatAmount(row.amount) }}</template>
                  </el-table-column>
                </el-table>

                <div class="so-totals-inline">
                  <span class="total-item">税抜: <strong>¥{{ formatAmount(calcSubtotal(detailLines(detail))) }}</strong></span>
                  <span class="total-item">税: <strong>¥{{ formatAmount(detail?.taxAmountTotal) }}</strong></span>
                  <span class="total-item total-grand">合計: <strong>¥{{ formatAmount(detail?.amountTotal || detail?.amount_total) }}</strong></span>
                </div>

                <div v-if="detail?.note" class="so-note">
                  <strong>備考:</strong> {{ detail.note }}
                </div>
              </div>

              <!-- 右侧：进度追踪（时间线样式） -->
              <div class="so-detail-progress">
                <div class="progress-title">進捗</div>
                
                <div class="progress-timeline">
                  <!-- 受注 -->
                  <div class="timeline-item active">
                    <div class="timeline-node">
                      <el-icon><Document /></el-icon>
                    </div>
                    <div class="timeline-content">
                      <div class="timeline-label">受注</div>
                      <div class="timeline-value">{{ detail?.soNo || detail?.so_no }}</div>
                      <div class="timeline-date">{{ detail?.orderDate || detail?.order_date }}</div>
                    </div>
                  </div>
                  
                  <!-- 纳品书 -->
                  <div class="timeline-item" :class="{ active: isStageActive('DeliveryNote'), pending: !isStageActive('DeliveryNote') }">
                    <div class="timeline-node">
                      <el-icon><Document /></el-icon>
                    </div>
                    <div class="timeline-content">
                      <div class="timeline-label">納品書作成</div>
                      <template v-if="isStageActive('DeliveryNote')">
                        <div class="timeline-value">{{ getStageDocNo('DeliveryNote') || '作成済' }}</div>
                        <div class="timeline-date">{{ getStageLabel('DeliveryNote') }}</div>
                      </template>
                      <div v-else class="timeline-pending">未開始</div>
                    </div>
                  </div>

                  <!-- 出库 -->
                  <div class="timeline-item" :class="{ active: isStageActive('Shipped'), pending: !isStageActive('Shipped') }">
                    <div class="timeline-node">
                      <el-icon><Box /></el-icon>
                    </div>
                    <div class="timeline-content">
                      <div class="timeline-label">出庫</div>
                      <template v-if="isStageActive('Shipped')">
                        <div class="timeline-value">{{ getStageLabel('Shipped') }}</div>
                      </template>
                      <div v-else class="timeline-pending">{{ getStageLabel('Shipped') }}</div>
                    </div>
                  </div>

                  <!-- 请求书 -->
                  <div class="timeline-item" :class="{ active: isStageActive('Invoice'), pending: !isStageActive('Invoice') }">
                    <div class="timeline-node">
                      <el-icon><Tickets /></el-icon>
                    </div>
                    <div class="timeline-content">
                      <div class="timeline-label">請求書 <span class="optional-badge">任意</span></div>
                      <template v-if="isStageActive('Invoice')">
                        <div class="timeline-value">{{ getStageDocNo('Invoice') || getStageLabel('Invoice') }}</div>
                      </template>
                      <div v-else class="timeline-pending">{{ getStageLabel('Invoice') }}</div>
                    </div>
                  </div>

                  <!-- 入金 -->
                  <div class="timeline-item" :class="{ active: isStageActive('Payment'), pending: !isStageActive('Payment') }">
                    <div class="timeline-node">
                      <el-icon><Money /></el-icon>
                    </div>
                    <div class="timeline-content">
                      <div class="timeline-label">入金</div>
                      <template v-if="isStageActive('Payment')">
                        <div class="timeline-value">{{ getStageLabel('Payment') }}</div>
                      </template>
                      <div v-else class="timeline-pending">{{ getStageLabel('Payment') }}</div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </template>
        <template #footer>
          <div class="dialog-footer">
            <el-button @click="detailVisible = false">{{ tableLabels.close || '閉じる' }}</el-button>
            <el-button v-if="!hasDeliveryNote()" type="primary" @click="openDeliveryDialog" :disabled="!currentOrderId">納品書作成</el-button>
          </div>
        </template>
      </el-dialog>

      <!-- 创建纳品书对话框 -->
      <el-dialog v-model="deliveryDialogVisible" title="納品書作成" width="480px" class="delivery-dialog">
        <el-form label-width="80px" class="delivery-form">
          <el-form-item label="倉庫" required>
            <el-select v-model="deliveryForm.warehouseCode" placeholder="倉庫を選択" class="delivery-select">
              <el-option v-for="w in warehouseOptions" :key="w.warehouse_code" :label="`${w.name} (${w.warehouse_code})`" :value="w.warehouse_code" />
            </el-select>
          </el-form-item>
          <el-form-item label="納品日">
            <el-date-picker v-model="deliveryForm.deliveryDate" type="date" value-format="YYYY-MM-DD" class="delivery-date" />
          </el-form-item>
        </el-form>
        <template #footer>
          <div class="dialog-footer">
            <el-button @click="deliveryDialogVisible = false">キャンセル</el-button>
            <el-button type="primary" :loading="deliveryCreating" @click="createDeliveryNote">作成</el-button>
          </div>
        </template>
      </el-dialog>

      <div class="page-pagination">
        <el-pagination
          background
          layout="prev, pager, next, sizes, total"
          :total="total"
          :page-size="pageSize"
          :current-page="page"
          @update:page-size="onPageSize"
          @update:current-page="onPage" />
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref, reactive, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { Document, Box, Tickets, Money } from '@element-plus/icons-vue'
import api from '../api'
import { useI18n } from '../i18n'
import SalesOrderLifecycle from '../components/SalesOrderLifecycle.vue'

// 状态映射
function getStatusType(status: string) {
  const map: Record<string, string> = {
    new: 'info',
    partial_shipped: 'warning',
    shipped: 'primary',
    partial_invoiced: '',
    invoiced: 'success',
    completed: 'success',
    cancelled: 'danger',
    draft: 'info',
    confirmed: 'primary'
  }
  return map[status] || 'info'
}

function getStatusLabel(status: string) {
  const map: Record<string, string> = {
    new: '新規登録',
    partial_shipped: '一部出庫',
    shipped: '出庫完了',
    partial_invoiced: '一部請求',
    invoiced: '請求完了',
    completed: '完了',
    cancelled: 'キャンセル',
    draft: '下書き',
    confirmed: '確定'
  }
  return map[status] || status
}

const router = useRouter()

const { section } = useI18n()
const tableLabels = section(
  {
    title: '',
    new: '',
    number: '',
    customer: '',
    amount: '',
    status: '',
    issueDate: '',
    actions: '',
    view: '',
    detailTitle: '',
    close: ''
  },
  (msg) => msg.tables.salesOrders
)

const rows = ref<any[]>([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const detailVisible = ref(false)
const detailLoading = ref(false)
const detail = ref<any | null>(null)
const currentOrderId = ref<string | null>(null)

// 纳品书创建相关
const deliveryDialogVisible = ref(false)
const deliveryCreating = ref(false)
const warehouseOptions = ref<any[]>([])
const deliveryForm = reactive({
  warehouseCode: '',
  deliveryDate: new Date().toISOString().slice(0, 10)
})

// 生命周期追踪
const lifecycleSummaries = ref<Record<string, any>>({})
const lifecycleData = ref<any>(null)

// 阶段名称映射（支持枚举数字和字符串）
const stageNameMap: Record<number | string, string> = {
  0: 'Order', 'Order': 'Order',
  1: 'DeliveryNote', 'DeliveryNote': 'DeliveryNote',
  2: 'Shipped', 'Shipped': 'Shipped',
  3: 'Invoice', 'Invoice': 'Invoice',
  4: 'Payment', 'Payment': 'Payment'
}

// 状态值映射（支持枚举数字和字符串）
const statusMap: Record<number | string, string> = {
  0: 'NotStarted', 'NotStarted': 'NotStarted',
  1: 'InProgress', 'InProgress': 'InProgress',
  2: 'Completed', 'Completed': 'Completed',
  3: 'Skipped', 'Skipped': 'Skipped'
}

// 从 lifecycleData.stages 中获取阶段状态的辅助函数
function getStageStatus(stageName: string) {
  if (!lifecycleData.value?.stages) return null
  return lifecycleData.value.stages.find((s: any) => {
    const normalizedStageName = stageNameMap[s.stage] || s.stageName
    return normalizedStageName === stageName
  })
}

// 判断阶段是否完成或进行中
function isStageActive(stageName: string) {
  const stage = getStageStatus(stageName)
  if (!stage) return false
  const normalizedStatus = statusMap[stage.status] || stage.status
  return normalizedStatus === 'Completed' || normalizedStatus === 'InProgress'
}

// 判断阶段是否完成
function isStageCompleted(stageName: string) {
  const stage = getStageStatus(stageName)
  if (!stage) return false
  const normalizedStatus = statusMap[stage.status] || stage.status
  return normalizedStatus === 'Completed'
}

// 获取阶段显示状态
function getStageLabel(stageName: string) {
  const stage = getStageStatus(stageName)
  if (!stage) return '未開始'
  return stage.statusLabel || '未開始'
}

// 获取阶段文档号
function getStageDocNo(stageName: string) {
  const stage = getStageStatus(stageName)
  return stage?.documentNo
}

// 判断是否有纳品书
function hasDeliveryNote() {
  return isStageActive('DeliveryNote')
}

function getLifecycleProgress(soNo: string) {
  const summary = lifecycleSummaries.value[soNo]
  if (!summary) return 0
  // 4个必要阶段：受注、纳品书、出库、入金
  return Math.round((summary.completedStages / 4) * 100)
}

function getLifecycleStages(soNo: string) {
  const summary = lifecycleSummaries.value[soNo]
  if (!summary) return '-'
  const stages = []
  stages.push('受注')
  if (summary.hasDeliveryNote) stages.push('納品')
  if (summary.hasShipped) stages.push('出庫')
  if (summary.hasInvoice) stages.push('請求')
  if (summary.hasPayment) stages.push('入金')
  return stages.join('→')
}

function getProgressColor(pct: number) {
  if (pct >= 100) return '#67c23a'
  if (pct >= 75) return '#409eff'
  if (pct >= 50) return '#e6a23c'
  return '#909399'
}

async function loadLifecycleSummaries() {
  const soNos = rows.value.map(r => r.so_no).filter(Boolean)
  if (soNos.length === 0) return
  try {
    const res = await api.post('/sales-orders/lifecycle-summaries', { soNos })
    lifecycleSummaries.value = res.data || {}
  } catch (e) {
    console.error('Failed to load lifecycle summaries:', e)
  }
}

// 加载数据后加载生命周期摘要
watch(rows, () => {
  loadLifecycleSummaries()
})
function detailLines(item: any) {
  if (!item) return []
  if (Array.isArray(item.lines)) return item.lines
  if (Array.isArray(item.payload?.lines)) return item.payload.lines
  return []
}

function calcSubtotal(lines: any[] | undefined) {
  if (!lines) return 0
  return lines.reduce((sum, line) => sum + (line.amount || 0), 0)
}

function formatAmount(val: any) {
  const num = Number(val || 0)
  if (!Number.isFinite(num)) return val ?? ''
  return num.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 2 })
}

async function load() {
  loading.value = true
  try {
    const r = await api.post('/objects/sales_order/search', {
      page: page.value,
      pageSize: pageSize.value,
      where: [],
      orderBy: [{ field: 'order_date', dir: 'DESC' }]
    })
    const data = Array.isArray(r.data?.data) ? r.data.data : []
    rows.value = data
    total.value = Number(r.data?.total) || data.length
  } finally {
    loading.value = false
  }
}

function onPage(p: number) {
  page.value = p
  load()
}

function onPageSize(s: number) {
  pageSize.value = s
  page.value = 1
  load()
}

onMounted(() => {
  load()
  loadWarehouses()
})

async function loadWarehouses() {
  try {
    const r = await api.get('/inventory/warehouses')
    warehouseOptions.value = r.data || []
  } catch (e) {
    console.error('Failed to load warehouses:', e)
  }
}

function openDeliveryDialog() {
  deliveryForm.warehouseCode = ''
  deliveryForm.deliveryDate = new Date().toISOString().slice(0, 10)
  deliveryDialogVisible.value = true
}

async function createDeliveryNote() {
  if (!deliveryForm.warehouseCode) {
    ElMessage.warning('倉庫を選択してください')
    return
  }
  if (!currentOrderId.value) {
    ElMessage.error('受注IDが見つかりません')
    return
  }
  deliveryCreating.value = true
  try {
    const res = await api.post(`/delivery-notes/from-sales-order/${currentOrderId.value}`, deliveryForm)
    ElMessage.success(`納品書 ${res.data?.deliveryNo} を作成しました`)
    deliveryDialogVisible.value = false
    
    // 刷新生命周期数据，而不是关闭详情弹窗
    const soNo = detail.value?.soNo || detail.value?.so_no
    if (soNo) {
      try {
        const lcResp = await api.get(`/sales-orders/${soNo}/lifecycle`)
        lifecycleData.value = lcResp.data
      } catch { /* ignore */ }
    }
    
    // 刷新列表数据
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '作成に失敗しました')
  } finally {
    deliveryCreating.value = false
  }
}

async function openDetail(row: any) {
  if (!row) return
  detailVisible.value = true
  detailLoading.value = true
  detail.value = null
  lifecycleData.value = null
  currentOrderId.value = row.id || null
  try {
    const id = row.id
    if (id) {
      const resp = await api.get(`/objects/sales_order/${encodeURIComponent(id)}`)
      const payload = resp.data?.payload || resp.data
      if (payload) {
        detail.value = payload
        // 加载生命周期数据
        const soNo = payload.soNo || row.so_no
        if (soNo) {
          try {
            const lcResp = await api.get(`/sales-orders/${soNo}/lifecycle`)
            lifecycleData.value = lcResp.data
          } catch { /* ignore */ }
        }
        return
      }
    }
    const soNo = row.so_no || row.payload?.soNo
    if (soNo) {
      const resp = await api.post('/objects/sales_order/search', {
        page: 1,
        pageSize: 1,
        where: [{ field: 'so_no', op: 'eq', value: soNo }]
      })
      const match = Array.isArray(resp.data?.data) ? resp.data.data[0] : null
      const payload = match?.payload || match
      if (payload) {
        detail.value = payload
        // 加载生命周期数据
        try {
          const lcResp = await api.get(`/sales-orders/${soNo}/lifecycle`)
          lifecycleData.value = lcResp.data
        } catch { /* ignore */ }
        return
      }
    }
    detail.value = row.payload || row
  } catch (e: any) {
    detail.value = {
      error: e?.response?.data?.error || e?.message || '取得に失敗しました'
    }
  } finally {
    detailLoading.value = false
  }
}
</script>

<style scoped>
.detail-dialog :deep(.el-dialog__body) {
  max-height: 75vh;
  overflow-y: auto;
  padding: 16px 20px;
}
.detail-content {
  display: block;
  width: fit-content;
  flex-direction: column;
  gap: 16px;
}
.detail-descriptions :deep(.el-descriptions__body) {
  word-break: break-word;
}
.detail-section-title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
}
.detail-table-wrapper {
  max-height: 360px;
  overflow-y: auto;
  border: 1px solid #e5e7eb;
  border-radius: 6px;
}
.detail-table {
  min-width: 100%;
}
.detail-error {
  color: #ef4444;
}
.detail-dialog :deep(.el-descriptions__body) {
  word-break: break-word;
}
.lifecycle-progress {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.lifecycle-stages {
  font-size: 11px;
  color: #909399;
}
.lifecycle-section {
  margin-bottom: 16px;
  padding: 12px;
  background: #f5f7fa;
  border-radius: 6px;
}

/* 两栏布局 */
.so-detail-layout {
  display: flex;
  gap: 20px;
  width: fit-content;
}

.so-detail-main {
  flex: 0 0 auto;
  width: 550px;
}

.so-detail-progress {
  width: 200px;
  flex-shrink: 0;
  background: linear-gradient(135deg, #f8fafc 0%, #f1f5f9 100%);
  border-radius: 8px;
  padding: 16px;
  border: 1px solid #e2e8f0;
  align-self: flex-start;  /* 不拉伸到父容器高度 */
  height: fit-content;
}

.progress-title {
  font-size: 14px;
  font-weight: 600;
  color: #1e293b;
  margin-bottom: 16px;
  padding-bottom: 10px;
  border-bottom: 2px solid #3b82f6;
}

/* 时间线 */
.progress-timeline {
  position: relative;
}

.timeline-item {
  display: flex;
  gap: 10px;
  padding-bottom: 16px;
  position: relative;
}

.timeline-item:not(:last-child)::before {
  content: '';
  position: absolute;
  left: 12px;
  top: 28px;
  bottom: 0;
  width: 2px;
  background: #e2e8f0;
}

.timeline-item.active:not(:last-child)::before {
  background: linear-gradient(180deg, #3b82f6 0%, #e2e8f0 100%);
}

.timeline-node {
  width: 26px;
  height: 26px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  background: #e2e8f0;
  color: #94a3b8;
  flex-shrink: 0;
  z-index: 1;
  font-size: 12px;
}

.timeline-item.active .timeline-node {
  background: linear-gradient(135deg, #3b82f6 0%, #2563eb 100%);
  color: white;
  box-shadow: 0 2px 8px rgba(59, 130, 246, 0.3);
}

.timeline-item.pending .timeline-node {
  background: #f1f5f9;
  border: 2px dashed #cbd5e1;
}

.timeline-content {
  flex: 1;
  padding-top: 2px;
  min-width: 0;
}

.timeline-label {
  font-size: 12px;
  font-weight: 600;
  color: #475569;
  margin-bottom: 2px;
}

.timeline-value {
  font-size: 11px;
  color: #1e293b;
  font-weight: 500;
}

.timeline-date {
  font-size: 10px;
  color: #64748b;
}

.timeline-pending {
  font-size: 11px;
  color: #94a3b8;
  font-style: italic;
}

.optional-badge {
  font-size: 9px;
  padding: 1px 4px;
  background: #f0f0f0;
  color: #909399;
  border-radius: 2px;
  margin-left: 4px;
}

.so-totals-inline {
  display: flex;
  justify-content: flex-end;
  align-items: center;
  gap: 24px;
  margin-top: 12px;
  padding: 10px 16px;
  background: #f5f7fa;
  border-radius: 4px;
  font-size: 13px;
}

.so-totals-inline .total-item {
  color: #606266;
}

.so-totals-inline .total-item strong {
  color: #303133;
  margin-left: 4px;
}

.so-totals-inline .total-item.total-grand {
  color: #409eff;
  font-weight: 500;
  padding-left: 16px;
  border-left: 2px solid #409eff;
}

.so-totals-inline .total-item.total-grand strong {
  color: #409eff;
  font-size: 14px;
}

.so-note {
  margin-top: 12px;
  padding: 10px;
  background: #fdf6ec;
  border-radius: 4px;
  color: #e6a23c;
  font-size: 13px;
}

.dialog-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

/* 纳品书创建弹窗样式 */
.delivery-dialog :deep(.el-dialog__body) {
  padding: 20px 24px;
  overflow: visible;
}

.delivery-form {
  max-width: 100%;
}

.delivery-form :deep(.el-form-item) {
  margin-bottom: 18px;
}

.delivery-select {
  width: 100%;
  max-width: 320px;
}

.delivery-date {
  width: 100%;
  max-width: 200px;
}

.dialog-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>


