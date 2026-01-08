<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Setting /></el-icon>
            <span class="page-header-title">{{ text.title }}</span>
          </div>
          <div class="page-actions">
            <el-button type="primary" @click="openCreate">{{ text.add }}</el-button>
          </div>
        </div>
        <div class="page-header-sub">{{ text.description }}</div>
      </template>

      <el-tabs v-model="activeStatement" @tab-change="onTabChange">
        <el-tab-pane :label="text.balanceSheet" name="BS" />
        <el-tab-pane :label="text.incomeStatement" name="PL" />
      </el-tabs>

      <el-table :data="rows" row-key="id" v-loading="loading" style="width: 100%">
        <el-table-column prop="code" :label="text.code" width="140" />
        <el-table-column prop="nameJa" :label="text.nameJa" min-width="200" />
        <el-table-column prop="nameEn" :label="text.nameEn" min-width="200" />
        <el-table-column prop="parentCode" :label="text.parent" width="160" />
        <el-table-column prop="order" :label="text.order" width="100" />
        <el-table-column :label="text.isSubtotal" width="120">
          <template #default="{ row }">
            <el-tag size="small" :type="row.isSubtotal ? 'success' : 'info'">{{ row.isSubtotal ? commonText.enabled : commonText.disabled }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="note" :label="text.note" min-width="200" />
        <el-table-column :label="commonText.actions" width="140" fixed="right">
          <template #default="{ row }">
            <el-button text type="primary" size="small" @click="openEdit(row)">{{ commonText.edit }}</el-button>
            <el-button text type="danger" size="small" @click="remove(row)">{{ commonText.delete }}</el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-empty v-if="!loading && rows.length === 0" :description="commonText.none" />
    </el-card>

    <el-dialog v-model="dialogVisible" :title="dialogMode === 'create' ? text.add : text.edit" width="520px" append-to-body destroy-on-close>
      <el-form :model="form" label-width="120px">
        <el-form-item :label="text.statement">
          <el-select v-model="form.statement" disabled>
            <el-option value="BS" :label="text.balanceSheet" />
            <el-option value="PL" :label="text.incomeStatement" />
          </el-select>
        </el-form-item>
        <el-form-item :label="text.code">
          <el-input v-model="form.code" :disabled="dialogMode === 'edit'" maxlength="48" />
        </el-form-item>
        <el-form-item :label="text.nameJa">
          <el-input v-model="form.nameJa" maxlength="120" />
        </el-form-item>
        <el-form-item :label="text.nameEn">
          <el-input v-model="form.nameEn" maxlength="120" />
        </el-form-item>
        <el-form-item :label="text.parent">
          <el-select v-model="form.parentCode" clearable :placeholder="text.parentPlaceholder">
            <el-option v-for="opt in parentOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
          </el-select>
        </el-form-item>
        <el-form-item :label="text.order">
          <el-input-number v-model="form.order" :min="-9999" :max="9999" :step="1" controls-position="right" />
        </el-form-item>
        <el-form-item :label="text.isSubtotal">
          <el-switch v-model="form.isSubtotal" />
        </el-form-item>
        <el-form-item :label="text.note">
          <el-input v-model="form.note" type="textarea" :rows="3" maxlength="200" show-word-limit />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible=false">{{ commonText.close }}</el-button>
        <el-button type="primary" :loading="saving" @click="save">{{ commonText.save }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Setting } from '@element-plus/icons-vue'
import api from '../api'
import { useI18n } from '../i18n'

type StatementType = 'BS' | 'PL'

const { section, lang } = useI18n()

const text = section({
  title: '',
  description: '',
  add: '',
  edit: '',
  delete: '',
  balanceSheet: '',
  incomeStatement: '',
  statement: '',
  code: '',
  nameJa: '',
  nameEn: '',
  parent: '',
  parentPlaceholder: '',
  order: '',
  isSubtotal: '',
  note: '',
  saveSuccess: '',
  deleteSuccess: '',
  saveFailed: ''
}, (msg) => msg.financialNodes)

const commonText = section({
  enabled: '',
  disabled: '',
  save: '',
  saved: '',
  close: '',
  delete: '',
  edit: '',
  actions: '',
  none: '',
  confirmDelete: ''
}, (msg) => msg.financialCommon)

const activeStatement = ref<StatementType>('BS')
const rows = ref<any[]>([])
const loading = ref(false)
const saving = ref(false)
const dialogVisible = ref(false)
const dialogMode = ref<'create' | 'edit'>('create')
const currentId = ref<string>('')
const form = reactive<any>({
  statement: 'BS',
  code: '',
  nameJa: '',
  nameEn: '',
  parentCode: '',
  order: null,
  isSubtotal: false,
  note: ''
})

const parentOptions = computed(() => {
  return rows.value
    .filter((row) => row.code !== form.code)
    .map((row) => ({
      label: lang.value === 'en' ? (row.nameEn || row.nameJa || row.code) : (row.nameJa || row.nameEn || row.code),
      value: row.code
    }))
})

async function loadNodes() {
  loading.value = true
  try {
    const r = await api.get('/financial/fs-nodes', { params: { statement: activeStatement.value } })
    rows.value = Array.isArray(r.data) ? r.data : []
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || text.value.saveFailed)
    rows.value = []
  } finally {
    loading.value = false
  }
}

function resetForm() {
  currentId.value = ''
  form.statement = activeStatement.value
  form.code = ''
  form.nameJa = ''
  form.nameEn = ''
  form.parentCode = ''
  form.order = null
  form.isSubtotal = false
  form.note = ''
}

function openCreate() {
  dialogMode.value = 'create'
  resetForm()
  dialogVisible.value = true
}

function openEdit(row: any) {
  dialogMode.value = 'edit'
  currentId.value = row.id
  form.statement = row.statement || activeStatement.value
  form.code = row.code
  form.nameJa = row.nameJa || ''
  form.nameEn = row.nameEn || ''
  form.parentCode = row.parentCode || ''
  form.order = typeof row.order === 'number' ? row.order : null
  form.isSubtotal = !!row.isSubtotal
  form.note = row.note || ''
  dialogVisible.value = true
}

async function save() {
  saving.value = true
  try {
    const payload = { ...form }
    if (!payload.parentCode) payload.parentCode = null
    if (payload.order === null || payload.order === undefined) payload.order = null
    if (dialogMode.value === 'create') {
      await api.post('/financial/fs-nodes', payload)
    } else {
      const targetId = currentId.value || payload.code
      await api.put(`/financial/fs-nodes/${encodeURIComponent(targetId)}`, payload)
    }
    ElMessage.success(text.value.saveSuccess)
    dialogVisible.value = false
    await loadNodes()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || text.value.saveFailed)
  } finally {
    saving.value = false
  }
}

async function remove(row: any) {
  try {
    await ElMessageBox.confirm(text.value.deleteConfirm.replace('{name}', row.code), text.value.delete, {
      type: 'warning'
    })
  } catch {
    return
  }
  try {
    await api.delete(`/financial/fs-nodes/${row.id}`)
    ElMessage.success(text.value.deleteSuccess)
    await loadNodes()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || e?.message || text.value.saveFailed)
  }
}

function onTabChange() {
  resetForm()
  loadNodes()
}

watch(() => lang.value, () => {
  rows.value = [...rows.value]
})

onMounted(loadNodes)

</script>

<style scoped>
.page {
  padding: 16px;
}

/* 标题区域样式 */
.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #409eff;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.page-header-sub {
  color: #6b7280;
  font-size: 13px;
  margin-top: 4px;
}
</style>


