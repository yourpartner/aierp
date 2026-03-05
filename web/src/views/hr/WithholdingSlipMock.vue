<template>
  <div class="withholding-page">
    <el-card class="toolbar-card">
      <div class="toolbar">
        <div class="toolbar-left">
          <el-icon><Document /></el-icon>
          <span class="title">源泉徴収票作成</span>
        </div>
        <div class="toolbar-right">
          <el-select v-model="selectedYear" style="width: 120px" @change="loadEmployees">
            <el-option v-for="y in years" :key="y" :label="`${y}年`" :value="y" />
          </el-select>
          <el-select
            v-model="selectedEmployees"
            multiple
            collapse-tags
            collapse-tags-tooltip
            filterable
            style="width: 320px"
            placeholder="全員（未選択で全員対象）"
          >
            <el-option
              v-for="e in employees"
              :key="e.employeeCode"
              :label="`${e.name} (${e.employeeCode})`"
              :value="e.employeeCode"
            />
          </el-select>
          <el-button type="primary" :loading="generating" @click="onGenerate">
            <el-icon><Edit /></el-icon>
            PDF作成
          </el-button>
        </div>
      </div>
    </el-card>

    <el-card v-if="employees.length === 0 && !loadingEmployees" class="empty-card">
      <el-empty description="対象年度の給与データがありません" />
    </el-card>

    <el-card v-if="loadingEmployees">
      <div class="loading-area">
        <el-icon class="is-loading"><Loading /></el-icon>
        従業員データを読み込み中...
      </div>
    </el-card>

    <el-card v-if="employees.length > 0 && !generating && results.length === 0" class="employee-summary">
      <template #header>
        <div class="card-header">
          <span>対象従業員（{{ selectedYear }}年）</span>
          <el-tag type="info" size="small">{{ employees.length }}名</el-tag>
        </div>
      </template>
      <div class="emp-grid">
        <div
          v-for="e in employees"
          :key="e.employeeCode"
          class="emp-chip"
          :class="{ selected: selectedEmployees.includes(e.employeeCode) }"
          @click="toggleEmployee(e.employeeCode)"
        >
          <div class="emp-name">{{ e.name }}</div>
          <div class="emp-code">{{ e.employeeCode }}</div>
        </div>
      </div>
    </el-card>

    <!-- Generating progress -->
    <el-card v-if="generating" class="progress-card">
      <div class="progress-area">
        <el-icon class="is-loading" :size="24"><Loading /></el-icon>
        <span>源泉徴収票を作成中... ({{ progressCount }}/{{ progressTotal }})</span>
        <el-progress :percentage="progressPercent" :stroke-width="8" style="width: 400px; margin-top: 8px" />
      </div>
    </el-card>

    <!-- Results -->
    <el-card v-if="results.length > 0" class="results-card">
      <template #header>
        <div class="card-header">
          <span>作成結果</span>
          <div class="result-summary">
            <el-tag type="success" size="small">成功: {{ results.filter(r => r.status === 'success').length }}</el-tag>
            <el-tag v-if="results.filter(r => r.status === 'skipped').length" type="warning" size="small">スキップ: {{ results.filter(r => r.status === 'skipped').length }}</el-tag>
            <el-tag v-if="results.filter(r => r.status === 'error').length" type="danger" size="small">エラー: {{ results.filter(r => r.status === 'error').length }}</el-tag>
            <el-button size="small" @click="results = []">閉じる</el-button>
          </div>
        </div>
      </template>
      <el-table :data="results" stripe size="small">
        <el-table-column prop="employeeCode" label="社員番号" width="110" />
        <el-table-column prop="name" label="氏名" width="160" />
        <el-table-column label="ステータス" width="120">
          <template #default="{ row }">
            <el-tag :type="row.status === 'success' ? 'success' : row.status === 'skipped' ? 'warning' : 'danger'" size="small">
              {{ row.status === 'success' ? '成功' : row.status === 'skipped' ? 'スキップ' : 'エラー' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="備考" min-width="200">
          <template #default="{ row }">
            <span v-if="row.reason">{{ row.reason }}</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="120" fixed="right">
          <template #default="{ row }">
            <el-button
              v-if="row.url"
              type="primary"
              link
              size="small"
              @click="openPdf(row.url)"
            >
              <el-icon><Download /></el-icon>
              PDF表示
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- Overwrite confirmation dialog -->
    <el-dialog v-model="overwriteDialog.visible" title="確認" width="500px">
      <p>以下の従業員の源泉徴収票は既に作成済みです：</p>
      <ul class="existing-list">
        <li v-for="e in overwriteDialog.existingList" :key="e.employeeCode">
          {{ e.employeeCode }}
        </li>
      </ul>
      <p>上書きしますか？</p>
      <template #footer>
        <el-button @click="overwriteDialog.visible = false">キャンセル</el-button>
        <el-button type="warning" @click="executeGenerate(false)">未作成分のみ作成</el-button>
        <el-button type="primary" @click="executeGenerate(true)">全て上書き作成</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { Document, Download, Edit, Loading } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import api from '../../api'

const currentYear = new Date().getFullYear()
const years = Array.from({ length: 5 }, (_, i) => currentYear - i)
const selectedYear = ref(currentYear - 1)
const selectedEmployees = ref<string[]>([])

const employees = ref<any[]>([])
const loadingEmployees = ref(false)
const generating = ref(false)
const progressCount = ref(0)
const progressTotal = ref(0)
const progressPercent = ref(0)
const results = ref<any[]>([])

const overwriteDialog = ref({
  visible: false,
  existingList: [] as any[],
})

async function loadEmployees() {
  loadingEmployees.value = true
  selectedEmployees.value = []
  results.value = []
  try {
    const resp = await api.get('/payroll/withholding-slip/employees', {
      params: { year: selectedYear.value },
    })
    employees.value = resp.data || []
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || '従業員データの読み込みに失敗しました')
  } finally {
    loadingEmployees.value = false
  }
}

function toggleEmployee(code: string) {
  const idx = selectedEmployees.value.indexOf(code)
  if (idx >= 0) selectedEmployees.value.splice(idx, 1)
  else selectedEmployees.value.push(code)
}

async function onGenerate() {
  if (employees.value.length === 0) return

  const targetCodes = selectedEmployees.value.length > 0
    ? selectedEmployees.value
    : employees.value.map((e: any) => e.employeeCode)

  try {
    const resp = await api.get('/payroll/withholding-slip/check-existing', {
      params: { year: selectedYear.value, employeeCodes: targetCodes.join(',') },
    })
    const existing = resp.data || []
    if (existing.length > 0) {
      overwriteDialog.value.existingList = existing
      overwriteDialog.value.visible = true
      return
    }
  } catch {
    // check failed, proceed anyway
  }

  await executeGenerate(true)
}

async function executeGenerate(overwrite: boolean) {
  overwriteDialog.value.visible = false

  const targetCodes = selectedEmployees.value.length > 0
    ? selectedEmployees.value
    : employees.value.map((e: any) => e.employeeCode)

  generating.value = true
  progressTotal.value = targetCodes.length
  progressCount.value = 0
  progressPercent.value = 0
  results.value = []

  try {
    const resp = await api.post('/payroll/withholding-slip/generate', {
      year: String(selectedYear.value),
      employeeCodes: targetCodes,
      overwrite,
    })
    results.value = resp.data?.results || []
    const successCount = results.value.filter((r: any) => r.status === 'success').length
    ElMessage.success(`${successCount}件の源泉徴収票を作成しました`)
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || '作成に失敗しました')
  } finally {
    generating.value = false
    progressPercent.value = 100
  }
}

function openPdf(url: string) {
  window.open(url, '_blank')
}

onMounted(loadEmployees)
</script>

<style scoped>
.withholding-page {
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.toolbar-card :deep(.el-card__body) { padding: 12px 16px; }
.toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}
.toolbar-left {
  display: flex;
  align-items: center;
  gap: 8px;
}
.title {
  font-size: 17px;
  font-weight: 600;
  color: #303133;
}
.toolbar-right {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.loading-area, .progress-area {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 24px;
  gap: 8px;
  color: #606266;
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.result-summary {
  display: flex;
  align-items: center;
  gap: 8px;
}

.emp-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(160px, 1fr));
  gap: 8px;
}
.emp-chip {
  padding: 10px 12px;
  border: 1px solid #dcdfe6;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.2s;
  background: #fafafa;
}
.emp-chip:hover { border-color: var(--el-color-primary-light-3); background: #f0f5ff; }
.emp-chip.selected {
  border-color: var(--el-color-primary);
  background: var(--el-color-primary-light-9);
}
.emp-name { font-size: 13px; font-weight: 500; color: #303133; }
.emp-code { font-size: 11px; color: #909399; margin-top: 2px; }

.existing-list {
  margin: 8px 0;
  padding-left: 20px;
}
.existing-list li {
  padding: 2px 0;
  font-size: 13px;
}
</style>
