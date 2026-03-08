<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Document /></el-icon>
            <span class="page-header-title">源泉徴収票</span>
          </div>
          <div class="page-actions">
            <el-select v-model="selectedYear" style="width: 120px" @change="onYearChange">
              <el-option v-for="y in years" :key="y" :label="`${y}年`" :value="y" />
            </el-select>
          </div>
        </div>
      </template>

      <el-tabs v-model="activeTab" @tab-change="onTabChange">
        <!-- Tab 1: Existing PDFs -->
        <el-tab-pane label="作成済み一覧" name="list">
          <el-skeleton v-if="loadingList" :rows="4" animated />
          <el-empty v-else-if="existingPdfs.length === 0" description="作成済みの源泉徴収票はありません" />
          <el-table v-else :data="existingPdfs" stripe size="small">
            <el-table-column prop="employeeCode" label="社員番号" width="120" />
            <el-table-column prop="name" label="氏名" min-width="160" />
            <el-table-column prop="createdOn" label="作成日時" width="160" />
            <el-table-column label="操作" width="120" fixed="right">
              <template #default="{ row }">
                <el-button type="primary" link size="small" @click="openPdf(row.url)">
                  <el-icon style="margin-right:2px"><Download /></el-icon>PDF表示
                </el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <!-- Tab 2: Generate -->
        <el-tab-pane label="PDF作成" name="generate">
          <div class="generate-toolbar">
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
              <el-icon style="margin-right:4px"><Edit /></el-icon>PDF作成
            </el-button>
            <el-tag v-if="employees.length > 0" type="info" size="small">{{ employees.length }}名</el-tag>
          </div>

          <el-skeleton v-if="loadingEmployees" :rows="4" animated />
          <el-empty v-else-if="employees.length === 0" description="対象年度の給与データがありません" />

          <!-- Employee grid -->
          <template v-else-if="!generating && results.length === 0">
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
          </template>

          <!-- Generating progress -->
          <div v-if="generating" class="progress-area">
            <el-icon class="is-loading" :size="24"><Loading /></el-icon>
            <span>源泉徴収票を作成中... ({{ progressCount }}/{{ progressTotal }})</span>
            <el-progress :percentage="progressPercent" :stroke-width="8" style="width: 400px; margin-top: 8px" />
          </div>

          <!-- Results -->
          <template v-if="results.length > 0">
            <div class="result-header">
              <span class="result-title">作成結果</span>
              <div class="result-summary">
                <el-tag type="success" size="small">成功: {{ results.filter(r => r.status === 'success').length }}</el-tag>
                <el-tag v-if="results.filter(r => r.status === 'skipped').length" type="warning" size="small">スキップ: {{ results.filter(r => r.status === 'skipped').length }}</el-tag>
                <el-tag v-if="results.filter(r => r.status === 'error').length" type="danger" size="small">エラー: {{ results.filter(r => r.status === 'error').length }}</el-tag>
                <el-button size="small" @click="results = []">閉じる</el-button>
              </div>
            </div>
            <el-table :data="results" stripe size="small" style="margin-top: 12px">
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
          </template>
        </el-tab-pane>
      </el-tabs>
    </el-card>

    <!-- Overwrite confirmation dialog -->
    <el-dialog v-model="overwriteDialog.visible" title="確認" width="500px"
      append-to-body>
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
const activeTab = ref('list')
const selectedEmployees = ref<string[]>([])

const employees = ref<any[]>([])
const loadingEmployees = ref(false)
const generating = ref(false)
const progressCount = ref(0)
const progressTotal = ref(0)
const progressPercent = ref(0)
const results = ref<any[]>([])

const existingPdfs = ref<any[]>([])
const loadingList = ref(false)

const overwriteDialog = ref({
  visible: false,
  existingList: [] as any[],
})

async function loadExistingPdfs() {
  loadingList.value = true
  try {
    const resp = await api.get('/payroll/withholding-slip/list', {
      params: { year: selectedYear.value },
    })
    existingPdfs.value = resp.data || []
  } catch (err: any) {
    ElMessage.error(err?.response?.data?.error || '一覧の読み込みに失敗しました')
  } finally {
    loadingList.value = false
  }
}

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

function onYearChange() {
  if (activeTab.value === 'list') {
    loadExistingPdfs()
  } else {
    loadEmployees()
  }
}

function onTabChange(tab: string) {
  if (tab === 'list' && existingPdfs.value.length === 0) {
    loadExistingPdfs()
  } else if (tab === 'generate' && employees.value.length === 0) {
    loadEmployees()
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
    // Refresh the list tab data
    loadExistingPdfs()
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

onMounted(loadExistingPdfs)
</script>

<style scoped>
.generate-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 16px;
}

.progress-area {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 24px;
  gap: 8px;
  color: #606266;
}

.result-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding-top: 12px;
  border-top: 1px solid #ebeef5;
  margin-top: 12px;
}
.result-title {
  font-size: 14px;
  font-weight: 600;
  color: #303133;
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
