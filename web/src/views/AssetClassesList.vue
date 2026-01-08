<template>
  <div class="asset-classes-list">
    <el-card class="asset-classes-card">
      <template #header>
        <div class="asset-classes-header">
          <div class="asset-classes-header__left">
            <el-icon class="asset-classes-header__icon"><Folder /></el-icon>
            <span class="asset-classes-header__title">資産クラス管理</span>
            <el-tag size="small" type="info" class="asset-classes-header__count">{{ rows.length }}件</el-tag>
          </div>
          <div class="asset-classes-header__right">
            <el-button type="primary" @click="openCreateDialog">
              <el-icon><Plus /></el-icon>
              <span>新規登録</span>
            </el-button>
          </div>
        </div>
      </template>
      <el-table :data="rows" border stripe highlight-current-row class="asset-classes-table" v-loading="loading">
        <el-table-column label="資産クラス名称" prop="class_name" min-width="180" />
        <el-table-column label="有形/無形" width="120">
          <template #default="{ row }">
            <el-tag :type="row.asset_type === 'TANGIBLE' ? 'primary' : 'success'" size="small">
              {{ row.asset_type === 'TANGIBLE' ? '有形資産' : '無形資産' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="取得勘定" min-width="100">
          <template #default="{ row }">
            {{ row.acquisition_account }}
          </template>
        </el-table-column>
        <el-table-column label="除却勘定" min-width="100">
          <template #default="{ row }">
            {{ row.disposal_account }}
          </template>
        </el-table-column>
        <el-table-column label="償却借方勘定" min-width="100">
          <template #default="{ row }">
            {{ row.depreciation_expense_account }}
          </template>
        </el-table-column>
        <el-table-column label="償却貸方勘定" min-width="100">
          <template #default="{ row }">
            {{ row.accumulated_depreciation_account }}
          </template>
        </el-table-column>
        <el-table-column label="消費税ありの償却転記" width="160">
          <template #default="{ row }">
            <el-checkbox :model-value="row.include_tax_in_depreciation" disabled />
          </template>
        </el-table-column>
        <el-table-column label="アクション" width="140" fixed="right">
          <template #default="{ row }">
            <el-button size="small" type="primary" circle @click="openEditDialog(row)">
              <el-icon><Edit /></el-icon>
            </el-button>
            <el-button size="small" type="danger" circle @click="confirmDelete(row)">
              <el-icon><Delete /></el-icon>
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 新建/编辑弹窗 -->
    <el-dialog v-model="showDialog" :title="isEdit ? '編集' : '新規登録'" width="600px" destroy-on-close class="asset-class-dialog">
      <el-form :model="form" label-width="160px" @submit.prevent="saveForm">
        <el-form-item label="資産クラス名称" required>
          <el-input v-model="form.className" placeholder="例：ソフトウェア" style="width: 320px" />
        </el-form-item>
        <el-form-item label="有形/無形">
          <el-switch 
            v-model="form.isTangible" 
            active-text="有形資産" 
            inactive-text="無形資産"
            inline-prompt
          />
        </el-form-item>
        <el-form-item label="取得勘定" required>
          <el-select v-model="form.acquisitionAccount" filterable placeholder="科目を選択" style="width: 320px">
            <el-option 
              v-for="opt in accountOptions" 
              :key="opt.value" 
              :label="opt.label" 
              :value="opt.value" 
            />
          </el-select>
        </el-form-item>
        <el-form-item label="除却勘定" required>
          <el-select v-model="form.disposalAccount" filterable placeholder="科目を選択" style="width: 320px">
            <el-option 
              v-for="opt in accountOptions" 
              :key="opt.value" 
              :label="opt.label" 
              :value="opt.value" 
            />
          </el-select>
        </el-form-item>
        <el-form-item label="償却借方勘定" required>
          <el-select v-model="form.depreciationExpenseAccount" filterable placeholder="科目を選択" style="width: 320px">
            <el-option 
              v-for="opt in accountOptions" 
              :key="opt.value" 
              :label="opt.label" 
              :value="opt.value" 
            />
          </el-select>
        </el-form-item>
        <el-form-item label="償却貸方勘定" required>
          <el-select v-model="form.accumulatedDepreciationAccount" filterable placeholder="科目を選択" style="width: 320px">
            <el-option 
              v-for="opt in accountOptions" 
              :key="opt.value" 
              :label="opt.label" 
              :value="opt.value" 
            />
          </el-select>
        </el-form-item>
        <el-form-item label="消費税ありの償却転記">
          <el-checkbox v-model="form.includeTaxInDepreciation" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showDialog = false">キャンセル</el-button>
        <el-button type="primary" @click="saveForm" :loading="saving">{{ isEdit ? '更新' : '登録' }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, reactive } from 'vue'
import api from '../api'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Edit, Delete, Folder, Plus } from '@element-plus/icons-vue'

const rows = ref<any[]>([])
const loading = ref(false)
const showDialog = ref(false)
const isEdit = ref(false)
const editId = ref<string | null>(null)
const saving = ref(false)
const accountOptions = ref<{ label: string; value: string }[]>([])

const form = reactive({
  className: '',
  isTangible: true,
  acquisitionAccount: '',
  disposalAccount: '',
  depreciationExpenseAccount: '',
  accumulatedDepreciationAccount: '',
  includeTaxInDepreciation: false
})

function resetForm() {
  form.className = ''
  form.isTangible = true
  form.acquisitionAccount = ''
  form.disposalAccount = ''
  form.depreciationExpenseAccount = ''
  form.accumulatedDepreciationAccount = ''
  form.includeTaxInDepreciation = false
  editId.value = null
}

async function loadAccounts() {
  try {
    const r = await api.post('/objects/account/search', { where: [], page: 1, pageSize: 500 })
    const data = r.data?.data || []
    accountOptions.value = data.map((x: any) => ({
      label: `${x.account_code} ${x.payload?.name || ''}`,
      value: x.account_code
    }))
  } catch (e) {
    console.error('Failed to load accounts', e)
  }
}

async function load() {
  loading.value = true
  try {
    const resp = await api.get('/fixed-assets/classes')
    rows.value = Array.isArray(resp.data) ? resp.data : []
  } catch (e) {
    console.error('Failed to load asset classes', e)
    ElMessage.error('資産クラスの読み込みに失敗しました')
  } finally {
    loading.value = false
  }
}

function openCreateDialog() {
  resetForm()
  isEdit.value = false
  showDialog.value = true
}

function openEditDialog(row: any) {
  resetForm()
  isEdit.value = true
  editId.value = row.id
  const payload = row.payload || {}
  form.className = payload.className || row.class_name || ''
  form.isTangible = payload.isTangible !== false && row.asset_type !== 'INTANGIBLE'
  form.acquisitionAccount = payload.acquisitionAccount || row.acquisition_account || ''
  form.disposalAccount = payload.disposalAccount || row.disposal_account || ''
  form.depreciationExpenseAccount = payload.depreciationExpenseAccount || row.depreciation_expense_account || ''
  form.accumulatedDepreciationAccount = payload.accumulatedDepreciationAccount || row.accumulated_depreciation_account || ''
  form.includeTaxInDepreciation = payload.includeTaxInDepreciation || row.include_tax_in_depreciation || false
  showDialog.value = true
}

async function saveForm() {
  if (!form.className) {
    ElMessage.warning('資産クラス名称を入力してください')
    return
  }
  if (!form.acquisitionAccount || !form.disposalAccount || !form.depreciationExpenseAccount || !form.accumulatedDepreciationAccount) {
    ElMessage.warning('すべての勘定科目を選択してください')
    return
  }

  saving.value = true
  try {
    const payload = {
      className: form.className,
      isTangible: form.isTangible,
      acquisitionAccount: form.acquisitionAccount,
      disposalAccount: form.disposalAccount,
      depreciationExpenseAccount: form.depreciationExpenseAccount,
      accumulatedDepreciationAccount: form.accumulatedDepreciationAccount,
      includeTaxInDepreciation: form.includeTaxInDepreciation
    }

    if (isEdit.value && editId.value) {
      await api.put(`/fixed-assets/classes/${editId.value}`, payload)
      ElMessage.success('更新しました')
    } else {
      await api.post('/fixed-assets/classes', payload)
      ElMessage.success('登録しました')
    }
    showDialog.value = false
    await load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '保存に失敗しました')
  } finally {
    saving.value = false
  }
}

async function confirmDelete(row: any) {
  try {
    await ElMessageBox.confirm(
      `資産クラス「${row.class_name}」を削除しますか？`,
      '削除確認',
      { confirmButtonText: '削除', cancelButtonText: 'キャンセル', type: 'warning' }
    )
    await api.delete(`/fixed-assets/classes/${row.id}`)
    ElMessage.success('削除しました')
    await load()
  } catch (e: any) {
    if (e !== 'cancel' && e?.response?.data?.error) {
      ElMessage.error(e.response.data.error)
    }
  }
}

onMounted(async () => {
  await loadAccounts()
  await load()
})
</script>

<style scoped>
.asset-classes-list {
  padding: 16px;
}

.asset-classes-card {
  border-radius: 12px;
  overflow: hidden;
}

.asset-classes-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.asset-classes-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.asset-classes-header__icon {
  font-size: 22px;
  color: #67c23a;
}

.asset-classes-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.asset-classes-header__count {
  font-weight: 500;
}

.asset-classes-header__right {
  display: flex;
  gap: 8px;
}

/* テーブル */
.asset-classes-table {
  border-radius: 8px;
  overflow: hidden;
}

.asset-classes-table :deep(.el-table__header th) {
  background: #f5f7fa;
  font-weight: 600;
  color: #303133;
}
</style>


