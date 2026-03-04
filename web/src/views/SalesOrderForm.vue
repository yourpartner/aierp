<template>
  <div :class="props.dialogMode ? 'so-form-dialog-body' : 'page page-wide'">
    <el-card :class="{ 'so-dialog-card': props.dialogMode }">
      <template v-if="!props.dialogMode" #header>
        <div class="page-header">
          <div class="page-header-title">{{ isEditMode ? '受注編集' : '受注登録' }}</div>
          <div class="page-actions">
            <el-button @click="$router.back()">キャンセル</el-button>
            <el-button type="primary" @click="save" :loading="saving">{{ commonText.save || '保存' }}</el-button>
          </div>
        </div>
      </template>

      <el-form label-position="left" label-width="100px">
        <!-- 基本信息 -->
        <el-row :gutter="20">
          <!-- 编辑模式才显示受注番号 -->
          <el-col :span="6" v-if="isEditMode">
            <el-form-item label="受注番号">
              <el-input v-model="form.soNo" disabled />
            </el-form-item>
          </el-col>
          <el-col :span="6">
            <el-form-item label="受注日" required>
              <el-date-picker v-model="form.orderDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="得意先" required>
              <el-select
                v-model="form.partnerCode"
                filterable
                remote
                reserve-keyword
                :remote-method="searchCustomers"
                :loading="partnerLoading"
                placeholder="得意先を検索..."
                style="width:100%"
                @change="onPartnerChange"
              >
                <el-option v-for="p in partnerOptions" :key="p.value" :label="p.label" :value="p.value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="4">
            <el-form-item label="ステータス">
              <el-select v-model="form.status" style="width:100%" :disabled="!isEditMode">
                <el-option label="新規登録" value="new" />
                <el-option label="一部出庫" value="partial_shipped" />
                <el-option label="出庫完了" value="shipped" />
                <el-option label="一部請求" value="partial_invoiced" />
                <el-option label="請求完了" value="invoiced" />
                <el-option label="完了" value="completed" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="6">
            <el-form-item label="希望納期">
              <el-date-picker v-model="form.requestedDeliveryDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
            </el-form-item>
          </el-col>
          <el-col :span="4">
            <el-form-item label="通貨">
              <el-select v-model="form.currency" style="width:100%">
                <el-option label="JPY" value="JPY" />
                <el-option label="USD" value="USD" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>
        
        <!-- 配送先 -->
        <el-row :gutter="20">
          <el-col :span="24">
            <el-form-item label="配送先">
              <el-input 
                v-model="form.deliveryAddress" 
                :placeholder="deliveryAddressPlaceholder"
                style="width:100%"
              />
            </el-form-item>
          </el-col>
        </el-row>
        
        <!-- 明細 -->
        <el-divider content-position="left">明細</el-divider>
        
        <div class="lines-section">
          <div class="lines-toolbar">
            <el-button type="primary" size="small" @click="addLine">
              <el-icon><Plus /></el-icon> 行を追加
            </el-button>
          </div>
          
          <el-table :data="form.lines" border size="small" class="lines-table">
            <el-table-column label="#" width="50" align="center">
              <template #default="{ $index }">{{ $index + 1 }}</template>
            </el-table-column>
            
            <el-table-column label="品目" min-width="250">
              <template #default="{ row }">
                <el-select
                  v-model="row.materialCode"
                  filterable
                  remote
                  reserve-keyword
                  :remote-method="(q) => searchMaterials(q, row)"
                  :loading="materialLoading"
                  placeholder="品目を検索..."
                  style="width:100%"
                  @change="(code) => onMaterialChange(code, row)"
                >
                  <el-option 
                    v-for="m in getMaterialOptions(row)" 
                    :key="m.value" 
                    :label="m.label" 
                    :value="m.value"
                  />
                </el-select>
              </template>
            </el-table-column>
            
            <el-table-column label="数量" width="100">
              <template #default="{ row }">
                <el-input-number 
                  v-model="row.quantity" 
                  :min="0" 
                  :precision="0"
                  :controls="false"
                  style="width:100%"
                  @change="recalcLine(row)"
                />
              </template>
            </el-table-column>
            
            <el-table-column label="単位" width="80">
              <template #default="{ row }">
                <el-input v-model="row.uom" disabled />
              </template>
            </el-table-column>
            
            <el-table-column label="単価" width="120">
              <template #default="{ row }">
                <el-input-number 
                  v-model="row.unitPrice" 
                  :min="0" 
                  :precision="0"
                  :controls="false"
                  style="width:100%"
                  @change="recalcLine(row)"
                />
              </template>
            </el-table-column>
            
            <el-table-column label="金額（税抜）" width="120">
              <template #default="{ row }">
                <span class="amount-cell">{{ formatNumber(row.amount) }}</span>
              </template>
            </el-table-column>
            
            <el-table-column label="税率" width="90">
              <template #default="{ row }">
                <el-select v-model="row.taxRate" style="width:100%" @change="recalcLine(row)">
                  <el-option label="10%" :value="10" />
                  <el-option label="8%" :value="8" />
                  <el-option label="0%" :value="0" />
                </el-select>
              </template>
            </el-table-column>
            
            <el-table-column label="税額" width="100">
              <template #default="{ row }">
                <span class="amount-cell">{{ formatNumber(row.taxAmount) }}</span>
              </template>
            </el-table-column>
            
            <el-table-column label="備考" min-width="120">
              <template #default="{ row }">
                <el-input v-model="row.note" placeholder="備考" size="small" />
              </template>
            </el-table-column>
            
            <el-table-column label="操作" width="70" fixed="right">
              <template #default="{ $index }">
                <el-button type="danger" text size="small" @click="removeLine($index)">削除</el-button>
              </template>
            </el-table-column>
          </el-table>
        </div>
        
        <!-- 合計 - 紧凑布局 -->
        <div class="totals-compact">
          <span class="total-item-compact">
            <span class="label">税抜:</span>
            <span class="value">¥{{ formatNumber(subtotal) }}</span>
          </span>
          <span class="total-item-compact">
            <span class="label">税:</span>
            <span class="value">¥{{ formatNumber(form.taxAmountTotal) }}</span>
          </span>
          <span class="total-item-compact total-grand-compact">
            <span class="label">合計:</span>
            <span class="value">¥{{ formatNumber(form.amountTotal) }}</span>
          </span>
        </div>
        
        <!-- 備考 -->
        <el-form-item label="備考">
          <el-input v-model="form.note" type="textarea" :rows="2" />
        </el-form-item>

        <!-- 添付ファイル（編集モードのみ） -->
        <template v-if="isEditMode">
          <div class="so-attachments-section">
            <div class="so-attachments-header">
              <span class="so-attachments-title">添付ファイル</span>
              <el-button size="small" :loading="attUploading" @click="triggerAttUpload">
                <el-icon><UploadFilled /></el-icon>アップロード
              </el-button>
              <input ref="attFileInputRef" type="file" style="display:none" @change="onAttFileSelected" />
            </div>
            <div v-if="!form.attachments?.length" class="so-att-empty">添付ファイルなし</div>
            <div v-else class="so-att-list">
              <div v-for="att in form.attachments" :key="att.id" class="so-att-item" @click="openAttachment(att)">
                <el-icon class="so-att-icon"><DocIcon /></el-icon>
                <span class="so-att-name">{{ att.fileName }}</span>
                <span class="so-att-size">{{ att.size ? (att.size / 1024).toFixed(1) + ' KB' : '' }}</span>
              </div>
            </div>
          </div>
        </template>
      </el-form>
      
      <div class="form-messages">
        <span v-if="msg" class="text-success">{{ msg }}</span>
        <span v-if="err" class="text-error">{{ err }}</span>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, computed } from 'vue'
import { useRoute } from 'vue-router'
import { Plus, UploadFilled, Document as DocIcon } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import api from '../api'
import { useI18n } from '../i18n'

const props = defineProps<{
  dialogMode?: boolean
  editOrderId?: string
  prefillData?: {
    partnerName?: string
    partnerCode?: string
    orderDate?: string
    requestedDeliveryDate?: string
    note?: string
    lines?: Array<{
      materialCode?: string
      materialName?: string
      quantity?: number
      uom?: string
      unitPrice?: number
    }>
  } | null
}>()
const emit = defineEmits<{ (e: 'saved'): void; (e: 'cancel'): void }>()

const route = useRoute()
const editId = ref<string | null>(null)
const isEditMode = computed(() => !!editId.value)

const { section, lang } = useI18n()
const tableLabels = section({ title:'', new:'', number:'', customer:'', amount:'', status:'', issueDate:'' }, (msg) => msg.tables.salesOrders)
const commonText = section({ save:'', loadFailed:'', saved:'', saveFailed:'' }, (msg) => msg.common)

// 客户搜索
const partnerOptions = ref<{ label: string; value: string; name: string }[]>([])
const partnerLoading = ref(false)

// 物料搜索
const materialOptions = ref<{ label: string; value: string; name: string; uom?: string; price?: number }[]>([])
const materialLoading = ref(false)
const materialCache = new Map<string, { label: string; value: string; name: string; uom?: string; price?: number }>()

// 表单数据
const form = reactive<any>({
  soNo: '',
  partnerCode: '',
  partnerName: '',
  orderDate: new Date().toISOString().slice(0, 10),
  requestedDeliveryDate: '',
  currency: 'JPY',
  lines: [createEmptyLine()],
  status: 'new',
  amountTotal: 0,
  taxAmountTotal: 0,
  deliveryAddress: '',
  note: '',
  attachments: [] as any[]
})

// 添付ファイルアップロード
const attFileInputRef = ref<HTMLInputElement | null>(null)
const attUploading = ref(false)

// 配送先占位符
const deliveryAddressPlaceholder = ref('配送先住所を入力してください')

const saving = ref(false)
const msg = ref('')
const err = ref('')

// 计算税前合计
const subtotal = computed(() => {
  return form.lines.reduce((sum: number, line: any) => sum + (line.amount || 0), 0)
})

function createEmptyLine() {
  return {
    lineNo: 1,
    materialCode: '',
    materialName: '',
    quantity: 1,
    uom: '',
    unitPrice: 0,
    amount: 0,
    taxRate: 10,
    taxAmount: 0,
    note: ''
  }
}

function formatNumber(value: number) {
  return new Intl.NumberFormat('ja-JP').format(Math.round(value || 0))
}

// 搜索客户
async function searchCustomers(query: string) {
  partnerLoading.value = true
  try {
    const where: any[] = [{ field: 'flag_customer', op: 'eq', value: true }]
    if (query && query.trim()) {
      where.push({ json: 'name', op: 'contains', value: query.trim() })
    }
    const resp = await api.post('/objects/businesspartner/search', { page: 1, pageSize: 50, where, orderBy: [] })
    const rows: any[] = resp.data?.data || []
    partnerOptions.value = rows.map((p: any) => {
      const code = p.partner_code || p.payload?.partnerCode || p.payload?.code
      const name = p.payload?.name || p.name || code
      return { label: `${name} (${code})`, value: code, name }
    })
  } finally {
    partnerLoading.value = false
  }
}

async function onPartnerChange(code: string) {
  const found = partnerOptions.value.find(opt => opt.value === code)
  form.partnerName = found?.name || ''
  
  // 获取默认配送先
  if (code) {
    try {
      const resp = await api.get(`/sales-orders/default-delivery-address?partnerCode=${encodeURIComponent(code)}`)
      if (resp.data?.address) {
        form.deliveryAddress = resp.data.address
      }
    } catch (e) {
      console.error('Failed to fetch default delivery address:', e)
    }
  }
}

// 搜索物料
async function searchMaterials(query: string, row?: any) {
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
    const opts = rows.map((m: any) => {
      const code = m.material_code || m.payload?.code
      const name = m.name || m.payload?.name || code
      const uom = m.payload?.baseUom || m.payload?.uom || ''
      const price = Number(m.payload?.price ?? m.price ?? 0)
      return { label: `${name} (${code})`, value: code, name, uom, price }
    })
    opts.forEach(opt => materialCache.set(opt.value, opt))
    materialOptions.value = opts
  } finally {
    materialLoading.value = false
  }
}

function getMaterialOptions(row: any) {
  if (row.materialCode && !materialOptions.value.find(o => o.value === row.materialCode)) {
    const cached = materialCache.get(row.materialCode)
    if (cached) {
      return [cached, ...materialOptions.value]
    }
    if (row.materialName) {
      return [
        { label: `${row.materialName} (${row.materialCode})`, value: row.materialCode, name: row.materialName, uom: row.uom },
        ...materialOptions.value
      ]
    }
  }
  return materialOptions.value
}

function onMaterialChange(code: string, row: any) {
  const material = materialCache.get(code) || materialOptions.value.find(m => m.value === code)
  if (material) {
    row.materialName = material.name
    row.uom = material.uom || ''
    row.unitPrice = material.price || 0
    recalcLine(row)
  }
}

function recalcLine(row: any) {
  const qty = Number(row.quantity ?? 0) || 0
  const price = Number(row.unitPrice ?? 0) || 0
  const taxRate = Number(row.taxRate ?? 10) || 10
  
  row.amount = Math.round(qty * price)
  row.taxAmount = Math.round(row.amount * taxRate / 100)
  
  recalcTotals()
}

function recalcTotals() {
  let totalAmount = 0
  let totalTax = 0
  
  form.lines.forEach((line: any, idx: number) => {
    line.lineNo = idx + 1
    totalAmount += line.amount || 0
    totalTax += line.taxAmount || 0
  })
  
  form.taxAmountTotal = totalTax
  form.amountTotal = totalAmount + totalTax
}

function addLine() {
  const newLine = createEmptyLine()
  newLine.lineNo = form.lines.length + 1
  form.lines.push(newLine)
}

function removeLine(index: number) {
  if (form.lines.length > 1) {
    form.lines.splice(index, 1)
    recalcTotals()
  }
}

// 加载编辑数据
async function loadEditData(id: string) {
  try {
    const resp = await api.get(`/objects/sales_order/${id}`)
    const data = resp.data
    if (data) {
      Object.assign(form, data)
      if (!Array.isArray(form.attachments)) form.attachments = []
      if (form.partnerCode && form.partnerName) {
        partnerOptions.value = [{ label: `${form.partnerName} (${form.partnerCode})`, value: form.partnerCode, name: form.partnerName }]
      }
      form.lines.forEach((line: any) => {
        if (line.materialCode && line.materialName) {
          materialCache.set(line.materialCode, { 
            label: `${line.materialName} (${line.materialCode})`, 
            value: line.materialCode, 
            name: line.materialName,
            uom: line.uom,
            price: line.unitPrice
          })
        }
      })
    }
  } catch (e: any) {
    err.value = 'データの読み込みに失敗しました'
  }
}

onMounted(async () => {
  await searchCustomers('')
  await searchMaterials('')

  // dialogModeの場合はrouteを使わずpropsを参照
  const id = props.dialogMode ? (props.editOrderId || null) : (route.params.id as string || null)
  if (id) {
    editId.value = id
    await loadEditData(id)
  }

  // LLMで解析済みのprefillDataがある場合はフォームに適用
  if (props.prefillData) {
    const d = props.prefillData
    if (d.partnerName) form.partnerName = d.partnerName
    if (d.partnerCode) {
      form.partnerCode = d.partnerCode
      partnerOptions.value = [{ label: `${d.partnerName || d.partnerCode} (${d.partnerCode})`, value: d.partnerCode, name: d.partnerName || d.partnerCode }]
    }
    if (d.orderDate) form.orderDate = d.orderDate
    if (d.requestedDeliveryDate) form.requestedDeliveryDate = d.requestedDeliveryDate
    if (d.note) form.note = d.note
    if (d.lines && d.lines.length > 0) {
      const prefillLines = d.lines
        .filter((l: any) => l.materialCode || l.materialName)
        .map((l: any) => {
          const line = createEmptyLine()
          if (l.materialCode) {
            line.materialCode = l.materialCode
            line.materialName = l.materialName || l.materialCode
            materialCache.set(l.materialCode, {
              label: `${l.materialName || l.materialCode} (${l.materialCode})`,
              value: l.materialCode,
              name: l.materialName || l.materialCode,
              uom: l.uom || '',
              price: l.unitPrice || 0
            })
          } else {
            // コードなし: 名称のみの場合は品目名欄に表示用途で使う
            line.materialName = l.materialName || ''
          }
          if (l.quantity != null) line.quantity = Number(l.quantity)
          if (l.uom) line.uom = l.uom
          if (l.unitPrice != null) {
            line.unitPrice = Number(l.unitPrice)
            line.amount = line.quantity * line.unitPrice
          }
          return line
        })
      if (prefillLines.length > 0) {
        form.lines.splice(0, form.lines.length, ...prefillLines)
        recalcTotals()
      }
    }
  }
})

async function save() {
  saving.value = true
  msg.value = ''
  err.value = ''
  try {
    if (!form.partnerCode || !form.partnerCode.trim()) {
      err.value = '得意先を選択してください'
      return
    }
    if (!form.lines.some((l: any) => l.materialCode)) {
      err.value = '少なくとも1つの明細を入力してください'
      return
    }
    
    const payload = JSON.parse(JSON.stringify(form))
    payload.lines = payload.lines.filter((l: any) => l.materialCode)
    
    if (isEditMode.value) {
      await api.put(`/objects/sales_order/${editId.value}`, { payload })
      msg.value = '更新しました'
      if (props.dialogMode) emit('saved')
    } else {
      // 新建时不传 soNo，由后端生成
      delete payload.soNo
      await api.post('/objects/sales_order', { payload })
      msg.value = commonText.value.saved || '保存しました'

      if (props.dialogMode) {
        // ダイアログモードは親に通知して閉じる
        emit('saved')
      } else {
        // ページモードはフォームをリセット
        form.soNo = ''
        form.partnerCode = ''
        form.partnerName = ''
        form.lines.splice(0, form.lines.length, createEmptyLine())
        form.note = ''
        form.status = 'new'
        recalcTotals()
      }
    }
  } catch (e: any) {
    err.value = e?.response?.data?.error || e?.message || commonText.value.saveFailed || '保存に失敗しました'
  } finally {
    saving.value = false
  }
}

// 添付ファイル: クリックでinput起動
function triggerAttUpload() {
  attFileInputRef.value?.click()
}

async function onAttFileSelected(e: Event) {
  const input = e.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !editId.value) return
  attUploading.value = true
  try {
    const r = await api.post(`/crm/sales-order/${editId.value}/attachments`, file, {
      headers: {
        'Content-Type': file.type || 'application/octet-stream',
        'X-File-Name': encodeURIComponent(file.name)
      }
    })
    const payload = r.data?.payload || r.data
    if (payload?.attachments) form.attachments = payload.attachments
    ElMessage.success('アップロードしました')
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'アップロードに失敗しました')
  } finally {
    attUploading.value = false
  }
}

function openAttachment(att: any) {
  if (att.url) window.open(att.url, '_blank')
}

defineExpose({ save, saving })
</script>

<style scoped>
.page.page-wide {
  max-width: 1200px;
}

.so-form-dialog-body {
  display: contents;
}

.so-dialog-card {
  box-shadow: none;
  border: none;
}

.so-dialog-card :deep(.el-card__body) {
  padding: 16px 20px;
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

.page-actions {
  display: flex;
  gap: 8px;
}

.lines-section {
  margin: 16px 0;
}

.lines-toolbar {
  margin-bottom: 12px;
}

.lines-table {
  width: 100%;
}

.lines-table :deep(.el-input-number) {
  width: 100%;
}

.lines-table :deep(.el-input-number .el-input__wrapper) {
  padding-left: 8px;
  padding-right: 8px;
}

.amount-cell {
  display: block;
  text-align: right;
  font-family: 'Roboto Mono', 'SF Mono', Consolas, monospace;
  font-size: 13px;
}

/* 紧凑合计区域 */
.totals-compact {
  display: flex;
  justify-content: flex-end;
  align-items: center;
  gap: 24px;
  padding: 12px 0;
  margin: 16px 0;
  border-top: 1px solid #ebeef5;
}

.total-item-compact {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 14px;
}

.total-item-compact .label {
  color: #909399;
}

.total-item-compact .value {
  font-family: 'Roboto Mono', 'SF Mono', Consolas, monospace;
  font-weight: 500;
}

.total-grand-compact {
  font-size: 16px;
  font-weight: 600;
  padding-left: 16px;
  border-left: 2px solid #409eff;
}

.total-grand-compact .value {
  color: #409eff;
  font-size: 16px;
}

.form-messages {
  margin-top: 18px;
  display: flex;
  gap: 14px;
  font-size: 13px;
}

.text-success {
  color: #67c23a;
}

.text-error {
  color: #f56c6c;
}

/* 添付ファイル */
.so-attachments-section {
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.so-attachments-header {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 10px;
}

.so-attachments-title {
  font-size: 13px;
  font-weight: 600;
  color: #606266;
}

.so-att-empty {
  font-size: 12px;
  color: #909399;
  padding: 8px 0;
}

.so-att-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.so-att-item {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  border-radius: 4px;
  background: #f5f7fa;
  cursor: pointer;
  transition: background 0.15s;
}

.so-att-item:hover {
  background: #ecf5ff;
}

.so-att-icon {
  color: #409eff;
  font-size: 14px;
  flex-shrink: 0;
}

.so-att-name {
  font-size: 13px;
  color: #303133;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.so-att-size {
  font-size: 11px;
  color: #909399;
  flex-shrink: 0;
}
</style>
