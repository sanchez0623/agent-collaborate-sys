// ============================================================
// CrmPanel - CRM 模式可视化面板
// 展示：工具调用卡片链 + 人审状态
// ============================================================

import { Collapse, Tag } from 'antd'
import {
  ToolOutlined, CheckCircleOutlined, LoadingOutlined,
  WarningOutlined, AuditOutlined
} from '@ant-design/icons'
import { AGENTS, OrchestrationEventPayload } from '../types'

// 参数 key 中文映射（简版，用于侧边栏紧凑展示）
const PARAM_CN_BRIEF: Record<string, string> = {
  customerId: '客户ID', name: '名称', company: '公司', phone: '电话',
  email: '邮箱', level: '等级', owner: '负责人', reason: '原因'
}

// 把 JSON 参数格式化为简短行列表（侧边栏用）
function formatParamsBrief(json?: string | null) {
  if (!json) return null
  try {
    const obj = JSON.parse(json)
    return Object.entries(obj).slice(0, 4).map(([k, v]) => (
      <div key={k}>
        <span className="text-gray-500">{PARAM_CN_BRIEF[k] ?? k}：</span>
        <span className="text-gray-300">{String(v)}</span>
      </div>
    ))
  } catch {
    return <div className="text-gray-500 font-mono break-all">{json}</div>
  }
}

export function CrmPanel({ events }: { events: OrchestrationEventPayload[] }) {
  const toolCalls = events.filter(e => e.eventType === 'tool_call' || e.eventType === 'tool_result')
  const approvals = events.filter(e => e.eventType === 'approval_required')
  const agentState = events.find(e => e.eventType === 'agent_started' && e.agent === 'CrmAgent')

  return (
    <div className="space-y-3">
      <div className="text-xs text-gray-500 mb-1">CRM Agent 工具调用链</div>

      {/* Agent 状态 */}
      <div className={`rounded-lg border p-2 transition-all
        ${agentState ? 'border-primary bg-primary/10' : 'border-gray-800'}`}>
        <div className="flex items-center gap-2">
          <span>{AGENTS.CrmAgent.icon}</span>
          <div className="flex-1">
            <div className="text-xs text-white">{AGENTS.CrmAgent.cn}</div>
            <div className="text-[10px] text-gray-500">
              {agentState ? '处理中...' : '待命'}
            </div>
          </div>
          {agentState && <LoadingOutlined spin style={{ color: AGENTS.CrmAgent.color }} />}
        </div>
      </div>

      {/* 工具调用列表 */}
      {toolCalls.length > 0 && (
        <div className="space-y-1.5">
          {toolCalls.map((e, i) => (
            <ToolCallItem key={i} event={e} />
          ))}
        </div>
      )}

      {/* 人审请求 */}
      {approvals.length > 0 && (
        <div className="border-t border-gray-800 pt-2 space-y-1.5">
          <div className="text-xs text-orange-400 flex items-center gap-1">
            <WarningOutlined /> 需人工审核
          </div>
          {approvals.map((a, i) => (
            <div key={i} className="bg-orange-900/20 border border-orange-700/50 rounded p-2">
              <div className="flex items-center gap-1 text-xs">
                <AuditOutlined style={{ color: '#fa8c16' }} />
                <span className="text-orange-300">{a.approvalAction}</span>
              </div>
              {a.approvalParams && (
                <div className="text-[10px] text-gray-400 mt-1 space-y-0.5">
                  {formatParamsBrief(a.approvalParams)}
                </div>
              )}
              <div className="text-[10px] text-gray-500 mt-1">
                风险等级: <Tag color="red">{a.reason}</Tag>
              </div>
            </div>
          ))}
        </div>
      )}

      {toolCalls.length === 0 && approvals.length === 0 && (
        <div className="text-xs text-gray-600 text-center py-4">
          等待 CRM Agent 调用工具
        </div>
      )}
    </div>
  )
}

function ToolCallItem({ event }: { event: OrchestrationEventPayload }) {
  const isCall = event.eventType === 'tool_call'
  const isResult = event.eventType === 'tool_result'

  if (isCall) {
    return (
      <div className="bg-dark-900/60 border border-gray-700 rounded p-2">
        <div className="flex items-center gap-1.5">
          <ToolOutlined style={{ color: '#1890ff', fontSize: 11 }} />
          <span className="text-xs text-blue-400 font-mono">{event.toolName}</span>
          {event.status === 'running' && <LoadingOutlined spin style={{ fontSize: 10, color: '#1890ff' }} />}
        </div>
        {event.toolArgs && (
          <pre className="text-[10px] text-gray-500 mt-1 font-mono whitespace-pre-wrap break-all max-h-20 overflow-auto">
            {event.toolArgs}
          </pre>
        )}
      </div>
    )
  }

  if (isResult) {
    const success = event.status !== 'failure'
    return (
      <Collapse
        size="small"
        className="!bg-dark-900/40 !border-gray-700"
        items={[{
          key: '1',
          label: (
            <div className="flex items-center gap-1.5">
              {success
                ? <CheckCircleOutlined style={{ color: '#52c41a', fontSize: 11 }} />
                : <WarningOutlined style={{ color: '#ff4d4f', fontSize: 11 }} />}
              <span className="text-xs text-gray-400 font-mono">{event.toolName} 结果</span>
            </div>
          ),
          children: (
            <pre className="text-[10px] text-gray-400 font-mono whitespace-pre-wrap break-all">
              {event.toolResult}
            </pre>
          )
        }]}
      />
    )
  }
  return null
}
