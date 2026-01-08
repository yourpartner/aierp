<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryWarehouses }}</div>
          <div class="page-actions">
            <el-input v-model="searchQuery" :placeholder="labels.search" style="width:200px;margin-right:8px" clearable @keyup.enter="load" />
            <el-button @click="load" :loading="loading">{{ buttons.search }}</el-button>
            <el-button type="primary" @click="openCreateDialog">{{ buttons.create }}</el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" stripe style="width: 100%" v-loading="loading">
        <el-table-column prop="warehouse_code" :label="labels.code" width="140" />
        <el-table-column prop="name" :label="labels.name" min-width="200" />
        <el-table-column :label="labels.status" width="100" align="center">
          <template #default="{ row }">
            <el-tag :type="row.payload?.inactive ? 'info' : 'success'" size="small">
              {{ row.payload?.inactive ? labels.inactive : labels.active }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="labels.actions" width="260" align="center">
          <template #default="{ row }">
            <el-button type="primary" link size="small" @click="viewDetail(row)">{{ buttons.view }}</el-button>
            <el-button type="primary" link size="small" @click="openEditDialog(row)">{{ buttons.edit }}</el-button>
            <el-button :type="row.payload?.inactive ? 'success' : 'warning'" link size="small" @click="toggleStatus(row)">
              {{ row.payload?.inactive ? buttons.activate : buttons.deactivate }}
            </el-button>
            <el-button type="danger" link size="small" @click="deleteRow(row)">{{ buttons.delete }}</el-button>
          </template>
        </el-table-column>
      </el-table>
      
      <!-- 分页 -->
      <div class="pagination-wrapper">
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="pageSize"
          :page-sizes="[20, 50, 100]"
          :total="total"
          layout="total, sizes, prev, pager, next"
          @size-change="load"
          @current-change="load"
        />
      </div>

      <!-- 详情弹窗 -->
      <el-dialog v-model="detailDialogVisible" :title="labels.detailTitle" width="700px" append-to-body>
        <el-descriptions :column="2" border>
          <el-descriptions-item :label="labels.code">{{ detailData.code }}</el-descriptions-item>
          <el-descriptions-item :label="labels.name">{{ detailData.name }}</el-descriptions-item>
          <el-descriptions-item :label="labels.status">
            <el-tag :type="detailData.inactive ? 'info' : 'success'" size="small">
              {{ detailData.inactive ? labels.inactive : labels.active }}
            </el-tag>
          </el-descriptions-item>
          <el-descriptions-item :label="labels.binCount">{{ detailBins.length }}</el-descriptions-item>
        </el-descriptions>
        
        <!-- 下属棚番列表 -->
        <div class="bins-section" v-if="detailBins.length > 0">
          <div class="bins-title">{{ labels.binsTitle }}</div>
          <el-table :data="detailBins" stripe size="small" max-height="300">
            <el-table-column prop="bin_code" :label="labels.binCode" width="120" />
            <el-table-column prop="name" :label="labels.binName" min-width="150" />
            <el-table-column :label="labels.status" width="80" align="center">
              <template #default="{ row }">
                <el-tag :type="row.payload?.inactive ? 'info' : 'success'" size="small">
                  {{ row.payload?.inactive ? labels.inactive : labels.active }}
                </el-tag>
              </template>
            </el-table-column>
          </el-table>
        </div>
        <div v-else class="bins-empty">{{ labels.noBins }}</div>
        
        <template #footer>
          <el-button @click="detailDialogVisible = false">{{ buttons.close }}</el-button>
          <el-button type="primary" @click="editFromDetail">{{ buttons.edit }}</el-button>
        </template>
      </el-dialog>

      <!-- 新建/编辑弹窗 -->
      <el-dialog 
        v-model="editDialogVisible" 
        :title="editMode === 'create' ? labels.createTitle : labels.editTitle" 
        width="500px" 
        append-to-body
        :close-on-click-modal="false"
      >
        <el-form ref="formRef" :model="editForm" :rules="formRules" label-width="100px">
          <el-form-item :label="labels.code" prop="code">
            <el-input 
              v-model="editForm.code" 
              :placeholder="labels.codePlaceholder"
              :disabled="editMode === 'edit'"
              maxlength="4"
              @input="editForm.code = editForm.code.toUpperCase().replace(/[^A-Za-z0-9]/g, '')"
            />
          </el-form-item>
          <el-form-item :label="labels.name" prop="name">
            <el-input v-model="editForm.name" :placeholder="labels.namePlaceholder" />
          </el-form-item>
          <el-form-item :label="labels.status">
            <el-switch 
              v-model="editForm.inactive" 
              :active-text="labels.inactive" 
              :inactive-text="labels.active"
              :active-value="true"
              :inactive-value="false"
            />
          </el-form-item>
        </el-form>
        <template #footer>
          <el-button @click="editDialogVisible = false">{{ buttons.cancel }}</el-button>
          <el-button type="primary" :loading="saving" @click="saveForm">{{ buttons.save }}</el-button>
        </template>
      </el-dialog>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted } from 'vue'
import type { FormInstance, FormRules } from 'element-plus'
import api from '../api'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from '../i18n'

const { section, lang } = useI18n()
const navText = section({ inventoryWarehouses:'' }, (msg) => msg.nav)

const labels = computed(() => ({
  code: lang.value === 'ja' ? '倉庫コード' : lang.value === 'en' ? 'Code' : '编码',
  codePlaceholder: lang.value === 'ja' ? '2〜4文字の英数字' : lang.value === 'en' ? '2-4 alphanumeric' : '2-4位字母数字',
  name: lang.value === 'ja' ? '倉庫名' : lang.value === 'en' ? 'Name' : '名称',
  namePlaceholder: lang.value === 'ja' ? '倉庫名を入力' : lang.value === 'en' ? 'Enter warehouse name' : '输入仓库名称',
  status: lang.value === 'ja' ? 'ステータス' : lang.value === 'en' ? 'Status' : '状态',
  active: lang.value === 'ja' ? '有効' : lang.value === 'en' ? 'Active' : '有效',
  inactive: lang.value === 'ja' ? '無効' : lang.value === 'en' ? 'Inactive' : '停用',
  actions: lang.value === 'ja' ? '操作' : lang.value === 'en' ? 'Actions' : '操作',
  search: lang.value === 'ja' ? 'コードまたは名前で検索' : lang.value === 'en' ? 'Search by code or name' : '按编码或名称搜索',
  detailTitle: lang.value === 'ja' ? '倉庫詳細' : lang.value === 'en' ? 'Warehouse Detail' : '仓库详情',
  createTitle: lang.value === 'ja' ? '倉庫新規作成' : lang.value === 'en' ? 'Create Warehouse' : '新建仓库',
  editTitle: lang.value === 'ja' ? '倉庫編集' : lang.value === 'en' ? 'Edit Warehouse' : '编辑仓库',
  binCount: lang.value === 'ja' ? '棚番数' : lang.value === 'en' ? 'Bin Count' : '棚番数量',
  binsTitle: lang.value === 'ja' ? '棚番一覧' : lang.value === 'en' ? 'Bins' : '棚番列表',
  binCode: lang.value === 'ja' ? '棚番コード' : lang.value === 'en' ? 'Bin Code' : '棚番编码',
  binName: lang.value === 'ja' ? '棚番名' : lang.value === 'en' ? 'Bin Name' : '棚番名称',
  noBins: lang.value === 'ja' ? 'この倉庫には棚番がありません' : lang.value === 'en' ? 'No bins in this warehouse' : '该仓库下没有棚番',
  deleteConfirm: lang.value === 'ja' ? 'この倉庫を削除しますか？' : lang.value === 'en' ? 'Delete this warehouse?' : '确定删除此仓库？',
  deleteTitle: lang.value === 'ja' ? '削除確認' : lang.value === 'en' ? 'Confirm Delete' : '删除确认',
  deleteSuccess: lang.value === 'ja' ? '削除しました' : lang.value === 'en' ? 'Deleted successfully' : '删除成功',
  hasReferences: lang.value === 'ja' ? 'この倉庫は使用中のため削除できません' : lang.value === 'en' ? 'Cannot delete: warehouse is in use' : '该仓库正在使用中，无法删除',
  statusUpdated: lang.value === 'ja' ? 'ステータスを更新しました' : lang.value === 'en' ? 'Status updated' : '状态已更新',
  saveSuccess: lang.value === 'ja' ? '保存しました' : lang.value === 'en' ? 'Saved successfully' : '保存成功',
  codeRequired: lang.value === 'ja' ? '倉庫コードは必須です' : lang.value === 'en' ? 'Warehouse code is required' : '仓库编码必填',
  codeFormat: lang.value === 'ja' ? '2〜4文字の英数字で入力してください' : lang.value === 'en' ? 'Must be 2-4 alphanumeric characters' : '请输入2-4位字母数字',
  codeExists: lang.value === 'ja' ? '同じコードの倉庫が既に存在します' : lang.value === 'en' ? 'Warehouse code already exists' : '已存在此编码的仓库',
  nameRequired: lang.value === 'ja' ? '倉庫名は必須です' : lang.value === 'en' ? 'Warehouse name is required' : '仓库名称必填'
}))

const buttons = computed(() => ({
  search: lang.value === 'ja' ? '検索' : lang.value === 'en' ? 'Search' : '搜索',
  create: lang.value === 'ja' ? '新規' : lang.value === 'en' ? 'Create' : '新建',
  view: lang.value === 'ja' ? '表示' : lang.value === 'en' ? 'View' : '查看',
  edit: lang.value === 'ja' ? '編集' : lang.value === 'en' ? 'Edit' : '编辑',
  delete: lang.value === 'ja' ? '削除' : lang.value === 'en' ? 'Delete' : '删除',
  close: lang.value === 'ja' ? '閉じる' : lang.value === 'en' ? 'Close' : '关闭',
  cancel: lang.value === 'ja' ? 'キャンセル' : lang.value === 'en' ? 'Cancel' : '取消',
  save: lang.value === 'ja' ? '保存' : lang.value === 'en' ? 'Save' : '保存',
  activate: lang.value === 'ja' ? '有効化' : lang.value === 'en' ? 'Activate' : '启用',
  deactivate: lang.value === 'ja' ? '無効化' : lang.value === 'en' ? 'Deactivate' : '停用'
}))

const rows = ref<any[]>([])
const loading = ref(false)
const searchQuery = ref('')
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)

// 详情弹窗
const detailDialogVisible = ref(false)
const detailData = reactive<any>({ id: '', code: '', name: '', inactive: false })
const detailBins = ref<any[]>([])

// 编辑弹窗
const editDialogVisible = ref(false)
const editMode = ref<'create' | 'edit'>('create')
const editForm = reactive({
  id: '',
  code: '',
  name: '',
  inactive: false
})
const formRef = ref<FormInstance>()
const saving = ref(false)

// 表单验证规则
const formRules = computed<FormRules>(() => ({
  code: [
    { required: true, message: labels.value.codeRequired, trigger: 'blur' },
    { pattern: /^[A-Za-z0-9]{2,4}$/, message: labels.value.codeFormat, trigger: 'blur' }
  ],
  name: [
    { required: true, message: labels.value.nameRequired, trigger: 'blur' }
  ]
}))

async function load() {
  loading.value = true
  try {
    const where: any[] = []
    const q = searchQuery.value.trim()
    if (q) {
      where.push({
        anyOf: [
          { field: 'warehouse_code', op: 'contains', value: q },
          { field: 'name', op: 'contains', value: q }
        ]
      })
    }
    const r = await api.post('/objects/warehouse/search', { page: page.value, pageSize: pageSize.value, where, orderBy: [{ field: 'warehouse_code', dir: 'ASC' }] })
    rows.value = r.data?.data || []
    total.value = r.data?.total || 0
  } finally {
    loading.value = false
  }
}

async function loadBins(warehouseCode: string) {
  try {
    const r = await api.post('/objects/bin/search', {
      page: 1, pageSize: 100,
      where: [{ field: 'warehouse_code', op: 'eq', value: warehouseCode }],
      orderBy: [{ field: 'bin_code', dir: 'ASC' }]
    })
    detailBins.value = r.data?.data || []
  } catch {
    detailBins.value = []
  }
}

async function viewDetail(row: any) {
  detailData.id = row.id
  detailData.code = row.warehouse_code || row.payload?.code
  detailData.name = row.name || row.payload?.name
  detailData.inactive = row.payload?.inactive || false
  detailDialogVisible.value = true
  await loadBins(detailData.code)
}

function openCreateDialog() {
  editMode.value = 'create'
  editForm.id = ''
  editForm.code = ''
  editForm.name = ''
  editForm.inactive = false
  editDialogVisible.value = true
}

function openEditDialog(row: any) {
  editMode.value = 'edit'
  editForm.id = row.id
  editForm.code = row.warehouse_code || row.payload?.code || ''
  editForm.name = row.name || row.payload?.name || ''
  editForm.inactive = row.payload?.inactive || false
  editDialogVisible.value = true
}

function editFromDetail() {
  detailDialogVisible.value = false
  editMode.value = 'edit'
  editForm.id = detailData.id
  editForm.code = detailData.code
  editForm.name = detailData.name
  editForm.inactive = detailData.inactive
  editDialogVisible.value = true
}

async function checkCodeExists(code: string, excludeId?: string): Promise<boolean> {
  try {
    const where: any[] = [
      { field: 'warehouse_code', op: 'eq', value: code }
    ]
    const r = await api.post('/objects/warehouse/search', { page: 1, pageSize: 1, where, orderBy: [] })
    const existing = r.data?.data || []
    if (existing.length === 0) return false
    if (excludeId && existing[0].id === excludeId) return false
    return true
  } catch {
    return false
  }
}

async function saveForm() {
  if (!formRef.value) return
  
  const valid = await formRef.value.validate().catch(() => false)
  if (!valid) return

  // 新建时检查编码是否重复
  if (editMode.value === 'create') {
    const exists = await checkCodeExists(editForm.code)
    if (exists) {
      ElMessage.warning(labels.value.codeExists)
      return
    }
  }

  saving.value = true
  try {
    const payload = {
      code: editForm.code,
      name: editForm.name,
      inactive: editForm.inactive
    }

    if (editMode.value === 'create') {
      await api.post('/objects/warehouse', { 
        payload,
        warehouse_code: editForm.code,
        name: editForm.name
      })
    } else {
      await api.put(`/objects/warehouse/${editForm.id}`, { 
        payload,
        name: editForm.name
      })
    }

    ElMessage.success(labels.value.saveSuccess)
    editDialogVisible.value = false
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'Save failed')
  } finally {
    saving.value = false
  }
}

async function toggleStatus(row: any) {
  try {
    const newPayload = { ...row.payload, inactive: !row.payload?.inactive }
    await api.put(`/objects/warehouse/${row.id}`, { payload: newPayload })
    ElMessage.success(labels.value.statusUpdated)
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'Update failed')
  }
}

async function deleteRow(row: any) {
  // 先检查是否被引用
  try {
    const refResp = await api.get(`/objects/warehouse/${row.id}/references`)
    const refs = refResp.data?.references || []
    
    if (refs.length > 0) {
      const details = refs.map((r: any) => r.description).join('、')
      ElMessage.warning(`${labels.value.hasReferences}（${details}）`)
      return
    }
  } catch {
    // 如果没有引用检查 API，继续删除流程
  }

  ElMessageBox.confirm(labels.value.deleteConfirm, labels.value.deleteTitle, {
    confirmButtonText: buttons.value.delete,
    cancelButtonText: buttons.value.close,
    type: 'warning'
  }).then(async () => {
    try {
      await api.delete(`/objects/warehouse/${row.id}`)
      ElMessage.success(labels.value.deleteSuccess)
      load()
    } catch (e: any) {
      ElMessage.error(e?.response?.data?.error || 'Delete failed')
    }
  }).catch(() => {})
}

onMounted(load)
</script>

<style scoped>
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}
.page-header-title {
  font-weight: 600;
  font-size: 16px;
}
.page-actions {
  display: flex;
  align-items: center;
}
.pagination-wrapper {
  margin-top: 16px;
  display: flex;
  justify-content: flex-end;
}
.bins-section {
  margin-top: 16px;
}
.bins-title {
  font-weight: 600;
  margin-bottom: 8px;
  color: #303133;
}
.bins-empty {
  margin-top: 16px;
  text-align: center;
  color: #909399;
  padding: 20px;
}
</style>
