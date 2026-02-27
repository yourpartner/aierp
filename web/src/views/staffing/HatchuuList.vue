<template>
  <div class="hatchuu-list">
    <el-card class="hatchuu-card">
      <template #header>
        <div class="hatchuu-header">
          <div class="hatchuu-header__left">
            <el-icon class="hatchuu-header__icon"><DocumentAdd /></el-icon>
            <span class="hatchuu-header__title">発注一覧</span>
            <el-tag size="small" type="info" class="hatchuu-header__count">{{ total }}件</el-tag>
          </div>
          <div class="hatchuu-header__right">
            <el-button type="primary" @click="openNew">
              <el-icon><Plus /></el-icon>
              <span>新規発注</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- フィルター -->
      <div class="hatchuu-filters">
        <el-select v-model="statusFilter" clearable placeholder="ステータス" style="width:130px">
          <el-option label="下書き" value="draft" />
          <el-option label="有効" value="active" />
          <el-option label="終了" value="ended" />
          <el-option label="解約" value="terminated" />
        </el-select>

        <el-select v-model="contractTypeFilter" clearable placeholder="契約形態" style="width:160px">
          <el-option label="SES（準委任）" value="ses" />
          <el-option label="派遣" value="dispatch" />
          <el-option label="請負" value="contract" />
        </el-select>

        <el-input
          v-model="keyword"
          placeholder="発注番号・リソース名で検索"
          clearable
          style="width:220px"
          @keyup.enter="load"
        >
          <template #prefix><el-icon><Search /></el-icon></template>
        </el-input>

        <el-button type="primary" plain @click="load">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
      </div>

      <!-- テーブル -->
      <el-table
        :data="rows"
        border
        stripe
        highlight-current-row
        class="hatchuu-table"
        v-loading="loading"
        @row-dblclick="onEdit"
      >
        <el-table-column type="index" width="48" align="center" />

        <el-table-column label="発注番号" width="130">
          <template #default="{ row }">
            <span class="doc-no">{{ row.hatchuuNo }}</span>
          </template>
        </el-table-column>

        <el-table-column label="受注番号" width="115">
          <template #default="{ row }">
            <span v-if="row.juchuuNo" class="juchuu-no">{{ row.juchuuNo }}</span>
            <span v-else class="cell-empty">-</span>
          </template>
        </el-table-column>

        <el-table-column label="リソース" min-width="130">
          <template #default="{ row }">
            <div v-if="row.resourceName">
              <div class="cell-main">{{ row.resourceName }}</div>
              <div class="cell-sub">{{ row.resourceCode }}</div>
            </div>
            <span v-else class="cell-empty">-</span>
          </template>
        </el-table-column>

        <el-table-column label="発注先（BP）" min-width="130">
          <template #default="{ row }">
            <div v-if="row.supplierName" class="cell-main">{{ row.supplierName }}</div>
            <span v-else class="cell-empty">-</span>
          </template>
        </el-table-column>

        <el-table-column label="契約形態" width="100" align="center">
          <template #default="{ row }">
            <el-tag :type="contractTypeColor(row.contractType)" size="small">
              {{ contractTypeLabel(row.contractType) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="期間" width="200" align="center">
          <template #default="{ row }">
            <span>{{ row.startDate || '-' }} 〜 {{ row.endDate || '-' }}</span>
          </template>
        </el-table-column>

        <el-table-column label="原価単価" width="130" align="right">
          <template #default="{ row }">
            <span v-if="row.costRate" class="rate-cell">
              ¥{{ Number(row.costRate).toLocaleString() }}
              <small>/ {{ rateTypeLabel(row.costRateType) }}</small>
            </span>
            <span v-else class="cell-empty">-</span>
          </template>
        </el-table-column>

        <el-table-column label="発注書" width="90" align="center">
          <template #default="{ row }">
            <el-button
              v-if="row.hasPdf"
              size="small"
              type="success"
              link
              @click.stop="printPdf(row)"
              title="発注書を表示"
            >
              <el-icon><Printer /></el-icon>
              表示
            </el-button>
            <span v-else class="cell-empty">-</span>
          </template>
        </el-table-column>

        <el-table-column label="ステータス" width="90" align="center">
          <template #default="{ row }">
            <el-tag :type="statusColor(row.status)" size="small">
              {{ statusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="操作" width="130" align="center" fixed="right">
          <template #default="{ row }">
            <el-button size="small" @click.stop="onEdit(row)">
              <el-icon><Edit /></el-icon>
              編集
            </el-button>
            <el-button size="small" type="warning" @click.stop="onGeneratePdf(row)" :loading="row._generating">
              <el-icon><Document /></el-icon>
              PDF
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <div class="hatchuu-pagination">
        <el-pagination
          v-model:current-page="currentPage"
          v-model:page-size="pageSize"
          :total="total"
          :page-sizes="[20, 50, 100]"
          layout="total, sizes, prev, pager, next"
          @change="load"
        />
      </div>
    </el-card>

    <!-- 発注フォームダイアログ -->
    <el-dialog
      v-model="dialogVisible"
      :title="editId ? '発注編集' : '新規発注登録'"
      width="800px"
      :close-on-click-modal="false"
      destroy-on-close
    >
      <HatchuuForm
        :hatchuu-id="editId"
        :initial-juchuu-id="initialJuchuuId"
        :initial-juchuu-no="initialJuchuuNo"
        @saved="onSaved"
        @cancel="dialogVisible = false"
      />
    </el-dialog>

    <!-- 発注書HTMLプレビューダイアログ -->
    <el-dialog
      v-model="pdfDialogVisible"
      title="発注書プレビュー"
      width="860px"
      :close-on-click-modal="false"
    >
      <div v-html="pdfHtml" class="pdf-preview" />
    </el-dialog>
  </div>
</template>

<script setup>
import { ref, onMounted } from 'vue'
import {
  DocumentAdd, Plus, Search, Edit,
  Document, Printer
} from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import HatchuuForm from './HatchuuForm.vue'

const props = defineProps({
  initialJuchuuId: { type: String, default: null },
  initialJuchuuNo: { type: String, default: null }
})

const rows = ref([])
const total = ref(0)
const loading = ref(false)
const currentPage = ref(1)
const pageSize = ref(50)

const statusFilter = ref('')
const contractTypeFilter = ref('')
const keyword = ref('')

const dialogVisible = ref(false)
const editId = ref(null)
const initialJuchuuId = ref(props.initialJuchuuId)
const initialJuchuuNo = ref(props.initialJuchuuNo)

const pdfDialogVisible = ref(false)
const pdfHtml = ref('')

function apiBase() {
  const host = window.location.hostname
  if (host === 'localhost' || host === '127.0.0.1') return 'http://localhost:5181'
  return ''
}

function getHeaders() {
  const token = localStorage.getItem('token') || sessionStorage.getItem('token')
  const cc = localStorage.getItem('companyCode') || 'JP01'
  return {
    'Authorization': `Bearer ${token}`,
    'x-company-code': cc,
    'Content-Type': 'application/json'
  }
}

async function load() {
  loading.value = true
  try {
    const params = new URLSearchParams({
      limit: pageSize.value,
      offset: (currentPage.value - 1) * pageSize.value
    })
    if (statusFilter.value) params.set('status', statusFilter.value)
    if (contractTypeFilter.value) params.set('contractType', contractTypeFilter.value)
    if (keyword.value) params.set('keyword', keyword.value)
    if (props.initialJuchuuId) params.set('juchuuId', props.initialJuchuuId)

    const res = await fetch(`${apiBase()}/staffing/hatchuu?${params}`, { headers: getHeaders() })
    if (!res.ok) throw new Error(await res.text())
    const data = await res.json()
    rows.value = (data.data || []).map(r => ({ ...r, _generating: false }))
    total.value = data.total || 0
  } catch (e) {
    ElMessage.error(`読み込みエラー: ${e.message}`)
  } finally {
    loading.value = false
  }
}

function openNew() {
  editId.value = null
  dialogVisible.value = true
}

function onEdit(row) {
  editId.value = row.id
  initialJuchuuId.value = null
  initialJuchuuNo.value = null
  dialogVisible.value = true
}

function onSaved() {
  dialogVisible.value = false
  load()
  ElMessage.success('保存しました')
}

async function onGeneratePdf(row) {
  row._generating = true
  try {
    const res = await fetch(`${apiBase()}/staffing/hatchuu/${row.id}/generate-pdf`, {
      method: 'POST',
      headers: getHeaders()
    })
    if (!res.ok) throw new Error(await res.text())
    const data = await res.json()
    row.hasPdf = true
    pdfHtml.value = data.htmlContent
    pdfDialogVisible.value = true
    ElMessage.success('発注書を生成しました')
    load()
  } catch (e) {
    ElMessage.error(`PDF生成エラー: ${e.message}`)
  } finally {
    row._generating = false
  }
}

async function printPdf(row) {
  try {
    const res = await fetch(`${apiBase()}/staffing/hatchuu/${row.id}/doc-html`, { headers: getHeaders() })
    if (!res.ok) throw new Error(await res.text())
    const data = await res.json()
    if (data.url) window.open(data.url, '_blank')
  } catch (e) {
    ElMessage.error(`表示エラー: ${e.message}`)
  }
}

function contractTypeLabel(t) {
  return { ses: 'SES', dispatch: '派遣', contract: '請負' }[t] || t || '-'
}
function contractTypeColor(t) {
  return { ses: 'primary', dispatch: 'warning', contract: 'success' }[t] || 'info'
}
function rateTypeLabel(t) {
  return { monthly: '月', daily: '日', hourly: '時' }[t] || t || '-'
}
function statusLabel(s) {
  return { draft: '下書き', active: '有効', ended: '終了', terminated: '解約' }[s] || s || '-'
}
function statusColor(s) {
  return { draft: 'info', active: 'success', ended: '', terminated: 'danger' }[s] ?? 'info'
}

onMounted(load)
</script>

<style scoped>
.hatchuu-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.hatchuu-header__left {
  display: flex;
  align-items: center;
  gap: 8px;
}
.hatchuu-header__icon {
  font-size: 18px;
  color: #67c23a;
}
.hatchuu-header__title {
  font-size: 16px;
  font-weight: 600;
}
.hatchuu-filters {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}
.hatchuu-table {
  width: 100%;
}
.hatchuu-pagination {
  margin-top: 16px;
  display: flex;
  justify-content: flex-end;
}
.doc-no {
  font-family: monospace;
  font-weight: 600;
  color: #67c23a;
}
.juchuu-no {
  font-family: monospace;
  color: #1a56db;
  font-size: 12px;
}
.cell-main { font-weight: 500; }
.cell-sub { font-size: 11px; color: #999; }
.cell-empty { color: #ccc; }
.rate-cell { color: #e6a23c; font-weight: 600; }
.rate-cell small { font-weight: normal; color: #999; }
.pdf-preview {
  max-height: 70vh;
  overflow-y: auto;
}
</style>
