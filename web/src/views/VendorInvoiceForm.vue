<template>
  <div :class="['page', 'page-wide', { 'dialog-mode': dialogMode }]">
    <el-card :shadow="dialogMode ? 'never' : 'always'" :style="dialogMode ? { border: 'none' } : {}">
      <template #header v-if="!dialogMode">
        <div class="page-header">
          <div class="page-header-title">{{ isEdit ? '請求書編集' : '請求書登録' }}</div>
          <div class="page-actions">
            <el-button @click="onCancel">キャンセル</el-button>
            <el-button type="primary" @click="save" :loading="saving">保存</el-button>
          </div>
        </div>
      </template>

      <el-alert v-if="err" type="error" :title="err" show-icon :closable="false" style="margin-bottom:16px" />

      <!-- 基本信息 -->
      <el-form label-position="left" label-width="100px" class="form-section">
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item label="仕入先" required>
              <el-select 
                v-model="form.vendorCode" 
                filterable 
                remote 
                reserve-keyword 
                :remote-method="searchVendors" 
                :loading="vendorLoading" 
                placeholder="仕入先を検索..." 
                style="width:100%"
                @change="onVendorChange"
              >
                <el-option v-for="v in vendorOptions" :key="v.code" :label="v.label" :value="v.code" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="6">
            <el-form-item label="請求日" required>
              <el-date-picker 
                v-model="form.invoiceDate" 
                type="date" 
                value-format="YYYY-MM-DD" 
                style="width:100%"
                @change="onInvoiceDateChange"
              />
            </el-form-item>
          </el-col>
          <el-col :span="6">
            <el-form-item label="支払期限" required>
              <el-date-picker v-model="form.dueDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="5">
            <el-form-item label="請求数量" required>
              <el-input-number 
                v-model="form.invoiceQuantity" 
                :min="0" 
                :max="99999999"
                :controls="false" 
                style="width:100%" 
                @change="autoMatchReceipts"
              />
            </el-form-item>
          </el-col>
          <el-col :span="5">
            <el-form-item label="請求金額" required>
              <el-input 
                v-model="formattedInvoiceAmount" 
                style="width:100%"
                @blur="onAmountBlur"
              >
                <template #prefix>¥</template>
              </el-input>
            </el-form-item>
          </el-col>
          <el-col :span="4">
            <el-form-item label="通貨">
              <el-select v-model="form.currency" style="width:100%">
                <el-option label="JPY" value="JPY" />
                <el-option label="USD" value="USD" />
                <el-option label="CNY" value="CNY" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="10">
            <el-form-item label="仕入先請求書番号" label-width="130px" class="nowrap-label">
              <el-input v-model="form.vendorInvoiceNo" placeholder="任意" />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>

      <!-- 入庫明細マッチング -->
      <el-divider content-position="left">入庫明細（{{ form.invoiceDate || '請求日' }}以前の未請求分）</el-divider>
      
      <div v-if="!form.vendorCode" class="empty-hint">
        <el-icon><Warning /></el-icon>
        仕入先を選択してください
      </div>
      
      <div v-else-if="loadingReceipts" class="loading-hint">
        <el-icon class="is-loading"><Loading /></el-icon>
        入庫明細を読み込み中...
      </div>

      <div v-else-if="availableReceipts.length === 0" class="empty-hint">
        <el-icon><InfoFilled /></el-icon>
        該当する入庫明細がありません
      </div>

      <el-table 
        v-else
        ref="receiptTableRef"
        :data="availableReceipts" 
        border 
        size="small" 
        class="receipt-table"
        @selection-change="onReceiptSelectionChange"
      >
        <el-table-column type="selection" width="50" />
        <el-table-column label="入庫日" width="110">
          <template #default="{ row }">{{ row.receiptDate }}</template>
        </el-table-column>
        <el-table-column label="発注番号" width="120">
          <template #default="{ row }">{{ row.poNo }}</template>
        </el-table-column>
        <el-table-column label="品目" min-width="200">
          <template #default="{ row }">
            <span class="material-name">{{ row.materialName }}</span>
            <span class="material-code">({{ row.materialCode }})</span>
          </template>
        </el-table-column>
        <el-table-column label="入庫数量" width="110" align="right">
          <template #default="{ row }">{{ row.quantity }} {{ row.uom || '' }}</template>
        </el-table-column>
        <el-table-column label="未請求数量" width="120" align="right">
          <template #default="{ row }">
            <span :class="{ 'highlight': row.uninvoicedQuantity > 0 }">{{ row.uninvoicedQuantity }} {{ row.uom || '' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="単価" width="100" align="right">
          <template #default="{ row }">¥{{ formatNumber(row.unitPrice) }}</template>
        </el-table-column>
        <el-table-column label="未請求金額" width="120" align="right">
          <template #default="{ row }">
            <span :class="{ 'highlight': row.uninvoicedAmount > 0 }">¥{{ formatNumber(row.uninvoicedAmount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="倉庫" width="100">
          <template #default="{ row }">{{ row.warehouseName || row.warehouseCode }}</template>
        </el-table-column>
      </el-table>

      <!-- 对比汇总 -->
      <div class="comparison-section" v-if="form.vendorCode && availableReceipts.length > 0">
        <div class="comparison-row">
          <div class="comparison-item">
            <span class="comp-label">請求数量:</span>
            <span class="comp-value">{{ formatNumber(form.invoiceQuantity || 0) }}</span>
          </div>
          <div class="comparison-item">
            <span class="comp-label">選択入庫数量:</span>
            <span class="comp-value" :class="quantityMatchClass">{{ formatNumber(selectedTotalQuantity) }}</span>
          </div>
          <div class="comparison-item diff" v-if="quantityDiff !== 0">
            <span class="comp-label">差異:</span>
            <span class="comp-value" :class="quantityDiff > 0 ? 'positive' : 'negative'">
              {{ quantityDiff > 0 ? '+' : '' }}{{ formatNumber(quantityDiff) }}
            </span>
          </div>
          <div class="match-status-inline" v-if="form.invoiceQuantity && form.invoiceAmount">
            <el-tag v-if="isFullMatch" type="success" size="small">一致</el-tag>
            <el-tag v-else-if="selectedReceipts.length > 0" type="warning" size="small">差異</el-tag>
            <el-tag v-else type="info" size="small">未選択</el-tag>
          </div>
        </div>
        <div class="comparison-row">
          <div class="comparison-item">
            <span class="comp-label">請求金額:</span>
            <span class="comp-value">¥{{ formatNumber(form.invoiceAmount || 0) }}</span>
          </div>
          <div class="comparison-item">
            <span class="comp-label">選択入庫金額:</span>
            <span class="comp-value" :class="amountMatchClass">¥{{ formatNumber(selectedTotalAmount) }}</span>
          </div>
          <div class="comparison-item diff" v-if="amountDiff !== 0">
            <span class="comp-label">差異:</span>
            <span class="comp-value" :class="amountDiff > 0 ? 'positive' : 'negative'">
              {{ amountDiff > 0 ? '+' : '' }}¥{{ formatNumber(amountDiff) }}
            </span>
          </div>
        </div>
      </div>

      <!-- 备注 -->
      <el-form label-width="140px" style="margin-top:20px">
        <el-form-item label="備考">
          <el-input v-model="form.memo" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>
    </el-card>

    <!-- 科目选择弹窗 -->
    <el-dialog v-model="showAccountSelection" title="勘定科目の選択" width="600px">
      <el-form label-width="140px">
        <el-form-item v-if="accountSelection.drAccount?.required" label="借方科目（在庫）" required>
          <el-select v-model="selectedDrAccount" style="width:100%" placeholder="科目を選択...">
            <el-option 
              v-for="opt in accountSelection.drAccount?.options || []" 
              :key="opt.code" 
              :label="`${opt.code} ${opt.name}`" 
              :value="opt.code" 
            />
          </el-select>
          <div class="form-hint" v-if="accountSelection.drAccount?.message">{{ accountSelection.drAccount.message }}</div>
        </el-form-item>
        <el-form-item v-if="accountSelection.crAccount?.required" label="貸方科目（買掛金）" required>
          <el-select v-model="selectedCrAccount" style="width:100%" placeholder="科目を選択...">
            <el-option 
              v-for="opt in accountSelection.crAccount?.options || []" 
              :key="opt.code" 
              :label="`${opt.code} ${opt.name}`" 
              :value="opt.code" 
            />
          </el-select>
          <div class="form-hint" v-if="accountSelection.crAccount?.message">{{ accountSelection.crAccount.message }}</div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showAccountSelection = false">キャンセル</el-button>
        <el-button type="primary" @click="confirmAccountSelection" :loading="saving">確定して保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, watch, nextTick } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Warning, InfoFilled, Loading } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import api from '../api'

const props = defineProps<{
  dialogMode?: boolean
  editId?: string
}>()

const emit = defineEmits<{
  (e: 'saved', data: { id: string }): void
  (e: 'cancel'): void
}>()

const route = useRoute()
const router = useRouter()

// 判断是否编辑模式
const isEdit = computed(() => !!(props.editId || route.params.id))
const currentId = computed(() => props.editId || (route.params.id as string))

// 科目选择
const showAccountSelection = ref(false)
const accountSelection = ref<any>({})
const selectedDrAccount = ref('')
const selectedCrAccount = ref('')
const pendingPayload = ref<any>(null)

const saving = ref(false)
const loadingReceipts = ref(false)
const err = ref('')

const receiptTableRef = ref<any>(null)

// 表单数据
const form = reactive({
  vendorInvoiceNo: '',  // 供应商请求书编号（非必输）
  invoiceDate: new Date().toISOString().slice(0, 10),
  dueDate: '',
  vendorCode: '',
  vendorName: '',
  currency: 'JPY',
  invoiceQuantity: 0,
  invoiceAmount: 0,
  memo: ''
})

// 供应商搜索
const vendorOptions = ref<any[]>([])
const vendorLoading = ref(false)

// 可匹配的入库记录
const availableReceipts = ref<any[]>([])
const selectedReceipts = ref<any[]>([])

// 计算属性
const selectedTotalQuantity = computed(() => 
  selectedReceipts.value.reduce((sum, r) => sum + (r.uninvoicedQuantity || 0), 0)
)

const selectedTotalAmount = computed(() => 
  selectedReceipts.value.reduce((sum, r) => sum + (r.uninvoicedAmount || 0), 0)
)

const quantityDiff = computed(() => (form.invoiceQuantity || 0) - selectedTotalQuantity.value)
const amountDiff = computed(() => (form.invoiceAmount || 0) - selectedTotalAmount.value)

const isFullMatch = computed(() => 
  selectedReceipts.value.length > 0 && 
  Math.abs(quantityDiff.value) < 0.01 && 
  Math.abs(amountDiff.value) < 1
)

const quantityMatchClass = computed(() => ({
  'match': quantityDiff.value === 0,
  'mismatch': quantityDiff.value !== 0
}))

const amountMatchClass = computed(() => ({
  'match': Math.abs(amountDiff.value) < 1,
  'mismatch': Math.abs(amountDiff.value) >= 1
}))

// 请求金额格式化显示（千分符）
const formattedInvoiceAmount = ref('')

// 监听 form.invoiceAmount 变化，更新格式化显示
watch(() => form.invoiceAmount, (val) => {
  if (val !== undefined && val !== null && val > 0) {
    formattedInvoiceAmount.value = new Intl.NumberFormat('ja-JP').format(Math.round(val))
  } else {
    formattedInvoiceAmount.value = ''
  }
}, { immediate: true })

// 金额输入失焦时解析数值
function onAmountBlur() {
  const raw = formattedInvoiceAmount.value.replace(/[,，\s]/g, '')
  const num = parseFloat(raw)
  if (!isNaN(num) && num >= 0) {
    form.invoiceAmount = Math.round(num)
    formattedInvoiceAmount.value = new Intl.NumberFormat('ja-JP').format(form.invoiceAmount)
  } else if (raw === '') {
    form.invoiceAmount = 0
    formattedInvoiceAmount.value = ''
  }
  autoMatchReceipts()
}

function formatNumber(value: number | undefined | null) {
  if (value === undefined || value === null) return '-'
  return new Intl.NumberFormat('ja-JP').format(Math.round(value))
}

// 搜索供应商
async function searchVendors(query: string) {
  vendorLoading.value = true
  try {
    const where: any[] = [{ field: 'flag_vendor', op: 'eq', value: true }]
    if (query && query.trim()) {
      where.push({
        type: 'or',
        conditions: [
          { json: 'name', op: 'contains', value: query.trim() },
          { field: 'partner_code', op: 'contains', value: query.trim() }
        ]
      })
    }
    const resp = await api.post('/objects/businesspartner/search', { page: 1, pageSize: 50, where, orderBy: [] })
    vendorOptions.value = (resp.data?.data || []).map((p: any) => {
      const code = p.partner_code || p.payload?.partnerCode
      const name = p.payload?.name || p.name || code
      const paymentTerms = p.payload?.paymentTerms
      return { code, name, label: `${name} (${code})`, paymentTerms }
    })
  } finally {
    vendorLoading.value = false
  }
}

// 根据支付条件计算支付期限
function calculateDueDate(invoiceDate: string, paymentTerms: any): string {
  if (!invoiceDate || !paymentTerms) return ''
  
  const cutOffDay = paymentTerms.cutOffDay || 31
  const paymentMonth = paymentTerms.paymentMonth || 1  // 1=翌月
  const paymentDay = paymentTerms.paymentDay || 31     // 31=月末
  
  const invoiceDt = new Date(invoiceDate)
  const day = invoiceDt.getDate()
  
  // 计算基准月份：如果请求日在截止日之后，则从下个月开始计算
  let baseMonth = invoiceDt.getMonth()
  let baseYear = invoiceDt.getFullYear()
  
  if (day > cutOffDay) {
    baseMonth += 1
    if (baseMonth > 11) {
      baseMonth = 0
      baseYear += 1
    }
  }
  
  // 加上支付月份
  let dueMonth = baseMonth + paymentMonth
  let dueYear = baseYear
  while (dueMonth > 11) {
    dueMonth -= 12
    dueYear += 1
  }
  
  // 确定支付日
  let dueDay = paymentDay
  // 获取该月的最后一天
  const lastDayOfMonth = new Date(dueYear, dueMonth + 1, 0).getDate()
  if (dueDay > lastDayOfMonth) {
    dueDay = lastDayOfMonth
  }
  
  const dueDate = new Date(dueYear, dueMonth, dueDay)
  return dueDate.toISOString().slice(0, 10)
}

async function onVendorChange(code: string) {
  const vendor = vendorOptions.value.find(v => v.code === code)
  form.vendorName = vendor?.name || ''
  
  // 根据支付条件自动计算支付期限
  if (vendor?.paymentTerms && form.invoiceDate) {
    const calculatedDueDate = calculateDueDate(form.invoiceDate, vendor.paymentTerms)
    if (calculatedDueDate) {
      form.dueDate = calculatedDueDate
    }
  }
  
  // 重新加载入库明细
  await loadAvailableReceipts()
}

async function onInvoiceDateChange() {
  // 重新计算支付期限
  if (form.vendorCode && form.invoiceDate) {
    const vendor = vendorOptions.value.find(v => v.code === form.vendorCode)
    if (vendor?.paymentTerms) {
      const calculatedDueDate = calculateDueDate(form.invoiceDate, vendor.paymentTerms)
      if (calculatedDueDate) {
        form.dueDate = calculatedDueDate
      }
    }
  }
  
  if (form.vendorCode) {
    await loadAvailableReceipts()
  }
}

// 加载可匹配的入库明细
async function loadAvailableReceipts() {
  if (!form.vendorCode) {
    availableReceipts.value = []
    return
  }
  
  loadingReceipts.value = true
  try {
    const resp = await api.post('/vendor-invoice/available-receipts', {
      vendorCode: form.vendorCode,
      beforeDate: form.invoiceDate
    })
    availableReceipts.value = resp.data?.receipts || []
    
    // 清空选择
    selectedReceipts.value = []
    
    // 自动匹配
    if (form.invoiceQuantity > 0 || form.invoiceAmount > 0) {
      await nextTick()
      autoMatchReceipts()
    }
  } catch (e: any) {
    console.error('Failed to load receipts', e)
    availableReceipts.value = []
  } finally {
    loadingReceipts.value = false
  }
}

// 自动匹配入库记录
function autoMatchReceipts() {
  if (!receiptTableRef.value || availableReceipts.value.length === 0) return
  
  // 清空当前选择
  receiptTableRef.value.clearSelection()
  
  const targetQuantity = form.invoiceQuantity || 0
  const targetAmount = form.invoiceAmount || 0
  
  if (targetQuantity <= 0 && targetAmount <= 0) return
  
  // 按日期先进先出排序（已经是排好序的）
  let accumulatedQty = 0
  let accumulatedAmt = 0
  
  // 优先按数量匹配，如果数量不够再按金额匹配
  for (const receipt of availableReceipts.value) {
    // 如果已经达到目标，停止
    if (targetQuantity > 0 && accumulatedQty >= targetQuantity) break
    if (targetQuantity <= 0 && targetAmount > 0 && accumulatedAmt >= targetAmount) break
    
    // 选中这条
    receiptTableRef.value.toggleRowSelection(receipt, true)
    accumulatedQty += receipt.uninvoicedQuantity || 0
    accumulatedAmt += receipt.uninvoicedAmount || 0
  }
}

function onReceiptSelectionChange(selection: any[]) {
  selectedReceipts.value = selection
}

function onCancel() {
  if (props.dialogMode) {
    emit('cancel')
  } else {
    router.back()
  }
}

// 保存
async function save() {
  if (!form.vendorCode) {
    err.value = '仕入先を選択してください'
    return
  }
  if (!form.invoiceDate) {
    err.value = '請求日を選択してください'
    return
  }
  if (!form.dueDate) {
    err.value = '支払期限を選択してください'
    return
  }
  if (!form.invoiceQuantity || form.invoiceQuantity <= 0) {
    err.value = '請求数量を入力してください'
    return
  }
  if (!form.invoiceAmount || form.invoiceAmount <= 0) {
    err.value = '請求金額を入力してください'
    return
  }

  err.value = ''
  saving.value = true
  
  // 构建明细（从选中的入库记录生成）- 移到 try 外面以便 catch 中访问
  const lines = selectedReceipts.value.map((r, idx) => ({
    lineNo: idx + 1,
    materialCode: r.materialCode,
    materialName: r.materialName,
    quantity: r.uninvoicedQuantity,
    unitPrice: r.unitPrice,
    amount: r.uninvoicedAmount,
    taxRate: r.taxRate || 10,
    taxAmount: Math.round((r.uninvoicedAmount || 0) * (r.taxRate || 10) / 100),
    matchedReceiptId: r.id,
    matchedPoNo: r.poNo,
    matchedReceiptDate: r.receiptDate
  }))

  const subtotal = lines.reduce((sum, l) => sum + (l.amount || 0), 0)
  const taxTotal = lines.reduce((sum, l) => sum + (l.taxAmount || 0), 0)

  const payload: any = {
    vendorInvoiceNo: form.vendorInvoiceNo,
    invoiceDate: form.invoiceDate,
    dueDate: form.dueDate,
    vendorCode: form.vendorCode,
    vendorName: form.vendorName,
    currency: form.currency,
    invoiceQuantity: form.invoiceQuantity,
    invoiceAmount: form.invoiceAmount,
    subtotal,
    taxTotal,
    grandTotal: subtotal + taxTotal,
    memo: form.memo,
    lines
  }

  // 如果已选择科目，添加到 payload
  if (selectedDrAccount.value) payload.selectedDrAccount = selectedDrAccount.value
  if (selectedCrAccount.value) payload.selectedCrAccount = selectedCrAccount.value

  try {
    let savedId: string
    if (isEdit.value && currentId.value) {
      await api.put(`/objects/vendor_invoice/${currentId.value}`, { payload })
      savedId = currentId.value
    } else {
      const resp = await api.post('/objects/vendor_invoice', { payload })
      savedId = resp.data?.id
    }

    ElMessage.success('保存しました')
    
    // 清除选择状态
    selectedDrAccount.value = ''
    selectedCrAccount.value = ''
    pendingPayload.value = null
    
    if (props.dialogMode) {
      emit('saved', { id: savedId })
    } else {
      router.push('/vendor-invoices')
    }
  } catch (e: any) {
    const errData = e?.response?.data
    
    // 检查是否需要科目选择
    if (errData?.error === 'account_selection_required' && errData?.needsAccountSelection) {
      accountSelection.value = errData.selection || {}
      
      // 设置默认选择（如果有的话）
      if (accountSelection.value.drAccount?.options?.length === 1) {
        selectedDrAccount.value = accountSelection.value.drAccount.options[0].code
      }
      if (accountSelection.value.crAccount?.options?.length === 1) {
        selectedCrAccount.value = accountSelection.value.crAccount.options[0].code
      }
      
      // 保存待处理的 payload
      pendingPayload.value = payload
      showAccountSelection.value = true
      err.value = ''
    } else {
      err.value = errData?.error || '保存に失敗しました'
    }
  } finally {
    saving.value = false
  }
}

// 确认科目选择后保存
async function confirmAccountSelection() {
  if (!pendingPayload.value) return
  
  // 验证选择
  if (accountSelection.value.drAccount?.required && !selectedDrAccount.value) {
    ElMessage.warning('借方科目を選択してください')
    return
  }
  if (accountSelection.value.crAccount?.required && !selectedCrAccount.value) {
    ElMessage.warning('貸方科目を選択してください')
    return
  }
  
  showAccountSelection.value = false
  
  // 重新调用 save，此时会带上选择的科目
  await save()
}

// 加载数据（编辑模式）
async function loadData() {
  if (!isEdit.value || !currentId.value) return
  
  try {
    const resp = await api.get(`/objects/vendor_invoice/${currentId.value}`)
    const data = resp.data?.payload
    if (data) {
      form.vendorInvoiceNo = data.vendorInvoiceNo || data.invoiceNo || ''
      form.invoiceDate = data.invoiceDate || ''
      form.dueDate = data.dueDate || ''
      form.vendorCode = data.vendorCode || ''
      form.vendorName = data.vendorName || ''
      form.currency = data.currency || 'JPY'
      form.invoiceQuantity = data.invoiceQuantity || 0
      form.invoiceAmount = data.invoiceAmount || 0
      form.memo = data.memo || ''
      
      // 加载供应商选项
      if (form.vendorCode) {
        vendorOptions.value = [{ code: form.vendorCode, name: form.vendorName, label: `${form.vendorName} (${form.vendorCode})` }]
      }
      
      // 加载入库明细
      await loadAvailableReceipts()
      
      // 恢复已选择的匹配记录
      if (data.lines?.length > 0 && receiptTableRef.value) {
        await nextTick()
        const matchedIds = new Set(data.lines.map((l: any) => l.matchedReceiptId))
        availableReceipts.value.forEach(r => {
          if (matchedIds.has(r.id)) {
            receiptTableRef.value.toggleRowSelection(r, true)
          }
        })
      }
    }
  } catch (e: any) {
    err.value = 'データの読み込みに失敗しました'
  }
}

// 暴露保存方法给父组件
defineExpose({ save, saving })

onMounted(() => {
  loadData()
  searchVendors('')
})
</script>

<style scoped>
.page.page-wide {
  max-width: 1200px;
}

.page.dialog-mode {
  max-width: none;
  padding: 0;
}

.page.dialog-mode .el-card {
  margin: 0;
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

.form-section {
  margin-bottom: 16px;
}

.form-section :deep(.el-input-number .el-input__inner) {
  text-align: right;
}

.nowrap-label :deep(.el-form-item__label) {
  white-space: nowrap;
}

.empty-hint,
.loading-hint {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 40px 20px;
  color: #909399;
  background: #fafafa;
  border-radius: 6px;
}

.receipt-table {
  font-size: 13px;
}

.receipt-table .material-name {
  font-weight: 500;
}

.receipt-table .material-code {
  color: #909399;
  font-size: 12px;
  margin-left: 4px;
}

.receipt-table .highlight {
  color: #409eff;
  font-weight: 600;
}

.comparison-section {
  margin-top: 20px;
  padding: 16px 24px;
  background: linear-gradient(135deg, #f0f7ff 0%, #e8f4fd 100%);
  border-radius: 8px;
  border: 1px solid #d4e8fc;
}

.comparison-row {
  display: flex;
  align-items: center;
  gap: 40px;
  padding: 8px 0;
}

.comparison-row:first-child {
  padding-bottom: 12px;
  border-bottom: 1px dashed #c4ddf7;
}

.comparison-item {
  display: flex;
  align-items: center;
  gap: 12px;
}

.comparison-item.diff {
  margin-left: auto;
}

.comp-label {
  font-size: 14px;
  color: #606266;
}

.comp-value {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
  min-width: 80px;
}

.comp-value.match {
  color: #67c23a;
}

.comp-value.mismatch {
  color: #e6a23c;
}

.comp-value.positive {
  color: #409eff;
}

.comp-value.negative {
  color: #f56c6c;
}

.match-status-inline {
  margin-left: auto;
  display: flex;
  align-items: center;
}

.match-status-inline :deep(.el-tag) {
  font-size: 12px;
}
</style>
