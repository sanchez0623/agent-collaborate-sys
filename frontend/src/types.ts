// ============================================================
// 共享类型定义 - 前后端 SSE 协议契约
// ============================================================

// 7 种编排模式（MVP-3 新增 rag / MVP-4 新增 crm）
export type OrchestrationMode =
  | 'sequential'
  | 'concurrent'
  | 'handoff'
  | 'groupchat'
  | 'magentic'
  | 'crm'
  | 'rag'

// 编排事件子类型（对应后端 OrchestrationEventType）
// MVP-4 新增 tool_call / tool_result / approval_required / approval_result
export type EventType =
  | 'agent_started'
  | 'agent_delta'
  | 'agent_completed'
  | 'handoff'
  | 'group_turn'
  | 'route_decision'
  | 'orchestration_done'
  | 'tool_call'
  | 'tool_result'
  | 'approval_required'
  | 'approval_result'

// 单个编排事件（对应后端 OrchestrationEvent）
export interface OrchestrationEventPayload {
  type: 'orchestration_event'
  eventType: EventType
  agent: string
  status: string // running / done / rejected / handoff
  output: string
  round: number
  fromAgent?: string | null
  toAgent?: string | null
  reason?: string | null
  parallelLane?: number | null
  // MVP-4 工具调用
  toolName?: string | null
  toolArgs?: string | null
  toolResult?: string | null
  // MVP-4 人审
  approvalId?: number | null
  approvalAction?: string | null
  approvalParams?: string | null
  approvalDecision?: string | null
}

// 所有 SSE 顶层事件
export type SseEvent =
  | { type: 'session'; sessionId: string }
  | { type: 'orchestration_start'; mode: OrchestrationMode }
  | OrchestrationEventPayload
  | { type: 'token'; content: string }
  | { type: 'done'; content: string }
  | { type: 'error'; message: string }

// 聊天消息
export interface Message {
  id: string
  role: 'user' | 'assistant' | 'system'
  content: string
  agentName?: string
  timestamp: Date
  streaming?: boolean
}

// Agent 元信息（用于面板渲染：图标/颜色/中文名）
export interface AgentMeta {
  name: string
  cn: string
  color: string
  icon: string
  desc: string
}

// 10 个 Agent 的元信息表（MVP-4 新增 CrmAgent/Approver）
export const AGENTS: Record<string, AgentMeta> = {
  Researcher: { name: 'Researcher', cn: '研究员', color: '#1890ff', icon: '🔍', desc: '收集信息、调研要点' },
  Writer: { name: 'Writer', cn: '写作者', color: '#52c41a', icon: '✍️', desc: '撰写结构化回答' },
  Critic: { name: 'Critic', cn: '审核员', color: '#fa8c16', icon: '🔴', desc: '审核质量，通过或退回' },
  Analyst: { name: 'Analyst', cn: '数据分析师', color: '#13c2c2', icon: '📊', desc: '量化分析与指标解读' },
  Coder: { name: 'Coder', cn: '程序员', color: '#722ed1', icon: '💻', desc: '代码生成与技术解答' },
  Consultant: { name: 'Consultant', cn: '产品顾问', color: '#eb2f96', icon: '💡', desc: '方案建议与需求拆解' },
  Support: { name: 'Support', cn: '技术支持', color: '#faad14', icon: '🔧', desc: '故障排查与问题分流' },
  Coordinator: { name: 'Coordinator', cn: '协调员', color: '#f5222d', icon: '🎯', desc: '群聊主持/路由决策/汇总' },
  CrmAgent: { name: 'CrmAgent', cn: 'CRM业务助手', color: '#2f54eb', icon: '🧑‍💼', desc: '客户查询/新建/跟进/删除(人审)' },
  Approver: { name: 'Approver', cn: '风险审核员', color: '#a8071a', icon: '👨‍⚖️', desc: '判断敏感操作是否需人审' },
}

// 模式元信息表（MVP-3 新增 rag / MVP-4 新增 crm 模式）
export const MODES: { key: OrchestrationMode; cn: string; desc: string }[] = [
  { key: 'sequential', cn: '顺序流水线', desc: '研究→写作→审核 串联' },
  { key: 'concurrent', cn: '并行处理', desc: '多专家同时跑+汇总' },
  { key: 'handoff', cn: '智能移交', desc: '客服→技术→专家 链式转接' },
  { key: 'groupchat', cn: '群聊讨论', desc: '主持人调度多Agent轮流发言' },
  { key: 'magentic', cn: '自动路由', desc: '根据意图自动选最合适Agent' },
  { key: 'crm', cn: 'CRM业务', desc: 'CrmAgent调工具+人审敏感操作' },
  { key: 'rag', cn: 'RAG知识库', desc: '检索知识库后生成回答' },
]
