<template>
  <div class="ai-matching">
    <div class="page-header">
      <div class="header-left">
        <el-icon class="header-icon"><MagicStick /></el-icon>
        <h1>AI マッチング</h1>
      </div>
    </div>

    <el-row :gutter="20">
      <!-- 左侧：需求输入 -->
      <el-col :span="10">
        <el-card class="input-card">
          <template #header>
            <div class="card-header">
              <el-icon><Edit /></el-icon>
              <span>案件要件</span>
            </div>
          </template>

          <!-- タブ切替 -->
          <el-tabs v-model="inputMode">
            <el-tab-pane label="メール解析" name="email">
              <el-form label-position="top">
                <el-form-item label="件名">
                  <el-input v-model="emailInput.subject" placeholder="【案件】Java開発エンジニア募集" />
                </el-form-item>
                <el-form-item label="メール本文">
                  <el-input 
                    v-model="emailInput.content" 
                    type="textarea" 
                    :rows="8"
                    placeholder="お世話になっております。
以下の案件でエンジニアを探しております。

・必要スキル：Java, Spring Boot
・経験年数：5年以上
・開始時期：来月から
・勤務地：渋谷
・単価：60-70万円/月

ご確認のほど、よろしくお願いいたします。"
                  />
                </el-form-item>
                <el-button type="primary" @click="parseEmail" :loading="parsing" style="width: 100%">
                  <el-icon><MagicStick /></el-icon>
                  AI で解析
                </el-button>
              </el-form>
            </el-tab-pane>

            <el-tab-pane label="直接入力" name="manual">
              <el-form label-position="top">
                <el-form-item label="必要スキル">
                  <el-select 
                    v-model="manualInput.skills" 
                    multiple 
                    filterable 
                    allow-create
                    placeholder="スキルを入力"
                    style="width: 100%"
                  >
                    <el-option v-for="s in commonSkills" :key="s" :label="s" :value="s" />
                  </el-select>
                </el-form-item>
                <el-form-item label="経験年数">
                  <el-input-number v-model="manualInput.experienceYears" :min="0" :max="30" style="width: 100%" />
                </el-form-item>
                <el-form-item label="案件説明">
                  <el-input v-model="manualInput.description" type="textarea" :rows="4" />
                </el-form-item>
                <el-form-item label="予算上限（万円/月）">
                  <el-input-number v-model="manualInput.budgetMax" :min="0" :step="5" style="width: 100%" />
                </el-form-item>
                <el-button type="primary" @click="searchCandidates" :loading="searching" style="width: 100%">
                  <el-icon><Search /></el-icon>
                  候補者を検索
                </el-button>
              </el-form>
            </el-tab-pane>
          </el-tabs>

          <!-- 解析結果 -->
          <div class="parsed-result" v-if="parsedProject">
            <el-divider content-position="left">AI 解析結果</el-divider>
            <div class="parsed-grid">
              <div class="parsed-item">
                <span class="label">案件名</span>
                <span class="value">{{ parsedProject.project_name }}</span>
              </div>
              <div class="parsed-item">
                <span class="label">スキル</span>
                <div class="skill-tags">
                  <el-tag v-for="s in parsedProject.required_skills" :key="s" size="small">{{ s }}</el-tag>
                </div>
              </div>
              <div class="parsed-item">
                <span class="label">経験年数</span>
                <span class="value">{{ parsedProject.experience_years }}年以上</span>
              </div>
              <div class="parsed-item">
                <span class="label">勤務地</span>
                <span class="value">{{ parsedProject.work_location }}</span>
              </div>
              <div class="parsed-item">
                <span class="label">予算</span>
                <span class="value">{{ parsedProject.budget_min }}〜{{ parsedProject.budget_max }}万円/月</span>
              </div>
              <div class="parsed-item">
                <span class="label">開始日</span>
                <span class="value">{{ parsedProject.start_date }}</span>
              </div>
            </div>
            <div class="parsed-actions">
              <el-button type="success" size="small" @click="createProjectFromParsed">
                <el-icon><Plus /></el-icon>
                案件として登録
              </el-button>
              <el-button type="primary" size="small" @click="searchFromParsed">
                <el-icon><Search /></el-icon>
                候補者を検索
              </el-button>
            </div>
          </div>
        </el-card>
      </el-col>

      <!-- 右侧：匹配结果 -->
      <el-col :span="14">
        <el-card class="result-card" v-loading="searching">
          <template #header>
            <div class="card-header">
              <el-icon><Connection /></el-icon>
              <span>マッチング結果</span>
              <el-tag v-if="candidates.length > 0" type="success" size="small">
                {{ candidates.length }}名
              </el-tag>
            </div>
          </template>

          <div class="candidate-list" v-if="candidates.length > 0">
            <div 
              v-for="(candidate, idx) in candidates" 
              :key="candidate.id" 
              class="candidate-card"
              :class="{ 'top-match': idx < 3 }"
            >
              <div class="candidate-rank">{{ idx + 1 }}</div>
              <div class="candidate-info">
                <div class="candidate-header">
                  <span class="candidate-name">{{ candidate.displayName }}</span>
                  <el-tag :type="getResourceTagType(candidate.resourceType)" size="small">
                    {{ getResourceLabel(candidate.resourceType) }}
                  </el-tag>
                </div>
                <div class="candidate-code">{{ candidate.resourceCode }}</div>
                <div class="candidate-skills">
                  <el-tag 
                    v-for="s in candidate.skills?.slice(0, 5)" 
                    :key="s" 
                    size="small" 
                    type="info"
                  >{{ s }}</el-tag>
                  <span v-if="candidate.skills?.length > 5" class="more-skills">
                    +{{ candidate.skills.length - 5 }}
                  </span>
                </div>
                <div class="candidate-meta">
                  <span v-if="candidate.monthlyRate">
                    <el-icon><Money /></el-icon>
                    ¥{{ formatNumber(candidate.monthlyRate) }}/月
                  </span>
                  <span>
                    <el-icon><Clock /></el-icon>
                    {{ getAvailabilityLabel(candidate.availabilityStatus) }}
                  </span>
                </div>
              </div>
              <div class="candidate-score">
                <div class="score-circle" :style="{ '--score': candidate.matchScore.overall }">
                  <span class="score-value">{{ Math.round(candidate.matchScore.overall * 100) }}</span>
                  <span class="score-label">%</span>
                </div>
                <div class="score-detail">
                  <div class="score-row">
                    <span>スキル</span>
                    <el-progress 
                      :percentage="candidate.matchScore.skillMatch * 100" 
                      :show-text="false" 
                      :stroke-width="4"
                    />
                  </div>
                  <div class="score-row">
                    <span>経験</span>
                    <el-progress 
                      :percentage="candidate.matchScore.experienceMatch * 100" 
                      :show-text="false" 
                      :stroke-width="4"
                    />
                  </div>
                </div>
              </div>
              <div class="candidate-actions">
                <el-button size="small" type="primary" @click="generateOutreach(candidate)">
                  <el-icon><Message /></el-icon>
                  連絡
                </el-button>
                <el-button size="small" @click="viewProfile(candidate)">詳細</el-button>
              </div>
            </div>
          </div>
          <el-empty v-else description="条件を入力して検索してください" />
        </el-card>
      </el-col>
    </el-row>

    <!-- 連絡生成ダイアログ -->
    <el-dialog v-model="outreachDialogVisible" title="案件紹介メール生成" width="600px">
      <div v-loading="generatingOutreach">
        <div class="outreach-preview" v-if="generatedOutreach">
          <div class="outreach-field">
            <span class="field-label">宛先:</span>
            <span>{{ selectedCandidate?.displayName }}</span>
          </div>
          <div class="outreach-field">
            <span class="field-label">件名:</span>
            <el-input v-model="generatedOutreach.subject" />
          </div>
          <div class="outreach-field">
            <span class="field-label">本文:</span>
            <el-input v-model="generatedOutreach.body" type="textarea" :rows="12" />
          </div>
        </div>
      </div>
      <template #footer>
        <el-button @click="outreachDialogVisible = false">キャンセル</el-button>
        <el-button type="primary" @click="sendOutreach" :loading="sendingOutreach">
          <el-icon><Promotion /></el-icon>
          送信キューに追加
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, reactive } from 'vue'
import { ElMessage } from 'element-plus'
import { MagicStick, Edit, Search, Connection, Plus, Message, Money, Clock, Promotion } from '@element-plus/icons-vue'
import api from '../../api'

interface Candidate {
  id: string
  resourceCode: string
  displayName: string
  resourceType: string
  skills: string[]
  monthlyRate?: number
  availabilityStatus: string
  matchScore: {
    overall: number
    skillMatch: number
    experienceMatch: number
  }
}

const inputMode = ref('email')
const parsing = ref(false)
const searching = ref(false)
const generatingOutreach = ref(false)
const sendingOutreach = ref(false)

const emailInput = reactive({
  subject: '',
  content: ''
})

const manualInput = reactive({
  skills: [] as string[],
  experienceYears: 3,
  description: '',
  budgetMax: 70
})

const parsedProject = ref<any>(null)
const candidates = ref<Candidate[]>([])

const outreachDialogVisible = ref(false)
const selectedCandidate = ref<Candidate | null>(null)
const generatedOutreach = ref<{ subject: string; body: string } | null>(null)

const commonSkills = [
  'Java', 'Python', 'JavaScript', 'TypeScript', 'Go', 'Rust', 'C#', 'PHP', 'Ruby',
  'React', 'Vue.js', 'Angular', 'Node.js', 'Spring Boot', 'Django', 'Rails',
  'AWS', 'Azure', 'GCP', 'Docker', 'Kubernetes',
  'MySQL', 'PostgreSQL', 'MongoDB', 'Redis',
  'Linux', 'Git', 'CI/CD', 'Agile', 'Scrum'
]

const parseEmail = async () => {
  if (!emailInput.content) {
    ElMessage.warning('メール本文を入力してください')
    return
  }
  parsing.value = true
  try {
    const res = await api.post('/staffing/ai/parse-project-request', {
      subject: emailInput.subject,
      content: emailInput.content
    })
    parsedProject.value = res.data.parsed
    ElMessage.success('解析完了')
  } catch (e: any) {
    ElMessage.error('解析に失敗しました')
  } finally {
    parsing.value = false
  }
}

const searchCandidates = async () => {
  if (manualInput.skills.length === 0) {
    ElMessage.warning('スキルを入力してください')
    return
  }
  searching.value = true
  try {
    const res = await api.post('/staffing/ai/match-candidates', {
      requiredSkills: manualInput.skills,
      experienceYears: manualInput.experienceYears,
      description: manualInput.description,
      budgetMax: manualInput.budgetMax * 10000
    })
    candidates.value = res.data.candidates
  } catch (e: any) {
    ElMessage.error('検索に失敗しました')
  } finally {
    searching.value = false
  }
}

const searchFromParsed = async () => {
  if (!parsedProject.value) return
  searching.value = true
  try {
    const res = await api.post('/staffing/ai/match-candidates', {
      requiredSkills: parsedProject.value.required_skills,
      experienceYears: parsedProject.value.experience_years,
      budgetMax: parsedProject.value.budget_max * 10000
    })
    candidates.value = res.data.candidates
  } catch (e: any) {
    ElMessage.error('検索に失敗しました')
  } finally {
    searching.value = false
  }
}

const createProjectFromParsed = () => {
  ElMessage.info('案件登録画面に遷移します')
  // TODO: router.push with parsed data
}

const generateOutreach = async (candidate: Candidate) => {
  selectedCandidate.value = candidate
  outreachDialogVisible.value = true
  generatingOutreach.value = true
  
  try {
    const res = await api.post('/staffing/ai/generate-outreach', {
      templateType: 'project_intro',
      resourceId: candidate.id,
      projectId: null // 如果有关联项目
    })
    generatedOutreach.value = {
      subject: res.data.subject,
      body: res.data.body
    }
  } catch (e: any) {
    ElMessage.error('メール生成に失敗しました')
  } finally {
    generatingOutreach.value = false
  }
}

const sendOutreach = async () => {
  if (!generatedOutreach.value || !selectedCandidate.value) return
  sendingOutreach.value = true
  try {
    await api.post('/staffing/email/send', {
      toAddresses: selectedCandidate.value.id, // 实际应该是邮箱
      subject: generatedOutreach.value.subject,
      bodyHtml: generatedOutreach.value.body.replace(/\n/g, '<br>'),
      linkedEntityType: 'resource',
      linkedEntityId: selectedCandidate.value.id
    })
    ElMessage.success('送信キューに追加しました')
    outreachDialogVisible.value = false
  } catch (e: any) {
    ElMessage.error('送信に失敗しました')
  } finally {
    sendingOutreach.value = false
  }
}

const viewProfile = (candidate: Candidate) => {
  ElMessage.info(`${candidate.displayName}の詳細を表示`)
}

const getResourceLabel = (type: string) => {
  const map: Record<string, string> = { employee: '自社', freelancer: '個人', bp: 'BP', candidate: '候補' }
  return map[type] || type
}

const getResourceTagType = (type: string) => {
  const map: Record<string, string> = { employee: 'primary', freelancer: 'success', bp: 'warning', candidate: 'info' }
  return map[type] || 'info'
}

const getAvailabilityLabel = (status: string) => {
  const map: Record<string, string> = { available: '即可', ending_soon: '間もなく可', assigned: '稼働中' }
  return map[status] || status
}

const formatNumber = (num: number) => {
  return new Intl.NumberFormat('ja-JP').format(num)
}
</script>

<style scoped>
.ai-matching {
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
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  -webkit-background-clip: text;
  -webkit-text-fill-color: transparent;
}

.input-card, .result-card {
  height: calc(100vh - 140px);
  overflow-y: auto;
}

.card-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 600;
}

.parsed-result {
  margin-top: 16px;
}

.parsed-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}

.parsed-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.parsed-item .label {
  font-size: 12px;
  color: #909399;
}

.parsed-item .value {
  font-size: 14px;
  font-weight: 500;
}

.skill-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.parsed-actions {
  display: flex;
  gap: 8px;
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.candidate-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.candidate-card {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 16px;
  background: white;
  border: 1px solid #ebeef5;
  border-radius: 8px;
  transition: all 0.2s;
}

.candidate-card:hover {
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
}

.candidate-card.top-match {
  border-left: 4px solid #667eea;
  background: linear-gradient(90deg, #667eea08 0%, transparent 100%);
}

.candidate-rank {
  width: 32px;
  height: 32px;
  border-radius: 50%;
  background: #f5f7fa;
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: 700;
  color: #909399;
  flex-shrink: 0;
}

.candidate-card.top-match .candidate-rank {
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  color: white;
}

.candidate-info {
  flex: 1;
  min-width: 0;
}

.candidate-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}

.candidate-name {
  font-size: 16px;
  font-weight: 600;
}

.candidate-code {
  font-size: 12px;
  color: #c0c4cc;
  font-family: monospace;
}

.candidate-skills {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  margin: 8px 0;
}

.more-skills {
  font-size: 12px;
  color: #909399;
}

.candidate-meta {
  display: flex;
  gap: 16px;
  font-size: 13px;
  color: #606266;
}

.candidate-meta span {
  display: flex;
  align-items: center;
  gap: 4px;
}

.candidate-score {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 0 16px;
}

.score-circle {
  width: 60px;
  height: 60px;
  border-radius: 50%;
  background: conic-gradient(
    #667eea calc(var(--score) * 100%),
    #ebeef5 calc(var(--score) * 100%)
  );
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  position: relative;
}

.score-circle::before {
  content: '';
  position: absolute;
  width: 48px;
  height: 48px;
  border-radius: 50%;
  background: white;
}

.score-value {
  font-size: 18px;
  font-weight: 700;
  color: #667eea;
  position: relative;
  z-index: 1;
}

.score-label {
  font-size: 10px;
  color: #909399;
  position: relative;
  z-index: 1;
  margin-top: -4px;
}

.score-detail {
  width: 80px;
}

.score-row {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 11px;
  color: #909399;
  margin-bottom: 4px;
}

.score-row span {
  width: 28px;
}

.candidate-actions {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.outreach-preview {
  padding: 16px;
}

.outreach-field {
  margin-bottom: 16px;
}

.field-label {
  display: block;
  font-size: 13px;
  color: #909399;
  margin-bottom: 8px;
}
</style>

