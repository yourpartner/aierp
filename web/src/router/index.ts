import { createRouter, createWebHistory, type Router, type RouteRecordRaw } from 'vue-router';
import store from '../utils/storage'
import { getAllRoutes, getRoutesForModules, findModuleIdForPath } from './moduleRoutes'
import { loadEditionInfo } from '../stores/edition'

// ============ 基础路由（始终加载）============
const baseRoutes: RouteRecordRaw[] = [
  { path: '/', redirect: '/chat' },
  { path: '/login', component: () => import('../views/Login.vue') },
  { path: '/chat', component: () => import('../views/ChatKit.vue'), meta: { requiresAuth: true } },
]

// ============ 创建路由实例 ============
// 默认加载所有路由以保持向后兼容
// 未来可以改为仅加载基础路由，然后动态添加
const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    ...baseRoutes,
    ...getAllRoutes() // 默认加载所有模块路由（向后兼容）
  ]
});

// ============ 动态路由管理 ============
let dynamicRoutesLoaded = false

/**
 * 根据启用的模块动态加载路由
 * @param enabledModules 启用的模块ID列表
 */
export async function loadDynamicRoutes(enabledModules: string[]): Promise<void> {
  if (dynamicRoutesLoaded) {
    console.log('[Router] Dynamic routes already loaded')
    return
  }
  
  const routes = getRoutesForModules(enabledModules)
  console.log(`[Router] Loading ${routes.length} routes for ${enabledModules.length} modules`)
  
  for (const route of routes) {
    // vue-router hasRoute() checks by name, so use path-based check for our setup
    if (!router.getRoutes().some(r => r.path === route.path)) router.addRoute(route)
  }
  
  dynamicRoutesLoaded = true
}

/**
 * 检查路由是否已注册
 */
export function isRouteRegistered(path: string): boolean {
  return router.getRoutes().some(r => r.path === path)
}

/**
 * 获取所有已注册的路由路径
 */
export function getRegisteredRoutePaths(): string[] {
  return router.getRoutes().map(r => r.path)
}

// ============ 路由守卫 ============
router.beforeEach(async (to) => {
  if (to.meta && (to.meta as any).requiresAuth) {
    const token = store.getItem('auth_token');
    const hasToken = !!token;
    if (!hasToken) return { path: '/login', query: { redirect: to.fullPath } };
    try {
      const [, raw] = (token as string).split('.')
      const base64 = raw.replace(/-/g,'+').replace(/_/g,'/')
      const pad = '='.repeat((4 - (base64.length % 4)) % 4)
      const json = JSON.parse(atob(base64 + pad))
      const expMs = Number(json.exp) * 1000
      if (!Number.isFinite(expMs) || Date.now() > expMs) {
        store.removeItem('auth_token')
        return { path: '/login', query: { redirect: to.fullPath } }
      }
    } catch {
      store.removeItem('auth_token')
      return { path: '/login', query: { redirect: to.fullPath } }
    }

    // 模块隔离：未启用的模块路由禁止访问（但加载失败时保持向后兼容）
    try {
      const info = await loadEditionInfo()
      if (info && Array.isArray(info.enabledModules)) {
        // 可选：确保动态路由已加载（当前仍默认加载全部路由，但这里会修正 addRoute 判重逻辑）
        await loadDynamicRoutes(info.enabledModules)

        const moduleId = findModuleIdForPath(to.path)
        if (moduleId && !info.enabledModules.includes(moduleId)) {
          console.warn(`[Router] Blocked route "${to.path}" because module "${moduleId}" is not enabled`)
          return { path: '/chat' }
        }
      }
    } catch {
      // ignore (fallback to old behavior)
    }
  }
  return true
});

export default router;
