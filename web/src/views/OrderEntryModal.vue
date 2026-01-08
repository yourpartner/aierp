<template>
  <div class="page page-medium order-entry-modal">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">受注登録</div>
          <div class="page-actions">
            <el-button type="primary" :loading="submitting" @click="submit" :disabled="!canSubmit">
              登録
            </el-button>
          </div>
        </div>
      </template>

      <el-form :model="form" label-width="120px" label-position="left" class="order-entry-form">
        <el-form-item label="受注番号">
          <el-input v-model="form.soNo" placeholder="SO-YYYYMMDD-001" />
        </el-form-item>
        <el-form-item label="顧客">
          <el-select
            v-model="form.partnerCode"
            filterable
            remote
            reserve-keyword
            clearable
            :remote-method="searchPartners"
            :loading="partnerLoading"
            placeholder="顧客を検索"
            @change="onPartnerChange"
            class="w-large"
          >
            <el-option
              v-for="item in partnerOptions"
              :key="item.value"
              :label="item.label"
              :value="item.value"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="商品">
          <el-select
            v-model="selectedMaterials"
            multiple
            filterable
            remote
            reserve-keyword
            collapse-tags
            collapse-tags-tooltip
            :remote-method="searchMaterials"
            :loading="materialLoading"
            placeholder="商品を検索して追加"
            class="w-large"
          >
            <el-option
              v-for="item in materialOptions"
              :key="item.value"
              :label="item.label"
              :value="item.value"
            />
          </el-select>
        </el-form-item>
        <el-form-item label="納品希望日">
          <el-date-picker
            v-model="form.deliveryDate"
            type="date"
            value-format="YYYY-MM-DD"
            placeholder="任意"
            class="w-medium"
          />
        </el-form-item>
        <el-form-item label="納品場所">
          <el-input v-model="form.deliveryPlace" placeholder="納品場所を入力" />
        </el-form-item>
        <el-form-item label="メモ">
          <el-input v-model="form.memo" type="textarea" :rows="3" placeholder="任意" />
        </el-form-item>
      </el-form>

      <div class="lines-section">
        <div class="section-title">受注明細</div>
        <el-table :data="form.lines" border size="small" style="width: 100%" empty-text="商品を選択してください">
          <el-table-column label="#" type="index" width="60" />
          <el-table-column label="商品コード" min-width="160">
            <template #default="{ row }">
              <div class="cell-main">
                <div class="cell-title">{{ row.materialCode }}</div>
                <div class="cell-sub">{{ row.materialName }}</div>
              </div>
            </template>
          </el-table-column>
          <el-table-column label="数量" width="140" align="right">
            <template #default="{ row }">
              <el-input-number
                v-model="row.qty"
                :min="0"
                :step="1"
                controls-position="right"
                @change="() => recalcLine(row)"
              />
            </template>
          </el-table-column>
          <el-table-column label="税抜単価" width="160" align="right">
            <template #default="{ row }">
              <el-input-number
                v-model="row.unitPrice"
                :min="0"
                :step="100"
                controls-position="right"
                @change="() => recalcLine(row)"
              />
            </template>
          </el-table-column>
          <el-table-column label="税抜総額" width="160" align="right">
            <template #default="{ row }">
              <span class="amount-text">{{ formatNumber(row.amount) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="消費税" width="140" align="right">
            <template #default="{ row }">
              <span class="amount-text">{{ formatNumber(row.taxAmount) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="操作" width="100" align="center">
            <template #default="{ row }">
              <el-button type="text" size="small" @click="removeLine(row)">削除</el-button>
            </template>
          </el-table-column>
        </el-table>
      </div>

      <div class="totals">
        <div>小計（税抜）：<span class="amount-text">{{ formatNumber(subtotal) }}</span></div>
        <div>消費税合計：<span class="amount-text">{{ formatNumber(totalTax) }}</span></div>
        <div>合計（税込）：<span class="amount-text grand">{{ formatNumber(grandTotal) }}</span></div>
      </div>

      <div class="messages">
        <span v-if="successMessage" class="msg-success">{{ successMessage }}</span>
        <span v-if="errorMessage" class="msg-error">{{ errorMessage }}</span>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import api from '../api'

type PartnerOption = { label: string; value: string; name: string }
type MaterialOption = { label: string; value: string; name: string; uom?: string }

interface OrderLine {
  lineNo: number
  materialCode: string
  materialName: string
  qty: number
  unitPrice: number
  amount: number
  taxRate: number
  taxAmount: number
  uom?: string
}

const emit = defineEmits<{ (e: 'done', payload: any): void }>()

const defaultTaxRate = 0.1
const amountFormatter = new Intl.NumberFormat('ja-JP', { minimumFractionDigits: 0, maximumFractionDigits: 2 })
const currencyFormatter = new Intl.NumberFormat('ja-JP', { style: 'currency', currency: 'JPY' })

const form = reactive({
  soNo: generateSoNo(),
  partnerCode: '',
  partnerName: '',
  deliveryDate: '',
  deliveryPlace: '',
  memo: '',
  lines: [] as OrderLine[]
})

const selectedMaterials = ref<string[]>([])
const partnerOptions = ref<PartnerOption[]>([])
const materialOptions = ref<MaterialOption[]>([])
const partnerLoading = ref(false)
const materialLoading = ref(false)
const submitting = ref(false)
const successMessage = ref('')
const errorMessage = ref('')

const materialCache = new Map<string, MaterialOption>()

const subtotal = computed(() => form.lines.reduce((sum, line) => sum + (Number.isFinite(line.amount) ? line.amount : 0), 0))
const totalTax = computed(() => form.lines.reduce((sum, line) => sum + (Number.isFinite(line.taxAmount) ? line.taxAmount : 0), 0))
const grandTotal = computed(() => subtotal.value + totalTax.value)

const canSubmit = computed(() => {
  if (!form.partnerCode) return false
  if (!form.soNo || !form.soNo.trim()) return false
  if (form.lines.length === 0) return false
  return form.lines.every((line) => line.qty > 0 && line.unitPrice >= 0)
})

function generateSoNo() {
  const now = new Date()
  const pad = (n: number) => n.toString().padStart(2, '0')
  return `SO-${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`
}

function ensureMaterialOptions(codes: string[]) {
  const map = new Map<string, MaterialOption>()
  materialOptions.value.forEach((opt) => map.set(opt.value, opt))
  codes.forEach((code) => {
    if (!map.has(code)) {
      const cached = materialCache.get(code)
      if (cached) map.set(code, cached)
      else map.set(code, { label: code, value: code, name: code })
    }
  })
  materialOptions.value = Array.from(map.values())
}

watch(selectedMaterials, (codes) => {
  const set = new Set(codes)
  for (let i = form.lines.length - 1; i >= 0; i -= 1) {
    if (!set.has(form.lines[i].materialCode)) form.lines.splice(i, 1)
  }

  codes.forEach((code, idx) => {
    let line = form.lines.find((l) => l.materialCode === code)
    const material = materialCache.get(code)
    if (!line) {
      line = {
        lineNo: idx + 1,
        materialCode: code,
        materialName: material?.name || code,
        qty: 1,
        unitPrice: 0,
        amount: 0,
        taxRate: defaultTaxRate,
        taxAmount: 0,
        uom: material?.uom
      }
      form.lines.push(line)
    }
    line.lineNo = idx + 1
    if (material) {
      line.materialName = material.name
      line.uom = material.uom
    }
    recalcLine(line)
  })
  ensureMaterialOptions(codes)
})

function roundAmount(value: number) {
  return Math.round((Number.isFinite(value) ? value : 0) * 100) / 100
}

function recalcLine(line: OrderLine) {
  const qty = Number.isFinite(line.qty) ? Number(line.qty) : 0
  const unitPrice = Number.isFinite(line.unitPrice) ? Number(line.unitPrice) : 0
  line.amount = roundAmount(qty * unitPrice)
  line.taxAmount = roundAmount(line.amount * line.taxRate)
}

function removeLine(line: OrderLine) {
  const idx = form.lines.indexOf(line)
  if (idx >= 0) form.lines.splice(idx, 1)
  selectedMaterials.value = form.lines.map((l) => l.materialCode)
}

function formatNumber(value: number | { valueOf(): number }) {
  const num = typeof value === 'number' ? value : Number(value.valueOf())
  return amountFormatter.format(Number.isFinite(num) ? num : 0)
}

async function searchPartners(query: string) {
  partnerLoading.value = true
  try {
    const where: any[] = [{ field: 'flag_customer', op: 'eq', value: true }]
    if (query && query.trim()) where.push({ json: 'name', op: 'contains', value: query.trim() })
    const resp = await api.post('/objects/businesspartner/search', {
      page: 1,
      pageSize: 50,
      where,
      orderBy: []
    })
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

async function searchMaterials(query: string) {
  materialLoading.value = true
  try {
    const where: any[] = []
    if (query && query.trim()) {
      const q = query.trim()
      where.push({ field: 'material_code', op: 'contains', value: q })
      where.push({ json: 'name', op: 'contains', value: q })
    }
    const resp = await api.post('/objects/material/search', {
      page: 1,
      pageSize: 50,
      where,
      orderBy: []
    })
    const rows: any[] = resp.data?.data || []
    const opts = rows.map((m: any) => {
      const code = m.material_code || m.payload?.code
      const name = m.name || m.payload?.name || code
      const uom = m.payload?.baseUom || m.payload?.uom
      const option: MaterialOption = { label: `${name} (${code})`, value: code, name, uom }
      materialCache.set(code, option)
      return option
    })
    materialOptions.value = opts
    ensureMaterialOptions(selectedMaterials.value)
  } finally {
    materialLoading.value = false
  }
}

function onPartnerChange(code: string) {
  const item = partnerOptions.value.find((opt) => opt.value === code)
  form.partnerName = item?.name || ''
}

function resetMessages() {
  successMessage.value = ''
  errorMessage.value = ''
}

function buildPayload() {
  const lines = form.lines.map((line, idx) => ({
    lineNo: idx + 1,
    item: line.materialName || line.materialCode,
    materialCode: line.materialCode,
    qty: roundAmount(line.qty),
    uom: line.uom,
    price: roundAmount(line.unitPrice),
    amount: roundAmount(line.amount),
    taxRate: line.taxRate,
    taxAmount: roundAmount(line.taxAmount)
  }))
  const payload: Record<string, any> = {
    soNo: form.soNo?.trim() || generateSoNo(),
    partnerCode: form.partnerCode,
    orderDate: new Date().toISOString().slice(0, 10),
    status: 'draft',
    amountTotal: roundAmount(subtotal.value),
    taxTotal: roundAmount(totalTax.value),
    amountGross: roundAmount(grandTotal.value),
    lines
  }
  if (form.partnerName) payload.partnerName = form.partnerName
  if (form.deliveryDate) payload.deliveryDate = form.deliveryDate
  if (form.deliveryPlace) payload.deliveryPlace = form.deliveryPlace
  if (form.memo) payload.memo = form.memo
  return payload
}

async function submit() {
  if (!canSubmit.value || submitting.value) return
  resetMessages()
  submitting.value = true
  try {
    const payload = buildPayload()
    await api.post('/objects/sales_order', { payload })
    const soNo = payload.soNo
    const total = currencyFormatter.format(payload.amountGross)
    successMessage.value = `受注 ${soNo} を登録しました。合計 ${total}`
    emit('done', {
      status: 'success',
      content: successMessage.value,
      tag: {
        label: soNo,
        action: 'openEmbed',
        key: 'crm.salesOrders',
        payload: { soNo }
      }
    })
    // reset form for next entry
    form.soNo = generateSoNo()
    form.partnerCode = ''
    form.partnerName = ''
    form.deliveryDate = ''
    form.deliveryPlace = ''
    form.memo = ''
    form.lines.splice(0, form.lines.length)
    selectedMaterials.value = []
  } catch (e: any) {
    errorMessage.value = e?.response?.data?.error || e?.message || '登録に失敗しました'
  } finally {
    submitting.value = false
  }
}

onMounted(() => {
  searchPartners('')
  searchMaterials('')
})

</script>

<style scoped>
.order-entry-modal .page-header-title {
  font-size: 18px;
  font-weight: 600;
}

.order-entry-modal .page-actions {
  display: flex;
  gap: 8px;
}

.order-entry-form {
  margin-bottom: 16px;
}

.order-entry-form .w-large {
  width: 360px;
}

.order-entry-form .w-medium {
  width: 220px;
}

.lines-section {
  margin-top: 8px;
}

.lines-section .section-title {
  font-size: 15px;
  font-weight: 600;
  margin-bottom: 8px;
}

.cell-main {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.cell-title {
  font-weight: 600;
}

.cell-sub {
  font-size: 12px;
  color: #666;
}

.amount-text {
  font-variant-numeric: tabular-nums;
}

.grand {
  font-weight: 600;
}

.totals {
  margin-top: 12px;
  display: flex;
  gap: 24px;
  font-size: 14px;
}

.messages {
  margin-top: 16px;
  min-height: 20px;
}

.msg-success {
  color: #1a73e8;
}

.msg-error {
  color: #d93025;
}
</style>



