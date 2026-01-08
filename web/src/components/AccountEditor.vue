<template>
  <div class="account-editor">
    <div v-if="uiError" class="account-editor-error">
      <el-result icon="error" :title="commonText.loadFailed" :sub-title="uiError" />
    </div>

    <div v-else>
      <div v-if="!uiLoading && ui" class="account-editor-body">
        <DynamicForm :ui="ui" :model="model" @action="onAction" />

        <div class="editor-actions" v-if="showActions">
          <div class="editor-buttons">
            <el-button type="primary" :loading="saving" @click="save">{{ commonText.save }}</el-button>
            <el-button @click="cancel">{{ commonText.close }}</el-button>
          </div>
          <div class="editor-messages">
            <span v-if="msg" class="text-success">{{ msg }}</span>
            <span v-if="err" class="text-error">{{ err }}</span>
          </div>
        </div>
      </div>

      <div v-else>
        <el-skeleton :rows="6" animated />
      </div>
    </div>

    <!-- 银行/支店选择弹窗 -->
    <el-dialog v-model="showBank" :title="accountsText.bankDialog" width="720px" append-to-body destroy-on-close>
      <BankBranchPicker mode="bank" @select="onPickBank" @cancel="showBank=false" />
    </el-dialog>
    <el-dialog v-model="showBranch" :title="accountsText.branchDialog" width="720px" append-to-body destroy-on-close>
      <BankBranchPicker mode="branch" :bank-code="model?.bankInfo?.bankCode" @select="onPickBranch" @cancel="showBranch=false" />
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref, watch, computed } from 'vue'
import api from '../api'
import DynamicForm from './DynamicForm.vue'
import BankBranchPicker from './BankBranchPicker.vue'
import { getLang, useI18n } from '../i18n'
import { buildFsTreeOptions, defaultTreeSelectProps, type FsTreeOption } from '../utils/fsTree'

type Mode = 'create' | 'edit'

const props = withDefaults(defineProps<{
  mode: Mode
  accountId?: string
  initial?: any
  showActions?: boolean
}>(), {
  showActions: true
})

const emit = defineEmits<{
  (e: 'saved', value: any): void
  (e: 'cancel'): void
}>()

const { section, lang } = useI18n()

const accountsText = section({
  bankDialog: '',
  branchDialog: '',
  fieldRuleSection: '',
  bankCashSection: '',
  fsGroupSection: '',
  code: '',
  name: '',
  bspl: '',
  tax: '',
  openItem: '',
  openItemBaseline: '',
  fsBalanceGroup: '',
  fsProfitGroup: '',
  customerRule: '',
  vendorRule: '',
  employeeRule: '',
  departmentRule: '',
  paymentDateRule: '',
  assetRule: '',
  fieldRuleRequired: '',
  fieldRuleOptional: '',
  fieldRuleHidden: '',
  baselineNone: '',
  baselineCustomer: '',
  baselineVendor: '',
  baselineEmployee: '',
  taxOptionNonTax: '',
  taxOptionInput: '',
  taxOptionOutput: '',
  taxOptionAccount: '',
  bankAccountFlag: '',
  cashAccountFlag: '',
  selectBankButton: '',
  selectBranchButton: '',
  bankName: '',
  branchName: '',
  accountType: '',
  accountTypeOrdinary: '',
  accountTypeChecking: '',
  accountNo: '',
  holder: '',
  currency: '',
  currencyJpy: '',
  currencyUsd: '',
  currencyCny: '',
  cashCurrency: ''
}, (msg) => msg.tables.accounts)

const commonText = section({
  save: '',
  close: '',
  saved: '',
  saveFailed: '',
  loadFailed: ''
}, (msg) => msg.common)

const categoryMapText = section({ bs: '', pl: '' }, (msg) => msg.tables.accounts?.categoryMap)
const categoryMap = computed(() => ({
  BS: categoryMapText.value.bs,
  PL: categoryMapText.value.pl
}))

const model = reactive<any>({})
const saving = ref(false)
const msg = ref('')
const err = ref('')
const ui = ref<any>(null)
const uiSource = ref<any>(null)
const uiLoading = ref(false)
const uiError = ref('')
const showBank = ref(false)
const showBranch = ref(false)

const balanceGroupSource = ref<any[]>([])
const profitGroupSource = ref<any[]>([])
const balanceGroups = ref<FsTreeOption[]>([])
const profitGroups = ref<FsTreeOption[]>([])
const treeFieldProps = { ...defaultTreeSelectProps }
const groupsLoaded = ref(false)

function deepClone<T>(v: T): T { return JSON.parse(JSON.stringify(v)) }
function resetReactiveObject(target: Record<string, any>, source: Record<string, any>) {
  Object.keys(target).forEach((key) => { delete target[key] })
  Object.keys(source).forEach((key) => { target[key] = source[key] })
}

function layoutContainsField(blocks: any[], field: string): boolean {
  if (!Array.isArray(blocks)) return false
  return blocks.some((item) => {
    if (item?.field === field) return true
    if (Array.isArray(item?.cols) && layoutContainsField(item.cols, field)) return true
    if (Array.isArray(item?.layout) && layoutContainsField(item.layout, field)) return true
    return false
  })
}

function appendFsGroupFields(doc: any) {
  if (!doc?.form) doc.form = {}
  doc.form.layout = Array.isArray(doc.form.layout) ? doc.form.layout : []
  if (!layoutContainsField(doc.form.layout, 'fsBalanceGroup')) {
    doc.form.layout.push({
      type: 'section',
      title: accountsText.value.fsGroupSection || '財務諸表グループ',
      layout: [
        {
          type: 'grid',
          cols: [
            { field: 'fsBalanceGroup', span: 12, widget: 'select' },
            { field: 'fsProfitGroup', span: 12, widget: 'select' }
          ]
        }
      ]
    })
  }
}

function appendAssetIdField(doc: any) {
  if (!Array.isArray(doc?.form?.layout)) return
  if (layoutContainsField(doc.form.layout, 'fieldRules.assetId')) return

  const assetIdCol = {
    field: 'fieldRules.assetId',
    label: accountsText.value.assetRule || '固定資産入力制御',
    span: 6,
    widget: 'select',
    props: {
      options: [
        { label: accountsText.value.fieldRuleRequired || '必須', value: 'required' },
        { label: accountsText.value.fieldRuleOptional || '任意', value: 'optional' },
        { label: accountsText.value.fieldRuleHidden || '非表示', value: 'hidden' }
      ]
    }
  }

  for (const block of doc.form.layout) {
    if (Array.isArray(block?.cols)) {
      const hasFieldRules = block.cols.some((col: any) => col.field?.startsWith('fieldRules.'))
      if (hasFieldRules) {
        block.cols.push(assetIdCol)
        return
      }
    }
    if (Array.isArray(block?.layout)) {
      for (const child of block.layout) {
        if (Array.isArray(child?.cols)) {
          const hasFieldRules = child.cols.some((col: any) => col.field?.startsWith('fieldRules.'))
          if (hasFieldRules) {
            child.cols.push(assetIdCol)
            return
          }
        }
      }
    }
  }

  const bankCashIdx = doc.form.layout.findIndex((b: any) => b.type === 'section' && (b.title?.includes('銀行') || b.title?.includes('bank')))
  if (bankCashIdx >= 0) {
    doc.form.layout.splice(bankCashIdx, 0, { type: 'grid', cols: [assetIdCol] })
  } else {
    doc.form.layout.push({ type: 'grid', cols: [assetIdCol] })
  }
}

function reorderPrimaryLayout(doc: any) {
  if (!Array.isArray(doc?.form?.layout)) return
  const layout = doc.form.layout
  const firstGridIndex = layout.findIndex((block: any) => Array.isArray(block?.cols))
  if (firstGridIndex === -1) return
  const block = layout[firstGridIndex]
  const getCol = (field: string) => block.cols?.find((col: any) => col.field === field)
  const required = ['code', 'name', 'category', 'taxType', 'openItem', 'openItemBaseline']
  if (!required.every((field) => !!getCol(field))) return

  const cloneCol = (field: string, span: number) => {
    const col = getCol(field)
    if (!col) return null
    const copy = deepClone(col)
    copy.span = span
    return copy
  }

  const firstRow = [cloneCol('code', 6), cloneCol('name', 8), cloneCol('category', 10)].filter(Boolean)
  const secondRow = [cloneCol('taxType', 8), cloneCol('openItem', 6), cloneCol('openItemBaseline', 10)].filter(Boolean)
  if (!firstRow.length || !secondRow.length) return

  layout.splice(firstGridIndex, 1,
    { type: 'grid', cols: firstRow },
    { type: 'grid', cols: secondRow }
  )
}

const fieldLabelMap: Record<string, () => string> = {
  code: () => accountsText.value.code,
  name: () => accountsText.value.name,
  category: () => accountsText.value.bspl,
  taxType: () => accountsText.value.tax,
  openItem: () => accountsText.value.openItem,
  openItemBaseline: () => accountsText.value.openItemBaseline,
  fsBalanceGroup: () => accountsText.value.fsBalanceGroup,
  fsProfitGroup: () => accountsText.value.fsProfitGroup,
  'fieldRules.customerId': () => accountsText.value.customerRule,
  'fieldRules.vendorId': () => accountsText.value.vendorRule,
  'fieldRules.employeeId': () => accountsText.value.employeeRule,
  'fieldRules.departmentId': () => accountsText.value.departmentRule,
  'fieldRules.paymentDate': () => accountsText.value.paymentDateRule,
  'fieldRules.assetId': () => accountsText.value.assetRule,
  'bankInfo.bankName': () => accountsText.value.bankName,
  'bankInfo.branchName': () => accountsText.value.branchName,
  'bankInfo.accountType': () => accountsText.value.accountType,
  'bankInfo.accountNo': () => accountsText.value.accountNo,
  'bankInfo.holder': () => accountsText.value.holder,
  'bankInfo.currency': () => accountsText.value.currency,
  cashCurrency: () => accountsText.value.cashCurrency
}

function hasFieldRulesFields(block: any): boolean {
  if (!Array.isArray(block?.layout)) return false
  return block.layout.some((child: any) =>
    Array.isArray(child?.cols) && child.cols.some((col: any) => col.field?.startsWith('fieldRules.'))
  )
}

function hasBankCashFields(block: any): boolean {
  if (!Array.isArray(block?.layout)) return false
  return block.layout.some((child: any) =>
    Array.isArray(child?.cols) && child.cols.some((col: any) =>
      col.field === 'isBank' || col.field === 'isCash' || col.field?.startsWith('bankInfo.')
    )
  )
}

function localizeBlock(block: any, idx: number) {
  if (block.type === 'section') {
    // 根据内容识别分组类型，而不是索引
    if (hasFieldRulesFields(block)) {
      block.title = accountsText.value.fieldRuleSection
    } else if (hasBankCashFields(block)) {
      block.title = accountsText.value.bankCashSection
    }
  }
  if (Array.isArray(block.cols)) block.cols.forEach(localizeCol)
  if (Array.isArray(block.layout)) block.layout.forEach((child: any, childIdx: number) => localizeBlock(child, childIdx))
}

function localizeCol(col: any) {
  const field = col.field as string | undefined
  if (field && fieldLabelMap[field]) col.label = fieldLabelMap[field]()

  if (col.widget === 'button' && col.props) {
    if (col.props.action === 'openBankPicker') col.props.text = accountsText.value.selectBankButton
    if (col.props.action === 'openBranchPicker') col.props.text = accountsText.value.selectBranchButton
  }

  if (field === 'taxType') {
    col.props = col.props || {}
    col.props.options = [
      { label: accountsText.value.taxOptionNonTax, value: 'NON_TAX' },
      { label: accountsText.value.taxOptionInput, value: 'INPUT_TAX' },
      { label: accountsText.value.taxOptionOutput, value: 'OUTPUT_TAX' },
      { label: accountsText.value.taxOptionAccount, value: 'TAX_ACCOUNT' }
    ]
  }

  if (field === 'openItemBaseline') {
    col.props = col.props || {}
    col.props.options = [
      { label: accountsText.value.baselineNone, value: 'NONE' },
      { label: accountsText.value.baselineCustomer, value: 'CUSTOMER' },
      { label: accountsText.value.baselineVendor, value: 'VENDOR' },
      { label: accountsText.value.baselineEmployee, value: 'EMPLOYEE' }
    ]
  }

  if (field === 'fsBalanceGroup') {
    col.props = col.props || {}
    col.widget = 'el-tree-select'
    col.props.data = balanceGroups.value
    col.props.props = { ...treeFieldProps }
    col.props.clearable = true
    col.props.filterable = true
    col.props.renderAfterExpand = false
    col.label = accountsText.value.fsBalanceGroup
  }
  if (field === 'fsProfitGroup') {
    col.props = col.props || {}
    col.widget = 'el-tree-select'
    col.props.data = profitGroups.value
    col.props.props = { ...treeFieldProps }
    col.props.clearable = true
    col.props.filterable = true
    col.props.renderAfterExpand = false
    col.label = accountsText.value.fsProfitGroup
  }

  if (field && field.startsWith('fieldRules.')) {
    col.props = col.props || {}
    col.props.options = [
      { label: accountsText.value.fieldRuleRequired, value: 'required' },
      { label: accountsText.value.fieldRuleOptional, value: 'optional' },
      { label: accountsText.value.fieldRuleHidden, value: 'hidden' }
    ]
  }

  if (field === 'bankInfo.accountType') {
    col.props = col.props || {}
    col.props.options = [
      { label: accountsText.value.accountTypeOrdinary, value: '普通' },
      { label: accountsText.value.accountTypeChecking, value: '当座' }
    ]
  }

  if (field === 'bankInfo.currency' || field === 'cashCurrency') {
    col.props = col.props || {}
    col.props.options = [
      { label: accountsText.value.currencyJpy, value: 'JPY' },
      { label: accountsText.value.currencyUsd, value: 'USD' },
      { label: accountsText.value.currencyCny, value: 'CNY' }
    ]
  }

  if (field === 'category') {
    col.props = col.props || {}
    col.props.options = [
      { label: categoryMap.value.BS || '貸借対照表科目', value: 'BS' },
      { label: categoryMap.value.PL || '損益計算書科目', value: 'PL' }
    ]
  }

  if (field === 'isBank') col.label = accountsText.value.bankAccountFlag
  if (field === 'isCash') col.label = accountsText.value.cashAccountFlag
}

function transformUi(source: any) {
  const doc = deepClone(source || {})
  appendFsGroupFields(doc)
  appendAssetIdField(doc)
  reorderPrimaryLayout(doc)
  if (Array.isArray(doc.form?.layout)) {
    doc.form.layout.forEach((block: any, idx: number) => localizeBlock(block, idx))
  }
  return doc
}

async function ensureFsGroups() {
  if (groupsLoaded.value) return
  groupsLoaded.value = true
  try {
    const [bs, pl] = await Promise.all([
      api.get('/financial/fs-nodes', { params: { statement: 'BS' } }),
      api.get('/financial/fs-nodes', { params: { statement: 'PL' } })
    ])
    balanceGroupSource.value = Array.isArray(bs.data) ? bs.data : []
    profitGroupSource.value = Array.isArray(pl.data) ? pl.data : []
    balanceGroups.value = buildFsTreeOptions(balanceGroupSource.value, lang.value)
    profitGroups.value = buildFsTreeOptions(profitGroupSource.value, lang.value)
  } catch {
    balanceGroups.value = []
    profitGroups.value = []
  }
}

function toBool(v: any): boolean {
  if (v === true) return true
  if (v === false) return false
  if (typeof v === 'number') return v !== 0
  if (typeof v === 'string') {
    const s = v.trim().toLowerCase()
    if (s === 'true' || s === '1' || s === 'yes') return true
    if (s === 'false' || s === '0' || s === 'no' || s === '') return false
  }
  return !!v
}

function normalizeBankAccountType(v: any): string {
  const s = (v ?? '').toString().trim()
  if (!s) return '普通'
  if (s === '普通' || s === '当座' || s === '貯蓄') return s
  if (s === 'Futsu' || s === 'ordinary') return '普通'
  if (s === 'Toza' || s === 'checking') return '当座'
  if (s === 'Chochiku' || s === 'savings') return '貯蓄'
  return '普通'
}

function normalizeAccountPayload(src: any) {
  const input = deepClone(src)

  const sanitizedFieldRules: Record<string, string> = {}
  const allowedRuleKeys = ['customerId', 'vendorId', 'employeeId', 'departmentId', 'paymentDate', 'assetId']
  allowedRuleKeys.forEach((key) => {
    const val = input.fieldRules?.[key]
    if (val === 'required' || val === 'optional' || val === 'hidden') sanitizedFieldRules[key] = val
  })

  const payload: any = {
    code: input.code,
    name: input.name,
    category: input.category || 'PL',
    openItem: !!input.openItem,
    openItemBaseline: input.openItem ? (input.openItemBaseline || 'NONE') : 'NONE',
    taxType: input.taxType || 'NON_TAX',
    fieldRules: sanitizedFieldRules
  }

  const isBank = toBool(input.isBank)
  const isCash = toBool(input.isCash)

  if (isBank) {
    const info = input.bankInfo || {}
    payload.isBank = true
    payload.bankInfo = {
      bankName: info.bankName || '',
      branchName: info.branchName || '',
      accountType: normalizeBankAccountType(info.accountType),
      accountNo: info.accountNo || '',
      holder: info.holder || '',
      currency: info.currency || 'JPY'
    }
  } else {
    payload.isBank = false
  }

  if (isCash && !isBank) {
    payload.isCash = true
    payload.cashCurrency = input.cashCurrency || 'JPY'
  } else {
    payload.isCash = false
  }

  if (typeof input.fsBalanceGroup !== 'undefined') payload.fsBalanceGroup = input.fsBalanceGroup || null
  if (typeof input.fsProfitGroup !== 'undefined') payload.fsProfitGroup = input.fsProfitGroup || null

  return payload
}

async function loadUi() {
  uiLoading.value = true
  uiError.value = ''
  try {
    const r = await api.get('/schemas/account', { params: { lang: getLang(), _: Date.now() } })
    uiSource.value = r.data?.ui || null
    await ensureFsGroups()
    ui.value = transformUi(uiSource.value)
  } catch (e: any) {
    uiError.value = e?.message || commonText.value.loadFailed
  } finally {
    uiLoading.value = false
  }
}

function initDefaultsIfNeeded() {
  if (typeof model.code === 'undefined') model.code = ''
  if (typeof model.name === 'undefined') model.name = ''
  if (typeof model.category === 'undefined') model.category = 'PL'
  if (typeof model.taxType === 'undefined') model.taxType = 'NON_TAX'
  if (typeof model.openItem === 'undefined') model.openItem = false
  if (typeof model.openItemBaseline === 'undefined') model.openItemBaseline = 'NONE'
  if (typeof model.isBank === 'undefined') model.isBank = false
  if (typeof model.isCash === 'undefined') model.isCash = false
}

watch(() => props.initial, async (v) => {
  if (v) resetReactiveObject(model, deepClone(v))
  initDefaultsIfNeeded()
  // 如果有银行信息但缺少名称，则获取银行和支店名称
  await fetchBankBranchNames()
}, { immediate: true })

async function fetchBankBranchNames() {
  if (!model.bankInfo) return
  
  // 提取银行编号：优先使用 bankCode，否则从 bankName 中提取（如果它只是编号）
  let bankCode = model.bankInfo.bankCode
  if (!bankCode && model.bankInfo.bankName && /^\d{4}$/.test(model.bankInfo.bankName.trim())) {
    bankCode = model.bankInfo.bankName.trim()
    model.bankInfo.bankCode = bankCode
  }
  
  // 提取支店编号：优先使用 branchCode，否则从 branchName 中提取（如果它只是编号）
  let branchCode = model.bankInfo.branchCode
  if (!branchCode && model.bankInfo.branchName && /^\d{3}$/.test(model.bankInfo.branchName.trim())) {
    branchCode = model.bankInfo.branchName.trim()
    model.bankInfo.branchCode = branchCode
  }

  // 如果有银行编码但名称只是编码或不包含名称，则获取银行名称
  if (bankCode && (!model.bankInfo.bankName || /^\d{4}$/.test(model.bankInfo.bankName.trim()) || !model.bankInfo.bankName.includes(' '))) {
    try {
      const res = await api.post('/objects/bank/search', {
        page: 1,
        pageSize: 1,
        where: [{ json: 'payload.bankCode', op: 'eq', value: bankCode }],
        orderBy: []
      })
      const items = res.data?.data || []
      if (items.length > 0) {
        const bank = items[0]
        model.bankInfo.bankName = `${bank.payload?.bankCode || bankCode} ${bank.payload?.name || ''}`
      }
    } catch { /* ignore */ }
  }

  // 如果有支店编码但名称只是编码或不包含名称，则获取支店名称
  if (bankCode && branchCode && (!model.bankInfo.branchName || /^\d{3}$/.test(model.bankInfo.branchName.trim()) || !model.bankInfo.branchName.includes(' '))) {
    try {
      const res = await api.post('/objects/branch/search', {
        page: 1,
        pageSize: 1,
        where: [
          { json: 'payload.bankCode', op: 'eq', value: bankCode },
          { json: 'payload.branchCode', op: 'eq', value: branchCode }
        ],
        orderBy: []
      })
      const items = res.data?.data || []
      if (items.length > 0) {
        const branch = items[0]
        model.bankInfo.branchName = `${branch.payload?.branchCode || branchCode} ${branch.payload?.branchName || ''}`
      }
    } catch { /* ignore */ }
  }
}

watch(() => model.isBank, (val: any) => {
  if (toBool(val)) {
    model.isCash = false
    model.bankInfo = model.bankInfo || {}
  } else {
    delete model.bankInfo
  }
})

watch(() => model.isCash, (val: any) => {
  if (toBool(val)) {
    model.isBank = false
  } else {
    if (model.cashCurrency) delete model.cashCurrency
  }
})

function onAction(name: string) {
  if (name === 'openBankPicker') showBank.value = true
  if (name === 'openBranchPicker' && model?.bankInfo?.bankCode) showBranch.value = true
}

function onPickBank(row: any) {
  model.bankInfo = model.bankInfo || {}
  model.bankInfo.bankCode = row.payload.bankCode
  model.bankInfo.bankName = `${row.payload.bankCode} ${row.payload.name}`
  delete model.bankInfo.branchCode
  delete model.bankInfo.branchName
  showBank.value = false
}

function onPickBranch(row: any) {
  model.bankInfo = model.bankInfo || {}
  model.bankInfo.branchCode = row.payload.branchCode
  model.bankInfo.branchName = `${row.payload.branchCode} ${row.payload.branchName}`
  showBranch.value = false
}

async function save() {
  saving.value = true
  msg.value = ''
  err.value = ''
  try {
    const normalized = normalizeAccountPayload(model)
    const body = { payload: normalized }
    const res = props.mode === 'edit' && props.accountId
      ? await api.put(`/objects/account/${props.accountId}`, body)
      : await api.post('/objects/account', body)
    msg.value = commonText.value.saved
    emit('saved', res.data)
  } catch (e: any) {
    const baseErr = e?.response?.data?.error || e?.message || commonText.value.saveFailed
    const details = e?.response?.data?.details
    const sent = (() => {
      try {
        const p = normalizeAccountPayload(model)
        return {
          code: p.code,
          isBank: p.isBank,
          isCash: p.isCash,
          taxType: p.taxType,
          bankInfo: p.bankInfo
            ? { accountType: p.bankInfo.accountType, bankName: p.bankInfo.bankName, branchName: p.bankInfo.branchName, currency: p.bankInfo.currency }
            : null
        }
      } catch { return null }
    })()
    err.value = details
      ? `${baseErr}\n${JSON.stringify(details)}\nSENT=${JSON.stringify(sent)}`
      : `${baseErr}\nSENT=${JSON.stringify(sent)}`
  } finally {
    saving.value = false
  }
}

function cancel() {
  emit('cancel')
}

onMounted(async () => {
  await loadUi()
  initDefaultsIfNeeded()
})
</script>

<style scoped>
.account-editor-body {
  padding: 4px 0;
}
.editor-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding-top: 10px;
}
.editor-buttons {
  display: flex;
  gap: 10px;
}
.editor-messages {
  flex: 1;
  text-align: left;
}
.text-success { color: #2e7d32; }
.text-error { color: #d32f2f; white-space: pre-wrap; }
</style>


