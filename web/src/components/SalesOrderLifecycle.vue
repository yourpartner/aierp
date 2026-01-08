<template>
  <div class="lifecycle-container" v-loading="loading">
    <div v-if="lifecycle" class="lifecycle-flow">
      <div 
        v-for="(stage, index) in lifecycle.stages" 
        :key="stage.stage"
        class="lifecycle-stage"
        :class="{ 
          'completed': stage.status === 'Completed',
          'in-progress': stage.status === 'InProgress',
          'skipped': stage.status === 'Skipped',
          'not-started': stage.status === 'NotStarted'
        }"
      >
        <div class="stage-icon">
          <el-icon v-if="stage.status === 'Completed'" class="icon-check"><CircleCheck /></el-icon>
          <el-icon v-else-if="stage.status === 'InProgress'" class="icon-loading"><Loading /></el-icon>
          <el-icon v-else-if="stage.status === 'Skipped'" class="icon-skip"><Remove /></el-icon>
          <span v-else class="stage-number">{{ index + 1 }}</span>
        </div>
        <div class="stage-content">
          <div class="stage-name">
            {{ stage.stageNameJp }}
            <span v-if="stage.isOptional" class="optional-badge">任意</span>
          </div>
          <div class="stage-status">{{ stage.statusLabel }}</div>
          <div v-if="stage.documentNo" class="stage-doc">
            <el-link type="primary" size="small" @click="openDocument(stage)">{{ stage.documentNo }}</el-link>
          </div>
          <div v-if="stage.completedAt" class="stage-date">
            {{ formatDate(stage.completedAt) }}
          </div>
        </div>
        <div v-if="index < lifecycle.stages.length - 1" class="stage-connector">
          <el-icon><ArrowRight /></el-icon>
        </div>
      </div>
    </div>
    
    <div v-if="lifecycle" class="lifecycle-summary">
      <el-progress 
        :percentage="lifecycle.progressPercent" 
        :stroke-width="8"
        :color="progressColor"
      >
        <template #default>
          <span class="progress-text">{{ lifecycle.completedStages }}/{{ lifecycle.totalStages }}</span>
        </template>
      </el-progress>
    </div>

    <div v-if="!lifecycle && !loading" class="no-data">
      データがありません
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue'
import { CircleCheck, Loading, Remove, ArrowRight } from '@element-plus/icons-vue'
import api from '../api'

const props = defineProps<{
  soNo: string
  autoLoad?: boolean
}>()

const emit = defineEmits<{
  (e: 'document-click', stage: any): void
}>()

interface StageInfo {
  stage: string
  stageName: string
  stageNameJp: string
  status: string
  statusLabel: string
  completedAt: string | null
  documentNo: string | null
  isOptional: boolean
}

interface LifecycleInfo {
  soNo: string
  customerCode: string
  customerName: string
  amountTotal: number
  status: string
  completedStages: number
  totalStages: number
  progressPercent: number
  stages: StageInfo[]
}

const loading = ref(false)
const lifecycle = ref<LifecycleInfo | null>(null)

const progressColor = computed(() => {
  if (!lifecycle.value) return '#909399'
  const pct = lifecycle.value.progressPercent
  if (pct >= 100) return '#67c23a'
  if (pct >= 60) return '#409eff'
  if (pct >= 30) return '#e6a23c'
  return '#909399'
})

async function load() {
  if (!props.soNo) return
  loading.value = true
  try {
    const res = await api.get(`/sales-orders/${props.soNo}/lifecycle`)
    lifecycle.value = res.data
  } catch (e) {
    console.error('Failed to load lifecycle:', e)
    lifecycle.value = null
  } finally {
    loading.value = false
  }
}

function formatDate(dateStr: string | null) {
  if (!dateStr) return ''
  const date = new Date(dateStr)
  return date.toLocaleDateString('ja-JP', { year: 'numeric', month: '2-digit', day: '2-digit' })
}

function openDocument(stage: StageInfo) {
  emit('document-click', stage)
}

watch(() => props.soNo, () => {
  if (props.autoLoad !== false) {
    load()
  }
}, { immediate: true })

onMounted(() => {
  if (props.autoLoad !== false && props.soNo) {
    load()
  }
})

defineExpose({ load })
</script>

<style scoped>
.lifecycle-container {
  padding: 16px;
}

.lifecycle-flow {
  display: flex;
  align-items: flex-start;
  overflow-x: auto;
  padding: 12px 0;
}

.lifecycle-stage {
  display: flex;
  flex-direction: column;
  align-items: center;
  min-width: 100px;
  position: relative;
}

.stage-icon {
  width: 40px;
  height: 40px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: 600;
  font-size: 16px;
  margin-bottom: 8px;
  transition: all 0.3s ease;
}

.lifecycle-stage.completed .stage-icon {
  background: linear-gradient(135deg, #67c23a, #52b348);
  color: white;
  box-shadow: 0 2px 8px rgba(103, 194, 58, 0.4);
}

.lifecycle-stage.in-progress .stage-icon {
  background: linear-gradient(135deg, #409eff, #337ecc);
  color: white;
  box-shadow: 0 2px 8px rgba(64, 158, 255, 0.4);
  animation: pulse 2s infinite;
}

.lifecycle-stage.skipped .stage-icon {
  background: #e4e7ed;
  color: #909399;
}

.lifecycle-stage.not-started .stage-icon {
  background: #f5f7fa;
  color: #c0c4cc;
  border: 2px dashed #dcdfe6;
}

@keyframes pulse {
  0% { transform: scale(1); }
  50% { transform: scale(1.05); }
  100% { transform: scale(1); }
}

.icon-check,
.icon-loading,
.icon-skip {
  font-size: 20px;
}

.stage-number {
  font-size: 14px;
}

.stage-content {
  text-align: center;
}

.stage-name {
  font-size: 13px;
  font-weight: 600;
  color: #303133;
  margin-bottom: 4px;
}

.optional-badge {
  font-size: 10px;
  padding: 1px 4px;
  background: #f0f0f0;
  color: #909399;
  border-radius: 2px;
  margin-left: 4px;
  font-weight: 400;
}

.stage-status {
  font-size: 11px;
  color: #909399;
}

.lifecycle-stage.completed .stage-status {
  color: #67c23a;
}

.lifecycle-stage.in-progress .stage-status {
  color: #409eff;
}

.stage-doc {
  margin-top: 4px;
  font-size: 11px;
}

.stage-date {
  font-size: 10px;
  color: #c0c4cc;
  margin-top: 2px;
}

.stage-connector {
  position: absolute;
  right: -24px;
  top: 18px;
  color: #dcdfe6;
  font-size: 18px;
}

.lifecycle-stage.completed .stage-connector,
.lifecycle-stage.in-progress .stage-connector {
  color: #67c23a;
}

.lifecycle-summary {
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.progress-text {
  font-size: 12px;
  color: #606266;
}

.no-data {
  text-align: center;
  padding: 20px;
  color: #909399;
}
</style>

