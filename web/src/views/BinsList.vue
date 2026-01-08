<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-title">{{ navText.inventoryBins }}</div>
          <div class="page-actions">
            <el-select v-model="filterWarehouse" :placeholder="labels.allWarehouses" clearable style="width:180px;margin-right:8px" @change="load">
              <el-option v-for="opt in warehouseOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
            </el-select>
            <el-input v-model="searchQuery" :placeholder="labels.search" style="width:200px;margin-right:8px" clearable @keyup.enter="load" />
            <el-button @click="load" :loading="loading">{{ buttons.search }}</el-button>
            <el-button type="primary" @click="openCreateDialog">{{ buttons.create }}</el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" stripe style="width: 100%" v-loading="loading">
        <el-table-column prop="warehouse_code" :label="labels.warehouse" width="140" />
        <el-table-column prop="bin_code" :label="labels.code" width="140" />
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
      <el-dialog v-model="detailDialogVisible" :title="labels.detailTitle" width="500px" append-to-body>
        <el-descriptions :column="1" border>
          <el-descriptions-item :label="labels.warehouse">{{ detailData.warehouseCode }}</el-descriptions-item>
          <el-descriptions-item :label="labels.code">{{ detailData.code }}</el-descriptions-item>
          <el-descriptions-item :label="labels.name">{{ detailData.name }}</el-descriptions-item>
          <el-descriptions-item :label="labels.status">
            <el-tag :type="detailData.inactive ? 'info' : 'success'" size="small">
              {{ detailData.inactive ? labels.inactive : labels.active }}
            </el-tag>
          </el-descriptions-item>
        </el-descriptions>
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
          <el-form-item :label="labels.warehouse" prop="warehouseCode">
            <el-select 
              v-model="editForm.warehouseCode" 
              :placeholder="labels.selectWarehouse" 
              style="width:100%"
              :disabled="editMode === 'edit'"
            >
              <el-option v-for="opt in warehouseOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
            </el-select>
          </el-form-item>
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
const navText = section({ inventoryBins:'' }, (msg) => msg.nav)

const labels = computed(() => ({
  warehouse: lang.value === 'ja' ? '倉庫' : lang.value === 'en' ? 'Warehouse' : '仓库',
  selectWarehouse: lang.value === 'ja' ? '倉庫を選択' : lang.value === 'en' ? 'Select Warehouse' : '选择仓库',
  code: lang.value === 'ja' ? '棚番コード' : lang.value === 'en' ? 'Bin Code' : '棚番编码',
  codePlaceholder: lang.value === 'ja' ? '2〜4文字の英数字' : lang.value === 'en' ? '2-4 alphanumeric' : '2-4位字母数字',
  name: lang.value === 'ja' ? '棚番名' : lang.value === 'en' ? 'Bin Name' : '棚番名称',
  namePlaceholder: lang.value === 'ja' ? '棚番名を入力' : lang.value === 'en' ? 'Enter bin name' : '输入棚番名称',
  status: lang.value === 'ja' ? 'ステータス' : lang.value === 'en' ? 'Status' : '状态',
  active: lang.value === 'ja' ? '有効' : lang.value === 'en' ? 'Active' : '有效',
  inactive: lang.value === 'ja' ? '無効' : lang.value === 'en' ? 'Inactive' : '停用',
  actions: lang.value === 'ja' ? '操作' : lang.value === 'en' ? 'Actions' : '操作',
  search: lang.value === 'ja' ? 'コードまたは名前で検索' : lang.value === 'en' ? 'Search by code or name' : '按编码或名称搜索',
  allWarehouses: lang.value === 'ja' ? 'すべての倉庫' : lang.value === 'en' ? 'All Warehouses' : '所有仓库',
  detailTitle: lang.value === 'ja' ? '棚番詳細' : lang.value === 'en' ? 'Bin Detail' : '棚番详情',
  createTitle: lang.value === 'ja' ? '棚番新規作成' : lang.value === 'en' ? 'Create Bin' : '新建棚番',
  editTitle: lang.value === 'ja' ? '棚番編集' : lang.value === 'en' ? 'Edit Bin' : '编辑棚番',
  deleteConfirm: lang.value === 'ja' ? 'この棚番を削除しますか？' : lang.value === 'en' ? 'Delete this bin?' : '确定删除此棚番？',
  deleteTitle: lang.value === 'ja' ? '削除確認' : lang.value === 'en' ? 'Confirm Delete' : '删除确认',
  deleteSuccess: lang.value === 'ja' ? '削除しました' : lang.value === 'en' ? 'Deleted successfully' : '删除成功',
  hasReferences: lang.value === 'ja' ? 'この棚番は使用中のため削除できません' : lang.value === 'en' ? 'Cannot delete: bin is in use' : '该棚番正在使用中，无法删除',
  statusUpdated: lang.value === 'ja' ? 'ステータスを更新しました' : lang.value === 'en' ? 'Status updated' : '状态已更新',
  saveSuccess: lang.value === 'ja' ? '保存しました' : lang.value === 'en' ? 'Saved successfully' : '保存成功',
  codeRequired: lang.value === 'ja' ? '棚番コードは必須です' : lang.value === 'en' ? 'Bin code is required' : '棚番编码必填',
  codeFormat: lang.value === 'ja' ? '2〜4文字の英数字で入力してください' : lang.value === 'en' ? 'Must be 2-4 alphanumeric characters' : '请输入2-4位字母数字',
  codeExists: lang.value === 'ja' ? 'この倉庫に同じコードの棚番が既に存在します' : lang.value === 'en' ? 'Bin code already exists in this warehouse' : '该仓库已存在此编码的棚番',
  nameRequired: lang.value === 'ja' ? '棚番名は必須です' : lang.value === 'en' ? 'Bin name is required' : '棚番名称必填',
  warehouseRequired: lang.value === 'ja' ? '倉庫は必須です' : lang.value === 'en' ? 'Warehouse is required' : '仓库必填'
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
const filterWarehouse = ref('')
const warehouseOptions = ref<{ label: string; value: string }[]>([])
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)

// 详情弹窗
const detailDialogVisible = ref(false)
const detailData = reactive<any>({ id: '', warehouseCode: '', code: '', name: '', inactive: false })

// 编辑弹窗
const editDialogVisible = ref(false)
const editMode = ref<'create' | 'edit'>('create')
const editForm = reactive({
  id: '',
  warehouseCode: '',
  code: '',
  name: '',
  inactive: false
})
const formRef = ref<FormInstance>()
const saving = ref(false)

// 表单验证规则
const formRules = computed<FormRules>(() => ({
  warehouseCode: [
    { required: true, message: labels.value.warehouseRequired, trigger: 'change' }
  ],
  code: [
    { required: true, message: labels.value.codeRequired, trigger: 'blur' },
    { pattern: /^[A-Za-z0-9]{2,4}$/, message: labels.value.codeFormat, trigger: 'blur' }
  ],
  name: [
    { required: true, message: labels.value.nameRequired, trigger: 'blur' }
  ]
}))

async function loadWarehouses() {
  try {
    const resp = await api.get('/inventory/warehouses')
    const rows = Array.isArray(resp.data) ? resp.data : []
    warehouseOptions.value = rows.map((row: any) => {
      const code = row?.warehouse_code || row?.payload?.code || ''
      const name = row?.name || row?.payload?.name || code
      return { label: `${name} (${code})`, value: code }
    }).filter((opt: any) => opt.value)
  } catch {}
}

async function load() {
  loading.value = true
  try {
    const where: any[] = []
    if (filterWarehouse.value) {
      where.push({ field: 'warehouse_code', op: 'eq', value: filterWarehouse.value })
    }
    const q = searchQuery.value.trim()
    if (q) {
      where.push({
        anyOf: [
          { field: 'bin_code', op: 'contains', value: q },
          { field: 'name', op: 'contains', value: q }
        ]
      })
    }
    const r = await api.post('/objects/bin/search', { page: page.value, pageSize: pageSize.value, where, orderBy: [{ field: 'warehouse_code', dir: 'ASC' }, { field: 'bin_code', dir: 'ASC' }] })
    rows.value = r.data?.data || []
    total.value = r.data?.total || 0
  } finally {
    loading.value = false
  }
}

function viewDetail(row: any) {
  detailData.id = row.id
  detailData.warehouseCode = row.warehouse_code || row.payload?.warehouseCode
  detailData.code = row.bin_code || row.payload?.code
  detailData.name = row.name || row.payload?.name
  detailData.inactive = row.payload?.inactive || false
  detailDialogVisible.value = true
}

function openCreateDialog() {
  editMode.value = 'create'
  editForm.id = ''
  editForm.warehouseCode = filterWarehouse.value || ''
  editForm.code = ''
  editForm.name = ''
  editForm.inactive = false
  editDialogVisible.value = true
}

function openEditDialog(row: any) {
  editMode.value = 'edit'
  editForm.id = row.id
  editForm.warehouseCode = row.warehouse_code || row.payload?.warehouseCode || ''
  editForm.code = row.bin_code || row.payload?.code || ''
  editForm.name = row.name || row.payload?.name || ''
  editForm.inactive = row.payload?.inactive || false
  editDialogVisible.value = true
}

function editFromDetail() {
  detailDialogVisible.value = false
  editMode.value = 'edit'
  editForm.id = detailData.id
  editForm.warehouseCode = detailData.warehouseCode
  editForm.code = detailData.code
  editForm.name = detailData.name
  editForm.inactive = detailData.inactive
  editDialogVisible.value = true
}

async function checkCodeExists(warehouseCode: string, code: string, excludeId?: string): Promise<boolean> {
  try {
    const where: any[] = [
      { field: 'warehouse_code', op: 'eq', value: warehouseCode },
      { field: 'bin_code', op: 'eq', value: code }
    ]
    const r = await api.post('/objects/bin/search', { page: 1, pageSize: 1, where, orderBy: [] })
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
    const exists = await checkCodeExists(editForm.warehouseCode, editForm.code)
    if (exists) {
      ElMessage.warning(labels.value.codeExists)
      return
    }
  }

  saving.value = true
  try {
    const payload = {
      warehouseCode: editForm.warehouseCode,
      code: editForm.code,
      name: editForm.name,
      inactive: editForm.inactive
    }

    if (editMode.value === 'create') {
      await api.post('/objects/bin', { 
        payload,
        warehouse_code: editForm.warehouseCode,
        bin_code: editForm.code,
        name: editForm.name
      })
    } else {
      await api.put(`/objects/bin/${editForm.id}`, { 
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
    await api.put(`/objects/bin/${row.id}`, { payload: newPayload })
    ElMessage.success(labels.value.statusUpdated)
    load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'Update failed')
  }
}

async function deleteRow(row: any) {
  // 先检查是否被引用
  try {
    const refResp = await api.get(`/objects/bin/${row.id}/references`)
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
      await api.delete(`/objects/bin/${row.id}`)
      ElMessage.success(labels.value.deleteSuccess)
      load()
    } catch (e: any) {
      ElMessage.error(e?.response?.data?.error || 'Delete failed')
    }
  }).catch(() => {})
}

onMounted(async () => {
  await loadWarehouses()
  await load()
})
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
</style>
