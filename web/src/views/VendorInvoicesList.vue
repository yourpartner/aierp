<template>
  <div class="page page-wide">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">請求書一覧</div>
          <div class="page-actions">
            <el-upload
              ref="uploadRef"
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
              <el-icon><Plus /></el-icon> 新規登録
            </el-button>
          </div>
        </div>
      </template>

      <!-- 搜索栏 -->
      <div class="search-bar">
        <el-input v-model="searchQuery" placeholder="請求書番号・仕入先で検索..." clearable style="width:300px" @keyup.enter="loadData">
          <template #prefix><el-icon><Search /></el-icon></template>
        </el-input>
        <el-select v-model="statusFilter" placeholder="ステータス" clearable style="width:120px" @change="loadData">
          <el-option label="下書き" value="draft" />
          <el-option label="転記済" value="posted" />
          <el-option label="転記保留" value="pending_post" />
          <el-option label="支払済" value="paid" />
        </el-select>
        <el-date-picker v-model="dateRange" type="daterange" start-placeholder="開始日" end-placeholder="終了日" value-format="YYYY-MM-DD" @change="loadData" />
        <el-button @click="loadData"><el-icon><Refresh /></el-icon></el-button>
      </div>

      <!-- 数据表格 -->
      <el-table :data="list" v-loading="loading" border stripe @row-click="onRowClick" style="cursor:pointer">
        <el-table-column prop="invoice_no" label="請求書番号" width="160" />
        <el-table-column label="仕入先請求書番号" width="160">
          <template #default="{ row }">{{ row.payload?.vendorInvoiceNo || '-' }}</template>
        </el-table-column>
        <el-table-column label="仕入先" min-width="200">
          <template #default="{ row }">{{ row.payload?.vendorName || row.vendor_code }}</template>
        </el-table-column>
        <el-table-column prop="invoice_date" label="請求日" width="120" />
        <el-table-column label="支払期限" width="120">
          <template #default="{ row }">{{ row.payload?.dueDate || '-' }}</template>
        </el-table-column>
        <el-table-column label="金額" width="140" align="right">
          <template #default="{ row }">¥{{ formatNumber(row.grand_total) }}</template>
        </el-table-column>
        <el-table-column label="発注" width="130">
          <template #default="{ row }">{{ row.payload?.poRef || '-' }}</template>
        </el-table-column>
        <el-table-column label="PDF" width="50" align="center">
          <template #default="{ row }">
            <el-icon v-if="row.payload?.attachment" :size="16" color="#409eff"><Document /></el-icon>
          </template>
        </el-table-column>
        <el-table-column prop="status" label="ステータス" width="100">
          <template #default="{ row }">
            <el-tag :type="getStatusType(row.status)" size="small">{{ getStatusLabel(row.status) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="180" fixed="right">
          <template #default="{ row }">
            <el-button type="primary" text size="small" @click.stop="viewDetail(row)">詳細</el-button>
            <el-button v-if="row.status === 'pending_post'" type="success" text size="small" @click.stop="postInvoice(row)">転記</el-button>
            <el-button v-if="row.status !== 'paid'" type="danger" text size="small" @click.stop="deleteInvoice(row)">削除</el-button>
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

    <!-- 新建/编辑弹窗 -->
    <el-dialog
      v-model="showForm"
      width="1000px"
      destroy-on-close
      append-to-body
    >
      <template #header>
        <div class="dialog-header">
          <span class="dialog-header-title">{{ editingId ? '請求書編集' : '請求書登録' }}</span>
          <div class="dialog-header-actions">
            <el-upload
              v-if="!editingId"
              ref="formUploadRef"
              :show-file-list="false"
              :auto-upload="false"
              :on-change="onFileSelected"
              accept=".pdf,.png,.jpg,.jpeg,.gif,.webp"
              style="display:inline-block"
            >
              <el-button size="small" :loading="recognizing">
                <el-icon><Upload /></el-icon> AI識別
              </el-button>
            </el-upload>
            <el-button type="primary" size="small" :loading="formRef?.saving" @click="formRef?.save()">保存</el-button>
            <el-button size="small" @click="showForm = false">キャンセル</el-button>
          </div>
        </div>
      </template>
      <VendorInvoiceForm
        ref="formRef"
        :dialog-mode="true"
        :edit-id="editingId"
        :initial-data="recognizedData"
        @saved="onFormSaved"
        @cancel="showForm = false"
      />
    </el-dialog>

    <!-- 详情弹窗 -->
    <el-dialog v-model="showDetail" width="900px" destroy-on-close append-to-body>
      <template #header>
        <div class="dialog-header">
          <span class="dialog-header-title">請求書詳細</span>
          <div class="dialog-header-actions">
            <el-button v-if="detailAttachment" size="small" @click="downloadAttachment">
              <el-icon><Download /></el-icon> PDF
            </el-button>
            <el-button v-if="detailData && canEditInvoice(detailData)" type="primary" size="small" @click="editInvoice(detailData)">編集</el-button>
            <el-button size="small" @click="showDetail = false">閉じる</el-button>
          </div>
        </div>
      </template>
      <div v-if="detailData">
        <el-descriptions :column="3" border size="small">
          <el-descriptions-item label="請求書番号">{{ detailData.invoice_no }}</el-descriptions-item>
          <el-descriptions-item label="仕入先請求書番号">{{ detailData.payload?.vendorInvoiceNo || '-' }}</el-descriptions-item>
          <el-descriptions-item label="ステータス">
            <el-tag :type="getStatusType(detailData.status)" size="small">{{ getStatusLabel(detailData.status) }}</el-tag>
          </el-descriptions-item>
          <el-descriptions-item label="請求日">{{ detailData.payload?.invoiceDate }}</el-descriptions-item>
          <el-descriptions-item label="支払期限">{{ detailData.payload?.dueDate || '-' }}</el-descriptions-item>
          <el-descriptions-item label="仕入先">{{ detailData.payload?.vendorName }} ({{ detailData.vendor_code }})</el-descriptions-item>
          <el-descriptions-item label="発注番号">
            <span v-if="detailData.payload?.poRef" class="voucher-link" @click="navigateToPo(detailData.payload.poRef)">{{ detailData.payload.poRef }}</span>
            <span v-else class="text-muted">-</span>
          </el-descriptions-item>
          <el-descriptions-item label="会計伝票番号">
            <span
              v-if="detailData.payload?.voucherNo"
              class="voucher-link"
              @click="openVoucherDetail(detailData.payload.voucherId, detailData.payload.voucherNo)"
            >{{ detailData.payload.voucherNo }}</span>
            <span v-else class="text-muted">-</span>
          </el-descriptions-item>
          <el-descriptions-item label="作成日">{{ formatDateTime(detailData.created_at) }}</el-descriptions-item>
        </el-descriptions>

        <!-- PDF プレビュー -->
        <div v-if="detailAttachment" class="attachment-section">
          <el-divider content-position="left">添付ファイル</el-divider>
          <div class="attachment-item">
            <el-icon><Document /></el-icon>
            <a class="attachment-link" @click="downloadAttachment">{{ detailAttachment.fileName || 'invoice.pdf' }}</a>
            <span class="attachment-size" v-if="detailAttachment.size">（{{ formatFileSize(detailAttachment.size) }}）</span>
            <el-button size="small" text type="primary" @click="previewAttachment">プレビュー</el-button>
          </div>
        </div>

        <el-divider content-position="left">明細</el-divider>
        <el-table :data="detailData.payload?.lines || []" border size="small">
          <el-table-column label="#" width="40" align="center">
            <template #default="{ $index }">{{ $index + 1 }}</template>
          </el-table-column>
          <el-table-column label="品目" min-width="180">
            <template #default="{ row }">{{ row.materialName || row.description || row.materialCode || '-' }}</template>
          </el-table-column>
          <el-table-column label="数量" width="80" align="right">
            <template #default="{ row }">{{ row.quantity || '-' }}</template>
          </el-table-column>
          <el-table-column label="単価" width="100" align="right">
            <template #default="{ row }">{{ row.unitPrice ? formatNumber(row.unitPrice) : '-' }}</template>
          </el-table-column>
          <el-table-column label="金額" width="110" align="right">
            <template #default="{ row }">{{ formatNumber(row.amount) }}</template>
          </el-table-column>
          <el-table-column label="税率" width="60" align="center">
            <template #default="{ row }">{{ row.taxRate != null ? row.taxRate + '%' : '-' }}</template>
          </el-table-column>
          <el-table-column label="発注番号" width="140">
            <template #default="{ row }">
              <span v-if="row.matchedPoNo" class="voucher-link" @click="navigateToPo(row.matchedPoNo)">{{ row.matchedPoNo }}</span>
              <span v-else class="text-muted">-</span>
            </template>
          </el-table-column>
        </el-table>

        <div class="totals-inline">
          <span class="total-item">税抜合計: <strong>¥{{ formatNumber(detailData.payload?.subtotal) }}</strong></span>
          <span class="total-item">消費税: <strong>¥{{ formatNumber(detailData.payload?.taxTotal || detailData.payload?.taxAmount) }}</strong></span>
          <span class="total-item total-grand">合計（税込）: <strong>¥{{ formatNumber(detailData.payload?.grandTotal) }}</strong></span>
        </div>

        <div v-if="detailData.payload?.memo" class="memo-section">
          <strong>備考:</strong> {{ detailData.payload.memo }}
        </div>
      </div>
    </el-dialog>

    <!-- PDF プレビュー弹窗 -->
    <el-dialog v-model="showPdfPreview" width="80%" destroy-on-close append-to-body title="PDF プレビュー">
      <iframe v-if="pdfPreviewUrl" :src="pdfPreviewUrl" style="width:100%; height:75vh; border:none;" />
    </el-dialog>

    <!-- 会计凭证弹窗 -->
    <el-dialog 
      v-model="showVoucher" 
      width="auto" 
      destroy-on-close
      append-to-body
      class="voucher-detail-dialog"
    >
      <template #header></template>
      <!-- 统一复用 VouchersList 的标准详情弹窗样式：不在此处包 wrapper / 不传 embed class -->
      <div>
        <VouchersList
          v-if="showVoucher && (currentVoucherId || currentVoucherNo)"
          :allow-edit="false"
          :initial-voucher-id="currentVoucherId || undefined"
          :initial-voucher-no="currentVoucherNo || undefined"
        />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { Plus, Search, Refresh, Upload, Download, Document } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '../api'
import VendorInvoiceForm from './VendorInvoiceForm.vue'
import VouchersList from './VouchersList.vue'

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

// 新建/编辑弹窗
const showForm = ref(false)
const editingId = ref<string>('')
const formRef = ref<InstanceType<typeof VendorInvoiceForm> | null>(null)

// AI識別
const recognizing = ref(false)
const uploadRef = ref<any>(null)
const formUploadRef = ref<any>(null)
const recognizedData = ref<any>(null)

// 会计凭证弹窗
const showVoucher = ref(false)
const currentVoucherId = ref<string>('')
const currentVoucherNo = ref<string>('')

// PDF プレビュー
const showPdfPreview = ref(false)
const pdfPreviewUrl = ref<string>('')

function formatNumber(value: number | undefined | null) {
  if (value === undefined || value === null) return '-'
  return new Intl.NumberFormat('ja-JP').format(Math.round(value))
}

function formatDateTime(value: string | undefined | null) {
  if (!value) return '-'
  try {
    const date = new Date(value)
    return date.toLocaleString('ja-JP', { 
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit'
    })
  } catch {
    return value
  }
}

function getStatusType(status: string) {
  const map: Record<string, string> = {
    draft: 'info',
    posted: 'success',
    pending_post: 'warning',
    paid: '',
    approved: ''
  }
  return map[status] || 'info'
}

function getStatusLabel(status: string) {
  const map: Record<string, string> = {
    draft: '下書き',
    posted: '転記済',
    pending_post: '転記保留',
    paid: '支払済',
    approved: '承認済',
    pending: '未処理'
  }
  return map[status] || status
}

async function loadData() {
  loading.value = true
  try {
    const where: any[] = []
    if (searchQuery.value) {
      where.push({
        type: 'or',
        conditions: [
          { field: 'invoice_no', op: 'contains', value: searchQuery.value },
          { field: 'vendor_code', op: 'contains', value: searchQuery.value },
          { json: 'vendorName', op: 'contains', value: searchQuery.value },
          { json: 'vendorInvoiceNo', op: 'contains', value: searchQuery.value }
        ]
      })
    }
    if (statusFilter.value) {
      where.push({ field: 'status', op: 'eq', value: statusFilter.value })
    }
    if (dateRange.value && dateRange.value[0] && dateRange.value[1]) {
      where.push({ field: 'invoice_date', op: 'gte', value: dateRange.value[0] })
      where.push({ field: 'invoice_date', op: 'lte', value: dateRange.value[1] })
    }
    
    const resp = await api.post('/objects/vendor_invoice/search', {
      page: page.value,
      pageSize: pageSize.value,
      where,
      orderBy: [{ field: 'invoice_date', dir: 'desc' }]
    })
    list.value = resp.data?.data || []
    total.value = resp.data?.total || 0
  } catch (e: any) {
    ElMessage.error('データの読み込みに失敗しました')
  } finally {
    loading.value = false
  }
}

function onRowClick(row: any) {
  viewDetail(row)
}

async function viewDetail(row: any) {
  // 重新加载最新数据
  try {
    const resp = await api.get(`/objects/vendor_invoice/${row.id}`)
    // API返回的是payload内容，需要合并到完整对象
    detailData.value = {
      ...row,
      payload: resp.data
    }
    showDetail.value = true
  } catch {
    detailData.value = row
    showDetail.value = true
  }
}

function openCreateDialog() {
  editingId.value = ''
  recognizedData.value = null
  showForm.value = true
}

function canEditInvoice(row: any): boolean {
  return row?.status === 'draft' || row?.status === 'pending_post'
}

function editInvoice(row: any) {
  showDetail.value = false
  editingId.value = row.id
  showForm.value = true
}

async function onFormSaved(data: { id: string }) {
  showForm.value = false
  recognizedData.value = null
  await loadData()
  
  // 显示详情弹窗
  if (data?.id) {
    try {
      // 从列表中找到对应的行数据
      const row = list.value.find((r: any) => r.id === data.id)
      const resp = await api.get(`/objects/vendor_invoice/${data.id}`)
      // 合并行数据和 payload
      detailData.value = {
        ...(row || {}),
        id: data.id,
        invoice_no: resp.data?.invoiceNo || row?.invoice_no,
        vendor_code: resp.data?.vendorCode || row?.vendor_code,
        status: resp.data?.status || row?.status || 'draft',
        payload: resp.data
      }
      showDetail.value = true
    } catch { /* ignore */ }
  }
}

async function postInvoice(row: any) {
  try {
    await ElMessageBox.confirm('この請求書を転記しますか？会計仕訳が作成されます。', '確認', { type: 'warning' })
    
    // 调用転記API
    const resp = await api.post(`/vendor-invoice/${row.id}/post`)
    if (resp.data?.voucherNo) {
      ElMessage.success(`転記しました（伝票番号: ${resp.data.voucherNo}）`)
    } else {
      ElMessage.success('転記しました')
    }
    loadData()
  } catch (e: any) {
    if (e !== 'cancel' && e?.message !== 'cancel') {
      const errMsg = e?.response?.data?.error || e?.message || '転記に失敗しました'
      ElMessage.error(errMsg)
    }
  }
}

async function deleteInvoice(row: any) {
  try {
    await ElMessageBox.confirm('この請求書を削除しますか？', '確認', { type: 'warning' })
    await api.delete(`/objects/vendor_invoice/${row.id}`)
    ElMessage.success('削除しました')
    loadData()
  } catch { /* cancelled */ }
}

function openVoucherDetail(voucherId?: string, voucherNo?: string) {
  if (!voucherId && !voucherNo) return
  currentVoucherId.value = voucherId || ''
  currentVoucherNo.value = voucherNo || ''
  showVoucher.value = true
}

async function onFileSelected(uploadFile: any) {
  if (!uploadFile?.raw) return
  recognizing.value = true
  try {
    const formData = new FormData()
    formData.append('file', uploadFile.raw)
    const resp = await api.post('/vendor-invoice/recognize', formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
      timeout: 120000
    })
    if (resp.data?.recognized && resp.data?.data) {
      // 将附件信息合并到识别数据中
      const aiData = resp.data.data
      if (resp.data.attachment) {
        aiData._attachment = resp.data.attachment
      }
      recognizedData.value = aiData
      ElMessage.success('AI識別完了。フォームに反映します。')
      // Open create dialog with pre-filled data
      editingId.value = ''
      showForm.value = true
    } else {
      ElMessage.warning('識別結果が取得できませんでした')
    }
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.detail || e?.message || 'AI識別に失敗しました')
  } finally {
    recognizing.value = false
    uploadRef.value?.clearFiles()
  }
}

// 添付ファイル関連
const detailAttachment = computed(() => {
  return detailData.value?.payload?.attachment || detailData.value?.payload?._attachment || null
})

function formatFileSize(bytes: number) {
  if (bytes < 1024) return bytes + ' B'
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
  return (bytes / (1024 * 1024)).toFixed(1) + ' MB'
}

async function downloadAttachment() {
  const att = detailAttachment.value
  if (!att) return
  if (att.url) {
    window.open(att.url, '_blank')
  } else if (att.blobName) {
    try {
      const resp = await api.get(`/blob/download-url?name=${encodeURIComponent(att.blobName)}`)
      if (resp.data?.url) window.open(resp.data.url, '_blank')
    } catch {
      ElMessage.error('ダウンロードURLの取得に失敗しました')
    }
  }
}

async function previewAttachment() {
  const att = detailAttachment.value
  if (!att) return
  let url = att.url
  if (!url && att.blobName) {
    try {
      const resp = await api.get(`/blob/download-url?name=${encodeURIComponent(att.blobName)}`)
      url = resp.data?.url
    } catch {
      ElMessage.error('プレビューURLの取得に失敗しました')
      return
    }
  }
  if (url) {
    pdfPreviewUrl.value = url
    showPdfPreview.value = true
  }
}

function navigateToPo(poNo: string) {
  if (!poNo) return
  // 目前直接提示，后续可跳转到PO详情
  ElMessage.info(`発注番号: ${poNo}`)
}

onMounted(() => {
  loadData()
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

.dialog-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding-right: 32px;
}
.dialog-header-title {
  font-size: 16px;
  font-weight: 600;
}
.dialog-header-actions {
  display: flex;
  gap: 8px;
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

.totals-inline {
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

.total-item {
  color: #606266;
}

.total-item strong {
  color: #303133;
  margin-left: 4px;
}

.total-item.total-grand {
  color: #409eff;
  font-weight: 500;
}

.total-item.total-grand strong {
  color: #409eff;
  font-size: 14px;
}

.memo-section {
  margin-top: 16px;
  padding: 12px;
  background: #fdf6ec;
  border-radius: 4px;
  color: #e6a23c;
  font-size: 13px;
}

.text-muted {
  color: #909399;
}

.attachment-section {
  margin-top: 4px;
}

.attachment-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: #f5f7fa;
  border-radius: 4px;
  font-size: 13px;
}

.attachment-link {
  color: #409eff;
  cursor: pointer;
  text-decoration: underline;
}

.attachment-link:hover {
  color: #66b1ff;
}

.attachment-size {
  color: #909399;
  font-size: 12px;
}

.voucher-link {
  color: #409eff;
  cursor: pointer;
  text-decoration: underline;
}

.voucher-link:hover {
  color: #66b1ff;
}

.voucher-dialog-card-wrap {
  min-width: 600px;
  max-width: 90vw;
  max-height: 80vh;
  overflow: auto;
}

.voucher-detail-embed {
  padding: 0;
}

:deep(.voucher-detail-embed .el-card) {
  box-shadow: none;
  border: none;
}

:deep(.voucher-detail-embed .el-card__header) {
  padding: 12px 16px;
  border-bottom: 1px solid #e4e7ed;
}

:deep(.voucher-detail-embed .el-card__body) {
  padding: 16px;
}
</style>
