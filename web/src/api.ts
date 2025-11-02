import axios from 'axios'

// 本地存储安全封装：在某些环境(localStorage被策略禁用/iframe沙箱)会抛出 SecurityError
const memStore: Record<string, string> = {}
function safeGet(key: string): string | null {
  try { return localStorage.getItem(key) } catch {}
  try { return sessionStorage.getItem(key) } catch {}
  return Object.prototype.hasOwnProperty.call(memStore, key) ? memStore[key] : null
}
function safeSet(key: string, val: string) {
  try { localStorage.setItem(key, val); return } catch {}
  try { sessionStorage.setItem(key, val); return } catch {}
  memStore[key] = val
}
function safeRemove(key: string) {
  try { localStorage.removeItem(key); return } catch {}
  try { sessionStorage.removeItem(key); return } catch {}
  delete memStore[key]
}

function decodeJwt(token: string): any | null {
  try {
    if (!token) return null
    const parts = token.split('.')
    if (parts.length < 2) return null
    const raw = parts[1]
    const base64 = raw.replace(/-/g, '+').replace(/_/g, '/')
    const pad = '='.repeat((4 - (base64.length % 4)) % 4)
    const json = atob(base64 + pad)
    return JSON.parse(json)
  } catch { return null }
}

function isTokenExpired(token: string): boolean {
  try {
    const payload = decodeJwt(token)
    if (!payload || typeof payload.exp === 'undefined' || payload.exp === null) return true
    const expMs = Number(payload.exp) * 1000
    return Number.isFinite(expMs) ? Date.now() > expMs : true
  } catch { return true }
}

// 简单的 axios 实例：统一在拦截器里规范化为 /api 前缀
const api = axios.create({ baseURL: '' })
// 由路由守卫统一处理登录与过期跳转，避免首屏脚本尚未加载时的提前跳转

// 禁用 GET 的缓存，避免 schema/ui 被浏览器或代理缓存
api.interceptors.request.use((config) => {
  // 统一规范路径到 /api 前缀（避免各处写成以 / 开头而绕过 baseURL 的问题）
  try {
    const raw = String(config.url || '')
    if (!/^https?:\/\//i.test(raw)) {
      let path = raw
      if (!path.startsWith('/')) path = '/' + path
      if (!path.startsWith('/api/')) path = '/api' + path
      config.url = path
    }
  } catch {}

  if ((config.method || 'get').toString().toLowerCase() === 'get') {
    const ts = Date.now().toString()
    const sep = (config.url || '').includes('?') ? '&' : '?'
    config.url = `${config.url || ''}${sep}_=${ts}`
    config.headers = config.headers || {}
    ;(config.headers as any)['Cache-Control'] = 'no-cache'
    ;(config.headers as any)['Pragma'] = 'no-cache'
  }
  // 注入 token 与公司代码
  const token = safeGet('auth_token')
  const company = safeGet('company_code')
  const openai = safeGet('openai_key')
  config.headers = config.headers || {}
  // 登录请求放行：不要求已有 token，也不注入 Authorization
  try {
    const base = (config.baseURL || '').startsWith('http')
      ? (config.baseURL as string)
      : (window.location.origin + (config.baseURL || ''))
    const u = new URL(config.url || '', base)
    if (u.pathname === '/auth/login') {
      // 登录请求不携带公司头，避免不必要的预检复杂度
      if ((config.headers as any)['x-company-code']) delete (config.headers as any)['x-company-code']
      if (company) (config.headers as any)['x-company-code'] = company
      if (openai) (config.headers as any)['x-openai-key'] = openai
      return config
    }
  } catch {}
  // 过期检测交给响应拦截器；此处仅在存在且有效时注入
  if (!token || isTokenExpired(token)) return config
  ;(config.headers as any)['Authorization'] = `Bearer ${token}`
  if (company) (config.headers as any)['x-company-code'] = company
  if (openai) (config.headers as any)['x-openai-key'] = openai
  const claims = decodeJwt(token) || {}
  try {
    const uid = claims.uid || claims.userId || claims.user_id || claims.sub
    if (uid && !(config.headers as any)['x-user-id']) (config.headers as any)['x-user-id'] = String(uid)
    const dept = claims.deptId || claims.dept_id || claims.departmentId
    if (dept && !(config.headers as any)['x-dept-id']) (config.headers as any)['x-dept-id'] = String(dept)
    const roles = claims.roles
    if (roles && !(config.headers as any)['x-roles']) {
      (config.headers as any)['x-roles'] = Array.isArray(roles) ? roles.join(',') : String(roles)
    }
    const caps = claims.caps || claims.permissions
    if (caps && !(config.headers as any)['x-caps']) {
      (config.headers as any)['x-caps'] = Array.isArray(caps) ? caps.join(',') : String(caps)
    }
  } catch {}
  return config
})
// 统一处理 401：清除本地 token 并跳转登录
api.interceptors.response.use(
  (resp) => resp,
  (error) => {
    const status = error?.response?.status
    if (status === 401) {
      try { safeRemove('auth_token') } catch {}
      const redirect = encodeURIComponent(location.pathname + location.search)
      // 避免死循环：当前已在 /login 则不再跳
      if (!location.pathname.startsWith('/login')) {
        location.href = `/login?redirect=${redirect}`
      }
    }
    return Promise.reject(error)
  }
)
api.defaults.headers.common['x-company-code'] = safeGet('company_code') || 'JP01'

export default api


