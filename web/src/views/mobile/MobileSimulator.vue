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
import { ref, onMounted, onUnmounted } from 'vue'
import WechatUI from './WechatUI.vue'
import LineUI from './LineUI.vue'

const currentTime = ref('12:00')
const theme = ref('light')

const currentWechatView = ref('chat')
const currentLineView = ref('chat')

const getDetailTitle = (view: string) => {
  switch(view) {
    case 'payslip': return '給与明細'
    case 'timesheet': return '勤怠入力'
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
</style>
