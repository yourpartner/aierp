import type { RouteRecordRaw } from 'vue-router'

/**
 * 模块路由配置
 * 
 * 注意：大部分业务页面通过 ChatKit 弹窗嵌入方式访问，不需要独立路由。
 * 这里只保留系统必须的路由：
 * 1. 核心入口（登录、主页）
 * 2. 通过 ChatKit 路由兜底访问的页面
 * 3. 独立于 ChatKit 的模块（员工门户等）
 * 4. 移动端专用页面
 */

// 核心模块路由 - 始终加载
export const coreRoutes: RouteRecordRaw[] = [
  { path: '/', redirect: '/chat' },
  { path: '/login', component: () => import('../views/Login.vue') },
  { path: '/chat', component: () => import('../views/ChatKit.vue'), meta: { requiresAuth: true } },
  // ChatKit 路由兜底访问的页面
  { path: '/company/settings', component: () => import('../views/CompanySettings.vue'), meta: { requiresAuth: true } },
  { path: '/system/users', component: () => import('../views/UsersList.vue'), meta: { requiresAuth: true } },
  { path: '/system/roles', component: () => import('../views/RolesList.vue'), meta: { requiresAuth: true } },
]

// 财务核心模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const financeCoreRoutes: RouteRecordRaw[] = []

// 财务扩展模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const financeExtRoutes: RouteRecordRaw[] = []

// HR核心模块路由 - 部分通过 ChatKit 路由兜底访问
export const hrCoreRoutes: RouteRecordRaw[] = [
  { path: '/hr/departments', component: () => import('../views/OrganizationChart.vue'), meta: { requiresAuth: true } },
  { path: '/hr/employees', component: () => import('../views/EmployeesList.vue'), meta: { requiresAuth: true } },
  { path: '/hr/employee/new', component: () => import('../views/EmployeeForm.vue'), meta: { requiresAuth: true } },
  { path: '/cert/request', component: () => import('../views/CertificateRequestForm.vue'), meta: { requiresAuth: true } },
]

// 薪酬模块路由
export const payrollRoutes: RouteRecordRaw[] = [
  { path: '/hr/resident-tax', component: () => import('../views/ResidentTaxList.vue'), meta: { requiresAuth: true } },
]

// AI核心模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const aiCoreRoutes: RouteRecordRaw[] = []

// 库存模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const inventoryRoutes: RouteRecordRaw[] = []

// CRM模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const crmRoutes: RouteRecordRaw[] = []

// 销售模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const salesRoutes: RouteRecordRaw[] = []

// 采购模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const purchaseRoutes: RouteRecordRaw[] = []

// 固定资产模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const fixedAssetRoutes: RouteRecordRaw[] = []

// Moneytree模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const moneytreeRoutes: RouteRecordRaw[] = []

// 通知模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const notificationRoutes: RouteRecordRaw[] = []

// 其他功能路由 - 移动端专用
export const miscRoutes: RouteRecordRaw[] = [
  { path: '/mobile/capture', component: () => import('../views/MobileCapture.vue'), meta: { requiresAuth: true } },
]

// 人才派遣 - 资源池模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const staffingResourcePoolRoutes: RouteRecordRaw[] = []

// 人才派遣 - 案件模块路由
export const staffingProjectRoutes: RouteRecordRaw[] = [
  { path: '/staffing/projects', component: () => import('../views/staffing/ProjectsList.vue'), meta: { requiresAuth: true } },
]

// 人才派遣 - 合同模块路由
export const staffingContractRoutes: RouteRecordRaw[] = [
  { path: '/staffing/contracts', component: () => import('../views/staffing/ContractsList.vue'), meta: { requiresAuth: true } },
]

// 人才派遣 - 勤怠連携モジュール路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const staffingTimesheetRoutes: RouteRecordRaw[] = []

// 人才派遣 - 請求管理モジュール路由
export const staffingBillingRoutes: RouteRecordRaw[] = [
  { path: '/staffing/invoices', component: () => import('../views/staffing/InvoicesList.vue'), meta: { requiresAuth: true } },
]

// 人才派遣 - 分析レポートモジュール路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const staffingAnalyticsRoutes: RouteRecordRaw[] = []

// 人才派遣 - 邮件自动化模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const staffingEmailRoutes: RouteRecordRaw[] = []

// 人才派遣 - AI智能助手模块路由 - 已通过 ChatKit 弹窗访问，不需要独立路由
export const staffingAiRoutes: RouteRecordRaw[] = []

// 员工自主门户路由 - 独立于 ChatKit，需要保留
export const staffingPortalRoutes: RouteRecordRaw[] = [
  { path: '/portal', redirect: '/portal/dashboard' },
  { path: '/portal/dashboard', component: () => import('../views/portal/PortalDashboard.vue'), meta: { requiresAuth: true } },
  { path: '/portal/timesheet', component: () => import('../views/portal/PortalTimesheet.vue'), meta: { requiresAuth: true } },
  { path: '/portal/payslip', component: () => import('../views/portal/PortalPayslips.vue'), meta: { requiresAuth: true } },
  { path: '/portal/certificates', component: () => import('../views/portal/PortalCertificates.vue'), meta: { requiresAuth: true } },
  // 个人事业主专属
  { path: '/portal/orders', component: () => import('../views/portal/PortalOrders.vue'), meta: { requiresAuth: true } },
  { path: '/portal/invoices', component: () => import('../views/portal/PortalInvoices.vue'), meta: { requiresAuth: true } },
  { path: '/portal/payments', component: () => import('../views/portal/PortalPayments.vue'), meta: { requiresAuth: true } },
]

/**
 * 模块ID到路由的映射
 */
export const moduleRouteMap: Record<string, RouteRecordRaw[]> = {
  // 核心模块（始终加载）
  'auth_core': coreRoutes,
  'finance_core': financeCoreRoutes,
  'hr_core': hrCoreRoutes,
  'ai_core': aiCoreRoutes,
  
  // 标准版模块
  'finance_ext': financeExtRoutes,
  'payroll': payrollRoutes,
  'inventory': inventoryRoutes,
  'crm': crmRoutes,
  'sales': salesRoutes,
  'purchase': purchaseRoutes,
  'fixed_assets': fixedAssetRoutes,
  'moneytree': moneytreeRoutes,
  'notifications': notificationRoutes,
  
  // 人才派遣版模块
  'staffing_resource_pool': staffingResourcePoolRoutes,
  'staffing_project': staffingProjectRoutes,
  'staffing_contract': staffingContractRoutes,
  'staffing_timesheet': staffingTimesheetRoutes,
  'staffing_billing': staffingBillingRoutes,
  'staffing_analytics': staffingAnalyticsRoutes,
  'staffing_email': staffingEmailRoutes,
  'staffing_portal': staffingPortalRoutes,
  'staffing_ai': staffingAiRoutes,
}

/**
 * 获取指定模块的路由
 */
export function getModuleRoutes(moduleId: string): RouteRecordRaw[] {
  return moduleRouteMap[moduleId] ?? []
}

/**
 * 获取多个模块的路由
 */
export function getRoutesForModules(moduleIds: string[]): RouteRecordRaw[] {
  const routes: RouteRecordRaw[] = []
  for (const id of moduleIds) {
    routes.push(...getModuleRoutes(id))
  }
  return routes
}

/**
 * 获取所有可用路由（用于开发/调试）
 */
export function getAllRoutes(): RouteRecordRaw[] {
  const allRoutes: RouteRecordRaw[] = []
  for (const routes of Object.values(moduleRouteMap)) {
    allRoutes.push(...routes)
  }
  return allRoutes
}

function normalizePath(path: string): string {
  if (!path) return '/'
  if (path === '/') return '/'
  return path.endsWith('/') ? path.slice(0, -1) : path
}

function matchPathPattern(pattern: string, actual: string): boolean {
  const p = normalizePath(pattern)
  const a = normalizePath(actual)
  if (p === a) return true

  const pSeg = p.split('/').filter(Boolean)
  const aSeg = a.split('/').filter(Boolean)
  if (pSeg.length !== aSeg.length) return false

  for (let i = 0; i < pSeg.length; i++) {
    const ps = pSeg[i]
    const as = aSeg[i]
    if (ps.startsWith(':')) continue
    if (ps !== as) return false
  }
  return true
}

/**
 * 根据路径反查所属模块（用于"未启用模块不可访问"的路由守卫）
 */
export function findModuleIdForPath(path: string): string | null {
  const actual = normalizePath(path)
  for (const [moduleId, routes] of Object.entries(moduleRouteMap)) {
    for (const r of routes) {
      if (typeof r.path !== 'string') continue
      if (matchPathPattern(r.path, actual)) return moduleId
    }
  }
  return null
}
