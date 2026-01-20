<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Notebook /></el-icon>
            <span class="page-header-title">{{ accountsText.title }}</span>
            <el-tag v-if="rows.length > 0" size="small" type="info">{{ rows.length }}件</el-tag>
          </div>
          <div class="page-actions">
            <el-button type="primary" @click="openNewAccount">{{ accountsText.new }}</el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" stripe style="width: 100%" v-loading="loading">
        <el-table-column prop="payload.code" :label="accountsText.code" width="140" />
        <el-table-column prop="payload.name" :label="accountsText.name" min-width="220" />
        <el-table-column :label="accountsText.bspl" width="140">
          <template #default="{ row }">{{ categoryLabel(row.payload.category) }}</template>
        </el-table-column>
        <el-table-column :label="accountsText.tax" width="140">
          <template #default="{ row }">{{ taxLabel(row.payload.taxType) }}</template>
        </el-table-column>
        <el-table-column :label="accountsText.openItem" width="120">
          <template #default="{ row }">
            <el-tag size="small" :type="row.payload.openItem ? 'success' : 'info'">{{ row.payload.openItem ? commonText.enabled : commonText.disabled }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="accountsText.bankCash" width="140">
          <template #default="{ row }">
            <span v-if="row.payload.isBank">{{ accountsText.bank }}</span>
            <span v-else-if="row.payload.isCash">{{ accountsText.cash }}</span>
            <span v-else>{{ accountsText.none }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="accountsText.detail" width="100">
          <template #default="{ row }">
            <el-button text type="primary" @click="openDetail(row)">{{ commonText.view }}</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
  <el-dialog
    v-model="show"
    width="860px"
    :show-close="false"
    append-to-body
    destroy-on-close
    class="account-detail-dialog"
  >
    <template #header></template>
    <div class="account-dialog-card-wrap">
      <el-card class="account-detail-card">
        <template #header>
          <div class="account-detail-header">
            <span class="account-detail-title">{{ accountsText.detailDialog }}</span>
            <div class="header-actions">
              <el-button type="primary" size="small" :loading="saving" @click="triggerSave">{{ commonText.save }}</el-button>
              <el-popconfirm
                v-if="currentId"
                :title="commonText.deleteConfirm || 'この勘定科目を削除しますか？'"
                :confirm-button-text="commonText.delete || '削除'"
                confirm-button-type="danger"
                @confirm="confirmDelete"
              >
                <template #reference>
                  <el-button type="danger" size="small">{{ commonText.delete }}</el-button>
                </template>
              </el-popconfirm>
            </div>
          </div>
        </template>
        <AccountEditor
          v-if="show"
          ref="editorRef"
          mode="edit"
          :account-id="currentId"
          :initial="editForm"
          :show-actions="false"
          @saved="onSaved"
          @deleted="onDeleted"
          @cancel="show=false"
        />
      </el-card>
    </div>
  </el-dialog>
  <el-empty v-if="!loading && rows.length===0" :description="accountsText.listEmpty" />
</template>

<script setup lang="ts">
import { onMounted, ref, reactive, watch, computed, inject, nextTick } from 'vue'
import api from '../api'
import DynamicForm from '../components/DynamicForm.vue'
import BankBranchPicker from '../components/BankBranchPicker.vue'
import AccountEditor from '../components/AccountEditor.vue'
import { useI18n } from '../i18n'
import { Close, Notebook } from '@element-plus/icons-vue'
import { useRouter } from 'vue-router'
import { buildFsTreeOptions, defaultTreeSelectProps, type FsTreeOption } from '../utils/fsTree'

const { section, lang } = useI18n()
const router = useRouter()
const openEmbed = inject<(key: string, payload?: any) => void>('chatkitOpenEmbed', undefined)

const accountsText = section({
  title: '',
  new: '',
  code: '',
  name: '',
  bspl: '',
  tax: '',
  openItem: '',
  openItemBaseline: '',
  bankCash: '',
  fsBalanceGroup: '',
  fsProfitGroup: '',
  fsGroupSection: '',
  detail: '',
  detailDialog: '',
  open: '',
  close: '',
  bank: '',
  cash: '',
  none: '',
  listEmpty: '',
  bankDialog: '',
  branchDialog: '',
  fieldRuleSection: '',
  bankCashSection: '',
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
  enabled: '',
  disabled: '',
  view: '',
  save: '',
  delete: '',
  saved: '',
  saveFailed: '',
  close: '',
  loadFailed: '',
  backList: '',
  deleteConfirm: '',
  deleteSuccess: ''
}, (msg) => ({
  ...msg.common,
  deleteConfirm: msg.tables.accounts?.deleteConfirm || 'この勘定科目を削除しますか？',
  deleteSuccess: msg.tables.accounts?.deleteSuccess || '削除しました'
}))
const schemaText = section({ create: '', refresh: '', createTitle: '', loadFailed: '', layoutMissing: '' }, (msg) => msg.schemaList)
const taxMapText = section({ nonTax: '', input: '', output: '', account: '' }, (msg) => msg.tables.accounts?.taxMap)
const categoryMapText = section({ bs: '', pl: '' }, (msg) => msg.tables.accounts?.categoryMap)

const rows = ref<any[]>([])
const loading = ref(false)
const show = ref(false)
const saving = ref(false)
const msg = ref('')
const err = ref('')
const ui = ref<any>(null)
const uiSource = ref<any>(null)
const uiLoading = ref(false)
const uiError = ref('')
const currentId = ref<string>('')
const editorRef = ref<any>(null)
const editForm = reactive<any>({})
const showBank = ref(false)
const showBranch = ref(false)
const balanceGroupSource = ref<any[]>([])
const profitGroupSource = ref<any[]>([])
const balanceGroups = ref<FsTreeOption[]>([])
const profitGroups = ref<FsTreeOption[]>([])
const treeFieldProps = { ...defaultTreeSelectProps }
const groupsLoaded = ref(false)

const taxMap = computed(() => ({
  NON_TAX: taxMapText.value.nonTax,
  INPUT_TAX: taxMapText.value.input,
  OUTPUT_TAX: taxMapText.value.output,
  TAX_ACCOUNT: taxMapText.value.account
}))

const categoryMap = computed(() => ({
  BS: categoryMapText.value.bs,
  PL: categoryMapText.value.pl
}))

function openNewAccount(){
  if (openEmbed){
    openEmbed('account.new')
    return
  }
  router.push('/account/new')
}

function deepClone<T>(v: T): T { return JSON.parse(JSON.stringify(v)) }

function resetReactiveObject(target: Record<string, any>, source: Record<string, any>) {
  Object.keys(target).forEach((key) => {
    delete target[key]
  })
  Object.keys(source).forEach((key) => {
    target[key] = source[key]
  })
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

// 在 fieldRules section 中添加 assetId 字段（如果不存在）
function appendAssetIdField(doc: any) {
  if (!Array.isArray(doc?.form?.layout)) return
  
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
  
  // 检查是否已经存在 assetId 字段
  if (layoutContainsField(doc.form.layout, 'fieldRules.assetId')) return
  
  // 方法1: 找到包含其他 fieldRules 字段的 grid 并添加
  for (const block of doc.form.layout) {
    if (Array.isArray(block?.cols)) {
      const hasFieldRules = block.cols.some((col: any) => col.field?.startsWith('fieldRules.'))
      if (hasFieldRules) {
        block.cols.push(assetIdCol)
        return
      }
    }
    // 递归检查嵌套 layout
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
  
  // 方法2: 如果找不到 fieldRules section，在 "銀行 / 現金" section 前添加一个新的 section
  const bankCashIdx = doc.form.layout.findIndex((b: any) => b.type === 'section' && b.title?.includes('銀行'))
  if (bankCashIdx > 0) {
    doc.form.layout.splice(bankCashIdx, 0, {
      type: 'grid',
      cols: [assetIdCol]
    })
  }
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

  layout.splice(
    firstGridIndex,
    1,
    {
      type: 'grid',
      cols: firstRow
    },
    {
      type: 'grid',
      cols: secondRow
    }
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

function localizeBlock(block: any, idx: number) {
  if (block.type === 'section') {
    if (idx === 1) block.title = accountsText.value.fieldRuleSection
    if (idx === 2) block.title = accountsText.value.bankCashSection
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
    col.props.props = treeFieldProps
    col.props.clearable = true
    col.props.filterable = true
    col.props.renderAfterExpand = false
    col.label = accountsText.value.fsBalanceGroup
  }

  if (field === 'fsProfitGroup') {
    col.props = col.props || {}
    col.widget = 'el-tree-select'
    col.props.data = profitGroups.value
    col.props.props = treeFieldProps
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

  if (field === 'bankInfo.currency') {
    col.props = col.props || {}
    col.props.options = [
      { label: accountsText.value.currencyJpy, value: 'JPY' },
      { label: accountsText.value.currencyUsd, value: 'USD' },
      { label: accountsText.value.currencyCny, value: 'CNY' }
    ]
  }

  if (field === 'cashCurrency') {
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
      { label: categoryMap.value.BS, value: 'BS' },
      { label: categoryMap.value.PL, value: 'PL' }
    ]
  }

  if (field === 'isBank') {
    col.label = accountsText.value.bankAccountFlag
  }

  if (field === 'isCash') {
    col.label = accountsText.value.cashAccountFlag
  }
}

function normalizeAccountPayload(src: any) {
  const input = deepClone(src)

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
    // accept legacy/internal values too
    if (s === '普通' || s === '当座' || s === '貯蓄') return s
    if (s === 'Futsu' || s === 'ordinary') return '普通'
    if (s === 'Toza' || s === 'checking') return '当座'
    if (s === 'Chochiku' || s === 'savings') return '貯蓄'
    return '普通'
  }

  const sanitizedFieldRules: Record<string, string> = {}
  const allowedRuleKeys = ['customerId', 'vendorId', 'employeeId', 'departmentId', 'paymentDate', 'assetId']
  allowedRuleKeys.forEach((key) => {
    const val = input.fieldRules?.[key]
    if (val === 'required' || val === 'optional' || val === 'hidden') {
      sanitizedFieldRules[key] = val
    }
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
    if (info.zenginBranchCode) payload.bankInfo.zenginBranchCode = info.zenginBranchCode
    if (info.payrollCompanyCode) payload.bankInfo.payrollCompanyCode = info.payrollCompanyCode
    if (info.juminzeiCompanyCode) payload.bankInfo.juminzeiCompanyCode = info.juminzeiCompanyCode
  } else {
    payload.isBank = false
  }

  // isBank and isCash are mutually exclusive per schema
  if (isCash && !isBank) {
    payload.isCash = true
    payload.cashCurrency = input.cashCurrency || 'JPY'
  } else {
    payload.isCash = false
  }

  if (typeof input.fsBalanceGroup !== 'undefined') {
    payload.fsBalanceGroup = input.fsBalanceGroup || null
  }
  if (typeof input.fsProfitGroup !== 'undefined') {
    payload.fsProfitGroup = input.fsProfitGroup || null
  }

  return payload
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
    rebuildFsGroupLabels()
  } catch (e) {
    balanceGroups.value = []
    profitGroups.value = []
  }
}

function rebuildFsGroupLabels() {
  const langCode = lang.value
  balanceGroups.value = buildFsTreeOptions(balanceGroupSource.value, langCode)
  profitGroups.value = buildFsTreeOptions(profitGroupSource.value, langCode)
}

watch(() => lang.value, () => {
  if (groupsLoaded.value) rebuildFsGroupLabels()
})

async function openDetail(row: any) {
  currentId.value = row.id
  resetReactiveObject(editForm, deepClone(row.payload || {}))
  show.value = true
}

function onSaved(updated: any) {
  // keep list row in sync after edit
  const idx = rows.value.findIndex(x => x.id === updated?.id)
  if (idx >= 0) rows.value[idx] = updated
  // keep dialog open so user can see success/failure messages
}

async function triggerSave() {
  if (editorRef.value) {
    saving.value = true
    try {
      await editorRef.value.save()
    } finally {
      saving.value = false
    }
  }
}

async function confirmDelete() {
  if (editorRef.value) {
    await editorRef.value.deleteAccount()
  }
}

function onDeleted(id: string) {
  const idx = rows.value.findIndex(x => x.id === id)
  if (idx >= 0) rows.value.splice(idx, 1)
  show.value = false
}

// Allow ChatKit modal payload to open an account by code.
async function applyIntent(payload: any) {
  const code = (payload?.openAccountCode || payload?.accountCode || payload?.code || '').toString().trim()
  if (!code) return
  await ensureFsGroups()
  // Ensure rows are loaded
  if (!rows.value || rows.value.length === 0) {
    try {
      const r = await api.post('/objects/account/search', { page: 1, pageSize: 500, where: [], orderBy: [{ field: 'account_code', dir: 'ASC' }] })
      rows.value = Array.isArray(r.data?.data) ? r.data.data : []
    } catch {}
  }
  const match = rows.value.find((r: any) => {
    const c = (r?.account_code ?? r?.code ?? r?.payload?.code ?? r?.payload?.accountCode ?? '').toString().trim()
    return c === code
  })
  if (!match) return
  await openDetail(match)
  await nextTick()
}

defineExpose({ applyIntent })

onMounted(async () => {
  loading.value = true
  try {
    const r = await api.post('/objects/account/search', {
      page: 1,
      pageSize: 500,  // 确保能加载所有科目
      where: [],
      orderBy: [{ field: 'account_code', dir: 'ASC' }]
    })
    rows.value = Array.isArray(r.data?.data) ? r.data.data : []
  } finally {
    loading.value = false
  }
})

async function saveEdit() {
  if (!currentId.value) return
  saving.value = true
  msg.value = ''
  err.value = ''
  try {
    const normalized = normalizeAccountPayload(editForm)
    const body = { payload: normalized }
    const r = await api.put(`/objects/account/${currentId.value}`, body)
    const idx = rows.value.findIndex(x => x.id === currentId.value)
    if (idx >= 0) rows.value[idx] = r.data
    msg.value = commonText.value.saved
  } catch (e: any) {
    const baseErr = e?.response?.data?.error || e?.message || commonText.value.saveFailed
    const details = e?.response?.data?.details
    // include key payload fields to make schema mismatch diagnosable without devtools
    const sent = (() => {
      try {
        const p = normalizeAccountPayload(editForm)
        return {
          code: p.code,
          isBank: p.isBank,
          isCash: p.isCash,
          taxType: p.taxType,
          bankInfo: p.bankInfo
            ? { accountType: p.bankInfo.accountType, bankName: p.bankInfo.bankName, branchName: p.bankInfo.branchName, currency: p.bankInfo.currency }
            : null
        }
      } catch {
        return null
      }
    })()
    err.value = details
      ? `${baseErr}\n${JSON.stringify(details)}\nSENT=${JSON.stringify(sent)}`
      : `${baseErr}\nSENT=${JSON.stringify(sent)}`
  } finally {
    saving.value = false
  }
}

function categoryLabel(v: string) {
  return categoryMap.value[v] || v || accountsText.value.none
}

function taxLabel(v: string) {
  return taxMap.value[v] || v || accountsText.value.none
}

function onAction(name: string) {
  if (name === 'openBankPicker') showBank.value = true
  if (name === 'openBranchPicker' && editForm?.bankInfo?.bankCode) showBranch.value = true
}

function ensureBankInfo() {
  if (!editForm.bankInfo) editForm.bankInfo = {}
}

function onPickBank(row: any) {
  ensureBankInfo()
  editForm.bankInfo.bankCode = row.payload.bankCode
  // 显示格式：编号 名称
  editForm.bankInfo.bankName = `${row.payload.bankCode} ${row.payload.name}`
  delete editForm.bankInfo.branchCode
  delete editForm.bankInfo.branchName
  showBank.value = false
}

function onPickBranch(row: any) {
  ensureBankInfo()
  editForm.bankInfo.branchCode = row.payload.branchCode
  // 显示格式：编号 名称
  editForm.bankInfo.branchName = `${row.payload.branchCode} ${row.payload.branchName}`
  showBranch.value = false
}

watch(() => editForm.isBank, (val: boolean) => {
  if (val) {
    ensureBankInfo()
  } else {
    delete editForm.bankInfo
  }
})

watch(() => editForm.isCash, (val: boolean) => {
  if (!val) delete editForm.cashCurrency
})

watch(() => lang.value, () => {
  if (uiSource.value) {
    ui.value = transformUi(uiSource.value)
  }
})

</script>

<style scoped>
/* 标题区域样式 */
.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #67c23a;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

/* 表格表头样式 - 浅灰色风格 */
:deep(.el-table th.el-table__cell) {
  background-color: #f5f7fa !important;
  color: #303133 !important;
  font-weight: 600;
}

/* 卡片圆角 */
:deep(.el-card) {
  border-radius: 12px;
  overflow: hidden;
}

/* 科目详情弹窗样式 - 与凭证详情保持一致 */
.account-dialog-card-wrap {
  padding: 0;
  margin: 0;
}

.account-detail-card {
  max-width: 100%;
  margin: 0;
  border: none;
  box-shadow: none;
}

.account-detail-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 20px 24px;
  border-bottom: 1px solid var(--color-divider, #ebeef5);
}

.account-detail-title {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}
</style>

<style>
/* 科目详情弹窗 - 与凭证详情弹窗保持一致 */
.el-dialog.account-detail-dialog {
  background: transparent !important;
  box-shadow: none !important;
  border: none !important;
  padding: 0 !important;
}

.el-dialog.account-detail-dialog .el-dialog__header {
  display: none !important;
}

.el-dialog.account-detail-dialog .el-dialog__body {
  padding: 0 !important;
  background: transparent !important;
}

/* 覆盖全局 el-card__header 的 padding，让分隔线延伸到边缘 */
.account-detail-card.el-card .el-card__header {
  padding: 0 !important;
}

.account-detail-card.el-card .el-card__body {
  padding: 20px !important;
}
</style>