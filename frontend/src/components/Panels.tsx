// ============================================================
// 5 种编排模式的可视化面板组件
// 共享同一份 events 状态，根据当前模式渲染不同 UI
// ============================================================

import { Card, Tag, Popover } from 'antd'
import type { PopoverProps } from 'antd'
import {
  LoadingOutlined, CheckCircleOutlined, CloseCircleOutlined,
  ClockCircleOutlined, ArrowRightOutlined
} from '@ant-design/icons'
import { AGENTS, OrchestrationEventPayload } from '../types'
import MarkdownContent from './MarkdownContent'

// 单个事件的简化状态视图
interface AgentState {
  status: string // running / done / rejected / handoff
  output: string
  round: number
  lane?: number | null
}

// 把事件流聚合为按 Agent 名索引的状态表
export function aggregateStates(events: OrchestrationEventPayload[]): Record<string, AgentState> {
  const states: Record<string, AgentState> = {}
  for (const e of events) {
    if (!e.agent) continue
    const prev = states[e.agent]
    states[e.agent] = {
      status: e.status || prev?.status || 'running',
      output: e.output || prev?.output || '',
      round: e.round ?? prev?.round ?? 1,
      lane: e.parallelLane ?? prev?.lane,
    }
  }
  return states
}

// 状态图标
function StatusIcon({ status, color }: { status: string; color: string }) {
  switch (status) {
    case 'running': return <LoadingOutlined style={{ color }} spin />
    case 'done': return <CheckCircleOutlined style={{ color: '#52c41a' }} />
    case 'rejected': return <CloseCircleOutlined style={{ color: '#ff4d4f' }} />
    case 'handoff': return <ArrowRightOutlined style={{ color: '#faad14' }} />
    default: return <ClockCircleOutlined style={{ color: '#555' }} />
  }
}

// Agent 输出浮层：hover 展示完整内容，Markdown 渲染 + 限宽限高可滚动
// 解决长文本撑爆屏幕的问题，同时保留标题/列表/代码块等格式
function OutputPopover({ content, children, placement = 'right' }: {
  content: string
  children: React.ReactNode
  placement?: PopoverProps['placement']
}) {
  return (
    <Popover
      trigger="hover"
      placement={placement}
      overlayStyle={{ maxWidth: 480 }}
      content={
        <div className="w-[440px] max-h-[360px] overflow-y-auto pr-1 text-sm">
          <MarkdownContent content={content} />
        </div>
      }
    >
      {children}
    </Popover>
  )
}

// ============================================================
// SequentialPanel - 顺序流水线步骤条（MVP-1 升级版）
// ============================================================
const SEQUENTIAL_STEPS = ['Researcher', 'Writer', 'Critic']

export function SequentialPanel({ events }: { events: OrchestrationEventPayload[] }) {
  const states = aggregateStates(events)
  return (
    <div className="space-y-2">
      {SEQUENTIAL_STEPS.map((name, idx) => {
        const meta = AGENTS[name]
        const state = states[name] || { status: 'pending', output: '', round: 0 }
        const isRunning = state.status === 'running'
        const isDone = state.status === 'done'
        const isRejected = state.status === 'rejected'
        return (
          <div key={name}>
            <div className={`
              relative rounded-xl border p-3 transition-all duration-300
              ${isRunning ? 'border-primary bg-primary/10 shadow-lg shadow-primary/20' : ''}
              ${isDone ? 'border-green-700/50 bg-green-900/10' : ''}
              ${isRejected ? 'border-red-700/70 bg-red-900/20 animate-shake' : ''}
              ${state.status === 'pending' ? 'border-gray-800 bg-dark-800/40' : ''}
            `}>
              <div className="flex items-center gap-2">
                <span className="text-lg">{meta.icon}</span>
                <div className="flex-1">
                  <div className="text-sm font-medium text-white flex items-center gap-2">
                    {meta.cn}
                    {state.round > 1 && <Tag color="orange" className="!text-xs">第{state.round}轮</Tag>}
                  </div>
                  <div className="text-xs text-gray-500">{meta.desc}</div>
                </div>
                <StatusIcon status={state.status} color={meta.color} />
              </div>
              {(isDone || isRejected) && state.output && (
                <OutputPopover content={state.output} placement="right">
                  <div className="mt-2 text-xs text-gray-400 bg-dark-900/60 rounded p-2 max-h-20 overflow-hidden whitespace-pre-wrap cursor-pointer hover:bg-dark-900 transition-colors">
                    {state.output.slice(0, 120)}{state.output.length > 120 ? '...' : ''}
                  </div>
                </OutputPopover>
              )}
              {isRunning && (
                <div className="absolute bottom-0 left-0 right-0 h-0.5 bg-primary/30 rounded-b-xl overflow-hidden">
                  <div className="h-full bg-primary animate-pulse-x" />
                </div>
              )}
            </div>
            {idx < SEQUENTIAL_STEPS.length - 1 && (
              <div className="flex justify-center py-1">
                <div className={`w-0.5 h-4 ${isDone ? 'bg-green-700' : 'bg-gray-800'}`} />
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}

// ============================================================
// ConcurrentPanel - 并行泳道图
// ============================================================
const CONCURRENT_LANES = ['Analyst', 'Coder', 'Consultant', 'Researcher']

export function ConcurrentPanel({ events }: { events: OrchestrationEventPayload[] }) {
  const states = aggregateStates(events)
  const synthState = states['Coordinator']
  return (
    <div className="space-y-3">
      <div className="text-xs text-gray-500 mb-1">并行专家（同时执行）</div>
      <div className="grid grid-cols-2 gap-2">
        {CONCURRENT_LANES.map(name => {
          const meta = AGENTS[name]
          const state = states[name] || { status: 'pending', output: '', round: 0 }
          const isRunning = state.status === 'running'
          const isDone = state.status === 'done'
          return (
            <div key={name} className={`
              rounded-lg border p-2 transition-all
              ${isRunning ? 'border-primary bg-primary/10' : ''}
              ${isDone ? 'border-green-700/50 bg-green-900/10' : ''}
              ${state.status === 'pending' ? 'border-gray-800' : ''}
            `}>
              <div className="flex items-center gap-1 mb-1">
                <span>{meta.icon}</span>
                <span className="text-xs text-white truncate">{meta.cn}</span>
                <span className="ml-auto"><StatusIcon status={state.status} color={meta.color} /></span>
              </div>
              {isDone && state.output && (
                <OutputPopover content={state.output} placement="top">
                  <div className="text-[10px] text-gray-500 truncate cursor-pointer hover:text-gray-300 transition-colors">{state.output.slice(0, 40)}...</div>
                </OutputPopover>
              )}
              {isRunning && (
                <div className="h-0.5 bg-primary/30 rounded overflow-hidden mt-1">
                  <div className="h-full bg-primary animate-pulse-x" />
                </div>
              )}
            </div>
          )
        })}
      </div>
      {/* 汇总阶段 */}
      <div className="border-t border-gray-800 pt-3">
        <div className="text-xs text-gray-500 mb-1">汇总整合</div>
        <div className={`
          rounded-lg border p-2 transition-all
          ${synthState?.status === 'running' ? 'border-primary bg-primary/10' : ''}
          ${synthState?.status === 'done' ? 'border-green-700/50 bg-green-900/10' : ''}
          ${!synthState ? 'border-gray-800' : ''}
        `}>
          <div className="flex items-center gap-1">
            <span>{AGENTS.Coordinator.icon}</span>
            <span className="text-xs text-white">{AGENTS.Coordinator.cn}</span>
            <span className="ml-auto">
              {synthState ? <StatusIcon status={synthState.status} color={AGENTS.Coordinator.color} /> : <ClockCircleOutlined style={{ color: '#555' }} />}
            </span>
          </div>
        </div>
      </div>
    </div>
  )
}

// ============================================================
// HandoffPanel - 移交链路图（A → B → C）
// ============================================================
export function HandoffPanel({ events }: { events: OrchestrationEventPayload[] }) {
  const states = aggregateStates(events)
  // 从 handoff 事件提取移交链路
  const handoffs = events.filter(e => e.eventType === 'handoff')
  // 链路顺序：初始 Support + 每次 handoff 的 toAgent
  const chain = ['Support', ...handoffs.map(h => h.toAgent).filter(Boolean) as string[]]
  return (
    <div className="space-y-2">
      <div className="text-xs text-gray-500 mb-2">移交链路</div>
      {chain.map((name, idx) => {
        const meta = AGENTS[name] || AGENTS.Support
        const state = states[name] || { status: 'pending', output: '', round: 0 }
        return (
          <div key={`${name}-${idx}`}>
            <div className={`
              rounded-lg border p-2 transition-all
              ${state.status === 'running' ? 'border-primary bg-primary/10' : ''}
              ${state.status === 'done' ? 'border-green-700/50 bg-green-900/10' : ''}
              ${state.status === 'handoff' ? 'border-orange-700/70 bg-orange-900/10' : ''}
              ${state.status === 'pending' ? 'border-gray-800' : ''}
            `}>
              <div className="flex items-center gap-2">
                <span>{meta.icon}</span>
                <div className="flex-1">
                  <div className="text-xs text-white">{meta.cn}</div>
                  <div className="text-[10px] text-gray-500">
                    {state.status === 'handoff' ? '已移交' : state.status === 'done' ? '已回答' : state.status === 'running' ? '处理中' : '等待'}
                  </div>
                </div>
                <StatusIcon status={state.status} color={meta.color} />
              </div>
              {state.status === 'done' && state.output && (
                <OutputPopover content={state.output} placement="right">
                  <div className="text-[10px] text-gray-500 truncate mt-1 cursor-pointer hover:text-gray-300 transition-colors">{state.output.slice(0, 50)}...</div>
                </OutputPopover>
              )}
            </div>
            {idx < chain.length - 1 && (
              <div className="flex justify-center py-1">
                <ArrowRightOutlined style={{ color: '#faad14', fontSize: 12 }} />
              </div>
            )}
          </div>
        )
      })}
      {/* 移交原因列表 */}
      {handoffs.length > 0 && (
        <div className="mt-3 pt-2 border-t border-gray-800">
          <div className="text-xs text-gray-500 mb-1">移交原因</div>
          {handoffs.map((h, i) => (
            <div key={i} className="text-[10px] text-gray-400 bg-dark-900/60 rounded p-2 mb-1">
              <span className="text-orange-400">{h.fromAgent} → {h.toAgent}</span>
              {h.reason && <div className="mt-1 text-gray-500">{h.reason.slice(0, 80)}</div>}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

// ============================================================
// GroupChatPanel - 群聊界面（Agent 轮流发言气泡）
// ============================================================
export function GroupChatPanel({ events }: { events: OrchestrationEventPayload[] }) {
  const states = aggregateStates(events)
  // 发言记录 = 所有 agent_completed 事件
  const speeches = events.filter(e => e.eventType === 'agent_completed' && e.agent !== 'Coordinator' && e.output)
  const coordinatorRunning = states['Coordinator']?.status === 'running'
  return (
    <div className="space-y-2">
      <div className="text-xs text-gray-500 mb-2">群聊讨论</div>
      {/* 主持人状态 */}
      <div className={`text-xs flex items-center gap-1 mb-2 ${coordinatorRunning ? 'text-primary' : 'text-gray-600'}`}>
        <span>{AGENTS.Coordinator.icon}</span>
        {coordinatorRunning ? '主持人选择下一个发言者...' : '主持人待命'}
      </div>
      {/* 发言气泡列表 */}
      {speeches.length === 0 ? (
        <div className="text-xs text-gray-600 text-center py-4">等待主持人开始讨论</div>
      ) : (
        <div className="space-y-2 max-h-80 overflow-y-auto">
          {speeches.map((s, i) => {
            const meta = AGENTS[s.agent] || AGENTS.Support
            return (
              <div key={i} className="flex gap-2">
                <div className="text-lg flex-shrink-0">{meta.icon}</div>
                <div className="flex-1 min-w-0">
                  <div className="text-[10px] mb-0.5" style={{ color: meta.color }}>{meta.cn}</div>
                  <div className="text-xs text-gray-300 bg-dark-800/80 rounded-lg p-2 whitespace-pre-wrap">
                    {s.output.slice(0, 200)}{s.output.length > 200 ? '...' : ''}
                  </div>
                </div>
              </div>
            )
          })}
        </div>
      )}
    </div>
  )
}

// ============================================================
// MagenticPanel - 路由决策树动画
// ============================================================
export function MagenticPanel({ events }: { events: OrchestrationEventPayload[] }) {
  const states = aggregateStates(events)
  const routeDecision = events.find(e => e.eventType === 'route_decision')
  const routerState = states['Coordinator']
  const targetAgent = routeDecision?.agent
  const targetState = targetAgent ? states[targetAgent] : undefined

  return (
    <div className="space-y-3">
      <div className="text-xs text-gray-500 mb-2">路由决策</div>
      {/* Router 决策阶段 */}
      <div className={`rounded-lg border p-2 transition-all
        ${routerState?.status === 'running' ? 'border-primary bg-primary/10' : ''}
        ${routerState?.status === 'done' ? 'border-green-700/50 bg-green-900/10' : ''}
      `}>
        <div className="flex items-center gap-2">
          <span>{AGENTS.Coordinator.icon}</span>
          <div className="flex-1">
            <div className="text-xs text-white">协调员（Router）</div>
            <div className="text-[10px] text-gray-500">
              {routerState?.status === 'running' ? '分析意图中...' : routerState?.status === 'done' ? '已决策' : '待命'}
            </div>
          </div>
          {routerState && <StatusIcon status={routerState.status} color={AGENTS.Coordinator.color} />}
        </div>
      </div>

      {/* 决策结果 */}
      {routeDecision && (
        <>
          <div className="flex justify-center">
            <ArrowRightOutlined style={{ color: '#faad14' }} />
          </div>
          <div className="bg-primary/10 border border-primary/30 rounded-lg p-2">
            <div className="text-[10px] text-primary mb-1">系统判断：此问题最适合由</div>
            <div className="flex items-center gap-2">
              <span className="text-xl">{AGENTS[targetAgent || 'Writer']?.icon}</span>
              <span className="text-sm font-medium text-white">
                {AGENTS[targetAgent || 'Writer']?.cn}
              </span>
            </div>
            {routeDecision.reason && (
              <div className="text-[10px] text-gray-400 mt-1">{routeDecision.reason.slice(0, 100)}</div>
            )}
          </div>
        </>
      )}

      {/* 目标 Agent 执行 */}
      {targetAgent && targetState && (
        <>
          <div className="flex justify-center">
            <ArrowRightOutlined style={{ color: '#faad14' }} />
          </div>
          <div className={`rounded-lg border p-2 transition-all
            ${targetState.status === 'running' ? 'border-primary bg-primary/10' : ''}
            ${targetState.status === 'done' ? 'border-green-700/50 bg-green-900/10' : ''}
          `}>
            <div className="flex items-center gap-2">
              <span>{AGENTS[targetAgent]?.icon}</span>
              <div className="flex-1">
                <div className="text-xs text-white">{AGENTS[targetAgent]?.cn}</div>
                <div className="text-[10px] text-gray-500">
                  {targetState.status === 'running' ? '执行中...' : '已完成'}
                </div>
              </div>
              <StatusIcon status={targetState.status} color={AGENTS[targetAgent]?.color} />
            </div>
            {targetState.status === 'done' && targetState.output && (
              <OutputPopover content={targetState.output} placement="right">
                <div className="text-[10px] text-gray-500 truncate mt-1 cursor-pointer hover:text-gray-300 transition-colors">
                  {targetState.output.slice(0, 50)}...
                </div>
              </OutputPopover>
            )}
          </div>
        </>
      )}
    </div>
  )
}
