import api from '../api'

export interface AgentAccountingRule {
  id: string
  companyCode: string
  title: string
  description?: string
  keywords: string[]
  accountCode?: string
  accountName?: string
  note?: string
  priority: number
  isActive: boolean
  options?: any
  createdAt: string
  updatedAt: string
}

export interface AgentAccountingRulePayload {
  title: string
  description?: string
  keywords?: string[]
  accountCode?: string
  accountName?: string
  note?: string
  priority?: number
  isActive?: boolean
  options?: any
}

export function listAgentAccountingRules(includeInactive = true) {
  const params: Record<string, any> = {}
  if (includeInactive) params.all = 1
  return api.get<AgentAccountingRule[]>('/ai/agent/accounting-rules', { params })
}

export function getAgentAccountingRule(id: string) {
  return api.get<AgentAccountingRule>(`/ai/agent/accounting-rules/${id}`)
}

export function createAgentAccountingRule(payload: AgentAccountingRulePayload) {
  return api.post<AgentAccountingRule>('/ai/agent/accounting-rules', payload)
}

export function updateAgentAccountingRule(id: string, payload: AgentAccountingRulePayload) {
  return api.put<AgentAccountingRule>(`/ai/agent/accounting-rules/${id}`, payload)
}

export function deleteAgentAccountingRule(id: string) {
  return api.delete(`/ai/agent/accounting-rules/${id}`)
}

