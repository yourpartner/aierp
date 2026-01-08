import api from '../api'

export interface WorkflowRulePayload {
  ruleKey: string
  title: string
  description: string
  instructions: string
  actions: any[]
  priority?: number
  isActive?: boolean
}

export function listWorkflowRules() {
  return api.get('/ai/workflow-rules')
}

export function getWorkflowRule(ruleKey: string) {
  return api.get(`/ai/workflow-rules/${encodeURIComponent(ruleKey)}`)
}

export function interpretWorkflowRule(prompt: string) {
  return api.post('/ai/workflow-rules/interpret', { prompt })
}

export function createWorkflowRule(payload: WorkflowRulePayload) {
  return api.post('/ai/workflow-rules', payload)
}

export function updateWorkflowRule(ruleKey: string, payload: WorkflowRulePayload) {
  return api.put(`/ai/workflow-rules/${encodeURIComponent(ruleKey)}`, payload)
}

export function deleteWorkflowRule(ruleKey: string) {
  return api.delete(`/ai/workflow-rules/${encodeURIComponent(ruleKey)}`)
}

export function testWorkflowRule(ruleKey: string, payload: any) {
  return api.post('/ai/workflows/test', { ruleKey, payload })
}
