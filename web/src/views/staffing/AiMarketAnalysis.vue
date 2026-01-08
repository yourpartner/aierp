<template>
  <div class="ai-market">
    <div class="page-header">
      <div class="header-left">
        <el-icon class="header-icon"><TrendCharts /></el-icon>
        <h1>市場分析 & 単価アドバイス</h1>
      </div>
    </div>

    <el-row :gutter="20">
      <!-- 入力 -->
      <el-col :span="8">
        <el-card>
          <template #header>
            <span class="card-title">分析条件</span>
          </template>
          <el-form label-position="top">
            <el-form-item label="スキル">
              <el-select 
                v-model="query.skills" 
                multiple 
                filterable 
                allow-create
                placeholder="スキルを選択"
                style="width: 100%"
              >
                <el-option v-for="s in commonSkills" :key="s" :label="s" :value="s" />
              </el-select>
            </el-form-item>
            <el-form-item label="経験年数">
              <el-slider v-model="query.experienceYears" :min="0" :max="20" :marks="experienceMarks" />
            </el-form-item>
            <el-button type="primary" @click="analyze" :loading="loading" style="width: 100%">
              <el-icon><DataAnalysis /></el-icon>
              分析実行
            </el-button>
          </el-form>
        </el-card>
      </el-col>

      <!-- 結果 -->
      <el-col :span="16">
        <el-card v-loading="loading">
          <template #header>
            <div class="card-header">
              <span class="card-title">分析結果</span>
              <el-tag v-if="result" size="small">サンプル数: {{ result.sampleSize }}件</el-tag>
            </div>
          </template>

          <div v-if="result" class="result-content">
            <!-- 価格レンジ -->
            <div class="price-range-section">
              <h4>市場単価レンジ</h4>
              <div class="price-bar">
                <div class="price-bar-fill" :style="{ 
                  left: `${(result.priceRange.percentile25 - result.priceRange.min) / (result.priceRange.max - result.priceRange.min) * 100}%`,
                  right: `${100 - (result.priceRange.percentile75 - result.priceRange.min) / (result.priceRange.max - result.priceRange.min) * 100}%`
                }"></div>
                <div class="price-marker median" :style="{ 
                  left: `${(result.priceRange.median - result.priceRange.min) / (result.priceRange.max - result.priceRange.min) * 100}%` 
                }">
                  <span class="marker-label">中央値</span>
                  <span class="marker-value">¥{{ formatNumber(result.priceRange.median) }}</span>
                </div>
              </div>
              <div class="price-labels">
                <span>¥{{ formatNumber(result.priceRange.min) }}</span>
                <span>¥{{ formatNumber(result.priceRange.max) }}</span>
              </div>
            </div>

            <!-- 価格推奨 -->
            <div class="recommendations-section">
              <h4>単価設定アドバイス</h4>
              <div class="recommendation-cards">
                <div 
                  v-for="rec in result.recommendations" 
                  :key="rec.label" 
                  class="recommendation-card"
                  :class="rec.label"
                >
                  <div class="rec-label">{{ rec.label }}</div>
                  <div class="rec-price">¥{{ formatNumber(rec.price) }}</div>
                  <div class="rec-probability">
                    <span>成約率</span>
                    <el-progress 
                      :percentage="rec.probability" 
                      :stroke-width="8"
                      :color="getProbabilityColor(rec.probability)"
                    />
                  </div>
                </div>
              </div>
            </div>

            <!-- 市場動向 -->
            <div class="market-trend-section">
              <h4>市場動向</h4>
              <div class="trend-grid">
                <div class="trend-item">
                  <div class="trend-label">トレンド</div>
                  <div class="trend-value">
                    <el-icon :class="['trend-icon', result.marketTrend]">
                      <component :is="getTrendIcon(result.marketTrend)" />
                    </el-icon>
                    {{ getTrendLabel(result.marketTrend) }}
                  </div>
                </div>
                <div class="trend-item">
                  <div class="trend-label">需給バランス</div>
                  <div class="trend-value">
                    <span :class="['ratio', result.supplyDemandRatio < 1 ? 'demand' : 'supply']">
                      {{ result.supplyDemandRatio < 1 ? '需要優位' : '供給優位' }}
                    </span>
                    <span class="ratio-value">({{ result.supplyDemandRatio.toFixed(2) }})</span>
                  </div>
                </div>
                <div class="trend-item full">
                  <div class="trend-label">季節性メモ</div>
                  <div class="trend-value">{{ result.seasonalNote }}</div>
                </div>
              </div>
            </div>

            <!-- スキル別の詳細は将来追加 -->
          </div>
          <el-empty v-else description="条件を入力して分析を実行してください" />
        </el-card>
      </el-col>
    </el-row>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive } from 'vue'
import { ElMessage } from 'element-plus'
import { TrendCharts, DataAnalysis, Top, Bottom, Minus } from '@element-plus/icons-vue'
import api from '../../api'

interface AnalysisResult {
  sampleSize: number
  priceRange: {
    min: number
    percentile25: number
    median: number
    percentile75: number
    max: number
    average: number
  }
  recommendations: Array<{
    price: number
    probability: number
    label: string
  }>
  marketTrend: string
  supplyDemandRatio: number
  seasonalNote: string
}

const loading = ref(false)
const result = ref<AnalysisResult | null>(null)

const query = reactive({
  skills: [] as string[],
  experienceYears: 5
})

const commonSkills = [
  'Java', 'Python', 'JavaScript', 'TypeScript', 'Go', 'C#', 'PHP', 'Ruby',
  'React', 'Vue.js', 'Angular', 'Node.js', 'Spring Boot',
  'AWS', 'Azure', 'GCP', 'Docker', 'Kubernetes',
  'MySQL', 'PostgreSQL', 'MongoDB'
]

const experienceMarks = {
  0: '未経験',
  3: '3年',
  5: '5年',
  10: '10年',
  15: '15年',
  20: '20年+'
}

const analyze = async () => {
  loading.value = true
  try {
    const res = await api.post('/staffing/ai/market-analysis', {
      skills: query.skills,
      experienceYears: query.experienceYears
    })
    result.value = res.data
  } catch (e: any) {
    ElMessage.error('分析に失敗しました')
  } finally {
    loading.value = false
  }
}

const formatNumber = (num: number) => {
  return new Intl.NumberFormat('ja-JP').format(num)
}

const getProbabilityColor = (prob: number) => {
  if (prob >= 90) return '#67c23a'
  if (prob >= 70) return '#409eff'
  return '#e6a23c'
}

const getTrendIcon = (trend: string) => {
  if (trend === 'rising') return Top
  if (trend === 'falling') return Bottom
  return Minus
}

const getTrendLabel = (trend: string) => {
  const map: Record<string, string> = {
    rising: '上昇傾向',
    falling: '下降傾向',
    stable: '安定'
  }
  return map[trend] || trend
}
</script>

<style scoped>
.ai-market {
  padding: 20px;
  background: #f5f7fa;
  min-height: 100vh;
}

.page-header {
  display: flex;
  align-items: center;
  margin-bottom: 20px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 12px;
}

.header-icon {
  font-size: 28px;
  color: #667eea;
}

.header-left h1 {
  margin: 0;
  font-size: 22px;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.card-title {
  font-weight: 600;
}

.result-content {
  padding: 0 8px;
}

.result-content h4 {
  margin: 0 0 16px 0;
  font-size: 14px;
  color: #606266;
  padding-bottom: 8px;
  border-bottom: 1px solid #ebeef5;
}

.price-range-section {
  margin-bottom: 32px;
}

.price-bar {
  position: relative;
  height: 24px;
  background: #f0f0f0;
  border-radius: 12px;
  margin: 40px 0 8px;
}

.price-bar-fill {
  position: absolute;
  top: 0;
  bottom: 0;
  background: linear-gradient(90deg, #67c23a, #409eff, #e6a23c);
  border-radius: 12px;
}

.price-marker {
  position: absolute;
  top: -32px;
  transform: translateX(-50%);
  text-align: center;
}

.price-marker::after {
  content: '';
  position: absolute;
  bottom: -8px;
  left: 50%;
  transform: translateX(-50%);
  width: 2px;
  height: 40px;
  background: #303133;
}

.marker-label {
  display: block;
  font-size: 11px;
  color: #909399;
}

.marker-value {
  display: block;
  font-size: 14px;
  font-weight: 600;
  color: #303133;
}

.price-labels {
  display: flex;
  justify-content: space-between;
  font-size: 12px;
  color: #909399;
}

.recommendations-section {
  margin-bottom: 32px;
}

.recommendation-cards {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 16px;
}

.recommendation-card {
  padding: 20px;
  border-radius: 12px;
  text-align: center;
}

.recommendation-card.強気 {
  background: linear-gradient(135deg, #fdf6ec 0%, #fef0e3 100%);
  border: 1px solid #e6a23c40;
}

.recommendation-card.適正 {
  background: linear-gradient(135deg, #ecf5ff 0%, #e6f1fc 100%);
  border: 1px solid #409eff40;
}

.recommendation-card.確実 {
  background: linear-gradient(135deg, #f0f9eb 0%, #e8f5e1 100%);
  border: 1px solid #67c23a40;
}

.rec-label {
  font-size: 14px;
  font-weight: 600;
  margin-bottom: 8px;
}

.recommendation-card.強気 .rec-label { color: #e6a23c; }
.recommendation-card.適正 .rec-label { color: #409eff; }
.recommendation-card.確実 .rec-label { color: #67c23a; }

.rec-price {
  font-size: 24px;
  font-weight: 700;
  margin-bottom: 12px;
}

.rec-probability span {
  display: block;
  font-size: 12px;
  color: #909399;
  margin-bottom: 4px;
}

.market-trend-section {
  margin-bottom: 20px;
}

.trend-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
}

.trend-item {
  padding: 16px;
  background: #f5f7fa;
  border-radius: 8px;
}

.trend-item.full {
  grid-column: span 2;
}

.trend-label {
  font-size: 12px;
  color: #909399;
  margin-bottom: 8px;
}

.trend-value {
  font-size: 16px;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 8px;
}

.trend-icon {
  font-size: 20px;
}

.trend-icon.rising { color: #67c23a; }
.trend-icon.falling { color: #f56c6c; }
.trend-icon.stable { color: #909399; }

.ratio.demand { color: #67c23a; }
.ratio.supply { color: #e6a23c; }
.ratio-value { font-size: 13px; color: #909399; }
</style>

