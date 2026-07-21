// ============================================================
// ChatPage - 对话页（从 App.tsx 改造为路由页面）
// 升级：7 模式（MVP-3 新增 RAG / MVP-4 新增 CRM）+ 工具调用可视化 + 人审弹窗 + JWT header
// ============================================================

import { useState, useRef, useEffect } from 'react'
import { Input, Button, Card, Avatar, Tag, Space, Spin, Select, Empty, message } from 'antd'
import {
  SendOutlined, RobotOutlined, UserOutlined, StopOutlined, PlusOutlined,
  BookOutlined,
} from '@ant-design/icons'
import { OrchestrationMode, Message, OrchestrationEventPayload, AGENTS, MODES } from '../types'
import {
  SequentialPanel, ConcurrentPanel, HandoffPanel,
  GroupChatPanel, MagenticPanel
} from '../components/Panels'
import { CrmPanel } from '../components/CrmPanel'
import { ApprovalModal } from '../components/ApprovalModal'
import MarkdownContent from '../components/MarkdownContent'
import { getToken } from '../auth'
import { kbApi, KbDatabase } from '../api'

export default function ChatPage() {
  const [messages, setMessages] = useState<Message[]>([
    {
      id: 'welcome',
      role: 'assistant',
      content: '你好！我是 7 模式编排系统（含 RAG 知识库与 CRM 业务模式）。顶部切换模式，试试问我点什么。RAG 模式会先检索所选知识库再生成回答；CRM 模式可查客户、建客户、删客户（删客户会触发人审）。',
      agentName: 'System',
      timestamp: new Date()
    }
  ])
  const [input, setInput] = useState('')
  const [loading, setLoading] = useState(false)
  const [mode, setMode] = useState<OrchestrationMode>('sequential')
  const [activeMode, setActiveMode] = useState<OrchestrationMode | null>(null)
  // 按模式隔离 session：每种编排模式维护独立的对话历史
  const sessionKey = (m: OrchestrationMode) => `session_${m}`
  const [sessionId, setSessionId] = useState<string>(() =>
    localStorage.getItem(sessionKey('sequential')) || crypto.randomUUID()
  )
  const [events, setEvents] = useState<OrchestrationEventPayload[]>([])
  // 人审：当前待决策的 approval_required 事件
  const [pendingApproval, setPendingApproval] = useState<OrchestrationEventPayload | null>(null)

  // RAG 模式：知识库列表与当前选中的知识库 ID
  const [knowledgeBases, setKnowledgeBases] = useState<KbDatabase[]>([])
  const [knowledgeBaseId, setKnowledgeBaseId] = useState<number | null>(null)

  const messagesEndRef = useRef<HTMLDivElement>(null)
  const mainRef = useRef<HTMLElement>(null)
  const abortRef = useRef<AbortController | null>(null)

  // 页面加载时从后端恢复历史消息（当前模式）
  const [historyLoaded, setHistoryLoaded] = useState(false)
  const loadHistory = async (sid: string) => {
    try {
      const token = getToken()
      const r = await fetch(`/api/chat/sessions/${sid}/history`, {
        headers: token ? { 'Authorization': `Bearer ${token}` } : {}
      })
      if (!r.ok) return
      const data: { role: string; content: string }[] = await r.json()
      if (data.length > 0) {
        const welcome = {
          id: 'welcome', role: 'assistant' as const,
          content: '你好！我是 7 模式编排系统（含 RAG 知识库与 CRM 业务模式）。顶部切换模式，试试问我点什么。RAG 模式会先检索所选知识库再生成回答；CRM 模式可查客户、建客户、删客户（删客户会触发人审）。',
          agentName: 'System', timestamp: new Date()
        }
        const history: Message[] = data.map((h, i) => ({
          id: `hist-${i}`, role: h.role as 'user' | 'assistant',
          content: h.content, agentName: undefined, timestamp: new Date(),
        }))
        setMessages([welcome, ...history])
      }
    } catch { /* 历史加载失败静默忽略 */ }
  }

  // 挂载时恢复当前模式的历史（仅执行一次）
  useEffect(() => { loadHistory(sessionId); setHistoryLoaded(true) }, [])

  // 切换编排模式：同时切换对应的独立会话
  const switchMode = (newMode: OrchestrationMode) => {
    if (loading || newMode === mode) return
    setMode(newMode)
    setEvents([])
    setPendingApproval(null)
    const stored = localStorage.getItem(sessionKey(newMode))
    const newSid = stored || crypto.randomUUID()
    setSessionId(newSid)
    setMessages([{
      id: 'welcome', role: 'assistant',
      content: '你好！我是 7 模式编排系统（含 RAG 知识库与 CRM 业务模式）。顶部切换模式，试试问我点什么。RAG 模式会先检索所选知识库再生成回答；CRM 模式可查客户、建客户、删客户（删客户会触发人审）。',
      agentName: 'System', timestamp: new Date()
    }])
    loadHistory(newSid)
  }

  // 自动滚底：scrollTop 瞬间到位 + requestAnimationFrame 节流，避免 smooth 动画排队
  useEffect(() => {
    const el = mainRef.current
    if (!el) return
    requestAnimationFrame(() => { el.scrollTop = el.scrollHeight })
  }, [messages])
  useEffect(() => { localStorage.setItem(sessionKey(mode), sessionId) }, [sessionId, mode])

  // 加载知识库列表（用于 RAG 模式选择器）
  useEffect(() => {
    kbApi.listDatabases()
      .then(list => {
        const arr = Array.isArray(list) ? list : []
        setKnowledgeBases(arr)
        if (arr.length > 0 && knowledgeBaseId == null) setKnowledgeBaseId(arr[0].id)
      })
      .catch(() => { /* 静默忽略，RAG 模式可选时再提示 */ })
  }, [])

  const sendMessage = async () => {
    const text = input.trim()
    if (!text || loading) return

    // RAG 模式必须选择知识库
    if (mode === 'rag' && !knowledgeBaseId) {
      message.warning('RAG 模式请先选择知识库')
      return
    }

    setInput('')
    setLoading(true)
    setActiveMode(mode)
    setEvents([])
    setPendingApproval(null)

    const userMsg: Message = {
      id: crypto.randomUUID(), role: 'user', content: text, timestamp: new Date()
    }
    setMessages(prev => [...prev, userMsg])

    const assistantMsgId = crypto.randomUUID()
    setMessages(prev => [...prev, {
      id: assistantMsgId, role: 'assistant', content: '', agentName: mode,
      timestamp: new Date(), streaming: true
    }])

    try {
      abortRef.current = new AbortController()
      const token = getToken()
      const response = await fetch('/api/chat', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(token ? { 'Authorization': `Bearer ${token}` } : {})
        },
        body: JSON.stringify({
          sessionId,
          message: text,
          orchestrationMode: mode,
          // RAG 模式携带知识库 ID
          ...(mode === 'rag' && knowledgeBaseId ? { knowledgeBaseId } : {}),
        }),
        signal: abortRef.current.signal
      })
      if (!response.ok) throw new Error(`请求失败: ${response.status}`)

      const reader = response.body!.getReader()
      const decoder = new TextDecoder()
      let buffer = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        const lines = buffer.split('\n')
        buffer = lines.pop() || ''
        let currentData = ''
        for (const line of lines) {
          if (line.startsWith('data: ')) {
            currentData = line.slice(6)
          } else if (line === '') {
            if (currentData) {
              try { handleSseEvent(JSON.parse(currentData), assistantMsgId) } catch { /* ignore */ }
            }
            currentData = ''
          }
        }
      }
    } catch (err: any) {
      if (err.name !== 'AbortError') {
        setMessages(prev => prev.map(m =>
          m.id === assistantMsgId
            ? { ...m, content: `⚠️ 请求出错: ${err.message}`, streaming: false }
            : m
        ))
      }
    } finally {
      setLoading(false)
      setActiveMode(null)
      setMessages(prev => prev.map(m => m.id === assistantMsgId ? { ...m, streaming: false } : m))
    }
  }

  const handleSseEvent = (event: any, assistantMsgId: string) => {
    if (event.type === 'session' && event.sessionId) {
      setSessionId(event.sessionId)
    } else if (event.type === 'orchestration_event') {
      const payload = event as OrchestrationEventPayload
      setEvents(prev => [...prev, payload])
      // 收到人审请求：弹出审核窗
      if (payload.eventType === 'approval_required' && payload.approvalId) {
        setPendingApproval(payload)
      }
    } else if (event.type === 'token' && event.content) {
      setMessages(prev => prev.map(m =>
        m.id === assistantMsgId ? { ...m, content: m.content + event.content } : m
      ))
    } else if (event.type === 'done') {
      setMessages(prev => prev.map(m =>
        m.id === assistantMsgId
          ? { ...m, streaming: false, content: event.content || m.content }
          : m
      ))
    } else if (event.type === 'error') {
      setMessages(prev => prev.map(m =>
        m.id === assistantMsgId
          ? { ...m, content: `⚠️ ${event.message || event.content}`, streaming: false }
          : m
      ))
    }
  }

  // 新对话：仅清空当前模式的会话
  const newChat = () => {
    const newId = crypto.randomUUID()
    setSessionId(newId)
    localStorage.removeItem(sessionKey(mode))
    setMessages([{
      id: 'welcome', role: 'assistant',
      content: '新对话开始了。选择编排模式后开始提问。',
      agentName: 'System', timestamp: new Date()
    }])
    setEvents([])
    setPendingApproval(null)
  }

  // 停止生成：中止 fetch（触发后端 ct 取消）+ 关闭人审弹窗 + 标记消息已停
  const stopGeneration = () => {
    abortRef.current?.abort()
    abortRef.current = null
    setLoading(false)
    setActiveMode(null)
    setPendingApproval(null)
    setMessages(prev => prev.map(m =>
      m.streaming ? { ...m, streaming: false, content: m.content + (m.content ? '\n\n_⏹ 已停止生成_' : '_⏹ 已停止生成_') } : m
    ))
  }

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendMessage() }
  }

  const renderPanel = () => {
    const m = activeMode || mode
    switch (m) {
      case 'sequential': return <SequentialPanel events={events} />
      case 'concurrent': return <ConcurrentPanel events={events} />
      case 'handoff': return <HandoffPanel events={events} />
      case 'groupchat': return <GroupChatPanel events={events} />
      case 'magentic': return <MagenticPanel events={events} />
      case 'crm': return <CrmPanel events={events} />
      case 'rag':
        return (
          <div className="space-y-3">
            <div className="text-xs text-gray-400 leading-relaxed">
              RAG 模式：发送问题时会先从所选知识库召回相关分片，再基于召回内容生成回答。
            </div>
            <div className="rounded-lg border border-gray-700 bg-dark-700/60 p-3">
              <div className="text-xs text-gray-500 mb-2">当前知识库</div>
              {knowledgeBases.length === 0 ? (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description={<span className="text-gray-500 text-xs">暂无知识库，请先到「知识库」页创建</span>}
                />
              ) : (
                <Tag color="purple" className="!text-sm">
                  {knowledgeBases.find(d => d.id === knowledgeBaseId)?.name || '未选择'}
                </Tag>
              )}
            </div>
          </div>
        )
    }
  }

  return (
    <div className="h-full flex flex-col">
      {/* 模式选择 Tab 条 + RAG 知识库选择器 */}
      <div className="flex items-center border-b border-gray-800 bg-dark-800/40 px-6 overflow-x-auto">
        <div className="flex">
          {MODES.map(m => (
            <button
              key={m.key}
              onClick={() => switchMode(m.key)}
              disabled={loading}
              className={`px-4 py-2.5 text-sm whitespace-nowrap border-b-2 transition-all
                ${mode === m.key
                  ? 'border-primary text-primary font-medium'
                  : 'border-transparent text-gray-400 hover:text-gray-200'}
                ${loading && mode !== m.key ? 'opacity-50 cursor-not-allowed' : ''}
              `}
            >
              <div className="flex flex-col items-center">
                <span>{m.cn}</span>
                <span className="text-[9px] text-gray-500 font-normal">{m.desc}</span>
              </div>
            </button>
          ))}
        </div>
        {/* RAG 模式专属：知识库选择下拉 */}
        {mode === 'rag' && (
          <div className="ml-auto flex items-center gap-2 py-2">
            <BookOutlined className="text-primary" />
            <Select
              size="small"
              value={knowledgeBaseId ?? undefined}
              onChange={setKnowledgeBaseId}
              placeholder="选择知识库"
              className="w-56"
              disabled={loading}
              options={knowledgeBases.map(d => ({
                value: d.id,
                label: `${d.name} (${d.documentCount} 文档)`,
              }))}
              notFoundContent="暂无知识库"
            />
          </div>
        )}
      </div>

      <div className="flex-1 flex overflow-hidden">
        {/* 左侧可视化面板 */}
        <aside className="w-72 border-r border-gray-800 bg-dark-800/60 backdrop-blur p-4 overflow-y-auto">
          <div className="mb-3 flex items-center justify-between">
            <h2 className="text-sm font-semibold text-white flex items-center gap-2">
              <RobotOutlined className="text-primary" />
              {MODES.find(m => m.key === (activeMode || mode))?.cn}
            </h2>
            <Tag color="blue">{sessionId.slice(0, 8)}</Tag>
          </div>
          <p className="text-xs text-gray-500 mb-3">
            {loading ? '执行中...' : '空闲'}
            {pendingApproval && <span className="text-orange-400 ml-2">· 待人审</span>}
          </p>
          {renderPanel()}
        </aside>

        {/* 右侧聊天区 */}
        <div className="flex-1 flex flex-col">
          <main ref={mainRef} className="flex-1 overflow-y-auto px-4 py-6">
            <div className="max-w-3xl mx-auto space-y-4">
              {messages.map(msg => (
                <div key={msg.id} className={`message-enter flex gap-3 ${msg.role === 'user' ? 'flex-row-reverse' : ''}`}>
                  <Avatar
                    icon={msg.role === 'user' ? <UserOutlined /> : <RobotOutlined />}
                    className={msg.role === 'user' ? 'bg-blue-600' : 'bg-primary'}
                    size={36}
                  />
                  <div className={`max-w-[75%] ${msg.role === 'user' ? 'text-right' : ''}`}>
                    {msg.agentName && msg.role === 'assistant' && (
                      <div className="text-xs text-gray-500 mb-1">
                        {msg.agentName === 'System' ? 'System' : `编排模式: ${msg.agentName}`}
                      </div>
                    )}
                    <Card
                      size="small"
                      className={`!rounded-2xl !border-0 ${
                        msg.role === 'user' ? '!bg-primary !text-white' : '!bg-dark-800 !text-gray-200'
                      }`}
                      styles={{ body: { padding: '10px 14px' } }}
                    >
                      {msg.role === 'user' ? (
                        <div className={`whitespace-pre-wrap text-sm leading-relaxed ${msg.streaming ? 'cursor-blink' : ''}`}>
                          {msg.content || (msg.streaming ? <Spin size="small" /> : '')}
                        </div>
                      ) : (
                        <div className={`text-sm leading-relaxed ${msg.streaming ? 'cursor-blink' : ''}`}>
                          {msg.content
                            ? <MarkdownContent content={msg.content} />
                            : (msg.streaming ? <Spin size="small" /> : '')}
                        </div>
                      )}
                    </Card>
                  </div>
                </div>
              ))}
              <div ref={messagesEndRef} />
            </div>
          </main>

          <footer className="border-t border-gray-800 p-4 bg-dark-800/80">
            <div className="max-w-3xl mx-auto">
              <div className="flex gap-3">
                <Input.TextArea
                  value={input}
                  onChange={e => setInput(e.target.value)}
                  onKeyDown={handleKeyDown}
                  placeholder={`输入消息... (当前模式: ${MODES.find(m => m.key === mode)?.cn}, Enter发送)`}
                  autoSize={{ minRows: 1, maxRows: 5 }}
                  disabled={loading}
                  className="!bg-dark-700 !border-gray-700 !text-white !rounded-xl"
                  style={{ resize: 'none' }}
                />
                <Space direction="vertical" align="end" size={4}>
                  {loading ? (
                    <Button
                      danger icon={<StopOutlined />}
                      onClick={stopGeneration}
                      className="!h-auto !rounded-xl" size="large"
                    >停止</Button>
                  ) : (
                    <Button
                      type="primary" icon={<SendOutlined />}
                      onClick={sendMessage}
                      disabled={!input.trim()}
                      className="!h-auto !rounded-xl !bg-primary" size="large"
                    />
                  )}
                  <Button icon={<PlusOutlined />} onClick={newChat} size="small" type="text">新对话</Button>
                </Space>
              </div>
              <p className="text-xs text-gray-600 text-center mt-2">
                .NET 11 + MAF 1.13 · 10 Agent · 7 编排模式 · RAG知识库 + CRM集成 + 人审
              </p>
            </div>
          </footer>
        </div>
      </div>

      {/* 人审弹窗 */}
      <ApprovalModal
        approvalEvent={pendingApproval}
        onClose={() => setPendingApproval(null)}
      />
    </div>
  )
}
