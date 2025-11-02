<template>
  <div class="chatkit-wrap">
    <aside class="sidebar" @click.capture="onSidebarClick">
      <div class="sidebar-header">
        <div class="brand">
          <div class="brand-title">{{ text.appTitle }}</div>
          <div class="brand-sub">{{ profile.company }}</div>
        </div>
      </div>
      <div class="sidebar-scroll">
        <div class="section">
          <div class="section-title">{{ text.nav.chat }}</div>
          <el-menu class="menu" :default-active="activeSessionId" @select="onSelectSession">
            <el-menu-item v-for="s in sessions" :key="safeSessionId(s)" :index="safeSessionId(s)">{{ s.title || (safeSessionId(s).slice(0,8) || text.nav.chat) }}</el-menu-item>
          </el-menu>
          <div class="session-actions">
            <el-button size="small" @click="newSession">{{ text.nav.newSession }}</el-button>
          </div>
        </div>
        <div class="section">
          <div class="section-title">{{ text.nav.common }}</div>
            <el-menu class="menu" @select="onSelectCommon">
            <el-menu-item index="voucher.new">{{ text.nav.voucherNew }}</el-menu-item>
            <el-menu-item index="vouchers.list">{{ text.nav.vouchers }}</el-menu-item>
            <el-menu-item index="accounts.list">{{ text.nav.accounts }}</el-menu-item>
            <el-menu-item index="op.bankCollect">{{ text.nav.bankReceipt }}</el-menu-item>
            <el-menu-item index="op.bankPayment">{{ text.nav.bankPayment }}</el-menu-item>
            <el-menu-item index="rcpt.planner">{{ text.nav.bankPlanner }}</el-menu-item>
            <el-menu-item index="schema.editor">{{ text.nav.schemaEditor }}</el-menu-item>
            <el-menu-item index="approvals.designer">{{ text.nav.approvalsDesigner }}</el-menu-item>
            <el-menu-item index="scheduler.tasks">{{ text.nav.schedulerTasks }}</el-menu-item>
            <el-menu-item index="notif.ruleRuns">{{ text.nav.notifRuleRuns }}</el-menu-item>
            <el-menu-item index="notif.logs">{{ text.nav.notifLogs }}</el-menu-item>
            <el-menu-item index="bp.list">{{ text.nav.partners }}</el-menu-item>
            <el-menu-item index="bp.new">{{ text.nav.partnerNew }}</el-menu-item>
            <el-menu-item index="hr.dept">{{ text.nav.hrDept }}</el-menu-item>
            <el-menu-item index="hr.emps">{{ text.nav.hrEmps }}</el-menu-item>
            <el-menu-item index="hr.emp.new">{{ text.nav.hrEmpNew }}</el-menu-item>
            <el-menu-item index="hr.empTypes">{{ text.nav.employmentTypes }}</el-menu-item>
            <el-menu-item index="hr.policy.editor">{{ text.nav.policyEditor }}</el-menu-item>
            <el-menu-item index="payroll.execute">{{ text.nav.payrollExecute }}</el-menu-item>
            <el-menu-item index="timesheets.list">{{ text.nav.timesheets }}</el-menu-item>
            <el-menu-item index="timesheet.new">{{ text.nav.timesheetNew }}</el-menu-item>
            <el-menu-item index="cert.request">{{ text.nav.certRequest }}</el-menu-item>
            <el-menu-item index="cert.list">{{ text.nav.certList }}</el-menu-item>
            <el-menu-item index="approvals.inbox">{{ text.nav.approvalsInbox }}</el-menu-item>
            <el-menu-item index="company.settings">{{ text.nav.companySettings }}</el-menu-item>
            <el-menu-item index="acct.periods">{{ text.nav.accountingPeriods }}</el-menu-item>
            <el-sub-menu index="inv">
              <template #title>{{ text.nav.inventory }}</template>
              <el-menu-item index="inv.materials">{{ text.nav.inventoryMaterials }}</el-menu-item>
              <el-menu-item index="inv.material.new">{{ text.nav.inventoryMaterialNew }}</el-menu-item>
              <el-menu-item index="inv.warehouses">{{ text.nav.inventoryWarehouses }}</el-menu-item>
              <el-menu-item index="inv.warehouse.new">{{ text.nav.inventoryWarehouseNew }}</el-menu-item>
              <el-menu-item index="inv.bins">{{ text.nav.inventoryBins }}</el-menu-item>
              <el-menu-item index="inv.bin.new">{{ text.nav.inventoryBinNew }}</el-menu-item>
              <el-menu-item index="inv.stockstatus">{{ text.nav.inventoryStatuses }}</el-menu-item>
              <el-menu-item index="inv.batches">{{ text.nav.inventoryBatches }}</el-menu-item>
              <el-menu-item index="inv.batch.new">{{ text.nav.inventoryBatchNew }}</el-menu-item>
              <el-menu-item index="inv.movement">{{ text.nav.inventoryMovement }}</el-menu-item>
              <el-menu-item index="inv.balances">{{ text.nav.inventoryBalances }}</el-menu-item>
            </el-sub-menu>
            <el-sub-menu index="crm">
              <template #title>{{ text.nav.crm }}</template>
              <el-menu-item index="crm.contacts">{{ text.nav.crmContacts }}</el-menu-item>
              <el-menu-item index="crm.deals">{{ text.nav.crmDeals }}</el-menu-item>
              <el-menu-item index="crm.quotes">{{ text.nav.crmQuotes }}</el-menu-item>
              <el-menu-item index="crm.salesOrders">{{ text.nav.crmSalesOrders }}</el-menu-item>
              <el-menu-item index="crm.activities">{{ text.nav.crmActivities }}</el-menu-item>
            </el-sub-menu>
          </el-menu>
        </div>
        <div class="section">
          <div class="section-title">{{ text.nav.recent }}</div>
          <el-menu class="menu" router>
            <el-menu-item v-for="(r,i) in recent" :key="i" :index="r.path">{{ r.name || r.path }}</el-menu-item>
          </el-menu>
        </div>
      </div>
    </aside>
    <main class="main">
      <header class="main-header">
        <div class="header-left">
          <div class="page-title">{{ text.chat.aiTitle }}</div>
          <div class="page-subtitle">{{ text.chat.empty }}</div>
        </div>
        <div class="header-right">
          <el-select v-model="langValue" size="small" class="lang-select">
            <el-option v-for="opt in langOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
          </el-select>
          <div class="profile-box">
            <div class="company-badge">{{ profile.company }}</div>
            <div class="user-chip">
              <div class="avatar">{{ profileInitials }}</div>
              <div class="user-meta">
                <div class="user-name">{{ profile.name }}</div>
                <div class="user-role">{{ text.nav.chat }}</div>
              </div>
            </div>
          </div>
        </div>
      </header>
      <section class="workspace" :class="{ withDock: modalOpen }">
        <section class="chat-card">
          <div class="chat-header">
            <div class="chat-title">{{ text.dialogs.todo }}</div>
            <div class="chat-hint">{{ text.dialogs.todoEmpty }}</div>
          </div>
          <div class="chat-content" ref="chatBoxRef">
            <div
              v-for="(m,i) in displayMessages"
              :key="i"
              class="msg"
              :class="[m.role, m.status ? `status-${m.status}` : '']"
            >
              <div class="bubble">
                <div class="bubble-text">{{ m.content }}</div>
                <el-tag
                  v-if="m.tag"
                  size="small"
                  class="msg-tag"
                  :type="tagType(m.status)"
                  @click.stop="onMessageTagClick(m.tag)"
                >
                  {{ m.tag.label }}
                </el-tag>
            </div>
            </div>
            <div v-if="displayMessages.length===0" class="empty">{{ text.chat.empty }}</div>
          </div>
          <div class="chat-input">
            <el-input v-model="input" type="textarea" :rows="2" :placeholder="text.chat.placeholder" />
            <div class="chat-actions">
              <el-button size="small" type="primary" :loading="sending" @click="send">{{ text.chat.send }}</el-button>
            </div>
          </div>
        </section>
        <el-dialog v-model="modalOpen" :title="modal.title" width="auto" append-to-body destroy-on-close @closed="onModalClosed" class="embed-dialog">
          <template #header></template>
          <component v-if="modal.key" :is="resolveComp(modal.key)" :key="modal.renderKey" ref="modalRef" @done="onModalDone" />
        </el-dialog>
        <el-dialog v-model="planner.open" :title="'银行入金分配预览'" width="auto" append-to-body destroy-on-close @close="resetPlanner" class="embed-dialog planner-dialog">
          <template #header></template>
          <el-card class="planner-card">
            <template #header>
              <div class="page-header">
                <div>
                  <div class="page-header-title">银行入金配分</div>
                  <div class="page-header-sub">AI 辅助分配未清应收款</div>
                </div>
                <div class="page-actions">
                  <el-button size="small" @click="aiReplan" :loading="planner.planning">AI 重算</el-button>
                </div>
              </div>
            </template>
            <div class="planner-body">
              <el-form :inline="true" class="rcpt-toolbar" label-position="left" label-suffix=":" size="small">
                <el-form-item label="合作伙伴">
                  <el-select v-model="planner.partnerId" filterable remote :remote-method="searchPartners" reserve-keyword placeholder="输入名称检索" class="w-partner" @change="onPartnerChange">
                    <el-option v-for="p in partnerOptions" :key="p.value" :label="p.label" :value="p.value" />
                  </el-select>
                </el-form-item>
                <el-form-item label="入金金额">
                  <el-input v-model="planner.amountText" class="w-amount" placeholder="入金金额" />
                </el-form-item>
                <el-form-item label="入金日期">
                  <el-date-picker v-model="planner.paymentDate" class="w-date" type="date" value-format="YYYY-MM-DD" placeholder="选择日期" />
                </el-form-item>
                <el-form-item label="币种">
                  <el-select v-model="planner.currency" class="w-currency" placeholder="选择">
                    <el-option v-for="c in currencies" :key="c" :label="c" :value="c" />
                  </el-select>
                </el-form-item>
                <el-form-item label="银行/现金">
                  <el-select v-model="planner.bankAccountCode" class="w-account" filterable remote :remote-method="searchBankAccounts" reserve-keyword placeholder="选择银行/现金科目">
                    <el-option v-for="a in bankAccountOptions" :key="a.value" :label="a.label" :value="a.value" />
                  </el-select>
                </el-form-item>
              </el-form>
              <div v-if="planner.partnerId && planner.openItems.length===0" class="planner-empty">未检索到未清应收款</div>
              <el-table :data="planner.plan" border style="width:100%" size="small">
                <el-table-column label="#" width="60" type="index" />
                <el-table-column label="凭证日期" width="120" prop="docDate" />
                <el-table-column label="原金额" width="150">
                  <template #default="{ row }">{{ formatAmount(row.original) }}</template>
                </el-table-column>
                <el-table-column label="未清余额" width="150">
                  <template #default="{ row }">{{ formatAmount(row.residual) }}</template>
                </el-table-column>
                <el-table-column label="本次分配" width="140">
                  <template #default="{ row }">
                    <el-input-number v-model="row.apply" :min="0" :max="row.residual" :step="1000" controls-position="right"
                      :formatter="thousandFmt" :parser="thousandParse" />
                  </template>
                </el-table-column>
              </el-table>
              <div class="planner-footer">
                <div class="planner-summary">
                  匹配金额：{{ formatAmount(sumApply()) }} / 入金金额：{{ formatAmount(targetAmount()) }}
                  <span v-if="sumApply()!==targetAmount()" class="planner-warning">（不等于）</span>
                </div>
                <div class="planner-footer-actions">
                  <el-button @click="planner.open=false">取消</el-button>
                  <el-button type="primary" :disabled="!canCommit()" :loading="planner.committing" @click="commitReceipt">确认记账</el-button>
                </div>
              </div>
            </div>
          </el-card>
        </el-dialog>
      </section>
    </main>
  </div>
</template>
<script setup lang="ts">
import { onMounted, onBeforeUnmount, reactive, ref, nextTick, watch, defineAsyncComponent, defineComponent, computed, provide } from 'vue'
import { useRouter } from 'vue-router'
import api from '../api'
import VoucherForm from './VoucherForm.vue'
import AccountsList from './AccountsList.vue'
import AccountForm from './AccountForm.vue'
import BankReceipt from './BankReceipt.vue'
import BankPayment from './BankPayment.vue'
import SchemaEditor from './SchemaEditor.vue'
import VouchersList from './VouchersList.vue'
import BusinessPartnersList from './BusinessPartnersList.vue'
import BusinessPartnerForm from './BusinessPartnerForm.vue'
import DepartmentTree from './DepartmentTree.vue'
import EmployeesList from './EmployeesList.vue'
import EmployeeForm from './EmployeeForm.vue'
import EmploymentTypes from './EmploymentTypes.vue'
import PolicyEditor from './PolicyEditor.vue'
import PayrollExecute from './PayrollExecute.vue'
import { useI18n } from '../i18n'
import MaterialsList from './MaterialsList.vue'
import MaterialForm from './MaterialForm.vue'
import WarehousesList from './WarehousesList.vue'
import WarehouseForm from './WarehouseForm.vue'
import BinsList from './BinsList.vue'
import BinForm from './BinForm.vue'
import StockStatuses from './StockStatuses.vue'
import BatchesList from './BatchesList.vue'
import BatchForm from './BatchForm.vue'
import InventoryMovement from './InventoryMovementSchema.vue'
import InventoryBalances from './InventoryBalances.vue'

const ApprovalsInbox = defineAsyncComponent(() => import('./ApprovalsInbox.vue'))
const CertificateRequestForm = defineAsyncComponent(() => import('./CertificateRequestForm.vue'))
// @ts-ignore 新增页面在构建时可解析
const SchedulerTasks = defineAsyncComponent(() => import('./SchedulerTasks.vue'))

const router = useRouter()
const recent = reactive<{path:string,name?:string}[]>([])
const messages = reactive<any[]>([])
const displayMessages = computed(() => messages.filter((m:any) => m && m.kind !== 'embed'))
const sessions = reactive<{id:string,title?:string}[]>([])
const activeSessionId = ref('')
const input = ref('')
const sending = ref(false)
const chatBoxRef = ref<HTMLElement | null>(null)
const { text, lang, setLang } = useI18n()
const langOptions = [
  { value: 'ja', label: '日本語' },
  { value: 'en', label: 'English' }
]
const langValue = computed({
  get: () => lang.value,
  set: (val) => setLang(val as any)
})

const profile = reactive({
  name: sessionStorage.getItem('currentUserName') || 'Admin User',
  company: sessionStorage.getItem('currentCompany') || 'JP01'
})

const profileInitials = computed(() => {
  const parts = profile.name.split(/\s+/).filter(Boolean)
  if (parts.length === 0) return 'AU'
  return parts.map(p => p.charAt(0).toUpperCase()).join('').slice(0, 2)
})

function syncProfileFromStorage(){
  const storedName = sessionStorage.getItem('currentUserName')
  if (storedName) profile.name = storedName
  const storedCompany = sessionStorage.getItem('currentCompany')
  if (storedCompany) profile.company = storedCompany
}

function handleProfileStorage(event: StorageEvent){
  if (event.storageArea !== sessionStorage) return
  if (event.key === 'currentUserName' || event.key === 'currentCompany') {
    syncProfileFromStorage()
  }
}

function getTitle(key:string){
  const navKey = titleKeyMap[key]
  if (navKey && (text.value.nav as any)[navKey]) return (text.value.nav as any)[navKey]
  return text.value.common.view
}

const embedMap:Record<string, any> = {
  'voucher.new': VoucherForm,
  'vouchers.list': VouchersList,
  'accounts.list': AccountsList,
  'account.new': AccountForm,
  'op.bankCollect': BankReceipt,
  'op.bankPayment': BankPayment,
  'schema.editor': SchemaEditor,
  'bp.list': BusinessPartnersList,
  'bp.new': BusinessPartnerForm,
  'hr.dept': DepartmentTree,
  'hr.emps': EmployeesList,
  'hr.emp.new': EmployeeForm
  ,'hr.empTypes': EmploymentTypes
  ,'hr.policy.editor': PolicyEditor
  ,'payroll.execute': PayrollExecute
  ,'timesheets.list': defineAsyncComponent(() => import('./TimesheetsList.vue'))
  ,'timesheet.new': defineAsyncComponent(() => import('./TimesheetForm.vue'))
  ,'approvals.inbox': ApprovalsInbox
  ,'cert.request': CertificateRequestForm
  ,'cert.list': defineAsyncComponent(() => import('./CertificateRequestsList.vue'))
  ,'approvals.designer': defineAsyncComponent(() => import('./ApprovalsDesigner.vue'))
  ,'scheduler.tasks': SchedulerTasks
  ,'acct.periods': defineAsyncComponent(() => import('./AccountingPeriods.vue'))
  // Inventory embeds（直接用路由页面组件复用）
  ,'inv.materials': MaterialsList
  ,'inv.material.new': MaterialForm
  ,'inv.warehouses': WarehousesList
  ,'inv.warehouse.new': WarehouseForm
  ,'inv.bins': BinsList
  ,'inv.bin.new': BinForm
  ,'inv.stockstatus': StockStatuses
  ,'inv.batches': BatchesList
  ,'inv.batch.new': BatchForm
  ,'inv.movement': InventoryMovement
  ,'inv.balances': InventoryBalances
  // CRM embeds（直接用路由页面组件复用）
  ,'crm.contacts': defineAsyncComponent(() => import('./ContactsList.vue'))
  ,'crm.contact.new': defineAsyncComponent(() => import('./ContactForm.vue'))
  ,'crm.deals': defineAsyncComponent(() => import('./DealsList.vue'))
  ,'crm.deal.new': defineAsyncComponent(() => import('./DealForm.vue'))
  ,'crm.quotes': defineAsyncComponent(() => import('./QuotesList.vue'))
  ,'crm.quote.new': defineAsyncComponent(() => import('./QuoteForm.vue'))
  ,'crm.salesOrders': defineAsyncComponent(() => import('./SalesOrdersList.vue'))
  ,'crm.salesOrder.new': defineAsyncComponent(() => import('./SalesOrderForm.vue'))
  ,'crm.activities': defineAsyncComponent(() => import('./ActivitiesList.vue'))
  ,'crm.activity.new': defineAsyncComponent(() => import('./ActivityForm.vue'))
  ,'company.settings': defineAsyncComponent(() => import('./CompanySettings.vue'))
  ,'notif.ruleRuns': defineAsyncComponent(() => import('./NotificationRuleRuns.vue'))
  ,'notif.logs': defineAsyncComponent(() => import('./NotificationLogs.vue'))
}
const titleKeyMap: Record<string, string> = {
  'voucher.new': 'voucherNew',
  'vouchers.list': 'vouchers',
  'accounts.list': 'accounts',
  'account.new': 'accountNew',
  'op.bankCollect': 'bankReceipt',
  'op.bankPayment': 'bankPayment',
  'schema.editor': 'schemaEditor',
  'bp.list': 'partners',
  'bp.new': 'partnerNew',
  'hr.dept': 'hrDept',
  'hr.emps': 'hrEmps',
  'hr.emp.new': 'hrEmpNew',
  'hr.empTypes': 'employmentTypes',
  'hr.policy.editor': 'policyEditor',
  'payroll.execute': 'payrollExecute',
  'timesheets.list': 'timesheets',
  'timesheet.new': 'timesheetNew',
  'approvals.inbox': 'approvalsInbox',
  'cert.request': 'certRequest',
  'cert.list': 'certList',
  'approvals.designer': 'approvalsDesigner',
  'scheduler.tasks': 'schedulerTasks',
  'acct.periods': 'accountingPeriods',
  'inv.materials': 'inventoryMaterials',
  'inv.material.new': 'inventoryMaterialNew',
  'inv.warehouses': 'inventoryWarehouses',
  'inv.warehouse.new': 'inventoryWarehouseNew',
  'inv.bins': 'inventoryBins',
  'inv.bin.new': 'inventoryBinNew',
  'inv.stockstatus': 'inventoryStatuses',
  'inv.batches': 'inventoryBatches',
  'inv.batch.new': 'inventoryBatchNew',
  'inv.movement': 'inventoryMovement',
  'inv.balances': 'inventoryBalances',
  'crm.contacts': 'crmContacts',
  'crm.contact.new': 'crmContactNew',
  'crm.deals': 'crmDeals',
  'crm.deal.new': 'crmDealNew',
  'crm.quotes': 'crmQuotes',
  'crm.quote.new': 'crmQuoteNew',
  'crm.salesOrders': 'crmSalesOrders',
  'crm.salesOrder.new': 'crmSalesOrderNew',
  'crm.activities': 'crmActivities',
  'crm.activity.new': 'crmActivityNew',
  'company.settings': 'companySettings',
  'notif.ruleRuns': 'notifRuleRuns',
  'notif.logs': 'notifLogs'
}
const Dummy = defineComponent({ name:'EmbedPlaceholder', setup(){ return () => null } })

function resolveComp(key:string){
  return (embedMap as any)[key] || Dummy
}
const modal = reactive<{ key:string, title:string, renderKey:number }>({ key:'', title:'', renderKey:0 })
const modalOpen = ref(false)
const modalRef = ref<any>(null)
const pendingModalPayload = ref<any>(null)
const pendingModalPayloadAttempts = ref(0)

type OperationMessage = { content: string; status?: 'success' | 'error' | 'info'; tag?: any }
type OperationFormatter = (payload: any) => OperationMessage | null

function tagType(status?: string){
  if (status === 'success') return 'success'
  if (status === 'error') return 'danger'
  return 'info'
}

function onMessageTagClick(tag: any){
  try{
    if (!tag) return
    const key = tag.key || tag.embedKey
    if (tag.action === 'openEmbed' || key){
      const targetKey = key || tag.key
      if (!targetKey) return
      const payload = tag.payload || tag.data || null
      if (!embedMap[targetKey]){
        pushEventMessage(`页面未注册：${targetKey}`, { status: 'error' })
        return
      }
      const title = getTitle(targetKey)
      pushEmbed(targetKey, title)
      openInModal(targetKey, title, payload)
    }
  }catch{}
}

function pushEventMessage(content: string, options: { status?: 'success' | 'error' | 'info'; tag?: any } = {}){
  if (!content) return
  const msg: any = {
    role: 'assistant',
    kind: 'event',
    content,
    status: options.status || 'info'
  }
  if (options.tag) msg.tag = options.tag
  messages.push(msg)
  nextTick().then(scrollToBottom)
  const payload: any = { kind:'event', status: msg.status }
  if (msg.tag) payload.tag = cloneSerializable(msg.tag)
  persistMessage('assistant', content, payload).catch(()=>{})
  return msg
}

const operationMessageMap: Record<string, OperationFormatter> = {
  'voucher.created': (payload) => {
    const voucherNo = payload?.voucherNo || payload?.voucher_no
    const message = payload?.message || (voucherNo ? `已创建会计凭证 ${voucherNo}` : '')
    const tag = voucherNo
      ? { label: voucherNo, action: 'openEmbed', key: 'vouchers.list', payload: { voucherNo, detailOnly: true } }
      : undefined
    return message ? { content: message, status: 'success', tag } : null
  },
  'voucher.failed': (payload) => {
    const message = payload?.message || payload?.error || '创建会计凭证失败'
    return { content: message, status: 'error' }
  }
}

function interpretModalResult(result: any): OperationMessage | null {
  if (!result) return null
  const explicitStatus: OperationMessage['status'] = result.status || (result.error ? 'error' : undefined)
  const formatter = result.kind ? operationMessageMap[result.kind] : undefined
  if (formatter){
    const formatted = formatter(result)
    if (formatted){
      return {
        content: formatted.content,
        status: formatted.status || explicitStatus,
        tag: formatted.tag
      }
    }
  }
  if (result.kind && typeof result.kind === 'string' && result.kind.includes('.')){
    const [entityKey, actionKey] = result.kind.split('.')
    const actionText: Record<string, string> = { created: '已创建', updated: '已更新', deleted: '已删除' }
    const verb = actionText[actionKey]
    if (verb){
      const entityLabel = result.entityName || result.entityLabel || result.entity || entityKey
      const identifier = result.identifier || result.code || result.number || result.name
      const contentText = identifier ? `${verb} ${entityLabel}：${identifier}` : `${verb} ${entityLabel}`
      let tag = result.tag
      if (!tag && result.targetKey){
        tag = { label: identifier || entityLabel, action: 'openEmbed', key: result.targetKey, payload: result.targetPayload || null }
      }
      return { content: contentText, status: explicitStatus || 'success', tag }
    }
  }
  let content = result.message || result.error || ''
  if (!content && result.action && result.entityName){
    const actionText: Record<string, string> = { create: '已创建', update: '已更新', delete: '已删除' }
    const verb = actionText[result.action] || result.action
    content = `${verb} ${result.entityName}`
  }
  if (!content && result.entity && result.action){
    content = `${result.action} ${result.entity}`
  }
  if (!content) return null
  let tag = result.tag
  if (!tag){
    const label = result.identifier || result.code || result.number || result.voucherNo || result.reference
    const targetKey = result.targetKey || result.embedKey || result.key
    if (label && targetKey){
      tag = { label, action: 'openEmbed', key: targetKey, payload: result.targetPayload || result.payload || null }
    }
  }
  return { content, status: explicitStatus || 'info', tag }
}

async function applyPendingModalPayload(){
  if (typeof pendingModalPayload.value === 'undefined' || pendingModalPayload.value === null) return
  pendingModalPayloadAttempts.value += 1
  await nextTick()
  for (let i = 0; i < 8; i++){
    const target = modalRef.value
    const handler = target && (typeof target.applyIntent === 'function'
      ? target.applyIntent
      : typeof target.receiveIntent === 'function'
        ? target.receiveIntent
        : typeof target.handleIntent === 'function'
          ? target.handleIntent
          : null)
    if (target && handler){
      try{ handler.call(target, pendingModalPayload.value) }catch{}
      pendingModalPayload.value = null
      pendingModalPayloadAttempts.value = 0
      return
    }
    await new Promise((resolve) => setTimeout(resolve, 120))
  }
  if (pendingModalPayload.value !== null && typeof pendingModalPayload.value !== 'undefined'){
    if (pendingModalPayloadAttempts.value >= 5){
      pendingModalPayload.value = null
      pendingModalPayloadAttempts.value = 0
      return
    }
    setTimeout(() => applyPendingModalPayload(), 240)
  }
}

let creatingSessionPromise: Promise<string | null> | null = null

async function ensureSessionId(): Promise<string | null>{
  if (activeSessionId.value) return activeSessionId.value
  if (creatingSessionPromise) return creatingSessionPromise
  creatingSessionPromise = (async () => {
    try{
      const title = text.value?.chat?.aiTitle || text.value?.nav?.chat || 'AI Chat'
      const resp = await api.post('/ai/sessions', { title })
      const sidRaw = resp.data?.id || resp.data?.sessionId || resp.data?.session_id || ''
      const sid = typeof sidRaw === 'string' ? sidRaw.trim() : String(sidRaw || '').trim()
      if (sid){
        const exists = sessions.find(s => safeSessionId(s) === sid)
        if (!exists) sessions.unshift({ id: sid, title })
        activeSessionId.value = sid
      }
      return activeSessionId.value || (sid || null)
    }catch{
      return activeSessionId.value || null
    }finally{
      creatingSessionPromise = null
    }
  })()
  return creatingSessionPromise
}

function cloneSerializable<T>(val: T): T {
  try{
    if (val === undefined || val === null) return val
    return JSON.parse(JSON.stringify(val))
  }catch{
    return val
  }
}

async function persistMessage(role: string, content: string, payload?: any){
  const sid = await ensureSessionId()
  if (!sid) return
  try{
    const body: any = { role, content }
    if (payload !== undefined) body.payload = cloneSerializable(payload)
    await api.post(`/ai/sessions/${encodeURIComponent(sid)}/messages`, body)
  }catch{}
}

function parseMessagePayload(raw:any){
  if (!raw) return null
  if (typeof raw === 'string'){
    try{ return JSON.parse(raw) }catch{ return null }
  }
  return raw
}

function onSidebarClick(e: MouseEvent){
  try { console.debug('[ChatKit] sidebar click', (e.target as HTMLElement)?.tagName) } catch {}
}

// 入金 AI 编排 Planner
const planner = reactive<any>({
  open: false,
  intent: null,
  candidates: [] as any[],
  partnerId: '',
  amount: 0,
  amountText: '',
  currency: 'JPY',
  paymentDate: '',
  bankAccountCode: '',
  openItems: [] as any[],
  plan: [] as any[],
  planning: false,
  committing: false
})
const currencies = ['JPY','USD','CNY','EUR']
const bankAccountOptions = reactive<{label:string,value:string}[]>([])

function recentKey(){ return `recent_links:${activeSessionId.value || 'default'}` }
function loadRecent(){
  try{
    recent.splice(0, recent.length)
    const raw = sessionStorage.getItem(recentKey())
    if (raw){ const arr = JSON.parse(raw); if (Array.isArray(arr)) arr.forEach((x:any)=>recent.push(x)) }
  }catch{}
}
function saveRecent(){
  try{ sessionStorage.setItem(recentKey(), JSON.stringify(recent)) }catch{}
}

onMounted(async () => {
  syncProfileFromStorage()
  window.addEventListener('storage', handleProfileStorage)
  loadRecent()
  router.afterEach((to) => {
    const p = to.fullPath
    if (!recent.find(x => x.path===p)){
      recent.unshift({ path: p, name: document.title })
      if (recent.length>10) recent.pop()
      saveRecent()
    }
  })
  await loadSessions()
  if (sessions.length>0) { activeSessionId.value = sessions[0].id; await loadMessages(); loadRecent() }
})

onBeforeUnmount(() => {
  window.removeEventListener('storage', handleProfileStorage)
})

watch(activeSessionId, () => { loadRecent() })

async function loadSessions(){
  const r = await api.get('/ai/sessions')
  const rows = Array.isArray(r.data) ? r.data : []
  const mapped = rows.map((x:any)=>({ id: String(x.id || x.session_id || x.sessionId || x._id || '').trim(), title: x.title }))
    .filter((x:any)=> !!x.id)
  sessions.splice(0, sessions.length, ...mapped)
}
async function loadMessages(){
  messages.splice(0, messages.length)
  if (!activeSessionId.value) return
  const r = await api.get(`/ai/sessions/${encodeURIComponent(activeSessionId.value)}/messages`)
  const rows = Array.isArray(r.data) ? r.data : []
  for (const m of rows){
    const payload = parseMessagePayload((m as any).payload)
    const msg:any = {
      role: (m as any).role || 'assistant',
      content: (m as any).content || '',
      kind: payload?.kind || (m as any).kind
    }
    if (payload?.status) msg.status = payload.status
    if (payload?.tag) msg.tag = payload.tag
    if (payload) msg.payload = payload
    messages.push(msg)
  }
  loadEmbeds()
  await nextTick(); scrollToBottom()
}
function safeSessionId(s:any){ return String(s?.id || '').trim() }

function onSelectSession(id:string){
  try { console.debug('[ChatKit] select session', id) } catch {}
  if (!id) return
  activeSessionId.value = id; loadMessages()
}
function newSession(){ activeSessionId.value=''; messages.splice(0,messages.length) }

async function send(){
  const text = input.value.trim()
  if (!text) return
  messages.push({ role: 'user', content: text })
  await nextTick(); scrollToBottom()
  input.value = ''
  sending.value = true
  const userPersistPayload = { kind: 'user.input' }
  let shouldPersistUser = true
  let reloadSessions = false
  let exitEarly = false
  try{
    // 优先判定“入金”意图，直接走编排流程，避免 /ai/route 打开创建凭证
    const recEarly = parseReceiptIntent(text)
    if (recEarly){
      await persistMessage('user', text, { ...userPersistPayload, intent: 'receipt' })
      shouldPersistUser = false
      reloadSessions = true
      await handleReceiptIntent(recEarly)
      exitEarly = true
    } else {
    const history = messages
        .filter(m => m.kind !== 'embed' && m.kind !== 'event')
      .map(m => ({ role: m.role || 'assistant', content: m.content || '' }))
      .filter(m => m.content && typeof m.content === 'string')
    const route = await api.post('/ai/route', { messages: [...history, { role:'user', content: text }] })
    const actions = route.data?.actions || []
    const msgText = route.data?.assistantMessage
      if (actions.length === 0 && msgText){
        messages.push({ kind:'text', role: 'assistant', content: msgText })
        persistMessage('assistant', msgText, { kind: 'assistant.route' }).catch(()=>{})
        reloadSessions = true
      }
    for (const a of actions){
      if (a.type === 'openEmbed'){
        const key = a.key
        const payloadRoute = (a.payload && typeof a.payload === 'object') ? a.payload : {}
        const intent = parseIntent(text)
        const payload = intent && intent.key===key ? { ...payloadRoute, code:intent.code, name:intent.name } : payloadRoute
        const title = getTitle(key)
          if (!embedMap[key]){
            pushEventMessage(`页面未注册：${key}`, { status: 'error' })
            continue
          }
        pushEmbed(key, title)
          openInModal(key, title, payload)
      }
    }
    if (actions.length === 0){
      const rec = parseReceiptIntent(text)
        if (rec){
          await persistMessage('user', text, { ...userPersistPayload, intent: 'receipt' })
          shouldPersistUser = false
          reloadSessions = true
          await handleReceiptIntent(rec)
          exitEarly = true
        } else {
      const payload:any = { messages: [{ role:'user', content: text }] }
      if (activeSessionId.value) payload.sessionId = activeSessionId.value
          const body:any = { messages: [{ role:'system', content: 'You are helpful.' }, ...payload.messages] }
          if (payload.sessionId) body.sessionId = payload.sessionId
          const r = await api.post('/ai/chat', body)
          shouldPersistUser = false
          reloadSessions = true
      const content = r.data?.choices?.[0]?.message?.content || ''
      if (content) messages.push({ kind:'text', role: 'assistant', content })
    }
      } else {
        reloadSessions = true
      }
      if (!exitEarly){
    await nextTick(); scrollToBottom()
      }
    }
  }catch(e:any){
    const errText = e?.response?.data?.error || e?.message || '调用失败'
    pushEventMessage(errText, { status: 'error' })
    try{
      const intent = parseIntent(text)
      if (intent && intent.key==='account.new'){
        openInModal('account.new', getTitle('account.new'), { code:intent.code, name:intent.name })
      }
    }catch{}
  }finally{
    if (shouldPersistUser){
      await persistMessage('user', text, userPersistPayload)
      reloadSessions = true
    }
    sending.value = false
  }
  if (reloadSessions){
    try{
      await loadSessions()
      if (!activeSessionId.value && sessions.length>0) {
        activeSessionId.value = sessions[0].id
      }
    }catch{}
  }
  if (exitEarly) return
}
function onSelectCommon(key:string){
  try { console.debug('[ChatKit] menu select', key) } catch {}
  if (key==='rcpt.planner'){
    planner.open = true
    return
  }
  // 库存管理子菜单：统一以弹窗方式打开
  if (key.startsWith('inv.')) return openInModal(key, getTitle(key))
  // CRM 子菜单：统一以弹窗方式打开
  if (key.startsWith('crm.')) return openInModal(key, getTitle(key))

  // 优先弹出内嵌对话框；若组件未注册，则跳路由兜底
  if (!embedMap[key]){
    // 路由兜底
    if (key==='hr.dept') return router.push('/hr/departments')
    if (key==='hr.emps') return router.push('/hr/employees')
    if (key==='hr.emp.new') return router.push('/hr/employee/new')
    if (key==='approvals.inbox') return router.push('/approvals/inbox')
    if (key==='cert.request') return router.push('/cert/request')
    if (key==='company.settings') return router.push('/company/settings')
  }
  openInModal(key, getTitle(key))
}
function openInModal(key:string, title:string, payload?:any){
  if (!embedMap[key]){
    pushEventMessage(`页面未注册：${key}`, { status: 'error' })
    return
  }
  const hasPayload = typeof payload !== 'undefined'
  pendingModalPayload.value = hasPayload ? payload : null
  pendingModalPayloadAttempts.value = 0
  modalOpen.value = false
  nextTick(() => {
    modal.key = key
    modal.title = title
    modal.renderKey++
    modalOpen.value = true
    if (hasPayload){
      nextTick().then(() => applyPendingModalPayload())
    }
  })
}
provide('chatkitOpenEmbed', (key: string, payload?: any) => openInModal(key, getTitle(key), payload))
function onModalDone(result:any){
  try{
    const summary = interpretModalResult(result)
    if (!summary) return
    pushEventMessage(summary.content, { status: summary.status, tag: summary.tag })
  }catch{}
}
function onModalClosed(){
  // 彻底清理，避免下次打开其它页面时残留上次组件
  modal.key = ''
  modal.title = ''
  modalRef.value = null
  pendingModalPayload.value = null
  pendingModalPayloadAttempts.value = 0
}

function parseIntent(text:string){
  const t = text.trim()
  let m = t.match(/(新建|新增|创建)(?:一个)?(?:会计)?科目\s*(\d{3,10})\s*(.*)$/)
  if (m) return { key:'account.new', code:m[2], name:(m[3]||'').trim() }
  m = t.match(/新科目\s*(\d{3,10})\s*(.+)?$/)
  if (m) return { key:'account.new', code:m[1], name:(m[2]||'').trim() }
  m = t.match(/科目\s*(\d{3,10})\s*(.*)$/)
  if (m) return { key:'account.new', code:m[1], name:(m[2]||'').trim() }
  return null
}

// 入金意图解析（轻量）：金额/日期/伙伴名
function parseReceiptIntent(text:string){
  const t = text.trim()
  const amtM = t.match(/(\d+[\d,]*)(?:\s*万)?\s*(?:円|日元|JPY)?/)
  if (!amtM) return null
  let raw = amtM[1].replace(/,/g,'')
  let amount = parseInt(raw,10)
  if (/万/.test(t)) amount = amount * 10000
  const dateM = t.match(/(\d{4}[\/\-]\d{1,2}[\/\-]\d{1,2}|今天|今日)/)
  let paymentDate = ''
  if (dateM){
    if (dateM[1]==='今天' || dateM[1]==='今日'){
      const d=new Date(); const y=d.getFullYear(); const m=String(d.getMonth()+1).padStart(2,'0'); const dd=String(d.getDate()).padStart(2,'0'); paymentDate=`${y}-${m}-${dd}`
    } else {
      const s=dateM[1].replace(/\//g,'-'); const parts=s.split('-'); paymentDate = parts[0].length===4? s : ''
    }
  }
  const partnerM = t.match(/收到(.+?)的?入金|来自(.+?)的?入金|(.+?)入金/)
  const partnerText = partnerM ? (partnerM[1]||partnerM[2]||partnerM[3]) : ''
  return { partnerText, amount, currency:'JPY', paymentDate }
}

async function handleReceiptIntent(intent:any){
  resetPlanner()
  // 查询伙伴候选
  const where:any[] = []
  if (intent.partnerText) where.push({ json:'name', op:'contains', value:intent.partnerText })
  where.push({ field:'flag_customer', op:'eq', value:true })
  const r = await api.post('/objects/businesspartner/search', { page:1, pageSize:20, where, orderBy: [] })
  const candidates = (r.data?.data||[]) as any[]
  planner.intent = intent
  planner.amount = intent.amount
  planner.amountText = String(intent.amount)
  planner.currency = intent.currency||'JPY'
  planner.paymentDate = intent.paymentDate||planner.paymentDate
  partnerOptions.splice(0, partnerOptions.length, ...candidates.map((p:any)=>({ label: `${p.name} (${p.partner_code})`, value: p.partner_code, name:p.name })))
  // 选择最佳匹配：名称或编码包含意图文本（不区分大小写）
  const txt = (intent.partnerText||'').toLowerCase()
  if (partnerOptions.length===1) planner.partnerId = partnerOptions[0].value
  else if (txt){
    const exact = partnerOptions.find(p => (p.value||'').toLowerCase()===txt || (p.name||'').toLowerCase()===txt)
    if (exact) planner.partnerId = exact.value
    else {
      const hit = partnerOptions.filter(p => (p.value||'').toLowerCase().includes(txt) || (p.name||'').toLowerCase().includes(txt))
      if (hit.length===1) planner.partnerId = hit[0].value
    }
  }
  planner.open = true
  if (planner.partnerId) await loadOpenItems()
  await aiReplan()
}

async function loadOpenItems(){
  if (!planner.partnerId) return
  const base:any[] = []
  // 先精确 partnerId 过滤
  let where:any[] = [{ field:'partner_id', op:'eq', value: planner.partnerId }, ...base]
  let r = await api.post('/objects/openitem/search', { page:1, pageSize:200, where, orderBy:[{field:'doc_date',dir:'ASC'}] })
  let data:any[] = (r.data?.data||[]).map((x:any)=>({ ...x, apply: 0 }))
  // 若无结果，尝试使用候选伙伴批量 IN 过滤（以防 partnerId 与投影不一致）
  if (data.length===0 && partnerOptions.length>0){
    const codes = partnerOptions.map(x=>x.value)
    where = [{ field:'partner_id', op:'in', value: codes }, ...base]
    r = await api.post('/objects/openitem/search', { page:1, pageSize:200, where, orderBy:[{field:'doc_date',dir:'ASC'}] })
    data = (r.data?.data||[]).map((x:any)=>({ ...x, apply: 0 }))
  }
  // 若仍无结果，最后兜底：不按伙伴过滤，展示全量未清（上限200）
  if (data.length===0){
    r = await api.post('/objects/openitem/search', { page:1, pageSize:200, where: base, orderBy:[{field:'doc_date',dir:'ASC'}] })
    data = (r.data?.data||[]).map((x:any)=>({ ...x, apply: 0 }))
  }
  planner.openItems = data
  // 默认将未清项映射为“计划列表”，保持单表显示；AI 重算会覆盖 plan
  planner.plan = (planner.openItems||[]).map((x:any)=>({ openItemId:x.id, docDate:x.doc_date, original:Number(x.original_amount), residual:Number(x.residual_amount), apply: 0 }))
}

function sumApply(){ return (planner.plan||[]).reduce((s:number, x:any)=> s + Number(x.apply||0), 0) }
function targetAmount(){
  const txt = String(planner.amountText ?? '').replace(/,/g, '').trim()
  if (txt){
    const parsed = Number(txt)
    if (Number.isFinite(parsed)) return parsed
  }
  const fallback = Number(planner.amount || 0)
  return Number.isFinite(fallback) ? fallback : 0
}
function canCommit(){
  const target = targetAmount()
  return !!(planner.bankAccountCode && planner.partnerId && target>0 && sumApply() === target)
}

function buildGreedyPlan(){
  const target = Number(planner.amount || planner.amountText || 0)
  if (!Number.isFinite(target) || target<=0) return [] as any[]
  const items = (planner.openItems||[]).map((x:any)=>({ openItemId:x.id, docDate:x.doc_date, original:Number(x.original_amount), residual:Number(x.residual_amount) }))
  let remain = target
  const plan:any[] = []
  for (const it of items){
    if (remain<=0) break
    const apply = Math.min(remain, Number(it.residual||0))
    if (apply>0) { plan.push({ ...it, apply }) ; remain -= apply }
  }
  return plan
}

async function aiReplan(){
  if (!planner.openItems || planner.openItems.length===0){ planner.plan=[]; return }
  const target = Number(planner.amount || planner.amountText || 0)
  if (!Number.isFinite(target) || target<=0){ return }
  planner.planning = true
  try{
    const sys = `你是会计助理。输入为未清应收清单(open items)与目标入金金额，输出 JSON 格式的分配方案数组，不要解释文字。字段: [{openItemId, docDate, original, residual, apply}]. 规则：优先到期/日期早的，apply≤residual，合计等于目标金额，若无法正好匹配，则尽量接近并给出最后一条 apply 为剩余金额。`
    const items = planner.openItems.map((x:any)=>({ openItemId:x.id, docDate:x.doc_date, original:Number(x.original_amount), residual:Number(x.residual_amount) }))
    const user = `目标金额: ${target}. 未清项: ${JSON.stringify(items)}`
    const resp = await api.post('/ai/chat', { messages: [{ role:'system', content: sys }, { role:'user', content: user }] })
    const text = resp.data?.choices?.[0]?.message?.content || '[]'
    let plan:any[]
    try{ plan = JSON.parse(text) }catch{ plan = [] }
    if (!Array.isArray(plan) || plan.length===0){ plan = buildGreedyPlan() }
    plan = plan.map((p:any)=>({ ...p, apply: Math.min(Number(p.apply||0), Number((items.find((i:any)=>i.openItemId===p.openItemId)||{}).residual||0)) }))
    planner.plan = plan
  }catch(e:any){
    // 如果 AI 接口不可用(例如 501)，采用本地贪心方案
    planner.plan = buildGreedyPlan()
  }
  finally{ planner.planning = false }
}

async function commitReceipt(){
  if (!canCommit()) return
  planner.committing = true
  try{
    const idempotencyKey = `rcpt-${Date.now()}`
    const allocations = (planner.plan||[]).filter((x:any)=> Number(x.apply)>0).map((x:any)=>({ openItemId: x.openItemId, applyAmount: Number(x.apply) }))
    const header = { postingDate: planner.paymentDate || (new Date().toISOString().slice(0,10)), currency: planner.currency || 'JPY', bankAccountCode: planner.bankAccountCode }
    await api.post('/operations/bank-collect/allocate', { header, allocations, idempotencyKey })
    pushEventMessage(`已记账银行入金 ${planner.amount} ${planner.currency}，生成入金凭证，合计分配 ${allocations.length} 笔。`, { status: 'success' })
    planner.open = false
  }catch(e:any){ pushEventMessage(e?.response?.data?.error || '提交失败', { status: 'error' }) }
  finally{ planner.committing = false }
}

// 伙伴远程检索
const partnerOptions = reactive<{label:string,value:string,name?:string}[]>([])
async function searchPartners(query:string){
  const where:any[] = [{ field:'flag_customer', op:'eq', value:true }]
  if (query && query.trim()) where.push({ json:'name', op:'contains', value: query.trim() })
  const r = await api.post('/objects/businesspartner/search', { page:1, pageSize:50, where, orderBy:[] })
  partnerOptions.splice(0, partnerOptions.length, ...((r.data?.data||[]).map((p:any)=>({ label:`${p.payload?.name||p.name} (${p.partner_code})`, value:p.partner_code, name:(p.payload?.name||p.name) }))))
}
function onPartnerChange(){ loadOpenItems().then(aiReplan) }

async function searchBankAccounts(query:string){
  const q = (query||'').trim()
  const extra = [] as any[]
  if (q) { extra.push({ json:'name', op:'contains', value:q }); extra.push({ field:'account_code', op:'contains', value:q }) }
  // 直接按布尔字段（后端存储：isbank / iscash 或 isBank / isCash）过滤
  const [bankRes1, cashRes1, bankRes2, cashRes2] = await Promise.all([
    api.post('/objects/account/search', { page:1, pageSize:100, where:[ { json:'isbank', op:'eq', value:true }, ...extra ], orderBy: [] }),
    api.post('/objects/account/search', { page:1, pageSize:100, where:[ { json:'iscash', op:'eq', value:true }, ...extra ], orderBy: [] }),
    api.post('/objects/account/search', { page:1, pageSize:100, where:[ { json:'isBank', op:'eq', value:true }, ...extra ], orderBy: [] }),
    api.post('/objects/account/search', { page:1, pageSize:100, where:[ { json:'isCash', op:'eq', value:true }, ...extra ], orderBy: [] })
  ])
  let all:any[] = ([] as any[]).concat(bankRes1.data?.data||[], cashRes1.data?.data||[], bankRes2.data?.data||[], cashRes2.data?.data||[])
  const seen = new Set<string>()
  const items:any[] = []
  for (const x of all){
    const code = x.account_code || x.payload?.code
    if (!code || seen.has(code)) continue
    seen.add(code)
    const name = x.name || x.payload?.name || code
    items.push({ label: `${name} (${code})`, value: code })
  }
  bankAccountOptions.splice(0, bankAccountOptions.length, ...items)
}

let replanTimer: ReturnType<typeof setTimeout> | null = null
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
    if (planner.openItems && planner.openItems.length > 0){
      await aiReplan()
    }
  }, delay)
}

watch(() => planner.partnerId, async (v) => {
  cancelScheduledReplan()
  if (!v) { planner.openItems = []; planner.plan = []; return }
  await loadOpenItems()
  const target = targetAmount()
  if (Number.isFinite(target) && target>0 && planner.openItems && planner.openItems.length>0) scheduleReplan(0)
})

watch(() => planner.amountText, (v) => {
  cancelScheduledReplan()
  const txt = String(v ?? '').replace(/,/g, '').trim()
  if (!txt) { planner.amount = 0; planner.plan = []; return }
  const n = Number(txt)
  if (!Number.isFinite(n) || n<=0) { planner.amount = 0; planner.plan = []; return }
  planner.amount = n
  if (planner.openItems && planner.openItems.length>0) scheduleReplan()
})

function storageKey(){ return `embed_cards:${activeSessionId.value || 'default'}` }
function saveEmbeds(){
  try{
    const embeds = messages.filter((m:any)=>m && m.kind==='embed')
    sessionStorage.setItem(storageKey(), JSON.stringify(embeds))
  }catch{}
}
function loadEmbeds(){
  try{
    const raw = sessionStorage.getItem(storageKey())
    if (!raw) return
    const arr = JSON.parse(raw)
    if (Array.isArray(arr)) arr.forEach((e:any)=> messages.push(e))
  }catch{}
}
function pushEmbed(key:string, title:string){
  messages.push({ kind:'embed', key, title })
  saveEmbeds()
  nextTick().then(scrollToBottom)
}

function scrollToBottom(){
  const el = chatBoxRef.value
  if (el) el.scrollTop = el.scrollHeight
}

function thousandFmt(val:any){ try{ const n=Number(val); return Number.isFinite(n)? n.toLocaleString('ja-JP'): val }catch{ return val } }
function thousandParse(val:any){ try{ return String(val).replace(/,/g,'') }catch{ return val } }
function formatAmount(n:any){ const v=Number(n||0); return Number.isFinite(v)? v.toLocaleString('ja-JP'): '0' }

function resetPlanner(){
  cancelScheduledReplan()
  planner.intent=null; planner.candidates=[]; planner.partnerId='';
  planner.amount=0; planner.amountText=''; planner.currency='JPY';
  planner.paymentDate=''; planner.bankAccountCode='';
  planner.openItems=[]; planner.plan=[]; planner.planning=false; planner.committing=false;
}
</script>
<style scoped>
.chatkit-wrap {
  position: fixed;
  inset: 0;
  display: flex;
  height: 100vh;
  width: 100vw;
  background: var(--color-page-bg);
  color: var(--color-text-primary);
  font-family: var(--font-family-base);
}

.sidebar {
  width: 260px;
  background: var(--color-sidebar-bg);
  display: flex;
  flex-direction: column;
  color: rgba(255, 255, 255, 0.9);
  box-shadow: 12px 0 28px rgba(15, 23, 42, 0.25);
  z-index: 20;
}

.sidebar-header {
  padding: 26px 22px 18px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.brand-title {
  font-size: 18px;
  font-weight: var(--font-weight-semibold);
  letter-spacing: 0.04em;
}

.brand-sub {
  margin-top: 6px;
  font-size: 12px;
  opacity: 0.7;
}

.sidebar-scroll {
  flex: 1;
  overflow-y: auto;
  padding: 18px 0 28px;
}

.section {
  padding: 0 16px 24px;
}

.section-title {
  font-size: 11px;
  letter-spacing: 0.18em;
  text-transform: uppercase;
  color: rgba(255, 255, 255, 0.5);
  margin-bottom: 12px;
}

.session-actions {
  padding-top: 12px;
}

:deep(.sidebar .el-button) {
  width: 100%;
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.14);
  border-color: transparent;
  color: #fff;
}

:deep(.sidebar .el-button:hover) {
  background: rgba(255, 255, 255, 0.22);
}

:deep(.sidebar .el-menu) {
  background: transparent;
  border-right: none;
}

:deep(.sidebar .el-menu-item),
:deep(.sidebar .el-sub-menu__title) {
  height: 40px;
  border-radius: 10px;
  margin: 2px 0;
  color: rgba(255, 255, 255, 0.75);
  font-size: 13px;
}

:deep(.sidebar .el-menu-item:hover),
:deep(.sidebar .el-sub-menu__title:hover) {
  background: rgba(255, 255, 255, 0.12);
  color: #fff;
}

:deep(.sidebar .el-menu-item.is-active) {
  background: rgba(255, 255, 255, 0.2);
  color: #fff;
}

:deep(.sidebar .el-sub-menu__icon-arrow) {
  color: rgba(255, 255, 255, 0.45);
}

.main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  background: var(--color-page-bg);
}

.main-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 36px 40px 20px;
}

.header-left {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.page-title {
  font-size: 24px;
  font-weight: var(--font-weight-semibold);
  color: #1f2937;
}

.page-subtitle {
  color: var(--color-text-secondary);
  font-size: 13px;
}

.header-right {
  display: flex;
  align-items: center;
  gap: 16px;
}

.lang-select {
  width: 120px;
}

.profile-box {
  display: flex;
  align-items: center;
  gap: 16px;
}

.company-badge {
  background: rgba(59, 130, 246, 0.16);
  color: var(--color-primary);
  padding: 6px 14px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: var(--font-weight-medium);
  letter-spacing: 0.02em;
}

.user-chip {
  display: flex;
  align-items: center;
  gap: 12px;
}

.avatar {
  width: 42px;
  height: 42px;
  border-radius: 50%;
  background: var(--color-primary);
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: var(--font-weight-semibold);
  letter-spacing: 0.04em;
}

.user-meta {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
}

.user-name {
  font-weight: var(--font-weight-semibold);
  color: #1f2937;
  font-size: 14px;
}

.user-role {
  font-size: 12px;
  color: var(--color-text-secondary);
}

.workspace {
  flex: 1;
  display: flex;
  gap: 26px;
  padding: 0 40px 36px;
  min-height: 0;
  position: relative;
}

.workspace.withDock {
  padding-right: 24px;
}

.chat-card {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--color-card-bg);
  border-radius: 22px;
  box-shadow: var(--shadow-card);
  overflow: hidden;
}

.chat-header {
  padding: 26px 28px 12px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.06);
}

.chat-title {
  font-size: 18px;
  font-weight: var(--font-weight-semibold);
  color: #1f2937;
}

.chat-hint {
  margin-top: 4px;
  font-size: 13px;
  color: var(--color-text-secondary);
}

.chat-content {
  flex: 1;
  padding: 26px 28px;
  overflow-y: auto;
  background: linear-gradient(180deg, #ffffff 0%, #f8fafc 100%);
}

.msg {
  display: flex;
  margin-bottom: 16px;
}

.msg.user {
  justify-content: flex-end;
}

.msg.assistant {
  justify-content: flex-start;
}

.bubble {
  max-width: 70%;
  padding: 12px 16px;
  border-radius: 18px;
  font-size: 14px;
  line-height: 1.65;
  word-break: break-word;
  box-shadow: var(--shadow-soft);
  display: flex;
  gap: 12px;
  align-items: flex-start;
  justify-content: space-between;
}

.msg.user .bubble {
  background: var(--color-primary);
  color: #fff;
}

.msg.assistant .bubble {
  background: #ffffff;
  color: #1f2937;
}

.bubble-text {
  flex: 1;
  white-space: pre-wrap;
}

.msg-tag {
  cursor: pointer;
  flex-shrink: 0;
  margin-top: 2px;
  white-space: nowrap;
}

.msg.status-success .bubble {
  border-left: 4px solid #16a34a;
}

.msg.status-error .bubble {
  border-left: 4px solid #dc2626;
}

.empty {
  margin-top: 40px;
  text-align: center;
  color: var(--color-text-secondary);
  font-size: 13px;
}

.chat-input {
  padding: 20px 28px 24px;
  border-top: 1px solid rgba(15, 23, 42, 0.06);
  background: #fff;
}

:deep(.chat-input .el-textarea__inner) {
  background: #f8fafc;
  border-radius: 14px;
  border: 1px solid var(--color-divider);
  font-size: 14px;
  min-height: 96px;
  padding: 14px 16px;
}

:deep(.chat-input .el-textarea__inner:focus) {
  border-color: rgba(59, 130, 246, 0.6);
  box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.12);
}

.chat-actions {
  margin-top: 14px;
  display: flex;
  justify-content: flex-end;
}

.rcpt-toolbar {
  margin: 6px 0 16px;
  display: flex;
  flex-wrap: wrap;
  column-gap: 16px;
  row-gap: 10px;
  align-items: center;
}

.w-partner {
  width: 320px;
}

.w-amount {
  width: 180px;
}

.w-date {
  width: 180px;
}

.w-currency {
  width: 120px;
}

.w-account {
  width: 280px;
}

.todo-empty {
  color: var(--color-text-secondary);
}

:deep(.planner-dialog .el-dialog__body) {
  padding: 24px 28px;
  background: #ffffff !important;
  border-radius: 18px;
  border: 1px solid #e2e8f0;
  box-shadow: var(--shadow-card);
}

:deep(.planner-dialog .el-form-item__label) {
  color: #475569;
}

.planner-card {
  border-radius: 18px;
  box-shadow: var(--shadow-card);
  overflow: hidden;
}

::deep(.planner-card .el-card__header) {
  padding: 20px 24px;
  border-bottom: 1px solid #e2e8f0;
  background: #f8fafc;
}

::deep(.planner-card .el-card__body) {
  padding: 0;
}

.planner-body {
  padding: 24px 28px;
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.planner-empty {
  margin: 0;
  color: #6b7280;
}

.planner-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 16px;
  margin-top: 4px;
}

.planner-summary {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  color: #334155;
}

.planner-warning {
  color: #d93025;
}

.planner-footer-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

@media (max-width: 1360px) {
  .sidebar {
    width: 240px;
  }

  .main-header {
    padding: 28px 28px 16px;
  }

  .workspace {
    padding: 0 28px 24px;
  }
}

@media (max-width: 960px) {
  .chatkit-wrap {
    flex-direction: column;
  }

  .sidebar {
    width: 100%;
    height: auto;
    box-shadow: none;
  }

  .sidebar-scroll {
    max-height: 260px;
  }

  .main {
    height: calc(100vh - 260px);
  }
}
</style>
<style>
/* Global overrides for embedded Element Plus dialogs */
.el-overlay-dialog {
  display: flex !important;
  align-items: center !important;
  justify-content: center !important;
}

.el-overlay-dialog .el-dialog.embed-dialog {
  margin: 0 !important;
}

.el-dialog.embed-dialog {
  background: transparent !important;
  box-shadow: none !important;
  border: none !important;
  padding: 0 !important;
  width: auto !important;
  max-width: 96vw;
  height: auto !important;
  max-height: 94vh;
}

.el-dialog.embed-dialog .el-dialog__header {
  display: none !important;
}

.el-dialog.embed-dialog .el-dialog__body {
  padding: 0 !important;
  overflow: visible;
  display: inline-block;
  width: auto;
  min-width: 780px;
  max-width: 1280px;
  background: transparent;
}

.el-dialog.embed-dialog.planner-dialog {
  background: #ffffff !important;
  border: 1px solid #e2e8f0 !important;
  box-shadow: var(--shadow-card);
  border-radius: 18px;
}

.el-dialog.embed-dialog.planner-dialog .el-dialog__body {
  padding: 24px 28px !important;
  background: #ffffff !important;
  border-radius: inherit;
  box-shadow: none;
}

@media (max-width: 900px) {
  .el-dialog.embed-dialog .el-dialog__body {
    min-width: 320px;
    max-width: 92vw;
  }
}
</style>
