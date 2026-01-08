// 简单安全存储：在 localStorage 不可用时退回 sessionStorage 或内存
const memory: Record<string, string> = {}

export function getItem(key: string): string | null {
  try { return window.localStorage.getItem(key) } catch {}
  try { return window.sessionStorage.getItem(key) } catch {}
  return Object.prototype.hasOwnProperty.call(memory, key) ? memory[key] : null
}

export function setItem(key: string, val: string) {
  try { window.localStorage.setItem(key, val); return } catch {}
  try { window.sessionStorage.setItem(key, val); return } catch {}
  memory[key] = val
}

export function removeItem(key: string) {
  try { window.localStorage.removeItem(key); return } catch {}
  try { window.sessionStorage.removeItem(key); return } catch {}
  delete memory[key]
}

export default { getItem, setItem, removeItem }


