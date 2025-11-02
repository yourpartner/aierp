<template>
  <div class="scheduler-page">
    <el-row :gutter="12">
      <el-col :span="16" :xs="24">
        <el-card shadow="never">
          <SchemaList ref="listRef" entity="scheduler_task" :title="title" @row-click="onRowClick" />
        </el-card>
      </el-col>
      <el-col :span="8" :xs="24">
        <el-card shadow="never">
          <template #header>
            <div class="actions-header">
              <span>任务操作</span>
              <el-button size="small" :loading="loading" @click="refresh">刷新</el-button>
            </div>
          </template>
          <div v-if="current">
            <el-descriptions :column="1" size="small" border>
              <el-descriptions-item label="状态">
                <el-tag :type="statusTag(current.status)">{{ statusLabel(current.status) }}</el-tag>
              </el-descriptions-item>
              <el-descriptions-item label="自然语言">{{ displayNl(current) }}</el-descriptions-item>
              <el-descriptions-item label="下一次运行">{{ formatTime(current.next_run_at) }}</el-descriptions-item>
              <el-descriptions-item label="最近执行">{{ formatTime(current.last_run_at) }}</el-descriptions-item>
            </el-descriptions>
            <el-space style="margin-top:12px" wrap>
              <el-button type="primary" size="small" :disabled="current.status!=='waiting_review'" :loading="actionLoading" @click="confirm">确认完成</el-button>
              <el-button type="warning" size="small" :loading="actionLoading" @click="retry">重试调度</el-button>
            </el-space>
            <div class="json-block" v-if="current.result">
              <div class="json-title">执行结果</div>
              <pre>{{ stringify(current.result) }}</pre>
            </div>
          </div>
          <div v-else class="empty">
            请选择左侧列表中的任务以执行操作。
          </div>
        </el-card>
      </el-col>
    </el-row>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { ElMessage } from 'element-plus'
import SchemaList from '../components/SchemaList.vue'
import api from '../api'
import { useI18n } from '../i18n'

const listRef = ref<{ reload: () => Promise<void>; rows: any[] } | null>(null)
const current = ref<any | null>(null)
const actionLoading = ref(false)
const loading = ref(false)
const { text } = useI18n()
const title = text.value?.nav?.schedulerTasks ?? '调度任务'

function onRowClick(row: any) {
  current.value = row
}

async function refresh() {
  loading.value = true
  try {
    current.value = null
    await listRef.value?.reload()
  } catch (error: any) {
    ElMessage.error(error?.response?.data?.error || '刷新失败')
  } finally {
    loading.value = false
  }
}

function displayNl(row: any) {
  if (!row) return '-'
  if (row.payload && row.payload.nlSpec) return row.payload.nlSpec
  if (row.nl_spec) return row.nl_spec
  if (row.nlspec) return row.nlspec
  return '-'
}

function statusLabel(status: string) {
  switch (status) {
    case 'pending': return '待执行'
    case 'running': return '执行中'
    case 'waiting_review': return '待审核'
    case 'failed': return '失败'
    default: return status || '未知'
  }
}

function statusTag(status: string) {
  switch (status) {
    case 'pending': return 'info'
    case 'running': return 'warning'
    case 'waiting_review': return 'success'
    case 'failed': return 'danger'
    default: return 'info'
  }
}

function formatTime(value?: string | null) {
  if (!value) return '-'
  try {
    const dt = new Date(value)
    if (Number.isNaN(dt.getTime())) return value
    return `${dt.getFullYear()}-${String(dt.getMonth() + 1).padStart(2, '0')}-${String(dt.getDate()).padStart(2, '0')} ${String(dt.getHours()).padStart(2, '0')}:${String(dt.getMinutes()).padStart(2, '0')}`
  } catch {
    return value
  }
}

function stringify(obj: any) {
  try { return JSON.stringify(obj, null, 2) } catch { return String(obj ?? '') }
}

async function confirm() {
  if (!current.value) return
  actionLoading.value = true
  try {
    await api.post(`/operations/tasks/${current.value.id}/confirm`, {})
    ElMessage.success('已确认任务结果')
    await refresh()
  } catch (error: any) {
    ElMessage.error(error?.response?.data?.error || '确认失败')
  } finally {
    actionLoading.value = false
  }
}

async function retry() {
  if (!current.value) return
  actionLoading.value = true
  try {
    await api.post(`/operations/tasks/${current.value.id}/retry`, {})
    ElMessage.success('已触发重试')
    await refresh()
  } catch (error: any) {
    ElMessage.error(error?.response?.data?.error || '重试失败')
  } finally {
    actionLoading.value = false
  }
}
</script>

<style scoped>
.scheduler-page {
  padding: 8px;
}
.actions-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.json-block {
  margin-top: 16px;
}
.json-title {
  font-weight: 600;
  margin-bottom: 4px;
}
.json-block pre {
  background: #f7f7f7;
  padding: 8px;
  border-radius: 4px;
  max-height: 240px;
  overflow: auto;
}
.empty {
  color: #6b7280;
  font-size: 13px;
}
</style>

