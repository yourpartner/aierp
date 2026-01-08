<template>
  <div class="org-chart">
    <!-- 左侧：组织树 -->
    <div class="org-tree-panel">
      <div class="org-tree-header">
        <div class="org-tree-title">
          <el-icon><OfficeBuilding /></el-icon>
          <span>組織階層</span>
        </div>
        <div class="org-tree-actions">
          <el-button size="small" type="primary" @click="addDepartment(null)">
            <el-icon><Plus /></el-icon>
          </el-button>
          <el-button size="small" @click="loadAll">
            <el-icon><Refresh /></el-icon>
          </el-button>
        </div>
      </div>

      <div class="org-tree-search">
        <el-input 
          v-model="searchKeyword" 
          placeholder="部門名で検索" 
          clearable 
          size="small"
          @input="onSearch"
        >
          <template #prefix>
            <el-icon><Search /></el-icon>
          </template>
        </el-input>
      </div>

      <div class="org-tree-content">
        <el-tree
          ref="treeRef"
          :data="treeData"
          node-key="id"
          :props="{ label: 'label', children: 'children' }"
          :expand-on-click-node="false"
          :default-expand-all="true"
          highlight-current
          draggable
          :allow-drop="allowDrop"
          :allow-drag="allowDrag"
          @node-click="onNodeClick"
          @node-drop="onNodeDrop"
        >
          <template #default="{ node, data }">
            <div class="org-tree-node" :class="getNodeClass(data)">
              <el-icon class="org-tree-node__icon">
                <OfficeBuilding v-if="data.type === 'company'" />
                <Folder v-else-if="data.type === 'department'" />
                <Medal v-else-if="data.type === 'position'" />
              </el-icon>
              <span class="org-tree-node__name">{{ data.name }}</span>
              <span class="org-tree-node__count" v-if="data.employeeCount">({{ data.employeeCount }}名)</span>
              <div class="org-tree-node__actions" v-if="data.type === 'department'" @click.stop>
                <el-button size="small" text type="primary" @click.stop="addDepartment(data)">
                  <el-icon><Plus /></el-icon>
                </el-button>
                <el-button size="small" text type="primary" @click.stop="editDepartment(data)">
                  <el-icon><Edit /></el-icon>
                </el-button>
                <el-button size="small" text type="danger" @click.stop="deleteDepartment(data)">
                  <el-icon><Delete /></el-icon>
                </el-button>
              </div>
            </div>
          </template>
        </el-tree>
      </div>
    </div>

    <!-- 右侧：员工列表 -->
    <div class="org-detail-panel">
      <div class="org-detail-header">
        <div class="org-detail-title">
          <template v-if="selectedNode">
            <el-icon>
              <Folder v-if="selectedNode.type === 'department'" />
              <Medal v-else-if="selectedNode.type === 'position'" />
            </el-icon>
            <span>{{ selectedNode.name }}</span>
            <el-tag v-if="selectedNode.type === 'position'" size="small" type="warning">役職</el-tag>
            <el-tag size="small" type="info">{{ employees.length }}名</el-tag>
          </template>
          <template v-else>
            <el-icon><User /></el-icon>
            <span>社員一覧</span>
          </template>
        </div>
        <div class="org-detail-actions" v-if="selectedNode?.type === 'department'">
          <el-button size="small" type="primary" @click="addEmployee">
            <el-icon><Plus /></el-icon>
            社員追加
          </el-button>
        </div>
      </div>

      <div class="org-detail-content">
        <div v-if="!selectedNode" class="org-detail-empty">
          <el-icon><Pointer /></el-icon>
          <span>左側の部門または役職を選択してください</span>
        </div>

        <div v-else-if="loadingEmployees" class="org-detail-loading">
          <el-skeleton :rows="5" animated />
        </div>

        <div v-else-if="!employees.length" class="org-detail-empty">
          <el-icon><User /></el-icon>
          <span v-if="selectedNode.type === 'department'">この部門に所属する社員はいません</span>
          <span v-else>この役職の社員はいません</span>
        </div>

        <div v-else class="org-employee-list">
          <div 
            v-for="emp in employees" 
            :key="emp.id" 
            class="org-employee-card"
            @click="openEmployee(emp)"
          >
            <div class="org-employee-card__info">
              <div class="org-employee-card__header">
                <span class="org-employee-card__code">{{ emp.employee_code }}</span>
                <span class="org-employee-card__name">{{ getNameKanji(emp) }}</span>
                <span class="org-employee-card__kana">{{ getNameKana(emp) }}</span>
              </div>
              <div class="org-employee-card__meta">
                <el-tag v-if="getEmploymentTypeName(emp)" size="small" :type="getEmploymentTypeTagType(emp)">
                  {{ getEmploymentTypeName(emp) }}
                </el-tag>
                <el-tag v-if="getPosition(emp)" size="small" type="warning">{{ getPosition(emp) }}</el-tag>
                <span v-if="getJoinDate(emp)" class="org-employee-card__date">
                  入社: {{ getJoinDate(emp) }}
                </span>
                <el-tag size="small" :type="isEmployeeActive(emp) ? 'success' : 'info'">
                  {{ isEmployeeActive(emp) ? '在籍' : '退職' }}
                </el-tag>
              </div>
            </div>
            <div class="org-employee-card__actions">
              <el-button size="small" type="primary" text @click.stop="openEmployee(emp)">
                <el-icon><Edit /></el-icon>
              </el-button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- 部门编辑弹窗 -->
    <el-dialog 
      v-model="showDeptDialog" 
      :title="editingDept?.id ? '部門編集' : '部門追加'" 
      width="450px" 
      append-to-body 
      destroy-on-close
    >
      <el-form :model="deptForm" label-width="100px" label-position="left">
        <el-form-item label="コード" required>
          <el-input v-model="deptForm.code" :disabled="!!editingDept?.id" placeholder="例：D001" maxlength="50" />
        </el-form-item>
        <el-form-item label="名称" required>
          <el-input v-model="deptForm.name" placeholder="例：営業部" maxlength="100" />
        </el-form-item>
        <el-form-item label="上位部門" v-if="!editingDept?.id">
          <el-input :value="parentDeptName" disabled />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showDeptDialog = false">キャンセル</el-button>
        <el-button type="primary" @click="saveDepartment" :disabled="!deptForm.code || !deptForm.name">保存</el-button>
      </template>
    </el-dialog>

    <!-- 员工编辑弹窗 -->
    <el-dialog 
      v-model="showEmployeeDialog" 
      width="auto" 
      append-to-body 
      destroy-on-close 
      class="employee-edit-dialog"
    >
      <template #header></template>
      <div class="employee-dialog-wrap">
        <EmployeeFormNew 
          v-if="showEmployeeDialog" 
          :emp-id="selectedEmployeeId || undefined" 
          @saved="onEmployeeSaved" 
        />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { 
  OfficeBuilding, Folder, User, Plus, Edit, Delete, 
  Refresh, Search, Pointer, View, Medal
} from '@element-plus/icons-vue'
import api from '../api'
import EmployeeFormNew from './EmployeeFormNew.vue'

const treeRef = ref()
const treeData = ref<any[]>([])
const departments = ref<any[]>([])
const allEmployees = ref<any[]>([])
const employees = ref<any[]>([])
const selectedNode = ref<any>(null)
const loadingEmployees = ref(false)
const searchKeyword = ref('')
const employmentTypeMap = ref<Record<string, string>>({})

// 部门编辑
const showDeptDialog = ref(false)
const editingDept = ref<any>(null)
const parentDept = ref<any>(null)
const deptForm = ref({ code: '', name: '' })

// 员工编辑
const showEmployeeDialog = ref(false)
const selectedEmployeeId = ref<string | null>(null)

const parentDeptName = computed(() => {
  if (parentDept.value) return parentDept.value.name
  return 'トップレベル'
})

async function loadAll() {
  await Promise.all([loadDepartments(), loadAllEmployees(), loadEmploymentTypes()])
  buildTree()
}

async function loadEmploymentTypes() {
  try {
    const r = await api.post('/objects/employment_type/search', { 
      page: 1, 
      pageSize: 100, 
      where: [], 
      orderBy: [{ field: 'payload->>name', dir: 'ASC' }] 
    })
    const list = r.data?.data || []
    for (const x of list) {
      const code = x.payload?.code || x.code || ''
      const name = x.payload?.name || x.name || ''
      if (code) employmentTypeMap.value[code] = name
    }
  } catch { /* ignore */ }
}

async function loadDepartments() {
  try {
    const r = await api.post('/objects/department/search', {
      page: 1,
      pageSize: 500,
      where: [],
      orderBy: [{ field: 'path', dir: 'ASC' }]
    })
    departments.value = r.data?.data || []
  } catch {
    departments.value = []
  }
}

async function loadAllEmployees() {
  try {
    const r = await api.post('/objects/employee/search', {
      page: 1,
      pageSize: 1000,
      where: [],
      orderBy: [{ field: 'employee_code', dir: 'ASC' }]
    })
    allEmployees.value = r.data?.data || []
  } catch {
    allEmployees.value = []
  }
}

function buildTree() {
  // 统计每个部门的员工和职务
  const deptEmployeeMap: Record<string, any[]> = {}
  const deptPositionMap: Record<string, Set<string>> = {}

  for (const emp of allEmployees.value) {
    const depts = emp.payload?.departments || []
    for (const d of depts) {
      const deptId = d.departmentId
      if (!deptId) continue
      if (!deptEmployeeMap[deptId]) deptEmployeeMap[deptId] = []
      deptEmployeeMap[deptId].push(emp)
      
      const position = d.position?.trim()
      if (position) {
        if (!deptPositionMap[deptId]) deptPositionMap[deptId] = new Set()
        deptPositionMap[deptId].add(position)
      }
    }
  }

  // 构建部门节点
  const list = departments.value.map((x: any) => {
    const deptId = x.id
    const deptEmployees = deptEmployeeMap[deptId] || []
    const positions = deptPositionMap[deptId] || new Set()

    // 为每个职务创建子节点
    const positionChildren: any[] = []
    for (const pos of positions) {
      const posEmployees = deptEmployees.filter(e => 
        (e.payload?.departments || []).some((d: any) => d.departmentId === deptId && d.position === pos)
      )
      positionChildren.push({
        id: `${deptId}_pos_${pos}`,
        type: 'position',
        name: pos,
        deptId: deptId,
        position: pos,
        employeeCount: posEmployees.length,
        children: []
      })
    }

    // 没有职务的员工数量
    const noPositionCount = deptEmployees.filter(e => 
      (e.payload?.departments || []).some((d: any) => d.departmentId === deptId && !d.position?.trim())
    ).length

    return {
      id: x.id,
      type: 'department',
      code: x.department_code || x.payload?.code,
      name: x.name || x.payload?.name,
      parentCode: x.payload?.parentCode || null,
      path: x.payload?.path || '',
      level: x.payload?.level || 1,
      employeeCount: deptEmployees.length,
      children: positionChildren
    }
  })

  const codeToNode = new Map(list.map(n => [n.code, n]))
  const roots: any[] = []

  for (const n of list) {
    if (n.parentCode && codeToNode.has(n.parentCode)) {
      // 插入到父部门的children开头（在职务节点之前）
      const parent = codeToNode.get(n.parentCode)
      const positionNodes = parent.children.filter((c: any) => c.type === 'position')
      const deptNodes = parent.children.filter((c: any) => c.type === 'department')
      deptNodes.push(n)
      parent.children = [...deptNodes, ...positionNodes]
    } else {
      roots.push(n)
    }
  }

  // 重新排列：子部门在前，职务在后
  function reorderChildren(node: any) {
    if (!node.children?.length) return
    const depts = node.children.filter((c: any) => c.type === 'department')
    const positions = node.children.filter((c: any) => c.type === 'position')
    node.children = [...depts, ...positions]
    for (const child of depts) {
      reorderChildren(child)
    }
  }

  for (const root of roots) {
    reorderChildren(root)
  }

  // 公司节点
  treeData.value = [{
    id: 'company',
    type: 'company',
    name: '株式会社ITBANK',
    code: 'JP01',
    children: roots
  }]
}

function getNodeClass(data: any) {
  return {
    'org-tree-node--company': data.type === 'company',
    'org-tree-node--department': data.type === 'department',
    'org-tree-node--position': data.type === 'position'
  }
}

function onSearch() {
  const q = searchKeyword.value.trim().toLowerCase()
  if (!q) {
    buildTree()
    return
  }
  const filtered = departments.value.filter((x: any) => {
    const name = (x.name || x.payload?.name || '').toLowerCase()
    const code = (x.department_code || x.payload?.code || '').toLowerCase()
    return name.includes(q) || code.includes(q)
  })
  const tempDepts = departments.value
  departments.value = filtered
  buildTree()
  departments.value = tempDepts
}

function onNodeClick(data: any) {
  if (data.type === 'company') {
    selectedNode.value = null
    employees.value = []
  } else if (data.type === 'department') {
    selectedNode.value = data
    loadEmployeesForDepartment(data.id)
  } else if (data.type === 'position') {
    selectedNode.value = data
    loadEmployeesForPosition(data.deptId, data.position)
  }
}

function loadEmployeesForDepartment(deptId: string) {
  loadingEmployees.value = true
  employees.value = allEmployees.value.filter(emp => {
    const depts = emp.payload?.departments || []
    return depts.some((d: any) => d.departmentId === deptId)
  })
  loadingEmployees.value = false
}

function loadEmployeesForPosition(deptId: string, position: string) {
  loadingEmployees.value = true
  employees.value = allEmployees.value.filter(emp => {
    const depts = emp.payload?.departments || []
    return depts.some((d: any) => d.departmentId === deptId && d.position === position)
  })
  loadingEmployees.value = false
}

function allowDrag(node: any) {
  // 只允许拖动部门
  return node.data.type === 'department'
}

function allowDrop(draggingNode: any, dropNode: any, type: string) {
  // 只能拖到部门或公司内
  if (dropNode.data.type === 'position') return false
  if (dropNode.data.type === 'company' && type !== 'inner') return false
  const dragPath = draggingNode.data.path || ''
  const dropPath = dropNode.data.path || ''
  if (dropPath.startsWith(dragPath)) return false
  return true
}

async function onNodeDrop(draggingNode: any, dropNode: any, type: string) {
  const newParentCode = type === 'inner' 
    ? (dropNode.data.code || null) 
    : (dropNode.parent?.data?.code || null)
  
  try {
    await api.post('/operations/department/reparent', {
      departmentId: draggingNode.data.id,
      newParentCode
    })
    ElMessage.success('部門を移動しました')
    await loadAll()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '移動に失敗しました')
  }
}

// 部门操作
function addDepartment(parent: any) {
  parentDept.value = parent
  editingDept.value = null
  deptForm.value = { code: '', name: '' }
  showDeptDialog.value = true
}

function editDepartment(dept: any) {
  parentDept.value = null
  editingDept.value = dept
  deptForm.value = { code: dept.code, name: dept.name }
  showDeptDialog.value = true
}

async function saveDepartment() {
  if (!deptForm.value.code || !deptForm.value.name) return

  try {
    if (editingDept.value?.id) {
      await api.put(`/objects/department/${editingDept.value.id}`, {
        payload: {
          code: deptForm.value.code,
          name: deptForm.value.name,
          path: editingDept.value.path,
          level: editingDept.value.level,
          parentCode: editingDept.value.parentCode
        }
      })
      ElMessage.success('部門を更新しました')
    } else {
      const parentCode = parentDept.value?.code || null
      const path = parentCode ? `${parentDept.value.path}/${deptForm.value.code}` : deptForm.value.code
      const level = parentCode ? (parentDept.value.level || 1) + 1 : 1

      await api.post('/objects/department', {
        payload: {
          code: deptForm.value.code,
          name: deptForm.value.name,
          parentCode,
          path,
          level
        }
      })
      ElMessage.success('部門を追加しました')
    }
    showDeptDialog.value = false
    await loadAll()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '保存に失敗しました')
  }
}

async function deleteDepartment(dept: any) {
  const hasChildren = departments.value.some((d: any) => 
    (d.payload?.parentCode || '') === dept.code
  )
  if (hasChildren) {
    ElMessage.error('子部門が存在するため削除できません')
    return
  }

  const hasEmployees = allEmployees.value.some(emp => {
    const depts = emp.payload?.departments || []
    return depts.some((d: any) => d.departmentId === dept.id)
  })
  if (hasEmployees) {
    ElMessage.error('所属社員が存在するため削除できません')
    return
  }

  try {
    await ElMessageBox.confirm(
      `「${dept.name}」を削除しますか？`,
      '削除確認',
      { confirmButtonText: '削除', cancelButtonText: 'キャンセル', type: 'warning' }
    )
    await api.delete(`/objects/department/${dept.id}`)
    ElMessage.success('部門を削除しました')
    selectedNode.value = null
    employees.value = []
    await loadAll()
  } catch {
    // cancelled
  }
}

// 员工操作
function addEmployee() {
  selectedEmployeeId.value = null
  showEmployeeDialog.value = true
}

function openEmployee(emp: any) {
  selectedEmployeeId.value = emp.id
  showEmployeeDialog.value = true
}

function onEmployeeSaved() {
  showEmployeeDialog.value = false
  loadAll()
}

function getNameKanji(emp: any) {
  const payload = emp?.payload || {}
  return payload.nameKanji || payload.name || emp.name || ''
}

function getNameKana(emp: any) {
  const payload = emp?.payload || {}
  return payload.nameKana || ''
}

function getPosition(emp: any) {
  if (!selectedNode.value) return ''
  const depts = emp.payload?.departments || []
  if (selectedNode.value.type === 'department') {
    const dept = depts.find((d: any) => d.departmentId === selectedNode.value.id)
    return dept?.position || ''
  }
  return selectedNode.value.position || ''
}

function getEmploymentTypeName(emp: any) {
  const payload = emp?.payload || {}
  const contracts = Array.isArray(payload.contracts) ? payload.contracts : []
  const now = new Date().toISOString().slice(0, 10)
  const activeContract = contracts.find((c: any) => {
    const from = c.periodFrom || ''
    const to = c.periodTo || '9999-12-31'
    return from <= now && to >= now
  }) || contracts[0]
  if (!activeContract?.employmentTypeCode) return ''
  return employmentTypeMap.value[activeContract.employmentTypeCode] || activeContract.employmentTypeCode
}

function getEmploymentTypeTagType(emp: any) {
  const payload = emp?.payload || {}
  const contracts = Array.isArray(payload.contracts) ? payload.contracts : []
  const activeContract = contracts[0]
  const code = activeContract?.employmentTypeCode || ''
  if (code === 'CONTRACTOR') return 'warning'
  if (code === 'PT') return 'info'
  return ''
}

function getJoinDate(emp: any) {
  const payload = emp?.payload || {}
  const contracts = Array.isArray(payload.contracts) ? payload.contracts : []
  const dates = contracts.map((c: any) => c.periodFrom).filter(Boolean).sort()
  return dates[0] || ''
}

function isEmployeeActive(emp: any) {
  const payload = emp?.payload || {}
  const contracts = Array.isArray(payload.contracts) ? payload.contracts : []
  if (!contracts.length) return false
  const now = new Date().toISOString().slice(0, 10)
  return contracts.some((c: any) => {
    const from = c.periodFrom || ''
    const to = c.periodTo || '9999-12-31'
    return from <= now && to >= now
  })
}

onMounted(loadAll)
</script>

<style scoped>
.org-chart {
  display: flex;
  height: calc(100vh - 100px);
  gap: 0;
  background: #f5f7fa;
}

/* 左侧树面板 */
.org-tree-panel {
  width: 380px;
  flex-shrink: 0;
  background: white;
  display: flex;
  flex-direction: column;
  margin: 16px;
  margin-right: 0;
  border-radius: 12px;
  box-shadow: 0 2px 12px rgba(0, 0, 0, 0.06);
  overflow: hidden;
}

.org-tree-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 16px 20px;
  border-bottom: 1px solid #ebeef5;
}

.org-tree-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.org-tree-title .el-icon {
  color: #667eea;
  font-size: 20px;
}

.org-tree-actions {
  display: flex;
  gap: 6px;
}

.org-tree-search {
  padding: 12px 16px;
  border-bottom: 1px solid #ebeef5;
}

.org-tree-content {
  flex: 1;
  overflow-y: auto;
  padding: 12px 8px;
}

.org-tree-node {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  border-radius: 6px;
  width: 100%;
  transition: all 0.2s;
}

.org-tree-node:hover {
  background: #f0f5ff;
}

.org-tree-node--company {
  font-weight: 600;
}

.org-tree-node--position {
  font-size: 13px;
  color: #606266;
}

.org-tree-node__icon {
  font-size: 16px;
  color: #667eea;
  flex-shrink: 0;
}

.org-tree-node--company .org-tree-node__icon {
  color: #409eff;
}

.org-tree-node--position .org-tree-node__icon {
  color: #e6a23c;
  font-size: 14px;
}

.org-tree-node__name {
  flex: 1;
  font-size: 14px;
  color: #303133;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.org-tree-node--position .org-tree-node__name {
  font-size: 13px;
}

.org-tree-node__count {
  font-size: 12px;
  color: #909399;
}

.org-tree-node__actions {
  display: none;
  gap: 2px;
}

.org-tree-node:hover .org-tree-node__actions {
  display: flex;
}

/* 右侧详情面板 */
.org-detail-panel {
  flex: 1;
  display: flex;
  flex-direction: column;
  background: white;
  margin: 16px;
  border-radius: 12px;
  box-shadow: 0 2px 12px rgba(0, 0, 0, 0.06);
  overflow: hidden;
}

.org-detail-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 16px 24px;
  border-bottom: 1px solid #ebeef5;
  background: linear-gradient(90deg, #f8f9fc 0%, #ffffff 100%);
}

.org-detail-title {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.org-detail-title .el-icon {
  color: #667eea;
  font-size: 20px;
}

.org-detail-content {
  flex: 1;
  overflow-y: auto;
  padding: 20px;
}

.org-detail-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 16px;
  padding: 60px 20px;
  color: #909399;
}

.org-detail-empty .el-icon {
  font-size: 48px;
  color: #c0c4cc;
}

.org-detail-loading {
  padding: 20px;
}

/* 员工卡片列表 */
.org-employee-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.org-employee-card {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 12px 16px;
  background: #fff;
  border-radius: 8px;
  border: 1px solid #ebeef5;
  cursor: pointer;
  transition: all 0.2s;
}

.org-employee-card:hover {
  border-color: #667eea;
  background: #f8f9fc;
}

.org-employee-card__info {
  flex: 1;
  min-width: 0;
}

.org-employee-card__header {
  display: flex;
  align-items: baseline;
  gap: 10px;
  margin-bottom: 6px;
}

.org-employee-card__code {
  font-size: 13px;
  color: #909399;
  font-family: 'Consolas', monospace;
}

.org-employee-card__name {
  font-size: 14px;
  font-weight: 600;
  color: #303133;
}

.org-employee-card__kana {
  font-size: 12px;
  color: #909399;
}

.org-employee-card__meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.org-employee-card__date {
  font-size: 12px;
  color: #606266;
}

.org-employee-card__actions {
  flex-shrink: 0;
}

.employee-dialog-wrap {
  min-width: 1000px;
  max-width: 1400px;
}

@media (max-width: 1200px) {
  .org-chart {
    flex-direction: column;
    height: auto;
  }
  .org-tree-panel {
    width: 100%;
    max-height: 400px;
  }
  .org-detail-panel {
    margin: 16px;
    min-height: 400px;
  }
}
</style>

<style>
/* 员工编辑弹窗 - 全局样式 */
.employee-edit-dialog {
  background: transparent !important;
  box-shadow: none !important;
}
.employee-edit-dialog .el-dialog__header {
  display: none !important;
}
.employee-edit-dialog .el-dialog__body {
  padding: 0 !important;
}
</style>
