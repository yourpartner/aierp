<template>
  <div class="sales-chart-message">
    <div class="chart-title" v-if="chartTitle">{{ chartTitle }}</div>
    <div class="chart-explanation" v-if="explanation">{{ explanation }}</div>
    <div v-if="hasData" class="chart-container" ref="chartRef" :style="{ height: chartHeight + 'px' }"></div>
    <div v-else class="no-data-container" :style="{ height: chartHeight + 'px' }">
      <div class="no-data-message">
        <el-icon :size="48"><Warning /></el-icon>
        <p>該当期間のデータがありません</p>
        <p class="no-data-hint">別の期間や条件で検索してみてください</p>
      </div>
    </div>
    <div class="chart-data-toggle" v-if="showDataTable">
      <el-collapse>
        <el-collapse-item :title="dataTableTitle">
          <el-table :data="tableData" stripe size="small" max-height="300">
            <el-table-column 
              v-for="col in tableColumns" 
              :key="col" 
              :prop="col" 
              :label="col"
              min-width="100"
            />
          </el-table>
        </el-collapse-item>
      </el-collapse>
    </div>
    <div class="chart-sql-toggle" v-if="sql">
      <el-collapse>
        <el-collapse-item :title="sqlTitle">
          <pre class="sql-code">{{ sql }}</pre>
        </el-collapse-item>
      </el-collapse>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount, watch, computed } from 'vue'
import * as echarts from 'echarts'
import { Warning } from '@element-plus/icons-vue'

const props = defineProps<{
  echartsConfig: any
  chartTitle?: string
  explanation?: string
  data?: any[]
  sql?: string
  chartHeight?: number
}>()

const chartRef = ref<HTMLElement | null>(null)
let chartInstance: echarts.ECharts | null = null

const dataTableTitle = '数据详情'
const sqlTitle = '执行的SQL'

const hasData = computed(() => {
  return props.data && props.data.length > 0
})

const showDataTable = computed(() => {
  return props.data && props.data.length > 0
})

const tableData = computed(() => props.data || [])

const tableColumns = computed(() => {
  if (!props.data || props.data.length === 0) return []
  return Object.keys(props.data[0])
})

const initChart = () => {
  if (!chartRef.value || !hasData.value) return
  
  // 销毁旧实例
  if (chartInstance) {
    chartInstance.dispose()
  }
  
  chartInstance = echarts.init(chartRef.value)
  
  if (props.echartsConfig) {
    chartInstance.setOption(props.echartsConfig)
  }
}

const handleResize = () => {
  chartInstance?.resize()
}

onMounted(() => {
  initChart()
  window.addEventListener('resize', handleResize)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', handleResize)
  chartInstance?.dispose()
})

watch(() => props.echartsConfig, () => {
  initChart()
}, { deep: true })
</script>

<style scoped>
.sales-chart-message {
  padding: 12px;
  background: #f8f9fa;
  border-radius: 8px;
  margin: 8px 0;
}

.chart-title {
  font-size: 14px;
  font-weight: 600;
  color: #303133;
  margin-bottom: 8px;
}

.chart-explanation {
  font-size: 13px;
  color: #606266;
  margin-bottom: 12px;
  line-height: 1.5;
}

.chart-container {
  min-height: 300px;
  background: white;
  border-radius: 4px;
  border: 1px solid #ebeef5;
}

.no-data-container {
  min-height: 300px;
  background: white;
  border-radius: 4px;
  border: 1px solid #ebeef5;
  display: flex;
  align-items: center;
  justify-content: center;
}

.no-data-message {
  text-align: center;
  color: #909399;
}

.no-data-message .el-icon {
  color: #c0c4cc;
  margin-bottom: 12px;
}

.no-data-message p {
  margin: 0;
  font-size: 14px;
}

.no-data-message .no-data-hint {
  font-size: 12px;
  margin-top: 8px;
  color: #c0c4cc;
}

.chart-data-toggle,
.chart-sql-toggle {
  margin-top: 12px;
}

.chart-data-toggle :deep(.el-collapse-item__header),
.chart-sql-toggle :deep(.el-collapse-item__header) {
  font-size: 12px;
  color: #909399;
  height: 32px;
  line-height: 32px;
}

.sql-code {
  font-family: 'Monaco', 'Menlo', 'Ubuntu Mono', monospace;
  font-size: 12px;
  background: #1e1e1e;
  color: #d4d4d4;
  padding: 12px;
  border-radius: 4px;
  overflow-x: auto;
  white-space: pre-wrap;
  word-break: break-all;
  margin: 0;
}
</style>

