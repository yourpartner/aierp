<template>
  <div class="position-types-page">
    <el-card>
      <template #header>
        <div class="position-header">
          <div class="position-header__left">
            <el-icon><Medal /></el-icon>
            <span class="position-header__title">役職マスタ</span>
            <el-tag size="small" type="info">{{ rows.length }}件</el-tag>
          </div>
          <div class="position-header__right">
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
        <el-table-column prop="payload.code" label="コード" width="120" />
        <el-table-column prop="payload.name" label="役職名" min-width="200" />
        <el-table-column prop="payload.level" label="ランク" width="100" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.payload?.level" size="small" type="info">{{ row.payload.level }}</el-tag>
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
        <el-empty description="役職データがありません" :image-size="80" />
      </div>
    </el-card>

    <!-- 新規/編集ダイアログ -->
    <el-dialog v-model="show" :title="isEdit ? '役職編集' : '役職追加'" width="480px" append-to-body destroy-on-close>
      <el-form :model="form" label-width="100px" label-position="left">
        <el-form-item label="コード">
          <el-input v-model="form.code" :disabled="isEdit" placeholder="自動生成（空欄可）" maxlength="50" />
          <div class="form-hint">空欄の場合は自動生成されます</div>
        </el-form-item>
        <el-form-item label="役職名" required>
          <el-input v-model="form.name" placeholder="例：部長、課長、主任..." maxlength="100" />
        </el-form-item>
        <el-form-item label="ランク">
          <el-input-number v-model="form.level" :min="1" :max="99" placeholder="1" />
          <div class="form-hint">数字が小さいほど上位（例：1=社長、2=部長...）</div>
        </el-form-item>
        <el-form-item label="有効">
          <el-switch v-model="form.isActive" />
        </el-form-item>
      </el-form>
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
import { Medal, Plus, Refresh, Edit, Delete } from '@element-plus/icons-vue'
import api from '../api'

const rows = ref<any[]>([])
const show = ref(false)
const saving = ref(false)
const isEdit = ref(false)
const editId = ref<string | null>(null)
const form = ref<any>({ code: '', name: '', level: null, isActive: true })

async function load() {
  try {
    const r = await api.post('/objects/position_type/search', { 
      page: 1, 
      pageSize: 200, 
      where: [], 
      orderBy: [{ field: 'payload->>level', dir: 'ASC' }, { field: 'payload->>name', dir: 'ASC' }] 
    })
    rows.value = r.data?.data || []
  } catch {
    rows.value = []
  }
}

function openNew() {
  form.value = { code: '', name: '', level: null, isActive: true }
  isEdit.value = false
  editId.value = null
  show.value = true
}

function openEdit(row: any) {
  form.value = { 
    code: row.payload?.code || '', 
    name: row.payload?.name || '', 
    level: row.payload?.level || null,
    isActive: row.payload?.isActive ?? true
  }
  isEdit.value = true
  editId.value = row.id
  show.value = true
}

async function save() {
  if (!form.value.name?.trim()) {
    ElMessage.error('役職名は必須です')
    return
  }
  saving.value = true
  try {
    const payload: any = { 
      name: form.value.name.trim(),
      isActive: form.value.isActive 
    }
    if (form.value.code?.trim()) {
      payload.code = form.value.code.trim()
    }
    if (form.value.level) {
      payload.level = form.value.level
    }
    
    if (isEdit.value && editId.value) {
      await api.put(`/objects/position_type/${editId.value}`, { payload })
      ElMessage.success('役職を更新しました')
    } else {
      await api.post('/objects/position_type', { payload })
      ElMessage.success('役職を追加しました')
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
    await api.delete(`/objects/position_type/${row.id}`)
    ElMessage.success('役職を削除しました')
    await load()
  } catch {
    // cancelled
  }
}

load()
</script>

<style scoped>
.position-types-page {
  padding: 16px;
}

.position-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.position-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.position-header__left .el-icon {
  font-size: 20px;
  color: #667eea;
}

.position-header__title {
  font-size: 16px;
  font-weight: 600;
  color: #303133;
}

.position-header__right {
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
</style>

