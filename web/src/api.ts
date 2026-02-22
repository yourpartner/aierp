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

const defaultApiBase = (() => {
  // 1) Prefer build-time env (Dockerfile sets VITE_API_BASE_URL=/api for nginx reverse proxy)
  const envBase = (() => {
    try {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const v = (import.meta as any)?.env?.VITE_API_BASE_URL
      return typeof v === 'string' ? v.trim() : ''
    } catch { return '' }
  })()
  if (envBase) return envBase

  // 2) Then allow user override from storage
  let stored = (safeGet('api_base_url') || '').trim()

  // 防呆：如果当前页面是 https，但用户把 api_base_url 配成 http，会触发浏览器 Mixed Content/CORS 拦截，
  // Axios 只会报 "Network Error"，后端也看不到请求。这里自动把常见的 azurewebsites 域名升级到 https。
  if (typeof window !== 'undefined') {
    try {
      const pageProto = window.location.protocol
      if (pageProto === 'https:' && /^http:\/\//i.test(stored)) {
        // 仅对 azurewebsites.net 这类明确支持 https 的域名自动升级
        const m = stored.match(/^http:\/\/([^/]+\.azurewebsites\.net)(\/.*)?$/i)
        if (m) {
          stored = `https://${m[1]}${m[2] || ''}`
          safeSet('api_base_url', stored)
        }
      }
    } catch {}
  }

  // 3) In dev mode (localhost), 使用同源 /api，由 Vite 代理到 5179，避免跨域导致 Network Error
  if (typeof window !== 'undefined') {
    try {
      const origin = new URL(window.location.href)
      const hostname = origin.hostname
      const isLocalHost = hostname === 'localhost' || hostname === '127.0.0.1'
      if (isLocalHost) {
        stored = '/api'
        safeSet('api_base_url', stored)
        return '/api'
      }
    } catch {}
  }

  // 4) Fallback: if stored exists use it, otherwise same-origin /api (for nginx production)
  if (stored) return stored
  return '/api'
})()

const api = axios.create({ baseURL: defaultApiBase })

// 如果浏览器中已经缓存了 token，则即时恢复到默认请求头，避免首次请求缺少认证信息
;(function restoreDefaultHeaders(){
  const token = safeGet('auth_token')
  if (token && token.trim()) {
    api.defaults.headers.common['Authorization'] = `Bearer ${token.trim()}`
  }
  const company = safeGet('company_code')
  if (company && company.trim()) {
    api.defaults.headers.common['x-company-code'] = company.trim()
  }
})()
// 由路由守卫统一处理登录与过期跳转，避免首屏脚本尚未加载时的提前跳转

// 禁用 GET 的缓存，避免 schema/ui 被浏览器或代理缓存
api.interceptors.request.use((config) => {
  // 规范化路径：确保以 / 开头，移除错误的 /api 前缀（后端不使用 /api 前缀）
  try {
    const raw = String(config.url || '')
    if (!/^https?:\/\//i.test(raw)) {
      let path = raw
      if (!path.startsWith('/')) path = '/' + path
      // 后端直接注册 /users, /roles, /staffing/* 等路由（不带 /api 前缀）
      // 如果前端代码写成 /api/xxx，需要去掉 /api 前缀
      if (path.startsWith('/api/')) {
        path = path.replace(/^\/api/, '')
      }
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
  const company = safeGet('company_code')  // 用户登录时指定的公司代码
  const openai = safeGet('openai_key')
  const uiLang = safeGet('ui_lang') || 'ja'
  config.headers = config.headers || {}
  ;(config.headers as any)['x-lang'] = uiLang
  // 始终添加 company-code（如果存在）
  if (company) (config.headers as any)['x-company-code'] = company
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
  // openai key 如果存在则注入
  if (openai) (config.headers as any)['x-openai-key'] = openai
  // 过期检测交给响应拦截器；此处仅在存在且有效时注入 Authorization
  if (!token || isTokenExpired(token)) return config
  ;(config.headers as any)['Authorization'] = `Bearer ${token}`
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
// company_code 由用户登录时指定，不设默认值
const storedCompany = safeGet('company_code')
if (storedCompany) api.defaults.headers.common['x-company-code'] = storedCompany

export default api


