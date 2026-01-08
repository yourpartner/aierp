<template>
  <div class="page page-wide">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Coin /></el-icon>
            <span class="page-header-title">{{ labels.title }}</span>
          </div>
        </div>
      </template>

      <div class="rcpt-form-grid">
        <!-- 第一行：查询条件 -->
        <div class="rcpt-form-row">
          <div class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.clearingAccount }}</label>
            <el-select v-model="form.accountCodes" multiple filterable collapse-tags collapse-tags-tooltip :placeholder="labels.selectAccount" class="rcpt-input-account" @change="onAccountChange" @focus="loadClearingAccounts">
              <el-option v-for="a in clearingAccountOptions" :key="a.value" :label="a.label" :value="a.value" />
            </el-select>
          </div>
          <div class="rcpt-form-item">
            <label class="rcpt-label">{{ labels.partner }}</label>
            <el-select v-model="form.partnerId" filterable remote clearable :remote-method="searchPartners" reserve-keyword :placeholder="labels.optional" class="rcpt-input-partner" @change="onPartnerChange">
              <el-option v-for="p in partnerOptions" :key="p.value" :label="p.label" :value="p.value" />
            </el-select>
          </div>
        </div>

        <!-- 第二行：入金情報 -->
        <div class="rcpt-form-row">
          <div class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.bankAccount }}</label>
            <el-select v-model="form.bankAccountCode" filterable remote :remote-method="searchBankAccounts" reserve-keyword :placeholder="labels.bankPlaceholder" class="rcpt-input-account" @change="onBankAccountChange">
              <el-option v-for="a in bankAccountOptions" :key="a.value" :label="a.label" :value="a.value" />
            </el-select>
            <span v-if="form.bankAccountCode" class="rcpt-currency-badge">{{ form.currency || 'JPY' }}</span>
          </div>
          <div class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.receiptAmount }}</label>
            <el-input v-model="form.amountText" class="rcpt-input-amt" :placeholder="labels.amount" />
          </div>
          <div class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.receiptDate }}</label>
            <el-date-picker v-model="form.paymentDate" class="rcpt-input-date-short" type="date" value-format="YYYY-MM-DD" :placeholder="labels.date" />
          </div>
        </div>

        <!-- 第三行：手数料 -->
        <div class="rcpt-form-row">
          <div class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.feeBearer }}</label>
            <el-select v-model="form.bankFeeBearer" class="rcpt-input-fee" :placeholder="labels.bearer">
              <el-option :label="labels.customerBears" value="customer" />
              <el-option :label="labels.companyBears" value="company" />
            </el-select>
          </div>
          <div v-if="form.bankFeeBearer==='company'" class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.feeAmount }}</label>
            <el-input v-model="form.bankFeeAmountText" class="rcpt-input-fee-amt" :placeholder="labels.amount" />
          </div>
          <div v-if="form.bankFeeBearer==='company'" class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.feeAccount }}</label>
            <el-select v-model="form.bankFeeAccountCode" filterable remote :remote-method="searchFeeAccounts" reserve-keyword :placeholder="labels.account" class="rcpt-input-account">
              <el-option v-for="a in feeAccountOptions" :key="a.value" :label="a.label" :value="a.value" />
            </el-select>
          </div>
        </div>
      </div>

      <!-- 无数据提示 -->
      <div v-if="form.accountCodes && form.accountCodes.length > 0 && openItems.length===0 && !loading" class="planner-empty">
        {{ labels.noOpenItems }}
      </div>

      <!-- 未清项列表 -->
      <el-table v-if="plan.length > 0" :data="plan" border style="width:100%" size="small" :row-class-name="planRowClass">
        <el-table-column label="" width="48" header-align="center" align="center">
          <template #default="{ row }">
            <el-checkbox v-model="row.selected" @change="() => onRowSelectChange(row)" />
          </template>
        </el-table-column>
        <el-table-column :label="labels.index" width="50" type="index" align="center" />
        <el-table-column :label="labels.voucherNo" width="120" align="left">
          <template #default="{ row }">
            <el-link v-if="row.voucherNo" type="primary" @click="openVoucherDetail(row.voucherId || row.voucher_id)">
              {{ row.voucherNo }}
            </el-link>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column :label="labels.account" width="160" align="left">
          <template #default="{ row }">
            <span :title="row.account_code">{{ row.accountName ? `${row.accountName} (${row.account_code})` : row.account_code || '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="labels.partnerCol" min-width="140" align="left">
          <template #default="{ row }">{{ row.partnerDisplay || '-' }}</template>
        </el-table-column>
        <el-table-column :label="labels.remark" min-width="100" align="left">
          <template #default="{ row }">
            <span class="cell-remark" :title="row.remark || '-'">{{ row.remark || '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="labels.docDate" width="100" align="center" prop="docDate" />
        <el-table-column :label="labels.originalAmount" width="110" align="right" header-align="right">
          <template #default="{ row }">{{ formatAmount(row.original) }}</template>
        </el-table-column>
        <el-table-column :label="labels.residualAmount" width="110" align="right" header-align="right">
          <template #default="{ row }">{{ formatAmount(row.residual) }}</template>
        </el-table-column>
        <el-table-column :label="labels.applyAmount" width="140" align="center" header-align="center">
          <template #default="{ row }">
            <el-input-number v-model="row.apply" :min="0" :max="row.residual" :step="1000" controls-position="right"
              :formatter="thousandFmt" :parser="thousandParse" @change="() => onApplyChange(row)" />
          </template>
        </el-table-column>
      </el-table>

      <!-- 底部汇总和操作 -->
      <div class="planner-footer">
        <div class="planner-summary">
          {{ labels.clearingTotal }}：{{ formatAmount(sumApply()) }} / {{ labels.actualReceipt }}：{{ formatAmount(netAmount()) }}
          <span v-if="form.bankFeeBearer==='company' && bankFeeAmount()>0" class="planner-fee-info">　{{ labels.fee }}：{{ formatAmount(bankFeeAmount()) }}（{{ labels.companyBears }}）</span>
          <span v-if="!amountsEqual(sumApply(), targetAmount())" class="planner-warning">（{{ labels.mismatch }}）</span>
        </div>
        <div class="planner-actions">
          <el-button @click="handleCancel">{{ labels.cancel }}</el-button>
          <el-button type="primary" :disabled="!canCommit()" :loading="committing" @click="submitReceipt">{{ labels.execute }}</el-button>
        </div>
      </div>

      <!-- 结果消息 -->
      <div v-if="msg || err" class="planner-message">
        <span v-if="msg" class="planner-success">{{ msg }}</span>
        <span v-if="err" class="planner-error">{{ err }}</span>
      </div>
    </el-card>

    <!-- 伝票详情弹窗（只读） -->
    <el-dialog
      v-model="voucherDialogVisible"
      width="auto"
      append-to-body
      destroy-on-close
      class="voucher-detail-dialog"
    >
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VouchersList
          v-if="voucherDialogVisible"
          ref="voucherDetailRef"
          class="voucher-detail-embed"
          :allow-edit="false"
        />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, watch, nextTick, onMounted, inject } from 'vue'
import { Coin } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import api from '../api'
import { useI18n } from '../i18n'
import VouchersList from './VouchersList.vue'

const emit = defineEmits<{
  (e: 'done', result: any): void
  (e: 'cancel'): void
}>()

// 从 ChatKit 注入的方法（可选）
const chatkitCloseModal = inject<() => void>('chatkitCloseModal', () => {})

const { section, lang } = useI18n()

const labels = section(
  {
    title: '', clearingAccount: '', selectAccount: '', partner: '', optional: '',
    bankAccount: '', bankPlaceholder: '', receiptAmount: '', amount: '',
    receiptDate: '', date: '', feeBearer: '', customerBears: '', companyBears: '',
    feeAmount: '', feeAccount: '', account: '', bearer: '',
    noOpenItems: '', index: '', voucherNo: '', docDate: '', originalAmount: '', residualAmount: '',
    applyAmount: '', remark: '', partnerCol: '', clearingTotal: '', actualReceipt: '', fee: '',
    mismatch: '', cancel: '', execute: ''
  },
  (msg) => ({
    title: msg.receiptPlanner?.title || '銀行入金配分',
    clearingAccount: msg.receiptPlanner?.clearingAccount || '消込科目',
    selectAccount: msg.receiptPlanner?.selectAccount || '科目を選択',
    partner: msg.receiptPlanner?.partner || '取引先',
    optional: msg.receiptPlanner?.optional || '任意',
    bankAccount: msg.receiptPlanner?.bankAccount || '入金口座',
    bankPlaceholder: msg.receiptPlanner?.bankPlaceholder || '銀行/現金科目',
    receiptAmount: msg.receiptPlanner?.receiptAmount || '入金金額',
    amount: msg.receiptPlanner?.amount || '金額',
    receiptDate: msg.receiptPlanner?.receiptDate || '入金日',
    date: msg.receiptPlanner?.date || '日付',
    feeBearer: msg.receiptPlanner?.feeBearer || '手数料負担',
    customerBears: msg.receiptPlanner?.customerBears || '先方負担',
    companyBears: msg.receiptPlanner?.companyBears || '当社負担',
    feeAmount: msg.receiptPlanner?.feeAmount || '手数料額',
    feeAccount: msg.receiptPlanner?.feeAccount || '手数料科目',
    account: msg.receiptPlanner?.account || '科目',
    bearer: msg.receiptPlanner?.bearer || '負担',
    noOpenItems: msg.receiptPlanner?.noOpenItems || '未消込項目は見つかりません',
    index: msg.receiptPlanner?.index || '#',
    voucherNo: msg.receiptPlanner?.voucherNo || '伝票番号',
    docDate: msg.receiptPlanner?.docDate || '伝票日付',
    originalAmount: msg.receiptPlanner?.originalAmount || '原金額',
    residualAmount: msg.receiptPlanner?.residualAmount || '未消込残高',
    applyAmount: msg.receiptPlanner?.applyAmount || '今回配分',
    remark: msg.receiptPlanner?.remark || '備考',
    partnerCol: msg.receiptPlanner?.partnerCol || '取引先',
    clearingTotal: msg.receiptPlanner?.clearingTotal || '消込金額',
    actualReceipt: msg.receiptPlanner?.actualReceipt || '実入金',
    fee: msg.receiptPlanner?.fee || '手数料',
    mismatch: msg.receiptPlanner?.mismatch || '不一致',
    cancel: msg.common?.cancel || 'キャンセル',
    execute: msg.receiptPlanner?.execute || '仕訳登録'
  })
)

// 表单数据
const form = reactive({
  accountCodes: [] as string[],
  partnerId: '',
  amount: 0,
  amountText: '',
  currency: '',
  paymentDate: '',
  bankAccountCode: '',
  bankFeeBearer: 'customer',
  bankFeeAmountText: '',
  bankFeeAccountCode: ''
})

// 状态
const loading = ref(false)
const committing = ref(false)
const msg = ref('')
const err = ref('')
const openItems = ref<any[]>([])
const plan = ref<any[]>([])

// 下拉选项
const clearingAccountOptions = reactive<{label:string,value:string}[]>([])
const bankAccountOptions = reactive<{label:string,value:string}[]>([])
const feeAccountOptions = reactive<{label:string,value:string}[]>([])
const partnerOptions = reactive<{label:string,value:string,name?:string}[]>([])

// 缓存
const partnerCache = new Map<string, { code: string; name: string }>()
const voucherCache = new Map<string, { voucherNo: string; summary: string; lines: any[] }>()
const accountCache = new Map<string, { code: string; name: string; currency?: string }>()

// 伝票详情弹窗
const voucherDialogVisible = ref(false)
const voucherDetailRef = ref<InstanceType<typeof VouchersList> | null>(null)

// 重新规划定时器
let replanTimer: ReturnType<typeof setTimeout> | null = null

function parseJsonSafe(val: any): any {
  if (typeof val === 'object' && val !== null) return val
  if (typeof val !== 'string') return null
  try { return JSON.parse(val) } catch { return null }
}

// === 数据加载 ===

async function loadOpenItems(){
  if (!form.accountCodes || form.accountCodes.length === 0) {
    openItems.value = []
    plan.value = []
    return
  }
  loading.value = true
  try {
    const where: any[] = []
    if (form.accountCodes.length === 1) {
      where.push({ field:'account_code', op:'eq', value: form.accountCodes[0] })
    } else {
      where.push({ field:'account_code', op:'in', value: form.accountCodes })
    }
    if (form.partnerId) {
      where.push({ field:'partner_id', op:'eq', value: form.partnerId })
    }
    const r = await api.post('/objects/openitem/search', { page:1, pageSize:200, where, orderBy:[{field:'doc_date',dir:'ASC'}] })
    let data: any[] = (r.data?.data || []).map((x: any) => ({ ...x, apply: 0 }))

    // 只保留正残高
    data = data.filter((item: any) => {
      const residual = Number(item?.residual_amount ?? item?.residualAmount ?? item?.residual ?? 0)
      return residual > 0.0001
    })

    // 预加载关联数据
    const voucherIds = data.map((x: any) => {
      const refs = parseJsonSafe(x.refs)
      return String(x.voucher_id || x.voucherId || refs?.voucherId || refs?.voucher_id || '')
    }).filter(id => !!id)
    await fetchVouchersByIds(voucherIds)

    const partnerCodes = data.map((x: any) => String(x.partner_id || x.partnerId || '').trim()).filter(code => !!code)
    await fetchPartnersByCodes(partnerCodes)

    const accountCodes = data.map((x: any) => String(x.account_code || '').trim()).filter(code => !!code)
    await fetchAccountsByCodes(accountCodes)

    // 丰富数据
    openItems.value = data.map((item: any) => {
      const refs = parseJsonSafe(item.refs)
      const voucherId = String(item.voucher_id || item.voucherId || refs?.voucherId || refs?.voucher_id || '')
      const voucherInfo = voucherId ? voucherCache.get(voucherId) : undefined
      const lineNo = Number(item.voucher_line_no ?? item.voucherLineNo ?? refs?.lineNo ?? 0)
      const line = voucherInfo && Array.isArray(voucherInfo.lines) && lineNo > 0 ? voucherInfo.lines[lineNo - 1] : null
      const partnerCode = String(item.partner_id || item.partnerId || line?.customerId || line?.customerCode || '').trim()
      const partnerInfo = partnerCache.get(partnerCode)
      const partnerName = partnerInfo?.name || line?.customerName || ''
      const partnerDisplayText = partnerDisplay(partnerCode, partnerName)
      const remark = (line?.note || line?.remark || line?.description || '').toString()
      const voucherNo = (voucherInfo?.voucherNo || refs?.voucherNo || '').toString()
      const original = Number(item.original_amount ?? item.originalAmount ?? 0)
      const residual = Number(item.residual_amount ?? item.residualAmount ?? 0)
      const docDate = item.doc_date || item.docDate || ''
      const accountCode = String(item.account_code || '').trim()
      const accountInfo = accountCache.get(accountCode)
      const accountName = accountInfo?.name || ''
      return {
        ...item,
        voucherId,
        partnerCode,
        partnerName,
        partnerDisplay: partnerDisplayText,
        voucherNo,
        remark,
        original,
        residual,
        docDate,
        accountName
      }
    })

    applyAllocations(new Map())
  } finally {
    loading.value = false
  }
}

async function fetchPartnersByCodes(codes: string[]){
  const unique = Array.from(new Set((codes || []).map(code => (code || '').toString().trim()).filter(code => !!code)))
  const missing = unique.filter(code => !partnerCache.has(code))
  if (missing.length === 0) return
  
  const body = { 
    page: 1, 
    pageSize: Math.max(200, missing.length), 
    where: [{ 
      or: [
        { field: 'id', op: 'in', value: missing },
        { field: 'partner_code', op: 'in', value: missing }
      ]
    }], 
    orderBy: [] as any[] 
  }
  try {
    const resp = await api.post('/objects/businesspartner/search', body)
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    for (const row of rows){
      const id = String(row.id || '').trim()
      const code = String(row.partner_code || row.partnerCode || row.code || '').trim()
      const payload = parseJsonSafe(row.payload) || {}
      const name = (payload?.name || row.name || row.partner_name || '').toString()
      if (id) partnerCache.set(id, { code: code || id, name })
      if (code) partnerCache.set(code, { code, name })
    }
  } catch {}
}

async function fetchAccountsByCodes(codes: string[]){
  const unique = Array.from(new Set((codes || []).map(code => (code || '').toString().trim()).filter(code => !!code)))
  const missing = unique.filter(code => !accountCache.has(code))
  if (missing.length === 0) return
  const body = { page: 1, pageSize: Math.max(200, missing.length), where: [{ field: 'account_code', op: 'in', value: missing }], orderBy: [] as any[] }
  try {
    const resp = await api.post('/objects/account/search', body)
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    for (const row of rows){
      const code = String(row.account_code || '').trim()
      if (!code) continue
      const payload = parseJsonSafe(row.payload) || {}
      const name = (payload?.name || row.name || '').toString()
      const currency = (payload?.currency || row.currency || '').toString()
      accountCache.set(code, { code, name, currency })
    }
  } catch {}
}

async function fetchVouchersByIds(ids: string[]){
  const unique = Array.from(new Set((ids || []).map(id => (id || '').toString().trim()).filter(id => !!id)))
  const missing = unique.filter(id => !voucherCache.has(id))
  if (missing.length === 0) return
  const body = { page: 1, pageSize: Math.max(200, missing.length), where: [{ field: 'id', op: 'in', value: missing }], orderBy: [] as any[] }
  try {
    const resp = await api.post('/objects/voucher/search', body)
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    for (const row of rows){
      const id = String(row.id || row.voucher_id || row.voucherId || '').trim()
      if (!id) continue
      const payload = parseJsonSafe(row.payload) || {}
      const header = payload.header || row.header || {}
      const voucherNo = (header.voucherNo || header.voucher_no || header.number || '').toString()
      const summary = (header.summary || header.remark || '').toString()
      const lines = Array.isArray(payload.lines) ? payload.lines : (Array.isArray(row.lines) ? row.lines : [])
      voucherCache.set(id, { voucherNo, summary, lines })
    }
  } catch {}
}

// === 下拉远程搜索 ===

async function loadClearingAccounts(){
  try {
    const r = await api.post('/objects/account/search', { 
      page:1, pageSize:200, 
      where:[
        { json:'openItem', op:'eq', value:true },
        {
          anyOf: [
            { json:'openItemBaseline', op:'eq', value:'CUSTOMER' },
            { json:'openItemBaseline', op:'eq', value:'NONE' },
            { json:'openItemBaseline', op:'eq', value:null }
          ]
        }
      ], 
      orderBy:[{field:'account_code',dir:'ASC'}] 
    })
    const data = r.data?.data || []
    clearingAccountOptions.splice(0, clearingAccountOptions.length, ...data.map((x: any) => ({
      label: `${x.name || x.payload?.name || x.account_code} (${x.account_code})`,
      value: x.account_code
    })))
  } catch (e) {
    console.error('Failed to load clearing accounts:', e)
  }
}

async function searchPartners(query: string){
  const where: any[] = [{ field:'flag_customer', op:'eq', value:true }]
  if (query && query.trim()) where.push({ json:'name', op:'contains', value: query.trim() })
  const r = await api.post('/objects/businesspartner/search', { page:1, pageSize:50, where, orderBy:[] })
  partnerOptions.splice(0, partnerOptions.length, ...((r.data?.data || []).map((p: any) => ({ 
    label:`${p.payload?.name||p.name} (${p.partner_code})`, 
    value:p.partner_code, 
    name:(p.payload?.name||p.name) 
  }))))
}

async function searchBankAccounts(query: string){
  const q = (query || '').trim()
  const extra = [] as any[]
  if (q) { extra.push({ json:'name', op:'contains', value:q }); extra.push({ field:'account_code', op:'contains', value:q }) }
  const [bankRes1, cashRes1, bankRes2, cashRes2] = await Promise.all([
    api.post('/objects/account/search', { page:1, pageSize:100, where:[ { json:'isbank', op:'eq', value:true }, ...extra ], orderBy: [] }),
    api.post('/objects/account/search', { page:1, pageSize:100, where:[ { json:'iscash', op:'eq', value:true }, ...extra ], orderBy: [] }),
    api.post('/objects/account/search', { page:1, pageSize:100, where:[ { json:'isBank', op:'eq', value:true }, ...extra ], orderBy: [] }),
    api.post('/objects/account/search', { page:1, pageSize:100, where:[ { json:'isCash', op:'eq', value:true }, ...extra ], orderBy: [] })
  ])
  let all: any[] = ([] as any[]).concat(bankRes1.data?.data || [], cashRes1.data?.data || [], bankRes2.data?.data || [], cashRes2.data?.data || [])
  const seen = new Set<string>()
  const items: any[] = []
  for (const x of all){
    const code = x.account_code || x.payload?.code
    if (!code || seen.has(code)) continue
    seen.add(code)
    const name = x.name || x.payload?.name || code
    items.push({ label: `${name} (${code})`, value: code })
  }
  bankAccountOptions.splice(0, bankAccountOptions.length, ...items)
}

async function searchFeeAccounts(query: string){
  const q = (query || '').trim()
  const where: any[] = []
  if (q){
    where.push({ field:'account_code', op:'contains', value:q })
    where.push({ json:'name', op:'contains', value:q })
  }
  try {
    const resp = await api.post('/objects/account/search', { page:1, pageSize:100, where, orderBy:[{ field:'account_code', dir:'ASC' }] })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    const seen = new Set<string>()
    const items: any[] = []
    for (const row of rows){
      const code = row.account_code || row.payload?.code
      if (!code || seen.has(code)) continue
      seen.add(code)
      const name = row.name || row.payload?.name || code
      items.push({ label: `${name} (${code})`, value: code })
    }
    feeAccountOptions.splice(0, feeAccountOptions.length, ...items)
  } catch {}
}

// === 事件处理 ===

function onAccountChange(){ 
  loadOpenItems().then(autoReplan) 
  try { localStorage.setItem('planner:lastAccountCodes', JSON.stringify(form.accountCodes)) } catch {}
}

function onPartnerChange(){ loadOpenItems().then(autoReplan) }

async function onBankAccountChange(){
  const code = form.bankAccountCode
  if (!code) {
    form.currency = ''
    return
  }
  let info = accountCache.get(code)
  if (!info) {
    try {
      const resp = await api.post('/objects/account/search', { 
        page: 1, pageSize: 1, 
        where: [{ field: 'account_code', op: 'eq', value: code }],
        orderBy: []
      })
      const rows = resp.data?.data || []
      if (rows.length > 0) {
        const row = rows[0]
        const payload = parseJsonSafe(row.payload) || {}
        const currency = payload.currency || payload.accountCurrency || row.currency || row.account_currency || 'JPY'
        info = { code, name: payload.name || row.name || '', currency }
        accountCache.set(code, info)
      }
    } catch {}
  }
  form.currency = info?.currency || 'JPY'
  try { localStorage.setItem('planner:lastBankAccountCode', code) } catch {}
}

function onRowSelectChange(row: any){
  if (row.selected) {
    row.apply = row.residual
  } else {
    row.apply = 0
  }
}

function onApplyChange(row: any){
  row.selected = Number(row.apply || 0) > 0
}

// === 智能配分 ===

function partnerDisplay(code?: string, name?: string){
  const trimmedCode = (code || '').toString().trim()
  const cached = trimmedCode ? partnerCache.get(trimmedCode) : undefined
  const finalName = (cached?.name || name || '').toString().trim()
  const finalCode = cached?.code || trimmedCode
  if (finalCode && finalName) return `${finalCode} ${finalName}`
  if (finalCode) return finalCode
  return finalName
}

function sumApply(){ return (plan.value || []).reduce((s: number, x: any) => s + Number(x.apply || 0), 0) }

function bankFeeAmount(){
  const txt = String(form.bankFeeAmountText ?? '').replace(/,/g, '').trim()
  if (!txt) return 0
  const n = Number(txt)
  if (!Number.isFinite(n) || n <= 0) return 0
  return n
}

function netAmount(){
  const val = Number(form.amount || 0)
  return Number.isFinite(val) && val > 0 ? val : 0
}

function targetAmount(){
  const net = netAmount()
  if (form.bankFeeBearer === 'company'){
    return net + bankFeeAmount()
  }
  return net
}

function amountsEqual(a: number, b: number){
  return Math.abs(Number(a || 0) - Number(b || 0)) <= 0.0001
}

function canCommit(){
  const target = targetAmount()
  const fee = form.bankFeeBearer === 'company' ? bankFeeAmount() : 0
  if (!form.accountCodes || form.accountCodes.length === 0) return false
  if (!form.bankAccountCode) return false
  if (target <= 0) return false
  if (!form.paymentDate) return false
  if (!form.bankFeeBearer) return false
  if (form.bankFeeBearer === 'company'){
    if (fee <= 0) return false
    if (!form.bankFeeAccountCode) return false
    if (fee >= target) return false
  }
  if (netAmount() <= 0) return false
  return amountsEqual(sumApply(), target)
}

function buildSmartAllocations(){
  const target = targetAmount()
  const map = new Map<string, number>()
  if (!Number.isFinite(target) || target <= 0) return map
  
  const items = [...(openItems.value || [])] as any[]
  if (items.length === 0) return map
  
  const openItemsList = items.map(it => ({
    key: String(it?.id || it?.openItemId || it?.open_item_id || ''),
    residual: Math.max(Number(it?.residual_amount ?? it?.residualAmount ?? it?.residual ?? 0) || 0, 0),
    docDate: it?.doc_date || it?.docDate || '',
    original: it
  })).filter(x => x.key && x.residual > 0)
    .sort((a, b) => (a.docDate || '9999-12-31').localeCompare(b.docDate || '9999-12-31'))
  
  const used = new Set<string>()
  let remain = target
  
  // 第1优先级：精确单笔匹配
  for (const item of openItemsList) {
    if (Math.abs(item.residual - remain) < 0.01) {
      map.set(item.key, item.residual)
      return map
    }
  }
  
  // 第2优先级：两笔组合精确匹配
  for (let i = 0; i < openItemsList.length; i++) {
    for (let j = i + 1; j < openItemsList.length; j++) {
      const sum = openItemsList[i].residual + openItemsList[j].residual
      if (Math.abs(sum - target) < 0.01) {
        map.set(openItemsList[i].key, openItemsList[i].residual)
        map.set(openItemsList[j].key, openItemsList[j].residual)
        return map
      }
    }
  }
  
  // 第3优先级：FIFO 贪婪匹配
  for (const item of openItemsList) {
    if (remain <= 0) break
    if (used.has(item.key)) continue
    const apply = Math.min(remain, item.residual)
    if (apply > 0) {
      map.set(item.key, apply)
      used.add(item.key)
      remain -= apply
    }
  }
  
  return map
}

function applyAllocations(allocations: Map<string, number>){
  plan.value = (openItems.value || []).map((it: any) => {
    const key = String(it?.id || it?.openItemId || it?.open_item_id || '')
    const apply = allocations.get(key) || 0
    return { ...it, apply, selected: apply > 0 }
  })
}

async function autoReplan(){
  if (!openItems.value || openItems.value.length === 0){ plan.value = []; return }
  const target = targetAmount()
  if (!Number.isFinite(target) || target <= 0){
    applyAllocations(new Map())
    return
  }
  const allocations = buildSmartAllocations()
  applyAllocations(allocations)
}

function cancelScheduledReplan(){
  if (replanTimer !== null){
    clearTimeout(replanTimer)
    replanTimer = null
  }
}

function scheduleReplan(delay = 300){
  cancelScheduledReplan()
  replanTimer = setTimeout(async () => {
    replanTimer = null
    if (openItems.value && openItems.value.length > 0){
      await autoReplan()
    }
  }, delay)
}

// === 提交 ===

async function submitReceipt(){
  if (!canCommit()) return
  committing.value = true
  err.value = ''
  msg.value = ''
  try {
    const idempotencyKey = `rcpt-${Date.now()}`
    const allocations = (plan.value || []).filter((x: any) => Number(x.apply) > 0).map((x: any) => ({ 
      openItemId: x.id || x.openItemId || x.open_item_id, 
      applyAmount: Number(x.apply) 
    }))
    const header: any = { 
      postingDate: form.paymentDate || (new Date().toISOString().slice(0, 10)), 
      currency: form.currency || 'JPY', 
      bankAccountCode: form.bankAccountCode 
    }
    if (form.bankFeeBearer === 'company'){
      const fee = bankFeeAmount()
      if (fee > 0 && form.bankFeeAccountCode){
        header.bankFeeAmount = fee
        header.bankFeeAccountCode = form.bankFeeAccountCode
      }
    }
    header.netAmount = netAmount()
    const resp = await api.post('/operations/bank-collect/allocate', { header, allocations, idempotencyKey })
    const result = resp.data || {}
    const voucherNo = result.voucherNo || result.voucher_no
    const feeMessage = form.bankFeeBearer === 'company' && bankFeeAmount() > 0 ? `（手数料 ${formatAmount(bankFeeAmount())}）` : ''
    const headMessage = voucherNo ? `銀行入金を登録しました（伝票番号 ${voucherNo}）。` : '銀行入金を登録しました。'
    const detailMessage = `実入金 ${formatAmount(netAmount())} ${form.currency}${feeMessage}、配分 ${allocations.length} 件。`
    
    ElMessage.success(`${headMessage}${detailMessage}`)
    msg.value = `${headMessage}${detailMessage}`
    
    emit('done', { 
      voucherNo, 
      voucherId: result.voucherId || result.voucher_id,
      message: `${headMessage}${detailMessage}`
    })
    
    // 如果在 ChatKit 弹窗中，关闭弹窗
    chatkitCloseModal()
  } catch (e: any) { 
    const errMsg = e?.response?.data?.error || '提交失敗'
    ElMessage.error(errMsg)
    err.value = errMsg
  } finally { 
    committing.value = false 
  }
}

function handleCancel(){
  emit('cancel')
  chatkitCloseModal()
}

// === 伝票详情 ===

async function openVoucherDetail(voucherId: string){
  if (!voucherId) return
  voucherDialogVisible.value = true
  await nextTick()
  // 使用 applyIntent 并设置 detailOnly: true 直接显示凭证详情，不显示列表
  voucherDetailRef.value?.applyIntent?.({
    voucherId,
    detailOnly: true
  })
}

// === 表格样式 ===

function planRowClass({ row }: { row: any }){
  if (row.selected || Number(row.apply || 0) > 0) return 'row-selected'
  return ''
}

// === 工具函数 ===

function thousandFmt(val: any){ try{ const n = Number(val); return Number.isFinite(n) ? n.toLocaleString('ja-JP') : val }catch{ return val } }
function thousandParse(val: any){ try{ return String(val).replace(/,/g, '') }catch{ return val } }
function formatAmount(n: any){ const v = Number(n || 0); return Number.isFinite(v) ? v.toLocaleString('ja-JP') : '0' }

// === 监听 ===

watch(() => form.accountCodes, async (v) => {
  cancelScheduledReplan()
  if (!v || v.length === 0) { openItems.value = []; plan.value = []; return }
  await loadOpenItems()
  const target = targetAmount()
  if (Number.isFinite(target) && target > 0 && openItems.value && openItems.value.length > 0) scheduleReplan(0)
}, { deep: true })

watch(() => form.partnerId, async () => {
  cancelScheduledReplan()
  if (!form.accountCodes || form.accountCodes.length === 0) { return }
  await loadOpenItems()
  const target = targetAmount()
  if (Number.isFinite(target) && target > 0 && openItems.value && openItems.value.length > 0) scheduleReplan(0)
})

watch(() => form.amountText, (v) => {
  cancelScheduledReplan()
  const txt = String(v ?? '').replace(/,/g, '').trim()
  if (!txt) {
    form.amount = 0
    if (openItems.value && openItems.value.length > 0) applyAllocations(new Map())
    else plan.value = []
    return
  }
  const n = Number(txt)
  if (!Number.isFinite(n) || n <= 0) {
    form.amount = 0
    if (openItems.value && openItems.value.length > 0) applyAllocations(new Map())
    else plan.value = []
    return
  }
  form.amount = n
  if (openItems.value && openItems.value.length > 0) scheduleReplan()
})

watch(() => form.bankFeeBearer, (v) => {
  cancelScheduledReplan()
  if (v !== 'company'){
    form.bankFeeAmountText = ''
    form.bankFeeAccountCode = ''
  }
  if (openItems.value && openItems.value.length > 0) scheduleReplan()
})

watch(() => form.bankFeeAmountText, () => {
  cancelScheduledReplan()
  if (form.bankFeeBearer === 'company'){
    if (openItems.value && openItems.value.length > 0) scheduleReplan()
  }
})

// === 初始化 ===

async function loadDefaults(){
  try {
    // 先加载清账科目选项
    await loadClearingAccounts()
    
    // 恢复用户上次选择（只保留仍然有效的科目）
    const lastAccountCodes = localStorage.getItem('planner:lastAccountCodes')
    const lastBankAccountCode = localStorage.getItem('planner:lastBankAccountCode')
    if (lastAccountCodes) {
      const codes = JSON.parse(lastAccountCodes)
      if (Array.isArray(codes) && codes.length > 0) {
        // 过滤：只保留存在于有效选项中的科目
        const validCodes = codes.filter((c: string) => 
          clearingAccountOptions.some(opt => opt.value === c)
        )
        if (validCodes.length > 0) {
          form.accountCodes = validCodes
          // 如果有无效科目被过滤掉了，更新 localStorage
          if (validCodes.length !== codes.length) {
            localStorage.setItem('planner:lastAccountCodes', JSON.stringify(validCodes))
          }
          loadOpenItems()
        } else {
          // 所有恢复的科目都无效，清除 localStorage
          localStorage.removeItem('planner:lastAccountCodes')
        }
      }
    }
    if (lastBankAccountCode) {
      // 尝试加载银行科目到选项中
      await ensureBankAccountOption(lastBankAccountCode)
      // 验证科目是否确实存在于选项中
      if (bankAccountOptions.some(opt => opt.value === lastBankAccountCode)) {
        form.bankAccountCode = lastBankAccountCode
        await onBankAccountChange()
      } else {
        // 科目不存在，清除 localStorage
        localStorage.removeItem('planner:lastBankAccountCode')
      }
    }
  } catch {}

  // 加载公司设置的默认值
  try {
    const r = await api.post('/objects/company_setting/search', { 
      page: 1, pageSize: 1, where: [], orderBy: [{ field: 'created_at', dir: 'DESC' }] 
    })
    const rows = r.data?.data || []
    if (rows.length > 0) {
      const settings = rows[0]?.payload || {}
      if (settings.bankFeeAccountCode && !form.bankFeeAccountCode) {
        form.bankFeeAccountCode = settings.bankFeeAccountCode
        await ensureFeeAccountOption(settings.bankFeeAccountCode)
      }
    }
  } catch {}
}

async function ensureBankAccountOption(code: string){
  if (!code) return
  if (bankAccountOptions.some(opt => opt.value === code)) return
  try {
    const resp = await api.post('/objects/account/search', { 
      page: 1, pageSize: 1, 
      where: [{ field: 'account_code', op: 'eq', value: code }],
      orderBy: []
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    if (rows.length > 0) {
      const row = rows[0]
      const name = row.name || row.payload?.name || code
      bankAccountOptions.push({ label: `${name} (${code})`, value: code })
    }
  } catch {}
}

async function ensureFeeAccountOption(code: string){
  if (!code) return
  if (feeAccountOptions.some(opt => opt.value === code)) return
  try {
    const resp = await api.post('/objects/account/search', { 
      page: 1, pageSize: 1, 
      where: [{ field: 'account_code', op: 'eq', value: code }],
      orderBy: []
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    if (rows.length > 0) {
      const row = rows[0]
      const name = row.name || row.payload?.name || code
      feeAccountOptions.push({ label: `${name} (${code})`, value: code })
    }
  } catch {}
}

onMounted(() => {
  loadDefaults()
})
</script>

<style scoped>
.page { padding: 16px; }
.page-wide { max-width: 1400px; margin: 0 auto; }

.page-header { display: flex; align-items: center; justify-content: space-between; }
.page-header-left { display: flex; align-items: center; gap: 8px; }
.page-header-icon { font-size: 20px; color: var(--el-color-primary); }
.page-header-title { font-size: 16px; font-weight: 600; }

.rcpt-form-grid { margin-bottom: 16px; }
.rcpt-form-row { display: flex; flex-wrap: wrap; gap: 16px; margin-bottom: 12px; }
.rcpt-form-item { display: flex; align-items: center; gap: 8px; }
.rcpt-label { font-size: 13px; white-space: nowrap; min-width: 80px; }
.rcpt-label.required::before { content: '*'; color: var(--el-color-danger); margin-right: 4px; }

.rcpt-input-account { width: 240px; }
.rcpt-input-partner { width: 200px; }
.rcpt-input-amt { width: 140px; }
.rcpt-input-date-short { width: 140px; }
.rcpt-input-fee { width: 120px; }
.rcpt-input-fee-amt { width: 100px; }
.rcpt-currency-badge { 
  padding: 2px 8px; 
  background: var(--el-fill-color-light); 
  border-radius: 4px; 
  font-size: 12px; 
  color: var(--el-text-color-secondary); 
}

.planner-empty {
  padding: 32px;
  text-align: center;
  color: var(--el-text-color-secondary);
}

.planner-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid var(--el-border-color-lighter);
}

.planner-summary { font-size: 14px; }
.planner-fee-info { color: var(--el-text-color-secondary); }
.planner-warning { color: var(--el-color-warning); font-weight: 500; }

.planner-actions { display: flex; gap: 8px; }

.planner-message { margin-top: 12px; }
.planner-success { color: var(--el-color-success); }
.planner-error { color: var(--el-color-danger); }

.cell-remark {
  max-width: 150px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  display: block;
}

:deep(.row-selected) {
  background-color: var(--el-color-primary-light-9) !important;
}

.voucher-detail-dialog :deep(.el-dialog__header) { display: none; }
.voucher-dialog-card-wrap { min-width: 800px; }
.voucher-detail-embed { box-shadow: none; }
</style>

