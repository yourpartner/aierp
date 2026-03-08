<template>
  <div class="page">
    <el-card>
      <template #header>
        <div class="page-header">
          <div class="page-header-left">
            <el-icon class="page-header-icon"><Money /></el-icon>
            <span class="page-header-title">経費精算一覧</span>
          </div>
          <div class="page-actions">
            <el-date-picker
              v-model="month"
              type="month"
              placeholder="対象月"
              value-format="YYYY-MM"
              style="width: 140px"
            />
            <el-select v-model="status" placeholder="ステータス" style="width: 130px" clearable>
              <el-option label="承認済" value="approved" />
              <el-option label="支払済" value="paid" />
            </el-select>
          </div>
        </div>
      </template>

      <el-table :data="tableData" style="width: 100%" border stripe>
        <el-table-column prop="month" label="年月" width="100" />
        <el-table-column prop="empCode" label="社員番号" width="120" />
        <el-table-column prop="empName" label="氏名" width="150" />
        <el-table-column prop="amount" label="精算金額" width="120" align="right">
          <template #default="{ row }">
            {{ row.amount.toLocaleString() }}
          </template>
        </el-table-column>
        <el-table-column prop="expectedDate" label="支払予定日" width="120" />
        <el-table-column prop="actualDate" label="実際支払日" width="120">
          <template #default="{ row }">
            {{ row.actualDate || '-' }}
          </template>
        </el-table-column>
        <el-table-column prop="status" label="承認ステータス" width="130" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.status === '支払済'" type="success" size="small">支払済</el-tag>
            <el-tag v-else size="small">承認済</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="voucherNo" label="会計伝票" width="150">
          <template #default="{ row }">
            <span class="voucher-link">{{ row.voucherNo }}</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" align="center" width="100">
          <template #default="{ row }">
            <el-button type="primary" text size="small">
              <el-icon><View /></el-icon>
            </el-button>
            <el-button v-if="row.status === '承認済'" type="warning" text size="small">
              <el-icon><RefreshLeft /></el-icon>
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { Money, View, RefreshLeft } from '@element-plus/icons-vue'

const month = ref('2023-09')
const status = ref('')

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
.page {
  padding: 20px;
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.page-header-left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.page-header-icon {
  font-size: 22px;
  color: #e6a23c;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.page-actions {
  display: flex;
  gap: 8px;
  align-items: center;
}

.voucher-link {
  color: #409eff;
  cursor: pointer;
}

.voucher-link:hover {
  text-decoration: underline;
}

:deep(.el-card) {
  border-radius: 12px;
  overflow: hidden;
}
</style>
