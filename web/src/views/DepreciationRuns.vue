<template>
  <div class="depreciation-list">
    <el-card class="depreciation-card">
      <template #header>
        <div class="depreciation-header">
          <div class="depreciation-header__left">
            <el-icon class="depreciation-header__icon"><Calendar /></el-icon>
            <span class="depreciation-header__title">定期償却記帳</span>
            <el-tag size="small" type="success" class="depreciation-header__count">{{ executedCount }}件実行済</el-tag>
          </div>
          <div class="depreciation-header__right">
            <el-date-picker
              v-model="selectedYear"
              type="year"
              placeholder="年度を選択"
              format="YYYY"
              value-format="YYYY"
              class="depreciation-header__year"
              @change="load"
            />
          </div>
        </div>
      </template>
      <el-table :data="schedule" border stripe highlight-current-row class="depreciation-table" v-loading="loading">
        <el-table-column label="年月" width="140">
          <template #default="{ row }">
            {{ row.year }}年{{ row.month }}月
          </template>
        </el-table-column>
        <el-table-column label="償却資産数" width="120" align="right">
          <template #default="{ row }">
            {{ row.run ? row.run.assetCount : row.pendingAssetCount }}
          </template>
        </el-table-column>
        <el-table-column label="償却伝票番号" min-width="160">
          <template #default="{ row }">
            <el-tooltip v-if="row.run?.voucherNo" content="クリックして伝票を表示">
              <el-link type="primary" @click="openVoucherDialog(row.run.voucherId)">
                {{ row.run.voucherNo }}
              </el-link>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column label="実行日時" width="140">
          <template #default="{ row }">
            {{ row.run?.executedAt || '' }}
          </template>
        </el-table-column>
        <el-table-column label="実行者" min-width="120">
          <template #default="{ row }">
            {{ row.run?.executedBy || '' }}
          </template>
        </el-table-column>
        <el-table-column label="アクション" width="140" fixed="right">
          <template #default="{ row }">
            <!-- 已执行：显示查看和删除 -->
            <template v-if="row.run">
              <el-button size="small" type="success" circle @click="openVoucherDialog(row.run.voucherId)">
                <el-icon><View /></el-icon>
              </el-button>
              <el-button size="small" type="danger" circle @click="confirmReversal(row)">
                <el-icon><Delete /></el-icon>
              </el-button>
            </template>
            <!-- 未执行：显示执行按钮 -->
            <template v-else>
              <el-button 
                size="small" 
                type="warning" 
                circle 
                @click="confirmRun(row)"
                :disabled="row.pendingAssetCount === 0"
              >
                <el-icon><DocumentCopy /></el-icon>
              </el-button>
            </template>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 凭证详情弹窗 -->
    <el-dialog v-model="showVoucherDialog" width="auto" append-to-body destroy-on-close class="voucher-detail-dialog">
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VouchersList v-if="showVoucherDialog" ref="voucherDetailRef" class="voucher-detail-embed" :allow-edit="false" />
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, nextTick, computed } from 'vue'
import api from '../api'
import { ElMessage, ElMessageBox } from 'element-plus'
import { View, Delete, DocumentCopy, Calendar } from '@element-plus/icons-vue'
import VouchersList from './VouchersList.vue'

const schedule = ref<any[]>([])
const loading = ref(false)
const selectedYear = ref(new Date().getFullYear().toString())
const showVoucherDialog = ref(false)
const voucherDetailRef = ref<InstanceType<typeof VouchersList> | null>(null)

// 已执行的月份数
const executedCount = computed(() => {
  return schedule.value.filter(row => row.run).length
})

async function load() {
  loading.value = true
  try {
    const resp = await api.get(`/fixed-assets/depreciation-schedule?year=${selectedYear.value}`)
    schedule.value = Array.isArray(resp.data) ? resp.data : []
  } catch (e) {
    console.error('Failed to load depreciation schedule', e)
    ElMessage.error('償却スケジュールの読み込みに失敗しました')
  } finally {
    loading.value = false
  }
}

async function confirmRun(row: any) {
  try {
    await ElMessageBox.confirm(
      `${row.year}年${row.month}月の償却を実行しますか？\n対象資産数: ${row.pendingAssetCount}件`,
      '償却実行確認',
      { confirmButtonText: '実行', cancelButtonText: 'キャンセル', type: 'warning' }
    )

    loading.value = true
    const resp = await api.post('/fixed-assets/depreciation-run', {
      yearMonth: row.yearMonth,
      executedBy: '管理者' // TODO: 从用户信息获取
    })
    
    ElMessage.success(`償却を実行しました。伝票番号: ${resp.data.voucherNo}`)
    await load()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e.response?.data?.error || '償却実行に失敗しました')
    }
  } finally {
    loading.value = false
  }
}

async function confirmReversal(row: any) {
  try {
    await ElMessageBox.confirm(
      `${row.year}年${row.month}月の償却を取消しますか？\n関連する伝票も削除されます。`,
      '償却取消確認',
      { confirmButtonText: '取消', cancelButtonText: 'キャンセル', type: 'error' }
    )

    loading.value = true
    await api.delete(`/fixed-assets/depreciation-run/${row.run.id}`)
    
    ElMessage.success('償却を取消しました')
    await load()
  } catch (e: any) {
    if (e !== 'cancel') {
      ElMessage.error(e.response?.data?.error || '償却取消に失敗しました')
    }
  } finally {
    loading.value = false
  }
}

function openVoucherDialog(voucherId: string) {
  if (!voucherId) return
  showVoucherDialog.value = true
  nextTick(() => {
    voucherDetailRef.value?.applyIntent?.({
      voucherId,
      detailOnly: true
    })
  })
}

onMounted(async () => {
  await load()
})
</script>

<style scoped>
.depreciation-list {
  padding: 16px;
}

.depreciation-card {
  border-radius: 12px;
  overflow: hidden;
}

.depreciation-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.depreciation-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.depreciation-header__icon {
  font-size: 22px;
  color: #409eff;
}

.depreciation-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.depreciation-header__count {
  font-weight: 500;
}

.depreciation-header__right {
  display: flex;
  gap: 8px;
  align-items: center;
}

.depreciation-header__year {
  width: 120px;
}

/* テーブル */
.depreciation-table {
  border-radius: 8px;
  overflow: hidden;
}

.depreciation-table :deep(.el-table__header th) {
  background: #f5f7fa;
  font-weight: 600;
  color: #303133;
}

.voucher-dialog-card-wrap {
  padding: 0 16px 16px;
}

.voucher-detail-embed {
  min-width: 800px;
  max-width: 1200px;
}
</style>

