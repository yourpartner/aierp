<template>
  <div class="cash-flow-container">
    <div class="page-header">
      <div class="title-area">
        <h2>資金繰り</h2>
        <el-tooltip content="資金繰りの状況を確認します" placement="right">
          <el-icon class="info-icon"><InfoFilled /></el-icon>
        </el-tooltip>
      </div>
      <div class="action-area">
        <el-date-picker
          v-model="dateRange"
          type="daterange"
          range-separator="〜"
          start-placeholder="開始日"
          end-placeholder="終了日"
          value-format="YYYY-MM-DD"
          size="small"
          class="date-picker"
        />
      </div>
    </div>

    <div class="chart-section">
      <div class="chart-legend">
        <div class="legend-item">
          <span class="color-box in"></span>
          <span>入金金額</span>
        </div>
        <div class="legend-item">
          <span class="color-box out"></span>
          <span>出金金額</span>
        </div>
        <div class="legend-item">
          <span class="line-box"></span>
          <span>資金残高</span>
        </div>
      </div>
      <div class="chart-wrapper">
        <!-- 模拟 ECharts 图表，这里用 CSS 画一个类似的示意图 -->
        <div class="mock-chart">
          <div class="y-axis">
            <span>400,000,000</span>
            <span>300,000,000</span>
            <span>200,000,000</span>
            <span>100,000,000</span>
            <span>0</span>
            <span>-100,000,000</span>
          </div>
          <div class="chart-content">
            <!-- 0 轴线 -->
            <div class="zero-line"></div>
            <!-- 折线 (资金残高) -->
            <svg class="line-svg" viewBox="0 0 800 300" preserveAspectRatio="none">
              <polyline points="0,80 100,85 200,95 300,70 400,75 500,50 600,45 700,50 800,20" fill="none" stroke="#ff6b6b" stroke-width="2" />
              <circle cx="0" cy="80" r="3" fill="#fff" stroke="#ff6b6b" stroke-width="2" />
              <circle cx="100" cy="85" r="3" fill="#fff" stroke="#ff6b6b" stroke-width="2" />
              <circle cx="200" cy="95" r="3" fill="#fff" stroke="#ff6b6b" stroke-width="2" />
              <circle cx="300" cy="70" r="3" fill="#fff" stroke="#ff6b6b" stroke-width="2" />
              <circle cx="400" cy="75" r="3" fill="#fff" stroke="#ff6b6b" stroke-width="2" />
              <circle cx="500" cy="50" r="3" fill="#fff" stroke="#ff6b6b" stroke-width="2" />
              <circle cx="600" cy="45" r="3" fill="#fff" stroke="#ff6b6b" stroke-width="2" />
              <circle cx="700" cy="50" r="3" fill="#fff" stroke="#ff6b6b" stroke-width="2" />
              <circle cx="800" cy="20" r="3" fill="#fff" stroke="#ff6b6b" stroke-width="2" />
            </svg>
            
            <!-- 柱状图 (入金/出金) -->
            <div class="bars">
              <div class="bar-group" style="left: 10%">
                <div class="bar out" style="height: 10px; bottom: -10px;"></div>
              </div>
              <div class="bar-group" style="left: 25%">
                <div class="bar in" style="height: 15px; bottom: 0;"></div>
              </div>
              <div class="bar-group" style="left: 35%">
                <div class="bar in" style="height: 30px; bottom: 0;"></div>
              </div>
              <div class="bar-group" style="left: 50%">
                <div class="bar out" style="height: 5px; bottom: -5px;"></div>
              </div>
              <div class="bar-group" style="left: 60%">
                <div class="bar in" style="height: 60px; bottom: 0;"></div>
                <div class="bar out" style="height: 15px; bottom: -15px;"></div>
              </div>
              <div class="bar-group" style="left: 75%">
                <div class="bar in" style="height: 30px; bottom: 0;"></div>
                <div class="bar out" style="height: 10px; bottom: -10px;"></div>
              </div>
              <div class="bar-group" style="left: 95%">
                <div class="bar in" style="height: 45px; bottom: 0;"></div>
              </div>
            </div>

            <div class="x-axis">
              <span>2023/11/2</span>
              <span>2023/11/10</span>
              <span>2023/11/20</span>
              <span>2023/11/25</span>
              <span>2023/12/5</span>
              <span>2023/12/15</span>
              <span>2023/12/25</span>
              <span>2023/12/31</span>
            </div>
          </div>
        </div>
      </div>
    </div>

    <div class="table-section">
      <div class="table-header">
        <h3>2023/11/30 入出金明細</h3>
      </div>
      <el-table :data="tableData" style="width: 100%" size="small" border>
        <el-table-column prop="type" label="入出金種別" width="200">
          <template #default="{ row }">
            <span :class="row.isIncome ? 'text-danger' : 'text-success'">{{ row.type }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="amount" label="金額" width="150" align="right">
          <template #default="{ row }">
            <span :class="row.isIncome ? 'text-danger' : 'text-success'">{{ formatAmount(row.amount) }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="partner" label="入出金相手" width="250">
          <template #default="{ row }">
            <span :class="row.isIncome ? 'text-danger' : 'text-success'">{{ row.partner }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="voucherNo" label="入出金伝票" width="150">
          <template #header>
            入出金伝票 <el-icon class="info-icon"><InfoFilled /></el-icon>
          </template>
          <template #default="{ row }">
            <a href="#" class="link-text" :class="row.isIncome ? 'text-danger' : 'text-success'">{{ row.voucherNo }}</a>
          </template>
        </el-table-column>
        <el-table-column prop="note" label="備考">
          <template #default="{ row }">
            <span :class="row.isIncome ? 'text-danger' : 'text-success'">{{ row.note }}</span>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { InfoFilled } from '@element-plus/icons-vue'

const dateRange = ref(['2023-11-01', '2023-12-31'])

const formatAmount = (amount: number) => {
  return amount.toLocaleString()
}

// 假数据
const tableData = ref([
  {
    type: '受注より入金予定（請求済）',
    amount: 825000,
    partner: '得意先 A社',
    voucherNo: '2309000115/1',
    note: '得意先 A社 2023年09月請求書YP2309-C136-001',
    isIncome: true
  },
  {
    type: 'その他入金',
    amount: 380160,
    partner: '得意先 Bホールディングス(株)',
    voucherNo: '2309000123/1',
    note: '得意先 Bホールディングス(株) 2023年09月請求書YP2309-C138-002',
    isIncome: true
  },
  {
    type: '発注より出金予定（請求済）',
    amount: 638000,
    partner: '仕入先 Cソフト',
    voucherNo: '2309000275/3',
    note: '仕入先 Cソフト 2023年09月請求書YP2309-V114-001',
    isIncome: false
  },
  {
    type: '発注より出金予定（請求済）',
    amount: 880000,
    partner: '仕入先 Cソフト',
    voucherNo: '2309000275/6',
    note: '仕入先 Cソフト 2023年09月請求書YP2309-V114-001',
    isIncome: false
  },
  {
    type: '発注より出金予定（請求済）',
    amount: 55000,
    partner: '仕入先 Cソフト',
    voucherNo: '2309000275/9',
    note: '仕入先 Cソフト 2023年09月請求書YP2309-V114-001',
    isIncome: false
  },
  {
    type: '受注より入金予定（請求済）',
    amount: 66000,
    partner: '得意先 D社',
    voucherNo: '2309000281/1',
    note: '得意先 D社 2023年09月請求書YP2309-C136-002',
    isIncome: true
  },
  {
    type: '受注より入金予定（請求済）',
    amount: 880000,
    partner: '得意先 E社',
    voucherNo: '2310000036/1',
    note: '得意先 E社 2023年10月請求書YP2310-C132-001',
    isIncome: true
  },
  {
    type: '受注より入金予定（請求済）',
    amount: 1320000,
    partner: '得意先 E社',
    voucherNo: '2310000036/4',
    note: '得意先 E社 2023年10月請求書YP2310-C132-001',
    isIncome: true
  },
  {
    type: '受注より入金予定（請求済）',
    amount: 935000,
    partner: '得意先 F社',
    voucherNo: '2310000038/1',
    note: '得意先 F社 2023年10月請求書YP2310-C140-001',
    isIncome: true
  }
])
</script>

<style scoped>
.cash-flow-container {
  padding: 20px;
  background-color: #fff;
  min-height: 100%;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
  padding-bottom: 10px;
  border-bottom: 1px solid #ebeef5;
}

.title-area {
  display: flex;
  align-items: center;
  gap: 8px;
}

.title-area h2 {
  margin: 0;
  font-size: 18px;
  color: #303133;
  font-weight: 500;
}

.info-icon {
  color: #409eff;
  cursor: pointer;
}

.chart-section {
  margin-bottom: 30px;
}

.chart-legend {
  display: flex;
  justify-content: center;
  gap: 20px;
  margin-bottom: 10px;
}

.legend-item {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: #606266;
}

.color-box {
  width: 24px;
  height: 12px;
  border-radius: 2px;
}

.color-box.in {
  background-color: #fca5a5;
}

.color-box.out {
  background-color: #b2f2bb;
}

.line-box {
  width: 24px;
  height: 2px;
  background-color: #ff6b6b;
  position: relative;
}

.line-box::after {
  content: '';
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  width: 6px;
  height: 6px;
  border-radius: 50%;
  border: 1px solid #ff6b6b;
  background-color: #fff;
}

.chart-wrapper {
  height: 300px;
  position: relative;
  padding-left: 80px;
  padding-bottom: 30px;
}

.mock-chart {
  width: 100%;
  height: 100%;
  position: relative;
}

.y-axis {
  position: absolute;
  left: -80px;
  top: 0;
  bottom: 0;
  width: 70px;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  align-items: flex-end;
  font-size: 11px;
  color: #606266;
  padding-bottom: 30px; /* 留出 x 轴空间 */
}

.chart-content {
  position: absolute;
  left: 0;
  right: 0;
  top: 0;
  bottom: 0;
  border-left: 1px solid #dcdfe6;
  border-bottom: 1px solid #dcdfe6;
}

.zero-line {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 20%; /* 假设 0 在 20% 的位置 */
  height: 1px;
  background-color: #909399;
  z-index: 1;
}

.line-svg {
  position: absolute;
  left: 0;
  top: 0;
  width: 100%;
  height: 100%;
  z-index: 3;
}

.bars {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 20%; /* 对齐 zero-line */
  height: 80%;
  z-index: 2;
}

.bar-group {
  position: absolute;
  width: 30px;
  transform: translateX(-50%);
}

.bar {
  position: absolute;
  width: 100%;
  opacity: 0.8;
}

.bar.in {
  background-color: #fca5a5;
}

.bar.out {
  background-color: #b2f2bb;
}

.x-axis {
  position: absolute;
  left: 0;
  right: 0;
  bottom: -25px;
  display: flex;
  justify-content: space-between;
  font-size: 11px;
  color: #606266;
}

.table-section {
  margin-top: 20px;
}

.table-header {
  margin-bottom: 10px;
}

.table-header h3 {
  margin: 0;
  font-size: 14px;
  color: #606266;
  font-weight: 500;
}

.text-danger {
  color: #f56c6c;
}

.text-success {
  color: #67c23a;
}

.link-text {
  text-decoration: none;
}

.link-text:hover {
  text-decoration: underline;
}

:deep(.el-table th.el-table__cell) {
  background-color: #f5f7fa;
  color: #606266;
  font-weight: 500;
}
</style>
