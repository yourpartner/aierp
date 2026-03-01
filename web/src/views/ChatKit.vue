<template>
  <div class="chatkit-wrap">
    <aside class="sidebar" @click.capture="onSidebarClick">
      <div class="sidebar-header">
        <div class="brand">
          <img src="/sfin-logo.png" alt="iTBank Sfin" class="brand-logo" />
        </div>
      </div>
      <div class="sidebar-scroll">
        <div class="section" v-if="showSessionNav">
          <div class="section-title">{{ text.nav.chat }}</div>
          <el-menu class="menu" :default-active="activeSessionId" @select="onSelectSession">
            <el-menu-item v-for="s in sessions" :key="safeSessionId(s)" :index="safeSessionId(s)">{{ s.title || (safeSessionId(s).slice(0,8) || text.nav.chat) }}</el-menu-item>
          </el-menu>
          <div class="session-actions">
            <el-button size="small" @click="newSession">{{ text.nav.newSession }}</el-button>
          </div>
        </div>
        <!-- 动态菜单（基于 permission_menus 数据库表，按角色配置显示） -->
        <template v-for="group in accessibleMenuGroups" :key="group.moduleCode">
          <div v-if="group.items.length > 0" class="section">
            <div class="section-title">{{ getModuleGroupLabel(group.moduleCode) }}</div>
            <el-menu class="menu">
              <el-menu-item
                v-for="item in group.items"
                :key="item.key"
                :index="item.key"
                @click="onDbMenuSelect(item)"
              >
                {{ getMenuItemLabel(item) }}
              </el-menu-item>
            </el-menu>
          </div>
        </template>
      </div>
    </aside>
    <main class="main">
      <header class="main-header">
        <div class="header-left">
          <div class="page-title">{{ text.chat.aiTitle }}</div>
        </div>
        <div class="header-right">
          <el-select v-model="langValue" size="small" class="lang-select">
            <el-option v-for="opt in langOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
          </el-select>
          <div class="profile-box">
            <div class="company-info">
              <div class="company-badge">{{ profile.company }}</div>
              <div class="company-name-sub">{{ companyDisplayName }}</div>
            </div>
            <div class="user-chip">
              <div class="user-meta">
                <div class="user-name">{{ profile.name }}</div>
                <el-button type="danger" size="small" plain @click="handleLogout">
                  {{ text.common?.logout || 'ログアウト' }}
                </el-button>
              </div>
            </div>
          </div>
        </div>
      </header>
      <section class="workspace" :class="{ withDock: modalOpen }">
        <aside class="task-panel" v-if="hasAnyTasks">
          <div class="task-panel-header">
            <div class="task-panel-title">{{ taskPanelTitle }}</div>
          </div>
          <div class="task-panel-body">
            <div
              v-for="task in pendingTasks"
              :key="task.id"
              class="task-panel-item"
              :class="[
                `kind-${task.kind}`,
                {
                active: task.id === activeTaskId,
                completed: task.status === 'completed',
                failed: task.status === 'failed',
                error: task.status === 'error',
                cancelled: task.status === 'cancelled'
                }
              ]"
              @click="selectTask(task.id)"
            >
              <div class="task-item-header">
                <div class="task-header-info">
                  <span class="task-badge" v-if="task.label">{{ task.label }}</span>
                  <span class="task-name">{{ task.title }}</span>
                </div>
              </div>
              <div class="task-item-meta">
                <el-tag size="small" :type="taskStatusType(task.status)">{{ taskStatusLabel(task.status) }}</el-tag>
                <span class="task-item-summary" v-if="task.summary">{{ task.summary }}</span>
              </div>
            </div>
            <div class="task-panel-completed" v-if="completedTasks.length">
              <div class="task-panel-completed-header" @click="toggleCompletedTasks">
                <el-icon class="task-completed-icon">
                  <component :is="showCompletedTasks ? ArrowDown : ArrowRight" />
                </el-icon>
                <span>{{ completedTasksHeaderText }}</span>
              </div>
              <transition name="fade">
                <div v-show="showCompletedTasks" class="task-panel-completed-list">
                  <div
                    v-for="task in completedTasks"
                    :key="task.id"
                    class="task-panel-item"
                    :class="[
                      `kind-${task.kind}`,
                      {
                      active: task.id === activeTaskId,
                      completed: task.status === 'completed',
                      failed: task.status === 'failed',
                      error: task.status === 'error',
                      cancelled: task.status === 'cancelled'
                      }
                    ]"
                    @click="selectTask(task.id)"
                  >
                    <div class="task-item-header">
                      <div class="task-header-info">
                        <span class="task-badge" v-if="task.label">{{ task.label }}</span>
                        <span class="task-name">{{ task.title }}</span>
                      </div>
                    </div>
                    <div class="task-item-meta">
                      <el-tag size="small" :type="taskStatusType(task.status)">{{ taskStatusLabel(task.status) }}</el-tag>
                      <span class="task-item-summary" v-if="task.summary">{{ task.summary }}</span>
                    </div>
                  </div>
                </div>
              </transition>
            </div>
          </div>
        </aside>
        <section class="chat-card">
          <div class="chat-content" ref="chatBoxRef" @scroll.passive="onChatScroll">
            <div v-if="timelineMessages.length" class="timeline-messages">
              <div class="timeline-header" :class="{ active: generalModeActive }">{{ text.chat.generalTimelineTitle || timelineTitleFallback }}</div>
              <div
                v-for="(m,i) in timelineMessages"
                :key="`timeline-${i}`"
                class="msg"
                :class="[m.role, m.status ? `status-${m.status}` : '']"
              >
                <div class="bubble">
                  <div
                    v-if="isClarificationMessage(m)"
                    class="clarify-card"
                    :class="{
                      answered: clarificationAnsweredAt(m),
                      active: activeClarificationId === (m.tag && m.tag.questionId)
                    }"
                  >
                    <div class="clarify-question">{{ clarificationQuestion(m) }}</div>
                    <div class="clarify-meta" v-if="clarificationLabel(m)">
                      <span class="clarify-label">[{{ clarificationLabel(m) }}]</span>
            </div>
                    <div class="clarify-detail" v-if="clarificationDetail(m)">{{ clarificationDetail(m) }}</div>
                    <div class="clarify-answer" v-if="clarificationAnswers(m).length">
                      <div class="clarify-answer-label">{{ clarifyAnswerLabel }}</div>
                      <div
                        class="clarify-answer-item"
                        v-for="(answer, idx) in clarificationAnswers(m)"
                        :key="`timeline-answer-${m?.tag?.questionId || 'clarify'}-${idx}`"
                      >
                        <div class="clarify-answer-content">
                          {{ answer.content }}
                          <span class="clarify-answer-pending" v-if="answer.pending">
                            <el-icon class="clarify-loading-icon"><Loading /></el-icon>
                            <span>{{ clarifyPendingText }}</span>
                          </span>
                        </div>
                      </div>
          </div>
                    <div class="clarify-actions">
                      <el-tag size="small" type="success" v-if="clarificationAnsweredAt(m)">{{ clarifyAnsweredLabel }}</el-tag>
                      <el-button
                        size="small"
                        type="primary"
                        :disabled="Boolean(clarificationAnsweredAt(m))"
                        @click="replyClarification(m)"
                      >
                        {{ clarifyReplyLabel }}
                      </el-button>
                    </div>
                  </div>
                  <div v-else-if="m.content" class="bubble-text">{{ m.content }}</div>
                  <div v-if="getMessageAttachments(m).length" class="bubble-attachments">
                    <template v-for="att in getMessageAttachments(m)" :key="attachmentKey(att)">
                      <div
                        v-if="!isImageAttachment(att)"
                        class="message-tile file"
                        role="button"
                        tabindex="0"
                        @click.prevent="openFilePreview(att)"
                        @keydown.enter.prevent="openFilePreview(att)"
                      >
                        <div class="tile-thumb">
                          <el-icon><Document /></el-icon>
                        </div>
                        <div class="tile-name" :title="att.name || att.fileName || fallbackFileLabel">
                          {{ att.name || att.fileName || fallbackFileLabel }}
                        </div>
                        <div class="tile-meta">{{ formatAttachmentMeta(att) }}</div>
                      </div>
                      <div
                        v-else
                        class="message-tile image"
                        role="button"
                        tabindex="0"
                        @click.prevent="openImagePreview(att)"
                        @keydown.enter.prevent="openImagePreview(att)"
                      >
                        <div class="tile-thumb">
                          <img :src="att.previewUrl || att.url" :alt="att.name || att.fileName || fallbackImageLabel" />
                        </div>
                        <div class="tile-name" :title="att.name || att.fileName || fallbackImageLabel">
                          {{ att.name || att.fileName || fallbackImageLabel }}
                        </div>
                        <div class="tile-meta">{{ formatAttachmentMeta(att) }}</div>
                      </div>
                    </template>
                  </div>
                  <el-tag
                    v-if="m.tag"
                    size="small"
                    class="msg-tag"
                    :type="tagType(m.status)"
                    @click.stop="onMessageTagClick(m.tag)"
                  >
                    {{ m.tag.label }}
                  </el-tag>
                </div>
              </div>
            </div>
            <div
              v-for="section in visibleTaskSections"
              :key="section.id"
              class="task-conversation"
              :class="{ active: section.id === activeTaskId }"
              :ref="el => setTaskSectionRef(section.id, el)"
              @click="selectTask(section.id)"
            >
              <div class="task-conversation-header">
                <div class="task-header-main">
                  <span class="task-badge" v-if="section.label">{{ section.label }}</span>
                  <span class="task-conversation-name">{{ section.title }}</span>
                </div>
                <div class="task-header-actions">
                  <el-tag size="small" :type="taskStatusType(section.status)">{{ taskStatusLabel(section.status) }}</el-tag>
                  <el-button
                    v-if="section.kind === 'invoice' && canRetrySection(section)"
                    link
                    size="small"
                    type="primary"
                    :icon="Refresh"
                    :loading="retryingTaskId === section.invoiceTask.id"
                    @click.stop="retryInvoiceTask(section.invoiceTask)"
                    class="task-retry-btn"
                  >
                    {{ localize('再試行', '重试', 'Retry') }}
                  </el-button>
                  <el-button
                    v-if="section.kind === 'invoice' && section.invoiceTask && canCancelTask(section.invoiceTask)"
                    link
                    size="small"
                    type="danger"
                    :icon="Delete"
                    @click.stop="confirmCancelTask(section.invoiceTask)"
                    class="task-delete-btn"
                  >
                    {{ text.common.delete }}
                  </el-button>
                </div>
              </div>

              <template v-if="section.kind === 'invoice'">
                <div class="task-conversation-summary" v-if="section.summary">{{ section.summary }}</div>
                <div class="task-conversation-attachments" v-if="section.invoiceTask && taskAttachments[section.id]">
                  <div
                    v-if="isImageAttachment(taskAttachments[section.id])"
                    class="message-tile image"
                    role="button"
                    tabindex="0"
                    @click.prevent="openImagePreview(taskAttachments[section.id])"
                    @keydown.enter.prevent="openImagePreview(taskAttachments[section.id])"
                  >
                    <div class="tile-thumb">
                      <img
                        :src="taskAttachments[section.id].previewUrl || taskAttachments[section.id].url"
                        :alt="taskAttachments[section.id].name || taskAttachments[section.id].fileName || fallbackImageLabel"
                      />
                    </div>
                    <div class="tile-name" :title="taskAttachments[section.id].name || taskAttachments[section.id].fileName || fallbackImageLabel">
                      {{ taskAttachments[section.id].name || taskAttachments[section.id].fileName || fallbackImageLabel }}
                    </div>
                    <div class="tile-meta">{{ formatAttachmentMeta(taskAttachments[section.id]) }}</div>
                  </div>
                  <div
                    v-else
                    class="message-tile file"
                    role="button"
                    tabindex="0"
                    @click.prevent="openFilePreview(taskAttachments[section.id])"
                    @keydown.enter.prevent="openFilePreview(taskAttachments[section.id])"
                  >
                    <div class="tile-thumb">
                      <el-icon><Document /></el-icon>
                    </div>
                    <div class="tile-name" :title="taskAttachments[section.id].name || taskAttachments[section.id].fileName || fallbackFileLabel">
                      {{ taskAttachments[section.id].name || taskAttachments[section.id].fileName || fallbackFileLabel }}
                    </div>
                    <div class="tile-meta">{{ formatAttachmentMeta(taskAttachments[section.id]) }}</div>
                  </div>
                </div>
                <div class="task-conversation-messages">
                  <div
                    v-for="(m,i) in section.messages"
                    :key="`${section.id}-${i}`"
                    class="msg"
                    :class="[m.role, m.status ? `status-${m.status}` : '']"
                  >
                    <div class="bubble">
                      <div
                        v-if="isClarificationMessage(m)"
                        class="clarify-card"
                        :class="{
                          answered: clarificationAnsweredAt(m),
                          active: activeClarificationId === (m.tag && m.tag.questionId)
                        }"
                      >
                        <div class="clarify-question">{{ clarificationQuestion(m) }}</div>
                        <div class="clarify-meta" v-if="clarificationLabel(m)">
                          <span class="clarify-label">[{{ clarificationLabel(m) }}]</span>
                        </div>
                        <div class="clarify-detail" v-if="clarificationDetail(m)">{{ clarificationDetail(m) }}</div>
                        <div class="clarify-answer" v-if="clarificationAnswers(m).length">
                          <div class="clarify-answer-label">{{ clarifyAnswerLabel }}</div>
                          <div
                            class="clarify-answer-item"
                            v-for="(answer, idx) in clarificationAnswers(m)"
                            :key="`task-answer-${m?.tag?.questionId || 'clarify'}-${idx}`"
                          >
                            <div class="clarify-answer-content">
                              {{ answer.content }}
                              <span class="clarify-answer-pending" v-if="answer.pending">
                                <el-icon class="clarify-loading-icon"><Loading /></el-icon>
                                <span>{{ clarifyPendingText }}</span>
                              </span>
                            </div>
                          </div>
                        </div>
                        <div class="clarify-actions">
                        <el-tag size="small" type="success" v-if="clarificationAnsweredAt(m)">{{ clarifyAnsweredLabel }}</el-tag>
                          <el-button
                            size="small"
                            type="primary"
                            :disabled="Boolean(clarificationAnsweredAt(m))"
                            @click="replyClarification(m)"
                          >
                          {{ clarifyReplyLabel }}
                          </el-button>
                        </div>
                      </div>
                      <SalesChartMessage
                        v-else-if="isSalesChartMessage(m)"
                        :echarts-config="m.tag?.echartsConfig"
                        :chart-title="m.tag?.chartTitle"
                        :explanation="m.content"
                        :data="m.tag?.data"
                        :sql="m.tag?.sql"
                        :chart-height="320"
                      />
                      <div v-else-if="m.content" class="bubble-text">{{ m.content }}</div>
                      <div v-if="section.invoiceTask && getMessageAttachments(m, section.invoiceTask).length" class="bubble-attachments">
                        <template v-for="att in getMessageAttachments(m, section.invoiceTask)" :key="attachmentKey(att)">
                          <div
                            v-if="!isImageAttachment(att)"
                            class="message-tile file"
                            role="button"
                            tabindex="0"
                            @click.prevent="openFilePreview(att)"
                            @keydown.enter.prevent="openFilePreview(att)"
                          >
                            <div class="tile-thumb">
                              <el-icon><Document /></el-icon>
                            </div>
                            <div class="tile-name" :title="att.name || att.fileName || fallbackFileLabel">
                              {{ att.name || att.fileName || fallbackFileLabel }}
                            </div>
                            <div class="tile-meta">{{ formatAttachmentMeta(att) }}</div>
                          </div>
                          <div
                            v-else
                            class="message-tile image"
                            role="button"
                            tabindex="0"
                            @click.prevent="openImagePreview(att)"
                            @keydown.enter.prevent="openImagePreview(att)"
                          >
                            <div class="tile-thumb">
                              <img :src="att.previewUrl || att.url" :alt="att.name || att.fileName || fallbackImageLabel" />
                            </div>
                            <div class="tile-name" :title="att.name || att.fileName || fallbackImageLabel">
                              {{ att.name || att.fileName || fallbackImageLabel }}
                            </div>
                            <div class="tile-meta">{{ formatAttachmentMeta(att) }}</div>
                          </div>
                        </template>
                      </div>
                      <el-tag
                        v-if="m.tag"
                        size="small"
                        class="msg-tag"
                        :type="tagType(m.status)"
                        @click.stop="onMessageTagClick(m.tag)"
                      >
                        {{ m.tag.label }}
                      </el-tag>
                    </div>
                  </div>
                  <div v-if="section.messages.length === 0" class="task-empty">{{ emptyTaskText }}</div>
                </div>
              </template>

              <template v-else-if="section.kind === 'sales_order'">
                <div class="sales-order-conversation">
                  <div class="task-conversation-summary" v-if="section.summary">{{ section.summary }}</div>
                  <div class="task-conversation-messages">
                    <div
                      v-for="(m,i) in section.messages"
                      :key="`${section.id}-${i}`"
                      class="msg"
                      :class="[m.role, m.status ? `status-${m.status}` : '']"
                    >
                      <div class="bubble">
                        <div
                          v-if="isClarificationMessage(m)"
                          class="clarify-card"
                          :class="{
                            answered: clarificationAnsweredAt(m),
                            active: activeClarificationId === (m.tag && m.tag.questionId)
                          }"
                        >
                          <div class="clarify-question">{{ clarificationQuestion(m) }}</div>
                          <div class="clarify-meta" v-if="clarificationLabel(m)">
                            <span class="clarify-label">[{{ clarificationLabel(m) }}]</span>
                          </div>
                          <div class="clarify-detail" v-if="clarificationDetail(m)">{{ clarificationDetail(m) }}</div>
                          <div class="clarify-actions">
                            <el-tag size="small" type="success" v-if="clarificationAnsweredAt(m)">{{ clarifyAnsweredLabel }}</el-tag>
                            <el-button
                              size="small"
                              type="primary"
                              :disabled="Boolean(clarificationAnsweredAt(m))"
                              @click="replyClarification(m)"
                            >
                              {{ clarifyReplyLabel }}
                            </el-button>
                          </div>
                        </div>
                        <div v-else-if="m.content" class="bubble-text">{{ m.content }}</div>
                        <div v-if="getMessageAttachments(m).length" class="bubble-attachments">
                          <template v-for="att in getMessageAttachments(m)" :key="attachmentKey(att)">
                            <div
                              v-if="!isImageAttachment(att)"
                              class="message-tile file"
                              role="button"
                              tabindex="0"
                              @click.prevent="openFilePreview(att)"
                              @keydown.enter.prevent="openFilePreview(att)"
                            >
                              <div class="tile-thumb">
                                <el-icon><Document /></el-icon>
                              </div>
                              <div class="tile-name" :title="att.name || att.fileName || fallbackFileLabel">
                                {{ att.name || att.fileName || fallbackFileLabel }}
                              </div>
                              <div class="tile-meta">{{ formatAttachmentMeta(att) }}</div>
                            </div>
                            <div
                              v-else
                              class="message-tile image"
                              role="button"
                              tabindex="0"
                              @click.prevent="openImagePreview(att)"
                              @keydown.enter.prevent="openImagePreview(att)"
                            >
                              <div class="tile-thumb">
                                <img :src="att.previewUrl || att.url" :alt="att.name || att.fileName || fallbackImageLabel" />
                              </div>
                              <div class="tile-name" :title="att.name || att.fileName || fallbackImageLabel">
                                {{ att.name || att.fileName || fallbackImageLabel }}
                              </div>
                              <div class="tile-meta">{{ formatAttachmentMeta(att) }}</div>
                            </div>
                          </template>
                        </div>
                        <el-tag
                          v-if="m.tag"
                          size="small"
                          class="msg-tag"
                          :type="tagType(m.status)"
                          @click.stop="onMessageTagClick(m.tag)"
                        >
                          {{ m.tag.label }}
                        </el-tag>
                      </div>
                    </div>
                    <div v-if="section.messages.length === 0" class="task-empty">{{ emptyTaskText }}</div>
                  </div>
                </div>
              </template>

              <template v-else>
                <div class="approval-conversation">
                  <div class="approval-conversation-row" v-if="section.approvalTask?.stepName">
                    <span class="approval-conversation-label">{{ text.chat.approvalStepLabel }}</span>
                    <span class="approval-conversation-value">{{ section.approvalTask?.stepName }}</span>
                  </div>
                  <div
                    class="approval-conversation-row"
                    v-if="section.approvalTask?.entity !== 'certificate_request' && formatApprovalApplicant(section.approvalTask)"
                  >
                    <span class="approval-conversation-label">{{ text.chat.approvalApplicantLabel }}</span>
                    <span class="approval-conversation-value">{{ formatApprovalApplicant(section.approvalTask) }}</span>
                  </div>
                  <template v-else-if="section.approvalTask?.entity === 'certificate_request'">
                    <div class="approval-conversation-row" v-if="certificateApplicantName(section.approvalTask)">
                      <span class="approval-conversation-label">{{ text.chat.approvalApplicantNameLabel }}</span>
                      <span class="approval-conversation-value">{{ certificateApplicantName(section.approvalTask) }}</span>
                    </div>
                    <div class="approval-conversation-row" v-if="certificateApplicantCode(section.approvalTask)">
                      <span class="approval-conversation-label">{{ text.chat.approvalApplicantCodeLabel }}</span>
                      <span class="approval-conversation-value">{{ certificateApplicantCode(section.approvalTask) }}</span>
                    </div>
                    <div class="approval-conversation-row" v-if="certificateApplicantNote(section.approvalTask)">
                      <span class="approval-conversation-label">{{ text.chat.approvalApplicantNoteLabel }}</span>
                      <span class="approval-conversation-value multiline">{{ certificateApplicantNote(section.approvalTask) }}</span>
                    </div>
                    <div class="approval-conversation-row" v-if="certificateApplicantResignReason(section.approvalTask)">
                      <span class="approval-conversation-label">{{ text.chat.approvalApplicantResignReasonLabel }}</span>
                      <span class="approval-conversation-value multiline">{{ certificateApplicantResignReason(section.approvalTask) }}</span>
                    </div>
                  </template>
                  <template v-else-if="section.approvalTask?.entity === 'timesheet_submission'">
                    <div class="approval-conversation-row">
                      <span class="approval-conversation-label">種類</span>
                      <span class="approval-conversation-value">勤怠承認</span>
                    </div>
                    <div class="approval-conversation-row" v-if="timesheetApplicantName(section.approvalTask)">
                      <span class="approval-conversation-label">申請者</span>
                      <span class="approval-conversation-value">{{ timesheetApplicantName(section.approvalTask) }}</span>
                    </div>
                    <div class="approval-conversation-row" v-if="timesheetMonth(section.approvalTask)">
                      <span class="approval-conversation-label">対象月</span>
                      <span class="approval-conversation-value">{{ timesheetMonth(section.approvalTask) }}</span>
                    </div>
                    <div class="approval-conversation-row" v-if="timesheetTotalHours(section.approvalTask)">
                      <span class="approval-conversation-label">総勤務時間</span>
                      <span class="approval-conversation-value">{{ timesheetTotalHours(section.approvalTask) }}</span>
                    </div>
                    <div class="approval-conversation-row" v-if="timesheetTotalOvertime(section.approvalTask)">
                      <span class="approval-conversation-label">総残業時間</span>
                      <span class="approval-conversation-value">{{ timesheetTotalOvertime(section.approvalTask) }}</span>
                    </div>
                    <div class="approval-conversation-row" v-if="timesheetWorkDays(section.approvalTask)">
                      <span class="approval-conversation-label">勤務日数</span>
                      <span class="approval-conversation-value">{{ timesheetWorkDays(section.approvalTask) }}</span>
                    </div>
                  </template>
                  <div class="approval-conversation-row" v-if="formatApprovalUser(section.approvalTask)">
                    <span class="approval-conversation-label">{{ text.chat.approvalUserLabel }}</span>
                    <span class="approval-conversation-value">{{ formatApprovalUser(section.approvalTask) }}</span>
                  </div>
                  <div class="approval-conversation-row">
                    <span class="approval-conversation-label">{{ text.chat.approvalCreatedAtLabel }}</span>
                    <span class="approval-conversation-value">{{ formatApprovalDate(section.approvalTask?.createdAt || '') }}</span>
                  </div>
                  <div class="approval-conversation-summary" v-if="section.summary">{{ section.summary }}</div>
                  <div class="approval-conversation-row" v-if="formatApprovalRemark(section.approvalTask)">
                    <span class="approval-conversation-label">{{ text.chat.approvalRemarkLabel }}</span>
                    <span class="approval-conversation-value multiline">{{ formatApprovalRemark(section.approvalTask) }}</span>
                  </div>
                  <div class="approval-conversation-actions">
                    <!-- 銀行自動記帳タスク専用ボタン -->
                    <template v-if="section.approvalTask?.entity === 'moneytree_posting'">
                      <el-button
                        type="primary"
                        size="small"
                        :disabled="!section.approvalTask"
                        :loading="section.approvalTask && bankAiSupportLoadingId === section.approvalTask.id"
                        @click="section.approvalTask && startBankAiSupport(section.approvalTask)"
                      >
                        <el-icon style="margin-right:4px"><ChatDotRound /></el-icon>
                        AI記帳サポート
                      </el-button>
                      <el-button
                        size="small"
                        type="info"
                        :disabled="!section.approvalTask"
                        @click="section.approvalTask && openBankTransactionsList(section.approvalTask)"
                      >
                        明細一覧
                      </el-button>
                    </template>
                    <el-button
                      v-if="section.approvalTask?.entity === 'timesheet_submission'"
                      size="small"
                      type="info"
                      :disabled="!section.approvalTask"
                      @click="section.approvalTask && openTimesheetDetail(section.approvalTask)"
                    >
                      詳細
                    </el-button>
                    <el-button
                      type="primary"
                      size="small"
                      :disabled="!section.approvalTask"
                      :loading="section.approvalTask && approvalActionLoadingId === section.approvalTask.id"
                      @click="section.approvalTask && approveApprovalTask(section.approvalTask)"
                    >
                      {{ text.chat.approvalApprove }}
                    </el-button>
                    <el-button
                      type="danger"
                      size="small"
                      :disabled="!section.approvalTask"
                      :loading="section.approvalTask && approvalActionLoadingId === section.approvalTask.id"
                      @click="section.approvalTask && rejectApprovalTask(section.approvalTask)"
                    >
                      {{ text.chat.approvalReject }}
                    </el-button>
                    <el-button
                      v-if="section.approvalTask?.entity === 'certificate_request'"
                      size="small"
                      type="info"
                      :disabled="!section.approvalTask"
                      :loading="section.approvalTask && approvalDownloadLoadingId === section.approvalTask.id"
                      @click="section.approvalTask && downloadApprovalAttachment(section.approvalTask)"
                    >
                      {{ text.chat.approvalDownload }}
                    </el-button>
                  </div>
                </div>
              </template>
            </div>

            <div v-if="timelineMessages.length === 0 && taskSections.length === 0" class="empty">{{ text.chat.empty }}</div>
          </div>
          <div class="chat-input" @dragover.prevent="onChatDragOver" @drop.prevent="onChatDrop" @dragleave.prevent="onChatDragLeave">
            <div class="input-shell" :class="{ 'drag-hover': isDragOver }">
              <div v-if="activeTaskContext && !activeClarification" class="task-context-banner">
                <div class="task-context-info">
                  <span class="task-context-icon">💬</span>
                  <span class="task-context-label">{{ activeTaskContext.label }}</span>
                  <span class="task-context-name">{{ activeTaskContext.title }}</span>
                  <el-tag v-if="activeTaskContext.voucherNo" size="small" type="success" style="margin-left:6px">{{ activeTaskContext.voucherNo }}</el-tag>
                </div>
                <el-button text size="small" @click="enterGeneralMode">{{ localize('一般会話へ', '切换到通用对话', 'General chat') }}</el-button>
              </div>
              <div v-if="activeClarification" class="clarify-banner">
                <div class="clarify-banner-text">
          <span v-if="activeClarification?.documentLabel" class="clarify-banner-label">[{{ activeClarification.documentLabel }}]</span>
                {{ clarifyAnsweringPrefix }}{{ activeClarification.question }}
                <span v-if="activeClarification.documentName || activeClarification.documentId">
                  {{ clarificationBannerFile(activeClarification) }}
                </span>
                </div>
                <el-button text size="small" @click="cancelClarification">{{ text.common.cancel }}</el-button>
              </div>
              <div v-if="attachments.length" class="input-attachments">
                <div
                  v-for="att in attachments"
                  :key="att.id"
                  class="attachment-tile"
                  :class="[att.status, isImageFile(att) ? 'image' : 'file']"
                  @click.stop
                >
                  <div class="tile-thumb">
                    <img v-if="isImageFile(att)" :src="attachmentThumbnail(att)" :alt="att.name" />
                    <div v-else class="tile-icon">
                      <el-icon><Document /></el-icon>
                    </div>
                  </div>
                  <button
                    type="button"
                    class="tile-remove"
                    :disabled="sending || att.status==='uploading'"
                    @click.stop="removeAttachment(att.id)"
                  >
                    <el-icon><Close /></el-icon>
                  </button>
                  <div class="tile-status">{{ attachmentStatus(att) }}</div>
                  <div class="tile-name" :title="att.name">{{ att.name }}</div>
                </div>
              </div>
              <div class="input-row">
                <button
                  type="button"
                  class="attach-trigger"
                  :disabled="sending || attachments.length >= maxAttachmentCount"
                  @click.stop="triggerFileDialog"
                >
                  <el-icon><Plus /></el-icon>
                </button>
                <el-input
                  ref="chatInputRef"
                  v-model="input"
                  type="textarea"
                  :rows="2"
                  :placeholder="chatPlaceholder"
                  class="chat-textarea"
                />
                <el-button
                  class="chat-send-button"
                  type="primary"
                  :loading="sending"
                  @click="send"
                >
                  {{ text.chat.send }}
                </el-button>
              </div>
              <input ref="fileInputRef" class="chat-file-input" type="file" multiple @change="onFileChange" />
            </div>
          </div>
        </section>
        <el-dialog v-model="modalOpen" :title="modal.title" width="auto" append-to-body destroy-on-close @closed="onModalClosed" class="embed-dialog" :close-on-click-modal="true">
          <template #header></template>
          <component v-if="modal.key" :is="resolveComp(modal.key)" :key="modal.renderKey" ref="modalRef" @done="onModalDone" />
        </el-dialog>
        <el-dialog
          v-model="imagePreview.visible"
          :title="imagePreview.name || imagePreviewTitle"
          width="auto"
          append-to-body
          destroy-on-close
          class="image-preview-dialog"
        >
          <img v-if="imagePreview.url" :src="imagePreview.url" :alt="imagePreview.name" class="preview-image" />
        </el-dialog>
        <el-dialog
          v-model="pdfPreview.visible"
          :title="pdfPreview.name || 'PDF Preview'"
          width="fit-content"
          append-to-body
          destroy-on-close
          class="pdf-preview-dialog"
          @closed="closePdfPreview"
        >
          <div v-if="pdfPreview.loading" class="pdf-loading">
            <el-skeleton :rows="10" animated />
          </div>
          <iframe
            v-else-if="pdfPreview.url"
            :src="pdfPreview.url"
            class="pdf-preview-iframe"
            frameborder="0"
          />
        </el-dialog>
        <el-dialog
          v-model="timesheetDetailDialog.visible"
          :title="timesheetDetailDialog.title || '勤怠明細'"
          width="900px"
          append-to-body
          destroy-on-close
          class="timesheet-detail-dialog"
          @closed="closeTimesheetDetail"
        >
          <el-skeleton :loading="timesheetDetailDialog.loading" :rows="8" animated />
          <template v-if="!timesheetDetailDialog.loading">
            <div v-if="timesheetDetailDialog.error" class="timesheet-detail-error">{{ timesheetDetailDialog.error }}</div>
            <div v-else class="timesheet-detail-content">
              <el-descriptions :column="3" border size="small" class="timesheet-detail-meta">
                <el-descriptions-item label="申請者">{{ timesheetDetailDialog.applicant || '-' }}</el-descriptions-item>
                <el-descriptions-item label="対象月">{{ timesheetDetailDialog.month || '-' }}</el-descriptions-item>
                <el-descriptions-item label="勤務日数">{{ timesheetDetailDialog.workDaysText || '-' }}</el-descriptions-item>
                <el-descriptions-item label="総勤務時間">{{ timesheetDetailDialog.totalHoursText || '-' }}</el-descriptions-item>
                <el-descriptions-item label="総残業時間">{{ timesheetDetailDialog.totalOvertimeText || '-' }}</el-descriptions-item>
                <el-descriptions-item label="ステータス">{{ timesheetDetailDialog.statusLabel || '-' }}</el-descriptions-item>
              </el-descriptions>

              <el-table :data="timesheetDetailDialog.rows" size="small" stripe style="margin-top:12px">
                <el-table-column label="日付" width="110">
                  <template #default="{ row }">
                    <span>{{ row.date }}</span>
                    <el-tag v-if="row.isHoliday" size="small" type="danger" effect="plain" style="margin-left:6px">祝</el-tag>
                  </template>
                </el-table-column>
                <el-table-column label="開始" width="90">
                  <template #default="{ row }">{{ row.startTime || '-' }}</template>
                </el-table-column>
                <el-table-column label="終了" width="90">
                  <template #default="{ row }">{{ row.endTime || '-' }}</template>
                </el-table-column>
                <el-table-column label="休憩(分)" width="90" align="right">
                  <template #default="{ row }">{{ typeof row.lunchMinutes === 'number' ? row.lunchMinutes : '-' }}</template>
                </el-table-column>
                <el-table-column label="勤務時間" width="100" align="right">
                  <template #default="{ row }">{{ formatHmHours(row.hours) }}</template>
                </el-table-column>
                <el-table-column label="残業" width="100" align="right">
                  <template #default="{ row }">{{ formatHmHours(row.overtime) }}</template>
                </el-table-column>
                <el-table-column label="作業内容" min-width="260">
                  <template #default="{ row }">{{ row.task || '' }}</template>
                </el-table-column>
                <el-table-column label="ステータス" width="100">
                  <template #default="{ row }">{{ row.status || '-' }}</template>
                </el-table-column>
              </el-table>
            </div>
          </template>
        </el-dialog>
        <el-dialog
          v-model="salesOrderDetailDialog.visible"
          :title="salesOrderDetailMeta.orderNo || text.chat.salesOrderDetailTitle || localize('受注詳細', '受注详情', 'Sales Order Detail')"
          width="900px"
          append-to-body
          destroy-on-close
          class="sales-order-detail-dialog"
        >
          <el-skeleton :loading="salesOrderDetailDialog.loading" :rows="6" animated />
          <template v-if="!salesOrderDetailDialog.loading">
            <div v-if="salesOrderDetailDialog.error" class="sales-order-detail-error">{{ salesOrderDetailDialog.error }}</div>
            <div v-else class="sales-order-detail-content">
              <el-descriptions :column="2" border class="sales-order-detail-descriptions">
                <el-descriptions-item :label="text.chat.salesOrderNoLabel">{{ salesOrderDetailMeta.orderNo }}</el-descriptions-item>
                <el-descriptions-item :label="text.chat.salesOrderCustomerLabel">
                  {{ salesOrderDetailMeta.customerName || salesOrderDetailMeta.customerCode }}
                </el-descriptions-item>
                <el-descriptions-item :label="text.chat.salesOrderAmountLabel">
                  {{ salesOrderDetailMeta.amount ? formatCurrencyAmount(salesOrderDetailMeta.amount, salesOrderDetailMeta.currency) : '' }}
                </el-descriptions-item>
                <el-descriptions-item :label="text.chat.salesOrderOrderDateLabel">{{ salesOrderDetailMeta.orderDate }}</el-descriptions-item>
                <el-descriptions-item :label="text.chat.salesOrderStatusLabel || 'ステータス'">
                  {{ salesOrderDetailDialog.task?.status || '-' }}
                </el-descriptions-item>
                <el-descriptions-item :label="text.chat.salesOrderCurrencyLabel || '通貨'">
                  {{ salesOrderDetailMeta.currency }}
                </el-descriptions-item>
                <el-descriptions-item v-if="salesOrderDetailPayload.note" :label="text.chat.salesOrderNoteLabel || '備考'" :span="2">
                  {{ salesOrderDetailPayload.note }}
                </el-descriptions-item>
              </el-descriptions>
              <div class="sales-order-lines" v-if="salesOrderDetailLinesList.length">
                <div class="sales-order-lines-title">{{ text.chat.salesOrderLinesLabel }}</div>
                <table>
                  <thead>
                    <tr>
                      <th scope="col">{{ text.chat.salesOrderLineNoLabel }}</th>
                      <th scope="col">{{ text.chat.salesOrderLineItemLabel }}</th>
                      <th scope="col">{{ text.chat.salesOrderLineQtyLabel }}</th>
                      <th scope="col">{{ text.chat.salesOrderLineUnitPriceLabel }}</th>
                      <th scope="col">{{ text.chat.salesOrderLineAmountLabel }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="line in salesOrderDetailLinesList" :key="`detail-line-${line.lineNo}`">
                      <td class="sales-order-line-no">{{ line.lineNo }}</td>
                      <td class="sales-order-line-item">
                        <div class="sales-order-line-name">
                          <span class="line-code" v-if="line.materialCode">{{ line.materialCode }}</span>
                          <span class="line-name">{{ line.materialName || '-' }}</span>
                        </div>
                        <div class="sales-order-line-desc" v-if="line.description">{{ line.description }}</div>
                        <div class="sales-order-line-note" v-if="line.note">{{ line.note }}</div>
                      </td>
                      <td class="sales-order-line-qty">{{ formatSalesOrderQuantity(line) }}</td>
                      <td class="sales-order-line-price">
                        {{ formatCurrencyAmount(line.unitPrice, salesOrderDetailCurrencyCode) }}
                      </td>
                      <td class="sales-order-line-amount">
                        {{ formatCurrencyAmount(line.amount, salesOrderDetailCurrencyCode) }}
                      </td>
                    </tr>
                  </tbody>
                </table>
              </div>
            </div>
          </template>
        </el-dialog>
      </section>
    </main>
  </div>
</template>
<script setup lang="ts">
import { onMounted, onBeforeUnmount, reactive, ref, nextTick, watch, defineAsyncComponent, defineComponent, computed, provide } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useRouter, useRoute } from 'vue-router'
import {
  listInvoiceTasks,
  type AgentTask,
  type InvoiceAgentTask,
  type SalesOrderAgentTask,
  type PayrollAgentTask
} from '../api/invoiceTasks'
import { listApprovalTasks, actApprovalTask } from '../api/approvalTasks'
import api from '../api'
import { loadEditionInfo, menuTree as editionMenuTree } from '../stores/edition'
import type { MenuTreeNode } from '../api/edition'
import { useI18n } from '../i18n'
import SalesChartMessage from '../components/SalesChartMessage.vue'
import { Document, Close, Plus, Delete, ArrowDown, ArrowRight, Loading, Refresh, ChatDotRound } from '@element-plus/icons-vue'

// 所有业务组件使用 defineAsyncComponent 按需加载，提升首次加载性能
// 财务核心
const VoucherForm = defineAsyncComponent(() => import('./VoucherForm.vue'))
const VouchersList = defineAsyncComponent(() => import('./VouchersList.vue'))
const AccountsList = defineAsyncComponent(() => import('./AccountsList.vue'))
const AccountLedger = defineAsyncComponent(() => import('./AccountLedger.vue'))
const AccountBalance = defineAsyncComponent(() => import('./AccountBalance.vue'))
const TrialBalance = defineAsyncComponent(() => import('./TrialBalance.vue'))
const LedgerExport = defineAsyncComponent(() => import('./LedgerExport.vue'))
const AccountForm = defineAsyncComponent(() => import('./AccountForm.vue'))

// 财务扩展
const FbPayment = defineAsyncComponent(() => import('./FbPayment.vue'))
const BankPayment = defineAsyncComponent(() => import('./BankPayment.vue'))
const ReceiptPlanner = defineAsyncComponent(() => import('./ReceiptPlanner.vue'))
const FinancialStatementsReport = defineAsyncComponent(() => import('./FinancialStatementsReport.vue'))
const FinancialStatementDesigner = defineAsyncComponent(() => import('./FinancialStatementDesigner.vue'))
const ConsumptionTaxReturn = defineAsyncComponent(() => import('./ConsumptionTaxReturn.vue'))
const MonthlyClosing = defineAsyncComponent(() => import('./MonthlyClosing.vue'))
const CashLedger = defineAsyncComponent(() => import('./CashLedger.vue'))

// 取引先
const BusinessPartnersList = defineAsyncComponent(() => import('./BusinessPartnersList.vue'))
const BusinessPartnerForm = defineAsyncComponent(() => import('./BusinessPartnerForm.vue'))

// HR
const OrganizationChart = defineAsyncComponent(() => import('./OrganizationChart.vue'))
const EmployeesList = defineAsyncComponent(() => import('./EmployeesList.vue'))
const EmployeeForm = defineAsyncComponent(() => import('./EmployeeForm.vue'))
const PolicyEditor = defineAsyncComponent(() => import('./PolicyEditor.vue'))
const PayrollExecute = defineAsyncComponent(() => import('./PayrollExecute.vue'))
const CertificateRequestForm = defineAsyncComponent(() => import('./CertificateRequestForm.vue'))

// 系统
const SchemaEditor = defineAsyncComponent(() => import('./SchemaEditor.vue'))
const SchedulerTasks = defineAsyncComponent(() => import('./SchedulerTasks.vue'))

// 库存
const MaterialsList = defineAsyncComponent(() => import('./MaterialsList.vue'))
const MaterialForm = defineAsyncComponent(() => import('./MaterialForm.vue'))
const WarehousesList = defineAsyncComponent(() => import('./WarehousesList.vue'))
const WarehouseForm = defineAsyncComponent(() => import('./WarehouseForm.vue'))
const BinsList = defineAsyncComponent(() => import('./BinsList.vue'))
const BinForm = defineAsyncComponent(() => import('./BinForm.vue'))
const StockStatuses = defineAsyncComponent(() => import('./StockStatuses.vue'))
const BatchesList = defineAsyncComponent(() => import('./BatchesList.vue'))
const BatchForm = defineAsyncComponent(() => import('./BatchForm.vue'))
const InventoryMovement = defineAsyncComponent(() => import('./InventoryMovementSchema.vue'))
const InventoryBalances = defineAsyncComponent(() => import('./InventoryBalances.vue'))

const router = useRouter()
const route = useRoute()
const { text, lang, setLang } = useI18n()
const recent = reactive<{path:string,name?:string}[]>([])
const messages = reactive<any[]>([])
const messagePager = reactive<{ cursor: string | null; hasMore: boolean; loading: boolean }>({ cursor: null, hasMore: false, loading: false })
const messagePageSize = 50
const sessions = reactive<{id:string,title?:string}[]>([])
const activeSessionId = ref('')
const input = ref('')
const chatInputRef = ref<any>(null)
const pendingBankTransactionId = ref('')
const fileInputRef = ref<HTMLInputElement | null>(null)
const attachments = reactive<ChatAttachment[]>([])
const isDragOver = ref(false)
const maxAttachmentCount = 8
const maxAttachmentSize = 20 * 1024 * 1024
const imagePreview = reactive<{ visible: boolean; url: string; name: string }>({
  visible: false,
  url: '',
  name: ''
})
const taskSectionRefs = new Map<string, HTMLElement>()
const emptyTaskText = computed(() => {
  const chat = (text.value as any)?.chat ?? {}
  return chat.emptyTask || chat.empty || ''
})

interface ClarificationAnswerEntry {
  content: string
  createdAt?: string
}

interface ClarificationDisplayAnswer extends ClarificationAnswerEntry {
  pending?: boolean
}

interface ClarificationEntry {
  questionId: string
  question: string
  documentId?: string
  documentName?: string
  detail?: string
  answeredAt?: string | null
  documentSessionId?: string
  documentLabel?: string
  answers?: ClarificationAnswerEntry[]
  displayText?: string
}

type TaskKind = 'invoice' | 'sales_order' | 'payroll' | 'approval'

interface TaskListItem {
  id: string
  kind: TaskKind
  status: string
  label?: string
  title: string
  summary?: string
  createdAt: string
  updatedAt?: string
  invoiceTask?: InvoiceAgentTask
  salesOrderTask?: SalesOrderAgentTask
  payrollTask?: PayrollAgentTask
  approvalTask?: ApprovalTaskItem
}

interface TaskSectionItem {
  id: string
  kind: TaskKind
  status: string
  label?: string
  title: string
  summary?: string
  invoiceTask?: InvoiceAgentTask
  salesOrderTask?: SalesOrderAgentTask
  payrollTask?: PayrollAgentTask
  approvalTask?: ApprovalTaskItem
  messages: any[]
  startedAt?: string
  startedAtTs: number
}

const activeClarificationId = ref('')
const clarificationMap = computed<Map<string, ClarificationEntry>>(() => {
  const map = new Map<string, ClarificationEntry>()
  messages.forEach((msg: any) => {
    if (!isClarificationMessage(msg)) return
    const questionId: string | undefined = msg?.tag?.questionId
    if (!questionId) return
    let docSessionId = msg?.tag?.documentSessionId ? String(msg.tag.documentSessionId).trim() : ''
    if (!docSessionId && msg?.tag?.documentId) {
      const mappedTask = taskByFileId.value.get(msg.tag.documentId)
      if (mappedTask) {
        docSessionId = mappedTask.documentSessionId
      }
    }
    const label = clarificationLabelValue(msg)
    const questionText = clarificationQuestion(msg)
    const displayText = label ? `[${label}] ${questionText}` : questionText
    map.set(questionId, {
      questionId,
      question: questionText,
      documentId: msg?.tag?.documentId,
      documentName: msg?.tag?.documentName,
      documentSessionId: docSessionId || undefined,
      documentLabel: label || undefined,
      detail: msg?.tag?.detail,
      answeredAt: clarificationAnsweredAt(msg),
      displayText
    })
  })

  messages.forEach((msg: any) => {
    const answerTo = typeof msg?.payload?.answerTo === 'string' ? msg.payload.answerTo.trim() : ''
    if (!answerTo) return
    if (msg?.payload?.localOnly) return
    const entry = map.get(answerTo)
    if (!entry) return
    const content = typeof msg?.content === 'string' ? msg.content.trim() : ''
    if (!content) return
    if (!entry.answers) entry.answers = []
    entry.answers.push({ content, createdAt: typeof msg?.createdAt === 'string' ? msg.createdAt : undefined })
    if (!entry.answeredAt && typeof msg?.createdAt === 'string') {
      entry.answeredAt = msg.createdAt
    }
  })

  return map
})

const clarificationQuestionSet = computed<Set<string>>(() => {
  const set = new Set<string>()
  clarificationMap.value.forEach(entry => {
    if (entry.question && entry.question.trim()) {
      set.add(entry.question.trim())
    }
    if (entry.displayText && entry.displayText.trim()) {
      set.add(entry.displayText.trim())
    }
  })
  return set
})

const pendingClarificationAnswers = reactive<Record<string, ClarificationAnswerEntry[]>>({})

function normalizeText(value?: string | null): string {
  if (typeof value !== 'string') return ''
  return value.replace(/\s+/g, ' ').trim()
}

function addPendingClarificationAnswer(questionId: string, content: string) {
  const normalizedId = (questionId || '').trim()
  if (!normalizedId) return
  const message = typeof content === 'string' ? content : ''
  if (!message.trim()) return
  if (!pendingClarificationAnswers[normalizedId]) {
    pendingClarificationAnswers[normalizedId] = []
  }
  pendingClarificationAnswers[normalizedId].push({
    content: message,
    createdAt: new Date().toISOString()
  })
}

function clearPendingClarificationAnswers(questionId?: string | null) {
  if (!questionId) return
  if (pendingClarificationAnswers[questionId]) {
    delete pendingClarificationAnswers[questionId]
  }
}

const clarifyPendingText = computed(() => {
  if (lang.value === 'zh') return 'AI 正在处理…'
  if (lang.value === 'en') return 'Waiting for AI response…'
  return 'AI が回答しています…'
})

function isClarificationEchoMessage(msg: any): boolean {
  if (!msg || typeof msg !== 'object') return false
  if (msg.status === 'clarify') return false
  const role = typeof msg.role === 'string' ? msg.role.toLowerCase() : ''
  if (role !== 'assistant') return false
  const content = normalizeText(msg.content)
  if (!content) return false
  for (const entry of clarificationMap.value.values()) {
    const question = normalizeText(entry.question)
    if (question && content.startsWith(question)) {
      const detail = normalizeText(entry.detail)
      if (!detail || content.includes(detail)) return true
    }
    const display = normalizeText(entry.displayText)
    if (display && content.startsWith(display)) return true
  }
  return clarificationQuestionSet.value.has(content)
}

const activeClarification = computed<ClarificationEntry | null>(() => {
  if (!activeClarificationId.value) return null
  return clarificationMap.value.get(activeClarificationId.value) || null
})

interface SalesOrderLine {
  lineNo: number
  materialCode: string
  materialName: string
  description?: string
  quantity: number
  uom?: string
  unitPrice: number
  amount: number
  note?: string
}

function normalizeInvoiceTask(raw: any, fallbackSessionId: string): InvoiceTask | null {
  if (!raw) return null
  const id = typeof raw.id === 'string' && raw.id ? raw.id : (raw.id?.toString?.() ?? '')
  if (!id) return null
  const fileId = typeof raw.fileId === 'string' && raw.fileId ? raw.fileId : (raw.file_id?.toString?.() ?? '')
  const documentSessionIdRaw =
    typeof raw.documentSessionId === 'string' && raw.documentSessionId
      ? raw.documentSessionId
      : (raw.document_session_id?.toString?.()
        ?? raw.documentSession
        ?? raw.document_session
        ?? raw.metadata?.documentSessionId
        ?? raw.metadata?.document_session_id
        ?? '')
  const documentSessionId = typeof documentSessionIdRaw === 'string' ? documentSessionIdRaw : ''
  const sessionId = typeof raw.sessionId === 'string' && raw.sessionId ? raw.sessionId : fallbackSessionId
  const fileNameCandidates = [
    raw.fileName,
    raw.file_name,
    raw.displayName,
    raw.display_name,
    raw.label,
    fileId || id
  ]
  const fileName = fileNameCandidates.find(value => typeof value === 'string' && value.trim())?.toString() || id
  const status = typeof raw.status === 'string' && raw.status ? raw.status : 'pending'
  const summary = typeof raw.summary === 'string' && raw.summary ? raw.summary : undefined
  const label = typeof raw.label === 'string' && raw.label ? raw.label : undefined
  const displayLabel =
    typeof raw.displayLabel === 'string' && raw.displayLabel ? raw.displayLabel : undefined
  const contentType = typeof raw.contentType === 'string' && raw.contentType
    ? raw.contentType
    : (typeof raw.content_type === 'string' ? raw.content_type : undefined)
  const size = Number.isFinite(Number(raw.size)) ? Number(raw.size) : undefined
  const createdAt = typeof raw.createdAt === 'string' && raw.createdAt ? raw.createdAt : new Date().toISOString()
  const updatedAt = typeof raw.updatedAt === 'string' && raw.updatedAt ? raw.updatedAt : createdAt
  const metadata = raw.metadata ?? undefined
  const voucherNo = typeof raw.voucherNo === 'string' && raw.voucherNo ? raw.voucherNo
    : (typeof metadata?.voucherNo === 'string' ? metadata.voucherNo : undefined)
  return {
    kind: 'invoice',
    id,
    sessionId,
    label,
    displayLabel,
    fileId,
    fileName,
    contentType,
    size,
    documentSessionId,
    status,
    summary,
    analysis: raw.analysis,
    metadata,
    voucherNo,
    createdAt,
    updatedAt
  }
}

function normalizeSalesOrderTask(raw: any, fallbackSessionId: string): SalesOrderAgentTask | null {
  if (!raw) return null
  const id = typeof raw.id === 'string' && raw.id ? raw.id : (raw.id?.toString?.() ?? '')
  if (!id) return null
  const sessionId = typeof raw.sessionId === 'string' && raw.sessionId ? raw.sessionId : fallbackSessionId
  const displayLabel =
    typeof raw.displayLabel === 'string' && raw.displayLabel ? raw.displayLabel : undefined
  const status = typeof raw.status === 'string' && raw.status ? raw.status : 'pending'
  const summary = typeof raw.summary === 'string' && raw.summary ? raw.summary : undefined
  const salesOrderIdRaw =
    typeof raw.salesOrderId === 'string' && raw.salesOrderId
      ? raw.salesOrderId
      : raw.salesOrderId?.toString?.()
  const salesOrderId = typeof salesOrderIdRaw === 'string' && salesOrderIdRaw ? salesOrderIdRaw : undefined
  const salesOrderNo = typeof raw.salesOrderNo === 'string' && raw.salesOrderNo ? raw.salesOrderNo : undefined
  const customerCode = typeof raw.customerCode === 'string' && raw.customerCode ? raw.customerCode : undefined
  const customerName = typeof raw.customerName === 'string' && raw.customerName ? raw.customerName : undefined
  const metadata = raw?.metadata ?? undefined
  const payload = raw?.payload ?? undefined
  const createdAt = typeof raw.createdAt === 'string' && raw.createdAt ? raw.createdAt : new Date().toISOString()
  const updatedAt = typeof raw.updatedAt === 'string' && raw.updatedAt ? raw.updatedAt : createdAt
  const completedAt = typeof raw.completedAt === 'string' && raw.completedAt ? raw.completedAt : undefined
  return {
    kind: 'sales_order',
    id,
    sessionId,
    displayLabel,
    status,
    summary,
    salesOrderId,
    salesOrderNo,
    customerCode,
    customerName,
    metadata,
    payload,
    createdAt,
    updatedAt,
    completedAt
  }
}

function normalizePayrollTask(raw: any, fallbackSessionId: string): PayrollAgentTask | null {
  if (!raw) return null
  const id = typeof raw.id === 'string' && raw.id ? raw.id : raw.id?.toString?.()
  if (!id) return null
  const sessionId = typeof raw.sessionId === 'string' && raw.sessionId ? raw.sessionId : fallbackSessionId
  const status = typeof raw.status === 'string' && raw.status ? raw.status : 'pending'
  const summary = typeof raw.summary === 'string' && raw.summary ? raw.summary : undefined
  const runId = typeof raw.runId === 'string' && raw.runId ? raw.runId : raw.runId?.toString?.()
  const entryId = typeof raw.entryId === 'string' && raw.entryId ? raw.entryId : raw.entryId?.toString?.()
  const employeeId = typeof raw.employeeId === 'string' && raw.employeeId ? raw.employeeId : raw.employeeId?.toString?.()
  if (!runId || !entryId || !employeeId) return null
  const employeeCode =
    typeof raw.employeeCode === 'string' && raw.employeeCode ? raw.employeeCode : undefined
  const employeeName =
    typeof raw.employeeName === 'string' && raw.employeeName ? raw.employeeName : undefined
  const periodMonth =
    typeof raw.periodMonth === 'string' && raw.periodMonth ? raw.periodMonth : ''
  const metadata = raw?.metadata ?? undefined
  const diffSummary = raw?.diffSummary ?? undefined
  const targetUserId =
    typeof raw.targetUserId === 'string' && raw.targetUserId ? raw.targetUserId : undefined
  const createdAt =
    typeof raw.createdAt === 'string' && raw.createdAt ? raw.createdAt : new Date().toISOString()
  const updatedAt =
    typeof raw.updatedAt === 'string' && raw.updatedAt ? raw.updatedAt : createdAt
  const completedAt =
    typeof raw.completedAt === 'string' && raw.completedAt ? raw.completedAt : undefined
  return {
    kind: 'payroll',
    id,
    sessionId,
    runId,
    entryId,
    employeeId,
    employeeCode,
    employeeName,
    periodMonth,
    status,
    summary,
    metadata,
    diffSummary,
    targetUserId,
    createdAt,
    updatedAt,
    completedAt
  }
}

function hydrateAgentTasks(rawTasks: any[], fallbackSessionId: string): AgentTask[] {
  const list: AgentTask[] = []
  if (!Array.isArray(rawTasks)) return list
  rawTasks.forEach((item) => {
    const kindRaw = typeof item?.kind === 'string' ? item.kind.trim().toLowerCase() : ''
    const inferredKind = kindRaw
      || (item?.salesOrderNo || item?.salesOrderId ? 'sales_order' : 'invoice')
    if (inferredKind === 'sales_order') {
      const normalized = normalizeSalesOrderTask(item, fallbackSessionId)
      if (normalized) list.push(normalized)
      return
    }
    if (inferredKind === 'payroll') {
      const normalized = normalizePayrollTask(item, fallbackSessionId)
      if (normalized) list.push(normalized)
      return
    }
    const normalized = normalizeInvoiceTask(item, fallbackSessionId)
    if (normalized) list.push(normalized)
  })
  return list
}

interface ApprovalTaskItem {
  id: string
  entity: string
  objectId: string
  title: string
  status: string
  createdAt: string
  updatedAt?: string
  stepName?: string
  applicant?: string
  applicantCode?: string
  userName?: string
  userCode?: string
  summary?: string
  remark?: string
  payload?: any
}

interface CertificateRequestDetail {
  id: string
  applicantCode: string
  applicantName: string
  purpose: string
  bodyText: string
  resignReason: string
}

interface TimesheetSubmissionDetail {
  id: string
  month: string
  creatorUserId: string
  employeeCode: string
  employeeName: string
  totalHours: number
  totalOvertime: number
  workDays: number
  status?: string
}

const certificateRequestDetails = reactive<Record<string, CertificateRequestDetail>>({})
const certificateRequestLoading = new Set<string>()
const timesheetSubmissionDetails = reactive<Record<string, TimesheetSubmissionDetail>>({})
const timesheetSubmissionLoading = new Set<string>()
const employeeProfileCache = reactive<Record<string, string>>({})
const employeeProfileLoading = new Set<string>()
function isInvoiceTask(task: AgentTask): task is InvoiceAgentTask {
  return task.kind === 'invoice'
}

function isSalesOrderTask(task: AgentTask): task is SalesOrderAgentTask {
  return task.kind === 'sales_order'
}

function isPayrollTask(task: AgentTask): task is PayrollAgentTask {
  return task.kind === 'payroll'
}

function normalizeApprovalTask(raw: any): ApprovalTaskItem | null {
  if (!raw) return null
  const id = raw.id?.toString?.() ?? ''
  const entity = raw.entity ?? raw.object_type ?? ''
  const objectId = raw.object_id ?? raw.objectId ?? ''
  if (!id || !entity || !objectId) return null
  const status = typeof raw.status === 'string' && raw.status ? raw.status : 'pending'
  const payload = parseJsonSafe(raw.payload) || raw.payload || {}
  const createdAt = typeof raw.created_at === 'string' && raw.created_at
    ? raw.created_at
    : (typeof raw.createdAt === 'string' ? raw.createdAt : new Date().toISOString())
  const updatedAt = typeof raw.updated_at === 'string' && raw.updated_at
    ? raw.updated_at
    : (typeof raw.updatedAt === 'string' ? raw.updatedAt : undefined)
  const stepName = raw.step_name ?? raw.stepName ?? ''
  const applicantName = raw.applicant_name ?? raw.applicantName ?? payload?.applicantName ?? payload?.applicant_name ?? ''
  const applicantCode = payload?.employeeCode ?? payload?.employee_code ?? raw.employee_code ?? raw.employeeCode ?? payload?.applicantCode ?? payload?.applicant_code ?? ''
  const summary = raw.summary ?? raw.description ?? payload?.summary ?? payload?.description ?? ''
  const remark = payload?.remark ?? payload?.memo ?? payload?.note ?? raw.remark ?? raw.memo ?? raw.note ?? ''
  const userName = payload?.userName ?? payload?.user_name ?? payload?.user ?? ''
  const userCode = payload?.userCode ?? payload?.user_code ?? ''
  const title = resolveApprovalTitle(entity, raw)
  return {
    id,
    entity,
    objectId: objectId.toString(),
    title,
    status,
    createdAt,
    updatedAt,
    stepName: stepName ? stepName.toString() : undefined,
    applicant: applicantName ? applicantName.toString() : undefined,
    applicantCode: applicantCode ? applicantCode.toString() : undefined,
    userName: userName ? userName.toString() : undefined,
    userCode: userCode ? userCode.toString() : undefined,
    summary: summary ? summary.toString() : undefined,
    remark: remark ? remark.toString() : undefined,
    payload: raw
  }
}

function resolveApprovalTitle(entity: string, raw: any): string {
  const lower = (entity || '').toString().toLowerCase()
  const mapping = (text.value?.chat as any)?.approvalEntityMap || {}
  const mapped = typeof mapping[lower] === 'string' ? mapping[lower] : ''
  if (mapped) return mapped
  if (typeof raw?.title === 'string' && raw.title.trim()) return raw.title
  if (typeof raw?.step_name === 'string' && raw.step_name.trim()) return raw.step_name
  return lower || 'approval'
}

async function loadTasks(){
  const sessionId = activeSessionId.value
  if (!sessionId) return
  try{
    const resp = await listInvoiceTasks(sessionId)
    const responseSessionId = typeof resp.data?.sessionId === 'string' && resp.data.sessionId
      ? resp.data.sessionId
      : sessionId
    const rawItems = Array.isArray(resp.data?.tasks) ? resp.data.tasks : []
    const normalized = hydrateAgentTasks(rawItems, responseSessionId)
    const ordered = [...normalized].sort((a, b) => compareByTimestampAsc(toTimestamp(a.createdAt), toTimestamp(b.createdAt)))
    taskList.value = ordered
    const activeIds = new Set(taskList.value.map(task => task.id))
    Object.keys(taskAttachments).forEach(id => {
      if (!activeIds.has(id)){
        delete taskAttachments[id]
      }
    })
    if (!normalized.length){
      if (!preferGeneralMode.value){
        activeTaskId.value = ''
      }
      return
    }
    // Only auto-select when the current activeTaskId does NOT exist in the loaded task list.
    // If it exists, NEVER override (regardless of how it was set).
    const currentStillExists = !!activeTaskId.value && normalized.some(task => task.id === activeTaskId.value)
    if (!currentStillExists){
      if (preferGeneralMode.value){
        activeTaskId.value = ''
      } else {
        const pending = normalized.find(task => task.status === 'pending') || normalized[0]
        activeTaskId.value = pending?.id || ''
      }
    }
    if (!preferGeneralMode.value && !pendingTasks.value.length && completedTasks.value.length){
      showCompletedTasks.value = true
    }
    await loadApprovalTasks()
  }catch(e){
    console.error('[ChatKit] loadTasks failed', e)
  }
}

async function ensureEmployeeProfiles(codes: string[]) {
  const normalized = Array.from(
    new Set(
      (codes || [])
        .map(code => normalizeText(code))
        .filter(code => !!code && !(code in employeeProfileCache) && !employeeProfileLoading.has(code))
    )
  )
  if (normalized.length === 0) return
  normalized.forEach(code => employeeProfileLoading.add(code))
  try {
    const where =
      normalized.length === 1
        ? [{ field: 'employee_code', op: 'eq', value: normalized[0] }]
        : [{ field: 'employee_code', op: 'in', value: normalized }]
    const resp = await api.post('/objects/employee/search', {
      page: 1,
      pageSize: Math.max(20, normalized.length),
      where,
      orderBy: [{ field: 'employee_code', dir: 'ASC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    rows.forEach((row: any) => {
      const code = normalizeText(row?.employee_code ?? row?.payload?.code)
      const payload = row?.payload || {}
      const name = normalizeText(payload.nameKanji ?? payload.name ?? row?.name)
      if (code) {
        employeeProfileCache[code] = name
      }
    })
    normalized.forEach(code => {
      if (!(code in employeeProfileCache)) {
        employeeProfileCache[code] = ''
      }
    })
  } catch {
    normalized.forEach(code => {
      if (!(code in employeeProfileCache)) {
        employeeProfileCache[code] = ''
      }
    })
  } finally {
    normalized.forEach(code => employeeProfileLoading.delete(code))
  }
  Object.values(certificateRequestDetails).forEach(detail => {
    if (detail && detail.applicantCode && !detail.applicantName) {
      const cached = employeeProfileCache[detail.applicantCode]
      if (cached) detail.applicantName = cached
    }
  })
}

async function ensureCertificateRequestDetails(tasks: ApprovalTaskItem[]) {
  if (!Array.isArray(tasks) || tasks.length === 0) return
  const ids = Array.from(
    new Set(
      tasks
        .filter(task => task && task.entity === 'certificate_request')
        .map(task => normalizeText(task.objectId))
        .filter(id => !!id)
    )
  )
  const missing = ids.filter(id => !certificateRequestDetails[id] && !certificateRequestLoading.has(id))
  if (missing.length === 0) return
  missing.forEach(id => certificateRequestLoading.add(id))
  try {
    const where =
      missing.length === 1
        ? [{ field: 'id', op: 'eq', value: missing[0] }]
        : [{ field: 'id', op: 'in', value: missing }]
    const resp = await api.post('/objects/certificate_request/search', {
      page: 1,
      pageSize: Math.max(20, missing.length),
      where
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    const codesToResolve = new Set<string>()
    rows.forEach((row: any) => {
      const id = normalizeText(row?.id)
      const payload = row?.payload || {}
      const detail: CertificateRequestDetail = {
        id,
        applicantCode: normalizeText(payload.employeeId ?? payload.employee_id),
        applicantName: normalizeText(payload.applicantName ?? payload.applicant_name),
        purpose: normalizeText(payload.purpose),
        bodyText: normalizeText(payload.bodyText),
        resignReason: normalizeText(payload.resignReason ?? payload.resign_reason)
      }
      certificateRequestDetails[id] = detail
      if (!detail.applicantName && detail.applicantCode) {
        codesToResolve.add(detail.applicantCode)
      }
    })
    missing.forEach(id => {
      if (!certificateRequestDetails[id]) {
        certificateRequestDetails[id] = {
          id,
          applicantCode: '',
          applicantName: '',
          purpose: '',
          bodyText: '',
          resignReason: ''
        }
      }
    })
    if (codesToResolve.size > 0) {
      await ensureEmployeeProfiles(Array.from(codesToResolve))
    }
  } catch {
    missing.forEach(id => {
      if (!certificateRequestDetails[id]) {
        certificateRequestDetails[id] = {
          id,
          applicantCode: '',
          applicantName: '',
          purpose: '',
          bodyText: '',
          resignReason: ''
        }
      }
    })
  } finally {
    missing.forEach(id => certificateRequestLoading.delete(id))
  }
}

async function ensureTimesheetSubmissionDetails(tasks: ApprovalTaskItem[]) {
  if (!Array.isArray(tasks) || tasks.length === 0) return
  const ids = Array.from(
    new Set(
      tasks
        .filter(task => task && task.entity === 'timesheet_submission')
        .map(task => normalizeText(task.objectId))
        .filter(id => !!id)
    )
  )
  const missing = ids.filter(id => !timesheetSubmissionDetails[id] && !timesheetSubmissionLoading.has(id))
  if (missing.length === 0) return
  missing.forEach(id => timesheetSubmissionLoading.add(id))
  try {
    const where =
      missing.length === 1
        ? [{ field: 'id', op: 'eq', value: missing[0] }]
        : [{ field: 'id', op: 'in', value: missing }]
    const resp = await api.post('/objects/timesheet_submission/search', {
      page: 1,
      pageSize: Math.max(20, missing.length),
      where
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    const codesToResolve = new Set<string>()
    rows.forEach((row: any) => {
      const id = normalizeText(row?.id)
      const payload = row?.payload || {}
      const detail: TimesheetSubmissionDetail = {
        id,
        month: normalizeText(payload.month ?? row?.month),
        creatorUserId: normalizeText(payload.creatorUserId ?? payload.creator_user_id ?? row?.created_by),
        employeeCode: normalizeText(payload.employeeCode ?? payload.employee_code ?? row?.employee_code),
        employeeName: normalizeText(payload.employeeName ?? payload.employee_name),
        totalHours: Number(payload.totalHours ?? payload.total_hours ?? 0),
        totalOvertime: Number(payload.totalOvertime ?? payload.total_overtime ?? 0),
        workDays: Number(payload.workDays ?? payload.work_days ?? 0),
        status: normalizeText(payload.status ?? row?.status)
      }
      timesheetSubmissionDetails[id] = detail
      if (!detail.employeeName && detail.employeeCode) codesToResolve.add(detail.employeeCode)
    })
    missing.forEach(id => {
      if (!timesheetSubmissionDetails[id]) {
        timesheetSubmissionDetails[id] = {
          id,
          month: '',
          creatorUserId: '',
          employeeCode: '',
          employeeName: '',
          totalHours: 0,
          totalOvertime: 0,
          workDays: 0
        }
      }
    })
    if (codesToResolve.size > 0) {
      await ensureEmployeeProfiles(Array.from(codesToResolve))
      Object.values(timesheetSubmissionDetails).forEach(detail => {
        if (detail && detail.employeeCode && !detail.employeeName) {
          const cached = employeeProfileCache[detail.employeeCode]
          if (cached) detail.employeeName = cached
        }
      })
    }
  } catch {
    missing.forEach(id => {
      if (!timesheetSubmissionDetails[id]) {
        timesheetSubmissionDetails[id] = {
          id,
          month: '',
          creatorUserId: '',
          employeeCode: '',
          employeeName: '',
          totalHours: 0,
          totalOvertime: 0,
          workDays: 0
        }
      }
    })
  } finally {
    missing.forEach(id => timesheetSubmissionLoading.delete(id))
  }
}

async function loadApprovalTasks(){
  try{
    // Load pending tasks
    const resp = await listApprovalTasks('pending', 50)
    const rawItems = Array.isArray(resp.data?.data) ? resp.data.data : []
    const normalized: ApprovalTaskItem[] = []
    rawItems.forEach((item: any) => {
      const task = normalizeApprovalTask(item)
      if (task) normalized.push(task)
    })
    approvalTasks.value = normalized

    // Load completed (approved/rejected) tasks from backend
    const completedFromQueue = new Set(approvalCompletionQueue.keys())
    const existingCompletedIds = new Set(approvalCompleted.value.map(t => t.id))
    
    // Only load from backend if we don't have locally completed tasks (initial load or refresh)
    if (approvalCompleted.value.length === 0 || approvalCompletionQueue.size === 0) {
      try {
        const [approvedResp, rejectedResp] = await Promise.all([
          listApprovalTasks('approved', 20),
          listApprovalTasks('rejected', 20)
        ])
        const approvedItems = Array.isArray(approvedResp.data?.data) ? approvedResp.data.data : []
        const rejectedItems = Array.isArray(rejectedResp.data?.data) ? rejectedResp.data.data : []
        const allCompleted = [...approvedItems, ...rejectedItems]
        const loadedCompleted: ApprovalTaskItem[] = []
        allCompleted.forEach((item: any) => {
          const task = normalizeApprovalTask(item)
          if (task && !existingCompletedIds.has(task.id) && !completedFromQueue.has(task.id)) {
            loadedCompleted.push(task)
          }
        })
        if (loadedCompleted.length > 0) {
          approvalCompleted.value = [...approvalCompleted.value, ...loadedCompleted]
            .sort((a, b) => new Date(b.updatedAt || b.createdAt).getTime() - new Date(a.updatedAt || a.createdAt).getTime())
        }
      } catch (e) {
        console.error('[ChatKit] loadApprovalTasks: failed to load completed tasks', e)
      }
    }

    const availableIds = new Set<string>([
      ...pendingTasks.value.map(task => task.id),
      ...completedTasks.value.map(task => task.id)
    ])
    // Only auto-select when the current activeTaskId is invalid (empty or not in list).
    // If it exists in the list, NEVER override.
    const currentValid = !!activeTaskId.value && availableIds.has(activeTaskId.value)
    if (!currentValid) {
      if (preferGeneralMode.value){
        activeTaskId.value = ''
      } else {
        const firstPending = pendingTasks.value[0]
        const firstCompleted = completedTasks.value[0]
        activeTaskId.value = firstPending?.id || firstCompleted?.id || ''
      }
    }

    // Process completion queue (for tasks just completed in this session)
    const pendingApprovalIds = new Set(normalized.map(task => task.id))
    for (const [id, completedTask] of approvalCompletionQueue.entries()){
      if (!pendingApprovalIds.has(id)){
        approvalCompleted.value = approvalCompleted.value.filter(item => item.id !== id)
        approvalCompleted.value.unshift({ ...completedTask })
        approvalCompletionQueue.delete(id)
      }
    }
    await ensureCertificateRequestDetails([...normalized, ...approvalCompleted.value])
    await ensureTimesheetSubmissionDetails([...normalized, ...approvalCompleted.value])
    if (approvalCompletionQueue.size > 0){
      scheduleApprovalReload()
    }
  }catch(e){
    console.error('[ChatKit] loadApprovalTasks failed', e)
  }
}

function mergeTasks(newTasks: AgentTask[]){
  if (!newTasks || newTasks.length === 0) return
  const map = new Map<string, AgentTask>()
  taskList.value.forEach(task => map.set(task.id, task))
  newTasks.forEach(task => map.set(task.id, task))
  taskList.value = Array.from(map.values()).sort((a, b) => compareByTimestampAsc(toTimestamp(a.createdAt), toTimestamp(b.createdAt)))
  const activeIds = new Set(taskList.value.map(task => task.id))
  Object.keys(taskAttachments).forEach(id => {
    if (!activeIds.has(id)){
      delete taskAttachments[id]
    }
  })
  // Only auto-select if there is NO current active task (never override an existing selection)
  const currentExists = !!activeTaskId.value && activeIds.has(activeTaskId.value)
  if (!preferGeneralMode.value && !currentExists && pendingTasks.value.length > 0){
    activeTaskId.value = pendingTasks.value[0].id
  }
  if (!preferGeneralMode.value && !pendingTasks.value.length && completedTasks.value.length){
    showCompletedTasks.value = true
  }
}

function selectTask(taskId: string){
  preferGeneralMode.value = false
  if (!taskId){
    activeTaskId.value = ''
    return
  }
  activeTaskId.value = taskId
  nextTick(() => {
    const target = taskSectionRefs.get(taskId)
    if (target){
      try { target.scrollIntoView({ behavior: 'smooth', block: 'start' }) } catch {}
    }
    try{ chatInputRef.value?.focus?.() }catch{}
  })
}

function enterGeneralMode(){
  preferGeneralMode.value = true
  if (activeTaskId.value){
    activeTaskId.value = ''
  }
  activeClarificationId.value = ''
  nextTick(() => {
    try{ chatInputRef.value?.focus?.() }catch{}
  })
}

function setTaskSectionRef(taskId: string, el: any){
  if (!el){
    taskSectionRefs.delete(taskId)
    return
  }
  const element = el instanceof HTMLElement ? el : (el?.$el ?? null)
  if (element instanceof HTMLElement){
    taskSectionRefs.set(taskId, element)
  }
}

function taskStatusType(status: string){
  const normalized = status?.toLowerCase() || ''
  if (normalized === 'completed') return 'success'
  if (normalized === 'failed') return 'danger'
  if (normalized === 'error') return 'danger'
  if (normalized === 'cancelled') return 'info'
  if (normalized === 'in_progress') return 'warning'
  if (normalized === 'approved') return 'success'
  if (normalized === 'rejected') return 'danger'
  return 'info'
}

function taskStatusLabel(status: string){
  const normalized = status?.toLowerCase().replace(/-/g, '_') || ''
  const label = normalized ? taskStatusLabels.value[normalized] : ''
  if (label) return label
  return taskStatusLabels.value.pending || '未処理'
}

function canCancelTask(task: InvoiceTask){
  const normalized = task.status?.toLowerCase() || ''
  // 允许删除任何状态的任务，因为后端现在支持强制删除已完成的任务
  return true
}

function canRetryTask(task: InvoiceTask){
  const normalized = task.status?.toLowerCase() || ''
  return normalized === 'failed' || normalized === 'error' || normalized === 'cancelled'
}

function canRetrySection(section: TaskSectionItem){
  if (!section || section.kind !== 'invoice' || !section.invoiceTask) return false
  if (canRetryTask(section.invoiceTask)) return true
  const msgs = Array.isArray(section.messages) ? section.messages : []
  return msgs.some((m: any) => (m?.status || '').toLowerCase() === 'error')
}

async function retryInvoiceTask(task: InvoiceTask){
  if (!activeSessionId.value){
    ElMessage.error('请先选择会话')
    return
  }
  if (retryingTaskId.value === task.id) return
  retryingTaskId.value = task.id
  try{
    const retryMessage = localize('このタスクを再試行してください。', '请重新尝试该任务。', 'Please retry this task.')
    const payload: Record<string, any> = { message: retryMessage, language: lang.value, taskId: task.id }
    if (activeSessionId.value) payload.sessionId = activeSessionId.value
    const resp = await api.post('/ai/agent/message', payload)
    applySessionFromResponse(resp.data?.sessionId)
    activeTaskId.value = task.id
    await loadMessages()
    await loadTasks()
    ElMessage.success(localize('再試行を開始しました。', '已开始重试。', 'Retry started.'))
  }catch(e:any){
    const errText = e?.response?.data?.error || e?.message || localize('再試行に失敗しました。', '重试失败。', 'Retry failed.')
    ElMessage.error(errText)
  }finally{
    retryingTaskId.value = ''
  }
}

async function confirmCancelTask(task: InvoiceTask){
  if (!activeSessionId.value){
    ElMessage.error(localize('セッションを選択してください', '请先选择会话', 'Please select a session first'))
    return
  }
  try{
    await ElMessageBox.confirm(
      localize('このタスクを削除しますか？', '确定要删除该任务吗？', 'Delete this task?'),
      localize('確認', '提示', 'Confirm'),
      {
      type: 'warning',
      confirmButtonText: localize('削除', '确定', 'Delete'),
      cancelButtonText: localize('キャンセル', '取消', 'Cancel')
    })
  }catch{
    return
  }
  try{
    await api.delete(`/ai/tasks/${task.id}`, {
      params: {
        sessionId: activeSessionId.value
      }
    })
    ElMessage.success(localize('タスクを削除しました', '任务已删除', 'Task deleted'))
    // 删除后自动选中时间轴上的下一个任务（如果当前选中的是被删除的任务）
    let nextTaskId = ''
    if (activeTaskId.value === task.id) {
      const sections = taskSections.value
      const idx = sections.findIndex(s => s.id === task.id)
      if (idx >= 0) {
        // 优先选下一个，没有则选上一个
        if (idx + 1 < sections.length) {
          nextTaskId = sections[idx + 1].id
        } else if (idx - 1 >= 0) {
          nextTaskId = sections[idx - 1].id
        }
      }
    }
    delete taskAttachments[task.id]
    await loadTasks()
    await loadMessages()
    if (nextTaskId) {
      selectTask(nextTaskId)
    } else if (activeTaskId.value === task.id) {
      activeTaskId.value = ''
    }
  }catch(e:any){
    const errText = e?.response?.data?.error || e?.message || localize('タスクの削除に失敗しました', '删除任务失败', 'Failed to delete task')
    ElMessage.error(errText)
  }
}

function toggleCompletedTasks(){
  showCompletedTasks.value = !showCompletedTasks.value
}

async function approveApprovalTask(task: ApprovalTaskItem){
  if (!task) return
  approvalActionLoadingId.value = task.id
  try{
    await actApprovalTask(task.entity, task.objectId, 'approve')
    queueApprovalCompletion(task, 'approved')
    ElMessage.success(text.value.chat?.approvalApproveSuccess || '审批已同意')
    await loadApprovalTasks()
  }catch(e:any){
    const errText = e?.response?.data?.error || e?.message || (text.value.chat?.approvalApproveFailed || '审批失败')
    ElMessage.error(errText)
  }finally{
    approvalActionLoadingId.value = ''
  }
}

async function rejectApprovalTask(task: ApprovalTaskItem){
  if (!task) return
  approvalActionLoadingId.value = task.id
  try{
    await actApprovalTask(task.entity, task.objectId, 'reject')
    queueApprovalCompletion(task, 'rejected')
    ElMessage.success(text.value.chat?.approvalRejectSuccess || '审批已驳回')
    await loadApprovalTasks()
  }catch(e:any){
    const errText = e?.response?.data?.error || e?.message || (text.value.chat?.approvalRejectFailed || '操作失败')
    ElMessage.error(errText)
  }finally{
    approvalActionLoadingId.value = ''
  }
}

async function downloadApprovalAttachment(task: ApprovalTaskItem){
  if (!task || task.entity !== 'certificate_request') return
  approvalDownloadLoadingId.value = task.id
  pdfPreview.value = { visible: true, url: '', name: `certificate-${task.objectId}.pdf`, loading: true }
  try{
    const resp = await api.get(`/operations/certificate_request/${encodeURIComponent(task.objectId)}/pdf`, { responseType: 'blob' })
    const data = resp?.data
    const blob = data instanceof Blob ? data : new Blob([data], { type: 'application/pdf' })
    const url = URL.createObjectURL(blob)
    pdfPreview.value.url = url
    pdfPreview.value.loading = false
  }catch(e:any){
    const errText = e?.response?.data?.error || e?.message || (text.value.chat?.approvalDownloadFailed || '下载失败')
    ElMessage.error(errText)
    pdfPreview.value.visible = false
  }finally{
    approvalDownloadLoadingId.value = ''
  }
}

async function openTimesheetDetail(task: ApprovalTaskItem){
  if (!task || task.entity !== 'timesheet_submission') return
  const objectId = normalizeText(task.objectId)
  const detail = objectId ? timesheetSubmissionDetails[objectId] : undefined
  const month = normalizeText(detail?.month) || timesheetMonth(task)
  const creatorUserId = normalizeText(detail?.creatorUserId)
  const applicant = timesheetApplicantName(task)
  timesheetDetailDialog.visible = true
  timesheetDetailDialog.loading = true
  timesheetDetailDialog.error = ''
  timesheetDetailDialog.month = month
  timesheetDetailDialog.creatorUserId = creatorUserId
  timesheetDetailDialog.applicant = applicant
  timesheetDetailDialog.totalHoursText = detail ? formatHmHours(detail.totalHours) : ''
  timesheetDetailDialog.totalOvertimeText = detail ? formatHmHours(detail.totalOvertime) : ''
  timesheetDetailDialog.workDaysText = detail ? `${detail.workDays || 0}日` : ''
  timesheetDetailDialog.statusLabel = normalizeText(detail?.status) || ''
  timesheetDetailDialog.title = applicant && month ? `勤怠明細 ${month} (${applicant})` : '勤怠明細'
  timesheetDetailDialog.rows = []

  if (!month || !creatorUserId){
    timesheetDetailDialog.error = '明細を取得できません（申請者/対象月が不足しています）'
    timesheetDetailDialog.loading = false
    return
  }
  try{
    const resp = await api.post('/objects/timesheet/search', {
      page: 1,
      pageSize: 400,
      where: [
        { field: 'month', op: 'in', value: [month] },
        { json: 'creatorUserId', op: 'in', value: [creatorUserId] }
      ],
      orderBy: [{ field: 'timesheet_date', dir: 'ASC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    timesheetDetailDialog.rows = rows.map((r: any) => {
      const payload = r?.payload || {}
      return {
        id: normalizeText(r?.id),
        date: normalizeText(r?.timesheet_date ?? payload?.date),
        startTime: normalizeText(payload?.startTime),
        endTime: normalizeText(payload?.endTime),
        lunchMinutes: typeof payload?.lunchMinutes === 'number' ? payload.lunchMinutes : (payload?.lunchMinutes != null ? Number(payload.lunchMinutes) : null),
        hours: Number(payload?.hours ?? 0),
        overtime: Number(payload?.overtime ?? 0),
        task: normalizeText(payload?.task),
        status: normalizeText(payload?.status),
        isHoliday: !!payload?.isHoliday
      }
    })
  }catch(e:any){
    timesheetDetailDialog.error = e?.response?.data?.error || e?.message || '明細の取得に失敗しました'
  }finally{
    timesheetDetailDialog.loading = false
  }
}

function closeTimesheetDetail(){
  timesheetDetailDialog.visible = false
  timesheetDetailDialog.loading = false
  timesheetDetailDialog.error = ''
  timesheetDetailDialog.title = ''
  timesheetDetailDialog.month = ''
  timesheetDetailDialog.applicant = ''
  timesheetDetailDialog.creatorUserId = ''
  timesheetDetailDialog.totalHoursText = ''
  timesheetDetailDialog.totalOvertimeText = ''
  timesheetDetailDialog.workDaysText = ''
  timesheetDetailDialog.statusLabel = ''
  timesheetDetailDialog.rows = []
}

// === 銀行自動記帳タスク: AI記帳サポート + 明細一覧 ===
const bankAiSupportLoadingId = ref('')

async function startBankAiSupport(task: ApprovalTaskItem){
  if (!task || task.entity !== 'moneytree_posting') return
  bankAiSupportLoadingId.value = task.id
  try {
    // 1. 获取该 task 关联的失败/needs_rule 交易列表
    const resp = await api.get(`/integrations/moneytree/posting-task/${encodeURIComponent(task.objectId)}/failed-transactions`)
    const failedTxs: any[] = resp.data?.items || []

    if (failedTxs.length === 0) {
      ElMessage.info('未記帳の取引はありません。')
      bankAiSupportLoadingId.value = ''
      return
    }

    // 2. 构建结构化消息：汇总失败交易，请 AI 引导用户逐条处理
    let message = `銀行自動記帳で${failedTxs.length}件の取引が記帳できませんでした。順番に確認して記帳をお願いします。\n\n`
    failedTxs.forEach((tx: any, i: number) => {
      const isW = (tx.withdrawalAmount ?? 0) > 0
      const amount = isW ? tx.withdrawalAmount : tx.depositAmount
      const type = isW ? '出金' : '入金'
      message += `${i + 1}. ${tx.transactionDate} ${type} ¥${Number(amount || 0).toLocaleString()} ${tx.description || ''}`
      if (tx.postingError) message += ` (エラー: ${tx.postingError})`
      message += ` [ID: ${tx.id}]\n`
    })
    message += `\nまず1件目から始めてください。`

    // 3. 发送消息（带第一条交易的 bankTransactionId）
    const payload: Record<string, any> = {
      message,
      language: 'ja',
      bankTransactionId: failedTxs[0].id
    }
    if (activeSessionId.value) payload.sessionId = activeSessionId.value

    const aiResp = await api.post('/ai/agent/message', payload)
    applySessionFromResponse(aiResp.data?.sessionId)
    await loadMessages()
    await loadTasks()
  } catch (e: any) {
    const errText = e?.response?.data?.error || e?.message || 'AI記帳サポートの開始に失敗しました'
    ElMessage.error(errText)
  } finally {
    bankAiSupportLoadingId.value = ''
  }
}

function openBankTransactionsList(task: ApprovalTaskItem){
  if (!task || task.entity !== 'moneytree_posting') return
  openInModal('moneytree.transactions', '銀行明細', {
    mode: 'task',
    approvalTaskId: task.id
  })
}

function closePdfPreview(){
  if (pdfPreview.value.url) {
    URL.revokeObjectURL(pdfPreview.value.url)
  }
  pdfPreview.value = { visible: false, url: '', name: '', loading: false }
}

function queueApprovalCompletion(task: ApprovalTaskItem, status: string){
  const clone: ApprovalTaskItem = {
    ...task,
    status,
    updatedAt: new Date().toISOString()
  }
  approvalTasks.value = approvalTasks.value.filter(item => item.id !== task.id)
  approvalCompleted.value = approvalCompleted.value.filter(item => item.id !== task.id)
  approvalCompletionQueue.set(task.id, clone)
  void ensureCertificateRequestDetails([task])
}

function formatApprovalDate(value: string){
  if (!value) return '-'
  try{
    const d = new Date(value)
    if (!isNaN(d.getTime())){
      const month = String(d.getMonth() + 1).padStart(2, '0')
      const day = String(d.getDate()).padStart(2, '0')
      return `${d.getFullYear()}-${month}-${day}`
    }
  }catch{}
  return value || '-'
}

function formatApprovalApplicant(task?: ApprovalTaskItem | null): string {
  if (!task) return ''
  let code = normalizeText(task.applicantCode)
  let name = normalizeText(task.applicant)
  if (task.entity === 'certificate_request') {
    const detail = certificateRequestDetails[task.objectId]
    if (detail) {
      if (!code) code = detail.applicantCode
      if (!name) {
        name = detail.applicantName || (detail.applicantCode ? employeeProfileCache[detail.applicantCode] || '' : '')
      }
    }
  }
  if (name && code) return `${name} (${code})`
  if (name) return name
  if (code) return code
  return ''
}

function formatApprovalUser(task?: ApprovalTaskItem | null): string {
  if (!task) return ''
  const code = (task.userCode || '').toString().trim()
  const name = (task.userName || '').toString().trim()
  if (code && name) return `${code} ${name}`
  if (code) return code
  if (name) return name
  return ''
}

function formatApprovalRemark(task?: ApprovalTaskItem | null): string {
  if (!task) return ''
  const remark = (task.remark || '').toString().trim()
  return remark
}

function certificateDetail(task?: ApprovalTaskItem | null): CertificateRequestDetail | undefined {
  if (!task || task.entity !== 'certificate_request') return undefined
  const id = normalizeText(task.objectId)
  if (!id) return undefined
  return certificateRequestDetails[id]
}

function certificateApplicantName(task?: ApprovalTaskItem | null): string {
  if (!task || task.entity !== 'certificate_request') return ''
  const direct = normalizeText(task.applicant)
  if (direct) return direct
  const detail = certificateDetail(task)
  if (!detail) return ''
  if (detail.applicantName) return detail.applicantName
  if (detail.applicantCode) {
    const cached = employeeProfileCache[detail.applicantCode]
    if (cached) return cached
  }
  return ''
}

function certificateApplicantCode(task?: ApprovalTaskItem | null): string {
  if (!task || task.entity !== 'certificate_request') return ''
  const direct = normalizeText(task.applicantCode)
  if (direct) return direct
  const detail = certificateDetail(task)
  if (!detail) return ''
  return detail.applicantCode
}

function certificateApplicantNote(task?: ApprovalTaskItem | null): string {
  if (!task || task.entity !== 'certificate_request') return ''
  const detail = certificateDetail(task)
  if (!detail) return ''
  return detail.purpose || detail.bodyText || ''
}

function certificateApplicantResignReason(task?: ApprovalTaskItem | null): string {
  if (!task || task.entity !== 'certificate_request') return ''
  const detail = certificateDetail(task)
  if (!detail) return ''
  return detail.resignReason || ''
}

// Timesheet approval helpers
function timesheetApplicantName(task?: ApprovalTaskItem | null): string {
  if (!task || task.entity !== 'timesheet_submission') return ''
  const id = normalizeText(task.objectId)
  const detail = id ? timesheetSubmissionDetails[id] : undefined
  if (detail) {
    const code = normalizeText(detail.employeeCode)
    const name = normalizeText(detail.employeeName) || (code ? employeeProfileCache[code] : '')
    if (code && name) return `${code} ${name}`
    if (name) return name
    if (code) return code
  }
  const applicant = normalizeText(task.applicant)
  if (applicant) return applicant
  const userName = normalizeText(task.userName)
  if (userName) return userName
  return ''
}

function timesheetMonth(task?: ApprovalTaskItem | null): string {
  if (!task || task.entity !== 'timesheet_submission') return ''
  const id = normalizeText(task.objectId)
  const detail = id ? timesheetSubmissionDetails[id] : undefined
  if (detail && detail.month) return detail.month
  const stepName = task.stepName || ''
  const match = stepName.match(/(\d{4}-\d{2})/)
  if (match) return match[1]
  return ''
}

function formatHmHours(hours: number): string {
  const mins = Math.round((Number(hours) || 0) * 60)
  if (!mins) return '0H'
  const h = Math.floor(mins / 60)
  const m = mins % 60
  if (!m) return `${h}H`
  return `${h}H${m}M`
}

function timesheetTotalHours(task?: ApprovalTaskItem | null): string {
  if (!task || task.entity !== 'timesheet_submission') return ''
  const id = normalizeText(task.objectId)
  const detail = id ? timesheetSubmissionDetails[id] : undefined
  if (!detail) return ''
  return formatHmHours(detail.totalHours)
}

function timesheetTotalOvertime(task?: ApprovalTaskItem | null): string {
  if (!task || task.entity !== 'timesheet_submission') return ''
  const id = normalizeText(task.objectId)
  const detail = id ? timesheetSubmissionDetails[id] : undefined
  if (!detail) return ''
  return formatHmHours(detail.totalOvertime)
}

function timesheetWorkDays(task?: ApprovalTaskItem | null): string {
  if (!task || task.entity !== 'timesheet_submission') return ''
  const id = normalizeText(task.objectId)
  const detail = id ? timesheetSubmissionDetails[id] : undefined
  if (!detail) return ''
  return `${detail.workDays || 0}日`
}

function ensurePlainObject<T extends Record<string, any>>(value: any): T {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return {} as T
  }
  return value as T
}

function salesOrderTaskLabel(task?: SalesOrderAgentTask | null, index?: number): string {
  if (!task) return ''
  const direct =
    typeof task.displayLabel === 'string' && task.displayLabel ? task.displayLabel.trim() : ''
  if (direct) return direct
  if (typeof index === 'number' && index >= 0) {
    return `#${index + 1}`
  }
  const fallback = (text.value?.chat as any)?.salesOrderTaskLabel
  if (typeof fallback === 'string' && fallback.trim()) return fallback.trim()
  return localize('受注', '受注', 'Sales Order')
}

function agentTaskDisplayLabel(task?: AgentTask | null, index?: number): string {
  if (!task) return ''
  const raw =
    typeof (task as any)?.displayLabel === 'string' && (task as any).displayLabel
      ? (task as any).displayLabel.trim()
      : ''
  if (raw) return raw
  if (isSalesOrderTask(task)) {
    return salesOrderTaskLabel(task, index)
  }
  if (isPayrollTask(task)) {
    if (task.employeeName) return task.employeeName
    if (task.employeeCode) return task.employeeCode
    return localize('給与', '工资', 'Payroll')
  }
  if (isInvoiceTask(task) && typeof task.label === 'string' && task.label.trim()) {
    return task.label.trim()
  }
  if (typeof index === 'number' && index >= 0) {
    return `#${index + 1}`
  }
  return ''
}

function salesOrderMeta(task?: SalesOrderAgentTask | null) {
  const payload = ensurePlainObject<Record<string, any>>(task?.payload)
  const metadata = ensurePlainObject<Record<string, any>>(task?.metadata)
  const orderNo =
    typeof task?.salesOrderNo === 'string' && task.salesOrderNo
      ? task.salesOrderNo
      : typeof payload.soNo === 'string' && payload.soNo
        ? payload.soNo
        : typeof payload.orderNo === 'string' && payload.orderNo
          ? payload.orderNo
          : typeof metadata.salesOrderNo === 'string' && metadata.salesOrderNo
            ? metadata.salesOrderNo
            : ''
  const orderDate =
    typeof payload.orderDate === 'string' && payload.orderDate
      ? payload.orderDate
      : typeof metadata.orderDate === 'string' && metadata.orderDate
        ? metadata.orderDate
        : ''
  const deliveryDate =
    typeof payload.requestedDeliveryDate === 'string' && payload.requestedDeliveryDate
      ? payload.requestedDeliveryDate
      : typeof metadata.deliveryDate === 'string' && metadata.deliveryDate
        ? metadata.deliveryDate
        : ''
  const customerName =
    typeof task?.customerName === 'string' && task.customerName
      ? task.customerName
      : typeof payload.partnerName === 'string' && payload.partnerName
        ? payload.partnerName
        : typeof payload.customerName === 'string' && payload.customerName
          ? payload.customerName
          : typeof metadata.customerName === 'string' && metadata.customerName
            ? metadata.customerName
            : ''
  const customerCode =
    typeof task?.customerCode === 'string' && task.customerCode
      ? task.customerCode
      : typeof payload.partnerCode === 'string' && payload.partnerCode
        ? payload.partnerCode
        : typeof payload.customerCode === 'string' && payload.customerCode
          ? payload.customerCode
          : typeof metadata.customerCode === 'string' && metadata.customerCode
            ? metadata.customerCode
            : ''
  const amountRaw = payload.amountTotal ?? payload.totalAmount ?? metadata.totalAmount ?? 0
  const amount = Number(isFinite(Number(amountRaw)) ? Number(amountRaw) : 0)
  const currencyRaw = payload.currency ?? metadata.currency ?? 'JPY'
  const currency = typeof currencyRaw === 'string' && currencyRaw ? currencyRaw.toUpperCase() : 'JPY'
  const shipTo = ensurePlainObject<Record<string, any>>(payload.shipTo ?? metadata.shipTo)
  const lines = Array.isArray(payload.lines) ? payload.lines : []
  return {
    orderNo,
    orderDate,
    deliveryDate,
    customerName,
    customerCode,
    amount,
    currency,
    shipTo,
    lines
  }
}

function formatSalesOrderTitle(task?: SalesOrderAgentTask | null): string {
  if (!task) return localize('受注タスク', '受注任务', 'Sales Order Task')
  const meta = salesOrderMeta(task)
  if (meta.orderNo) return meta.orderNo
  if (meta.customerName && meta.customerCode) return `${meta.customerName} (${meta.customerCode})`
  if (meta.customerName) return meta.customerName
  if (meta.customerCode) return meta.customerCode
  return task.summary || localize('受注タスク', '受注任务', 'Sales Order Task')
}

function formatSalesOrderSummary(task?: SalesOrderAgentTask | null): string {
  if (!task) return ''
  if (task.summary) return task.summary
  const meta = salesOrderMeta(task)
  const parts: string[] = []
  const customer = salesOrderCustomer(task)
  if (customer) parts.push(customer)
  const amount = salesOrderTotalDisplay(task)
  if (amount) parts.push(amount)
  const delivery = salesOrderDeliveryDate(task)
  if (delivery) parts.push(`${localize('納期', '交期', 'Delivery')}: ${delivery}`)
  return parts.join(' / ')
}

function payrollDiffDisplay(task?: PayrollAgentTask | null): string {
  if (!task?.diffSummary) return ''
  const diff = ensurePlainObject<Record<string, any>>(task.diffSummary)
  const amountDiff = Number(diff.difference ?? diff.differenceAmount ?? 0)
  const percentDiff = Number(diff.differencePercent ?? diff.differenceRatio ?? 0)
  const parts: string[] = []
  if (amountDiff) {
    const sign = amountDiff >= 0 ? '+' : ''
    parts.push(`Δ ${sign}${Math.round(amountDiff).toLocaleString('ja-JP')}円`)
  }
  if (percentDiff) {
    parts.push(`${(percentDiff * 100).toFixed(1)}%`)
  }
  return parts.join(' / ')
}

function formatPayrollTaskTitle(task?: PayrollAgentTask | null): string {
  if (!task) return localize('給与タスク', '工资任务', 'Payroll Task')
  const base = task.employeeName || task.employeeCode || task.employeeId
  if (task.periodMonth) return `${base}｜${task.periodMonth}`
  return base
}

function formatPayrollTaskSummary(task?: PayrollAgentTask | null): string {
  if (!task) return ''
  if (task.summary) return task.summary
  const diff = payrollDiffDisplay(task)
  const parts: string[] = []
  if (diff) parts.push(diff)
  if (task.metadata?.totalAmount) {
    parts.push(`${localize('支給額', '支給額', 'Total')}: ${formatAmount(task.metadata.totalAmount)}`)
  }
  return parts.join(' / ')
}

function salesOrderNumber(task?: SalesOrderAgentTask | null): string {
  const meta = salesOrderMeta(task)
  return meta.orderNo || ''
}

function salesOrderCustomer(task?: SalesOrderAgentTask | null): string {
  const meta = salesOrderMeta(task)
  const name = (meta.customerName || '').trim()
  const code = (meta.customerCode || '').trim()
  if (name && code) return `${name} (${code})`
  return name || code
}

function salesOrderOrderDate(task?: SalesOrderAgentTask | null): string {
  const meta = salesOrderMeta(task)
  return (meta.orderDate || '').trim()
}

function salesOrderDeliveryDate(task?: SalesOrderAgentTask | null): string {
  const meta = salesOrderMeta(task)
  return (meta.deliveryDate || '').trim()
}

function salesOrderCurrency(task?: SalesOrderAgentTask | null): string {
  const meta = salesOrderMeta(task)
  return meta.currency || 'JPY'
}

function salesOrderTotalDisplay(task?: SalesOrderAgentTask | null): string {
  const meta = salesOrderMeta(task)
  const amount = Number(meta.amount || 0)
  if (!Number.isFinite(amount) || amount <= 0) return ''
  return formatCurrencyAmount(amount, meta.currency)
}

function salesOrderShipToDisplay(task?: SalesOrderAgentTask | null): string {
  const meta = salesOrderMeta(task)
  const ship = ensurePlainObject<Record<string, any>>(meta.shipTo)
  if (!ship || Object.keys(ship).length === 0) return ''
  const parts: string[] = []
  const addressText = typeof ship.addressText === 'string' && ship.addressText.trim() ? ship.addressText.trim() : ''
  if (addressText) parts.push(addressText)
  const addressObj = ensurePlainObject<Record<string, any>>(ship.address)
  if (!addressText && Object.keys(addressObj).length > 0) {
    const fields = [
      addressObj.postalCode ?? addressObj.zip,
      addressObj.country,
      addressObj.state ?? addressObj.prefecture,
      addressObj.city,
      addressObj.district,
      addressObj.address1 ?? addressObj.line1,
      addressObj.address2 ?? addressObj.line2
    ]
    const formatted = fields
      .map(value => (typeof value === 'string' ? value.trim() : ''))
      .filter(value => !!value)
      .join(' ')
    if (formatted) parts.push(formatted)
  }
  const contactObj = ensurePlainObject<Record<string, any>>(ship.contact)
  const contactName = typeof ship.contact === 'string' && ship.contact.trim()
    ? ship.contact.trim()
    : (typeof contactObj.name === 'string' && contactObj.name.trim()
        ? contactObj.name.trim()
        : '')
  if (contactName) parts.push(contactName)
  const phone =
    typeof ship.phone === 'string' && ship.phone.trim()
      ? ship.phone.trim()
      : (typeof contactObj.phone === 'string' && contactObj.phone.trim() ? contactObj.phone.trim() : '')
  if (phone) parts.push(phone)
  return parts.join(' / ')
}

function salesOrderLines(task?: SalesOrderAgentTask | null): SalesOrderLine[] {
  const meta = salesOrderMeta(task)
  const linesRaw = Array.isArray(meta.lines) ? meta.lines : []
  const list: SalesOrderLine[] = []
  linesRaw.forEach((entry: any, index: number) => {
    if (!entry || typeof entry !== 'object') return
    const obj = ensurePlainObject<Record<string, any>>(entry)
    const lineNoValue = Number(obj.lineNo ?? obj.line_no ?? index + 1)
    const lineNo = Number.isFinite(lineNoValue) ? lineNoValue : index + 1
    const materialCode =
      typeof obj.materialCode === 'string' && obj.materialCode
        ? obj.materialCode
        : typeof obj.code === 'string' && obj.code
          ? obj.code
          : ''
    const materialName =
      typeof obj.materialName === 'string' && obj.materialName
        ? obj.materialName
        : typeof obj.name === 'string' && obj.name
          ? obj.name
          : ''
    const description = typeof obj.description === 'string' && obj.description ? obj.description : ''
    const quantityVal = Number(obj.quantity ?? obj.qty ?? 0)
    const quantity = Number.isFinite(quantityVal) ? quantityVal : 0
    const uom =
      typeof obj.uom === 'string' && obj.uom
        ? obj.uom
        : typeof obj.unit === 'string' && obj.unit
          ? obj.unit
          : ''
    const unitPriceVal = Number(obj.unitPrice ?? obj.price ?? 0)
    const unitPrice = Number.isFinite(unitPriceVal) ? unitPriceVal : 0
    let amount = Number(obj.amount ?? obj.total ?? unitPrice * quantity)
    if (!Number.isFinite(amount)) amount = 0
    const note =
      typeof obj.note === 'string' && obj.note
        ? obj.note
        : typeof obj.remark === 'string' && obj.remark
          ? obj.remark
          : ''
    list.push({
      lineNo,
      materialCode,
      materialName,
      description,
      quantity,
      uom,
      unitPrice,
      amount,
      note
    })
  })
  return list
}

function formatSalesOrderQuantity(line: SalesOrderLine): string {
  const qty = Number(line.quantity || 0)
  const qtyText = Number.isFinite(qty) ? qty.toLocaleString('ja-JP') : '0'
  const uom = typeof line.uom === 'string' && line.uom.trim() ? line.uom.trim() : ''
  return uom ? `${qtyText} ${uom}` : qtyText
}

function formatCurrencyAmount(amount: number, currency: string): string {
  const value = Number(amount || 0)
  const formatted = Number.isFinite(value) ? value.toLocaleString('ja-JP') : '0'
  const code = typeof currency === 'string' && currency.trim() ? currency.trim().toUpperCase() : ''
  return code ? `${formatted} ${code}` : formatted
}

async function resolveSalesOrderPayload(task: SalesOrderAgentTask): Promise<Record<string, any>> {
  const existing = ensurePlainObject<Record<string, any>>(task.payload)
  if (Object.keys(existing).length > 0) return existing
  const metadata = ensurePlainObject<Record<string, any>>(task.metadata)
  const idCandidates = [
    task.salesOrderId,
    metadata.salesOrderId,
    metadata.id,
    existing.id
  ].map(value => (typeof value === 'string' && value.trim() ? value.trim() : ''))

  for (const id of idCandidates) {
    if (!id) continue
    try {
      const resp = await api.get(`/objects/sales_order/${encodeURIComponent(id)}`)
      const payload = ensurePlainObject<Record<string, any>>(resp.data?.payload ?? resp.data)
      if (Object.keys(payload).length > 0) return payload
    } catch {}
  }

  const meta = salesOrderMeta(task)
  const numberCandidates = [
    task.salesOrderNo,
    meta.orderNo,
    existing.soNo,
    existing.orderNo,
    metadata.salesOrderNo
  ].map(value => (typeof value === 'string' && value.trim() ? value.trim() : ''))

  for (const soNo of numberCandidates) {
    if (!soNo) continue
    try {
      const resp = await api.post('/objects/sales_order/search', {
        page: 1,
        pageSize: 1,
        where: [{ field: 'so_no', op: 'eq', value: soNo }]
      })
      const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
      if (rows.length > 0) {
        const match = rows[0]
        const payload = ensurePlainObject<Record<string, any>>(match?.payload ?? match)
        if (Object.keys(payload).length > 0) return payload
      }
    } catch {}
  }

  if (Object.keys(existing).length > 0) return existing
  const metaFallback: Record<string, any> = { ...meta }
  if (meta.orderNo) metaFallback.soNo = meta.orderNo
  return metaFallback
}

async function openSalesOrderDetail(task?: SalesOrderAgentTask | null) {
  if (!task) return
  salesOrderDetailDialog.visible = true
  salesOrderDetailDialog.loading = true
  salesOrderDetailDialog.error = ''
  salesOrderDetailDialog.task = { ...task }
  try {
    const payload = await resolveSalesOrderPayload(task)
    salesOrderDetailDialog.task = { ...task, payload }
    if (!payload || Object.keys(payload).length === 0) {
      const meta = salesOrderMeta(task)
      if (!meta.orderNo && !meta.customerCode && !meta.customerName) {
        salesOrderDetailDialog.error = localize('受注詳細の取得に失敗しました。', '无法获取受注详情。', 'Failed to load sales order detail.')
      }
    }
  } catch (e: any) {
    salesOrderDetailDialog.error =
      e?.response?.data?.error || e?.message || localize('受注詳細の取得に失敗しました。', '无法获取受注详情。', 'Failed to load sales order detail.')
  } finally {
    salesOrderDetailDialog.loading = false
  }
}

const taskList = ref<AgentTask[]>([])
const approvalTasks = ref<ApprovalTaskItem[]>([])
const approvalCompleted = ref<ApprovalTaskItem[]>([])
const approvalActionLoadingId = ref('')
const approvalDownloadLoadingId = ref('')
const pdfPreview = ref<{ visible: boolean; url: string; name: string; loading: boolean }>({ visible: false, url: '', name: '', loading: false })
const activeTaskId = ref('')
const preferGeneralMode = ref(false)
const showCompletedTasks = ref(false)
const hasAnyTasks = computed(() => taskList.value.length > 0 || approvalTasks.value.length > 0 || approvalCompleted.value.length > 0)
const generalModeActive = computed(() => preferGeneralMode.value || !activeTaskId.value)

// ==================== Task Context for input area ====================
const activeTaskContext = computed(() => {
  if (generalModeActive.value || !activeTaskId.value) return null
  const task = taskList.value.find(t => t.id === activeTaskId.value)
  if (!task) return null

  // Use agentTaskDisplayLabel() for consistent labeling with left panel and task sections
  const label = agentTaskDisplayLabel(task)
  let title = ''
  let voucherNo = ''

  if (isInvoiceTask(task)) {
    title = task.title || task.summary || task.fileName || ''
    const match = task.summary?.match(/\d{10}/)
    if (match) voucherNo = match[0]
  } else {
    title = (task as any).title || (task as any).summary || ''
  }

  return { id: task.id, label, title, voucherNo }
})

const chatPlaceholder = computed(() => {
  if (activeTaskContext.value) {
    const ctx = activeTaskContext.value
    const taskDesc = ctx.voucherNo ? `${ctx.label} (${ctx.voucherNo})` : (ctx.label || ctx.title)
    return localize(
      `${taskDesc} について入力... 例：「日付を1/20に変更」「科目を変更」`,
      `针对 ${taskDesc} 输入... 例："把日期改成1月20日""修改科目"`,
      `About ${taskDesc}... e.g. "change date to 1/20"`
    )
  }
  return text.value?.chat?.placeholder || localize('AI と会話する...', 'AI 对话...', 'Chat with AI...')
})

const approvalCompletionQueue = new Map<string, ApprovalTaskItem>()
let approvalReloadTimer: ReturnType<typeof setTimeout> | null = null

const taskByDocumentSession = computed(() => {
  const map = new Map<string, InvoiceAgentTask>()
  taskList.value.forEach(task => {
    if (!isInvoiceTask(task)) return
    const key = task.documentSessionId ? task.documentSessionId.toLowerCase() : ''
    if (key) map.set(key, task)
  })
  return map
})

const taskByFileId = computed(() => {
  const map = new Map<string, InvoiceAgentTask>()
  taskList.value.forEach(task => {
    if (!isInvoiceTask(task)) return
    map.set(task.fileId, task)
  })
  return map
})

const taskAttachments = reactive<Record<string, any>>({})

const salesOrderDetailDialog = reactive<{
  visible: boolean
  loading: boolean
  task: SalesOrderAgentTask | null
  error: string
}>({
  visible: false,
  loading: false,
  task: null,
  error: ''
})

const timesheetDetailDialog = reactive<{
  visible: boolean
  loading: boolean
  error: string
  title: string
  month: string
  applicant: string
  creatorUserId: string
  totalHoursText: string
  totalOvertimeText: string
  workDaysText: string
  statusLabel: string
  rows: Array<{
    id: string
    date: string
    startTime: string
    endTime: string
    lunchMinutes: number | null
    hours: number
    overtime: number
    task: string
    status: string
    isHoliday: boolean
  }>
}>({
  visible: false,
  loading: false,
  error: '',
  title: '',
  month: '',
  applicant: '',
  creatorUserId: '',
  totalHoursText: '',
  totalOvertimeText: '',
  workDaysText: '',
  statusLabel: '',
  rows: []
})


const knownTaskIds = computed<Set<string>>(() => {
  const set = new Set<string>()
  taskList.value.forEach(task => {
    set.add(task.id)
  })
  approvalTasks.value.forEach(task => {
    set.add(task.id)
    set.add(`approval:${task.id}`)
  })
  approvalCompleted.value.forEach(task => {
    set.add(task.id)
    set.add(`approval:${task.id}`)
  })
  return set
})

const salesOrderDetailMeta = computed(() => salesOrderMeta(salesOrderDetailDialog.task ?? null))
const salesOrderDetailLinesList = computed(() => salesOrderLines(salesOrderDetailDialog.task ?? null))
const salesOrderDetailCurrencyCode = computed(() => salesOrderCurrency(salesOrderDetailDialog.task ?? null))
const salesOrderDetailPayload = computed(() => ensurePlainObject<Record<string, any>>(salesOrderDetailDialog.task?.payload))


const pendingTasks = computed<TaskListItem[]>(() => {
  const invoiceItems: TaskListItem[] = taskList.value
    .filter(task => {
      const status = (task.status || '').toLowerCase()
      return status !== 'completed' && status !== 'cancelled'
    })
    .filter(isInvoiceTask)
    .map(task => ({
      id: task.id,
      kind: 'invoice' as TaskKind,
      status: task.status || 'pending',
      label: agentTaskDisplayLabel(task),
      title: task.fileName || task.label || task.id,
      summary: task.summary,
      createdAt: task.createdAt,
      updatedAt: task.updatedAt,
      invoiceTask: task
    }))

  const salesTasks = taskList.value.filter(isSalesOrderTask)
  const salesItems: TaskListItem[] = salesTasks
    .filter(task => {
      const status = (task.status || '').toLowerCase()
      return status !== 'completed' && status !== 'cancelled'
    })
    .map((task, index) => ({
      id: task.id,
      kind: 'sales_order' as TaskKind,
      status: task.status || 'pending',
      label: agentTaskDisplayLabel(task, index),
      title: formatSalesOrderTitle(task),
      summary: formatSalesOrderSummary(task),
      createdAt: task.createdAt,
      updatedAt: task.updatedAt,
      salesOrderTask: task
    }))

  const approvalItems: TaskListItem[] = approvalTasks.value.map((task, index) => ({
    id: `approval:${task.id}`,
    kind: 'approval',
    status: task.status || 'pending',
    label: `#${index + 1}`,
    title: task.title,
    summary: task.summary,
    createdAt: task.createdAt,
    updatedAt: task.updatedAt,
    approvalTask: task
  }))

  return [...invoiceItems, ...salesItems, ...approvalItems].sort((a, b) => compareByTimestampAsc(toTimestamp(a.createdAt), toTimestamp(b.createdAt)))
})

const completedTasks = computed<TaskListItem[]>(() => {
  const invoiceItems: TaskListItem[] = taskList.value
    .filter(task => {
      const status = (task.status || '').toLowerCase()
      return status === 'completed' || status === 'cancelled'
    })
    .filter(isInvoiceTask)
    .map(task => ({
      id: task.id,
      kind: 'invoice' as TaskKind,
      status: task.status || 'completed',
      label: agentTaskDisplayLabel(task),
      title: task.fileName || task.label || task.id,
      summary: task.summary,
      createdAt: task.createdAt,
      updatedAt: task.updatedAt,
      invoiceTask: task
    }))

  const completedSalesTasks = taskList.value.filter(isSalesOrderTask)
  const salesItems: TaskListItem[] = completedSalesTasks
    .filter(task => {
      const status = (task.status || '').toLowerCase()
      return status === 'completed' || status === 'cancelled'
    })
    .map((task, index) => ({
      id: task.id,
      kind: 'sales_order' as TaskKind,
      status: task.status || 'completed',
      label: agentTaskDisplayLabel(task, index),
      title: formatSalesOrderTitle(task),
      summary: formatSalesOrderSummary(task),
      createdAt: task.createdAt,
      updatedAt: task.updatedAt,
      salesOrderTask: task
    }))

  const approvalItems: TaskListItem[] = approvalCompleted.value.map((task, index) => ({
    id: `approval:${task.id}`,
    kind: 'approval' as TaskKind,
    status: task.status || 'completed',
    label: `#${index + 1}`,
    title: task.title,
    summary: task.summary,
    createdAt: task.createdAt,
    updatedAt: task.updatedAt,
    approvalTask: task
  }))

  return [...invoiceItems, ...salesItems, ...approvalItems].sort((a, b) => compareByTimestampAsc(toTimestamp(b.updatedAt || b.createdAt), toTimestamp(a.updatedAt || a.createdAt)))
})

const completedTasksHeaderText = computed(() => {
  const chatSection = text.value?.chat as Record<string, any> | undefined
  const template = typeof chatSection?.completedTasksCount === 'string' ? chatSection.completedTasksCount : ''
  if (template.includes('{count}')) {
    return template.replace('{count}', String(completedTasks.value.length))
  }
  const base = typeof chatSection?.completedTasksTitle === 'string' && chatSection.completedTasksTitle
    ? chatSection.completedTasksTitle
    : '完了タスク'
  return `${base}（${completedTasks.value.length}）`
})

function cloneUploadDocument(doc: any){
  if (!doc || typeof doc !== 'object') return null
  if (typeof structuredClone === 'function'){
    try{
      return structuredClone(doc)
    }catch{}
  }
  try{
    return JSON.parse(JSON.stringify(doc))
  }catch{
    return null
  }
}

function expandUploadMessages(list: any[]): any[] {
  if (!Array.isArray(list) || list.length === 0) return Array.isArray(list) ? list.slice() : []
  const result: any[] = []
  for (const msg of list){
    const payloadKind = msg?.payload?.kind
    const documents = Array.isArray(msg?.payload?.documents) ? msg.payload.documents : null
    if (payloadKind === 'user.uploadBatch' && documents && documents.length > 0){
      const taskMap = new Map<string, any>()
      const rawTasks = Array.isArray(msg?.payload?.tasks) ? msg.payload.tasks : []
      rawTasks.forEach((task: any) => {
        const fileId = typeof task?.fileId === 'string' ? task.fileId : ''
        if (fileId) taskMap.set(fileId, task)
      })

      documents.forEach((doc: any) => {
        const docClone = cloneUploadDocument(doc)
        if (!docClone) return
        const docPayload = {
          kind: 'user.upload',
          attachments: [docClone],
          documents: [docClone],
          documentSessionId: docClone.documentSessionId,
          documentLabel: docClone.documentLabel,
          fileId: docClone.fileId
        }
        const taskInfo = docClone.fileId ? taskMap.get(docClone.fileId) : undefined
        const contentPieces: string[] = []
        const uploadedLabel = localize('アップロード済み', '已上传', 'Uploaded')
        if (typeof docClone.documentLabel === 'string' && docClone.documentLabel.trim()){
          contentPieces.push(`${uploadedLabel} ${docClone.documentLabel}`)
        } else if (typeof docClone.fileName === 'string' && docClone.fileName.trim()){
          contentPieces.push(`${uploadedLabel} ${docClone.fileName}`)
        }
        const docMessage: any = {
          role: msg.role,
          kind: 'user.upload',
          content: contentPieces.length
            ? contentPieces.join(lang.value === 'en' ? ' / ' : '、')
            : msg.content || '',
          createdAt: msg.createdAt,
          payload: docPayload
        }
        if (msg.status) docMessage.status = msg.status
        if (msg.tag) docMessage.tag = msg.tag
        if (taskInfo?.taskId && typeof taskInfo.taskId === 'string' && taskInfo.taskId.trim()){
          docMessage.taskId = taskInfo.taskId.trim()
        } else if (typeof docClone.documentSessionId === 'string' && docClone.documentSessionId.trim()){
          const mappedTask = taskByDocumentSession.value.get(docClone.documentSessionId.trim().toLowerCase())
          if (mappedTask) docMessage.taskId = mappedTask.id
        }
        if (!docMessage.taskId && rawTasks.length === 1){
          const singleTaskId = rawTasks[0]?.taskId
          if (typeof singleTaskId === 'string' && singleTaskId.trim()){
            docMessage.taskId = singleTaskId.trim()
          }
        }
        const normalizedAttachment = normalizeAttachment(docClone)
        if (docMessage.taskId && normalizedAttachment){
          taskAttachments[docMessage.taskId] = normalizedAttachment
        }
        if (docMessage.taskId){
          (docPayload as any).taskId = docMessage.taskId
        }
        result.push(docMessage)
      })
      continue
    }
    result.push(msg)
  }
  return result
}

function resolveTaskIdFromSessionId(value: unknown): string {
  if (typeof value !== 'string') return ''
  const normalized = value.trim()
  if (!normalized) return ''
  const mapped = taskByDocumentSession.value.get(normalized.toLowerCase())
  return mapped ? mapped.id : ''
}

function resolveTaskIdFromDocumentId(value: unknown): string {
  if (typeof value !== 'string') return ''
  const normalized = value.trim()
  if (!normalized) return ''
  const mapped = taskByFileId.value.get(normalized)
  return mapped ? mapped.id : ''
}

function resolveTaskIdFromLabel(value: unknown): string {
  if (typeof value !== 'string') return ''
  const normalized = value.trim()
  if (!normalized) return ''
  const matched = taskList.value.find(task => task.label === normalized)
  return matched ? matched.id : ''
}

function matchKnownTaskId(candidate: string): string {
  const trimmed = typeof candidate === 'string' ? candidate.trim() : ''
  if (!trimmed) return ''
  if (knownTaskIds.value.has(trimmed)) return trimmed
  if (!trimmed.startsWith('approval:')) {
    const approvalKey = `approval:${trimmed}`
    if (knownTaskIds.value.has(approvalKey)) return approvalKey
  }
  return ''
}

function resolveMessageTaskId(msg: any, allowFallback = true): string {
  if (!msg || typeof msg !== 'object') return ''
  const directId = typeof msg.taskId === 'string' ? msg.taskId.trim() : ''
  const directKnown = matchKnownTaskId(directId)
  if (directKnown) return directKnown

  const taskIdCandidates: string[] = []
  const sessionCandidates: string[] = []
  const documentCandidates: string[] = []
  const labelCandidates: string[] = []

  const collectFromSource = (source: any) => {
    if (!source || typeof source !== 'object') return
    const tid = typeof source.taskId === 'string' ? source.taskId : (typeof source.task_id === 'string' ? source.task_id : '')
    if (tid && tid.trim()) taskIdCandidates.push(tid.trim())
    if (Array.isArray(source.taskIds)) {
      source.taskIds.forEach((id: any) => {
        if (typeof id === 'string' && id.trim()) taskIdCandidates.push(id.trim())
      })
    }
    if (Array.isArray(source.task_ids)) {
      source.task_ids.forEach((id: any) => {
        if (typeof id === 'string' && id.trim()) taskIdCandidates.push(id.trim())
      })
    }
    const sessionId = typeof source.documentSessionId === 'string'
      ? source.documentSessionId
      : (typeof source.document_session_id === 'string' ? source.document_session_id : '')
    if (sessionId && sessionId.trim()) sessionCandidates.push(sessionId.trim())
    const docId = typeof source.documentId === 'string'
      ? source.documentId
      : (typeof source.document_id === 'string' ? source.document_id : '')
    if (docId && docId.trim()) documentCandidates.push(docId.trim())
    const label = typeof source.documentLabel === 'string'
      ? source.documentLabel
      : (typeof source.label === 'string' ? source.label : '')
    if (label && label.trim()) labelCandidates.push(label.trim())
  }

  collectFromSource(msg.tag)
  collectFromSource(msg.payload)

  const attachments = getMessageAttachments(msg)
  if (Array.isArray(attachments)) {
    attachments.forEach((att: any) => {
      collectFromSource(att)
    })
  }

  for (const candidate of taskIdCandidates) {
    const known = matchKnownTaskId(candidate)
    if (known) return known
  }

  for (const sessionId of sessionCandidates) {
    const mapped = resolveTaskIdFromSessionId(sessionId)
    if (mapped) return mapped
  }

  for (const documentId of documentCandidates) {
    const mapped = resolveTaskIdFromDocumentId(documentId)
    if (mapped) return mapped
  }

  for (const label of labelCandidates) {
    const mapped = resolveTaskIdFromLabel(label)
    if (mapped) return mapped
  }

  if (!allowFallback) return ''

  const activeKnown = matchKnownTaskId(activeTaskId.value)
  if (activeKnown) return activeKnown
  if (taskList.value.length === 1) {
    const singleKnown = matchKnownTaskId(taskList.value[0].id)
    if (singleKnown) return singleKnown
  }
  const pending = taskList.value.find(task => task.status === 'pending')
  const pendingKnown = pending ? matchKnownTaskId(pending.id) : ''
  if (pendingKnown) return pendingKnown
  if (taskList.value.length > 0) {
    const fallbackKnown = matchKnownTaskId(taskList.value[0].id)
    if (fallbackKnown) return fallbackKnown
  }
  return ''
}

function shouldHideMessage(msg: any): boolean {
  if (!msg || typeof msg !== 'object') return false
  const payloadKind = typeof msg?.payload?.kind === 'string' ? msg.payload.kind : ''
  if (payloadKind === 'user.upload') return true
  const msgKind = typeof msg.kind === 'string' ? msg.kind : ''
  if (msgKind === 'user.upload') return true
  if (payloadKind === 'plan.summary' || payloadKind === 'task.plan') return true
  const tagKind = typeof msg?.tag?.kind === 'string' ? msg.tag.kind : ''
  if (tagKind === 'plan.summary' || tagKind === 'task.plan') return true
  const content = typeof msg.content === 'string' ? msg.content.trim() : ''
  if (content.startsWith('AI 任务分组结果')) return true
  const answerTo = typeof msg?.payload?.answerTo === 'string' ? msg.payload.answerTo.trim() : ''
  if (answerTo) return true
  if (isClarificationEchoMessage(msg)) return true
  return false
}

function toTimestamp(value?: string | null): number {
  if (!value || typeof value !== 'string') return 0
  const parsed = Date.parse(value)
  return Number.isNaN(parsed) ? 0 : parsed
}

function resolveMessageTimestamp(msg: any): string | undefined {
  if (!msg || typeof msg !== 'object') return undefined
  if (typeof msg.createdAt === 'string' && msg.createdAt) return msg.createdAt
  if (typeof msg.created_at === 'string' && msg.created_at) return msg.created_at
  if (typeof msg.timestamp === 'string' && msg.timestamp) return msg.timestamp
  if (typeof msg.time === 'string' && msg.time) return msg.time
  return undefined
}

function latestMessageTimestamp(list: any[]): string | undefined {
  if (!Array.isArray(list) || list.length === 0) return undefined
  let best: string | undefined
  let bestTime = 0
  for (const item of list) {
    const ts = resolveMessageTimestamp(item)
    const value = toTimestamp(ts)
    if (value > bestTime) {
      bestTime = value
      best = ts
    }
  }
  return best
}

function compareByTimestampAsc(aTs: number, bTs: number, aIndex = 0, bIndex = 0): number {
  const diff = aTs - bTs
  if (diff !== 0) return diff
  return aIndex - bIndex
}

const groupedMessages = computed(() => {
  const groups: Record<string, { msg: any; index: number }[]> = {}
  messages.forEach((msg: any, index: number) => {
    if (!msg) return
    if (shouldHideMessage(msg)) return
    if (msg.kind === 'embed' && !msg.taskId) return
    const resolvedId = resolveMessageTaskId(msg, false)
    if (!resolvedId) return
    if (!groups[resolvedId]) groups[resolvedId] = []
    groups[resolvedId].push({ msg, index })
  })
  const sortedGroups: Record<string, any[]> = {}
  Object.keys(groups).forEach(taskId => {
    const list = groups[taskId]
    list.sort((a, b) => {
      const aTs = toTimestamp(resolveMessageTimestamp(a.msg))
      const bTs = toTimestamp(resolveMessageTimestamp(b.msg))
      const diff = compareByTimestampAsc(aTs, bTs, a.index, b.index)
      if (diff !== 0) return diff
      return a.index - b.index
    })
    sortedGroups[taskId] = list.map(item => item.msg)
  })
  return sortedGroups
})

const timelineMessages = computed(() => {
  const rows: { msg: any; index: number; ts: number }[] = []
  messages.forEach((msg: any, index: number) => {
    if (!msg) return
    if (shouldHideMessage(msg)) return
    if (msg.kind === 'embed') return
    const resolvedId = resolveMessageTaskId(msg, false)
    if (resolvedId) return
    const ts = toTimestamp(resolveMessageTimestamp(msg))
    rows.push({ msg, index, ts })
  })
  rows.sort((a, b) => compareByTimestampAsc(a.ts, b.ts, a.index, b.index))
  return rows.map(item => item.msg)
})

const taskSections = computed<TaskSectionItem[]>(() => {
  const sections: TaskSectionItem[] = []
  const taskSectionMap = new Map<string, TaskSectionItem>()
  const approvalSectionMap = new Map<string, TaskSectionItem>()

  taskList.value.forEach((task, index) => {
    const taskMessages = groupedMessages.value[task.id] ?? []
    const latestMsgTs = latestMessageTimestamp(taskMessages)
    const taskCreatedTs = toTimestamp(task.createdAt)
    const messageTs = toTimestamp(latestMsgTs)
    const startedAtValue = messageTs > taskCreatedTs
      ? (latestMsgTs || task.createdAt)
      : task.createdAt
    const startedAtTs = Math.max(messageTs, taskCreatedTs)
    if (isInvoiceTask(task)) {
      const section: TaskSectionItem = {
        id: task.id,
        kind: 'invoice' as TaskKind,
        status: task.status || 'pending',
        label: agentTaskDisplayLabel(task, index),
        title: task.fileName || task.label || task.id,
        summary: task.summary,
        invoiceTask: task,
        messages: taskMessages,
        startedAt: startedAtValue,
        startedAtTs
      }
      sections.push(section)
      taskSectionMap.set(task.id, section)
      return
    }
    if (isSalesOrderTask(task)) {
      const section: TaskSectionItem = {
        id: task.id,
        kind: 'sales_order' as TaskKind,
        status: task.status || 'pending',
        label: agentTaskDisplayLabel(task, index),
        title: formatSalesOrderTitle(task),
        summary: formatSalesOrderSummary(task),
        salesOrderTask: task,
        messages: taskMessages,
        startedAt: startedAtValue,
        startedAtTs
      }
      sections.push(section)
      taskSectionMap.set(task.id, section)
      return
    }
  })

  Object.keys(groupedMessages.value).forEach(taskId => {
    if (!taskSectionMap.has(taskId)) {
      const messageList = groupedMessages.value[taskId] ?? []
      const startedAt = latestMessageTimestamp(messageList)
      const startedAtTs = toTimestamp(startedAt)
      if (approvalSectionMap.has(taskId)) {
        const existing = approvalSectionMap.get(taskId)!
        existing.messages = messageList
        existing.startedAt = startedAt
        existing.startedAtTs = startedAtTs
      } else {
        sections.push({
          id: taskId,
          kind: 'invoice' as TaskKind,
          status: 'pending',
          title: taskId,
          messages: messageList,
          startedAt,
          startedAtTs
        })
      }
    }
  })

  approvalTasks.value.forEach((task, index) => {
    const taskId = `approval:${task.id}`
    const messageList = groupedMessages.value[taskId] ?? []
    const latestMsgTs = latestMessageTimestamp(messageList)
    const startedAt = toTimestamp(latestMsgTs) > toTimestamp(task.createdAt)
      ? (latestMsgTs || task.createdAt)
      : task.createdAt
    const startedAtTs = Math.max(toTimestamp(latestMsgTs), toTimestamp(task.createdAt))
    const section: TaskSectionItem = {
      id: taskId,
      kind: 'approval' as TaskKind,
      status: task.status || 'pending',
      label: `#${index + 1}`,
      title: task.title,
      summary: task.summary,
      approvalTask: task,
      messages: messageList,
      startedAt,
      startedAtTs
    }
    sections.push(section)
    approvalSectionMap.set(section.id, section)
  })

  approvalCompleted.value.forEach((task, index) => {
    const taskId = `approval:${task.id}`
    const messageList = groupedMessages.value[taskId] ?? []
    const latestMsgTs = latestMessageTimestamp(messageList)
    const startedAt = toTimestamp(latestMsgTs) > toTimestamp(task.createdAt)
      ? (latestMsgTs || task.createdAt)
      : task.createdAt
    const startedAtTs = Math.max(toTimestamp(latestMsgTs), toTimestamp(task.createdAt))
    const section: TaskSectionItem = {
      id: taskId,
      kind: 'approval' as TaskKind,
      status: task.status || 'completed',
      label: `#${index + 1}`,
      title: task.title,
      summary: task.summary,
      approvalTask: task,
      messages: messageList,
      startedAt,
      startedAtTs
    }
    sections.push(section)
    approvalSectionMap.set(section.id, section)
  })

  sections.sort((a, b) => compareByTimestampAsc(a.startedAtTs, b.startedAtTs))

  return sections
})

/** 只加载当前任务前后各 10 个（虚拟窗口），避免大量任务导致页面卡顿 */
const TASK_WINDOW_RADIUS = 10
const visibleTaskSections = computed<TaskSectionItem[]>(() => {
  const all = taskSections.value
  if (all.length <= TASK_WINDOW_RADIUS * 2 + 1) return all // 21个以内全部显示
  const activeIdx = activeTaskId.value
    ? all.findIndex(s => s.id === activeTaskId.value)
    : -1
  // 没有选中任务时，显示最后 21 个
  const center = activeIdx >= 0 ? activeIdx : all.length - 1
  const start = Math.max(0, center - TASK_WINDOW_RADIUS)
  const end = Math.min(all.length, center + TASK_WINDOW_RADIUS + 1)
  return all.slice(start, end)
})

watch(clarificationMap, map => {
  if (activeClarificationId.value) {
    const entry = map.get(activeClarificationId.value)
    if (!entry || entry.answeredAt) {
      activeClarificationId.value = ''
    }
  }
  Object.keys(pendingClarificationAnswers).forEach(questionId => {
    const entry = map.get(questionId)
    if (!entry) {
      delete pendingClarificationAnswers[questionId]
      return
    }
    if (entry.answeredAt || (Array.isArray(entry.answers) && entry.answers.length)) {
      delete pendingClarificationAnswers[questionId]
    }
  })
})
const sending = ref(false)
const retryingTaskId = ref('')
const chatBoxRef = ref<HTMLElement | null>(null)
const localize = (ja: string, zh: string, en?: string) => {
  if (lang.value === 'zh') return zh
  if (lang.value === 'en' && en) return en
  return ja
}
const timelineTitleFallback = computed(() => localize('AI会話', 'AI 对话', 'AI Conversation'))
const clarifyAnsweredLabel = computed(() => localize('回答済み', '已回答', 'Answered'))
const clarifyReplyLabel = computed(() => localize('回答', '回答', 'Reply'))
const clarifyAnswerLabel = computed(() => localize('回答内容', '回答内容', 'Response'))
const fallbackFileLabel = computed(() => localize('添付ファイル', '附件', 'Attachment'))
const fallbackImageLabel = computed(() => localize('画像', '图片', 'Image'))
const imagePreviewTitle = computed(() => localize('画像プレビュー', '图片预览', 'Image Preview'))
const clarifyAnsweringPrefix = computed(() => localize('質問に回答中：', '正在回答问题：', 'Answering: '))
const clarifyAnsweringFilePrefix = computed(() => localize('（ファイル：', '（文件：', ' (File: '))
const clarifyAnsweringFileSuffix = computed(() => (lang.value === 'en' ? ')' : '）'))
function clarificationBannerFile(entry: ClarificationEntry | null): string {
  if (!entry) return ''
  const name = entry.documentName || entry.documentId
  if (!name) return ''
  return `${clarifyAnsweringFilePrefix.value}${name}${clarifyAnsweringFileSuffix.value}`
}
const SINGLE_SESSION_STORAGE_KEY = 'chatkit:single-session-id'
const showSessionNav = false
const defaultSessionTitle = computed(() => text.value?.chat?.aiTitle || text.value?.nav?.chat || 'AI Chat')
const taskPanelTitle = computed(() => {
  const chatSection = text.value?.chat as Record<string, any> | undefined
  const rawTitle = chatSection?.taskListTitle
  return typeof rawTitle === 'string' && rawTitle ? rawTitle : localize('伝票タスク', '票据任务', 'Voucher Tasks')
})
const defaultTaskStatusLabels: Record<string, string> = {
  pending: '未処理',
  in_progress: '処理中',
  completed: '完了',
  failed: '失敗',
  error: 'エラー',
  cancelled: 'キャンセル',
  approved: '承認済み',
  rejected: '却下'
}
const taskStatusLabels = computed<Record<string, string>>(() => {
  const overrides = text.value?.chat?.taskStatus
  const map = { ...defaultTaskStatusLabels }
  if (overrides && typeof overrides === 'object') {
    Object.entries(overrides).forEach(([key, value]) => {
      if (typeof value === 'string' && value.trim()) {
        const normalizedKey = key.trim().toLowerCase().replace(/-/g, '_')
        map[normalizedKey] = value
      }
    })
  }
  return map
})
const langOptions = [
  { value: 'ja', label: '日本語' },
  { value: 'zh', label: '简体中文' },
  { value: 'en', label: 'English' }
]
const langValue = computed({
  get: () => lang.value,
  set: (val) => setLang(val as any)
})

const profile = reactive({
  name: sessionStorage.getItem('currentUserName') || '',
  company: sessionStorage.getItem('currentCompany') || '',
  caps: (sessionStorage.getItem('userCaps') || '').split(',').filter(Boolean)
})

const companyDisplayName = ref('')
async function loadCompanyDisplayName() {
  try {
    const resp = await api.post('/objects/company_setting/search', {
      page: 1, pageSize: 1, where: [], orderBy: [{ field: 'created_at', dir: 'DESC' }]
    })
    const rows = Array.isArray(resp.data?.data) ? resp.data.data : []
    const name = rows[0]?.payload?.companyName
    companyDisplayName.value = typeof name === 'string' ? name.trim() : ''
  } catch { companyDisplayName.value = '' }
}

// 权限检查方法
function hasCap(cap: string): boolean {
  if (!profile.caps || profile.caps.length === 0) return true // 没有caps信息时不限制
  return profile.caps.includes(cap) || profile.caps.includes('roles:manage')
}

// 菜单项结构（从 /api/permissions/accessible-menus-full 返回）
interface MenuItemFromDb {
  key: string
  name: { ja?: string; en?: string; zh?: string } | string
  path: string | null
  order: number
}
interface MenuGroupFromDb {
  moduleCode: string
  items: MenuItemFromDb[]
}

// 可访问的菜单列表（用于菜单过滤）
const accessibleMenus = ref<string[]>([])
const accessibleMenuGroups = ref<MenuGroupFromDb[]>([])
const menusLoaded = ref(false)

async function loadAccessibleMenus() {
  try {
    // 调用新的 API 获取完整菜单结构
    const res = await api.get('/api/permissions/accessible-menus-full')
    accessibleMenuGroups.value = res.data || []
    // 同时维护 accessibleMenus 列表（用于兼容）
    accessibleMenus.value = accessibleMenuGroups.value.flatMap(g => g.items.map(i => i.key))
    menusLoaded.value = true
  } catch (e) {
    // 获取失败时不限制菜单显示（向后兼容）
    console.warn('Failed to load accessible menus:', e)
    accessibleMenus.value = []
    accessibleMenuGroups.value = []
    menusLoaded.value = false
  }
}

// 获取菜单项的显示名称（支持多语言）
function getMenuItemLabel(item: MenuItemFromDb): string {
  if (typeof item.name === 'string') return item.name
  const lang = langValue.value || 'ja'
  if (lang === 'ja' && item.name.ja) return item.name.ja
  if (lang === 'en' && item.name.en) return item.name.en
  if (lang === 'zh' && item.name.zh) return item.name.zh
  // 回退到任意可用的语言
  return item.name.ja || item.name.en || item.name.zh || item.key
}

// 获取模块分组的显示名称
function getModuleGroupLabel(moduleCode: string): string {
  const navTexts = (text.value as any)?.nav || {}
  // 尝试常见的映射
  const mapping: Record<string, string> = {
    'finance': navTexts.groupFinance || '財務会計',
    'hr': navTexts.groupHR || '人事管理',
    'inventory': navTexts.groupInventory || '在庫購買',
    'crm': navTexts.groupCRM || 'CRM',
    'sales': navTexts.groupOrders || '受注管理',
    'system': navTexts.groupSystem || 'システム設定',
    'ai': 'AI',
    'staffing': navTexts.staffing || '人材派遣',
    'portal': navTexts.portal || 'ポータル',
    'fixed_assets': navTexts.groupFixedAssets || '固定資産',
  }
  return mapping[moduleCode] || moduleCode
}

// 检查菜单是否可访问
function isMenuAccessible(menuKey: string): boolean {
  // 如果权限还没有加载成功，不限制（向后兼容）
  if (!menusLoaded.value) return true
  // 如果菜单列表为空，表示用户没有任何权限
  if (accessibleMenus.value.length === 0) return false
  return accessibleMenus.value.includes(menuKey)
}

// ============ 动态菜单（基于后端 /edition API）============
// 过滤出顶级菜单节点（有 path 的子节点或本身有 path）
const dynamicMenuSections = computed<MenuTreeNode[]>(() => {
  const tree = editionMenuTree.value || []
  // 只返回有子节点或自身有 path 的顶级节点
  return tree.filter(node => node.children.length > 0 || (node.path && node.path !== ''))
})

// 获取菜单图标组件名
function getMenuIcon(iconName: string | undefined): string {
  return iconName || 'Menu'
}

// 获取菜单标签（支持国际化 key）
function getMenuLabel(menu: MenuTreeNode): string {
  const label = menu.label || ''
  // 如果是 menu.xxx 格式的 key，尝试从 i18n 获取
  if (label.startsWith('menu.')) {
    const key = label.replace('menu.', '')
    const navTexts = (text.value as any)?.nav || {}
    // 尝试多种可能的 key 格式
    const translated = navTexts[key] || navTexts[toCamelCase(key)] || navTexts[toSnakeCase(key)]
    if (translated) return translated
  }
  return label
}

// 辅助函数：转换为 camelCase
function toCamelCase(str: string): string {
  return str.replace(/_([a-z])/g, (_, c) => c.toUpperCase())
}

// 辅助函数：转换为 snake_case
function toSnakeCase(str: string): string {
  return str.replace(/([A-Z])/g, '_$1').toLowerCase()
}

// 路径到 embed key 的映射（用于动态菜单）
const pathToEmbedKeyMap: Record<string, string> = {
  // Staffing 模块
  '/staffing/resources': 'staffing.resources',
  '/staffing/projects': 'staffing.projects',
  '/staffing/contracts': 'staffing.contracts',
  '/staffing/juchuu': 'staffing.juchuu',
  '/staffing/hatchuu': 'staffing.hatchuu',
  '/staffing/timesheets': 'staffing.timesheets',
  '/staffing/invoices': 'staffing.invoices',
  '/staffing/analytics': 'staffing.analytics',
  '/staffing/email/inbox': 'staffing.email.inbox',
  '/staffing/email/templates': 'staffing.email.templates',
  '/staffing/email/rules': 'staffing.email.rules',
  '/staffing/ai/matching': 'staffing.ai.matching',
  '/staffing/ai/market': 'staffing.ai.market',
  '/staffing/ai/alerts': 'staffing.ai.alerts',
  // 财务模块
  '/voucher/new': 'voucher.new',
  '/vouchers': 'vouchers.list',
  '/accounts': 'accounts.list',
  '/account-ledger': 'account.ledger',
  '/account-balance': 'account.balance',
  '/trial-balance': 'trial.balance',
  '/ledger-export': 'ledger.export',
  '/operations/bank-payment': 'op.bankPayment',
  '/fb-payment': 'op.fbPayment',
  '/financial/statements': 'fin.reports',
  '/financial/nodes': 'fin.designer',
  '/financial/consumption-tax': 'fin.consumptionTax',
  '/financial/monthly-closing': 'fin.monthlyClosing',
  '/cash/ledger': 'cash.ledger',
  '/moneytree/transactions': 'moneytree.transactions',
  // 人事模块
  '/hr/departments': 'hr.dept',
  '/hr/employees': 'hr.emps',
  '/payroll-execute': 'payroll.execute',
  '/payroll-history': 'payroll.history',
  '/hr/resident-tax': 'hr.resident_tax',
  '/timesheets': 'timesheets.list',
  // 系统模块
  '/company/settings': 'company.settings',
  '/system/users': 'system.users',
  '/system/roles': 'system.roles',
  '/ai/agent-scenarios': 'ai.agentScenarios',
  '/ai/agent-skills': 'ai.agentSkills',
  '/workflow/rules': 'ai.workflowRules',
  '/notifications/runs': 'notif.ruleRuns',
  '/notifications/logs': 'notif.logs',
  // 固定资产
  '/fixed-assets/classes': 'fa.classes',
  '/fixed-assets/list': 'fa.list',
  '/fixed-assets/depreciation': 'fa.depreciation',
  // 库存
  '/materials': 'inv.materials',
  '/warehouses': 'inv.warehouses',
  '/bins': 'inv.bins',
  '/inventory/movement': 'inv.movement',
  '/inventory/balances': 'inv.balances',
  '/inventory/ledger': 'inv.ledger',
  '/inventory-counts': 'inv.counts',
  '/purchase-orders': 'inv.po.list',
  '/vendor-invoices': 'inv.vi.list',
  // CRM
  '/crm/contacts': 'crm.contacts',
  '/crm/deals': 'crm.deals',
  '/crm/quotes': 'crm.quotes',
  '/crm/sales-orders': 'crm.salesOrders',
  '/crm/activities': 'crm.activities',
  '/businesspartners': 'bp.list',
  // 销售
  '/sales-analytics': 'crm.salesAnalytics',
  '/sales-alerts': 'crm.salesAlerts',
  '/sales-invoices': 'crm.salesInvoices',
  '/delivery-notes': 'crm.deliveryNotes',
}

// 检查菜单项是否可访问（基于权限）
function isMenuItemAccessible(menu: MenuTreeNode): boolean {
  // 如果权限还没加载，显示所有菜单（向后兼容）
  if (!menusLoaded.value) return true
  // 如果菜单没有设置权限要求，则可访问
  if (!menu.permission) return true
  // 检查用户是否有该权限
  return accessibleMenus.value.includes(menu.id) || accessibleMenus.value.includes(menu.permission!)
}

// 检查模块是否有任何可访问的菜单项
function hasSectionAccessibleMenus(section: MenuTreeNode): boolean {
  if (!menusLoaded.value) return true
  // 递归检查是否有任何可访问的子菜单
  return section.children.some(child => {
    if (child.children && child.children.length > 0) {
      return child.children.some(sub => isMenuItemAccessible(sub))
    }
    return child.path && isMenuItemAccessible(child)
  })
}

// 检查子菜单组是否有可访问的项
function hasSubMenuAccessibleItems(submenu: MenuTreeNode): boolean {
  if (!menusLoaded.value) return true
  return submenu.children.some(child => isMenuItemAccessible(child))
}

// 获取可访问的子菜单项
function getAccessibleChildren(children: MenuTreeNode[]): MenuTreeNode[] {
  if (!menusLoaded.value) return children
  return children.filter(child => isMenuItemAccessible(child))
}

// 处理动态菜单点击
function onDynamicMenuSelect(menu: MenuTreeNode) {
  if (!menu || !menu.path) return
  console.debug('[ChatKit] dynamic menu select', menu.id, menu.path)
  
  // 检查是否有对应的 embed key，使用弹窗方式打开
  const embedKey = pathToEmbedKeyMap[menu.path]
  if (embedKey && embedMap[embedKey]) {
    openInModal(embedKey, getTitle(embedKey))
    return
  }
  
  // 没有注册的 embed 页面，使用路由跳转（兜底）
  router.push(menu.path)
}

// 处理基于数据库的菜单点击
function onDbMenuSelect(item: MenuItemFromDb) {
  if (!item) return
  console.debug('[ChatKit] db menu select', item.key, item.path)
  
  // 优先使用 menu_key 作为 embed key
  if (embedMap[item.key]) {
    openInModal(item.key, getTitle(item.key))
    return
  }
  
  // 如果有 path，检查是否有对应的 embed key
  if (item.path) {
    const embedKey = pathToEmbedKeyMap[item.path]
    if (embedKey && embedMap[embedKey]) {
      openInModal(embedKey, getTitle(embedKey))
      return
    }
    // 使用路由跳转
    router.push(item.path)
  }
}

// 判断菜单是否应该显示（基于权限）- 保留用于兼容
function shouldShowMenu(menu: MenuTreeNode): boolean {
  return isMenuItemAccessible(menu)
}

const profileInitials = computed(() => {
  const parts = profile.name.split(/\s+/).filter(Boolean)
  if (parts.length === 0) return 'AU'
  return parts.map(p => p.charAt(0).toUpperCase()).join('').slice(0, 2)
})

async function syncProfileFromStorage(){
  // sessionStorage → localStorage の順で読む（リフレッシュ後は localStorage が残る）
  const storedName = sessionStorage.getItem('currentUserName') || localStorage.getItem('currentUserName')
  if (storedName) profile.name = storedName
  const storedCompany = sessionStorage.getItem('currentCompany') || localStorage.getItem('currentCompany')
  if (storedCompany) profile.company = storedCompany
  const storedCaps = sessionStorage.getItem('userCaps') || localStorage.getItem('userCaps')
  if (storedCaps) profile.caps = storedCaps.split(',').filter(Boolean)
  // それでも空なら /auth/me で確実に補完
  if (!profile.name || !profile.company) {
    try {
      const me = await api.get('/auth/me')
      if (me.data?.name) {
        profile.name = me.data.name
        localStorage.setItem('currentUserName', me.data.name)
        sessionStorage.setItem('currentUserName', me.data.name)
      }
      if (me.data?.companyCode) {
        profile.company = me.data.companyCode
        localStorage.setItem('currentCompany', me.data.companyCode)
        sessionStorage.setItem('currentCompany', me.data.companyCode)
      }
      if (me.data?.caps) {
        localStorage.setItem('userCaps', me.data.caps)
        sessionStorage.setItem('userCaps', me.data.caps)
      }
    } catch { /* 未ログイン状態などは無視 */ }
  }
}

function handleProfileStorage(event: StorageEvent){
  if (event.storageArea !== sessionStorage) return
  if (event.key === 'currentUserName' || event.key === 'currentCompany') {
    syncProfileFromStorage()
  }
}

function getTitle(key:string){
  const navKey = titleKeyMap[key]
  if (navKey && (text.value.nav as any)[navKey]) return (text.value.nav as any)[navKey]
  return text.value.common.view
}

const embedMap:Record<string, any> = {
  'voucher.new': VoucherForm,
  'vouchers.list': VouchersList,
  'accounts.list': AccountsList,
  'account.ledger': AccountLedger,
  'account.balance': AccountBalance,
  'trial.balance': TrialBalance,
  'ledger.export': LedgerExport,
  'account.new': AccountForm,
  'op.bankPayment': BankPayment,
  'op.fbPayment': FbPayment,
  'fin.reports': FinancialStatementsReport,
  'fin.designer': FinancialStatementDesigner,
  'fin.consumptionTax': ConsumptionTaxReturn,
  'fin.monthlyClosing': MonthlyClosing,
  'cash.ledger': CashLedger,
  'schema.editor': SchemaEditor,
  'bp.list': BusinessPartnersList,
  'bp.new': BusinessPartnerForm,
  'hr.dept': OrganizationChart,
  'hr.emps': EmployeesList,
  'hr.emp.new': EmployeeForm
  ,'hr.policy.editor': PolicyEditor
  ,'payroll.execute': PayrollExecute
  ,'payroll.history': defineAsyncComponent(() => import('./PayrollHistory.vue'))
  ,'hr.resident_tax': defineAsyncComponent(() => import('./ResidentTaxList.vue'))
  ,'timesheets.list': defineAsyncComponent(() => import('./TimesheetsList.vue'))
  ,'timesheet.new': defineAsyncComponent(() => import('./TimesheetForm.vue'))
  ,'cert.request': CertificateRequestForm
  ,'cert.list': defineAsyncComponent(() => import('./CertificateRequestsList.vue'))
  ,'approvals.designer': defineAsyncComponent(() => import('./ApprovalsDesigner.vue'))
  ,'scheduler.tasks': SchedulerTasks
  ,'acct.periods': defineAsyncComponent(() => import('./AccountingPeriods.vue'))
  ,'moneytree.transactions': defineAsyncComponent(() => import('./MoneytreeTransactions.vue'))
  ,'rcpt.planner': ReceiptPlanner
  // Inventory embeds（直接用路由页面组件复用）
  ,'inv.materials': MaterialsList
  ,'inv.material.new': MaterialForm
  ,'inv.warehouses': WarehousesList
  ,'inv.warehouse.new': WarehouseForm
  ,'inv.bins': BinsList
  ,'inv.bin.new': BinForm
  ,'inv.stockstatus': StockStatuses
  ,'inv.batches': BatchesList
  ,'inv.batch.new': BatchForm
  ,'inv.movement': InventoryMovement
  ,'inv.balances': InventoryBalances
  ,'inv.ledger': defineAsyncComponent(() => import('./InventoryLedgerList.vue'))
  ,'inv.counts': defineAsyncComponent(() => import('./InventoryCountsList.vue'))
  ,'inv.count.report': defineAsyncComponent(() => import('./InventoryCountReport.vue'))
  ,'inv.po.list': defineAsyncComponent(() => import('./PurchaseOrdersList.vue'))
  ,'inv.po.new': defineAsyncComponent(() => import('./PurchaseOrderForm.vue'))
  ,'inv.vi.list': defineAsyncComponent(() => import('./VendorInvoicesList.vue'))
  ,'inv.vi.new': defineAsyncComponent(() => import('./VendorInvoiceForm.vue'))
  // CRM embeds（直接用路由页面组件复用）
  ,'crm.contacts': defineAsyncComponent(() => import('./ContactsList.vue'))
  ,'crm.contact.new': defineAsyncComponent(() => import('./ContactForm.vue'))
  ,'crm.deals': defineAsyncComponent(() => import('./DealsList.vue'))
  ,'crm.deal.new': defineAsyncComponent(() => import('./DealForm.vue'))
  ,'crm.quotes': defineAsyncComponent(() => import('./QuotesList.vue'))
  ,'crm.quote.new': defineAsyncComponent(() => import('./QuoteForm.vue'))
  ,'crm.salesOrders': defineAsyncComponent(() => import('./SalesOrdersList.vue'))
  ,'crm.deliveryNotes': defineAsyncComponent(() => import('./DeliveryNotesList.vue'))
  ,'crm.salesInvoices': defineAsyncComponent(() => import('./SalesInvoicesList.vue'))
  ,'crm.salesInvoiceCreate': defineAsyncComponent(() => import('./SalesInvoiceCreate.vue'))
  ,'crm.salesAnalytics': defineAsyncComponent(() => import('./SalesAnalytics.vue'))
  ,'crm.salesAlerts': defineAsyncComponent(() => import('./SalesAlertTasks.vue'))
  ,'crm.orderEntry': defineAsyncComponent(() => import('./SalesOrderForm.vue'))
  ,'crm.salesOrder.new': defineAsyncComponent(() => import('./SalesOrderForm.vue'))
  ,'crm.activities': defineAsyncComponent(() => import('./ActivitiesList.vue'))
  ,'crm.activity.new': defineAsyncComponent(() => import('./ActivityForm.vue'))
  ,'company.settings': defineAsyncComponent(() => import('./CompanySettings.vue'))
  ,'notif.ruleRuns': defineAsyncComponent(() => import('./NotificationRuleRuns.vue'))
  ,'notif.logs': defineAsyncComponent(() => import('./NotificationLogs.vue'))
  ,'ai.workflowRules': defineAsyncComponent(() => import('./WorkflowRules.vue'))
  ,'ai.agentScenarios': defineAsyncComponent(() => import('./AgentScenarios.vue'))
  ,'ai.agentSkills': defineAsyncComponent(() => import('./AgentSkills.vue'))
  // Fixed Assets embeds
  ,'fa.classes': defineAsyncComponent(() => import('./AssetClassesList.vue'))
  ,'fa.list': defineAsyncComponent(() => import('./FixedAssetsList.vue'))
  ,'fa.depreciation': defineAsyncComponent(() => import('./DepreciationRuns.vue'))
  // User & Role Management
  ,'system.users': defineAsyncComponent(() => import('./UsersList.vue'))
  ,'system.roles': defineAsyncComponent(() => import('./RolesList.vue'))
  // Staffing embeds (人才派遣)
  ,'staffing.resources': defineAsyncComponent(() => import('./staffing/ResourcePoolList.vue'))
  ,'staffing.projects': defineAsyncComponent(() => import('./staffing/ProjectsList.vue'))
  ,'staffing.contracts': defineAsyncComponent(() => import('./staffing/ContractsList.vue'))
  ,'staffing.juchuu': defineAsyncComponent(() => import('./staffing/JuchuuList.vue'))
  ,'staffing.hatchuu': defineAsyncComponent(() => import('./staffing/HatchuuList.vue'))
  ,'staffing.timesheets': defineAsyncComponent(() => import('./staffing/TimesheetSummaryList.vue'))
  ,'staffing.invoices': defineAsyncComponent(() => import('./staffing/InvoicesList.vue'))
  ,'staffing.analytics': defineAsyncComponent(() => import('./staffing/AnalyticsDashboard.vue'))
  ,'staffing.email.inbox': defineAsyncComponent(() => import('./staffing/EmailInbox.vue'))
  ,'staffing.email.templates': defineAsyncComponent(() => import('./staffing/EmailTemplates.vue'))
  ,'staffing.email.rules': defineAsyncComponent(() => import('./staffing/EmailRules.vue'))
  ,'staffing.ai.matching': defineAsyncComponent(() => import('./staffing/AiMatching.vue'))
  ,'staffing.ai.market': defineAsyncComponent(() => import('./staffing/AiMarketAnalysis.vue'))
  ,'staffing.ai.alerts': defineAsyncComponent(() => import('./staffing/AiAlerts.vue'))
}
const titleKeyMap: Record<string, string> = {
  'voucher.new': 'voucherNew',
  'vouchers.list': 'vouchers',
  'accounts.list': 'accounts',
  'account.ledger': 'accountLedger',
  'account.balance': 'accountBalance',
  'trial.balance': 'trialBalance',
  'ledger.export': 'ledgerExport',
  'account.new': 'accountNew',
  'op.bankPayment': 'bankPayment',
  'op.fbPayment': 'fbPayment',
  'fin.reports': 'financialReports',
  'fin.designer': 'financialDesigner',
  'fin.consumptionTax': 'consumptionTax',
  'fin.monthlyClosing': 'monthlyClosing',
  'cash.ledger': 'cashLedger',
  'schema.editor': 'schemaEditor',
  'bp.list': 'partners',
  'bp.new': 'partnerNew',
  'hr.dept': 'hrDept',
  'hr.emps': 'hrEmps',
  'hr.emp.new': 'hrEmpNew',
  'hr.policy.editor': 'policyEditor',
  'payroll.execute': 'payrollExecute',
  'payroll.history': 'payrollHistory',
  'hr.resident_tax': 'residentTax',
  'timesheets.list': 'timesheets',
  'timesheet.new': 'timesheetNew',
  'cert.request': 'certRequest',
  'cert.list': 'certList',
  'approvals.designer': 'approvalsDesigner',
  'scheduler.tasks': 'schedulerTasks',
  'acct.periods': 'accountingPeriods',
  'inv.materials': 'inventoryMaterials',
  'inv.material.new': 'inventoryMaterialNew',
  'inv.warehouses': 'inventoryWarehouses',
  'inv.warehouse.new': 'inventoryWarehouseNew',
  'inv.bins': 'inventoryBins',
  'inv.bin.new': 'inventoryBinNew',
  'inv.stockstatus': 'inventoryStatuses',
  'inv.batches': 'inventoryBatches',
  'inv.batch.new': 'inventoryBatchNew',
  'inv.movement': 'inventoryMovement',
  'inv.balances': 'inventoryBalances',
  'inv.ledger': 'inventoryLedger',
  'inv.counts': 'inventoryCounts',
  'inv.count.report': 'inventoryCountReport',
  'inv.po.list': 'purchaseOrders',
  'inv.po.new': 'purchaseOrderNew',
  'inv.vi.list': 'vendorInvoices',
  'inv.vi.new': 'vendorInvoiceNew',
  'crm.contacts': 'crmContacts',
  'crm.contact.new': 'crmContactNew',
  'crm.deals': 'crmDeals',
  'crm.deal.new': 'crmDealNew',
  'crm.quotes': 'crmQuotes',
  'crm.quote.new': 'crmQuoteNew',
  'crm.salesOrders': 'crmSalesOrders',
  'crm.orderEntry': 'crmOrderEntry',
  'crm.salesOrder.new': 'crmSalesOrderNew',
  'crm.deliveryNotes': 'crmDeliveryNotes',
  'crm.salesInvoices': 'crmSalesInvoices',
  'crm.salesInvoiceCreate': 'crmSalesInvoiceCreate',
  'crm.salesAnalytics': 'crmSalesAnalytics',
  'crm.salesAlerts': 'crmSalesAlerts',
  'crm.activities': 'crmActivities',
  'crm.activity.new': 'crmActivityNew',
  'company.settings': 'companySettings',
  'notif.ruleRuns': 'notifRuleRuns',
  'notif.logs': 'notifLogs',
  'ai.workflowRules': 'workflowRules',
  'ai.agentScenarios': 'agentScenarios',
  // Fixed Assets
  'fa.classes': 'faClasses',
  'fa.list': 'faList',
  'fa.depreciation': 'faDepreciation',
  // User & Role Management
  'system.users': 'userManagement',
  'system.roles': 'roleManagement',
  // Staffing (人才派遣)
  'staffing.resources': 'resourcePool',
  'staffing.projects': 'staffingProjects',
  'staffing.contracts': 'staffingContracts',
  'staffing.timesheets': 'staffingTimesheet',
  'staffing.invoices': 'staffingInvoices',
  'staffing.analytics': 'staffingAnalytics',
  'staffing.email.inbox': 'staffingEmailInbox',
  'staffing.email.templates': 'staffingEmailTemplates',
  'staffing.email.rules': 'staffingEmailRules',
  'staffing.ai.matching': 'staffingAiMatching',
  'staffing.ai.market': 'staffingAiMarket',
  'staffing.ai.alerts': 'staffingAiAlerts'
}
const Dummy = defineComponent({ name:'EmbedPlaceholder', setup(){ return () => null } })

function resolveComp(key:string){
  return (embedMap as any)[key] || Dummy
}
const modal = reactive<{ key:string, title:string, renderKey:number }>({ key:'', title:'', renderKey:0 })
const modalOpen = ref(false)
const modalRef = ref<any>(null)
const pendingModalPayload = ref<any>(null)
const pendingModalPayloadAttempts = ref(0)

type OperationMessage = { content: string; status?: 'success' | 'error' | 'info'; tag?: any }
type OperationFormatter = (payload: any) => OperationMessage | null

function tagType(status?: string){
  if (status === 'success') return 'success'
  if (status === 'error') return 'danger'
  return 'info'
}

function onMessageTagClick(tag: any){
  try{
    if (!tag) return
    const key = tag.key || tag.embedKey
    if (tag.action === 'openEmbed' || key){
      const targetKey = key || tag.key
      if (!targetKey) return
      if (targetKey === 'crm.salesOrders' && tryOpenSalesOrderFromTag(tag)){
        return
      }
      const payload = tag.payload || tag.data || null
      if (!embedMap[targetKey]){
        pushEventMessage(localize(`ページが登録されていません：${targetKey}`, `页面未注册：${targetKey}`, `Page not registered: ${targetKey}`), { status: 'error' })
        return
      }
      const title = getTitle(targetKey)
      pushEmbed(targetKey, title, { scroll: false })
      openInModal(targetKey, title, payload)
    }
  }catch{}
}

function tryOpenSalesOrderFromTag(tag: any): boolean {
  const payload = ensurePlainObject<Record<string, any>>(tag?.payload || tag?.data || tag?.targetPayload)
  const nestedSources = [
    payload?.salesOrder,
    payload?.sales_order,
    payload?.order,
    payload?.data
  ].filter(item => item && typeof item === 'object')
  const idCandidates: string[] = []
  const numberCandidates: string[] = []

  const pushCandidate = (list: string[], value: unknown) => {
    if (typeof value !== 'string') return
    const trimmed = value.trim()
    if (!trimmed) return
    if (!list.includes(trimmed)) list.push(trimmed)
  }

  const collectFromSource = (source: Record<string, any> | null | undefined) => {
    if (!source || typeof source !== 'object') return
    pushCandidate(idCandidates, source.salesOrderId ?? source.sales_order_id ?? source.salesOrderID ?? source.orderId ?? source.order_id ?? source.objectId ?? source.object_id ?? source.id)
    pushCandidate(numberCandidates, source.salesOrderNo ?? source.sales_order_no ?? source.soNo ?? source.so_no ?? source.orderNo ?? source.order_no ?? source.number ?? source.code ?? source.identifier)
    if (typeof source.meta === 'object' && source.meta){
      collectFromSource(source.meta as Record<string, any>)
    }
  }

  collectFromSource(payload)
  nestedSources.forEach(src => collectFromSource(src as Record<string, any>))

  const label = typeof tag?.label === 'string' ? tag.label.trim() : ''
  if (label) pushCandidate(numberCandidates, label)

  const idSet = new Set(idCandidates)
  const numberSet = new Set(numberCandidates)

  const matchedTask = taskList.value.find(task => {
    if (!isSalesOrderTask(task)) return false
    if (task.salesOrderId && idSet.has(task.salesOrderId)) return true
    if (task.metadata){
      const meta = ensurePlainObject<Record<string, any>>(task.metadata)
      const metaId = meta.salesOrderId ?? meta.sales_order_id ?? meta.id
      if (typeof metaId === 'string' && idSet.has(metaId.trim())) return true
    }
    const taskNoCandidates = [
      task.salesOrderNo,
      salesOrderMeta(task).orderNo
    ].map(value => (typeof value === 'string' ? value.trim() : '')).filter(Boolean)
    return taskNoCandidates.some(no => numberSet.has(no))
  })

  if (matchedTask){
    openSalesOrderDetail(matchedTask)
    return true
  }

  const fallbackNo = numberCandidates[0] || ''
  const fallbackId = idCandidates[0] || ''
  if (!fallbackNo && !fallbackId) return false

  const resolveString = (...values: unknown[]): string | undefined => {
    for (const value of values){
      if (typeof value === 'string' && value.trim()) return value.trim()
    }
    return undefined
  }

  const nowIso = new Date().toISOString()
  const placeholder: SalesOrderAgentTask = {
    kind: 'sales_order',
    id: `tag:${fallbackId || fallbackNo || nowIso}`,
    sessionId: activeSessionId.value || '',
    status: resolveString(payload.status, payload.currentStatus) || 'pending',
    summary: resolveString(payload.summary, payload.message),
    salesOrderId: fallbackId || undefined,
    salesOrderNo: fallbackNo || undefined,
    customerCode: resolveString(
      payload.customerCode,
      payload.customer_code,
      payload.partnerCode,
      payload.partner_code,
      payload.customer?.code,
      payload.partner?.code
    ),
    customerName: resolveString(
      payload.customerName,
      payload.customer_name,
      payload.partnerName,
      payload.partner_name,
      payload.customer?.name,
      payload.partner?.name
    ),
    metadata: payload,
    payload,
    createdAt: nowIso,
    updatedAt: nowIso
  }

  openSalesOrderDetail(placeholder)
  return true
}

function pushEventMessage(content: string, options: { status?: 'success' | 'error' | 'info'; tag?: any; persistPayload?: any; taskId?: string } = {}){
  if (!content) return
  const msg: any = {
    role: 'assistant',
    kind: 'event',
    content,
    status: options.status || 'info'
  }
  if (options.tag) msg.tag = options.tag
  if (options.taskId) msg.taskId = options.taskId
  messages.push(msg)
  nextTick().then(scrollToBottom)
  return msg
}

function mapActionVerb(action: string): string {
  const normalized = (action || '').toLowerCase()
  if (normalized === 'created' || normalized === 'create') return localize('作成済み', '已创建', 'Created')
  if (normalized === 'updated' || normalized === 'update') return localize('更新済み', '已更新', 'Updated')
  if (normalized === 'deleted' || normalized === 'delete') return localize('削除済み', '已删除', 'Deleted')
  return action
}

const operationMessageMap: Record<string, OperationFormatter> = {
  'voucher.created': (payload) => {
    const voucherNo = payload?.voucherNo || payload?.voucher_no
    const message =
      payload?.message ||
      (voucherNo
        ? localize(`会計伝票 ${voucherNo} を作成しました`, `已创建会计凭证 ${voucherNo}`, `Voucher ${voucherNo} created`)
        : '')
    const tag = voucherNo
      ? { label: voucherNo, action: 'openEmbed', key: 'vouchers.list', payload: { voucherNo, detailOnly: true } }
      : undefined
    return message ? { content: message, status: 'success', tag } : null
  },
  'voucher.failed': (payload) => {
    const message = payload?.message || payload?.error || localize('会計伝票の作成に失敗しました', '创建会计凭证失败', 'Failed to create voucher')
    return { content: message, status: 'error' }
  }
}

function interpretModalResult(result: any): OperationMessage | null {
  if (!result) return null
  const explicitStatus: OperationMessage['status'] = result.status || (result.error ? 'error' : undefined)
  const shouldShow = result.showInChat === true || (!result.showInChat && explicitStatus === 'success')
  if (!shouldShow) return null
  const formatter = result.kind ? operationMessageMap[result.kind] : undefined
  if (formatter){
    const formatted = formatter(result)
    if (formatted){
      return {
        content: formatted.content,
        status: formatted.status || explicitStatus,
        tag: formatted.tag
      }
    }
  }
  if (result.kind && typeof result.kind === 'string' && result.kind.includes('.')){
    const [entityKey, actionKey] = result.kind.split('.')
    const verb = mapActionVerb(actionKey)
    if (verb){
      const entityLabel = result.entityName || result.entityLabel || result.entity || entityKey
      const identifier = result.identifier || result.code || result.number || result.name
      const separator = lang.value === 'en' ? ': ' : '：'
      const contentText = identifier ? `${verb} ${entityLabel}${separator}${identifier}` : `${verb} ${entityLabel}`
      let tag = result.tag
      if (!tag && result.targetKey){
        tag = { label: identifier || entityLabel, action: 'openEmbed', key: result.targetKey, payload: result.targetPayload || null }
      }
      return { content: contentText, status: explicitStatus || 'success', tag }
    }
  }
  let content = result.message || result.error || ''
  if (!content && result.action && result.entityName){
    const verb = mapActionVerb(result.action)
    content = `${verb} ${result.entityName}`
  }
  if (!content && result.entity && result.action){
    content = `${mapActionVerb(result.action)} ${result.entity}`
  }
  if (!content) return null
  let tag = result.tag
  if (!tag){
    const label = result.identifier || result.code || result.number || result.voucherNo || result.reference
    const targetKey = result.targetKey || result.embedKey || result.key
    if (label && targetKey){
      tag = { label, action: 'openEmbed', key: targetKey, payload: result.targetPayload || result.payload || null }
    }
  }
  return { content, status: explicitStatus || 'info', tag }
}

async function applyPendingModalPayload(){
  if (typeof pendingModalPayload.value === 'undefined' || pendingModalPayload.value === null) return
  pendingModalPayloadAttempts.value += 1
  await nextTick()
  for (let i = 0; i < 8; i++){
    const target = modalRef.value
    const handler = target && (typeof target.applyIntent === 'function'
      ? target.applyIntent
      : typeof target.receiveIntent === 'function'
        ? target.receiveIntent
        : typeof target.handleIntent === 'function'
          ? target.handleIntent
          : null)
    if (target && handler){
      try{ handler.call(target, pendingModalPayload.value) }catch{}
      pendingModalPayload.value = null
      pendingModalPayloadAttempts.value = 0
      return
    }
    await new Promise((resolve) => setTimeout(resolve, 120))
  }
  if (pendingModalPayload.value !== null && typeof pendingModalPayload.value !== 'undefined'){
    if (pendingModalPayloadAttempts.value >= 5){
      pendingModalPayload.value = null
      pendingModalPayloadAttempts.value = 0
      return
    }
    setTimeout(() => applyPendingModalPayload(), 240)
  }
}

function setActiveSession(id: string, title?: string){
  const trimmed = (id || '').trim()
  if (!trimmed) return
  const normalizedTitle = title && title.trim() ? title.trim() : defaultSessionTitle.value
  const existing = sessions.find(s => safeSessionId(s) === trimmed)
  if (!existing){
    sessions.unshift({ id: trimmed, title: normalizedTitle })
  } else if (normalizedTitle && existing.title !== normalizedTitle){
    existing.title = normalizedTitle
  }
  if (activeSessionId.value !== trimmed){
    activeSessionId.value = trimmed
  }
  try{
    localStorage.setItem(SINGLE_SESSION_STORAGE_KEY, trimmed)
  }catch{}
}

async function fetchSessionsList(): Promise<{ id: string; title?: string }[]>{
  try{
    const resp = await api.get('/ai/sessions')
    const rawList = Array.isArray(resp.data) ? resp.data : []
    const normalized: { id: string; title?: string }[] = []
    rawList.forEach((item: any) => {
      const sid = safeSessionId(item)
      if (!sid) return
      const title = typeof item?.title === 'string' && item.title.trim()
        ? item.title.trim()
        : defaultSessionTitle.value
      normalized.push({ id: sid, title })
    })
    sessions.splice(0, sessions.length, ...normalized)
    return normalized
  }catch(e){
    console.error('[ChatKit] fetchSessions failed', e)
    return []
  }
}

async function ensureSessionId(): Promise<string | null>{
  if (activeSessionId.value) return activeSessionId.value
  const stored = localStorage.getItem(SINGLE_SESSION_STORAGE_KEY)
  if (stored && stored.trim()){
    setActiveSession(stored)
    return stored.trim()
  }
  if (!sessions.length){
    const fetched = await fetchSessionsList()
    if (fetched.length){
      setActiveSession(fetched[0].id, fetched[0].title)
      return activeSessionId.value
    }
    return null
  }
  const first = sessions[0]
  if (first){
    setActiveSession(safeSessionId(first), first.title)
    return activeSessionId.value
  }
  return null
}

function applySessionFromResponse(sessionId?: string | null){
  if (!sessionId) return
  const id = String(sessionId).trim()
  if (!id) return
  setActiveSession(id)
}

function parseMessagePayload(raw:any){
  if (!raw) return null
  if (typeof raw === 'string'){
    try{ return JSON.parse(raw) }catch{ return null }
  }
  return raw
}

function parseJsonSafe(raw:any){
  if (!raw) return null
  if (typeof raw === 'object') return raw
  if (typeof raw === 'string'){
    try{ return JSON.parse(raw) }catch{ return null }
  }
  return null
}

function onSidebarClick(e: MouseEvent){
  try { console.debug('[ChatKit] sidebar click', (e.target as HTMLElement)?.tagName) } catch {}
}

function recentKey(){ return `recent_links:${activeSessionId.value || 'default'}` }
function loadRecent(){
  try{
    recent.splice(0, recent.length)
    const raw = sessionStorage.getItem(recentKey())
    if (raw){ const arr = JSON.parse(raw); if (Array.isArray(arr)) arr.forEach((x:any)=>recent.push(x)) }
  }catch{}
}
function saveRecent(){
  try{ sessionStorage.setItem(recentKey(), JSON.stringify(recent)) }catch{}
}

function scheduleApprovalReload(){
  if (approvalReloadTimer !== null) return
  approvalReloadTimer = setTimeout(async () => {
    approvalReloadTimer = null
    await loadApprovalTasks()
  }, 1500)
}

onMounted(async () => {
  await syncProfileFromStorage()
  window.addEventListener('storage', handleProfileStorage)
  loadRecent()
  loadAccessibleMenus() // 加载可访问的菜单列表
  loadCompanyDisplayName()
  await loadEditionInfo() // 加载版本和动态菜单
  router.afterEach((to) => {
    const p = to.fullPath
    if (!recent.find(x => x.path===p)){
      recent.unshift({ path: p, name: document.title })
      if (recent.length>10) recent.pop()
      saveRecent()
    }
  })

  const storedSessionId = localStorage.getItem(SINGLE_SESSION_STORAGE_KEY)
  if (storedSessionId){
    setActiveSession(storedSessionId)
  }
  const fetchedSessions = await fetchSessionsList()
  if (!activeSessionId.value && fetchedSessions.length){
    setActiveSession(fetchedSessions[0].id, fetchedSessions[0].title)
  } else if (activeSessionId.value && fetchedSessions.length){
    const matched = fetchedSessions.find(item => item.id === activeSessionId.value)
    if (matched){
      setActiveSession(matched.id, matched.title)
    }
  }

  if (activeSessionId.value){
    await loadMessages()
    await loadTasks()
    loadRecent()
  } else {
    await loadApprovalTasks()
  }

  // === 银行明细 AI 相談：从 MoneytreeTransactions 页面跳转过来时自动发送记账请求 ===
  if (route.query.bankTxId) {
    const txId = route.query.bankTxId as string
    const desc = route.query.bankTxDesc as string || ''
    const amount = route.query.bankTxAmount as string || '0'
    const date = route.query.bankTxDate as string || ''
    const type = route.query.bankTxType as string || 'withdrawal'
    const bank = route.query.bankTxBank as string || ''
    const account = route.query.bankTxAccount as string || ''
    const isWithdrawal = type === 'withdrawal'
    const typeLabel = isWithdrawal ? '出金' : '入金'
    const amountFormatted = Number(amount).toLocaleString()

    // 构建用户可以编辑的预填消息
    let bankMsg = `以下の銀行取引を記帳してください。\n`
    bankMsg += `取引日: ${date}\n`
    bankMsg += `種別: ${typeLabel}\n`
    bankMsg += `金額: ¥${amountFormatted}\n`
    if (desc) bankMsg += `摘要: ${desc}\n`
    if (bank) bankMsg += `金融機関: ${bank}\n`
    if (account) bankMsg += `口座番号: ${account}\n`
    bankMsg += `\n銀行取引ID: ${txId}`

    // 设置输入内容并存储银行交易ID（用于后端关联）
    pendingBankTransactionId.value = txId
    input.value = bankMsg
    await nextTick()
    // 清除 URL 查询参数（避免刷新重复触发）
    router.replace({ path: '/chat', query: {} })
    // 自动聚焦到输入框，让用户可以编辑后发送，或直接发送
    chatInputRef.value?.focus?.()
  }

})

onBeforeUnmount(() => {
  window.removeEventListener('storage', handleProfileStorage)
  window.removeEventListener('chatkit:bankConsult', onBankConsultEvent)
})

watch(activeSessionId, (id) => {
  loadRecent()
  if (id){
    void loadTasks()
  } else {
    activeTaskId.value = ''
    void loadApprovalTasks()
  }
})

interface LoadMessagesOptions {
  before?: string | null
  append?: boolean
}

async function loadMessages(options: LoadMessagesOptions = {}){
  const append = options.append === true
  if (!activeSessionId.value || messagePager.loading) return
  const sessionId = activeSessionId.value
  const params: Record<string, any> = { limit: messagePageSize }
  if (options.before) params.before = options.before

  const box = chatBoxRef.value
  let prevHeight = 0
  let prevScroll = 0
  if (append && box){
    prevHeight = box.scrollHeight
    prevScroll = box.scrollTop
  }

  if (!append){
    messagePager.cursor = null
    messagePager.hasMore = false
    Object.keys(taskAttachments).forEach(key => {
      delete taskAttachments[key]
    })
  }

  messagePager.loading = true
  try{
    const r = await api.get(`/ai/sessions/${encodeURIComponent(sessionId)}/messages`, { params })
    let rows: any[] = []
    let hasMore = false
    let nextCursor: string | null = null

    if (Array.isArray(r.data?.messages)){
      rows = r.data.messages
      hasMore = Boolean(r.data?.hasMore)
      nextCursor = typeof r.data?.nextCursor === 'string' ? r.data.nextCursor : null
    } else if (Array.isArray(r.data)) {
      rows = r.data
    }

    const parsedMessages: any[] = []
    for (const raw of rows){
      const payload = parseMessagePayload((raw as any).payload)
      const createdAt = (raw as any).createdAt || (raw as any).created_at || null
      const msg: any = {
        role: (raw as any).role || 'assistant',
        content: (raw as any).content || '',
        kind: payload?.kind || (raw as any).kind
      }
      if (createdAt) msg.createdAt = createdAt
      const messageTaskId = (raw as any).taskId || (raw as any).task_id || null
      if (typeof messageTaskId === 'string' && messageTaskId.trim()) msg.taskId = messageTaskId.trim()
      if (payload?.status) msg.status = payload.status
      if (payload?.tag) msg.tag = payload.tag
      if (payload) msg.payload = payload
      parsedMessages.push(msg)
    }

    if (!nextCursor && parsedMessages.length > 0){
      const earliest = parsedMessages[0]?.createdAt
      if (typeof earliest === 'string' && earliest) nextCursor = earliest
    }

    const enhancedMessages = expandUploadMessages(parsedMessages)

    if (!append){
      messages.splice(0, messages.length, ...enhancedMessages)
  loadEmbeds()
      messagePager.hasMore = hasMore && !!nextCursor
      messagePager.cursor = nextCursor
      await loadTasks()
      await nextTick()
      scrollToBottom()
    }else{
      if (enhancedMessages.length > 0){
        messages.splice(0, 0, ...enhancedMessages)
        messagePager.hasMore = hasMore && !!nextCursor
        messagePager.cursor = nextCursor
        await loadTasks()
        await nextTick()
        const el = chatBoxRef.value
        if (el){
          const diff = el.scrollHeight - prevHeight
          el.scrollTop = prevScroll + diff
        }
      } else {
        messagePager.hasMore = false
        messagePager.cursor = null
      }
    }
  }catch(e:any){
    if (!append){
      messages.splice(0, messages.length)
      messagePager.hasMore = false
      messagePager.cursor = null
    }
    if (e?.response?.status === 404){
      localStorage.removeItem(SINGLE_SESSION_STORAGE_KEY)
      activeSessionId.value = ''
      try{
        await ensureSessionId()
        if (activeSessionId.value){
          setTimeout(() => {
            loadMessages()
          }, 0)
        }
      }catch{}
    }
  }finally{
    messagePager.loading = false
  }
}

function onChatScroll(e: Event){
  if (!messagePager.hasMore || messagePager.loading) return
  const el = e.target as HTMLElement | null
  if (!el) return
  if (el.scrollTop <= 60){
    if (!messagePager.cursor) return
    loadMessages({ before: messagePager.cursor, append: true })
  }
}
function safeSessionId(s:any){ return String(s?.id || '').trim() }

function onSelectSession(id:string){
  try { console.debug('[ChatKit] select session', id) } catch {}
  if (!id) return
  activeSessionId.value = id
  messagePager.cursor = null
  messagePager.hasMore = false
  loadMessages()
}
function newSession(){
  localStorage.removeItem(SINGLE_SESSION_STORAGE_KEY)
  activeSessionId.value=''
  messagePager.cursor = null
  messagePager.hasMore = false
  messages.splice(0,messages.length)
}

async function send(){
  const rawText = input.value
  const text = rawText.trim()

  if (attachments.length > 0){
    await submitAttachmentTask(text)
    return
  }

  if (!text) return
  const targetTaskId = preferGeneralMode.value ? '' : activeTaskId.value
  if (!targetTaskId){
    preferGeneralMode.value = true
  }
  const currentClarificationId = activeClarificationId.value
  const userMessage: any = { role: 'user', content: text }
  if (targetTaskId){
    userMessage.taskId = targetTaskId
  }
  if (currentClarificationId){
    userMessage.payload = {
      ...(userMessage.payload || {}),
      answerTo: currentClarificationId,
      localOnly: true
    }
    addPendingClarificationAnswer(currentClarificationId, text)
  }
  messages.push(userMessage)
  await nextTick(); scrollToBottom()
  input.value = ''

  sending.value = true
  try{
    const payload: Record<string, any> = { message: text, language: lang.value }
    if (activeSessionId.value) payload.sessionId = activeSessionId.value
    if (currentClarificationId) payload.answerTo = currentClarificationId
    if (targetTaskId) payload.taskId = targetTaskId
    // 银行明细 AI 相談：将银行交易ID传给后端，用于自动关联凭证
    if (pendingBankTransactionId.value) {
      payload.bankTransactionId = pendingBankTransactionId.value
      pendingBankTransactionId.value = ''
    }
    const resp = await api.post('/ai/agent/message', payload)
    applySessionFromResponse(resp.data?.sessionId)
    await loadMessages()
    await loadTasks()
    activeClarificationId.value = ''
  }catch(e:any){
    const errText = e?.response?.data?.error || e?.message || localize('呼び出しに失敗しました', '调用失败', 'Request failed')
    pushEventMessage(errText, { status: 'error' })
    if (currentClarificationId){
      clearPendingClarificationAnswers(currentClarificationId)
    }
  }finally{
    sending.value = false
  }
}

async function submitAttachmentTask(messageText: string){
  if (!attachments.length) return

  sending.value = true
  const queue = attachments.slice()
  try{
    const form = new FormData()
    for (const att of queue){
      att.status = 'uploading'
      att.error = undefined
      form.append('files', att.file, att.name)
    }
    if (messageText) form.append('message', messageText)
    if (activeSessionId.value) form.append('sessionId', activeSessionId.value)
    if (activeClarificationId.value) form.append('answerTo', activeClarificationId.value)
    form.append('language', lang.value)

    pushEventMessage(localize(`ファイル ${queue.length} 件を送信しました。AI が解析中…`, `已提交 ${queue.length} 个文件，AI 正在解析…`, `Submitted ${queue.length} files. AI is processing…`), { status: 'info' })
    const resp = await api.post('/ai/agent/tasks', form)
    applySessionFromResponse(resp.data?.sessionId)

    const respData = resp.data || {}
    const rawTasks = Array.isArray(respData.tasks) ? respData.tasks : []
    const normalizedTasks = hydrateAgentTasks(
      rawTasks,
      activeSessionId.value || (respData.sessionId ?? '')
    ).filter((task): task is InvoiceTask => isInvoiceTask(task))
    if (normalizedTasks.length){
      normalizedTasks.forEach((task, idx) => {
        const att = queue[idx]
        if (att){
          att.status = 'done'
          att.fileId = task.fileId
          att.summary = task.summary || task.analysis
        }
        const summaryText = task.summary
          ? (lang.value === 'en' ? ` (${task.summary})` : `（${task.summary}）`)
          : ''
        pushEventMessage(localize(
          `伝票タスク ${task.label} を作成しました：${task.fileName}${summaryText}`,
          `已创建票据任务 ${task.label}：${task.fileName}${summaryText}`,
          `Created voucher task ${task.label}: ${task.fileName}${summaryText}`
        ), { status: 'info', taskId: task.id })
      })
      mergeTasks(normalizedTasks)
      const pending = normalizedTasks.find(t => t.status === 'pending') || normalizedTasks[0]
      activeTaskId.value = pending.id
    }else{
      queue.forEach(att => {
        if (att.status === 'uploading'){
          att.status = 'done'
        }
      })
    }

    input.value = ''

    await loadMessages()
    await loadTasks()
    activeClarificationId.value = ''

    const cleanupList = queue.slice()
    setTimeout(() => {
      cleanupList.forEach(revokeAttachmentObjectUrl)
      attachments.splice(0, attachments.length)
    }, 200)
  }catch(e:any){
    const errText = e?.response?.data?.error || e?.message || localize('アップロードに失敗しました', '上传失败', 'Upload failed')
    queue.forEach(att => {
      if (att.status === 'uploading'){
        att.status = 'error'
        att.error = errText
      }
    })
    pushEventMessage(localize(`一括解析に失敗しました：${errText}`, `批量解析失败：${errText}`, `Batch processing failed: ${errText}`), { status: 'error' })
  }finally{
    sending.value = false
  }
}

function handleLogout() {
  const keys = ['auth_token', 'company_code', 'currentUserName', 'currentCompany', 'userCaps', 'userRoles']
  keys.forEach(k => { localStorage.removeItem(k); sessionStorage.removeItem(k) })
  delete api.defaults.headers.common['Authorization']
  delete api.defaults.headers.common['x-company-code']
  router.push('/login')
}

async function onSelectCommon(key:string){
  try { console.debug('[ChatKit] menu select', key) } catch {}
  // 库存管理子菜单：统一以弹窗方式打开
  if (key.startsWith('inv.')) return openInModal(key, getTitle(key))
  // CRM 子菜单：统一以弹窗方式打开
  if (key.startsWith('crm.')) return openInModal(key, getTitle(key))
  // 固定資産子菜单：统一以弹窗方式打开
  if (key.startsWith('fa.')) return openInModal(key, getTitle(key))

  // 优先弹出内嵌对话框；若组件未注册，则跳路由兜底
  if (!embedMap[key]){
    // 路由兜底
    if (key==='hr.dept') return router.push('/hr/departments')
    if (key==='hr.emps') return router.push('/hr/employees')
    if (key==='hr.emp.new') return router.push('/hr/employee/new')
    if (key==='cert.request') return router.push('/cert/request')
    if (key==='company.settings') return router.push('/company/settings')
  }
  openInModal(key, getTitle(key))
}
function openInModal(key:string, title:string, payload?:any){
  if (!embedMap[key]){
    pushEventMessage(localize(`ページが登録されていません：${key}`, `页面未注册：${key}`, `Page not registered: ${key}`), { status: 'error' })
    return
  }
  const hasPayload = typeof payload !== 'undefined'
  pendingModalPayload.value = hasPayload ? payload : null
  pendingModalPayloadAttempts.value = 0
  modalOpen.value = false
  nextTick(() => {
    modal.key = key
    modal.title = title
    modal.renderKey++
    modalOpen.value = true
    if (hasPayload){
      nextTick().then(() => applyPendingModalPayload())
    }
  })
}
provide('chatkitOpenEmbed', (key: string, payload?: any) => openInModal(key, getTitle(key), payload))
provide('chatkitCloseModal', () => { modalOpen.value = false })
// 银行明细 AI 相談：子组件调用此方法关闭弹窗并填充银行交易消息到输入框
provide('chatkitStartBankConsult', (txId: string, message: string) => {
  handleBankConsultStart(txId, message)
})
function handleBankConsultStart(txId: string, message: string) {
  modalOpen.value = false
  pendingBankTransactionId.value = txId
  input.value = message
  nextTick(() => {
    chatInputRef.value?.focus?.()
  })
}
// 也通过 window 事件监听（解决 append-to-body 导致 provide/inject 断裂的问题）
function onBankConsultEvent(e: Event) {
  const detail = (e as CustomEvent).detail
  if (detail?.txId && detail?.message) {
    handleBankConsultStart(detail.txId, detail.message)
  }
}
window.addEventListener('chatkit:bankConsult', onBankConsultEvent)
function onModalDone(result:any){
  try{
    const summary = interpretModalResult(result)
    if (!summary) return
    pushEventMessage(summary.content, { status: summary.status, tag: summary.tag })
    // 凭证新建成功后，自动关闭当前弹窗，并跳转到凭证详情编辑画面
    if (result?.kind === 'voucher.created' && result?.status === 'success' && result?.voucherNo) {
      modalOpen.value = false
      nextTick(() => {
        openInModal('vouchers.list', getTitle('vouchers.list'), { voucherNo: result.voucherNo, detailOnly: true, autoEdit: true })
      })
    }
  }catch{}
}
function onModalClosed(){
  // 彻底清理，避免下次打开其它页面时残留上次组件
  modal.key = ''
  modal.title = ''
  modalRef.value = null
  pendingModalPayload.value = null
  pendingModalPayloadAttempts.value = 0
}

function storageKey(){ return `embed_cards:${activeSessionId.value || 'default'}` }
function saveEmbeds(){
  try{
    const embeds = messages.filter((m:any)=>m && m.kind==='embed')
    sessionStorage.setItem(storageKey(), JSON.stringify(embeds))
  }catch{}
}
function loadEmbeds(){
  try{
    const raw = sessionStorage.getItem(storageKey())
    if (!raw) return
    const arr = JSON.parse(raw)
    if (Array.isArray(arr)) arr.forEach((e:any)=> messages.push(e))
  }catch{}
}
function pushEmbed(key:string, title:string, options?:{ scroll?: boolean }){
  messages.push({ kind:'embed', key, title })
  saveEmbeds()
  if (options?.scroll !== false){
  nextTick().then(scrollToBottom)
  }
}

function scrollToBottom(){
  const el = chatBoxRef.value
  if (el) el.scrollTop = el.scrollHeight
}

function formatAmount(n:any){ const v=Number(n||0); return Number.isFinite(v)? v.toLocaleString('ja-JP'): '0' }

interface ChatAttachment {
  id: string
  file: File
  name: string
  size: number
  status: 'pending' | 'uploading' | 'done' | 'error'
  fileId?: string
  summary?: any
  error?: string
  objectUrl?: string
}

let attachmentIdSeed = 0

function genAttachmentId(): string {
  attachmentIdSeed = (attachmentIdSeed + 1) % 1_000_000
  return `att_${Date.now().toString(36)}_${attachmentIdSeed.toString(36)}`
}

function formatFileSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unitIndex = 0
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024
    unitIndex += 1
  }
  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unitIndex]}`
}

function formatAttachmentMeta(att: any){
  const parts: string[] = []
  if (Number.isFinite(Number(att?.size))) parts.push(formatFileSize(Number(att.size)))
  if (typeof att?.contentType === 'string') parts.push(att.contentType)
  return parts.join(' • ')
}

function normalizeAttachment(raw: any){
  if (!raw || typeof raw !== 'object') return null
  const obj = { ...raw }
  const name = obj.name || obj.fileName || obj.filename || ''
  if (name) obj.name = name
  if (!obj.fileName && name) obj.fileName = name
  const contentType = obj.contentType || obj.mimeType || ''
  if (contentType) obj.contentType = contentType
  if (obj.size == null && typeof obj.length === 'number') obj.size = obj.length
  if (!obj.previewUrl && obj.url) obj.previewUrl = obj.url
  return obj
}

function getMessageAttachments(msg: any, task?: InvoiceTask | null){
  const payload = msg?.payload
  if (!payload) return []
  const results: any[] = []
  if (msg?.kind === 'user.upload'){
    return []
  }
  if (Array.isArray(payload.attachments)){
    payload.attachments.forEach((att:any) => {
      const normalized = normalizeAttachment(att)
      if (normalized) results.push(normalized)
    })
  }
  if (Array.isArray(payload.documents)){
    payload.documents.forEach((doc:any) => {
      const normalized = normalizeAttachment(doc)
      if (normalized) results.push(normalized)
    })
  }
  if (!task || results.length === 0) return results

  const taskId = typeof task.id === 'string' ? task.id.trim() : ''
  const taskSessionId = typeof task.documentSessionId === 'string' ? task.documentSessionId.trim().toLowerCase() : ''
  const taskFileId = typeof task.fileId === 'string' ? task.fileId.trim().toLowerCase() : ''

  const msgTaskId = typeof msg?.taskId === 'string' ? msg.taskId.trim() : ''
  if (msgTaskId){
    if (taskId && msgTaskId !== taskId) return []
    return results
  }

  const messageSessionId = (() => {
    if (typeof msg?.payload?.documentSessionId === 'string') return msg.payload.documentSessionId.trim().toLowerCase()
    if (typeof msg?.payload?.document_session_id === 'string') return msg.payload.document_session_id.trim().toLowerCase()
    if (typeof msg?.documentSessionId === 'string') return msg.documentSessionId.trim().toLowerCase()
    return ''
  })()

  const messageFileId = (() => {
    if (typeof msg?.payload?.fileId === 'string') return msg.payload.fileId.trim().toLowerCase()
    if (typeof msg?.payload?.file_id === 'string') return msg.payload.file_id.trim().toLowerCase()
    if (typeof msg?.documentId === 'string') return msg.documentId.trim().toLowerCase()
    if (typeof msg?.payload?.documentId === 'string') return msg.payload.documentId.trim().toLowerCase()
    if (typeof msg?.payload?.document_id === 'string') return msg.payload.document_id.trim().toLowerCase()
    return ''
  })()

  if (taskSessionId && messageSessionId && taskSessionId !== messageSessionId) return []
  if (taskFileId && messageFileId && taskFileId !== messageFileId) return []

  const filtered = results.filter((att: any) => {
    const attTaskId = typeof att?.taskId === 'string' ? att.taskId.trim() : ''
    if (attTaskId && taskId && attTaskId === taskId) return true

    const attSessionId = typeof att?.documentSessionId === 'string'
      ? att.documentSessionId.trim().toLowerCase()
      : (typeof att?.document_session_id === 'string' ? att.document_session_id.trim().toLowerCase() : '')
    if (attSessionId && taskSessionId && attSessionId === taskSessionId) return true

    const attDocId = typeof att?.documentId === 'string'
      ? att.documentId.trim().toLowerCase()
      : (typeof att?.document_id === 'string' ? att.document_id.trim().toLowerCase() : '')
    if (attDocId && taskSessionId && attDocId === taskSessionId) return true

    const attFileId = typeof att?.fileId === 'string'
      ? att.fileId.trim().toLowerCase()
      : (typeof att?.file_id === 'string' ? att.file_id.trim().toLowerCase() : '')
    if (attFileId && taskFileId && attFileId === taskFileId) return true

    return false
  })

  if (filtered.length > 0) return filtered
  if (taskSessionId && !messageSessionId) return []
  if (taskFileId && !messageFileId) return []
  return results
}

function attachmentKey(att: any): string {
  if (!att || typeof att !== 'object') return Math.random().toString(36).slice(2)
  if (typeof att.id === 'string' && att.id) return att.id
  if (typeof att.fileId === 'string' && att.fileId) return att.fileId
  if (typeof att.file_id === 'string' && att.file_id) return att.file_id
  if (typeof att.url === 'string' && att.url) return att.url
  if (typeof att.previewUrl === 'string' && att.previewUrl) return att.previewUrl
  if (typeof att.name === 'string' && att.name) return `${att.name}-${Math.random().toString(36).slice(2, 6)}`
  return JSON.stringify(att)
}

function isClarificationMessage(msg: any): boolean {
  return Boolean(msg && msg.status === 'clarify' && msg.tag && msg.tag.questionId)
}

function isSalesChartMessage(msg: any): boolean {
  return Boolean(msg && msg.status === 'chart' && msg.tag?.kind === 'salesChart' && msg.tag?.echartsConfig)
}

function clarificationQuestion(msg: any): string {
  if (!isClarificationMessage(msg)) return ''
  return (msg.tag?.question || msg.content || '').toString()
}

function clarificationLabelValue(msg: any): string {
  if (!isClarificationMessage(msg)) return ''
  const label = msg?.tag?.documentLabel
  if (typeof label === 'string' && label.trim()) return label.trim()
  const sessionId = msg?.tag?.documentSessionId ? String(msg.tag.documentSessionId).trim() : ''
  if (sessionId) {
    const mappedTask = taskByDocumentSession.value.get(sessionId.toLowerCase())
    if (mappedTask?.label) return mappedTask.label
  }
  return ''
}

function clarificationDetail(msg: any): string {
  if (!isClarificationMessage(msg)) return ''
  return (msg.tag?.detail || '').toString()
}

function clarificationAnsweredAt(msg: any): string | null {
  if (!isClarificationMessage(msg)) return null
  const answered = msg?.payload?.answeredAt || msg?.tag?.answeredAt
  return typeof answered === 'string' && answered ? answered : null
}

function clarificationLabel(msg: any): string {
  return clarificationLabelValue(msg)
}

function clarificationEntryForMessage(msg: any): ClarificationEntry | undefined {
  if (!isClarificationMessage(msg)) return undefined
  const questionId = typeof msg?.tag?.questionId === 'string' ? msg.tag.questionId : ''
  if (!questionId) return undefined
  return clarificationMap.value.get(questionId)
}

function clarificationAnswers(msg: any): ClarificationDisplayAnswer[] {
  const entry = clarificationEntryForMessage(msg)
  if (!entry) return []
  const confirmed = Array.isArray(entry.answers)
    ? entry.answers.map(answer => ({ ...answer, pending: false }))
    : []
  const pending = entry.questionId && pendingClarificationAnswers[entry.questionId]
    ? pendingClarificationAnswers[entry.questionId].map(answer => ({ ...answer, pending: true }))
    : []
  return [...confirmed, ...pending]
}

function replyClarification(msg: any){
  if (!isClarificationMessage(msg)) return
  const questionId = msg?.tag?.questionId
  if (!questionId) return
  activeClarificationId.value = questionId
  let docSessionId = msg?.tag?.documentSessionId ? String(msg.tag.documentSessionId).trim() : ''
  if (!docSessionId && msg?.tag?.documentId){
    const mappedTask = taskByFileId.value.get(msg.tag.documentId)
    if (mappedTask){
      docSessionId = mappedTask.documentSessionId
    }
  }
  if (docSessionId){
    const mappedTask = taskByDocumentSession.value.get(docSessionId.toLowerCase())
    if (mappedTask){
      activeTaskId.value = mappedTask.id
    }
  }
  nextTick(() => {
    try{ chatInputRef.value?.focus?.() }catch{}
  })
}

function cancelClarification(){
  activeClarificationId.value = ''
}

function attachmentStatus(att: ChatAttachment): string {
  if (att.status === 'pending') return localize('送信待ち', '待提交', 'Pending')
  if (att.status === 'uploading') return localize('アップロード中...', '上传中...', 'Uploading...')
  if (att.status === 'done') return localize('アップロード済み', '已上传', 'Uploaded')
  if (att.status === 'error') return att.error || localize('アップロードに失敗しました', '上传失败', 'Upload failed')
  return ''
}

const unnamedFileLabel = () => localize('名称未設定ファイル', '未命名文件', 'Untitled file')
const maxAttachmentLimitMessage = (count: number) =>
  localize(`同時に添付できるファイルは最大 ${count} 件です`, `最多只支持同时上传 ${count} 个附件`, `You can attach up to ${count} files at once`)
const fileTooLargeMessage = (fileName: string, maxSize: number) =>
  localize(
    `${fileName} は添付上限（最大 ${formatFileSize(maxSize)}）を超えています`,
    `${fileName} 超出大小限制（最大 ${formatFileSize(maxSize)}）`,
    `${fileName} exceeds the size limit (max ${formatFileSize(maxSize)})`
  )
const duplicateAttachmentMessage = (name: string) =>
  localize(`${name} はすでにアップロード待ちです`, `${name} 已在上传队列中`, `${name} is already in the upload queue`)

function openImagePreview(att: any){
  if (!att) return
  const url = att.previewUrl || att.url
  if (!url) return
  imagePreview.url = url
  imagePreview.name = att.name || att.fileName || ''
  imagePreview.visible = true
}

function isImageFile(att: any){
  if (!att) return false
  const type = typeof att?.contentType === 'string' ? att.contentType : (att?.file?.type || '')
  return typeof type === 'string' && type.startsWith('image/')
}

function isImageAttachment(att: any){
  return isImageFile(att)
}

function isPdfAttachment(att: any){
  if (!att) return false
  const type = typeof att?.contentType === 'string' ? att.contentType : (att?.file?.type || '')
  const name = att?.name || att?.fileName || ''
  return (typeof type === 'string' && type === 'application/pdf') || 
         (typeof name === 'string' && name.toLowerCase().endsWith('.pdf'))
}

async function openFilePreview(att: any){
  if (!att) return
  const url = att.url
  if (!url) return
  const name = att.name || att.fileName || 'file'
  
  if (isPdfAttachment(att)) {
    // PDF 预览
    pdfPreview.value = { visible: true, url: '', name, loading: true }
    try {
      const resp = await api.get(url, { responseType: 'blob' })
      const data = resp?.data
      const blob = data instanceof Blob ? data : new Blob([data], { type: 'application/pdf' })
      const blobUrl = URL.createObjectURL(blob)
      pdfPreview.value.url = blobUrl
      pdfPreview.value.loading = false
    } catch (e: any) {
      ElMessage.error(e?.message || 'PDF 加载失败')
      pdfPreview.value.visible = false
    }
  } else {
    // 其他文件直接下载
    window.open(url, '_blank')
  }
}

function attachmentThumbnail(att: any){
  if (!att) return ''
  if (typeof att.previewUrl === 'string' && att.previewUrl) return att.previewUrl
  if (typeof att.url === 'string' && att.url) return att.url
  if (typeof att.objectUrl === 'string' && att.objectUrl) return att.objectUrl
  return ''
}

function revokeAttachmentObjectUrl(att: ChatAttachment | any){
  if (att && typeof att.objectUrl === 'string' && att.objectUrl){
    URL.revokeObjectURL(att.objectUrl)
    att.objectUrl = undefined
  }
}

function triggerFileDialog(){
  if (sending.value) return
  if (attachments.length >= maxAttachmentCount){
    pushEventMessage(maxAttachmentLimitMessage(maxAttachmentCount), { status: 'error' })
    return
  }
  fileInputRef.value?.click()
}

function onFileChange(e: Event){
  const inputEl = e.target as HTMLInputElement | null
  if (!inputEl?.files) return
  addFiles(Array.from(inputEl.files))
  inputEl.value = ''
}

function addFiles(files: File[]){
  for (const file of files){
    if (attachments.length >= maxAttachmentCount){
      pushEventMessage(maxAttachmentLimitMessage(maxAttachmentCount), { status: 'error' })
      break
    }
    if (file.size > maxAttachmentSize){
      pushEventMessage(fileTooLargeMessage(file.name, maxAttachmentSize), { status: 'error' })
      continue
    }
    const name = file.name || unnamedFileLabel()
    const duplicate = attachments.find(att => att.name === name && att.size === file.size && att.status !== 'error')
    if (duplicate){
      pushEventMessage(duplicateAttachmentMessage(name), { status: 'info' })
      continue
    }
    const objectUrl = file.type.startsWith('image/') ? URL.createObjectURL(file) : undefined
    attachments.push({ id: genAttachmentId(), file, name, size: file.size, status: 'pending', objectUrl })
  }
}

function removeAttachment(id: string){
  const idx = attachments.findIndex(att => att.id === id)
  if (idx >= 0){
    revokeAttachmentObjectUrl(attachments[idx])
    attachments.splice(idx, 1)
  }
}

function onChatDragOver(evt: DragEvent){
  try{
    if (evt.dataTransfer) evt.dataTransfer.dropEffect = 'copy'
  }catch{}
  isDragOver.value = true
}

function onChatDrop(evt: DragEvent){
  try{
    const files = evt.dataTransfer?.files
    if (!files || files.length === 0) return
    addFiles(Array.from(files))
  }finally{
    if (evt.dataTransfer) evt.dataTransfer.clearData()
    isDragOver.value = false
  }
}

function onChatDragLeave(){
  isDragOver.value = false
}

</script>
<style scoped>
.chatkit-wrap {
  position: fixed;
  inset: 0;
  display: flex;
  height: 100vh;
  width: 100vw;
  background: var(--color-page-bg);
  color: var(--color-text-primary);
  font-family: var(--font-family-base);
}

.sidebar {
  width: 260px;
  background: linear-gradient(180deg, #1e293b 0%, #0f172a 100%);
  display: flex;
  flex-direction: column;
  color: rgba(255, 255, 255, 0.9);
  box-shadow: 12px 0 28px rgba(15, 23, 42, 0.25);
  z-index: 20;
}

.sidebar-header {
  padding: 6px 22px 6px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.03);
}

.brand {
  display: flex;
  justify-content: center;
  align-items: center;
}

.brand-logo {
  height: 96px;
  width: auto;
}

.brand-title {
  font-size: 18px;
  font-weight: var(--font-weight-semibold);
  letter-spacing: 0.04em;
}

.brand-sub {
  margin-top: 6px;
  font-size: 12px;
  opacity: 0.7;
}

.sidebar-scroll {
  flex: 1;
  overflow-y: auto;
  padding: 18px 0 28px;
}

/* 侧边栏滚动条样式 - 在深色背景上更明显 */
.sidebar-scroll::-webkit-scrollbar {
  width: 6px;
}

.sidebar-scroll::-webkit-scrollbar-thumb {
  background: rgba(255, 255, 255, 0.25);
  border-radius: 3px;
}

.sidebar-scroll::-webkit-scrollbar-thumb:hover {
  background: rgba(255, 255, 255, 0.4);
}

.sidebar-scroll::-webkit-scrollbar-track {
  background: rgba(255, 255, 255, 0.05);
  border-radius: 3px;
}

.section {
  padding: 0 16px 24px;
}

.section-title {
  font-size: 11px;
  letter-spacing: 0.18em;
  text-transform: uppercase;
  color: rgba(255, 255, 255, 0.5);
  margin-bottom: 12px;
}

/* 动态菜单子菜单组样式 */
.submenu-group {
  margin: 8px 0;
}

.submenu-title {
  font-size: 10px;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: rgba(255, 255, 255, 0.35);
  padding: 4px 12px 4px 8px;
  margin-top: 8px;
}

.submenu-group .el-menu-item {
  padding-left: 24px !important;
  font-size: 12px;
}

.session-actions {
  padding-top: 12px;
}

:deep(.sidebar .el-button) {
  width: 100%;
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.14);
  border-color: transparent;
  color: #fff;
}

:deep(.sidebar .el-button:hover) {
  background: rgba(255, 255, 255, 0.22);
}

:deep(.sidebar .el-menu) {
  background: transparent;
  border-right: none;
}

:deep(.sidebar .el-menu-item),
:deep(.sidebar .el-sub-menu__title) {
  height: 40px;
  border-radius: 10px;
  margin: 2px 0;
  color: rgba(255, 255, 255, 0.7);
  font-size: 13px;
  transition: all 0.2s ease;
}

:deep(.sidebar .el-menu-item:hover),
:deep(.sidebar .el-sub-menu__title:hover) {
  background: rgba(59, 130, 246, 0.25);
  color: #fff;
}

:deep(.sidebar .el-menu-item.is-active) {
  background: linear-gradient(90deg, rgba(59, 130, 246, 0.4) 0%, rgba(59, 130, 246, 0.15) 100%);
  color: #fff;
  border-left: 3px solid #3b82f6;
  padding-left: 17px;
}

:deep(.sidebar .el-sub-menu__icon-arrow) {
  color: rgba(255, 255, 255, 0.45);
}

.main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  background: var(--color-page-bg);
}

.main-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 18px 32px;
}

.header-left {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.page-title {
  font-size: 22px;
  font-weight: var(--font-weight-semibold);
  color: #1f2937;
}

.header-right {
  display: flex;
  align-items: center;
  gap: 16px;
}

.lang-select {
  width: 120px;
}

.profile-box {
  display: flex;
  align-items: flex-start;
  gap: 16px;
}

.company-info {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding-top: 2px;
}

.company-badge {
  background: rgba(59, 130, 246, 0.16);
  color: var(--color-primary);
  padding: 2px 10px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: var(--font-weight-medium);
  letter-spacing: 0.02em;
}

.company-name-sub {
  font-size: 11px;
  color: #333;
  margin-top: 2px;
}

.user-chip {
  display: flex;
  align-items: center;
  gap: 12px;
}

.avatar {
  width: 42px;
  height: 42px;
  border-radius: 50%;
  background: var(--color-primary);
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: var(--font-weight-semibold);
  letter-spacing: 0.04em;
}

.user-meta {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
}

.user-name {
  font-weight: var(--font-weight-semibold);
  color: #1f2937;
  font-size: 14px;
}

.user-role {
  font-size: 12px;
  color: var(--color-text-secondary);
}

.workspace {
  flex: 1;
  display: flex;
  gap: 26px;
  padding: 0 40px 36px;
  min-height: 0;
  position: relative;
}

.workspace.withDock {
  padding-right: 24px;
}

.chat-card {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--color-card-bg);
  border-radius: 22px;
  box-shadow: var(--shadow-card);
  overflow: hidden;
  position: relative;
}

.chat-card::before {
  content: '';
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: 40px;
  background: linear-gradient(180deg, rgba(255, 255, 255, 0.95) 0%, rgba(255, 255, 255, 0) 100%);
  pointer-events: none;
  z-index: 10;
  border-radius: 22px 22px 0 0;
}

.chat-toolbar {
  padding: 12px 24px 0;
  display: flex;
  justify-content: flex-end;
}

.task-panel {
  width: 280px;
  flex: 0 0 280px;
  display: flex;
  flex-direction: column;
  background: var(--color-card-bg);
  border-radius: 22px;
  box-shadow: var(--shadow-card);
  overflow: hidden;
}

.task-panel-header {
  padding: 22px 24px 14px;
  border-bottom: 1px solid rgba(15, 23, 42, 0.06);
}

.task-panel-title {
  font-size: 15px;
  font-weight: var(--font-weight-semibold);
  color: #1f2937;
}

.task-panel-body {
  flex: 1;
  overflow-y: auto;
  padding: 8px 10px 16px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.task-panel-item {
  background: transparent;
  border-radius: 10px;
  border: none;
  padding: 10px 12px;
  cursor: pointer;
  transition: all 0.15s ease;
  display: flex;
  flex-direction: column;
  gap: 4px;
  box-shadow: none;
  position: relative;
}

.task-panel-item::before {
  content: '';
  position: absolute;
  left: 0;
  top: 12px;
  bottom: 12px;
  width: 3px;
  background: var(--color-primary);
  border-radius: 0 2px 2px 0;
  opacity: 0;
  transition: opacity 0.2s ease;
}

.task-panel-item:hover::before,
.task-panel-item.active::before {
  opacity: 1;
}

.task-panel-item:hover {
  background: rgba(59, 130, 246, 0.06);
}

.task-panel-item.active {
  background: rgba(59, 130, 246, 0.1);
}
.task-panel-item.active::before {
  opacity: 1;
}

.task-panel-item.completed {
  opacity: 0.6;
}

.task-panel-item.failed::before,
.task-panel-item.error::before {
  background: #ef4444;
  opacity: 1;
}

.task-panel-item.cancelled {
  opacity: 0.45;
}

/* 不同类型任务的指示条颜色 */
.task-panel-item.kind-invoice::before {
  background: var(--color-primary);
}

.task-panel-item.kind-approval::before {
  background: #8b5cf6; /* 紫色 */
  opacity: 1;
}

.task-panel-item.kind-sales_order::before {
  background: #f59e0b; /* 橙色 */
}

/* 未完成任务显示橘红色 */
.task-panel-item:not(.completed):not(.cancelled)::before {
  background: #f97316; /* 橘红色 */
  opacity: 1;
}

/* 已完成任务显示绿色（优先级更高） */
.task-panel-item.completed::before {
  background: #10b981 !important;
}

.task-item-header {
  display: flex;
  align-items: center;
  gap: 8px;
  justify-content: space-between;
}

.task-header-info {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.task-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.task-delete-btn {
  margin-left: 4px;
  opacity: 0.6;
  transition: opacity 0.2s;
}

.task-delete-btn:hover {
  opacity: 1;
}

.task-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 2px 10px;
  border-radius: 999px;
  background: rgba(59, 130, 246, 0.18);
  color: var(--color-primary);
  font-weight: 600;
  font-size: 12px;
}

.task-name {
  font-weight: 500;
  color: #334155;
  font-size: 13px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 180px;
}
.task-panel-item.active .task-name {
  color: var(--color-primary);
  font-weight: 600;
}

.task-item-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--color-text-secondary);
}

.task-item-summary {
  flex: 1;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  color: #94a3b8;
}

.chat-content {
  flex: 1;
  padding: 20px 24px;
  padding-top: 28px;
  overflow-y: auto;
  background: linear-gradient(180deg, #ffffff 0%, #f8fafc 100%);
  position: relative;
}

.timeline-messages {
  display: flex;
  flex-direction: column;
  gap: 12px;
  margin-bottom: 20px;
}

.timeline-header {
  font-size: 13px;
  font-weight: 600;
  color: #1f2937;
  letter-spacing: 0.02em;
}

.timeline-header.active {
  color: var(--color-primary);
}

.general-banner {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-radius: 10px;
  border: 1px dashed rgba(148, 163, 184, 0.6);
  background: #f8fafc;
  color: #1f2937;
  font-size: 12px;
}

.general-banner::before {
  content: '●';
  font-size: 10px;
  color: var(--color-primary);
}

.task-conversation {
  background: #ffffff;
  border-radius: 14px;
  padding: 16px 18px;
  margin-bottom: 12px;
  border: 1.5px solid transparent;
  cursor: pointer;
  transition: border-color 0.2s ease, box-shadow 0.2s ease;
}
.task-conversation:hover {
  border-color: rgba(59, 130, 246, 0.15);
}

.task-conversation.active {
  border-color: rgba(59, 130, 246, 0.35);
  box-shadow: 0 2px 12px rgba(59, 130, 246, 0.1);
}

.task-conversation.general {
  background: linear-gradient(180deg, rgba(15, 23, 42, 0.02) 0%, rgba(15, 23, 42, 0.05) 100%);
}

.task-conversation-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 8px;
}

.task-header-main {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.task-conversation-name {
  font-size: 14px;
  font-weight: 500;
  color: #1f2937;
  max-width: 360px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.task-conversation-summary {
  font-size: 12px;
  color: #64748b;
  line-height: 1.45;
  margin-bottom: 12px;
}

.task-conversation-attachments {
  margin-bottom: 12px;
  display: flex;
}

.task-panel-completed {
  margin-top: 12px;
}

.task-panel-completed-header {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  border-radius: 8px;
  cursor: pointer;
  color: #475569;
  font-size: 12px;
  transition: color 0.2s ease, background 0.2s ease;
}

.task-panel-completed-header:hover {
  color: #1d4ed8;
  background: rgba(59, 130, 246, 0.08);
}

.task-completed-icon {
  display: inline-flex;
  transition: transform 0.2s ease;
}

.task-panel-completed-list {
  margin-top: 8px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.approval-conversation {
  display: flex;
  flex-direction: column;
  gap: 12px;
  font-size: 13px;
  color: var(--color-text-secondary);
}

.approval-conversation-row {
  display: flex;
  gap: 12px;
  align-items: flex-start;
}

.approval-conversation-label {
  width: 90px;
  font-weight: 600;
  color: #1f2937;
}

.approval-conversation-value {
  flex: 1;
}

.approval-conversation-value.multiline {
  white-space: pre-wrap;
  line-height: 1.6;
}

.approval-conversation-summary {
  font-size: 13px;
  line-height: 1.6;
  color: #475569;
  background: #f8fbff;
  border-radius: 12px;
  padding: 12px 14px;
  border: 1px solid rgba(148, 163, 184, 0.25);
}

.approval-conversation-actions {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
}

.task-conversation-messages {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.sales-order-conversation {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.sales-order-meta {
  display: flex;
  flex-direction: column;
  gap: 6px;
  background: #f8fafc;
  border-radius: 12px;
  padding: 12px 16px;
}

.sales-order-meta-row {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  font-size: 13px;
}

.sales-order-meta-label {
  width: 96px;
  color: #6b7280;
  font-weight: 600;
  flex-shrink: 0;
}

.sales-order-meta-value {
  color: #111827;
  flex: 1;
  word-break: break-word;
}

.link-button {
  border: none;
  background: transparent;
  color: var(--color-primary);
  padding: 0;
  cursor: pointer;
  font: inherit;
  text-decoration: underline;
}

.link-button:hover {
  opacity: 0.85;
}

.sales-order-detail-dialog :deep(.el-dialog__body) {
  max-height: 75vh;
  overflow-y: auto;
}

.sales-order-detail-content {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.sales-order-detail-descriptions :deep(.el-descriptions__body) {
  word-break: break-word;
}

.sales-order-detail-error {
  color: #ef4444;
  font-weight: 600;
}

.sales-order-lines {
  background: #ffffff;
  border: 1px solid #e5e7eb;
  border-radius: 12px;
  padding: 12px 16px;
  overflow: hidden;
}

.sales-order-lines-title {
  font-weight: 600;
  color: #111827;
  margin-bottom: 8px;
}

.sales-order-lines table {
  width: 100%;
  border-collapse: collapse;
}

.sales-order-lines th {
  font-size: 12px;
  color: #6b7280;
  font-weight: 600;
  text-align: left;
  padding: 6px 8px;
  border-bottom: 1px solid #e5e7eb;
}

.sales-order-lines td {
  font-size: 13px;
  color: #111827;
  padding: 8px;
  border-bottom: 1px solid #f3f4f6;
  vertical-align: top;
}

.sales-order-lines tbody tr:last-child td {
  border-bottom: none;
}

.sales-order-line-no {
  width: 56px;
  color: #6b7280;
}

.sales-order-line-item {
  min-width: 200px;
}

.sales-order-line-name {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  font-weight: 600;
  color: #111827;
}

.sales-order-line-name .line-code {
  font-size: 12px;
  color: #4f46e5;
  background: rgba(79, 70, 229, 0.08);
  border-radius: 999px;
  padding: 2px 8px;
  font-weight: 500;
}

.sales-order-line-desc,
.sales-order-line-note {
  margin-top: 4px;
  font-size: 12px;
  color: #6b7280;
}

.sales-order-line-qty {
  min-width: 90px;
  text-align: right;
}

.sales-order-line-price,
.sales-order-line-amount {
  text-align: right;
  white-space: nowrap;
}

.task-empty {
  text-align: center;
  font-size: 13px;
  color: #94a3b8;
  padding: 8px 0;
}

.msg {
  display: flex;
  margin-bottom: 12px;
}

.msg.user {
  justify-content: flex-end;
}

.msg.assistant {
  justify-content: flex-start;
}

.bubble {
  max-width: 65%;
  padding: 10px 14px;
  border-radius: 14px;
  font-size: 14px;
  line-height: 1.5;
  word-break: break-word;
  box-shadow: var(--shadow-soft);
  display: flex;
  gap: 8px;
  align-items: flex-start;
  justify-content: space-between;
}

.msg.user .bubble {
  background: var(--color-primary);
  color: #fff;
}

.msg.assistant .bubble {
  background: #ffffff;
  color: #1f2937;
}

.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.2s ease;
}

.fade-enter-from,
.fade-leave-to {
  opacity: 0;
}

.bubble-text {
  flex: 1;
  white-space: pre-wrap;
}

.clarify-card {
  border: 1px solid rgba(59, 130, 246, 0.18);
  background: rgba(59, 130, 246, 0.06);
  border-radius: 10px;
  padding: 10px 12px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.clarify-card.active {
  border-color: rgba(59, 130, 246, 0.45);
  box-shadow: 0 0 0 1px rgba(59, 130, 246, 0.2);
}

.clarify-card.answered {
  opacity: 0.7;
}

.clarify-question {
  font-weight: 600;
  color: #1f2937;
}

.clarify-meta {
  font-size: 12px;
  color: #475569;
}

.clarify-detail {
  font-size: 12px;
  color: #334155;
  white-space: pre-wrap;
}

.clarify-answer {
  border: 1px dashed rgba(59, 130, 246, 0.35);
  border-radius: 8px;
  padding: 8px 10px;
  background: rgba(255, 255, 255, 0.8);
}

.clarify-answer-label {
  font-size: 12px;
  font-weight: 600;
  color: var(--color-primary);
  margin-bottom: 4px;
  display: block;
}

.clarify-answer-item + .clarify-answer-item {
  margin-top: 4px;
}

.clarify-answer-content {
  font-size: 13px;
  color: #1f2937;
  white-space: pre-wrap;
}

.clarify-answer-pending {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-left: 8px;
  font-size: 12px;
  color: #64748b;
}

.clarify-loading-icon {
  animation: clarify-spin 1s linear infinite;
}

@keyframes clarify-spin {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}

.clarify-label {
  font-weight: 600;
  color: var(--color-primary);
  margin-right: 6px;
}

.clarify-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.bubble-attachments {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 8px;
}

.attachment-file {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 10px 14px;
  border-radius: 12px;
  background: rgba(15, 23, 42, 0.04);
  color: inherit;
  text-decoration: none;
  transition: background 0.2s ease;
  max-width: 260px;
}

.attachment-file:hover {
  background: rgba(59, 130, 246, 0.14);
}

.attachment-thumb {
  position: relative;
  display: inline-flex;
  flex-direction: column;
  text-decoration: none;
  border-radius: 12px;
  overflow: hidden;
  max-width: 220px;
  background: rgba(15, 23, 42, 0.08);
  cursor: pointer;
}

.attachment-thumb img {
  display: block;
  width: 100%;
  height: auto;
}

.attachment-thumb:focus {
  outline: 2px solid rgba(59, 130, 246, 0.6);
  outline-offset: 2px;
}

.attachment-meta {
  font-size: 12px;
  color: rgba(15, 23, 42, 0.6);
}

.thumb-meta {
  padding: 6px 10px;
}

.attachment-name {
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.file-icon {
  font-size: 14px;
  color: rgba(15, 23, 42, 0.6);
}

.image-preview-dialog {
  text-align: center;
}

.image-preview-dialog .preview-image {
  max-width: 80vw;
  max-height: 80vh;
  border-radius: 12px;
  box-shadow: var(--shadow-soft);
}

.pdf-preview-dialog .pdf-preview-iframe {
  width: calc(85vh * 0.707);  /* A4 ratio: width = height * (210/297) */
  height: 85vh;
  min-width: 500px;
  max-width: 90vw;
  border: none;
  border-radius: 8px;
}

.pdf-preview-dialog .pdf-loading {
  width: calc(85vh * 0.707);
  min-width: 500px;
  padding: 20px;
}

.msg-tag {
  cursor: pointer;
  flex-shrink: 0;
  margin-top: 2px;
  white-space: nowrap;
}

.msg.status-success .bubble {
  border-left: 4px solid #16a34a;
}

.msg.status-error .bubble {
  border-left: 4px solid #dc2626;
}

.empty {
  margin-top: 40px;
  text-align: center;
  color: var(--color-text-secondary);
  font-size: 13px;
}

.chat-input {
  padding: 20px 28px 24px;
  border-top: 1px solid rgba(15, 23, 42, 0.06);
  background: #fff;
}

.input-shell {
  border: 1px dashed rgba(148, 163, 184, 0.6);
  border-radius: 16px;
  background: #f8fafc;
  padding: 14px;
  display: flex;
  flex-direction: column;
  gap: 12px;
  transition: border-color 0.2s ease, background-color 0.2s ease;
}

.input-shell.drag-hover {
  border-color: rgba(59, 130, 246, 0.7);
  background: #eff6ff;
}

.task-context-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 14px;
  border: 1px solid rgba(16, 185, 129, 0.3);
  background: rgba(16, 185, 129, 0.06);
  border-radius: 10px;
  margin-bottom: 4px;
}
.task-context-info {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: #1f2937;
  min-width: 0;
  overflow: hidden;
}
.task-context-icon {
  flex-shrink: 0;
}
.task-context-label {
  font-weight: 600;
  color: #059669;
  flex-shrink: 0;
}
.task-context-name {
  color: #374151;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.clarify-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border: 1px solid rgba(59, 130, 246, 0.25);
  background: rgba(59, 130, 246, 0.08);
  border-radius: 10px;
}

.clarify-banner-text {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 13px;
  color: #1f2937;
}

.clarify-banner-label {
  font-weight: 600;
  color: var(--color-primary);
}

.input-attachments {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.input-row {
  display: flex;
  align-items: flex-end;
  gap: 12px;
}

.chat-textarea {
  flex: 1;
  min-width: 0;
}

.chat-send-button {
  width: 46px;
  height: 46px;
  border-radius: 14px;
  padding: 0;
  flex-shrink: 0;
  font-weight: var(--font-weight-semibold);
  --el-button-text-color: #fff;
  --el-button-bg-color: var(--color-primary);
  --el-button-border-color: var(--color-primary);
  --el-button-hover-bg-color: var(--color-primary);
  --el-button-hover-border-color: var(--color-primary);
  --el-button-active-bg-color: var(--color-primary);
  --el-button-active-border-color: var(--color-primary);
  box-shadow: 0 10px 24px rgba(59, 130, 246, 0.28);
  transition: transform 0.2s ease, box-shadow 0.2s ease;
}

.chat-send-button:not(:disabled):hover {
  transform: translateY(-1px);
  box-shadow: 0 16px 32px rgba(59, 130, 246, 0.32);
}

.chat-send-button:disabled {
  opacity: 0.7;
  cursor: not-allowed;
  box-shadow: 0 6px 16px rgba(59, 130, 246, 0.2);
}

.chat-send-button:focus-visible {
  outline: none;
  box-shadow:
    0 0 0 3px rgba(59, 130, 246, 0.25),
    0 12px 28px rgba(59, 130, 246, 0.28);
}

.attach-trigger {
  width: 38px;
  height: 38px;
  border-radius: 14px;
  border: 1px solid rgba(148, 163, 184, 0.5);
  background: #ffffff;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #2563eb;
  font-size: 18px;
  cursor: pointer;
  transition: border-color 0.2s ease, background 0.2s ease, color 0.2s ease;
}

.attach-trigger:hover:not(:disabled) {
  background: #2563eb;
  color: #ffffff;
  border-color: #2563eb;
}

.attach-trigger:disabled {
  cursor: not-allowed;
  opacity: 0.35;
}

:deep(.chat-input .el-textarea__inner) {
  background: #ffffff;
  border-radius: 14px;
  border: none;
  box-shadow: none;
  font-size: 14px;
  min-height: 96px;
  padding: 14px 16px;
}

:deep(.chat-input .el-textarea__inner:focus) {
  box-shadow:
    0 0 0 3px rgba(59, 130, 246, 0.16);
}

.rcpt-form-grid {
  margin: 8px 0 16px;
}
.rcpt-form-row {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  margin-bottom: 12px;
}
.rcpt-form-row:last-child {
  margin-bottom: 0;
}
.rcpt-form-item {
  display: flex;
  align-items: center;
  gap: 8px;
}
.rcpt-label {
  font-size: 13px;
  color: #606266;
  white-space: nowrap;
}
.rcpt-label.required::before {
  content: '*';
  color: #f56c6c;
  margin-right: 2px;
}
.rcpt-input-account {
  width: 200px;
}
.rcpt-input-partner {
  width: 240px;
}
.rcpt-input-amt {
  width: 120px;
}
.rcpt-input-date-short {
  width: 130px !important;
}
:deep(.rcpt-input-date-short) {
  width: 130px !important;
}
:deep(.rcpt-input-date-short .el-input__wrapper) {
  width: 100% !important;
}
.rcpt-input-fee {
  width: 100px;
}
.rcpt-input-fee-amt {
  width: 80px;
}
.rcpt-currency-badge {
  display: inline-flex;
  align-items: center;
  padding: 0 8px;
  height: 24px;
  background: #f0f2f5;
  border-radius: 4px;
  font-size: 12px;
  color: #606266;
  margin-left: 4px;
}

.w-amount {
  width: 180px;
}

.w-date {
  width: 180px;
}

.w-currency {
  width: 120px;
}

.w-account {
  width: 280px;
}

.w-fee-bearer {
  width: 180px;
}

.todo-empty {
  color: var(--color-text-secondary);
}

::deep(.planner-dialog .el-form-item__label) {
  color: #475569;
}

.planner-empty {
  margin: 6px 0;
  color: #6b7280;
}

.planner-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 16px;
  margin-top: 16px;
}

.planner-summary {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 14px;
  color: #334155;
}

.planner-warning {
  color: #d93025;
}

.planner-fee-info {
  color: #475569;
}

.planner-footer-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.chat-attachments {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  width: 100%;
}

.attachment-tile {
  position: relative;
  width: 96px;
  padding: 10px 8px 12px;
  border-radius: 14px;
  border: 1px solid rgba(148, 163, 184, 0.5);
  background: #ffffff;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  transition: border-color 0.2s ease, box-shadow 0.2s ease;
}

.attachment-tile.image .tile-thumb {
  background: #f1f5f9;
}

.attachment-tile.uploading {
  border-color: rgba(59, 130, 246, 0.6);
  box-shadow: 0 6px 18px -8px rgba(59, 130, 246, 0.4);
}

.attachment-tile.done {
  border-color: rgba(34, 197, 94, 0.5);
}

.attachment-tile.error {
  border-color: rgba(239, 68, 68, 0.55);
}

.tile-thumb {
  width: 64px;
  height: 64px;
  border-radius: 12px;
  overflow: hidden;
  background: #f8fafc;
  display: flex;
  align-items: center;
  justify-content: center;
}

.tile-thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.tile-icon {
  font-size: 22px;
  color: #64748b;
}

.tile-remove {
  position: absolute;
  top: 4px;
  right: 4px;
  width: 22px;
  height: 22px;
  border: none;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(255, 255, 255, 0.9);
  color: #475569;
  cursor: pointer;
  transition: background 0.2s ease, color 0.2s ease;
}

.tile-remove:hover:not(:disabled) {
  background: #1f2937;
  color: #ffffff;
}

.tile-remove:disabled {
  cursor: not-allowed;
  opacity: 0.4;
}

.tile-status {
  font-size: 11px;
  color: #475569;
  text-align: center;
  line-height: 1.2;
}

.attachment-tile.uploading .tile-status {
  color: #2563eb;
}

.attachment-tile.error .tile-status {
  color: #dc2626;
}

.tile-name {
  font-size: 11px;
  color: #334155;
  text-align: center;
  max-width: 80px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.chat-file-input {
  display: none;
}

.cell-remark {
  display: inline-block;
  max-width: 260px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: middle;
}

:deep(.plan-row-selected td) {
  background-color: #ecfdf5 !important;
}

:deep(.plan-row-selected:hover td) {
  background-color: #d1fae5 !important;
}

@media (max-width: 1360px) {
  .sidebar {
    width: 240px;
  }

  .main-header {
    padding: 28px 28px 16px;
  }

  .workspace {
    padding: 0 28px 24px;
  }
}

@media (max-width: 960px) {
  .chatkit-wrap {
    flex-direction: column;
  }

  .sidebar {
    width: 100%;
    height: auto;
    box-shadow: none;
  }

  .sidebar-scroll {
    max-height: 260px;
  }

  .main {
    height: calc(100vh - 260px);
  }
}
</style>
<style>
/* Global overrides for embedded Element Plus dialogs */
.el-overlay-dialog {
  /* display: flex without !important so Element Plus v-show can still apply display:none to close dialogs */
  display: flex;
  align-items: flex-start !important;
  justify-content: center !important;
  overflow-y: auto !important;
  overflow-x: hidden !important;
  padding: 3vh 0 !important;
  pointer-events: auto !important;
}

.el-overlay-dialog .el-dialog.embed-dialog {
  margin: auto !important;
}

.el-dialog.embed-dialog {
  background: transparent !important;
  box-shadow: none !important;
  border: none !important;
  padding: 0 !important;
  width: auto !important;
  max-width: 96vw;
  height: auto !important;
}

.el-dialog.embed-dialog .el-dialog__header {
  display: none !important;
}

.el-dialog.embed-dialog .el-dialog__body {
  padding: 0 !important;
  overflow: visible;
  display: inline-block;
  width: auto;
  min-width: 780px;
  max-width: 1280px;
  background: transparent;
}

/* 修复嵌套弹窗内的下拉框显示问题 */
.el-dialog.embed-dialog .el-dialog__body .el-dialog {
  overflow: visible !important;
  background: #fff !important;
  border-radius: 12px !important;
  box-shadow: 0 4px 20px rgba(0, 0, 0, 0.15) !important;
}
.el-dialog.embed-dialog .el-dialog__body .el-dialog .el-dialog__header {
  display: none !important;
}
.el-dialog.embed-dialog .el-dialog__body .el-dialog .el-dialog__body {
  overflow: visible !important;
  padding: 20px !important;
}
.el-dialog.embed-dialog .el-dialog__body .el-card {
  overflow: visible !important;
}
.el-dialog.embed-dialog .el-dialog__body .el-card__body {
  overflow: visible !important;
}
/* 确保嵌套弹窗的 overlay 允许 overflow */
.el-overlay-dialog .el-overlay {
  overflow: visible !important;
}
/* 确保嵌套弹窗的按钮（footer）可以点击 */
.el-overlay-dialog .el-dialog__footer {
  pointer-events: auto !important;
}
.el-overlay-dialog .el-dialog__footer .el-button {
  pointer-events: auto !important;
}


@media (max-width: 900px) {
  .el-dialog.embed-dialog .el-dialog__body {
    min-width: 320px;
    max-width: 92vw;
  }
}
</style>
