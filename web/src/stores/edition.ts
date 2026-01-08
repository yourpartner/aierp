import { ref, computed } from 'vue'
import { getEditionInfo, buildMenuTree, type EditionInfo, type MenuConfig, type MenuTreeNode } from '../api/edition'

/**
 * 版本和模块状态管理
 */
const editionInfo = ref<EditionInfo | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)

/**
 * 加载版本信息
 */
export async function loadEditionInfo(): Promise<EditionInfo | null> {
  if (editionInfo.value) {
    return editionInfo.value
  }
  
  loading.value = true
  error.value = null
  
  try {
    editionInfo.value = await getEditionInfo()
    console.log('[Edition] Loaded:', editionInfo.value.type, 'with', editionInfo.value.enabledModules.length, 'modules')
    return editionInfo.value
  } catch (e: any) {
    error.value = e.message || 'Failed to load edition info'
    console.error('[Edition] Failed to load:', e)
    return null
  } finally {
    loading.value = false
  }
}

/**
 * 获取版本类型
 */
export const editionType = computed(() => editionInfo.value?.type ?? 'standard')

/**
 * 获取版本显示名称
 */
export const editionDisplayName = computed(() => editionInfo.value?.displayName ?? 'AI-ERP')

/**
 * 获取启用的模块列表
 */
export const enabledModules = computed(() => editionInfo.value?.enabledModules ?? [])

/**
 * 获取菜单配置
 */
export const menus = computed<MenuConfig[]>(() => editionInfo.value?.menus ?? [])

/**
 * 获取菜单树
 */
export const menuTree = computed<MenuTreeNode[]>(() => buildMenuTree(menus.value))

/**
 * 检查模块是否启用
 */
export function isModuleEnabled(moduleId: string): boolean {
  return enabledModules.value.includes(moduleId)
}

/**
 * 检查是否为特定版本
 */
export function isEdition(type: 'standard' | 'staffing' | 'retail'): boolean {
  return editionType.value.toLowerCase() === type
}

/**
 * 根据模块ID获取该模块的菜单
 */
export function getModuleMenus(moduleId: string): MenuConfig[] {
  // 这里可以根据菜单ID前缀来过滤，或者在后端提供更详细的映射
  return menus.value.filter(m => m.id.startsWith(`menu_${moduleId}`))
}

/**
 * 重新加载版本信息
 */
export async function reloadEditionInfo(): Promise<void> {
  editionInfo.value = null
  await loadEditionInfo()
}

/**
 * 导出状态
 */
export function useEdition() {
  return {
    editionInfo,
    loading,
    error,
    editionType,
    editionDisplayName,
    enabledModules,
    menus,
    menuTree,
    isModuleEnabled,
    isEdition,
    loadEditionInfo,
    reloadEditionInfo
  }
}

