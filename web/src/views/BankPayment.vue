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

        <!-- 第二行：出金信息 -->
        <div class="rcpt-form-row">
          <div class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.bankAccount }}</label>
            <el-select v-model="form.bankAccountCode" filterable remote :remote-method="searchBankAccounts" reserve-keyword :placeholder="labels.bankPlaceholder" class="rcpt-input-account" @change="onBankAccountChange">
              <el-option v-for="a in bankAccountOptions" :key="a.value" :label="a.label" :value="a.value" />
            </el-select>
            <span v-if="form.bankAccountCode" class="rcpt-currency-badge">{{ form.currency || 'JPY' }}</span>
          </div>
          <div class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.paymentAmount }}</label>
            <el-input v-model="form.amountText" class="rcpt-input-amt" :placeholder="labels.amount" />
          </div>
          <div class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.paymentDate }}</label>
            <el-date-picker v-model="form.paymentDate" class="rcpt-input-date-short" type="date" value-format="YYYY-MM-DD" :placeholder="labels.date" />
          </div>
        </div>

        <!-- 第三行：手数料 -->
        <div class="rcpt-form-row">
          <div class="rcpt-form-item">
            <label class="rcpt-label required">{{ labels.feeBearer }}</label>
            <el-select v-model="form.bankFeeBearer" class="rcpt-input-fee" :placeholder="labels.bearer">
              <el-option :label="labels.vendorBears" value="vendor" />
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
        <el-table-column :label="labels.docDate" width="100" header-align="center" align="center">
          <template #default="{ row }">{{ row.doc_date?.slice(0,10) || '-' }}</template>
        </el-table-column>
        <el-table-column :label="labels.account" width="160" header-align="left" align="left">
          <template #default="{ row }">
            <span :title="row.account_code">{{ row.accountName ? `${row.accountName} (${row.account_code})` : row.account_code || '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="labels.voucherNo" width="130" header-align="left" align="left">
          <template #default="{ row }">
            <span v-if="row.voucher_no" class="voucher-link" @click="openVoucherDetail(row)">{{ row.voucher_no }}</span>
            <span v-else-if="row.voucherId" class="voucher-link" @click="openVoucherDetail(row)">{{ row.voucherId.slice(0, 8) }}...</span>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column :label="labels.originalAmount" width="120" header-align="right" align="right">
          <template #default="{ row }">
            <span :class="{ 'amount-debit': row.isDebit }">{{ row.isDebit ? '-' : '' }}{{ formatAmount(row.original_amount) }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="labels.residualAmount" width="120" header-align="right" align="right">
          <template #default="{ row }">
            <span :class="{ 'amount-debit': row.isDebit }">{{ row.isDebit ? '-' : '' }}{{ formatAmount(row.residual_amount) }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="labels.applyAmount" width="130" header-align="right" align="right">
          <template #default="{ row }">
            <el-input-number v-model="row.apply" :min="0" :max="row.residual_amount" :step="100" :precision="0" controls-position="right" size="small" @change="onApplyChange(row)" />
          </template>
        </el-table-column>
        <el-table-column :label="labels.remark" min-width="160" header-align="left" align="left">
          <template #default="{ row }">{{ row.remark || '-' }}</template>
        </el-table-column>
      </el-table>

      <!-- 底部汇总和操作 -->
      <div class="planner-footer">
        <div class="planner-summary">
          {{ labels.clearingTotal }}：{{ formatAmount(sumApply()) }} / {{ labels.actualPayment }}：{{ formatAmount(actualPaymentAmount()) }}
          <span v-if="form.bankFeeBearer==='company' && bankFeeAmount()>0" class="planner-fee-info">　{{ labels.fee }}：{{ formatAmount(bankFeeAmount()) }}（{{ labels.companyBears }}）</span>
          <span v-if="!amountsEqual(sumApply(), targetAmount())" class="planner-warning">（{{ labels.mismatch }}）</span>
        </div>
        <div class="planner-actions">
          <el-button type="primary" :disabled="!canCommit()" :loading="committing" @click="submitPayment">{{ labels.execute }}</el-button>
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
import { ref, reactive, watch, nextTick } from 'vue'
import { useRouter } from 'vue-router'
import { Coin } from '@element-plus/icons-vue'
import api from '../api'
import { useI18n } from '../i18n'
import VouchersList from './VouchersList.vue'

const router = useRouter()

// 伝票详情弹窗
const voucherDialogVisible = ref(false)
const voucherDetailRef = ref<InstanceType<typeof VouchersList> | null>(null)
const { section, lang } = useI18n()

const labels = section(
  {
    title: '', clearingAccount: '', selectAccount: '', partner: '', optional: '',
    bankAccount: '', bankPlaceholder: '', paymentAmount: '', amount: '',
    paymentDate: '', date: '', feeBearer: '', vendorBears: '', companyBears: '',
    feeAmount: '', feeAccount: '', account: '', bearer: '',
    noOpenItems: '', docDate: '', voucherNo: '', originalAmount: '', residualAmount: '',
    applyAmount: '', remark: '', clearingTotal: '', actualPayment: '', fee: '',
    mismatch: '', execute: '', success: '', failed: ''
  },
  (msg) => ({
    title: msg.bankPayment?.title || '銀行出金配分',
    clearingAccount: msg.bankPayment?.clearingAccount || '消込科目',
    selectAccount: msg.bankPayment?.selectAccount || '科目を選択',
    partner: msg.bankPayment?.partner || '取引先',
    optional: msg.bankPayment?.optional || '任意',
    bankAccount: msg.bankPayment?.bankAccount || '出金口座',
    bankPlaceholder: msg.bankPayment?.bankPlaceholder || '銀行/現金科目',
    paymentAmount: msg.bankPayment?.paymentAmount || '出金金額',
    amount: msg.bankPayment?.amount || '金額',
    paymentDate: msg.bankPayment?.paymentDate || '出金日',
    date: msg.bankPayment?.date || '日付',
    feeBearer: msg.bankPayment?.feeBearer || '手数料負担',
    vendorBears: msg.bankPayment?.vendorBears || '先方負担',
    companyBears: msg.bankPayment?.companyBears || '当社負担',
    feeAmount: msg.bankPayment?.feeAmount || '手数料額',
    feeAccount: msg.bankPayment?.feeAccount || '手数料科目',
    account: msg.bankPayment?.account || '科目',
    bearer: msg.bankPayment?.bearer || '負担',
    noOpenItems: msg.bankPayment?.noOpenItems || '未消込項目は見つかりません',
    docDate: msg.bankPayment?.docDate || '伝票日付',
    voucherNo: msg.bankPayment?.voucherNo || '伝票番号',
    originalAmount: msg.bankPayment?.originalAmount || '原金額',
    residualAmount: msg.bankPayment?.residualAmount || '未消込残高',
    applyAmount: msg.bankPayment?.applyAmount || '今回消込',
    remark: msg.bankPayment?.remark || '摘要',
    clearingTotal: msg.bankPayment?.clearingTotal || '消込金額',
    actualPayment: msg.bankPayment?.actualPayment || '実出金',
    fee: msg.bankPayment?.fee || '手数料',
    mismatch: msg.bankPayment?.mismatch || '不一致',
    execute: msg.bankPayment?.execute || '出金実行',
    success: msg.bankPayment?.success || '出金完了',
    failed: msg.bankPayment?.failed || '処理に失敗しました'
  })
)

// 表单数据
const form = reactive({
  accountCodes: [] as string[],
  partnerId: '',
  bankAccountCode: '',
  currency: '',
  amountText: '',
  amount: 0,
  paymentDate: new Date().toISOString().slice(0, 10),
  bankFeeBearer: 'vendor',
  bankFeeAmountText: '',
  bankFeeAccountCode: ''
})

// 下拉选项
const clearingAccountOptions = ref<{ label: string; value: string }[]>([])
const partnerOptions = ref<{ label: string; value: string }[]>([])
const bankAccountOptions = ref<{ label: string; value: string }[]>([])
const feeAccountOptions = ref<{ label: string; value: string }[]>([])

// 数据
const openItems = ref<any[]>([])
const plan = ref<any[]>([])

// 状态
const loading = ref(false)
const committing = ref(false)
const msg = ref('')
const err = ref('')

// 缓存
const accountCache = new Map<string, { name: string; currency?: string }>()

// 监听金额输入
watch(() => form.amountText, (val) => {
  const txt = String(val ?? '').replace(/,/g, '').trim()
  const n = Number(txt)
  form.amount = Number.isFinite(n) && n > 0 ? n : 0
  autoReplan()
})

// 监听手数料变化
watch(() => form.bankFeeBearer, () => autoReplan())
watch(() => form.bankFeeAmountText, () => autoReplan())

// 加载清账科目（openItem=true 的科目）
async function loadClearingAccounts() {
  if (clearingAccountOptions.value.length > 0) return
  try {
    // 出金画面：只显示清账基准为 VENDOR 或 EMPLOYEE 或 NONE 或 null 的科目
    const r = await api.post('/objects/account/search', {
      page: 1, pageSize: 500,
      where: [
        { json: 'openItem', op: 'eq', value: true },
        {
          anyOf: [
            { json: 'openItemBaseline', op: 'eq', value: 'VENDOR' },
            { json: 'openItemBaseline', op: 'eq', value: 'EMPLOYEE' },
            { json: 'openItemBaseline', op: 'eq', value: 'NONE' },
            { json: 'openItemBaseline', op: 'eq', value: null }
          ]
        }
      ],
      orderBy: [{ field: 'account_code', dir: 'ASC' }]
    })
    const arr: any[] = r.data?.data || []
    clearingAccountOptions.value = arr.map((a: any) => {
      const code = a.account_code
      const name = a.payload?.name || code
      accountCache.set(code, { name, currency: a.payload?.currency })
      return { label: `${name} (${code})`, value: code }
    })
  } catch (e) {
    console.error('loadClearingAccounts failed', e)
  }
}

// 搜索供应商（vendor）
async function searchPartners(query: string) {
  const where: any[] = [{ field: 'flag_vendor', op: 'eq', value: true }]
  if (query && query.trim()) {
    where.push({ json: 'name', op: 'contains', value: query.trim() })
  }
  const r = await api.post('/objects/businesspartner/search', { page: 1, pageSize: 50, where, orderBy: [] })
  const arr: any[] = r.data?.data || []
  partnerOptions.value = arr.map((p: any) => ({
    label: `${p.payload?.name || p.name} (${p.partner_code})`,
    value: p.partner_code
  }))
}

// 搜索银行/现金科目（与入金配分保持一致的逻辑）
async function searchBankAccounts(query: string) {
  const q = (query || '').trim()
  const extra: any[] = []
  if (q) {
    extra.push({ json: 'name', op: 'contains', value: q })
  }
  // 兼容不同大小写的字段名（isbank/iscash 或 isBank/isCash）
  const [bankRes1, cashRes1, bankRes2, cashRes2] = await Promise.all([
    api.post('/objects/account/search', { page: 1, pageSize: 100, where: [{ json: 'isbank', op: 'eq', value: true }, ...extra], orderBy: [] }),
    api.post('/objects/account/search', { page: 1, pageSize: 100, where: [{ json: 'iscash', op: 'eq', value: true }, ...extra], orderBy: [] }),
    api.post('/objects/account/search', { page: 1, pageSize: 100, where: [{ json: 'isBank', op: 'eq', value: true }, ...extra], orderBy: [] }),
    api.post('/objects/account/search', { page: 1, pageSize: 100, where: [{ json: 'isCash', op: 'eq', value: true }, ...extra], orderBy: [] })
  ])
  const all: any[] = ([] as any[]).concat(
    bankRes1.data?.data || [],
    cashRes1.data?.data || [],
    bankRes2.data?.data || [],
    cashRes2.data?.data || []
  )
  const seen = new Set<string>()
  bankAccountOptions.value = []
  for (const a of all) {
    const code = a.account_code
    if (!code || seen.has(code)) continue
    seen.add(code)
    const name = a.payload?.name || code
    accountCache.set(code, { name, currency: a.payload?.currency || a.payload?.accountCurrency })
    bankAccountOptions.value.push({ label: `${name} (${code})`, value: code })
  }
}

// 搜索手续费科目
async function searchFeeAccounts(query: string) {
  const where: any[] = []
  if (query && query.trim()) {
    where.push({ json: 'name', op: 'contains', value: query.trim() })
  }
  const r = await api.post('/objects/account/search', { page: 1, pageSize: 50, where, orderBy: [] })
  const arr: any[] = r.data?.data || []
  feeAccountOptions.value = arr.map((a: any) => {
    const code = a.account_code
    const name = a.payload?.name || code
    return { label: `${name} (${code})`, value: code }
  })
}

// 确保手续费科目在选项中
async function ensureFeeAccountOption(code: string) {
  if (!code) return
  if (feeAccountOptions.value.some(o => o.value === code)) return
  try {
    const r = await api.post('/objects/account/search', {
      page: 1, pageSize: 1,
      where: [{ field: 'account_code', op: 'eq', value: code }]
    })
    const arr: any[] = r.data?.data || []
    if (arr.length > 0) {
      const a = arr[0]
      const name = a.payload?.name || code
      feeAccountOptions.value = [{ label: `${name} (${code})`, value: code }, ...feeAccountOptions.value]
    }
  } catch (e) {
    console.error('ensureFeeAccountOption failed', e)
  }
}

// 确保银行科目在选项中
async function ensureBankAccountOption(code: string) {
  if (!code) return
  if (bankAccountOptions.value.some(o => o.value === code)) return
  try {
    const r = await api.post('/objects/account/search', {
      page: 1, pageSize: 1,
      where: [{ field: 'account_code', op: 'eq', value: code }]
    })
    const arr: any[] = r.data?.data || []
    if (arr.length > 0) {
      const a = arr[0]
      const name = a.payload?.name || code
      accountCache.set(code, { name, currency: a.payload?.currency || a.payload?.accountCurrency })
      bankAccountOptions.value = [{ label: `${name} (${code})`, value: code }, ...bankAccountOptions.value]
    }
  } catch (e) {
    console.error('ensureBankAccountOption failed', e)
  }
}

// 科目变化时重新加载未清项
function onAccountChange() {
  loadOpenItems()
  // 保存用户选择到 localStorage
  try { localStorage.setItem('bankPayment:lastAccountCodes', JSON.stringify(form.accountCodes)) } catch {}
}

// 取引先变化时重新加载未清项
function onPartnerChange() {
  loadOpenItems()
}

// 银行科目变化时更新货币
async function onBankAccountChange() {
  const code = form.bankAccountCode
  if (!code) {
    form.currency = ''
    return
  }
  let info = accountCache.get(code)
  if (!info) {
    try {
      const r = await api.post('/objects/account/search', {
        page: 1, pageSize: 1,
        where: [{ field: 'account_code', op: 'eq', value: code }]
      })
      const arr: any[] = r.data?.data || []
      if (arr.length > 0) {
        const row = arr[0]
        const name = row.payload?.name || code
        const currency = row.payload?.currency || row.payload?.accountCurrency || row.currency || row.account_currency
        info = { name, currency }
        accountCache.set(code, info)
      }
    } catch (e) {
      console.error('onBankAccountChange lookup failed', e)
    }
  }
  if (info) {
    form.currency = info.currency || 'JPY'
  } else {
    form.currency = 'JPY'
  }
  // 保存用户选择到 localStorage
  try { localStorage.setItem('bankPayment:lastBankAccountCode', code) } catch {}
}

// 加载未清项
async function loadOpenItems() {
  if (!form.accountCodes || form.accountCodes.length === 0) {
    openItems.value = []
    plan.value = []
    return
  }
  loading.value = true
  try {
    const where: any[] = []
    // 科目条件
    if (form.accountCodes.length === 1) {
      where.push({ field: 'account_code', op: 'eq', value: form.accountCodes[0] })
    } else {
      where.push({ field: 'account_code', op: 'in', value: form.accountCodes })
    }
    // 取引先条件（可选）
    if (form.partnerId) {
      where.push({ field: 'partner_id', op: 'eq', value: form.partnerId })
    }
    const r = await api.post('/objects/openitem/search', {
      page: 1, pageSize: 200,
      where,
      orderBy: [{ field: 'doc_date', dir: 'ASC' }]
    })
    let data: any[] = (r.data?.data || []).map((x: any) => ({ ...x, apply: 0 }))

    // 收集所有 voucher_id，批量查询伝票信息
    const voucherIds = new Set<string>()
    for (const item of data) {
      if (item.voucher_id) voucherIds.add(item.voucher_id)
      if (item.refs) {
        try {
          const refs = typeof item.refs === 'string' ? JSON.parse(item.refs) : item.refs
          if (refs.voucherId) voucherIds.add(refs.voucherId)
        } catch {}
      }
    }

    // 批量查询伝票获取 voucher_no 和行的 drcr
    const voucherMap = new Map<string, { voucherNo: string; lines: any[] }>()
    if (voucherIds.size > 0) {
      try {
        const vRes = await api.post('/objects/voucher/search', {
          page: 1, pageSize: voucherIds.size,
          where: [{ field: 'id', op: 'in', value: Array.from(voucherIds) }]
        })
        for (const v of (vRes.data?.data || [])) {
          const payload = v.payload || {}
          const header = payload.header || {}
          const lines = payload.lines || []
          voucherMap.set(v.id, {
            voucherNo: header.voucherNo || v.voucher_no || '',
            headerText: header.headerText || header.description || '',
            lines
          })
        }
      } catch (e) {
        console.error('批量查询伝票失败', e)
      }
    }

    // 补充科目名称和伝票信息
    const enriched = data.map((item: any) => {
      const code = item.account_code
      let accountName = ''
      if (code) {
        const cached = accountCache.get(code)
        if (cached) {
          accountName = cached.name
        } else {
          const opt = clearingAccountOptions.value.find(o => o.value === code)
          if (opt) {
            accountName = opt.label.replace(` (${code})`, '')
          }
        }
      }

      // 解析 refs 获取 voucherId 和 lineNo
      let voucherId = item.voucher_id || ''
      let lineNo = item.voucher_line_no || 0
      if (item.refs) {
        try {
          const refs = typeof item.refs === 'string' ? JSON.parse(item.refs) : item.refs
          if (refs.voucherId) voucherId = refs.voucherId
          if (refs.lineNo) lineNo = refs.lineNo
        } catch {}
      }

      // 从 voucherMap 获取 voucher_no 和 drcr
      let voucherNo = ''
      let drcr = 'CR' // 默认为贷方（应付）
      let lineRemark = '' // 从凭证明细行获取摘要
      let headerRemark = '' // 从凭证抬头获取摘要
      const vInfo = voucherMap.get(voucherId)
      if (vInfo) {
        voucherNo = vInfo.voucherNo
        // 获取凭证抬头摘要
        headerRemark = vInfo.headerText || ''
        // 根据 lineNo 找到对应行的 drcr 和摘要
        const line = vInfo.lines.find((l: any) => l.lineNo === lineNo)
        if (line) {
          if (line.drcr) drcr = line.drcr
          // 获取明细行摘要（优先使用 note，其次 description，再次 lineText）
          lineRemark = line.note || line.description || line.lineText || ''
        }
      }

      // isDebit 为 true 表示是债权（借方），出金时应显示为红色负数
      const isDebit = drcr === 'DR'

      return {
        ...item,
        accountName,
        voucher_no: voucherNo,
        voucherId,
        drcr,
        isDebit,
        remark: lineRemark || headerRemark || ''
      }
    })

    openItems.value = enriched
    applyAllocations(new Map())
  } finally {
    loading.value = false
  }
}

// 应用分配结果
function applyAllocations(allocMap: Map<string, number>) {
  plan.value = openItems.value.map((item: any) => ({
    ...item,
    apply: allocMap.get(item.id) ?? 0,
    selected: (allocMap.get(item.id) ?? 0) > 0
  }))
}

// 智能分配算法
function buildSmartAllocations(): Map<string, number> {
  const target = targetAmount()
  const map = new Map<string, number>()
  if (!Number.isFinite(target) || target <= 0) return map

  const items = [...(openItems.value || [])] as any[]
  if (items.length === 0) return map

  // 策略1：精确匹配单项
  const exactMatch = items.find(item => Math.abs(item.residual_amount - target) < 0.01)
  if (exactMatch) {
    map.set(exactMatch.id, target)
    return map
  }

  // 策略2：两项组合精确匹配
  for (let i = 0; i < items.length; i++) {
    for (let j = i + 1; j < items.length; j++) {
      const sum = items[i].residual_amount + items[j].residual_amount
      if (Math.abs(sum - target) < 0.01) {
        map.set(items[i].id, items[i].residual_amount)
        map.set(items[j].id, items[j].residual_amount)
        return map
      }
    }
  }

  // 策略3：FIFO贪婪匹配
  let remaining = target
  for (const item of items) {
    if (remaining <= 0) break
    const apply = Math.min(item.residual_amount, remaining)
    if (apply > 0) {
      map.set(item.id, apply)
      remaining -= apply
    }
  }

  return map
}

// 自动重新分配
function autoReplan() {
  if (!openItems.value || openItems.value.length === 0) {
    plan.value = []
    return
  }
  const target = targetAmount()
  if (!Number.isFinite(target) || target <= 0) {
    applyAllocations(new Map())
    return
  }
  const allocations = buildSmartAllocations()
  applyAllocations(allocations)
}

// 行选择变化
function onRowSelectChange(row: any) {
  if (row.selected) {
    row.apply = row.residual_amount
  } else {
    row.apply = 0
  }
}

// 金额变化
function onApplyChange(row: any) {
  row.selected = row.apply > 0
}

// 行样式
function planRowClass({ row }: any) {
  if (row.apply > 0) return 'plan-row-active'
  return ''
}

// 计算函数
function sumApply() {
  return (plan.value || []).reduce((s: number, x: any) => s + Number(x.apply || 0), 0)
}

function bankFeeAmount() {
  const txt = String(form.bankFeeAmountText ?? '').replace(/,/g, '').trim()
  if (!txt) return 0
  const n = Number(txt)
  if (!Number.isFinite(n) || n <= 0) return 0
  return n
}

function netAmount() {
  return Number.isFinite(form.amount) && form.amount > 0 ? form.amount : 0
}

function targetAmount() {
  // 目标金额 = 要消掉的应付金额 = 用户输入的出金金额
  // 手续费是额外的，不影响消込金額
  return netAmount()
}

function actualPaymentAmount() {
  // 实际出金金额 = 消込金額 + 手续费（当社负担时）
  const clearing = netAmount()
  if (form.bankFeeBearer === 'company') {
    return clearing + bankFeeAmount()
  }
  return clearing
}

function amountsEqual(a: number, b: number) {
  return Math.abs(a - b) < 0.01
}

function canCommit() {
  const target = targetAmount()
  const fee = form.bankFeeBearer === 'company' ? bankFeeAmount() : 0
  // 必填项检查
  if (!form.accountCodes || form.accountCodes.length === 0) return false
  if (!form.bankAccountCode) return false
  if (form.amount <= 0) return false
  if (!form.paymentDate) return false
  if (!form.bankFeeBearer) return false
  // 当社负担时
  if (form.bankFeeBearer === 'company') {
    if (fee <= 0) return false
    if (!form.bankFeeAccountCode) return false
  }
  if (netAmount() <= 0) return false
  return amountsEqual(sumApply(), target)
}

// 格式化金额
function formatAmount(val: any) {
  const n = Number(val)
  if (!Number.isFinite(n)) return '-'
  return n.toLocaleString('ja-JP', { minimumFractionDigits: 0, maximumFractionDigits: 0 })
}

// 打开伝票详情弹窗（只读）
function openVoucherDetail(row: any) {
  const voucherId = row.voucherId
  const voucherNo = row.voucher_no
  const normalized = (voucherId || voucherNo || '').trim()
  if (!normalized) return
  
  voucherDialogVisible.value = true
  nextTick(() => {
    // 支持通过 voucherId（UUID格式）或 voucherNo 查找
    const isUuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(normalized)
    voucherDetailRef.value?.applyIntent?.({
      ...(isUuid ? { voucherId: normalized } : { voucherNo: normalized }),
      detailOnly: true
    })
  })
}

// 提交出金
async function submitPayment() {
  if (!canCommit()) return
  err.value = ''
  msg.value = ''
  committing.value = true
  try {
    const allocations = (plan.value || [])
      .filter((x: any) => Number(x.apply) > 0)
      .map((x: any) => ({ openItemId: x.id, applyAmount: Number(x.apply) }))

    const header: any = {
      postingDate: form.paymentDate || new Date().toISOString().slice(0, 10),
      currency: form.currency || 'JPY',
      bankAccountCode: form.bankAccountCode
    }
    if (form.bankFeeBearer === 'company') {
      const fee = bankFeeAmount()
      if (fee > 0 && form.bankFeeAccountCode) {
        header.bankFeeAmount = fee
        header.bankFeeAccountCode = form.bankFeeAccountCode
      }
    } else if (form.bankFeeBearer === 'vendor') {
      // 先方负担时，手续费从支付金额中扣除
      const fee = bankFeeAmount()
      if (fee > 0) {
        header.vendorFeeAmount = fee
      }
    }

    const body = { header, allocations }
    const r = await api.post('/operations/bank-payment/allocate', body)
    msg.value = `${labels.value.success}：${formatAmount(r.data?.amount)}`
    // 重新加载
    await loadOpenItems()
  } catch (e: any) {
    err.value = e?.response?.data?.error || e?.message || labels.value.failed
  } finally {
    committing.value = false
  }
}

// 加载默认值
async function loadDefaults() {
  try {
    // 1. 先加载清账科目选项
    await loadClearingAccounts()
    
    // 2. 恢复用户上次选择的清账科目和出金口座（只保留有效科目）
    try {
      const lastAccountCodes = localStorage.getItem('bankPayment:lastAccountCodes')
      const lastBankAccountCode = localStorage.getItem('bankPayment:lastBankAccountCode')
      if (lastAccountCodes) {
        const codes = JSON.parse(lastAccountCodes)
        if (Array.isArray(codes) && codes.length > 0) {
          // 过滤：只保留存在于有效选项中的科目
          const validCodes = codes.filter((c: string) => 
            clearingAccountOptions.value.some(opt => opt.value === c)
          )
          if (validCodes.length > 0) {
            form.accountCodes = validCodes
            // 如果有无效科目被过滤掉了，更新 localStorage
            if (validCodes.length !== codes.length) {
              localStorage.setItem('bankPayment:lastAccountCodes', JSON.stringify(validCodes))
            }
            loadOpenItems() // 加载未清项
          } else {
            // 所有恢复的科目都无效，清除 localStorage
            localStorage.removeItem('bankPayment:lastAccountCodes')
          }
        }
      }
      if (lastBankAccountCode) {
        // 尝试加载银行科目到选项中
        await ensureBankAccountOption(lastBankAccountCode)
        // 验证科目是否确实存在于选项中
        if (bankAccountOptions.value.some(opt => opt.value === lastBankAccountCode)) {
          form.bankAccountCode = lastBankAccountCode
          await onBankAccountChange()
        } else {
          // 科目不存在，清除 localStorage
          localStorage.removeItem('bankPayment:lastBankAccountCode')
        }
      }
    } catch {}

    // 3. 加载公司设置的默认值
    const r = await api.post('/objects/company_setting/search', { page: 1, pageSize: 1, where: [], orderBy: [] })
    const settings = r.data?.data?.[0]?.payload || {}
    // 默认银行手续费科目
    if (settings.bankFeeAccountCode && !form.bankFeeAccountCode) {
      form.bankFeeAccountCode = settings.bankFeeAccountCode
      await ensureFeeAccountOption(settings.bankFeeAccountCode)
    }
  } catch (e) {
    console.error('loadDefaults failed', e)
  }
}

// 初始化
loadDefaults()
</script>

<style scoped>
/* 与入金配分弹窗完全一致的样式 */
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #e6a23c;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.rcpt-form-grid {
  margin: 8px 0 16px;
}

.rcpt-form-row {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  margin-bottom: 12px;
}

.rcpt-form-row:last-child {
  margin-bottom: 0;
}

.rcpt-form-item {
  display: flex;
  align-items: center;
  gap: 8px;
}

.rcpt-label {
  font-size: 13px;
  color: #606266;
  white-space: nowrap;
}

.rcpt-label.required::before {
  content: '*';
  color: #f56c6c;
  margin-right: 2px;
}

.rcpt-input-account {
  width: 200px;
}

.rcpt-input-partner {
  width: 240px;
}

.rcpt-input-amt {
  width: 120px;
}

.rcpt-input-date-short {
  width: 130px !important;
}

:deep(.rcpt-input-date-short) {
  width: 130px !important;
}

:deep(.rcpt-input-date-short .el-input__wrapper) {
  width: 100% !important;
}

.rcpt-input-fee {
  width: 100px;
}

.rcpt-input-fee-amt {
  width: 80px;
}

.rcpt-currency-badge {
  display: inline-flex;
  align-items: center;
  padding: 0 8px;
  background: #ecf5ff;
  color: #409eff;
  border-radius: 4px;
  font-size: 12px;
  font-weight: 500;
  height: 24px;
}

.planner-empty {
  margin: 6px 0;
  color: #6b7280;
}

.planner-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.planner-summary {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  color: #303133;
}

.planner-fee-info {
  color: #475569;
}

.planner-warning {
  color: #d93025;
}

.planner-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.planner-message {
  margin-top: 12px;
  padding: 8px 12px;
  border-radius: 4px;
}

.planner-success {
  color: #67c23a;
}

.planner-error {
  color: #f56c6c;
}

.voucher-link {
  color: #409eff;
  cursor: pointer;
  text-decoration: underline;
}

.voucher-link:hover {
  color: #66b1ff;
}

/* 借方金额（债权）显示为红色 */
.amount-debit {
  color: #f56c6c;
  font-weight: 500;
}

/* 伝票详情弹窗样式 */
.voucher-dialog-card-wrap {
  min-width: 800px;
  max-width: 1200px;
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

:deep(.plan-row-active) {
  background-color: #f0f9eb !important;
}

:deep(.el-table .cell) {
  padding: 0 8px;
}

:deep(.el-input-number--small) {
  width: 100px;
}
</style>
