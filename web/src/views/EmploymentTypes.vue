<template>
  <div class="employment-types-page">
    <el-card>
      <template #header>
        <div class="types-header">
          <div class="types-header__left">
            <el-icon><Briefcase /></el-icon>
            <span class="types-header__title">雇用区分</span>
            <el-tag size="small" type="info">{{ rows.length }}件</el-tag>
          </div>
          <div class="types-header__right">
            <el-button type="primary" size="small" @click="openNew">
              <el-icon><Plus /></el-icon>
              新規追加
            </el-button>
            <el-button size="small" @click="load">
              <el-icon><Refresh /></el-icon>
              更新
            </el-button>
          </div>
        </div>
      </template>

      <el-table :data="rows" size="small" border stripe>
        <el-table-column type="index" width="50" align="center" />
        <el-table-column prop="payload.code" label="コード" width="140" />
        <el-table-column prop="payload.name" label="名称" min-width="200" />
        <el-table-column label="個人事業主" width="120" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.payload?.isContractor" type="warning" size="small">該当</el-tag>
            <span v-else class="empty-text">—</span>
          </template>
        </el-table-column>
        <el-table-column label="有効" width="80" align="center">
          <template #default="{ row }">
            <el-tag :type="row.payload?.isActive !== false ? 'success' : 'info'" size="small">
              {{ row.payload?.isActive !== false ? '有効' : '無効' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="" width="80" align="center">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click="openEdit(row)">
              <el-icon><Edit /></el-icon>
            </el-button>
            <el-button link type="danger" size="small" @click="confirmDelete(row)">
              <el-icon><Delete /></el-icon>
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <div v-if="!rows.length" class="empty-state">
        <el-empty description="雇用区分データがありません" :image-size="80" />
      </div>
    </el-card>

    <!-- 新規/編集ダイアログ -->
    <el-dialog v-model="show" :title="isEdit ? '雇用区分編集' : '雇用区分追加'" width="520px" append-to-body destroy-on-close>
      <el-form :model="form" label-width="120px" label-position="left">
        <el-form-item label="コード">
          <el-input v-model="form.code" :disabled="isEdit" placeholder="自動生成（空欄可）" maxlength="50" />
          <div class="form-hint">空欄の場合は自動生成されます</div>
        </el-form-item>
        <el-form-item label="名称" required>
          <el-input v-model="form.name" @input="onNameInput" placeholder="例：正社員、契約社員、個人事業主..." maxlength="100" />
        </el-form-item>
        <el-form-item label="個人事業主">
          <el-switch v-model="form.isContractor" />
          <span class="form-hint" style="margin-left: 12px;">
            該当する場合はON（インボイス番号が必要になります）
          </span>
        </el-form-item>
        <el-form-item label="有効">
          <el-switch v-model="form.isActive" />
        </el-form-item>
      </el-form>
      <div v-if="autoMatchHint" class="auto-match-hint">
        <el-alert :title="autoMatchHint" type="info" :closable="false" show-icon />
      </div>
      <template #footer>
        <el-button @click="show=false">キャンセル</el-button>
        <el-button type="primary" :loading="saving" @click="save" :disabled="!form.name?.trim()">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Briefcase, Plus, Refresh, Edit, Delete } from '@element-plus/icons-vue'
import api from '../api'

const rows = ref<any[]>([])
const show = ref(false)
const saving = ref(false)
const isEdit = ref(false)
const editId = ref<string | null>(null)
const form = ref<any>({ code: '', name: '', isActive: true, isContractor: false })
const autoMatchHint = ref('')

// 个人事业主类型的关键词
const contractorKeywords = ['個人事業主', '个人事业主', '業務委託', '业务委托', '外注', 'フリーランス', 'freelance']

async function load() {
  try {
    const r = await api.post('/objects/employment_type/search', { 
      page: 1, 
      pageSize: 200, 
      where: [], 
      orderBy: [{ field: 'type_code', dir: 'ASC' }] 
    })
    rows.value = r.data?.data || []
  } catch {
    rows.value = []
  }
}

function openNew() {
  form.value = { code: '', name: '', isActive: true, isContractor: false }
  isEdit.value = false
  editId.value = null
  autoMatchHint.value = ''
  show.value = true
}

function openEdit(row: any) {
  form.value = { 
    code: row.payload?.code || '', 
    name: row.payload?.name || '', 
    isActive: row.payload?.isActive ?? true,
    isContractor: row.payload?.isContractor ?? false
  }
  isEdit.value = true
  editId.value = row.id
  autoMatchHint.value = ''
  show.value = true
}

function onNameInput() {
  const name = (form.value.name || '').toLowerCase()
  const matched = contractorKeywords.some(kw => name.includes(kw.toLowerCase()))
  
  if (matched && !form.value.isContractor) {
    form.value.isContractor = true
    autoMatchHint.value = '個人事業主関連のキーワードを検出しました。「個人事業主」を自動的にONにしました。'
  } else if (!matched && autoMatchHint.value) {
    autoMatchHint.value = ''
  }
}

async function save() {
  if (!form.value.name?.trim()) {
    ElMessage.error('名称は必須です')
    return
  }
  saving.value = true
  try {
    const payload: any = { 
      name: form.value.name.trim(),
      isActive: form.value.isActive,
      isContractor: form.value.isContractor
    }
    if (form.value.code?.trim()) {
      payload.code = form.value.code.trim()
    }
    
    if (isEdit.value && editId.value) {
      await api.put(`/objects/employment_type/${editId.value}`, { payload })
      ElMessage.success('雇用区分を更新しました')
    } else {
      await api.post('/objects/employment_type', { payload })
      ElMessage.success('雇用区分を追加しました')
    }
    show.value = false
    await load()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '保存に失敗しました')
  } finally {
    saving.value = false
  }
}

async function confirmDelete(row: any) {
  try {
    await ElMessageBox.confirm(
      `「${row.payload?.name}」を削除しますか？`,
      '削除確認',
      { confirmButtonText: '削除', cancelButtonText: 'キャンセル', type: 'warning' }
    )
    await api.delete(`/objects/employment_type/${row.id}`)
    ElMessage.success('雇用区分を削除しました')
    await load()
  } catch {
    // cancelled
  }
}

load()
</script>

<style scoped>
.employment-types-page {
  padding: 16px;
}

.types-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.types-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.types-header__left .el-icon {
  font-size: 20px;
  color: #667eea;
}

.types-header__title {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.types-header__right {
  display: flex;
  gap: 8px;
}

.empty-text {
  color: #c0c4cc;
}

.empty-state {
  padding: 40px 0;
}

.form-hint {
  font-size: 12px;
  color: #909399;
  margin-top: 4px;
}

.auto-match-hint {
  padding: 0 20px;
  margin-top: -10px;
  margin-bottom: 10px;
}
</style>
