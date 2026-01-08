<template>
  <div class="users-list">
    <el-card>
      <template #header>
        <div class="users-list-header">
          <div class="users-list-header__title">{{ text.title }}</div>
          <div class="users-list-header__filters">
            <el-select v-model="userTypeFilter" size="small" clearable :placeholder="text.userType" style="width:120px">
              <el-option :label="text.internal" value="internal" />
              <el-option :label="text.external" value="external" />
            </el-select>
            <el-radio-group v-model="statusFilter" size="small">
              <el-radio-button label="all">{{ text.all }}</el-radio-button>
              <el-radio-button label="active">{{ text.active }}</el-radio-button>
              <el-radio-button label="inactive">{{ text.inactive }}</el-radio-button>
            </el-radio-group>
            <el-button size="small" @click="load">{{ text.search }}</el-button>
            <el-button size="small" type="primary" @click="showCreate=true">{{ text.create }}</el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" size="small" border @row-dblclick="onEdit">
        <el-table-column type="index" width="60" />
        <el-table-column :label="text.employeeCode" prop="employeeCode" width="120" />
        <el-table-column :label="text.name" width="120">
          <template #default="{ row }">{{ row.name || '-' }}</template>
        </el-table-column>
        <el-table-column :label="text.linkedEmployee" width="160">
          <template #default="{ row }">
            <span v-if="row.employeeId">{{ row.employeeName || '-' }}</span>
            <span v-else class="no-link">-</span>
          </template>
        </el-table-column>
        <el-table-column :label="text.userType" width="80">
          <template #default="{ row }">
            <el-tag :type="row.userType === 'internal' ? 'primary' : 'warning'" size="small">
              {{ row.userType === 'internal' ? text.internal : text.external }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="text.roles" min-width="180">
          <template #default="{ row }">
            <el-tag v-for="r in row.roleCodes" :key="r" size="small" style="margin-right:4px">{{ r }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="text.status" width="80">
          <template #default="{ row }">
            <el-tag :type="row.isActive ? 'success' : 'info'" size="small">
              {{ row.isActive ? text.active : text.inactive }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="text.lastLogin" width="160">
          <template #default="{ row }">{{ row.lastLoginAt ? formatDate(row.lastLoginAt) : '-' }}</template>
        </el-table-column>
        <el-table-column :label="text.actions" width="160">
          <template #default="{ row }">
            <el-button size="small" @click="openEdit(row.id)">{{ text.edit }}</el-button>
            <el-popconfirm :title="text.confirmDelete" @confirm="deleteUser(row.id)">
              <template #reference>
                <el-button size="small" type="danger">{{ text.delete }}</el-button>
              </template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
      <div style="display:flex;justify-content:flex-end;margin-top:8px">
        <el-pagination layout="prev, pager, next, total" :page-size="pageSize" :total="total" v-model:current-page="page" @current-change="load" />
      </div>
    </el-card>

    <!-- 创建/编辑用户对话框 -->
    <el-dialog v-model="showCreate" :title="editId ? text.editUser : text.createUser" width="500px" destroy-on-close>
      <el-form :model="form" label-width="90px" size="small" class="user-form">
        <el-form-item :label="text.employeeCode" required>
          <el-input v-model="form.employeeCode" :disabled="!!editId" />
        </el-form-item>
        <el-form-item :label="text.name">
          <el-input v-model="form.name" />
        </el-form-item>
        <el-form-item :label="text.password" :required="!editId">
          <el-input v-model="form.password" type="password" show-password :placeholder="editId ? text.passwordPlaceholder : ''" />
        </el-form-item>
        <el-form-item :label="text.userType">
          <el-radio-group v-model="form.userType">
            <el-radio label="internal">{{ text.internal }}</el-radio>
            <el-radio label="external">{{ text.external }}</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item :label="text.linkedEmployee" v-if="form.userType === 'internal'">
          <el-select v-model="form.employeeId" filterable clearable :placeholder="text.selectEmployee">
            <el-option v-for="e in employeeOptions" :key="e.id" :label="`${e.employeeCode} - ${e.name}`" :value="e.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="text.externalType" v-if="form.userType === 'external'">
          <el-select v-model="form.externalType" clearable>
            <el-option :label="text.taxAccountant" value="tax_accountant" />
            <el-option :label="text.auditor" value="auditor" />
            <el-option :label="text.client" value="client" />
            <el-option :label="text.other" value="other" />
          </el-select>
        </el-form-item>
        <el-form-item :label="text.email">
          <el-input v-model="form.email" />
        </el-form-item>
        <el-form-item :label="text.phone">
          <el-input v-model="form.phone" />
        </el-form-item>
        <el-form-item :label="text.roles">
          <el-select v-model="form.roleCodes" multiple filterable>
            <el-option v-for="r in roleOptions" :key="r.roleCode" :label="r.roleName || r.roleCode" :value="r.roleCode" />
          </el-select>
        </el-form-item>
        <el-form-item :label="text.status">
          <el-switch v-model="form.isActive" :active-text="text.active" :inactive-text="text.inactive" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showCreate=false">{{ text.cancel }}</el-button>
        <el-button type="primary" @click="saveUser" :loading="saving">{{ text.save }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { ElMessage } from 'element-plus'
import api from '../api'
import { useI18n } from '../i18n'

const { lang } = useI18n()
const texts: Record<string, Record<string, string>> = {
  ja: {
    title: 'ユーザー管理',
    userType: 'ユーザー種別',
    internal: '社内',
    external: '社外',
    all: '全部',
    active: '有効',
    inactive: '無効',
    search: '検索',
    create: '新規作成',
    employeeCode: 'ログインID',
    name: '氏名',
    password: 'パスワード',
    passwordPlaceholder: '変更する場合のみ入力',
    email: 'メール',
    phone: '電話番号',
    roles: 'ロール',
    status: 'ステータス',
    lastLogin: '最終ログイン',
    actions: '操作',
    edit: '編集',
    delete: '削除',
    confirmDelete: 'このユーザーを削除しますか？',
    createUser: 'ユーザー作成',
    editUser: 'ユーザー編集',
    externalType: '外部種別',
    cancel: 'キャンセル',
    save: '保存',
    saveSuccess: '保存しました',
    deleteSuccess: '削除しました',
    taxAccountant: '税理士',
    auditor: '監査人',
    client: '顧客',
    other: 'その他',
    requiredLoginId: 'ログインIDは必須です',
    requiredPassword: 'パスワードは必須です',
    linkedEmployee: '関連社員',
    selectEmployee: '社員を選択'
  },
  zh: {
    title: '用户管理',
    userType: '用户类型',
    internal: '内部',
    external: '外部',
    all: '全部',
    active: '启用',
    inactive: '禁用',
    search: '搜索',
    create: '新建',
    employeeCode: '登录ID',
    name: '姓名',
    password: '密码',
    passwordPlaceholder: '仅修改时填写',
    email: '邮箱',
    phone: '电话',
    roles: '角色',
    status: '状态',
    lastLogin: '最后登录',
    actions: '操作',
    edit: '编辑',
    delete: '删除',
    confirmDelete: '确定删除该用户吗？',
    createUser: '创建用户',
    editUser: '编辑用户',
    externalType: '外部类型',
    cancel: '取消',
    save: '保存',
    saveSuccess: '保存成功',
    deleteSuccess: '删除成功',
    taxAccountant: '税理士',
    auditor: '审计师',
    client: '客户',
    other: '其他',
    requiredLoginId: '登录ID必填',
    requiredPassword: '密码必填',
    linkedEmployee: '关联员工',
    selectEmployee: '选择员工'
  },
  en: {
    title: 'User Management',
    userType: 'User Type',
    internal: 'Internal',
    external: 'External',
    all: 'All',
    active: 'Active',
    inactive: 'Inactive',
    search: 'Search',
    create: 'Create',
    employeeCode: 'Login ID',
    name: 'Name',
    password: 'Password',
    passwordPlaceholder: 'Enter only if changing',
    email: 'Email',
    phone: 'Phone',
    roles: 'Roles',
    status: 'Status',
    lastLogin: 'Last Login',
    actions: 'Actions',
    edit: 'Edit',
    delete: 'Delete',
    confirmDelete: 'Delete this user?',
    createUser: 'Create User',
    editUser: 'Edit User',
    externalType: 'External Type',
    cancel: 'Cancel',
    save: 'Save',
    saveSuccess: 'Saved successfully',
    deleteSuccess: 'Deleted successfully',
    taxAccountant: 'Tax Accountant',
    auditor: 'Auditor',
    client: 'Client',
    other: 'Other',
    requiredLoginId: 'Login ID is required',
    requiredPassword: 'Password is required',
    linkedEmployee: 'Linked Employee',
    selectEmployee: 'Select Employee'
  }
}
const text = computed(() => texts[lang.value] || texts.ja)

const rows = ref<any[]>([])
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const userTypeFilter = ref<string>('')
const statusFilter = ref<string>('all')
const roleOptions = ref<any[]>([])
const employeeOptions = ref<any[]>([])

const showCreate = ref(false)
const editId = ref<string>('')
const saving = ref(false)
const form = ref({
  employeeCode: '',
  name: '',
  password: '',
  userType: 'internal',
  externalType: '',
  email: '',
  phone: '',
  roleCodes: [] as string[],
  isActive: true,
  employeeId: '' as string | null
})

async function load() {
  try {
    const params: any = {
      offset: (page.value - 1) * pageSize.value,
      limit: pageSize.value
    }
    if (userTypeFilter.value) params.userType = userTypeFilter.value
    if (statusFilter.value === 'active') params.isActive = true
    else if (statusFilter.value === 'inactive') params.isActive = false
    
    const res = await api.get('/api/users', { params })
    rows.value = res.data.users || []
    total.value = res.data.total || 0
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  }
}

async function loadRoles() {
  try {
    const res = await api.get('/api/roles')
    roleOptions.value = res.data || []
  } catch (e) {
    console.error('Failed to load roles', e)
  }
}

async function loadEmployees() {
  try {
    const res = await api.post('/objects/employee/search', {
      where: [],
      page: 1,
      pageSize: 500,
      orderBy: [{ field: 'employee_code', dir: 'ASC' }]
    })
    const rows = res.data?.data || []
    employeeOptions.value = rows.map((e: any) => ({
      id: e.id,
      employeeCode: e.employee_code || e.payload?.code || '',
      name: e.payload?.nameKanji || e.payload?.name || ''
    }))
  } catch (e) {
    console.error('Failed to load employees', e)
  }
}

function formatDate(dateStr: string) {
  if (!dateStr) return ''
  const d = new Date(dateStr)
  return d.toLocaleString()
}

function onEdit(row: any) {
  openEdit(row.id)
}

async function openEdit(id: string) {
  editId.value = id
  try {
    const res = await api.get(`/api/users/${id}`)
    const user = res.data
    form.value = {
      employeeCode: user.employeeCode || '',
      name: user.name || '',
      password: '',
      userType: user.userType || 'internal',
      externalType: user.externalType || '',
      email: user.email || '',
      phone: user.phone || '',
      roleCodes: user.roleCodes || [],
      isActive: user.isActive !== false,
      employeeId: user.employeeId || null
    }
    showCreate.value = true
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  }
}

async function saveUser() {
  if (!form.value.employeeCode) {
    ElMessage.warning(text.value.requiredLoginId)
    return
  }
  if (!editId.value && !form.value.password) {
    ElMessage.warning(text.value.requiredPassword)
    return
  }
  
  saving.value = true
  try {
    const payload: any = {
      name: form.value.name,
      userType: form.value.userType,
      email: form.value.email,
      phone: form.value.phone,
      roleCodes: form.value.roleCodes,
      isActive: form.value.isActive
    }
    if (form.value.userType === 'external') {
      payload.externalType = form.value.externalType
    }
    if (form.value.userType === 'internal') {
      payload.employeeId = form.value.employeeId || null
    }
    if (form.value.password) {
      payload.password = form.value.password
    }
    
    if (editId.value) {
      await api.put(`/api/users/${editId.value}`, payload)
    } else {
      payload.employeeCode = form.value.employeeCode
      payload.password = form.value.password
      await api.post('/api/users', payload)
    }
    
    ElMessage.success(text.value.saveSuccess)
    showCreate.value = false
    editId.value = ''
    resetForm()
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  } finally {
    saving.value = false
  }
}

async function deleteUser(id: string) {
  try {
    await api.delete(`/api/users/${id}`)
    ElMessage.success(text.value.deleteSuccess)
    load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || e.message)
  }
}

function resetForm() {
  form.value = {
    employeeCode: '',
    name: '',
    password: '',
    userType: 'internal',
    externalType: '',
    email: '',
    phone: '',
    roleCodes: [],
    isActive: true,
    employeeId: null
  }
}

onMounted(() => {
  load()
  loadRoles()
  loadEmployees()
})
</script>

<style scoped>
.users-list {
  padding: 0;
}
.users-list-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.users-list-header__title {
  font-size: 16px;
  font-weight: 600;
}
.users-list-header__filters {
  display: flex;
  gap: 8px;
  align-items: center;
}
.user-form :deep(.el-input),
.user-form :deep(.el-select) {
  width: 100%;
  max-width: 360px;
}
.user-form :deep(.el-form-item__content) {
  max-width: 360px;
}
.no-link {
  color: #999;
}
</style>

