import { createRouter, createWebHistory } from 'vue-router';
import store from '../utils/storage'

const SchemaEditor = () => import('../views/SchemaEditor.vue');
const VoucherForm = () => import('../views/VoucherForm.vue');
const VouchersList = () => import('../views/VouchersList.vue');
const AccountForm = () => import('../views/AccountForm.vue');
const AccountsList = () => import('../views/AccountsList.vue');
const BusinessPartnerForm = () => import('../views/BusinessPartnerForm.vue');
const DepartmentTree = () => import('../views/DepartmentTree.vue');
const EmployeesList = () => import('../views/EmployeesList.vue');
const EmployeeForm = () => import('../views/EmployeeForm.vue');
const BusinessPartnersList = () => import('../views/BusinessPartnersList.vue');
const BankReceipt = () => import('../views/BankReceipt.vue');
const BankPayment = () => import('../views/BankPayment.vue');
const Login = () => import('../views/Login.vue');
const ChatKit = () => import('../views/ChatKit.vue');
const PolicyEditor = () => import('../views/PolicyEditor.vue');
const TimesheetForm = () => import('../views/TimesheetForm.vue');
const TimesheetsList = () => import('../views/TimesheetsList.vue');
const ApprovalsInbox = () => import('../views/ApprovalsInbox.vue');
const CertificateRequestForm = () => import('../views/CertificateRequestForm.vue');
const CertificateRequestsList = () => import('../views/CertificateRequestsList.vue');
const MobileCapture = () => import('../views/MobileCapture.vue');
const MaterialsList = () => import('../views/MaterialsList.vue');
const MaterialForm = () => import('../views/MaterialForm.vue');
const WarehousesList = () => import('../views/WarehousesList.vue');
const WarehouseForm = () => import('../views/WarehouseForm.vue');
const BinsList = () => import('../views/BinsList.vue');
const BinForm = () => import('../views/BinForm.vue');
const StockStatuses = () => import('../views/StockStatuses.vue');
const BatchesList = () => import('../views/BatchesList.vue');
const BatchForm = () => import('../views/BatchForm.vue');
const InventoryMovement = () => import('../views/InventoryMovementSchema.vue');
const InventoryBalances = () => import('../views/InventoryBalances.vue');
const ContactsList = () => import('../views/ContactsList.vue');
const ContactForm = () => import('../views/ContactForm.vue');
const CompanySettings = () => import('../views/CompanySettings.vue');
const DealsList = () => import('../views/DealsList.vue');
const DealForm = () => import('../views/DealForm.vue');
const QuotesList = () => import('../views/QuotesList.vue');
const QuoteForm = () => import('../views/QuoteForm.vue');
const SalesOrdersList = () => import('../views/SalesOrdersList.vue');
const SalesOrderForm = () => import('../views/SalesOrderForm.vue');
const ActivitiesList = () => import('../views/ActivitiesList.vue');
const ActivityForm = () => import('../views/ActivityForm.vue');
const NotificationRuleRuns = () => import('../views/NotificationRuleRuns.vue');
const NotificationLogs = () => import('../views/NotificationLogs.vue');

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    { path: '/', redirect: '/chat' },
    { path: '/login', component: Login },
    { path: '/chat', component: ChatKit, meta: { requiresAuth: true } },
    { path: '/schema', component: SchemaEditor, meta: { requiresAuth: true } },
    { path: '/voucher/new', component: VoucherForm, meta: { requiresAuth: true } },
    { path: '/vouchers', component: VouchersList, meta: { requiresAuth: true } },
    { path: '/account/new', component: AccountForm, meta: { requiresAuth: true } },
    { path: '/accounts', component: AccountsList, meta: { requiresAuth: true } },
    { path: '/operations/bank-collect', component: BankReceipt, meta: { requiresAuth: true } },
    { path: '/operations/bank-payment', component: BankPayment, meta: { requiresAuth: true } },
    { path: '/businesspartner/new', component: BusinessPartnerForm, meta: { requiresAuth: true } },
    { path: '/businesspartners', component: BusinessPartnersList, meta: { requiresAuth: true } }
    ,{ path: '/hr/departments', component: DepartmentTree, meta: { requiresAuth: true } }
    ,{ path: '/hr/employees', component: EmployeesList, meta: { requiresAuth: true } }
    ,{ path: '/hr/employee/new', component: EmployeeForm, meta: { requiresAuth: true } }
    ,{ path: '/hr/policy/editor', component: PolicyEditor, meta: { requiresAuth: true } }
    ,{ path: '/timesheet/new', component: TimesheetForm, meta: { requiresAuth: true } }
    ,{ path: '/timesheets', component: TimesheetsList, meta: { requiresAuth: true } }
    ,{ path: '/approvals/inbox', component: ApprovalsInbox, meta: { requiresAuth: true } }
    ,{ path: '/cert/request', component: CertificateRequestForm, meta: { requiresAuth: true } }
    ,{ path: '/cert/list', component: CertificateRequestsList, meta: { requiresAuth: true } }
    ,{ path: '/mobile/capture', component: MobileCapture, meta: { requiresAuth: true } }
    ,{ path: '/materials', component: MaterialsList, meta: { requiresAuth: true } }
    ,{ path: '/material/new', component: MaterialForm, meta: { requiresAuth: true } }
    ,{ path: '/warehouses', component: WarehousesList, meta: { requiresAuth: true } }
    ,{ path: '/warehouse/new', component: WarehouseForm, meta: { requiresAuth: true } }
    ,{ path: '/bins', component: BinsList, meta: { requiresAuth: true } }
    ,{ path: '/bin/new', component: BinForm, meta: { requiresAuth: true } }
    ,{ path: '/stock-statuses', component: StockStatuses, meta: { requiresAuth: true } }
    ,{ path: '/batches', component: BatchesList, meta: { requiresAuth: true } }
    ,{ path: '/batch/new', component: BatchForm, meta: { requiresAuth: true } }
    ,{ path: '/inventory/movement', component: InventoryMovement, meta: { requiresAuth: true } }
    ,{ path: '/inventory/balances', component: InventoryBalances, meta: { requiresAuth: true } }
    ,{ path: '/crm/contacts', component: ContactsList, meta: { requiresAuth: true } }
    ,{ path: '/crm/contact/new', component: ContactForm, meta: { requiresAuth: true } }
    ,{ path: '/crm/deals', component: DealsList, meta: { requiresAuth: true } }
    ,{ path: '/crm/deal/new', component: DealForm, meta: { requiresAuth: true } }
    ,{ path: '/crm/quotes', component: QuotesList, meta: { requiresAuth: true } }
    ,{ path: '/crm/quote/new', component: QuoteForm, meta: { requiresAuth: true } }
    ,{ path: '/crm/sales-orders', component: SalesOrdersList, meta: { requiresAuth: true } }
    ,{ path: '/crm/sales-order/new', component: SalesOrderForm, meta: { requiresAuth: true } }
    ,{ path: '/crm/activities', component: ActivitiesList, meta: { requiresAuth: true } }
    ,{ path: '/crm/activity/new', component: ActivityForm, meta: { requiresAuth: true } }
    ,{ path: '/company/settings', component: CompanySettings, meta: { requiresAuth: true } }
    ,{ path: '/notifications/runs', component: NotificationRuleRuns, meta: { requiresAuth: true } }
    ,{ path: '/notifications/logs', component: NotificationLogs, meta: { requiresAuth: true } }
  ]
});

router.beforeEach((to, _from, next) => {
  if (to.meta && (to.meta as any).requiresAuth) {
    const token = store.getItem('auth_token');
    const hasToken = !!token;
    if (!hasToken) return next({ path: '/login', query: { redirect: to.fullPath } });
    try {
      const [, raw] = (token as string).split('.')
      const base64 = raw.replace(/-/g,'+').replace(/_/g,'/')
      const pad = '='.repeat((4 - (base64.length % 4)) % 4)
      const json = JSON.parse(atob(base64 + pad))
      const expMs = Number(json.exp) * 1000
      if (!Number.isFinite(expMs) || Date.now() > expMs) {
        store.removeItem('auth_token')
        return next({ path: '/login', query: { redirect: to.fullPath } })
      }
    } catch {
      store.removeItem('auth_token')
      return next({ path: '/login', query: { redirect: to.fullPath } })
    }
  }
  next();
});

export default router;
