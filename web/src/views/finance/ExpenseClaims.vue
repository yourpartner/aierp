<template>
  <div class="expense-claims-container">
    <div class="page-header">
      <div class="title-area">
        <h2>経費精算一覧</h2>
        <el-tooltip content="経費精算の状況を確認します" placement="right">
          <el-icon class="info-icon"><InfoFilled /></el-icon>
        </el-tooltip>
      </div>
      <div class="action-area">
        <el-date-picker
          v-model="month"
          type="month"
          placeholder="対象月"
          size="small"
          class="month-picker"
        />
        <el-select v-model="status" placeholder="ステータス" size="small" class="filter-select" clearable>
          <el-option label="承認済" value="approved" />
          <el-option label="支払済" value="paid" />
        </el-select>
      </div>
    </div>

    <div class="table-section">
      <el-table :data="tableData" style="width: 100%" size="small" border stripe>
        <el-table-column prop="month" label="年月" width="100" />
        <el-table-column prop="empCode" label="社員番号" width="120" />
        <el-table-column prop="empName" label="氏名" width="150" />
        <el-table-column prop="amount" label="精算金額" width="120" align="right">
          <template #default="{ row }">
            {{ formatAmount(row.amount) }}
          </template>
        </el-table-column>
        <el-table-column prop="expectedDate" label="支払予定日" width="120" />
        <el-table-column prop="actualDate" label="実際支払日" width="120">
          <template #default="{ row }">
            {{ row.actualDate || '-' }}
          </template>
        </el-table-column>
        <el-table-column prop="status" label="承認ステータス" width="120" align="center">
          <template #default="{ row }">
            <div :class="['status-badge', row.status === '支払済' ? 'paid' : 'approved']">
              {{ row.status }}
            </div>
          </template>
        </el-table-column>
        <el-table-column prop="voucherNo" label="会計伝票" width="150">
          <template #header>
            会計伝票 <el-icon class="info-icon"><InfoFilled /></el-icon>
          </template>
          <template #default="{ row }">
            <a href="#" class="link-text text-danger">{{ row.voucherNo }}</a>
          </template>
        </el-table-column>
        <el-table-column label="アクション" align="center" width="100">
          <template #default="{ row }">
            <div class="action-icons">
              <el-icon class="action-icon view"><View /></el-icon>
              <el-icon v-if="row.status === '承認済'" class="action-icon revert"><RefreshLeft /></el-icon>
            </div>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { InfoFilled, View, RefreshLeft } from '@element-plus/icons-vue'

const month = ref(new Date('2023-09-01'))
const status = ref('')

const formatAmount = (amount: number) => {
  return amount.toLocaleString()
}

// 假数据
const tableData = ref([
  { month: '2023-9', empCode: 'YP232', empName: '山田 太郎', amount: 14270, expectedDate: '2023-10-25', actualDate: '', status: '承認済', voucherNo: '2309000003' },
  { month: '2023-9', empCode: 'YP231', empName: '鈴木 一郎', amount: 18450, expectedDate: '2023-10-25', actualDate: '2023-10-23', status: '支払済', voucherNo: '2309000002' },
  { month: '2023-9', empCode: 'YP194', empName: '佐藤 花子', amount: 4757, expectedDate: '2023-10-25', actualDate: '2023-10-23', status: '支払済', voucherNo: '2309000011' },
  { month: '2023-9', empCode: 'YP269', empName: '田中 次郎', amount: 7320, expectedDate: '2023-10-25', actualDate: '2023-10-23', status: '支払済', voucherNo: '2309000010' },
  { month: '2023-9', empCode: 'YP257', empName: '伊藤 三郎', amount: 16920, expectedDate: '2023-10-25', actualDate: '2023-10-23', status: '支払済', voucherNo: '2309000021' },
  { month: '2023-9', empCode: 'YP259', empName: '渡辺 四郎', amount: 5620, expectedDate: '2023-10-25', actualDate: '2023-10-23', status: '支払済', voucherNo: '2309000020' },
  { month: '2023-9', empCode: 'YP226', empName: '小林 五郎', amount: 18600, expectedDate: '2023-10-25', actualDate: '2023-10-23', status: '支払済', voucherNo: '2309000042' },
  { month: '2023-9', empCode: 'YP119', empName: '加藤 六郎', amount: 9360, expectedDate: '2023-10-25', actualDate: '2023-10-23', status: '支払済', voucherNo: '2309000041' },
  { month: '2023-9', empCode: 'YP211', empName: '吉田 七郎', amount: 9450, expectedDate: '2023-10-25', actualDate: '2023-10-23', status: '支払済', voucherNo: '2309000040' },
  { month: '2023-9', empCode: 'YP290', empName: '山田 八郎', amount: 6950, expectedDate: '2023-10-25', actualDate: '2023-10-23', status: '支払済', voucherNo: '2309000039' },
  { month: '2023-9', empCode: 'YP102', empName: '佐々木 九郎', amount: 13270, expectedDate: '2023-10-25', actualDate: '2023-10-23', status: '支払済', voucherNo: '2309000038' }
])
</script>

<style scoped>
.expense-claims-container {
  padding: 20px;
  background-color: #fff;
  min-height: 100%;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 24px;
  padding-bottom: 12px;
  border-bottom: 1px solid #ebeef5;
  background-color: #f8f9fa;
  padding: 12px 16px;
  border-radius: 4px 4px 0 0;
}

.title-area {
  display: flex;
  align-items: center;
  gap: 8px;
}

.title-area h2 {
  margin: 0;
  font-size: 16px;
  color: #303133;
  font-weight: 500;
}

.info-icon {
  color: #409eff;
  cursor: pointer;
}

.action-area {
  display: flex;
  gap: 12px;
  align-items: center;
}

.month-picker {
  width: 120px !important;
}

.filter-select {
  width: 120px;
}

.table-section {
  margin-top: 10px;
}

.status-badge {
  display: inline-block;
  padding: 4px 12px;
  border-radius: 2px;
  font-size: 12px;
  width: 100%;
  box-sizing: border-box;
}

.status-badge.approved {
  background-color: transparent;
  color: #606266;
}

.status-badge.paid {
  background-color: #67c23a;
  color: #fff;
}

.link-text {
  text-decoration: none;
}

.link-text:hover {
  text-decoration: underline;
}

.text-danger {
  color: #f56c6c;
}

.action-icons {
  display: flex;
  justify-content: center;
  gap: 12px;
}

.action-icon {
  cursor: pointer;
  font-size: 16px;
}

.action-icon.view {
  color: #67c23a;
}

.action-icon.revert {
  color: #f56c6c;
}

:deep(.el-table th.el-table__cell) {
  background-color: #f5f7fa;
  color: #606266;
  font-weight: 500;
}
</style>
