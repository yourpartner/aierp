<template>
  <div class="page page-wide">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">発注一覧</div>
          <div class="page-actions">
            <el-button type="primary" @click="$router.push('/purchase-order/new')">
              <el-icon><Plus /></el-icon> 新規発注
            </el-button>
          </div>
        </div>
      </template>

      <!-- 搜索栏 -->
      <div class="search-bar">
        <el-input v-model="searchQuery" placeholder="発注番号・仕入先で検索..." clearable style="width:300px" @keyup.enter="loadData">
          <template #prefix><el-icon><Search /></el-icon></template>
        </el-input>
        <el-select v-model="statusFilter" placeholder="ステータス" clearable style="width:150px" @change="loadData">
          <el-option label="新規" value="new" />
          <el-option label="一部入庫" value="partial_received" />
          <el-option label="入庫完了" value="fully_received" />
          <el-option label="一部請求済" value="partial_invoiced" />
          <el-option label="請求済" value="fully_invoiced" />
          <el-option label="完了" value="closed" />
        </el-select>
        <el-date-picker v-model="dateRange" type="daterange" start-placeholder="開始日" end-placeholder="終了日" value-format="YYYY-MM-DD" @change="loadData" />
        <el-button @click="loadData"><el-icon><Refresh /></el-icon></el-button>
      </div>

      <!-- 数据表格 -->
      <el-table :data="list" v-loading="loading" border stripe @row-click="onRowClick" style="cursor:pointer">
        <el-table-column prop="po_no" label="発注番号" width="150" />
        <el-table-column label="仕入先" min-width="200">
          <template #default="{ row }">
            <span>{{ row.payload?.partnerName || row.partner_code }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="order_date" label="発注日" width="120" />
        <el-table-column label="納期" width="120">
          <template #default="{ row }">{{ row.payload?.expectedDeliveryDate || '-' }}</template>
        </el-table-column>
        <el-table-column label="金額" width="140" align="right">
          <template #default="{ row }">
            <span class="amount-cell">{{ formatNumber(row.amount_total) }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="status" label="ステータス" width="120">
          <template #default="{ row }">
            <el-tag :type="getStatusType(row.status)" size="small">{{ getStatusLabel(row.status) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="180" fixed="right">
          <template #default="{ row }">
            <el-button type="primary" text size="small" @click.stop="viewDetail(row)">詳細</el-button>
            <el-button v-if="row.status === 'new' || row.status === 'partial_received'" type="success" text size="small" @click.stop="openReceiveDialog(row)">入庫</el-button>
            <el-button v-if="row.status === 'new'" type="danger" text size="small" @click.stop="deletePO(row)">削除</el-button>
          </template>
        </el-table-column>
      </el-table>

      <!-- 分页 -->
      <div class="pagination-bar">
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="pageSize"
          :total="total"
          :page-sizes="[20, 50, 100]"
          layout="total, sizes, prev, pager, next"
          @size-change="loadData"
          @current-change="loadData"
        />
      </div>
    </el-card>

    <!-- 详情/编辑弹窗 -->
    <el-dialog v-model="showDetail" :title="isEditMode ? '発注編集' : '発注詳細'" width="fit-content" :style="{ maxWidth: '95vw' }" destroy-on-close>
      <div v-if="detailData" class="po-detail">
        <!-- 查看模式 -->
        <template v-if="!isEditMode">
          <div class="po-detail-layout">
            <!-- 左侧：订单详情 -->
            <div class="po-detail-main">
              <el-descriptions :column="2" border size="small">
                <el-descriptions-item label="発注番号">{{ detailData.payload?.poNo }}</el-descriptions-item>
                <el-descriptions-item label="発注日">{{ detailData.payload?.orderDate }}</el-descriptions-item>
                <el-descriptions-item label="仕入先">{{ detailData.payload?.partnerName }} ({{ detailData.partner_code }})</el-descriptions-item>
                <el-descriptions-item label="ステータス">
                  <el-tag :type="getActualPoStatusType()" size="small">{{ getActualPoStatusLabel() }}</el-tag>
                </el-descriptions-item>
              </el-descriptions>

              <el-divider content-position="left">明細</el-divider>
              <el-table :data="detailData.payload?.lines || []" border size="small" class="detail-table">
                <el-table-column label="#" width="40" align="center">
                  <template #default="{ $index }">{{ $index + 1 }}</template>
                </el-table-column>
                <el-table-column label="品目" min-width="200">
                  <template #default="{ row }">{{ row.materialName || row.materialCode }}</template>
                </el-table-column>
                <el-table-column label="数量" width="80" align="right">
                  <template #default="{ row }">{{ row.quantity }} {{ row.uom || '' }}</template>
                </el-table-column>
                <el-table-column label="入庫済" width="80" align="right">
                  <template #default="{ row }">{{ row.receivedQuantity || 0 }} {{ row.uom || '' }}</template>
                </el-table-column>
                <el-table-column label="単価" width="80" align="right">
                  <template #default="{ row }">{{ formatNumber(row.unitPrice) }}</template>
                </el-table-column>
                <el-table-column label="金額" width="90" align="right">
                  <template #default="{ row }">{{ formatNumber(row.amount) }}</template>
                </el-table-column>
                <el-table-column label="納期" width="90">
                  <template #default="{ row }">{{ row.deliveryDate || '-' }}</template>
                </el-table-column>
                <el-table-column label="状態" width="80">
                  <template #default="{ row }">
                    <el-tag :type="getActualLineStatusType(row)" size="small">{{ getActualLineStatusLabel(row) }}</el-tag>
                  </template>
                </el-table-column>
              </el-table>

              <div class="po-totals-inline">
                <span class="total-item">税抜合計: <strong>¥{{ formatNumber(calcSubtotal(detailData.payload?.lines)) }}</strong></span>
                <span class="total-item">消費税: <strong>¥{{ formatNumber(detailData.payload?.taxAmountTotal) }}</strong></span>
                <span class="total-item total-grand">合計（税込）: <strong>¥{{ formatNumber(detailData.amount_total) }}</strong></span>
              </div>

              <div v-if="detailData.payload?.note" class="po-note">
                <strong>備考:</strong> {{ detailData.payload.note }}
              </div>
            </div>

            <!-- 右侧：进度追踪 -->
            <div class="po-detail-progress">
              <div class="progress-title">進捗</div>
              
              <div class="progress-timeline">
                <!-- 采购订单 -->
                <div class="timeline-item active">
                  <div class="timeline-node">
                    <el-icon><Document /></el-icon>
                  </div>
                  <div class="timeline-content">
                    <div class="timeline-label">発注</div>
                    <div class="timeline-value">{{ detailData.payload?.poNo }}</div>
                    <div class="timeline-date">{{ detailData.payload?.orderDate }}</div>
                  </div>
                </div>
                
                <!-- 入库 -->
                <div class="timeline-item" :class="{ active: progressData.receipts?.length > 0, pending: !progressData.receipts?.length }">
                  <div class="timeline-node">
                    <el-icon><Box /></el-icon>
                  </div>
                  <div class="timeline-content">
                    <div class="timeline-label">入庫</div>
                    <template v-if="progressData.receipts?.length > 0">
                      <div v-for="(receipt, idx) in progressData.receipts" :key="idx" class="timeline-sub-item">
                        <div class="timeline-value">{{ receipt.movementDate }}</div>
                        <div class="timeline-detail">
                          {{ getReceiptQuantityText(receipt) }}
                        </div>
                      </div>
                    </template>
                    <div v-else class="timeline-pending">未入庫</div>
                  </div>
                </div>

                <!-- 请求书/凭证 -->
                <div class="timeline-item" :class="{ active: progressData.vouchers?.length > 0, pending: !progressData.vouchers?.length }">
                  <div class="timeline-node">
                    <el-icon><Tickets /></el-icon>
                  </div>
                  <div class="timeline-content">
                    <div class="timeline-label">請求書</div>
                    <template v-if="progressData.vouchers?.length > 0">
                      <div v-for="(invoice, idx) in progressData.vouchers" :key="idx" class="timeline-sub-item">
                        <div class="timeline-value">{{ invoice.invoiceNo }}</div>
                        <div class="timeline-detail">{{ invoice.postingDate }} / ¥{{ formatNumber(invoice.amountTotal) }}</div>
                        <div v-if="invoice.voucherNumber" class="timeline-voucher clickable" @click="openVoucher(invoice.voucherNumber)">
                          伝票: {{ invoice.voucherNumber }}
                        </div>
                        <el-tag v-if="invoice.status === 'posted'" type="success" size="small">転記済</el-tag>
                        <el-tag v-else-if="invoice.status === 'pending_post'" type="warning" size="small">未転記</el-tag>
                      </div>
                    </template>
                    <div v-else class="timeline-pending">未受領</div>
                  </div>
                </div>

                <!-- 支付 -->
                <div class="timeline-item pending">
                  <div class="timeline-node">
                    <el-icon><Money /></el-icon>
                  </div>
                  <div class="timeline-content">
                    <div class="timeline-label">支払</div>
                    <div class="timeline-pending">未払い</div>
                  </div>
                </div>
              </div>

            </div>
          </div>
        </template>

        <!-- 编辑模式 -->
        <template v-else>
          <el-form label-width="100px" size="small">
            <el-row :gutter="16">
              <el-col :span="8">
                <el-form-item label="発注番号">
                  <el-input v-model="editForm.poNo" disabled />
                </el-form-item>
              </el-col>
              <el-col :span="8">
                <el-form-item label="発注日">
                  <el-date-picker v-model="editForm.orderDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
                </el-form-item>
              </el-col>
              <el-col :span="8">
                <el-form-item label="仕入先">
                  <el-select v-model="editForm.partnerCode" filterable remote reserve-keyword :remote-method="searchVendors" :loading="vendorLoading" placeholder="仕入先を検索..." style="width:100%" @change="onVendorChange">
                    <el-option v-for="p in vendorOptions" :key="p.value" :label="p.label" :value="p.value" />
                  </el-select>
                </el-form-item>
              </el-col>
            </el-row>

            <el-divider content-position="left">明細</el-divider>
            <div class="edit-lines-toolbar">
              <el-button type="primary" size="small" @click="addEditLine">行を追加</el-button>
            </div>
            <el-table :data="editForm.lines" border size="small" class="edit-table">
              <el-table-column label="#" width="40" align="center">
                <template #default="{ $index }">{{ $index + 1 }}</template>
              </el-table-column>
              <el-table-column label="品目" min-width="200">
                <template #default="{ row }">
                  <el-select v-model="row.materialCode" filterable remote reserve-keyword :remote-method="(q) => searchMaterials(q)" :loading="materialLoading" placeholder="品目..." style="width:100%" @change="(code) => onMaterialChange(code, row)">
                    <el-option v-for="m in getMaterialOptions(row)" :key="m.value" :label="m.label" :value="m.value" />
                  </el-select>
                </template>
              </el-table-column>
              <el-table-column label="数量" width="80">
                <template #default="{ row }">
                  <el-input-number v-model="row.quantity" :min="0" :controls="false" style="width:100%" @change="recalcEditLine(row)" />
                </template>
              </el-table-column>
              <el-table-column label="単価" width="100">
                <template #default="{ row }">
                  <el-input-number v-model="row.unitPrice" :min="0" :controls="false" style="width:100%" @change="recalcEditLine(row)" />
                </template>
              </el-table-column>
              <el-table-column label="金額" width="90" align="right">
                <template #default="{ row }">{{ formatNumber(row.amount) }}</template>
              </el-table-column>
              <el-table-column label="税率" width="80">
                <template #default="{ row }">
                  <el-select v-model="row.taxRate" style="width:100%" @change="recalcEditLine(row)">
                    <el-option label="10%" :value="10" />
                    <el-option label="8%" :value="8" />
                    <el-option label="0%" :value="0" />
                  </el-select>
                </template>
              </el-table-column>
              <el-table-column label="納期" width="130">
                <template #default="{ row }">
                  <el-date-picker v-model="row.deliveryDate" type="date" value-format="YYYY-MM-DD" placeholder="納期" style="width:100%" />
                </template>
              </el-table-column>
              <el-table-column label="" width="50" align="center">
                <template #default="{ $index }">
                  <el-button type="danger" text size="small" @click="removeEditLine($index)">×</el-button>
                </template>
              </el-table-column>
            </el-table>

            <div class="po-totals">
              <div class="total-item">
                <span>税抜合計:</span>
                <span class="total-value">¥{{ formatNumber(editSubtotal) }}</span>
              </div>
              <div class="total-item">
                <span>消費税:</span>
                <span class="total-value">¥{{ formatNumber(editForm.taxAmountTotal) }}</span>
              </div>
              <div class="total-item total-grand">
                <span>合計（税込）:</span>
                <span class="total-value">¥{{ formatNumber(editForm.amountTotal) }}</span>
              </div>
            </div>

            <el-form-item label="備考" style="margin-top:16px">
              <el-input v-model="editForm.note" type="textarea" :rows="2" />
            </el-form-item>
          </el-form>
        </template>
      </div>
      <template #footer>
        <template v-if="!isEditMode">
          <el-button @click="showDetail = false">閉じる</el-button>
          <el-button v-if="detailData?.status === 'new'" type="primary" @click="switchToEditMode">編集</el-button>
        </template>
        <template v-else>
          <el-button @click="cancelEdit">キャンセル</el-button>
          <el-button type="primary" :loading="saving" @click="saveEdit">保存</el-button>
        </template>
      </template>
    </el-dialog>

    <!-- 入库弹窗 -->
    <el-dialog v-model="showReceive" title="入庫処理" width="800px" destroy-on-close>
      <div v-if="receiveData" style="padding-right: 10px;">
        <el-alert type="info" :closable="false" style="margin-bottom:16px">
          発注番号: {{ receiveData.payload?.poNo }} | 仕入先: {{ receiveData.payload?.partnerName }}
        </el-alert>
        
        <el-form label-width="100px">
          <el-form-item label="入庫日">
            <el-date-picker v-model="receiveForm.movementDate" type="date" value-format="YYYY-MM-DD" style="width:200px" />
          </el-form-item>
          <el-form-item label="入庫先倉庫">
            <el-select v-model="receiveForm.toWarehouse" placeholder="倉庫を選択" style="width:200px" @change="onWarehouseChange">
              <el-option v-for="w in warehouses" :key="w.warehouse_code" :label="w.name || w.warehouse_code" :value="w.warehouse_code" />
            </el-select>
          </el-form-item>
          <el-form-item label="棚番">
            <el-select v-model="receiveForm.toBin" placeholder="棚番を選択" style="width:200px" :loading="binsLoading" :disabled="!receiveForm.toWarehouse">
              <el-option v-for="b in bins" :key="b.bin_code" :label="b.name || b.bin_code" :value="b.bin_code" />
            </el-select>
          </el-form-item>
        </el-form>

        <el-divider content-position="left">入庫明細</el-divider>
        <el-table :data="receiveLines" border size="small" style="margin-bottom:16px">
          <el-table-column label="品目" min-width="200">
            <template #default="{ row }">{{ row.materialName || row.materialCode }}</template>
          </el-table-column>
          <el-table-column label="発注数" width="100" align="right">
            <template #default="{ row }">{{ row.quantity }}</template>
          </el-table-column>
          <el-table-column label="入庫済" width="100" align="right">
            <template #default="{ row }">{{ row.receivedQuantity || 0 }}</template>
          </el-table-column>
          <el-table-column label="残数" width="100" align="right">
            <template #default="{ row }">{{ row.quantity - (row.receivedQuantity || 0) }}</template>
          </el-table-column>
          <el-table-column label="今回入庫数" width="150">
            <template #default="{ row }">
              <el-input-number 
                v-model="row.receiveQty" 
                :min="0" 
                :max="row.quantity - (row.receivedQuantity || 0)"
                :precision="0"
                :controls="false"
                size="small"
                style="width:100%"
              />
            </template>
          </el-table-column>
          <el-table-column label="単位" width="80">
            <template #default="{ row }">{{ row.uom }}</template>
          </el-table-column>
        </el-table>
      </div>
      <template #footer>
        <el-button @click="showReceive = false">キャンセル</el-button>
        <el-button type="primary" :loading="receiving" @click="doReceive">入庫実行</el-button>
      </template>
    </el-dialog>

    <!-- 会计凭证弹窗 -->
    <el-dialog 
      v-model="showVoucherDialog" 
      width="auto" 
      destroy-on-close
      append-to-body
      class="voucher-detail-dialog"
    >
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VouchersList
          v-if="showVoucherDialog && currentVoucherNo"
          class="voucher-detail-embed"
          :allow-edit="false"
          :initial-voucher-no="currentVoucherNo"
        />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, nextTick } from 'vue'
import { Plus, Search, Refresh, Document, Box, Tickets, Money } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useRouter } from 'vue-router'
import api from '../api'
import VouchersList from './VouchersList.vue'

const router = useRouter()

const list = ref<any[]>([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const searchQuery = ref('')
const statusFilter = ref('')
const dateRange = ref<[string, string] | null>(null)

// 详情弹窗
const showDetail = ref(false)
const detailData = ref<any>(null)
const isEditMode = ref(false)
const saving = ref(false)

// 进度数据
const progressData = ref<any>({
  receipts: [],
  vouchers: [],
  receivedSummary: {}
})

// 会计凭证弹窗
const showVoucherDialog = ref(false)
const currentVoucherNo = ref('')


// 编辑表单
const editForm = reactive<any>({
  poNo: '',
  partnerCode: '',
  partnerName: '',
  orderDate: '',
  currency: 'JPY',
  lines: [],
  status: 'new',
  amountTotal: 0,
  taxAmountTotal: 0,
  note: ''
})

// 供应商和物料搜索
const vendorOptions = ref<any[]>([])
const vendorLoading = ref(false)
const materialOptions = ref<any[]>([])
const materialLoading = ref(false)
const materialCache = new Map<string, any>()

// 入库弹窗
const showReceive = ref(false)
const receiveData = ref<any>(null)
const receiveLines = ref<any[]>([])
const receiveForm = reactive({
  movementDate: new Date().toISOString().slice(0, 10),
  toWarehouse: '',
  toBin: ''
})
const receiving = ref(false)
const warehouses = ref<any[]>([])
const bins = ref<any[]>([])
const binsLoading = ref(false)

function formatNumber(value: number | undefined | null) {
  if (value === undefined || value === null) return '-'
  return new Intl.NumberFormat('ja-JP').format(value)
}

function getStatusType(status: string) {
  const map: Record<string, string> = {
    new: 'info',
    partial_received: 'warning',
    fully_received: 'primary',
    partial_invoiced: '',
    fully_invoiced: 'success',
    closed: 'success'
  }
  return map[status] || 'info'
}

function getStatusLabel(status: string) {
  const map: Record<string, string> = {
    new: '新規',
    partial_received: '一部入庫',
    fully_received: '入庫完了',
    partial_invoiced: '一部請求済',
    fully_invoiced: '請求済',
    closed: '完了'
  }
  return map[status] || status
}

function getLineStatusType(status: string) {
  const map: Record<string, string> = {
    open: 'info',
    partial: 'warning',
    closed: 'success',
    cancelled: 'danger'
  }
  return map[status] || 'info'
}

function getLineStatusLabel(status: string) {
  const map: Record<string, string> = {
    open: '未入庫',
    partial: '一部入庫',
    closed: '入庫完了',
    cancelled: 'キャンセル'
  }
  return map[status] || status
}

// 获取采购订单实际状态类型（考虑请求书状态）
function getActualPoStatusType() {
  const hasInvoice = progressData.value?.vouchers?.length > 0
  const hasReceipt = progressData.value?.receipts?.length > 0
  
  if (hasInvoice) {
    const invoice = progressData.value.vouchers[0]
    if (invoice.status === 'posted') return 'success'
    return 'warning'
  }
  
  if (hasReceipt) return 'primary'
  return 'info'
}

// 获取采购订单实际状态文字（考虑请求书状态）
function getActualPoStatusLabel() {
  const hasInvoice = progressData.value?.vouchers?.length > 0
  const hasReceipt = progressData.value?.receipts?.length > 0
  
  if (hasInvoice) {
    const invoice = progressData.value.vouchers[0]
    if (invoice.status === 'posted') return '請求済'
    return '請求中'
  }
  
  if (hasReceipt) return '入庫完了'
  return '新規'
}

// 根据实际状态（包括请求书）返回行状态类型
function getActualLineStatusType(row: any) {
  const hasInvoice = progressData.value?.vouchers?.length > 0
  const receivedQty = row.receivedQuantity || 0
  const orderedQty = row.quantity || 0
  
  if (hasInvoice) {
    const invoice = progressData.value.vouchers[0]
    if (invoice.status === 'posted') return 'success'
    return 'warning'
  }
  
  if (receivedQty >= orderedQty) return 'primary'
  if (receivedQty > 0) return 'warning'
  return 'info'
}

// 根据实际状态（包括请求书）返回行状态文字
function getActualLineStatusLabel(row: any) {
  const hasInvoice = progressData.value?.vouchers?.length > 0
  const receivedQty = row.receivedQuantity || 0
  const orderedQty = row.quantity || 0
  
  if (hasInvoice) {
    const invoice = progressData.value.vouchers[0]
    if (invoice.status === 'posted') return '請求済'
    return '請求中'
  }
  
  if (receivedQty >= orderedQty) return '入庫完了'
  if (receivedQty > 0) return '一部入庫'
  return '未入庫'
}

// 获取入库数量文本（包含单位）
function getReceiptQuantityText(receipt: any) {
  const lines = receipt.lines || []
  const totalQty = lines.reduce((s: number, l: any) => s + (l.quantity || 0), 0)
  // 从采购订单明细获取单位
  const poLines = detailData.value?.payload?.lines || []
  const uom = poLines[0]?.uom || '個'
  return `${totalQty} ${uom}`
}

function calcSubtotal(lines: any[] | undefined) {
  if (!lines) return 0
  return lines.reduce((sum, line) => sum + (line.amount || 0), 0)
}

async function loadData() {
  loading.value = true
  try {
    const where: any[] = []
    if (searchQuery.value) {
      where.push({
        type: 'or',
        conditions: [
          { field: 'po_no', op: 'contains', value: searchQuery.value },
          { field: 'partner_code', op: 'contains', value: searchQuery.value },
          { json: 'partnerName', op: 'contains', value: searchQuery.value }
        ]
      })
    }
    if (statusFilter.value) {
      where.push({ field: 'status', op: 'eq', value: statusFilter.value })
    }
    if (dateRange.value && dateRange.value[0] && dateRange.value[1]) {
      where.push({ field: 'order_date', op: 'gte', value: dateRange.value[0] })
      where.push({ field: 'order_date', op: 'lte', value: dateRange.value[1] })
    }
    
    const resp = await api.post('/objects/purchase_order/search', {
      page: page.value,
      pageSize: pageSize.value,
      where,
      orderBy: [{ field: 'order_date', direction: 'desc' }]
    })
    list.value = resp.data?.data || []
    total.value = resp.data?.total || 0
  } catch (e: any) {
    ElMessage.error('データの読み込みに失敗しました')
  } finally {
    loading.value = false
  }
}

async function loadWarehouses() {
  try {
    const resp = await api.get('/inventory/warehouses')
    warehouses.value = resp.data || []
  } catch { /* ignore */ }
}

function onRowClick(row: any) {
  viewDetail(row)
}

async function viewDetail(row: any) {
  detailData.value = row
  isEditMode.value = false
  progressData.value = { receipts: [], vouchers: [], receivedSummary: {} }
  showDetail.value = true
  
  // 加载进度数据
  try {
    const resp = await api.get(`/purchase-orders/${row.id}/progress`)
    progressData.value = resp.data || { receipts: [], vouchers: [], receivedSummary: {} }
  } catch { /* ignore */ }
}

function openVoucher(voucherNo: string) {
  if (!voucherNo) return
  currentVoucherNo.value = voucherNo
  showVoucherDialog.value = true
}

// 切换到编辑模式
function switchToEditMode() {
  if (!detailData.value) return
  
  // 复制数据到编辑表单
  const payload = detailData.value.payload
  Object.assign(editForm, {
    poNo: payload.poNo,
    partnerCode: payload.partnerCode,
    partnerName: payload.partnerName,
    orderDate: payload.orderDate,
    currency: payload.currency || 'JPY',
    lines: JSON.parse(JSON.stringify(payload.lines || [])),
    status: payload.status,
    amountTotal: payload.amountTotal || 0,
    taxAmountTotal: payload.taxAmountTotal || 0,
    note: payload.note || ''
  })
  
  // 添加供应商到选项
  if (editForm.partnerCode && editForm.partnerName) {
    vendorOptions.value = [{ label: `${editForm.partnerName} (${editForm.partnerCode})`, value: editForm.partnerCode, name: editForm.partnerName }]
  }
  
  // 缓存物料
  editForm.lines.forEach((line: any) => {
    if (line.materialCode) {
      materialCache.set(line.materialCode, {
        label: `${line.materialName || line.materialCode} (${line.materialCode})`,
        value: line.materialCode,
        name: line.materialName,
        uom: line.uom,
        price: line.unitPrice
      })
    }
  })
  
  isEditMode.value = true
}

function cancelEdit() {
  isEditMode.value = false
}

async function saveEdit() {
  if (!detailData.value) return
  
  saving.value = true
  try {
    const payload = JSON.parse(JSON.stringify(editForm))
    payload.lines = payload.lines.filter((l: any) => l.materialCode)
    
    await api.put(`/objects/purchase_order/${detailData.value.id}`, { payload })
    ElMessage.success('保存しました')
    isEditMode.value = false
    showDetail.value = false
    loadData()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '保存に失敗しました')
  } finally {
    saving.value = false
  }
}

// 搜索供应商
async function searchVendors(query: string) {
  vendorLoading.value = true
  try {
    const where: any[] = [{ field: 'flag_vendor', op: 'eq', value: true }]
    if (query && query.trim()) {
      where.push({ json: 'name', op: 'contains', value: query.trim() })
    }
    const resp = await api.post('/objects/businesspartner/search', { page: 1, pageSize: 50, where, orderBy: [] })
    const rows: any[] = resp.data?.data || []
    vendorOptions.value = rows.map((p: any) => {
      const code = p.partner_code || p.payload?.partnerCode || p.payload?.code
      const name = p.payload?.name || p.name || code
      return { label: `${name} (${code})`, value: code, name }
    })
  } finally {
    vendorLoading.value = false
  }
}

function onVendorChange(code: string) {
  const found = vendorOptions.value.find((opt: any) => opt.value === code)
  editForm.partnerName = found?.name || ''
}

// 搜索物料
async function searchMaterials(query: string) {
  materialLoading.value = true
  try {
    const where: any[] = []
    if (query && query.trim()) {
      where.push({ 
        type: 'or',
        conditions: [
          { field: 'material_code', op: 'contains', value: query.trim() },
          { json: 'name', op: 'contains', value: query.trim() }
        ]
      })
    }
    const resp = await api.post('/objects/material/search', { page: 1, pageSize: 50, where, orderBy: [] })
    const rows: any[] = resp.data?.data || []
    materialOptions.value = rows.map((m: any) => {
      const code = m.material_code || m.payload?.code
      const name = m.name || m.payload?.name || code
      const uom = m.payload?.baseUom || m.payload?.uom || ''
      const price = Number(m.payload?.price ?? 0)
      const opt = { label: `${name} (${code})`, value: code, name, uom, price }
      materialCache.set(code, opt)
      return opt
    })
  } finally {
    materialLoading.value = false
  }
}

function getMaterialOptions(row: any) {
  if (row.materialCode && !materialOptions.value.find((o: any) => o.value === row.materialCode)) {
    const cached = materialCache.get(row.materialCode)
    if (cached) return [cached, ...materialOptions.value]
  }
  return materialOptions.value
}

function onMaterialChange(code: string, row: any) {
  const material = materialCache.get(code) || materialOptions.value.find((m: any) => m.value === code)
  if (material) {
    row.materialName = material.name
    row.uom = material.uom || ''
    row.unitPrice = material.price || 0
    recalcEditLine(row)
  }
}

function recalcEditLine(row: any) {
  const qty = Number(row.quantity ?? 0) || 0
  const price = Number(row.unitPrice ?? 0) || 0
  const taxRate = Number(row.taxRate ?? 10) || 10
  row.amount = Math.round(qty * price)
  row.taxAmount = Math.round(row.amount * taxRate / 100)
  recalcEditTotals()
}

function recalcEditTotals() {
  let totalAmount = 0
  let totalTax = 0
  editForm.lines.forEach((line: any, idx: number) => {
    line.lineNo = idx + 1
    totalAmount += line.amount || 0
    totalTax += line.taxAmount || 0
  })
  editForm.taxAmountTotal = totalTax
  editForm.amountTotal = totalAmount + totalTax
}

const editSubtotal = computed(() => {
  return editForm.lines.reduce((sum: number, line: any) => sum + (line.amount || 0), 0)
})

function addEditLine() {
  editForm.lines.push({
    lineNo: editForm.lines.length + 1,
    materialCode: '',
    materialName: '',
    quantity: 1,
    uom: '',
    unitPrice: 0,
    amount: 0,
    taxRate: 10,
    taxAmount: 0,
    deliveryDate: '',
    receivedQuantity: 0,
    status: 'open'
  })
}

function removeEditLine(index: number) {
  if (editForm.lines.length > 1) {
    editForm.lines.splice(index, 1)
    recalcEditTotals()
  }
}

async function confirmPO() {
  if (!detailData.value) return
  try {
    const payload = { ...detailData.value.payload, status: 'confirmed' }
    await api.put(`/objects/purchase_order/${detailData.value.id}`, { payload })
    ElMessage.success('発注を確定しました')
    showDetail.value = false
    loadData()
  } catch (e: any) {
    ElMessage.error('確定に失敗しました')
  }
}

function openReceiveDialog(row: any) {
  receiveData.value = row
  receiveLines.value = (row.payload?.lines || []).map((line: any) => ({
    ...line,
    receiveQty: Math.max(0, (line.quantity || 0) - (line.receivedQuantity || 0))
  }))
  receiveForm.movementDate = new Date().toISOString().slice(0, 10)
  receiveForm.toWarehouse = warehouses.value[0]?.warehouse_code || ''
  receiveForm.toBin = ''
  bins.value = []
  if (receiveForm.toWarehouse) {
    loadBins(receiveForm.toWarehouse)
  }
  showReceive.value = true
}

async function onWarehouseChange(warehouseCode: string) {
  receiveForm.toBin = ''
  bins.value = []
  if (warehouseCode) {
    await loadBins(warehouseCode)
  }
}

async function loadBins(warehouseCode: string) {
  binsLoading.value = true
  try {
    const resp = await api.get(`/inventory/warehouses/${warehouseCode}/bins`)
    bins.value = resp.data || []
    if (bins.value.length > 0) {
      receiveForm.toBin = bins.value[0].bin_code
    }
  } catch { /* ignore */ }
  finally {
    binsLoading.value = false
  }
}

async function doReceive() {
  if (!receiveData.value) return
  if (!receiveForm.toWarehouse) {
    ElMessage.warning('倉庫を選択してください')
    return
  }
  if (!receiveForm.toBin) {
    ElMessage.warning('棚番を選択してください')
    return
  }
  
  const linesToReceive = receiveLines.value.filter(l => l.receiveQty > 0)
  if (linesToReceive.length === 0) {
    ElMessage.warning('入庫数量を入力してください')
    return
  }

  receiving.value = true
  try {
    // 1. 创建库存移动（入库）
    const movementPayload = {
      movementType: 'IN',
      movementDate: receiveForm.movementDate,
      toWarehouse: receiveForm.toWarehouse,
      toBin: receiveForm.toBin,
      referenceNo: receiveData.value.payload?.poNo,
      referenceType: 'purchase_order',
      referenceId: receiveData.value.id,
      lines: linesToReceive.map((line, idx) => ({
        lineNo: idx + 1,
        materialCode: line.materialCode,
        quantity: line.receiveQty,
        uom: line.uom
      }))
    }
    await api.post('/inventory/movements', movementPayload)

    // 2. 更新采购订单的入库数量
    const updatedLines = receiveData.value.payload.lines.map((line: any) => {
      const receiveLine = linesToReceive.find(l => l.lineNo === line.lineNo)
      if (receiveLine) {
        const newReceivedQty = (line.receivedQuantity || 0) + receiveLine.receiveQty
        const newStatus = newReceivedQty >= line.quantity ? 'closed' : 'partial'
        return { ...line, receivedQuantity: newReceivedQty, status: newStatus }
      }
      return line
    })
    
    const allClosed = updatedLines.every((l: any) => l.status === 'closed' || l.status === 'cancelled')
    const poStatus = allClosed ? 'fully_received' : 'partial_received'
    
    const updatedPayload = {
      ...receiveData.value.payload,
      lines: updatedLines,
      status: poStatus
    }
    await api.put(`/objects/purchase_order/${receiveData.value.id}`, { payload: updatedPayload })

    ElMessage.success('入庫処理が完了しました')
    showReceive.value = false
    loadData()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '入庫処理に失敗しました')
  } finally {
    receiving.value = false
  }
}

async function deletePO(row: any) {
  try {
    await ElMessageBox.confirm('この発注を削除しますか？', '確認', { type: 'warning' })
    await api.delete(`/objects/purchase_order/${row.id}`)
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
.page.page-wide {
  max-width: 1400px;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
}

.search-bar {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.pagination-bar {
  margin-top: 16px;
  display: flex;
  justify-content: flex-end;
}

.po-detail {
  padding: 0;
}

.detail-table {
  font-size: 13px;
}

.po-totals-inline {
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

.po-totals-inline .total-item {
  color: #606266;
}

.po-totals-inline .total-item strong {
  color: #303133;
  margin-left: 4px;
}

.po-totals-inline .total-item.total-grand {
  color: #409eff;
  font-weight: 500;
}

.po-totals-inline .total-item.total-grand strong {
  color: #409eff;
  font-size: 14px;
  border-top: 2px solid #409eff;
  color: #409eff;
}

.po-note {
  margin-top: 16px;
  padding: 12px;
  background: #fdf6ec;
  border-radius: 4px;
  color: #e6a23c;
  font-size: 13px;
}

.edit-lines-toolbar {
  margin-bottom: 12px;
}

.edit-table {
  font-size: 13px;
}

.edit-table :deep(.el-input-number) {
  width: 100%;
}

.edit-table :deep(.el-input-number .el-input__inner) {
  text-align: right;
}


/* 两栏布局 */
.po-detail-layout {
  display: flex;
  gap: 20px;
}

.po-detail-main {
  flex: 1;
  min-width: 600px;
}

.po-detail-progress {
  width: 220px;
  flex-shrink: 0;
  background: linear-gradient(135deg, #f8fafc 0%, #f1f5f9 100%);
  border-radius: 8px;
  padding: 16px;
  border: 1px solid #e2e8f0;
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
  padding-bottom: 20px;
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
  word-break: break-all;
}

.timeline-date {
  font-size: 10px;
  color: #64748b;
}

.timeline-detail {
  font-size: 10px;
  color: #64748b;
}

.timeline-voucher {
  font-size: 10px;
  color: #3b82f6;
  margin-top: 2px;
}

.timeline-voucher.clickable {
  cursor: pointer;
  text-decoration: underline;
}

.timeline-voucher.clickable:hover {
  color: #2563eb;
}

.timeline-pending {
  font-size: 11px;
  color: #94a3b8;
  font-style: italic;
}

.timeline-sub-item {
  margin-bottom: 4px;
  padding: 4px 6px;
  background: white;
  border-radius: 4px;
  border: 1px solid #e2e8f0;
}

.timeline-sub-item.clickable {
  cursor: pointer;
}

.timeline-sub-item.clickable:hover {
  border-color: #3b82f6;
}

</style>

