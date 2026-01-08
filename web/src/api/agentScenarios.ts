import api from '../api'

export interface AgentScenario {
  scenarioKey: string
  title: string
  description?: string
  instructions?: string
  toolHints: string[]
  priority: number
  isActive: boolean
  updatedAt: string
  metadata?: any
  context?: any
}

export interface AgentScenarioPayload {
  scenarioKey: string
  title: string
  description?: string
  instructions?: string
  toolHints?: string[]
  metadata?: any
  context?: any
  priority?: number
  isActive?: boolean
}

export interface ScenarioTestPayload {
  scenarioKey?: string
  message?: string
  fileName?: string
  contentType?: string
  preview?: string
}

export interface ScenarioTestResult {
  matchedScenarioKeys: string[]
  systemPrompt: string
  contextMessages: Array<{ role: string; content: string }>
}

export interface ScenarioInterpretResult {
  scenarioKey: string
  title: string
  description?: string
  instructions?: string
  toolHints?: string[]
  priority?: number
  isActive?: boolean
  metadata?: any
  context?: any
}

export function listAgentScenarios(includeInactive = true) {
  const params: Record<string, any> = {}
  if (includeInactive) params.all = 1
  return api.get<AgentScenario[]>('/ai/agent-scenarios', { params })
}

export function getAgentScenario(key: string) {
  return api.get<AgentScenario>(`/ai/agent-scenarios/${encodeURIComponent(key)}`)
}

export function createAgentScenario(payload: AgentScenarioPayload) {
  return api.post<AgentScenario>('/ai/agent-scenarios', payload)
}

export function updateAgentScenario(key: string, payload: AgentScenarioPayload) {
  return api.put<AgentScenario>(`/ai/agent-scenarios/${encodeURIComponent(key)}`, payload)
}

export function deleteAgentScenario(key: string) {
  return api.delete(`/ai/agent-scenarios/${encodeURIComponent(key)}`)
}

export function testAgentScenario(payload: ScenarioTestPayload) {
  return api.post<ScenarioTestResult>('/ai/agent-scenarios/test', payload)
}

export function interpretAgentScenario(prompt: string) {
  return api.post<ScenarioInterpretResult>('/ai/agent-scenarios/interpret', { prompt })
}
