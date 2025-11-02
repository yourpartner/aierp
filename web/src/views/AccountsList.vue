<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ accountsText.title }}</div>
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
  <el-dialog v-model="show" :title="accountsText.detail" width="860px" append-to-body destroy-on-close>
    <template v-if="uiError">
      <el-result icon="error" :title="commonText.loadFailed" :sub-title="uiError" />
    </template>
    <template v-else>
      <div v-if="!uiLoading && ui" class="accounts-dialog-content">
        <DynamicForm :ui="ui" :model="editForm" @action="onAction" />
        <div class="dialog-actions">
          <div class="dialog-buttons">
            <el-button type="primary" :loading="saving" @click="saveEdit">{{ commonText.save }}</el-button>
            <el-button @click="show=false">{{ commonText.close }}</el-button>
          </div>
          <div class="dialog-messages">
            <span v-if="msg" class="text-success">{{ msg }}</span>
            <span v-if="err" class="text-error">{{ err }}</span>
          </div>
        </div>
      </div>
      <div v-else>
        <el-skeleton :rows="6" animated />
      </div>
    </template>
  </el-dialog>
  <!-- 银行/支店选择弹窗（用于编辑弹窗内） -->
  <el-dialog v-model="showBank" :title="accountsText.bankDialog" width="720px" append-to-body destroy-on-close>
    <BankBranchPicker mode="bank" @select="onPickBank" @cancel="showBank=false" />
  </el-dialog>
  <el-dialog v-model="showBranch" :title="accountsText.branchDialog" width="720px" append-to-body destroy-on-close>
    <BankBranchPicker mode="branch" :bank-code="editForm?.bankInfo?.bankCode" @select="onPickBranch" @cancel="showBranch=false" />
  </el-dialog>
  <el-empty v-if="!loading && rows.length===0" :description="accountsText.listEmpty" />
</template>

<script setup lang="ts">
import { onMounted, ref, reactive, watch, computed, inject } from 'vue'
import api from '../api'
import DynamicForm from '../components/DynamicForm.vue'
import BankBranchPicker from '../components/BankBranchPicker.vue'
import { useI18n } from '../i18n'
import { Close } from '@element-plus/icons-vue'
import { useRouter } from 'vue-router'

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
  detail: '',
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
  saved: '',
  saveFailed: '',
  close: '',
  loadFailed: '',
  backList: ''
}, (msg) => msg.common)
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
const editForm = reactive<any>({})
const showBank = ref(false)
const showBranch = ref(false)

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

function transformUi(source: any) {
  const doc = deepClone(source || {})
  if (Array.isArray(doc.form?.layout)) {
    doc.form.layout.forEach((block: any, idx: number) => localizeBlock(block, idx))
  }
  return doc
}

const fieldLabelMap: Record<string, () => string> = {
  code: () => accountsText.value.code,
  name: () => accountsText.value.name,
  category: () => accountsText.value.bspl,
  taxType: () => accountsText.value.tax,
  openItem: () => accountsText.value.openItem,
  openItemBaseline: () => accountsText.value.openItemBaseline,
  'fieldRules.customerId': () => accountsText.value.customerRule,
  'fieldRules.vendorId': () => accountsText.value.vendorRule,
  'fieldRules.employeeId': () => accountsText.value.employeeRule,
  'fieldRules.departmentId': () => accountsText.value.departmentRule,
  'fieldRules.paymentDate': () => accountsText.value.paymentDateRule,
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

  const sanitizedFieldRules: Record<string, string> = {}
  const allowedRuleKeys = ['customerId', 'vendorId', 'employeeId', 'departmentId', 'paymentDate']
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

  if (input.isBank) {
    const info = input.bankInfo || {}
    payload.isBank = true
    payload.bankInfo = {
      bankName: info.bankName || '',
      branchName: info.branchName || '',
      accountType: info.accountType || '普通',
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

  if (input.isCash) {
    payload.isCash = true
    payload.cashCurrency = input.cashCurrency || 'JPY'
  } else {
    payload.isCash = false
  }

  return payload
}

async function openDetail(row: any) {
  msg.value = ''
  err.value = ''
  currentId.value = row.id
  if (!uiSource.value) {
    uiLoading.value = true
    uiError.value = ''
    try {
      const r = await api.get('/schemas/account', { params: { lang: lang.value } })
      uiSource.value = r.data?.ui || null
      ui.value = uiSource.value ? transformUi(uiSource.value) : null
      if (!ui.value) uiError.value = schemaText.value.layoutMissing
    } catch (e: any) {
      uiError.value = e?.response?.data?.error || e?.message || schemaText.value.loadFailed
    } finally {
      uiLoading.value = false
    }
  } else {
    ui.value = transformUi(uiSource.value)
  }
  Object.assign(editForm, deepClone(row.payload || {}))
  show.value = true
}

onMounted(async () => {
  loading.value = true
  try {
    const r = await api.post('/objects/account/search', {
      page: 1,
      where: [],
      orderBy: []
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
    const body = { payload: normalizeAccountPayload(editForm) }
    const r = await api.put(`/objects/account/${currentId.value}`, body)
    const idx = rows.value.findIndex(x => x.id === currentId.value)
    if (idx >= 0) rows.value[idx] = r.data
    msg.value = commonText.value.saved
  } catch (e: any) {
    err.value = e?.response?.data?.error || e?.message || commonText.value.saveFailed
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
  editForm.bankInfo.bankName = row.payload.name
  delete editForm.bankInfo.branchCode
  delete editForm.bankInfo.branchName
  showBank.value = false
}

function onPickBranch(row: any) {
  ensureBankInfo()
  editForm.bankInfo.branchCode = row.payload.branchCode
  editForm.bankInfo.branchName = row.payload.branchName
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