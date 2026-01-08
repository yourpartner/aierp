<template>
  <div class="page page-wide">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ isEditMode ? '発注編集' : '発注登録' }}</div>
          <div class="page-actions">
            <el-button @click="$router.back()">キャンセル</el-button>
            <el-button type="primary" @click="save" :loading="saving">保存</el-button>
          </div>
        </div>
      </template>
      
      <el-form label-position="left" label-width="100px">
        <!-- 基本信息 -->
        <el-row :gutter="20">
          <el-col :span="6">
            <el-form-item label="発注番号">
              <el-input v-model="form.poNo" disabled :placeholder="isEditMode ? '' : '自動採番'" />
            </el-form-item>
          </el-col>
          <el-col :span="6">
            <el-form-item label="発注日" required>
              <el-date-picker v-model="form.orderDate" type="date" value-format="YYYY-MM-DD" style="width:100%" />
            </el-form-item>
          </el-col>
          <el-col :span="6">
            <el-form-item label="仕入先" required>
              <el-select
                v-model="form.partnerCode"
                filterable
                remote
                reserve-keyword
                :remote-method="searchVendors"
                :loading="vendorLoading"
                placeholder="仕入先を検索..."
                style="width:100%"
                @change="onVendorChange"
              >
                <el-option v-for="p in vendorOptions" :key="p.value" :label="p.label" :value="p.value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="6">
            <el-form-item label="ステータス">
              <el-select v-model="form.status" style="width:100%" disabled>
                <el-option label="新規" value="new" />
              </el-select>
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
            
            <el-table-column label="品目" min-width="280">
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
            
            <el-table-column label="数量" width="100" align="right">
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
            
            <el-table-column label="単位" width="70" align="center">
              <template #default="{ row }">
                <span>{{ row.uom || '-' }}</span>
              </template>
            </el-table-column>
            
            <el-table-column label="単価" width="110" align="right">
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
            
            <el-table-column label="金額" width="110" align="right">
              <template #default="{ row }">
                <span class="amount-cell">{{ formatNumber(row.amount) }}</span>
              </template>
            </el-table-column>
            
            <el-table-column label="税率" width="85">
              <template #default="{ row }">
                <el-select v-model="row.taxRate" style="width:100%" @change="recalcLine(row)">
                  <el-option label="10%" :value="10" />
                  <el-option label="8%" :value="8" />
                  <el-option label="0%" :value="0" />
                </el-select>
              </template>
            </el-table-column>
            
            <el-table-column label="税額" width="100" align="right">
              <template #default="{ row }">
                <span class="amount-cell">{{ formatNumber(row.taxAmount) }}</span>
              </template>
            </el-table-column>
            
            <el-table-column label="納期" width="140">
              <template #default="{ row }">
                <el-date-picker 
                  v-model="row.deliveryDate" 
                  type="date" 
                  value-format="YYYY-MM-DD" 
                  placeholder="納期"
                  style="width:100%"
                  size="small"
                />
              </template>
            </el-table-column>
            
            <el-table-column label="操作" width="60" fixed="right" align="center">
              <template #default="{ $index }">
                <el-button type="danger" text size="small" @click="removeLine($index)">削除</el-button>
              </template>
            </el-table-column>
          </el-table>
        </div>
        
        <!-- 合計 -->
        <el-row :gutter="20" class="totals-row">
          <el-col :span="8" :offset="16">
            <div class="total-item">
              <span class="total-label">税抜合計:</span>
              <span class="total-value">¥{{ formatNumber(subtotal) }}</span>
            </div>
            <div class="total-item">
              <span class="total-label">消費税合計:</span>
              <span class="total-value">¥{{ formatNumber(form.taxAmountTotal) }}</span>
            </div>
            <div class="total-item total-grand">
              <span class="total-label">合計（税込）:</span>
              <span class="total-value">¥{{ formatNumber(form.amountTotal) }}</span>
            </div>
          </el-col>
        </el-row>
        
        <!-- 備考 -->
        <el-form-item label="備考">
          <el-input v-model="form.note" type="textarea" :rows="3" />
        </el-form-item>
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
import { useRoute, useRouter } from 'vue-router'
import { Plus } from '@element-plus/icons-vue'
import api from '../api'

const route = useRoute()
const router = useRouter()
const editId = ref<string | null>(null)
const isEditMode = computed(() => !!editId.value)

// 供应商搜索
const vendorOptions = ref<{ label: string; value: string; name: string }[]>([])
const vendorLoading = ref(false)

// 物料搜索
const materialOptions = ref<{ label: string; value: string; name: string; uom?: string; price?: number }[]>([])
const materialLoading = ref(false)
const materialCache = new Map<string, { label: string; value: string; name: string; uom?: string; price?: number }>()

// 表单数据
const form = reactive<any>({
  poNo: '',
  partnerCode: '',
  partnerName: '',
  orderDate: new Date().toISOString().slice(0, 10),
  currency: 'JPY',
  lines: [createEmptyLine()],
  status: 'new',
  amountTotal: 0,
  taxAmountTotal: 0,
  note: ''
})

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
    deliveryDate: '',
    receivedQuantity: 0,
    status: 'open'
  }
}

// 编号由后端自动生成

function formatNumber(value: number) {
  return new Intl.NumberFormat('ja-JP').format(Math.round(value || 0))
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
  const found = vendorOptions.value.find(opt => opt.value === code)
  form.partnerName = found?.name || ''
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

async function onMaterialChange(code: string, row: any) {
  const material = materialCache.get(code) || materialOptions.value.find(m => m.value === code)
  if (material) {
    row.materialName = material.name
    row.uom = material.uom || ''
    
    // 从历史采购订单获取价格（同一供应商同一品目最近一次采购的价格）
    if (form.partnerCode) {
      try {
        const resp = await api.get('/purchase-orders/last-price', {
          params: { partnerCode: form.partnerCode, materialCode: code }
        })
        if (resp.data?.found && resp.data.unitPrice > 0) {
          row.unitPrice = resp.data.unitPrice
        } else {
          // 没有历史价格，不设置默认值，让用户手动输入
          row.unitPrice = 0
        }
      } catch (e) {
        // 获取失败时不设置默认价格
        row.unitPrice = 0
      }
    } else {
      // 未选择供应商时不设置默认价格
      row.unitPrice = 0
    }
    
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
    const resp = await api.get(`/objects/purchase_order/${id}`)
    const data = resp.data
    if (data) {
      Object.assign(form, data)
      // 确保供应商在选项中
      if (form.partnerCode && form.partnerName) {
        vendorOptions.value = [{ label: `${form.partnerName} (${form.partnerCode})`, value: form.partnerCode, name: form.partnerName }]
      }
      // 缓存物料选项
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
  await searchVendors('')
  await searchMaterials('')
  
  // 检查是否是编辑模式
  const id = route.params.id as string
  if (id) {
    editId.value = id
    await loadEditData(id)
  } else {
    form.poNo = '' // 新建时由后端自动生成
  }
})

async function save() {
  saving.value = true
  msg.value = ''
  err.value = ''
  try {
    if (!form.partnerCode || !form.partnerCode.trim()) {
      err.value = '仕入先を選択してください'
      return
    }
    if (!form.lines.some((l: any) => l.materialCode)) {
      err.value = '少なくとも1つの明細を入力してください'
      return
    }
    
    const payload = JSON.parse(JSON.stringify(form))
    payload.lines = payload.lines.filter((l: any) => l.materialCode)
    
    if (isEditMode.value) {
      // 更新模式
      await api.put(`/objects/purchase_order/${editId.value}`, { payload })
      msg.value = '更新しました'
    } else {
      // 新建模式
      await api.post('/objects/purchase_order', { payload })
      msg.value = '保存しました'
      
      // 重置表单
      form.poNo = '' // 新建时由后端自动生成
      form.partnerCode = ''
      form.partnerName = ''
      form.lines.splice(0, form.lines.length, createEmptyLine())
      form.note = ''
      recalcTotals()
    }
  } catch (e: any) {
    err.value = e?.response?.data?.error || e?.message || '保存に失敗しました'
  } finally {
    saving.value = false
  }
}
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

.lines-table :deep(.el-input-number .el-input__inner) {
  text-align: right;
  font-family: 'Roboto Mono', 'SF Mono', Consolas, monospace;
  font-size: 13px;
}

.lines-table :deep(.el-table__cell) {
  font-size: 13px;
}

.amount-cell {
  display: block;
  text-align: right;
  font-family: 'Roboto Mono', 'SF Mono', Consolas, monospace;
  font-size: 13px;
}

.totals-row {
  margin-top: 24px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.total-item {
  display: flex;
  justify-content: space-between;
  padding: 6px 0;
  font-size: 14px;
}

.total-label {
  color: #606266;
}

.total-value {
  font-family: 'Roboto Mono', 'SF Mono', Consolas, monospace;
  font-size: 14px;
  font-weight: 500;
}

.total-grand {
  font-size: 16px;
  font-weight: 600;
  padding-top: 12px;
  margin-top: 8px;
  border-top: 2px solid #409eff;
}

.total-grand .total-value {
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
</style>
