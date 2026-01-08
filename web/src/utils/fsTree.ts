export type FsTreeOption = {
  label: string
  value: string
  disabled?: boolean
  children?: FsTreeOption[]
}

type RawFsNode = {
  code?: string
  nameJa?: string
  nameEn?: string
  parentCode?: string | null
  order?: number | null
}

type NormalizedNode = {
  code: string
  nameJa?: string
  nameEn?: string
  parentCode?: string
  order?: number
}

const treeValueKey = 'value'
const treeLabelKey = 'label'
const treeChildrenKey = 'children'
const treeDisabledKey = 'disabled'

export const defaultTreeSelectProps = {
  [treeValueKey]: 'value',
  [treeLabelKey]: 'label',
  [treeChildrenKey]: 'children',
  [treeDisabledKey]: 'disabled'
} as const

export function buildFsTreeOptions(nodes: RawFsNode[] | undefined, locale: string | undefined): FsTreeOption[] {
  if (!Array.isArray(nodes) || nodes.length === 0) return []

  const normalized: NormalizedNode[] = nodes
    .map((raw) => raw || {})
    .map((raw) => ({
      code: typeof raw.code === 'string' ? raw.code.trim() : '',
      nameJa: typeof raw.nameJa === 'string' ? raw.nameJa : undefined,
      nameEn: typeof raw.nameEn === 'string' ? raw.nameEn : undefined,
      parentCode: typeof raw.parentCode === 'string' ? raw.parentCode.trim() || undefined : undefined,
      order: typeof raw.order === 'number' ? raw.order : undefined
    }))
    .filter((node) => node.code.length > 0)

  if (normalized.length === 0) return []

  const dict = new Map<string, NormalizedNode>()
  normalized.forEach((node) => dict.set(node.code, node))

  const childrenMap = new Map<string, NormalizedNode[]>()
  normalized.forEach((node) => {
    if (node.parentCode && dict.has(node.parentCode)) {
      if (!childrenMap.has(node.parentCode)) {
        childrenMap.set(node.parentCode, [])
      }
      childrenMap.get(node.parentCode)!.push(node)
    }
  })

  const sortNodes = (a: NormalizedNode, b: NormalizedNode) => {
    const orderA = typeof a.order === 'number' ? a.order : Number.MAX_SAFE_INTEGER
    const orderB = typeof b.order === 'number' ? b.order : Number.MAX_SAFE_INTEGER
    if (orderA !== orderB) return orderA - orderB
    return a.code.localeCompare(b.code, 'ja')
  }

  const preferEnglish = (locale || '').toLowerCase().startsWith('en')
  const formatLabel = (node: NormalizedNode) => {
    const candidates = preferEnglish
      ? [node.nameEn, node.nameJa, node.code]
      : [node.nameJa, node.nameEn, node.code]
    const label = candidates.find((text) => typeof text === 'string' && text.trim().length > 0)
    return label ?? node.code
  }

  const buildOption = (node: NormalizedNode): FsTreeOption => {
    const children = (childrenMap.get(node.code) || []).sort(sortNodes)
    const option: FsTreeOption = {
      label: formatLabel(node),
      value: node.code,
      disabled: children.length > 0
    }
    if (children.length) {
      option.children = children.map(buildOption)
    }
    return option
  }

  return normalized
    .filter((node) => !node.parentCode || !dict.has(node.parentCode))
    .sort(sortNodes)
    .map(buildOption)
}


