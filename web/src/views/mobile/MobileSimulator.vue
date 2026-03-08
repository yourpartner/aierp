<template>
  <div class="mobile-simulator-container">
    <div class="device-gallery">
      <!-- WeChat 模拟器 -->
      <div class="device-wrapper">
        <div class="device-title">WeChat 模拟器</div>
        <div class="device-frame">
          <div class="device-screen">
            <div class="status-bar" :class="{ 'dark': theme === 'dark' }">
              <div class="time">{{ currentTime }}</div>
              <div class="icons">
                <span class="icon-signal">📶</span>
                <span class="icon-wifi">📶</span>
                <span class="icon-battery">🔋</span>
              </div>
            </div>
            <div class="app-content">
              <!-- 根据状态切换视图 -->
              <WechatUI v-if="currentWechatView === 'chat'" />
              
              <!-- 微信风格的工资明细 H5 页面 -->
              <div v-else-if="currentWechatView === 'payslip'" class="detail-view wechat-style">
                <div class="detail-header">
                  <div class="back-btn" @click="currentWechatView = 'chat'">&lt;</div>
                  <div class="title">給与明細詳細</div>
                  <div class="more-btn">...</div>
                </div>
                <div class="detail-body payslip-body">
                  <div class="payslip-header-card">
                    <div class="month">2026年02月分</div>
                    <div class="amount-label">差引支給額</div>
                    <div class="amount-value">¥ 325,000</div>
                    <div class="payment-date">支給日: 2026-03-01</div>
                  </div>
                  
                  <div class="payslip-section">
                    <div class="section-title">勤怠情報</div>
                    <div class="section-row"><span>所定労働日数</span><span>20日</span></div>
                    <div class="section-row"><span>出勤日数</span><span>20日</span></div>
                    <div class="section-row"><span>欠勤日数</span><span>0日</span></div>
                    <div class="section-row"><span>残業時間</span><span>12.5h</span></div>
                  </div>
                  
                  <div class="payslip-section">
                    <div class="section-title">支給項目</div>
                    <div class="section-row"><span>基本給</span><span>¥ 300,000</span></div>
                    <div class="section-row"><span>役職手当</span><span>¥ 50,000</span></div>
                    <div class="section-row"><span>残業手当</span><span>¥ 25,000</span></div>
                    <div class="section-row"><span>通勤手当</span><span>¥ 15,000</span></div>
                    <div class="section-row total"><span>総支給額</span><span>¥ 390,000</span></div>
                  </div>
                  
                  <div class="payslip-section">
                    <div class="section-title">控除項目</div>
                    <div class="section-row"><span>健康保険料</span><span>¥ 18,000</span></div>
                    <div class="section-row"><span>厚生年金保険料</span><span>¥ 32,000</span></div>
                    <div class="section-row"><span>雇用保険料</span><span>¥ 1,500</span></div>
                    <div class="section-row"><span>所得税</span><span>¥ 8,500</span></div>
                    <div class="section-row"><span>住民税</span><span>¥ 5,000</span></div>
                    <div class="section-row total"><span>総控除額</span><span>¥ 65,000</span></div>
                  </div>
                </div>
              </div>

              <!-- 微信风格的历史工资单列表 -->
              <div v-else-if="currentWechatView === 'payslip_history'" class="detail-view wechat-style">
                <div class="detail-header">
                  <div class="back-btn" @click="currentWechatView = 'chat'">&lt;</div>
                  <div class="title">給与明細履歴</div>
                  <div class="more-btn">...</div>
                </div>
                <div class="detail-body history-body">
                  <div class="year-group">
                    <div class="year-title">2026年</div>
                    <div class="history-list">
                      <div class="history-item" @click="currentWechatView = 'payslip'">
                        <div class="history-info">
                          <div class="history-month">02月分</div>
                          <div class="history-date">支給日: 2026-03-01</div>
                        </div>
                        <div class="history-amount">¥ 325,000</div>
                        <div class="history-arrow">&gt;</div>
                      </div>
                      <div class="history-item">
                        <div class="history-info">
                          <div class="history-month">01月分</div>
                          <div class="history-date">支給日: 2026-02-01</div>
                        </div>
                        <div class="history-amount">¥ 320,000</div>
                        <div class="history-arrow">&gt;</div>
                      </div>
                    </div>
                  </div>
                  <div class="year-group">
                    <div class="year-title">2025年</div>
                    <div class="history-list">
                      <div class="history-item">
                        <div class="history-info">
                          <div class="history-month">12月分 (賞与)</div>
                          <div class="history-date">支給日: 2025-12-15</div>
                        </div>
                        <div class="history-amount">¥ 600,000</div>
                        <div class="history-arrow">&gt;</div>
                      </div>
                      <div class="history-item">
                        <div class="history-info">
                          <div class="history-month">12月分</div>
                          <div class="history-date">支給日: 2025-12-01</div>
                        </div>
                        <div class="history-amount">¥ 318,000</div>
                        <div class="history-arrow">&gt;</div>
                      </div>
                      <div class="history-item">
                        <div class="history-info">
                          <div class="history-month">11月分</div>
                          <div class="history-date">支給日: 2025-11-01</div>
                        </div>
                        <div class="history-amount">¥ 322,000</div>
                        <div class="history-arrow">&gt;</div>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
              
              <!-- 微信风格的勤怠入力页面 -->
              <div v-else-if="currentWechatView === 'timesheet_input'" class="detail-view wechat-style">
                <div class="detail-header">
                  <div class="back-btn" @click="handleTimesheetBack('wechat')">&lt;</div>
                  <div class="title">勤怠入力</div>
                  <div class="more-btn">...</div>
                </div>
                <div class="detail-body timesheet-body">
                  <div class="ts-month-header">
                    <div class="ts-month-title">2026年02月分 勤怠表</div>
                    <div class="ts-client-info">派遣先: 株式会社ABC商事</div>
                    <div class="ts-status-badge not-submitted" v-if="!wechatTimesheetSubmitted">未提出</div>
                    <div class="ts-status-badge submitted" v-else>提出済</div>
                  </div>

                  <div class="ts-day-list">
                    <div v-for="day in timesheetDays" :key="day.date"
                         :class="['ts-day-row', { weekend: day.isWeekend, filled: day.start }]">
                      <div class="ts-day-date">
                        <span class="date">{{ day.dateLabel }}</span>
                        <span :class="['dow', { sat: day.dow === '土', sun: day.dow === '日' }]">{{ day.dow }}</span>
                      </div>
                      <div class="ts-day-times" v-if="!day.isWeekend">
                        <input type="time" v-model="day.start" class="time-input" :disabled="wechatTimesheetSubmitted" />
                        <span class="time-sep">~</span>
                        <input type="time" v-model="day.end" class="time-input" :disabled="wechatTimesheetSubmitted" />
                      </div>
                      <div class="ts-day-times" v-else>
                        <span class="rest-label">休日</span>
                      </div>
                      <div class="ts-day-hours">
                        {{ day.isWeekend ? '-' : calcHours(day.start, day.end) }}
                      </div>
                    </div>
                  </div>

                  <div class="ts-summary">
                    <div class="ts-summary-row"><span>稼働日数</span><span>{{ workingDays }}日</span></div>
                    <div class="ts-summary-row"><span>合計時間</span><span>{{ totalHours }}h</span></div>
                    <div class="ts-summary-row"><span>残業時間</span><span>{{ overtimeHours }}h</span></div>
                  </div>

                  <div class="ts-actions" v-if="!wechatTimesheetSubmitted">
                    <button class="ts-btn save" @click="saveTimesheet('wechat')">一時保存</button>
                    <button class="ts-btn submit" @click="submitTimesheet('wechat')">提出する</button>
                  </div>
                  <div class="ts-actions" v-else>
                    <div class="ts-submitted-msg">勤怠を提出しました。お疲れ様でした！</div>
                  </div>
                </div>
              </div>

              <div v-else class="detail-view wechat-style">
                <div class="detail-header">
                  <div class="back-btn" @click="currentWechatView = 'chat'">&lt;</div>
                  <div class="title">{{ getDetailTitle(currentWechatView) }}</div>
                  <div class="more-btn">...</div>
                </div>
                <div class="detail-body">
                  <div class="placeholder-text">ここは{{ getDetailTitle(currentWechatView) }}のH5ページです。</div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- Line 模拟器 -->
      <div class="device-wrapper">
        <div class="device-title">Line 模拟器</div>
        <div class="device-frame">
          <div class="device-screen">
            <div class="status-bar" :class="{ 'dark': theme === 'dark' }">
              <div class="time">{{ currentTime }}</div>
              <div class="icons">
                <span class="icon-signal">📶</span>
                <span class="icon-wifi">📶</span>
                <span class="icon-battery">🔋</span>
              </div>
            </div>
            <div class="app-content">
              <!-- 根据状态切换视图 -->
              <LineUI v-if="currentLineView === 'chat'" />
              
              <!-- Line 风格的工资明细 LIFF 页面 -->
              <div v-else-if="currentLineView === 'payslip'" class="detail-view line-style">
                <div class="detail-header line-style">
                  <div class="back-btn" @click="currentLineView = 'chat'">×</div>
                  <div class="title">給与明細</div>
                  <div class="more-btn">⋮</div>
                </div>
                <div class="detail-body payslip-body line-theme">
                  <div class="liff-header">
                    <h2>2026年02月分 給与明細</h2>
                    <p class="subtitle">株式会社サンプル</p>
                  </div>
                  
                  <div class="summary-card">
                    <div class="summary-label">差引支給額</div>
                    <div class="summary-amount">¥ 325,000</div>
                  </div>
                  
                  <div class="accordion-group">
                    <details class="line-accordion" open>
                      <summary>支給項目 <span class="sum-value">¥ 390,000</span></summary>
                      <div class="accordion-content">
                        <div class="row"><span>基本給</span><span>¥ 300,000</span></div>
                        <div class="row"><span>役職手当</span><span>¥ 50,000</span></div>
                        <div class="row"><span>残業手当</span><span>¥ 25,000</span></div>
                        <div class="row"><span>通勤手当</span><span>¥ 15,000</span></div>
                      </div>
                    </details>
                    
                    <details class="line-accordion" open>
                      <summary>控除項目 <span class="sum-value">¥ 65,000</span></summary>
                      <div class="accordion-content">
                        <div class="row"><span>健康保険料</span><span>¥ 18,000</span></div>
                        <div class="row"><span>厚生年金保険料</span><span>¥ 32,000</span></div>
                        <div class="row"><span>雇用保険料</span><span>¥ 1,500</span></div>
                        <div class="row"><span>所得税</span><span>¥ 8,500</span></div>
                        <div class="row"><span>住民税</span><span>¥ 5,000</span></div>
                      </div>
                    </details>
                    
                    <details class="line-accordion">
                      <summary>勤怠情報</summary>
                      <div class="accordion-content">
                        <div class="row"><span>所定労働日数</span><span>20日</span></div>
                        <div class="row"><span>出勤日数</span><span>20日</span></div>
                        <div class="row"><span>残業時間</span><span>12.5h</span></div>
                      </div>
                    </details>
                  </div>
                  
                  <div class="action-area">
                    <button class="line-btn primary">PDFをダウンロード</button>
                  </div>
                </div>
              </div>

              <!-- Line 风格的历史工资单列表 -->
              <div v-else-if="currentLineView === 'payslip_history'" class="detail-view line-style">
                <div class="detail-header line-style">
                  <div class="back-btn" @click="currentLineView = 'chat'">×</div>
                  <div class="title">給与明細履歴</div>
                  <div class="more-btn">⋮</div>
                </div>
                <div class="detail-body history-body line-theme">
                  <div class="liff-header">
                    <h2>過去の給与明細</h2>
                  </div>
                  
                  <div class="line-history-list">
                    <div class="line-history-card" @click="currentLineView = 'payslip'">
                      <div class="card-header">
                        <span class="month-badge">2026年 2月</span>
                        <span class="pay-date">3/1 支給</span>
                      </div>
                      <div class="card-body">
                        <div class="amount-label">差引支給額</div>
                        <div class="amount-value">¥ 325,000</div>
                      </div>
                      <div class="card-footer">
                        詳細を見る <span class="arrow">→</span>
                      </div>
                    </div>
                    
                    <div class="line-history-card">
                      <div class="card-header">
                        <span class="month-badge">2026年 1月</span>
                        <span class="pay-date">2/1 支給</span>
                      </div>
                      <div class="card-body">
                        <div class="amount-label">差引支給額</div>
                        <div class="amount-value">¥ 320,000</div>
                      </div>
                      <div class="card-footer">
                        詳細を見る <span class="arrow">→</span>
                      </div>
                    </div>
                    
                    <div class="line-history-card bonus">
                      <div class="card-header">
                        <span class="month-badge bonus-badge">2025年 冬季賞与</span>
                        <span class="pay-date">12/15 支給</span>
                      </div>
                      <div class="card-body">
                        <div class="amount-label">差引支給額</div>
                        <div class="amount-value">¥ 600,000</div>
                      </div>
                      <div class="card-footer">
                        詳細を見る <span class="arrow">→</span>
                      </div>
                    </div>
                    
                    <div class="line-history-card">
                      <div class="card-header">
                        <span class="month-badge">2025年 12月</span>
                        <span class="pay-date">12/1 支給</span>
                      </div>
                      <div class="card-body">
                        <div class="amount-label">差引支給額</div>
                        <div class="amount-value">¥ 318,000</div>
                      </div>
                      <div class="card-footer">
                        詳細を見る <span class="arrow">→</span>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
              
              <!-- Line 风格的勤怠入力 LIFF 页面 -->
              <div v-else-if="currentLineView === 'timesheet_input'" class="detail-view line-style">
                <div class="detail-header line-style">
                  <div class="back-btn" @click="handleTimesheetBack('line')">×</div>
                  <div class="title">勤怠入力</div>
                  <div class="more-btn">⋮</div>
                </div>
                <div class="detail-body timesheet-body line-theme">
                  <div class="liff-header">
                    <h2>2026年02月分 勤怠表</h2>
                    <p class="subtitle">派遣先: 株式会社ABC商事</p>
                  </div>

                  <div class="ts-status-card" :class="{ submitted: lineTimesheetSubmitted }">
                    <span v-if="!lineTimesheetSubmitted">未提出 - 提出期限: 2026-03-10</span>
                    <span v-else>提出済</span>
                  </div>

                  <div class="ts-day-list line-list">
                    <div v-for="day in timesheetDays" :key="day.date"
                         :class="['ts-day-card', { weekend: day.isWeekend }]">
                      <div class="ts-day-left">
                        <span class="date">{{ day.dateLabel }}</span>
                        <span :class="['dow', { sat: day.dow === '土', sun: day.dow === '日' }]">{{ day.dow }}</span>
                      </div>
                      <div class="ts-day-center" v-if="!day.isWeekend">
                        <input type="time" v-model="day.start" class="line-time-input" :disabled="lineTimesheetSubmitted" />
                        <span class="time-sep">~</span>
                        <input type="time" v-model="day.end" class="line-time-input" :disabled="lineTimesheetSubmitted" />
                      </div>
                      <div class="ts-day-center" v-else>
                        <span class="rest-label">休日</span>
                      </div>
                      <div class="ts-day-right">
                        {{ day.isWeekend ? '-' : calcHours(day.start, day.end) }}
                      </div>
                    </div>
                  </div>

                  <div class="summary-card ts-line-summary">
                    <div class="row"><span>稼働日数</span><span>{{ workingDays }}日</span></div>
                    <div class="row"><span>合計時間</span><span class="total-val">{{ totalHours }}h</span></div>
                    <div class="row"><span>残業時間</span><span>{{ overtimeHours }}h</span></div>
                  </div>

                  <div class="action-area" v-if="!lineTimesheetSubmitted">
                    <button class="line-btn secondary" @click="saveTimesheet('line')">一時保存</button>
                    <button class="line-btn primary" @click="submitTimesheet('line')">提出する</button>
                  </div>
                  <div class="action-area" v-else>
                    <div class="ts-submitted-msg line-submitted">勤怠を提出しました。お疲れ様でした！</div>
                  </div>
                </div>
              </div>

              <div v-else class="detail-view line-style">
                <div class="detail-header line-style">
                  <div class="back-btn" @click="currentLineView = 'chat'">×</div>
                  <div class="title">{{ getDetailTitle(currentLineView) }}</div>
                  <div class="more-btn">⋮</div>
                </div>
                <div class="detail-body">
                  <div class="placeholder-text">ここは{{ getDetailTitle(currentLineView) }}のLIFFページです。</div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue'
import { useRoute } from 'vue-router'
import WechatUI from './WechatUI.vue'
import LineUI from './LineUI.vue'

const route = useRoute()
const currentTime = ref('12:00')
const theme = ref('light')

const currentWechatView = ref('chat')
const currentLineView = ref('chat')

// --- Timesheet data ---
interface TimesheetDay {
  date: string
  dateLabel: string
  dow: string
  isWeekend: boolean
  start: string
  end: string
}

const wechatTimesheetSubmitted = ref(false)
const lineTimesheetSubmitted = ref(false)

const generateTimesheetDays = (): TimesheetDay[] => {
  const days: TimesheetDay[] = []
  const dowNames = ['日', '月', '火', '水', '木', '金', '土']
  // February 2026
  for (let d = 1; d <= 28; d++) {
    const dt = new Date(2026, 1, d)
    const dow = dowNames[dt.getDay()]
    const isWeekend = dt.getDay() === 0 || dt.getDay() === 6
    days.push({
      date: `2026-02-${String(d).padStart(2, '0')}`,
      dateLabel: `2/${d}`,
      dow,
      isWeekend,
      start: isWeekend ? '' : '09:00',
      end: isWeekend ? '' : '18:00'
    })
  }
  return days
}

const timesheetDays = ref<TimesheetDay[]>(generateTimesheetDays())

const calcHours = (start: string, end: string): string => {
  if (!start || !end) return '-'
  const [sh, sm] = start.split(':').map(Number)
  const [eh, em] = end.split(':').map(Number)
  const mins = (eh * 60 + em) - (sh * 60 + sm) - 60 // 1h break
  return mins > 0 ? (mins / 60).toFixed(1) : '0.0'
}

const workingDays = computed(() => {
  return timesheetDays.value.filter(d => !d.isWeekend && d.start && d.end).length
})

const totalHours = computed(() => {
  let sum = 0
  for (const d of timesheetDays.value) {
    if (d.isWeekend || !d.start || !d.end) continue
    const h = parseFloat(calcHours(d.start, d.end))
    if (!isNaN(h)) sum += h
  }
  return sum.toFixed(1)
})

const overtimeHours = computed(() => {
  let sum = 0
  for (const d of timesheetDays.value) {
    if (d.isWeekend || !d.start || !d.end) continue
    const h = parseFloat(calcHours(d.start, d.end))
    if (!isNaN(h) && h > 8) sum += h - 8
  }
  return sum.toFixed(1)
})

const saveTimesheet = (_platform: string) => {
  alert('一時保存しました。')
}

const submitTimesheet = (platform: string) => {
  if (platform === 'wechat') {
    wechatTimesheetSubmitted.value = true
  } else {
    lineTimesheetSubmitted.value = true
  }
}

const handleTimesheetBack = (platform: string) => {
  if (platform === 'wechat') {
    if (wechatTimesheetSubmitted.value) {
      // Post confirmation message back to chat
      window.dispatchEvent(new CustomEvent('timesheet-submitted', { detail: { platform: 'wechat' } }))
    }
    currentWechatView.value = 'chat'
  } else {
    if (lineTimesheetSubmitted.value) {
      window.dispatchEvent(new CustomEvent('timesheet-submitted', { detail: { platform: 'line' } }))
    }
    currentLineView.value = 'chat'
  }
}

watch(() => route.query.scenario, () => {
  currentWechatView.value = 'chat'
  currentLineView.value = 'chat'
  wechatTimesheetSubmitted.value = false
  lineTimesheetSubmitted.value = false
  timesheetDays.value = generateTimesheetDays()
})

const getDetailTitle = (view: string) => {
  switch(view) {
    case 'payslip': return '給与明細'
    case 'timesheet': return '勤怠入力'
    case 'timesheet_input': return '勤怠入力'
    case 'cert': return '各種証明書'
    case 'dashboard': return 'マイページ'
    default: return '詳細ページ'
  }
}

let timer: number
onMounted(() => {
  const updateTime = () => {
    const now = new Date()
    currentTime.value = `${now.getHours().toString().padStart(2, '0')}:${now.getMinutes().toString().padStart(2, '0')}`
  }
  updateTime()
  timer = window.setInterval(updateTime, 60000)

  window.addEventListener('navigate-to-detail', (e: any) => {
    const view = e.detail.view
    const platform = e.detail.platform

    if (platform === 'wechat') {
      currentWechatView.value = view
    } else if (platform === 'line') {
      currentLineView.value = view
    } else {
      currentWechatView.value = view
      currentLineView.value = view
    }
  })
})

onUnmounted(() => {
  clearInterval(timer)
  window.removeEventListener('navigate-to-detail', () => {})
})
</script>

<style scoped>
.mobile-simulator-container {
  display: flex;
  justify-content: center;
  align-items: center;
  min-height: 100vh;
  background-color: #f0f2f5;
  padding: 40px 20px;
  overflow-x: auto;
}

.device-gallery {
  display: flex;
  gap: 60px;
  align-items: flex-start;
}

.device-wrapper {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 16px;
}

.device-title {
  font-size: 20px;
  font-weight: bold;
  color: #333;
}

.device-frame {
  width: 375px;
  height: 812px; /* iPhone X dimensions */
  background-color: #000;
  border-radius: 40px;
  padding: 12px;
  box-shadow: 0 20px 50px rgba(0, 0, 0, 0.3);
  position: relative;
}

.device-screen {
  width: 100%;
  height: 100%;
  background-color: #fff;
  border-radius: 32px;
  overflow: hidden;
  position: relative;
  display: flex;
  flex-direction: column;
}

.status-bar {
  height: 44px;
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0 20px;
  font-size: 14px;
  font-weight: 600;
  color: #000;
  background-color: transparent;
  z-index: 10;
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
}

.status-bar.dark {
  color: #fff;
}

.time {
  width: 54px;
  text-align: center;
}

.icons {
  display: flex;
  gap: 4px;
}

.app-content {
  flex: 1;
  overflow-y: auto;
  position: relative;
  padding-top: 44px; /* 为状态栏留出空间 */
  background-color: #ededed;
  display: flex;
  flex-direction: column;
}

/* 隐藏滚动条 */
.app-content::-webkit-scrollbar {
  display: none;
}

/* 详情视图基础样式 */
.detail-view {
  display: flex;
  flex-direction: column;
  height: 100%;
  background-color: #f5f5f5;
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
}

.detail-header {
  height: 44px;
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 0 16px;
  font-size: 17px;
  font-weight: 500;
  flex-shrink: 0;
}

.wechat-style .detail-header {
  background-color: #ededed;
  border-bottom: 1px solid #e5e5e5;
  color: #000;
}

.line-style .detail-header {
  background-color: #fff;
  border-bottom: 1px solid #eee;
  color: #000;
}

.back-btn, .more-btn {
  font-size: 20px;
  cursor: pointer;
  width: 40px;
}

.more-btn {
  text-align: right;
}

.detail-body {
  flex: 1;
  overflow-y: auto;
}

.placeholder-text {
  padding: 40px 20px;
  font-size: 16px;
  color: #666;
  text-align: center;
  line-height: 1.5;
}

/* --- WeChat 风格工资单 H5 样式 --- */
.wechat-style .payslip-body {
  padding: 16px;
  background-color: #f5f5f5;
}

.payslip-header-card {
  background: linear-gradient(135deg, #3370ff, #5c8dff);
  border-radius: 12px;
  padding: 24px 20px;
  color: white;
  margin-bottom: 16px;
  box-shadow: 0 4px 12px rgba(51, 112, 255, 0.2);
}

.payslip-header-card .month {
  font-size: 16px;
  opacity: 0.9;
  margin-bottom: 16px;
}

.payslip-header-card .amount-label {
  font-size: 14px;
  opacity: 0.8;
  margin-bottom: 4px;
}

.payslip-header-card .amount-value {
  font-size: 32px;
  font-weight: bold;
  margin-bottom: 16px;
}

.payslip-header-card .payment-date {
  font-size: 12px;
  opacity: 0.8;
  text-align: right;
}

.wechat-style .payslip-section {
  background-color: #fff;
  border-radius: 8px;
  padding: 0 16px;
  margin-bottom: 16px;
}

.wechat-style .section-title {
  padding: 16px 0 12px;
  font-size: 15px;
  font-weight: bold;
  color: #333;
  border-bottom: 1px solid #f0f0f0;
  margin-bottom: 8px;
}

.wechat-style .section-row {
  display: flex;
  justify-content: space-between;
  padding: 8px 0;
  font-size: 14px;
  color: #666;
}

.wechat-style .section-row.total {
  border-top: 1px dashed #f0f0f0;
  margin-top: 4px;
  padding-top: 12px;
  padding-bottom: 16px;
  font-weight: bold;
  color: #333;
}

/* --- Line 风格工资单 LIFF 样式 --- */
.line-style .payslip-body {
  background-color: #f4f5f6;
  padding-bottom: 40px;
}

.liff-header {
  background-color: #fff;
  padding: 20px 16px;
  text-align: center;
}

.liff-header h2 {
  margin: 0 0 8px 0;
  font-size: 18px;
  color: #111;
}

.liff-header .subtitle {
  margin: 0;
  font-size: 14px;
  color: #888;
}

.summary-card {
  margin: 16px;
  background-color: #fff;
  border-radius: 12px;
  padding: 24px;
  text-align: center;
  box-shadow: 0 2px 8px rgba(0,0,0,0.05);
}

.summary-label {
  font-size: 14px;
  color: #666;
  margin-bottom: 8px;
}

.summary-amount {
  font-size: 36px;
  font-weight: bold;
  color: #00c300; /* Line Green */
}

.accordion-group {
  margin: 0 16px;
}

.line-accordion {
  background-color: #fff;
  border-radius: 12px;
  margin-bottom: 12px;
  overflow: hidden;
  box-shadow: 0 2px 8px rgba(0,0,0,0.05);
}

.line-accordion summary {
  padding: 16px;
  font-size: 15px;
  font-weight: bold;
  color: #111;
  display: flex;
  justify-content: space-between;
  align-items: center;
  list-style: none;
  cursor: pointer;
}

.line-accordion summary::-webkit-details-marker {
  display: none;
}

.line-accordion summary::after {
  content: '▼';
  font-size: 12px;
  color: #999;
  transition: transform 0.2s;
}

.line-accordion[open] summary::after {
  transform: rotate(180deg);
}

.sum-value {
  margin-left: auto;
  margin-right: 12px;
  font-weight: normal;
  color: #666;
}

.accordion-content {
  padding: 0 16px 16px;
  border-top: 1px solid #f0f0f0;
  margin-top: 4px;
  padding-top: 12px;
}

.line-theme .row {
  display: flex;
  justify-content: space-between;
  padding: 6px 0;
  font-size: 14px;
  color: #444;
}

.action-area {
  margin: 24px 16px;
}

.line-btn {
  width: 100%;
  padding: 14px;
  border-radius: 8px;
  font-size: 16px;
  font-weight: bold;
  border: none;
  cursor: pointer;
}

.line-btn.primary {
  background-color: #00c300;
  color: white;
}

/* --- WeChat 风格历史工资单样式 --- */
.wechat-style .history-body {
  background-color: #f5f5f5;
}

.year-group {
  margin-bottom: 16px;
}

.year-title {
  padding: 12px 16px 8px;
  font-size: 14px;
  color: #888;
  font-weight: bold;
}

.history-list {
  background-color: #fff;
  border-top: 1px solid #e5e5e5;
  border-bottom: 1px solid #e5e5e5;
}

.history-item {
  display: flex;
  align-items: center;
  padding: 16px;
  border-bottom: 1px solid #f0f0f0;
  cursor: pointer;
}

.history-item:last-child {
  border-bottom: none;
}

.history-item:active {
  background-color: #f9f9f9;
}

.history-info {
  flex: 1;
}

.history-month {
  font-size: 16px;
  color: #333;
  margin-bottom: 4px;
}

.history-date {
  font-size: 12px;
  color: #999;
}

.history-amount {
  font-size: 16px;
  font-weight: bold;
  color: #333;
  margin-right: 12px;
}

.history-arrow {
  font-size: 16px;
  color: #ccc;
}

/* --- Line 风格历史工资单样式 --- */
.line-style .history-body {
  background-color: #f4f5f6;
  padding-bottom: 40px;
}

.line-history-list {
  padding: 0 16px;
}

.line-history-card {
  background-color: #fff;
  border-radius: 12px;
  margin-bottom: 16px;
  box-shadow: 0 2px 8px rgba(0,0,0,0.05);
  overflow: hidden;
  cursor: pointer;
  transition: transform 0.1s;
}

.line-history-card:active {
  transform: scale(0.98);
}

.line-history-card.bonus {
  border-left: 4px solid #ff9800;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 16px;
  border-bottom: 1px solid #f0f0f0;
  background-color: #fafafa;
}

.month-badge {
  font-size: 14px;
  font-weight: bold;
  color: #111;
}

.bonus-badge {
  color: #ff9800;
}

.pay-date {
  font-size: 12px;
  color: #888;
}

.line-history-card .card-body {
  padding: 16px;
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.line-history-card .amount-label {
  font-size: 13px;
  color: #666;
}

.line-history-card .amount-value {
  font-size: 20px;
  font-weight: bold;
  color: #00c300;
}

.line-history-card.bonus .amount-value {
  color: #ff9800;
}

.line-history-card .card-footer {
  padding: 12px 16px;
  font-size: 13px;
  color: #00c300;
  text-align: right;
  border-top: 1px solid #f0f0f0;
}

.line-history-card.bonus .card-footer {
  color: #ff9800;
}

.line-history-card .arrow {
  margin-left: 4px;
}

/* --- Timesheet Shared Styles --- */
.timesheet-body {
  padding: 0;
  background-color: #f5f5f5;
}

.ts-month-header {
  background: linear-gradient(135deg, #4a90d9, #357abd);
  padding: 20px 16px;
  color: white;
  text-align: center;
}

.ts-month-title {
  font-size: 18px;
  font-weight: bold;
  margin-bottom: 6px;
}

.ts-client-info {
  font-size: 13px;
  opacity: 0.9;
  margin-bottom: 10px;
}

.ts-status-badge {
  display: inline-block;
  padding: 4px 16px;
  border-radius: 12px;
  font-size: 12px;
  font-weight: bold;
}

.ts-status-badge.not-submitted {
  background-color: rgba(255,255,255,0.2);
  color: #ffcc00;
  border: 1px solid #ffcc00;
}

.ts-status-badge.submitted {
  background-color: rgba(255,255,255,0.2);
  color: #90ee90;
  border: 1px solid #90ee90;
}

.ts-day-list {
  padding: 8px 12px;
}

.ts-day-row {
  display: flex;
  align-items: center;
  padding: 8px 10px;
  margin-bottom: 2px;
  background-color: #fff;
  border-radius: 6px;
  font-size: 13px;
}

.ts-day-row.weekend {
  background-color: #f9f0f0;
  opacity: 0.7;
}

.ts-day-row.filled {
  border-left: 3px solid #4a90d9;
}

.ts-day-date {
  width: 60px;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: 6px;
}

.ts-day-date .date {
  font-weight: 500;
}

.ts-day-date .dow {
  font-size: 11px;
  color: #888;
}

.ts-day-date .dow.sat {
  color: #4a90d9;
}

.ts-day-date .dow.sun {
  color: #e53935;
}

.ts-day-times {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 4px;
}

.time-input {
  width: 70px;
  padding: 4px 4px;
  border: 1px solid #ddd;
  border-radius: 4px;
  font-size: 12px;
  text-align: center;
  background-color: #fafafa;
}

.time-input:focus {
  border-color: #4a90d9;
  outline: none;
}

.time-input:disabled {
  background-color: #f0f0f0;
  color: #999;
}

.time-sep {
  font-size: 12px;
  color: #999;
}

.rest-label {
  font-size: 12px;
  color: #ccc;
}

.ts-day-hours {
  width: 40px;
  text-align: right;
  font-size: 12px;
  color: #666;
  flex-shrink: 0;
}

.ts-summary {
  margin: 12px;
  background-color: #fff;
  border-radius: 8px;
  padding: 12px 16px;
}

.ts-summary-row {
  display: flex;
  justify-content: space-between;
  padding: 6px 0;
  font-size: 14px;
  color: #333;
}

.ts-actions {
  padding: 12px 16px 24px;
  display: flex;
  gap: 12px;
}

.ts-btn {
  flex: 1;
  padding: 12px;
  border-radius: 8px;
  font-size: 15px;
  font-weight: bold;
  border: none;
  cursor: pointer;
}

.ts-btn.save {
  background-color: #f0f0f0;
  color: #333;
}

.ts-btn.submit {
  background-color: #4a90d9;
  color: white;
}

.ts-btn:active {
  opacity: 0.8;
}

.ts-submitted-msg {
  width: 100%;
  text-align: center;
  padding: 16px;
  font-size: 15px;
  font-weight: bold;
  color: #4a90d9;
  background-color: #e8f4fd;
  border-radius: 8px;
}

/* --- LINE Timesheet Styles --- */
.line-theme .liff-header + .ts-status-card {
  margin: 0;
}

.ts-status-card {
  margin: 0 16px 12px;
  padding: 10px 16px;
  border-radius: 8px;
  font-size: 13px;
  font-weight: bold;
  text-align: center;
  background-color: #fff3e0;
  color: #e65100;
}

.ts-status-card.submitted {
  background-color: #e8f5e9;
  color: #2e7d32;
}

.ts-day-list.line-list {
  padding: 0 12px;
}

.ts-day-card {
  display: flex;
  align-items: center;
  padding: 10px 12px;
  margin-bottom: 4px;
  background-color: #fff;
  border-radius: 10px;
  font-size: 13px;
  box-shadow: 0 1px 3px rgba(0,0,0,0.04);
}

.ts-day-card.weekend {
  background-color: #fafafa;
  opacity: 0.6;
}

.ts-day-left {
  width: 60px;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: 6px;
}

.ts-day-left .date {
  font-weight: 600;
}

.ts-day-left .dow {
  font-size: 11px;
  color: #888;
}

.ts-day-left .dow.sat {
  color: #1e88e5;
}

.ts-day-left .dow.sun {
  color: #e53935;
}

.ts-day-center {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 4px;
}

.line-time-input {
  width: 70px;
  padding: 5px 4px;
  border: 1px solid #e0e0e0;
  border-radius: 8px;
  font-size: 12px;
  text-align: center;
  background-color: #fafafa;
}

.line-time-input:focus {
  border-color: #00c300;
  outline: none;
}

.line-time-input:disabled {
  background-color: #f0f0f0;
  color: #999;
}

.ts-day-right {
  width: 40px;
  text-align: right;
  font-size: 12px;
  color: #666;
  flex-shrink: 0;
}

.ts-line-summary {
  margin: 12px 16px;
}

.ts-line-summary .total-val {
  color: #00c300;
  font-weight: bold;
}

.line-btn.secondary {
  background-color: #f0f0f0;
  color: #333;
  margin-bottom: 8px;
}

.line-submitted {
  color: #00c300 !important;
  background-color: #e8f5e9 !important;
}
</style>
