<template>
  <div class="juchuu-list">
    <el-card class="juchuu-card">
      <template #header>
        <div class="juchuu-header">
          <div class="juchuu-header__left">
            <el-icon class="juchuu-header__icon"><DocumentChecked /></el-icon>
            <span class="juchuu-header__title">受注一覧</span>
            <el-tag size="small" type="info" class="juchuu-header__count">{{ total }}件</el-tag>
          </div>
          <div class="juchuu-header__right">
            <el-button type="primary" @click="openNew">
              <el-icon><Plus /></el-icon>
              <span>新規受注</span>
            </el-button>
          </div>
        </div>
      </template>

      <!-- フィルター -->
      <div class="juchuu-filters">
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
          placeholder="受注番号・顧客名で検索"
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
        class="juchuu-table"
        v-loading="loading"
        @row-dblclick="onEdit"
      >
        <el-table-column type="index" width="48" align="center" />

        <el-table-column label="受注番号" width="130">
          <template #default="{ row }">
            <span class="doc-no">{{ row.juchuuNo }}</span>
          </template>
        </el-table-column>

        <el-table-column label="顧客" min-width="140">
          <template #default="{ row }">
            <div v-if="row.clientName" class="cell-main">{{ row.clientName }}</div>
            <span v-else class="cell-empty">-</span>
          </template>
        </el-table-column>

        <el-table-column label="要員" min-width="160">
          <template #default="{ row }">
            <div v-if="row.resourceNames" class="cell-main">{{ row.resourceNames }}</div>
            <el-tag v-else-if="row.resourceCount > 0" type="info" size="small">{{ row.resourceCount }}名</el-tag>
            <span v-else class="cell-empty">-</span>
          </template>
        </el-table-column>

        <el-table-column label="契約形態" width="110" align="center">
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

        <el-table-column label="注文書" width="80" align="center">
          <template #default="{ row }">
            <el-icon v-if="row.hasAttachedDoc" color="#52c41a" title="注文書あり"><CircleCheck /></el-icon>
            <el-icon v-else color="#d9d9d9" title="注文書なし"><CircleClose /></el-icon>
          </template>
        </el-table-column>

        <el-table-column label="ステータス" width="90" align="center">
          <template #default="{ row }">
            <el-tag :type="statusColor(row.status)" size="small">
              {{ statusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="操作" width="140" align="center" fixed="right">
          <template #default="{ row }">
            <el-button size="small" @click.stop="onEdit(row)">
              <el-icon><Edit /></el-icon>
              編集
            </el-button>
            <el-button size="small" type="success" @click.stop="onCreateHatchuu(row)" title="この受注から発注を作成">
              <el-icon><Plus /></el-icon>
              発注
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <div class="juchuu-pagination">
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

    <!-- 受注フォームダイアログ -->
    <el-dialog
      v-model="dialogVisible"
      :title="editId ? '受注編集' : '新規受注登録'"
      width="800px"
      :close-on-click-modal="false"
      destroy-on-close
      class="juchuu-dialog"
    >
      <JuchuuForm
        :juchuu-id="editId || undefined"
        @saved="onSaved"
        @cancel="dialogVisible = false"
      />
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import {
  DocumentChecked, Plus, Search, Edit,
  CircleCheck, CircleClose
} from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import api from '../../api'
import JuchuuForm from './JuchuuForm.vue'

const emit = defineEmits(['open-hatchuu-form'])

const rows = ref([])
const total = ref(0)
const loading = ref(false)
const currentPage = ref(1)
const pageSize = ref(50)

const statusFilter = ref('')
const contractTypeFilter = ref('')
const keyword = ref('')

const dialogVisible = ref(false)
const editId = ref<string | undefined>(undefined)

async function load() {
  loading.value = true
  try {
    const params: Record<string, any> = {
      limit: pageSize.value,
      offset: (currentPage.value - 1) * pageSize.value
    }
    if (statusFilter.value) params.status = statusFilter.value
    if (contractTypeFilter.value) params.contractType = contractTypeFilter.value
    if (keyword.value) params.keyword = keyword.value

    const res = await api.get('/staffing/juchuu', { params })
    rows.value = res.data.data || []
    total.value = res.data.total || 0
  } catch (e: any) {
    ElMessage.error(`読み込みエラー: ${e.message}`)
  } finally {
    loading.value = false
  }
}

function openNew() {
  editId.value = undefined
  dialogVisible.value = true
}

function onEdit(row: any) {
  editId.value = row.id
  dialogVisible.value = true
}

function onSaved() {
  dialogVisible.value = false
  load()
  ElMessage.success('保存しました')
}

function onCreateHatchuu(row: any) {
  emit('open-hatchuu-form', { juchuuId: row.id, juchuuNo: row.juchuuNo, clientName: row.clientName })
}

function contractTypeLabel(t: string) {
  return ({ ses: 'SES', dispatch: '派遣', contract: '請負' } as Record<string,string>)[t] || t || '-'
}
function contractTypeColor(t: string) {
  return ({ ses: 'primary', dispatch: 'warning', contract: 'success' } as Record<string,string>)[t] || 'info'
}
function rateTypeLabel(t: string) {
  return ({ monthly: '月', daily: '日', hourly: '時' } as Record<string,string>)[t] || t || '-'
}
function statusLabel(s: string) {
  return ({ draft: '下書き', active: '有効', ended: '終了', terminated: '解約' } as Record<string,string>)[s] || s || '-'
}
function statusColor(s: string) {
  return ({ draft: 'info', active: 'success', ended: '', terminated: 'danger' } as Record<string,string>)[s] ?? 'info'
}

onMounted(load)
</script>

<style scoped>
.juchuu-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.juchuu-header__left {
  display: flex;
  align-items: center;
  gap: 8px;
}
.juchuu-header__icon {
  font-size: 18px;
  color: #409eff;
}
.juchuu-header__title {
  font-size: 16px;
  font-weight: 600;
}
.juchuu-filters {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}
.juchuu-table {
  width: 100%;
}
.juchuu-pagination {
  margin-top: 16px;
  display: flex;
  justify-content: flex-end;
}
.doc-no {
  font-family: monospace;
  font-weight: 600;
  color: #1a56db;
}
.cell-main {
  font-weight: 500;
}
.cell-sub {
  font-size: 11px;
  color: #999;
}
.cell-empty {
  color: #ccc;
}
.rate-cell {
  color: #52c41a;
  font-weight: 600;
}
.rate-cell small {
  font-weight: normal;
  color: #999;
}
:deep(.juchuu-dialog .el-dialog__body) {
  padding: 20px 24px;
  max-height: 72vh;
  overflow-y: auto;
}
</style>
