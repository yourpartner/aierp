import api from '../api'

export type AgentTaskKind =
  | 'invoice'
  | 'sales_order'
  | 'payroll'
  | 'alert'
  | 'invoice_validation_warning'
  | 'master_data'

export interface InvoiceAgentTask {
  kind: 'invoice'
  id: string
  sessionId: string
  title?: string
  label?: string
  displayLabel?: string
  fileId: string
  fileName: string
  contentType?: string
  size?: number
  documentSessionId: string
  status: string
  summary?: string
  analysis?: any
  metadata?: any
  payload?: any
  voucherNo?: string
  url?: string
  previewUrl?: string
  createdAt: string
  updatedAt: string
}

export interface SalesOrderAgentTask {
  kind: 'sales_order'
  id: string
  sessionId: string
  displayLabel?: string
  status: string
  summary?: string
  salesOrderId?: string
  salesOrderNo?: string
  customerCode?: string
  customerName?: string
  orderDate?: string
  deliveryDate?: string
  totalAmount?: number
  currency?: string
  lineCount?: number
  metadata?: any
  payload?: any
  createdAt: string
  updatedAt?: string
  completedAt?: string
}

export interface PayrollAgentTask {
  kind: 'payroll'
  id: string
  sessionId: string
  runId: string
  entryId: string
  employeeId: string
  employeeCode?: string
  employeeName?: string
  periodMonth: string
  status: string
  summary?: string
  metadata?: any
  diffSummary?: any
  targetUserId?: string
  createdAt: string
  updatedAt: string
  completedAt?: string
}

export interface AlertAgentTask {
  kind: 'alert' | 'invoice_validation_warning'
  id: string
  sessionId?: string
  title?: string
  label?: string
  displayLabel?: string
  status: string
  summary?: string
  metadata?: any
  payload?: any
  targetUserId?: string
  createdAt: string
  updatedAt: string
  completedAt?: string
}

export interface MasterDataAgentTask {
  kind: 'master_data'
  id: string
  sessionId: string
  title?: string
  displayLabel?: string
  status: string
  summary?: string
  metadata?: any
  payload?: any
  createdAt: string
  updatedAt: string
  completedAt?: string
}

export type AgentTask = InvoiceAgentTask | SalesOrderAgentTask | PayrollAgentTask | AlertAgentTask | MasterDataAgentTask

export interface ListTasksParams {
  limit?: number
  cursor?: string
  status?: 'pending' | 'completed' | ''
}

export interface ListTasksResponse {
  sessionId: string
  tasks: AgentTask[]
  hasMore: boolean
  nextCursor?: string
  counts?: {
    pending: number
    completed: number
  }
}

export function listAgentTasks(sessionId: string, params?: ListTasksParams) {
  const query = new URLSearchParams()
  if (params?.limit) query.set('limit', String(params.limit))
  if (params?.cursor) query.set('cursor', params.cursor)
  if (params?.status) query.set('status', params.status)
  const qs = query.toString()
  return api.get<ListTasksResponse>(`/ai/sessions/${encodeURIComponent(sessionId)}/tasks${qs ? '?' + qs : ''}`)
}

export function listInvoiceTasks(sessionId: string, params?: ListTasksParams) {
  return listAgentTasks(sessionId, params)
}

export function getAgentTask(taskId: string) {
  return api.get(`/ai/tasks/${encodeURIComponent(taskId)}`)
}

export function getInvoiceTask(taskId: string) {
  return getAgentTask(taskId)
}

