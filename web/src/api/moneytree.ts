import api from '../api'

export interface MoneytreeTransactionQuery {
  startDate?: string
  endDate?: string
  type?: 'all' | 'deposit' | 'withdrawal'
  status?: string
  keyword?: string
  page?: number
  pageSize?: number
}

export interface MoneytreeTransactionResponse {
  page: number
  pageSize: number
  total: number
  items: MoneytreeTransactionItem[]
}

export interface MoneytreeTransactionItem {
  id: string
  transactionDate: string | null
  depositAmount: number | null
  withdrawalAmount: number | null
  balance: number | null
  currency: string | null
  bankName: string | null
  description: string | null
  accountName: string | null
  accountNumber: string | null
  postingStatus: string | null
  postingError: string | null
  voucherNo: string | null
  ruleTitle: string | null
  importedAt: string | null
}

export interface MoneytreeSimulationResult {
  transactionId: string
  status: string
  message: string
  ruleTitle?: string
  voucher?: any
  debitAccount?: string
  creditAccount?: string
  wouldClearOpenItem: boolean
  debitAccountName?: string | null
  creditAccountName?: string | null
}

export interface MoneytreeRule {
  id: string
  title: string
  description?: string | null
  priority: number
  matcher: Record<string, unknown>
  action: Record<string, unknown>
  isActive: boolean
  updatedAt: string
  createdAt: string
}

export interface MoneytreeRulePayload {
  title?: string
  description?: string | null
  priority?: number
  matcher?: Record<string, unknown>
  action?: Record<string, unknown>
  isActive?: boolean
}

export interface RunMoneytreePostingOptions {
  batchSize?: number
  ids?: string[]
}

export function fetchMoneytreeTransactions(params: MoneytreeTransactionQuery) {
  return api
    .get<MoneytreeTransactionResponse>('/integrations/moneytree/transactions', { params })
    .then((res) => res.data)
}

// For "moneytree_posting" approval task: fetch only the transactions processed in that run (taskId -> approval_tasks.object_id).
export function fetchMoneytreePostingTaskTransactions(taskId: string, params: MoneytreeTransactionQuery) {
  return api
    .get<MoneytreeTransactionResponse>(`/integrations/moneytree/posting/tasks/${encodeURIComponent(taskId)}/transactions`, { params })
    .then((res) => res.data)
}

export function simulateMoneytreePosting(ids: string[]) {
  return api
    .post<{ count: number; items: MoneytreeSimulationResult[] }>(
      '/integrations/moneytree/posting/simulate',
      { ids }
    )
    .then((res) => res.data)
}

export function runMoneytreePosting(options?: RunMoneytreePostingOptions) {
  const payload = options?.ids && options.ids.length ? { ids: options.ids } : undefined
  const config = options?.batchSize
    ? {
        params: {
          batchSize: options.batchSize
        }
      }
    : undefined

  return api.post('/integrations/moneytree/posting/run', payload, config).then((res) => res.data)
}

export function fetchMoneytreeRules(includeInactive = false) {
  return api
    .get<MoneytreeRule[]>('/integrations/moneytree/rules', {
      params: includeInactive ? { includeInactive: true } : undefined
    })
    .then((res) => res.data)
}

export function createMoneytreeRule(payload: Required<MoneytreeRulePayload>) {
  return api.post('/integrations/moneytree/rules', payload).then((res) => res.data)
}

export function updateMoneytreeRule(id: string, payload: MoneytreeRulePayload) {
  return api.put(`/integrations/moneytree/rules/${id}`, payload).then((res) => res.data)
}

export function deleteMoneytreeRule(id: string) {
  return api.delete(`/integrations/moneytree/rules/${id}`).then((res) => res.data)
}

export interface MoneytreeImportRequest {
  startDate: string
  endDate: string
  otpSecret?: string | null
  /** 导入模式：normal（正常模式，自动记账）或 history（历史导入模式，只匹配既存凭证） */
  importMode?: 'normal' | 'history'
}

export interface MoneytreeImportResponse {
  batchId: string
  totalRows: number
  insertedRows: number
  skippedRows: number
  linkedRows: number
  importMode: string
}

export function importMoneytreeTransactions(payload: MoneytreeImportRequest) {
  return api.post<MoneytreeImportResponse>('/integrations/moneytree/import', payload).then((res) => res.data)
}

