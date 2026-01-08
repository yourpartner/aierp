import api from '../api'

/**
 * 菜单配置项
 */
export interface MenuConfig {
  id: string
  label: string
  icon?: string
  path: string
  parentId?: string
  order: number
  permission?: string
}

/**
 * 版本信息
 */
export interface EditionInfo {
  type: string
  displayName: string
  enabledModules: string[]
  menus: MenuConfig[]
}

/**
 * 获取当前版本信息和启用的模块
 */
export async function getEditionInfo(): Promise<EditionInfo> {
  const resp = await api.get('/edition')
  return resp.data
}

/**
 * 获取启用的模块ID列表
 */
export async function getEnabledModules(): Promise<string[]> {
  const resp = await api.get('/edition/modules')
  return resp.data.modules
}

/**
 * 获取所有启用的菜单配置
 */
export async function getMenus(): Promise<MenuConfig[]> {
  const resp = await api.get('/edition/menus')
  return resp.data.menus
}

/**
 * 检查模块是否启用
 */
export function isModuleEnabled(modules: string[], moduleId: string): boolean {
  return modules.includes(moduleId)
}

/**
 * 将扁平菜单列表构建为树形结构
 */
export interface MenuTreeNode extends MenuConfig {
  children: MenuTreeNode[]
}

export function buildMenuTree(menus: MenuConfig[]): MenuTreeNode[] {
  const map = new Map<string, MenuTreeNode>()
  const roots: MenuTreeNode[] = []

  // 首先创建所有节点
  for (const menu of menus) {
    map.set(menu.id, { ...menu, children: [] })
  }

  // 构建树
  for (const menu of menus) {
    const node = map.get(menu.id)!
    if (menu.parentId && map.has(menu.parentId)) {
      map.get(menu.parentId)!.children.push(node)
    } else if (!menu.parentId || menu.path) {
      // 没有父节点或有路径的作为根节点
      roots.push(node)
    }
  }

  // 按order排序
  const sortNodes = (nodes: MenuTreeNode[]): MenuTreeNode[] => {
    return nodes
      .sort((a, b) => a.order - b.order)
      .map(node => ({
        ...node,
        children: sortNodes(node.children)
      }))
  }

  return sortNodes(roots)
}

/**
 * 根据权限过滤菜单
 */
export function filterMenusByPermission(
  menus: MenuConfig[],
  userPermissions: string[]
): MenuConfig[] {
  return menus.filter(menu => {
    if (!menu.permission) return true
    return userPermissions.includes(menu.permission)
  })
}

