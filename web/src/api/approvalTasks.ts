import api from '../api'

export interface ApprovalTask {
  id: string
  entity: string
  objectId: string
  stepName?: string
  status: string
  createdAt: string
  updatedAt?: string
  applicantName?: string
  summary?: string
  payload?: any
}

export function listApprovalTasks(status: 'pending' | 'approved' | 'rejected' | 'all' = 'pending', pageSize = 50) {
  const where: any[] = []
  if (status !== 'all') where.push({ field: 'status', op: 'eq', value: status })
  return api.post('/objects/approval_task/search', {
    page: 1,
    pageSize,
    where,
    orderBy: [{ field: 'created_at', dir: 'DESC' }]
  })
}

export function actApprovalTask(entity: string, objectId: string, action: 'approve' | 'reject') {
  return api.post('/operations/approvals/next', { entity, objectId, action })
}

// Moneytree auto-posting confirmation task: complete (mark approved) by task id.
export function completeMoneytreePostingTask(taskId: string) {
  return api.post(`/integrations/moneytree/posting/tasks/${encodeURIComponent(taskId)}/complete`)
}

