// ============================================================
// EvalDashboard - MVP-5 评测仪表盘
// 控制面板 + SSE进度 + 6维雷达图 + A/B对比表 + 历史报告
// ============================================================

import { useState, useEffect, useCallback, useRef } from 'react'
import {
  Card, Row, Col, Button, Select, Checkbox, InputNumber, Progress, Table,
  Drawer, Descriptions, Tag, message, Space, Typography, Divider, Spin, Empty,
  Tabs, Timeline, Popconfirm
} from 'antd'
import {
  PlayCircleOutlined, HistoryOutlined, ExperimentOutlined,
  BarChartOutlined, DownloadOutlined, ReloadOutlined
} from '@ant-design/icons'
import ReactECharts from 'echarts-for-react'
import { apiGet, apiPost, apiDelete } from '../api'

// ---------- 类型 ----------
interface EvalRunRequest {
  caseSet: string; modes: string[]; enableRag: boolean; disableRag: boolean
  judgeCount: number; timeoutSeconds: number; maxConcurrency: number; retryCount: number
}
interface EvalProgress { type: string; done: number; total: number; percent: number; currentCase: string; currentMode: string; completed: number; failed: number }
interface DimensionScore { dimension: string; score: number; weight: number; weightedScore: number; reasoning: string }
interface EvalCaseResult {
  id: number; testCaseId: number; mode: string; ragEnabled: boolean; success: boolean; errorMessage?: string
  dimensions: DimensionScore[]; responseTimeMs: number; totalTokens: number
  inputTokens: number; outputTokens: number
  toolCallAccuracy: number; expectedToolCount: number; actualToolCount: number
  conversationLog: string; agentOutputs: string; toolCallLog: string; judgeRawOutput: string
}
interface ModeComparison { mode: string; casesRun: number; successCount: number; avgScores: Record<string,number>; weightedTotal: number; avgResponseMs: number; totalTokens: number; avgToolAccuracy: number }
interface RagComparison { mode: string; ragEnabled: boolean; weightedTotal: number; avgHallucination: number; avgAccuracy: number }
interface ABComparison { summary: string; modeComparisons: ModeComparison[]; ragComparisons: RagComparison[] }
interface EvalReport {
  taskId: string; caseSet: string; modes: string[]; status: string
  totalCases: number; successCases: number; failedCases: number
  overallScore: number; avgResponseMs: number; totalTokens: number
  caseResults: EvalCaseResult[]; comparison?: ABComparison
  createdAt: string; completedAt?: string
}
interface CaseSetItem { [key: string]: string[] }

// ---------- 常量 ----------
const SET_LABELS: Record<string, string> = {
  'full': '全量',
  'quick-smoke': '快速冒烟',
  'rag': 'RAG专项',
  'tool': '工具专项',
  'crm': 'CRM专项',
}
// 用例集 × 模式适配：不适用的模式置灰（与后端 ApplicableModes 校验对齐）
const INCOMPATIBLE_MODES: Record<string, string[]> = {
  'rag': ['Crm'],
  'tool': ['Crm', 'Rag'],
  'crm': ['Rag'],
}
// 切换用例集时的默认模式推荐
const DEFAULT_MODES: Record<string, string[]> = {
  'rag': ['Rag', 'Sequential'],
  'crm': ['Crm'],
  'tool': ['Sequential'],
  'quick-smoke': ['Sequential'],
  'full': ['Sequential'],
}
const ALL_MODES = ['Sequential', 'Concurrent', 'Handoff', 'GroupChat', 'Magentic', 'Crm', 'Rag']

// 维度元信息：中文名 + 评分来源 + 颜色（兼容旧数据中 dimension 为整数的情况）
const DIMENSION_META: Record<string, { cn: string; source: string; color: string }> = {
  Accuracy:      { cn: '准确性', source: 'LLM-Judge', color: '#1890ff' },
  Completeness:  { cn: '完整性', source: 'LLM-Judge', color: '#52c41a' },
  Relevance:     { cn: '相关性', source: 'LLM-Judge', color: '#faad14' },
  Hallucination: { cn: '低幻觉', source: 'LLM-Judge', color: '#f5222d' },
  ToolAccuracy:  { cn: '工具准确', source: '自动计算', color: '#722ed1' },
  Efficiency:    { cn: '响应效率', source: '自动计算', color: '#13c2c2' },
  // 兼容旧数据（enum 序列化为整数）
  '0': { cn: '准确性', source: 'LLM-Judge', color: '#1890ff' },
  '1': { cn: '完整性', source: 'LLM-Judge', color: '#52c41a' },
  '2': { cn: '相关性', source: 'LLM-Judge', color: '#faad14' },
  '3': { cn: '低幻觉', source: 'LLM-Judge', color: '#f5222d' },
  '4': { cn: '工具准确', source: '自动计算', color: '#722ed1' },
  '5': { cn: '响应效率', source: '自动计算', color: '#13c2c2' },
}

export default function EvalDashboard() {
  // 控制面板
  const [caseSet, setCaseSet] = useState('quick-smoke')
  const [caseSetOptions, setCaseSetOptions] = useState<{ label: string; value: string }[]>([])
  const [modes, setModes] = useState<string[]>(['Sequential'])
  const [enableRag, setEnableRag] = useState(true)
  const [disableRag, setDisableRag] = useState(false)
  const [judgeCount, setJudgeCount] = useState(1)
  const [timeoutSec, setTimeoutSec] = useState(60)
  const [concurrency, setConcurrency] = useState(1)
  const [retryCount, setRetryCount] = useState(2)

  // 运行状态
  const [running, setRunning] = useState(false)
  const [taskId, setTaskId] = useState<string | null>(null)
  const [progress, setProgress] = useState({ done: 0, total: 0, pct: 0, msg: '' })

  // 结果
  const [report, setReport] = useState<EvalReport | null>(null)
  const [reports, setReports] = useState<EvalReport[]>([])
  const [loading, setLoading] = useState(false)

  // 详情抽屉
  const [detailOpen, setDetailOpen] = useState(false)
  const [detailCase, setDetailCase] = useState<EvalCaseResult | null>(null)

  // 报告对比
  const [compareA, setCompareA] = useState<string | null>(null)
  const [compareB, setCompareB] = useState<string | null>(null)
  const [compareResult, setCompareResult] = useState<{ a: EvalReport; b: EvalReport } | null>(null)
  const [comparing, setComparing] = useState(false)

  const eventSourceRef = useRef<EventSource | null>(null)

  // 加载历史报告
  const loadReports = useCallback(async () => {
    setLoading(true)
    try { setReports(await apiGet<EvalReport[]>('/api/eval/reports?limit=10')) }
    catch (e: any) { message.error(`加载失败: ${e.message}`) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => { loadReports() }, [loadReports])

  // 加载用例集（动态数量）
  useEffect(() => {
    apiGet<Record<string, string[]>>('/api/eval/testcases/sets')
      .then(sets => {
        const opts = Object.entries(sets).map(([key, titles]) => ({
          label: `${SET_LABELS[key] || key} (${titles.length}用例)`,
          value: key,
        }))
        setCaseSetOptions(opts)
      })
      .catch(() => {
        // fallback：API 不可用时用静态标签
        setCaseSetOptions(Object.entries(SET_LABELS).map(([k, v]) => ({ label: v, value: k })))
      })
  }, [])

  // 切换用例集时：自动推荐默认模式 + 移除不兼容模式
  useEffect(() => {
    const incompatible = INCOMPATIBLE_MODES[caseSet] || []
    const defaults = DEFAULT_MODES[caseSet] || ['Sequential']
    setModes(prev => {
      const valid = prev.filter(m => !incompatible.includes(m))
      return valid.length > 0 ? valid : defaults
    })
  }, [caseSet])

  // SSE 连接（启动新任务 / 重连运行中任务 共用）
  // 进度 Channel 是无界缓冲的：重连时会先补发刷新期间积压的事件，进度条自动追平
  const connectSSE = (id: string) => {
    eventSourceRef.current?.close()
    const sse = new EventSource(`/api/eval/progress/${id}`)
    eventSourceRef.current = sse
    sse.onmessage = (ev) => {
      try {
        const data: EvalProgress = JSON.parse(ev.data)
        if (data.type === 'eval_progress') {
          setProgress({
            done: data.done, total: data.total, pct: data.percent,
            msg: `第 ${data.done}/${data.total} 个用例 · ${data.currentMode} · ${data.currentCase}${data.failed > 0 ? ` · 失败 ${data.failed}` : ''}`
          })
        } else if (data.type === 'done') {
          sse.close()
          setRunning(false)
          loadReport(id)
          loadReports()
        }
      } catch {}
    }
    sse.onerror = () => { sse.close(); setRunning(false); message.error('SSE 连接断开') }
  }

  // 重连到一个正在运行的任务（页面刷新后恢复进度条）
  const attachToTask = (id: string) => {
    setTaskId(id)
    setRunning(true)
    setProgress({ done: 0, total: 0, pct: 0, msg: '正在连接运行中的任务...' })
    setReport(null)
    connectSSE(id)
  }

  // 启动评测
  const onStartEval = async () => {
    if (modes.length === 0) { message.warning('至少选择一个编排模式'); return }
    setRunning(true)
    setProgress({ done: 0, total: 0, pct: 0, msg: '启动中...' })
    setReport(null)
    try {
      const req: EvalRunRequest = { caseSet, modes, enableRag, disableRag, judgeCount, timeoutSeconds: timeoutSec, maxConcurrency: concurrency, retryCount }
      const res = await apiPost<{ taskId: string }>('/api/eval/run', req)
      setTaskId(res.taskId)
      connectSSE(res.taskId)
    } catch (e: any) {
      setRunning(false)
      message.error(`启动失败: ${e.message}`)
    }
  }

  const onStop = async () => {
    if (taskId) {
      try {
        await apiPost(`/api/eval/cancel/${taskId}`, {})
        message.success('评测已取消')
      } catch (e: any) {
        message.warning(`取消请求失败: ${e.message}`)
      }
    }
    eventSourceRef.current?.close()
    setRunning(false)
    loadReports()
  }

  // 加载报告
  const loadReport = async (id: string) => {
    try {
      const r = await apiGet<EvalReport>(`/api/eval/reports/${id}`)
      setReport(r)
      message.success('评测完成！')
    } catch (e: any) { message.error(`加载报告失败: ${e.message}`) }
  }

  const onViewHistory = async (id: string) => {
    setLoading(true)
    try { setReport(await apiGet<EvalReport>(`/api/eval/reports/${id}`)) }
    catch (e: any) { message.error(`加载失败`) }
    finally { setLoading(false) }
  }

  const onExport = async (id: string, fmt: string) => {
    window.open(`/api/eval/reports/${id}/export?format=${fmt}`, '_blank')
  }

  const onDeleteReport = async (id: string) => {
    try {
      await apiDelete(`/api/eval/reports/${id}`)
      message.success('报告已删除')
      if (report?.taskId === id) setReport(null)
      loadReports()
    } catch (e: any) { message.error(`删除失败: ${e.message}`) }
  }

  // ========== 报告对比 ==========
  const onCompare = async () => {
    if (!compareA || !compareB || compareA === compareB) { message.warning('请选择两份不同的报告'); return }
    setComparing(true)
    try {
      const [a, b] = await Promise.all([
        apiGet<EvalReport>(`/api/eval/reports/${compareA}`),
        apiGet<EvalReport>(`/api/eval/reports/${compareB}`),
      ])
      setCompareResult({ a, b })
    } catch (e: any) { message.error(`对比加载失败: ${e.message}`) }
    finally { setComparing(false) }
  }

  // ========== 趋势折线图配置 ==========
  const getTrendOption = () => {
    const completed = reports.filter(r => r.status === 'completed').reverse()
    if (completed.length < 2) return null
    return {
      tooltip: { trigger: 'axis' as const },
      grid: { left: 40, right: 20, top: 20, bottom: 30 },
      xAxis: {
        type: 'category' as const,
        data: completed.map(r => r.createdAt?.replace('T', ' ').slice(5, 16) || r.taskId.slice(0, 6)),
        axisLabel: { color: '#888', fontSize: 10 },
        axisLine: { lineStyle: { color: '#333' } },
      },
      yAxis: {
        type: 'value' as const, min: 0, max: 10,
        axisLabel: { color: '#888' },
        splitLine: { lineStyle: { color: '#222' } },
      },
      series: [{
        type: 'line', smooth: true,
        data: completed.map(r => r.overallScore),
        itemStyle: { color: '#4F46E5' },
        areaStyle: { color: 'rgba(79,70,229,0.1)' },
      }],
    }
  }

  // ========== 雷达图配置 ==========
  const getRadarOption = (report: EvalReport | null) => {
    if (!report?.comparison?.modeComparisons?.length) return {}
    const dims = ['Accuracy', 'Completeness', 'Relevance', 'Hallucination', 'ToolAccuracy', 'Efficiency']
    const dimLabels = ['准确性', '完整性', '相关性', '低幻觉', '工具准确', '效率']
    return {
      tooltip: {},
      legend: { data: report.comparison.modeComparisons.map(m => m.mode), textStyle: { color: '#aaa' }, top: 0 },
      radar: {
        indicator: dimLabels.map(l => ({ name: l, max: 10 })),
        axisName: { color: '#aaa' }, splitArea: { areaStyle: { color: ['#1a1a2e', '#16213e'] } }
      },
      series: [{
        type: 'radar',
        data: report.comparison.modeComparisons.map(m => ({
          name: m.mode,
          value: dims.map(d => Math.round((m.avgScores[d] || 0) * 100) / 100)
        }))
      }]
    }
  }

  return (
    <div className="p-6 max-w-7xl mx-auto space-y-6">
      <Typography.Title level={4} className="!text-white">评测仪表盘 <Tag color="purple" className="ml-2">MVP-5</Tag></Typography.Title>

      {/* 运行中任务横幅：页面刷新后可重新挂上进度条 */}
      {!running && reports.filter(r => r.status === 'running').length > 0 && (
        <Card
          size="small"
          className="!bg-dark-800 !border-indigo-700"
          title={
            <span className="text-white flex items-center gap-2">
              <span className="inline-block w-2 h-2 rounded-full bg-green-400 animate-pulse" />
              检测到 {reports.filter(r => r.status === 'running').length} 个正在运行的评测任务
            </span>
          }
        >
          <div className="space-y-2">
            {reports.filter(r => r.status === 'running').map(r => (
              <div key={r.taskId} className="flex items-center justify-between bg-dark-700 rounded px-3 py-2">
                <div className="flex items-center gap-3 text-sm">
                  <span className="text-primary font-mono">{r.taskId.slice(0, 8)}</span>
                  <Tag color="blue">{r.caseSet}</Tag>
                  <span className="text-gray-400">{(r.modes || []).join(' / ')}</span>
                  <span className="text-gray-500 text-xs">{r.createdAt?.replace('T', ' ').slice(0, 16)}</span>
                </div>
                <Button size="small" type="primary" className="!bg-primary" onClick={() => attachToTask(r.taskId)}>
                  挂上进度
                </Button>
              </div>
            ))}
          </div>
        </Card>
      )}

      {/* 控制面板 */}
      <Card className="!bg-dark-800 !border-gray-700" title={<span className="text-white"><ExperimentOutlined className="mr-2" />评测控制面板</span>}>
        <Row gutter={[16, 12]}>
          <Col xs={24} sm={8}>
            <Typography.Text className="!text-gray-400">用例集</Typography.Text>
            <Select value={caseSet} onChange={setCaseSet} options={caseSetOptions} className="w-full mt-1" />
          </Col>
          <Col xs={24} sm={10}>
            <Typography.Text className="!text-gray-400">编排模式</Typography.Text>
            <Checkbox.Group value={modes} onChange={v => setModes(v as string[])} className="mt-1">
              <Space wrap>{ALL_MODES.map(m => {
                const disabled = (INCOMPATIBLE_MODES[caseSet] || []).includes(m)
                return <Checkbox key={m} value={m} disabled={disabled} className="!text-gray-300" title={disabled ? '该用例集不适用此模式' : undefined}>{m}</Checkbox>
              })}</Space>
            </Checkbox.Group>
          </Col>
          <Col xs={24} sm={6}>
            <Typography.Text className="!text-gray-400">执行参数</Typography.Text>
            <Space className="mt-1 block" wrap>
              <span className="text-gray-400 text-sm">Judge:</span>
              <InputNumber min={1} max={3} value={judgeCount} onChange={v => setJudgeCount(v || 1)} size="small" />
              <span className="text-gray-400 text-sm">超时:</span>
              <InputNumber min={10} max={300} value={timeoutSec} onChange={v => setTimeoutSec(v || 60)} size="small" addonAfter="s" />
              <span className="text-gray-400 text-sm">并发:</span>
              <InputNumber min={1} max={5} value={concurrency} onChange={v => setConcurrency(v || 1)} size="small" />
              <span className="text-gray-400 text-sm">重试:</span>
              <InputNumber min={0} max={3} value={retryCount} onChange={v => setRetryCount(v ?? 2)} size="small" />
            </Space>
          </Col>
        </Row>
        <Divider className="!border-gray-700 !my-3" />
        <Row gutter={12}>
          <Col><Checkbox checked={enableRag} onChange={e => setEnableRag(e.target.checked)} className="!text-gray-300">开启RAG</Checkbox></Col>
          <Col><Checkbox checked={disableRag} onChange={e => setDisableRag(e.target.checked)} className="!text-gray-300">A/B: 关闭RAG对比</Checkbox></Col>
          <Col flex="auto" />
          <Col>
            {running ? (
              <Button danger icon={<ReloadOutlined spin />} onClick={onStop}>停止</Button>
            ) : (
              <Button type="primary" icon={<PlayCircleOutlined />} onClick={onStartEval} className="!bg-primary">
                启动评测
              </Button>
            )}
          </Col>
        </Row>
        {running && (
          <div className="mt-4">
            <Typography.Text className="!text-gray-400">执行中... {progress.msg}</Typography.Text>
            <Progress percent={progress.pct} status="active" strokeColor="#4F46E5" className="mt-1" />
          </div>
        )}
      </Card>

      {/* 评测结果 */}
      {loading && <div className="text-center py-12"><Spin size="large" tip="加载中..." /></div>}

      {report && (
        <>
          {/* 总分卡片 */}
          <Row gutter={16}>
            <Col xs={12} sm={4}><StatCard title="总体分" value={report.overallScore.toFixed(1)} suffix="/10" color="#4F46E5" /></Col>
            <Col xs={12} sm={4}><StatCard title="成功" value={report.successCases} suffix={`/ ${report.totalCases}`} color="#10B981" /></Col>
            <Col xs={12} sm={4}><StatCard title="平均延迟" value={(report.avgResponseMs / 1000).toFixed(1)} suffix="s" color="#F59E0B" /></Col>
            <Col xs={12} sm={4}><StatCard title="总Token" value={report.totalTokens > 1000 ? `${(report.totalTokens / 1000).toFixed(1)}k` : report.totalTokens} suffix="" color="#EF4444" /></Col>
            <Col xs={24} sm={8}>
              <Button icon={<DownloadOutlined />} onClick={() => onExport(report.taskId, 'markdown')} className="mr-2">导出Markdown</Button>
              <Button icon={<DownloadOutlined />} onClick={() => onExport(report.taskId, 'json')}>导出JSON</Button>
            </Col>
          </Row>

          {/* 雷达图 + 对比表 */}
          <Row gutter={16}>
            <Col xs={24} lg={12}>
              <Card className="!bg-dark-800 !border-gray-700" title={<span className="text-white"><BarChartOutlined className="mr-2" />6维雷达图</span>}>
                <ReactECharts option={getRadarOption(report)} style={{ height: 360 }} />
              </Card>
            </Col>
            <Col xs={24} lg={12}>
              <Card className="!bg-dark-800 !border-gray-700" title={<span className="text-white">A/B 模式对比</span>}>
                {report.comparison?.modeComparisons && report.comparison.modeComparisons.length > 0 && (() => {
                  const modes = report.comparison!.modeComparisons
                  const best = modes.reduce((a, b) => a.weightedTotal > b.weightedTotal ? a : b)
                  const others = modes.filter(m => m.mode !== best.mode)
                  const avgRt = others.length > 0 ? others.reduce((s, m) => s + m.avgResponseMs, 0) / others.length : 0
                  const reasons: string[] = []
                  if (best.avgResponseMs < avgRt) reasons.push(`延迟低 ${((1 - best.avgResponseMs / avgRt) * 100).toFixed(0)}%`)
                  const bestTool = best.avgToolAccuracy; const avgTool = others.reduce((s,m)=>s+m.avgToolAccuracy,0)/(others.length||1)
                  if (bestTool > avgTool) reasons.push(`工具准确率高 ${((bestTool - avgTool) * 100).toFixed(0)}pp`)
                  if (others.every(m => best.weightedTotal > m.weightedTotal)) reasons.push('全部维度领先')
                  return <Typography.Paragraph className="!text-gray-300 !mb-3">
                    最优模式：<Tag color="green">{best.mode}</Tag> 综合得分 {best.weightedTotal} 分
                    {reasons.length > 0 && <span className="text-gray-400 ml-2">——{reasons.join('，')}</span>}
                  </Typography.Paragraph>
                })()}
                <Table
                  size="small"
                  pagination={false}
                  dataSource={report.comparison?.modeComparisons || []}
                  rowKey="mode"
                  columns={[
                    { title: '模式', dataIndex: 'mode', key: 'mode', width: 100 },
                    { title: '总分', dataIndex: 'weightedTotal', key: 'wt', width: 70, render: (v: number) => <span className={v >= 6 ? 'text-green-400' : 'text-red-400'}>{v}</span> },
                    { title: '工具', dataIndex: 'avgToolAccuracy', key: 'ta', width: 70, render: (v: number) => `${(v * 100).toFixed(0)}%` },
                    { title: '延迟', dataIndex: 'avgResponseMs', key: 'rt', width: 80, render: (v: number) => `${(v / 1000).toFixed(1)}s` },
                    { title: 'Token', dataIndex: 'totalTokens', key: 'tk', width: 70 },
                    { title: '成功', dataIndex: 'successCount', key: 'sc', width: 60, render: (v: number, r: ModeComparison) => `${v}/${r.casesRun}` },
                  ]}
                  className="dark-table"
                />
              </Card>
            </Col>
          </Row>

          {/* RAG 开关 A/B 对比（仅当有 RAG on/off 数据时展示） */}
          {report.comparison?.ragComparisons && report.comparison.ragComparisons.length > 0 && (
            <Card className="!bg-dark-800 !border-gray-700" title={<span className="text-white">RAG 效果对比（开/关知识库）</span>}>
              <Table
                size="small"
                pagination={false}
                dataSource={(() => {
                  const comps = report.comparison!.ragComparisons
                  const modes = [...new Set(comps.map(c => c.mode))]
                  return modes.map(mode => {
                    const on = comps.find(c => c.mode === mode && c.ragEnabled)
                    const off = comps.find(c => c.mode === mode && !c.ragEnabled)
                    return { mode, on, off, delta: on && off ? on.weightedTotal - off.weightedTotal : null }
                  })
                })()}
                rowKey="mode"
                columns={[
                  { title: '模式', dataIndex: 'mode', key: 'mode', width: 100 },
                  { title: 'RAG开', key: 'on', width: 90, render: (_: any, r: any) => r.on ? <span className="text-green-400 font-bold">{r.on.weightedTotal}</span> : '-' },
                  { title: 'RAG关', key: 'off', width: 90, render: (_: any, r: any) => r.off ? <span className="text-gray-400">{r.off.weightedTotal}</span> : '-' },
                  { title: '提升', dataIndex: 'delta', key: 'delta', width: 90, render: (v: number | null) => v == null ? '-' : (
                    <span className={v >= 0 ? 'text-green-400' : 'text-red-400'}>{v >= 0 ? '+' : ''}{v.toFixed(2)}</span>
                  )},
                  { title: '准确性(开/关)', key: 'acc', width: 120, render: (_: any, r: any) => `${r.on?.avgAccuracy ?? '-'} / ${r.off?.avgAccuracy ?? '-'}` },
                  { title: '低幻觉(开/关)', key: 'hall', width: 120, render: (_: any, r: any) => `${r.on?.avgHallucination ?? '-'} / ${r.off?.avgHallucination ?? '-'}` },
                ]}
                className="dark-table"
              />
            </Card>
          )}

          {/* 用例详情表 */}
          <Card className="!bg-dark-800 !border-gray-700" title={<span className="text-white">用例详情</span>}>
            <Table
              size="small"
              pagination={{ pageSize: 15 }}
              dataSource={report.caseResults}
              rowKey="id"
              columns={[
                { title: '#', dataIndex: 'testCaseId', width: 50 },
                { title: '模式', dataIndex: 'mode', width: 100 },
                { title: '状态', dataIndex: 'success', width: 70, render: (v: boolean) => v ? <Tag color="green">成功</Tag> : <Tag color="red">失败</Tag> },
                { title: '评分', dataIndex: 'dimensions', width: 80, render: (v: DimensionScore[]) => {
                  if (!v || v.length === 0) return '-'
                  const totalWeight = v.reduce((s, d) => s + (d.weight || 1), 0)
                  const weighted = v.reduce((s, d) => s + d.score * (d.weight || 1), 0)
                  return totalWeight > 0 ? (weighted / totalWeight).toFixed(1) : '-'
                }},
                { title: '工具', dataIndex: 'toolCallAccuracy', width: 70, render: (v: number) => v != null ? `${(v * 100).toFixed(0)}%` : '-' },
                { title: 'Token', dataIndex: 'totalTokens', width: 70 },
                { title: '延迟', dataIndex: 'responseTimeMs', width: 80, render: (v: number) => `${v}ms` },
                { title: '操作', key: 'action', width: 80, render: (_: any, r: EvalCaseResult) => (
                  <Button size="small" type="link" onClick={() => { setDetailCase(r); setDetailOpen(true) }}>查看</Button>
                )},
              ]}
              className="dark-table"
            />
          </Card>
        </>
      )}

      {!report && !loading && !running && (
        <Card className="!bg-dark-800 !border-gray-700"><Empty description={'点击「启动评测」开始'} /></Card>
      )}

      {/* 历史报告 */}
      <Card className="!bg-dark-800 !border-gray-700" title={<span className="text-white"><HistoryOutlined className="mr-2" />历史报告</span>}>
        {/* 趋势折线图 */}
        {getTrendOption() && (
          <div className="mb-4">
            <Typography.Text className="!text-gray-400 text-sm">综合分趋势</Typography.Text>
            <ReactECharts option={getTrendOption()!} style={{ height: 160 }} />
          </div>
        )}

        {/* 报告对比选择器 */}
        <div className="flex items-center gap-2 mb-4 flex-wrap">
          <Typography.Text className="!text-gray-400 text-sm">对比:</Typography.Text>
          <Select
            value={compareA} onChange={setCompareA} placeholder="报告 A" allowClear
            className="w-44" size="small"
            options={reports.filter(r => r.status === 'completed').map(r => ({ value: r.taskId, label: `${r.taskId.slice(0, 6)} · ${r.caseSet} · ${r.overallScore.toFixed(1)}分` }))}
          />
          <Typography.Text className="!text-gray-500">vs</Typography.Text>
          <Select
            value={compareB} onChange={setCompareB} placeholder="报告 B" allowClear
            className="w-44" size="small"
            options={reports.filter(r => r.status === 'completed').map(r => ({ value: r.taskId, label: `${r.taskId.slice(0, 6)} · ${r.caseSet} · ${r.overallScore.toFixed(1)}分` }))}
          />
          <Button size="small" type="primary" className="!bg-primary" onClick={onCompare} loading={comparing}>对比</Button>
        </div>

        {/* 对比结果 */}
        {compareResult && (() => {
          const { a, b } = compareResult
          const dims = ['Accuracy', 'Completeness', 'Relevance', 'Hallucination', 'ToolAccuracy', 'Efficiency']
          const dimCn: Record<string, string> = { Accuracy: '准确性', Completeness: '完整性', Relevance: '相关性', Hallucination: '低幻觉', ToolAccuracy: '工具准确', Efficiency: '效率' }
          const avgDim = (r: EvalReport, dim: string) => {
            const scores = r.caseResults.filter(c => c.success).flatMap(c => c.dimensions).filter(d => String(d.dimension) === dim)
            return scores.length > 0 ? scores.reduce((s, d) => s + d.score, 0) / scores.length : 0
          }
          const delta = b.overallScore - a.overallScore
          // 新增通过/失败
          const aKeys = new Map(a.caseResults.map(r => [`${r.testCaseId}|${r.mode}`, r.success]))
          const newlyPassed = b.caseResults.filter(r => r.success && aKeys.get(`${r.testCaseId}|${r.mode}`) === false)
          const newlyFailed = b.caseResults.filter(r => !r.success && aKeys.get(`${r.testCaseId}|${r.mode}`) === true)

          return (
            <Card size="small" className="!bg-dark-700 !border-gray-700 mb-4">
              <div className="flex items-center justify-between mb-3">
                <Typography.Text className="!text-gray-300 text-sm font-medium">
                  {a.taskId.slice(0, 6)} → {b.taskId.slice(0, 6)} 综合分变化：
                  <span className={delta >= 0 ? 'text-green-400' : 'text-red-400'}> {delta >= 0 ? '+' : ''}{delta.toFixed(2)}</span>
                  <span className="text-gray-500 ml-1">({a.overallScore.toFixed(1)} → {b.overallScore.toFixed(1)})</span>
                </Typography.Text>
                <Button size="small" type="text" onClick={() => setCompareResult(null)}>关闭</Button>
              </div>
              <div className="grid grid-cols-2 sm:grid-cols-3 gap-2 mb-3">
                {dims.map(dim => {
                  const va = avgDim(a, dim), vb = avgDim(b, dim), d = vb - va
                  return (
                    <div key={dim} className="bg-dark-800 rounded px-2 py-1 text-xs">
                      <span className="text-gray-400">{dimCn[dim]}</span>
                      <span className={`ml-1 font-medium ${d >= 0 ? 'text-green-400' : 'text-red-400'}`}>
                        {d >= 0 ? '↑' : '↓'}{Math.abs(d).toFixed(1)}
                      </span>
                      <span className="text-gray-600 ml-1">{va.toFixed(1)}→{vb.toFixed(1)}</span>
                    </div>
                  )
                })}
              </div>
              {(newlyPassed.length > 0 || newlyFailed.length > 0) && (
                <div className="text-xs space-y-1">
                  {newlyPassed.length > 0 && <div className="text-green-400">新增通过 {newlyPassed.length} 个：{newlyPassed.map(r => `#${r.testCaseId}(${r.mode})`).join('、')}</div>}
                  {newlyFailed.length > 0 && <div className="text-red-400">新增失败 {newlyFailed.length} 个：{newlyFailed.map(r => `#${r.testCaseId}(${r.mode})`).join('、')}</div>}
                </div>
              )}
            </Card>
          )
        })()}

        <Table
          size="small" pagination={false}
          dataSource={reports} rowKey="taskId"
          columns={[
            { title: '任务ID', dataIndex: 'taskId', width: 120, render: (v: string) => v.slice(0, 8) },
            { title: '用例集', dataIndex: 'caseSet', width: 100 },
            { title: '状态', dataIndex: 'status', width: 90, render: (v: string) => {
              const map: Record<string, { color: string; label: string }> = {
                completed: { color: 'green', label: '完成' },
                running: { color: 'orange', label: '运行中' },
                interrupted: { color: 'red', label: '已中断' },
                cancelled: { color: 'default', label: '已取消' },
                failed: { color: 'red', label: '失败' },
              }
              const s = map[v] || { color: 'default', label: v }
              return <Tag color={s.color}>{s.label}</Tag>
            }},
            { title: '得分', dataIndex: 'overallScore', width: 70, sorter: (a: EvalReport, b: EvalReport) => a.overallScore - b.overallScore, render: (v: number) => v.toFixed(1) },
            { title: '用例', key: 'cases', width: 60, render: (_: any, r: EvalReport) => `${r.successCases}/${r.totalCases}` },
            { title: '时间', dataIndex: 'createdAt', width: 140, defaultSortOrder: 'descend' as const, sorter: (a: EvalReport, b: EvalReport) => (a.createdAt || '').localeCompare(b.createdAt || ''), render: (v: string) => v?.replace('T', ' ').slice(0, 16) },
            { title: '操作', key: 'action', width: 160, render: (_: any, r: EvalReport) => (
              <Space>
                <Button size="small" type="link" onClick={() => onViewHistory(r.taskId)}>查看</Button>
                <Button size="small" type="link" onClick={() => onExport(r.taskId, 'markdown')}>导出</Button>
                <Popconfirm title="删除该报告？" okText="删除" cancelText="取消" okButtonProps={{ danger: true }} onConfirm={() => onDeleteReport(r.taskId)}>
                  <Button size="small" type="link" danger>删除</Button>
                </Popconfirm>
              </Space>
            )},
          ]}
          className="dark-table"
        />
      </Card>

      {/* 用例详情抽屉 */}
      <Drawer
        title={<span className="text-white">用例详情 #{detailCase?.testCaseId}</span>}
        open={detailOpen} onClose={() => setDetailOpen(false)}
        width={640} className="dark-drawer"
      >
        {detailCase && (() => {
          // 解析对话日志为时间线条目
          const timelineItems = (detailCase.conversationLog || '').split('\n').filter(l => l.trim()).map((line, i) => {
            if (line.startsWith('[User]')) return { key: i, color: 'blue' as const, label: '用户', text: line.slice(6).trim() }
            if (line.startsWith('[Agent]')) return { key: i, color: 'green' as const, label: 'Agent', text: line.slice(7).trim() }
            if (line.startsWith('[Tool]')) return { key: i, color: 'purple' as const, label: '工具', text: line.slice(6).trim() }
            if (line.startsWith('[Handoff]')) return { key: i, color: 'orange' as const, label: '移交', text: line.slice(9).trim() }
            if (line.startsWith('[FinalAnswer]')) return { key: i, color: 'red' as const, label: '最终回答', text: line.slice(13).trim() }
            return { key: i, color: 'gray' as const, label: '', text: line }
          })
          // 解析工具调用列表
          let toolNames: string[] = []
          try { toolNames = JSON.parse(detailCase.toolCallLog || '[]') } catch { toolNames = [] }
          // 同用例其他模式的结果（用于模式对比）
          const siblings = (report?.caseResults || []).filter(r => r.testCaseId === detailCase.testCaseId && r.id !== detailCase.id)

          return (
            <div className="space-y-4">
              <Descriptions column={2} size="small" bordered>
                <Descriptions.Item label="模式">{detailCase.mode}</Descriptions.Item>
                <Descriptions.Item label="状态">{detailCase.success ? <Tag color="green">成功</Tag> : <Tag color="red">失败</Tag>}</Descriptions.Item>
                <Descriptions.Item label="延迟">{detailCase.responseTimeMs}ms</Descriptions.Item>
                <Descriptions.Item label="RAG">{detailCase.ragEnabled ? <Tag color="cyan">开启</Tag> : <Tag>关闭</Tag>}</Descriptions.Item>
                <Descriptions.Item label="Token">{detailCase.totalTokens} <span className="text-gray-500 text-xs">(入{detailCase.inputTokens ?? '-'} / 出{detailCase.outputTokens ?? '-'})</span></Descriptions.Item>
                <Descriptions.Item label="工具">{detailCase.actualToolCount ?? 0} 次调用 · 准确率 {((detailCase.toolCallAccuracy ?? 0) * 100).toFixed(0)}%</Descriptions.Item>
              </Descriptions>
              {detailCase.errorMessage && <Typography.Text type="danger">错误: {detailCase.errorMessage}</Typography.Text>}

              <Tabs
                defaultActiveKey="dims"
                size="small"
                items={[
                  {
                    key: 'dims',
                    label: '维度评分',
                    children: (
                      <div>
                        {detailCase.dimensions.map((d, idx) => {
                          const meta = DIMENSION_META[d.dimension] || DIMENSION_META[String(idx)] || { cn: d.dimension, source: '未知', color: '#888' }
                          const scoreColor = d.score >= 7 ? '#10B981' : d.score >= 4 ? '#F59E0B' : '#EF4444'
                          return (
                            <Card key={`${d.dimension}-${idx}`} size="small" className="!bg-dark-700 !border-gray-700 !mb-2">
                              <div className="flex items-center justify-between">
                                <Space>
                                  <strong style={{ color: meta.color }}>{meta.cn}</strong>
                                  <Tag color={meta.source === 'LLM-Judge' ? 'purple' : 'blue'} className="!text-xs">{meta.source}</Tag>
                                </Space>
                                <Space>
                                  <span className="text-gray-500 text-xs">权重 {d.weight}</span>
                                  <span className="text-lg font-bold" style={{ color: scoreColor }}>{d.score}</span>
                                  <span className="text-gray-500 text-xs">/10</span>
                                </Space>
                              </div>
                              <div className="text-gray-400 text-sm mt-2 leading-relaxed">{d.reasoning}</div>
                            </Card>
                          )
                        })}
                      </div>
                    ),
                  },
                  {
                    key: 'conversation',
                    label: '对话过程',
                    children: timelineItems.length > 0 ? (
                      <Timeline
                        items={timelineItems.map(item => ({
                          color: item.color,
                          children: (
                            <div>
                              {item.label && <Tag className="!text-xs" color={item.color === 'gray' ? undefined : item.color}>{item.label}</Tag>}
                              <span className="text-gray-300 text-xs">{item.text.length > 200 ? item.text.slice(0, 200) + '…' : item.text}</span>
                            </div>
                          ),
                        }))}
                      />
                    ) : <Empty description="无对话记录" />,
                  },
                  {
                    key: 'tools',
                    label: '工具调用',
                    children: (
                      <div className="space-y-3">
                        <div>
                          <Typography.Text className="!text-gray-400 text-sm">实际调用序列：</Typography.Text>
                          <div className="mt-1">
                            {toolNames.length > 0
                              ? toolNames.map((t, i) => <Tag key={i} color="purple" className="!mb-1">{t}</Tag>)
                              : <Tag>无工具调用</Tag>}
                          </div>
                        </div>
                        <div className="text-gray-400 text-sm">
                          期望 {detailCase.expectedToolCount ?? 0} 次 · 实际 {detailCase.actualToolCount ?? 0} 次 · F1 准确率 {((detailCase.toolCallAccuracy ?? 0) * 100).toFixed(0)}%
                        </div>
                      </div>
                    ),
                  },
                  ...(siblings.length > 0 ? [{
                    key: 'compare',
                    label: '模式对比',
                    children: (
                      <Table
                        size="small"
                        pagination={false}
                        dataSource={[detailCase, ...siblings]}
                        rowKey="id"
                        columns={[
                          { title: '模式', dataIndex: 'mode', width: 100, render: (v: string, r: EvalCaseResult) => <span className={r.id === detailCase.id ? 'text-primary font-bold' : ''}>{v}{r.id === detailCase.id ? ' ←当前' : ''}</span> },
                          { title: '状态', dataIndex: 'success', width: 70, render: (v: boolean) => v ? <Tag color="green">成功</Tag> : <Tag color="red">失败</Tag> },
                          { title: '评分', dataIndex: 'dimensions', width: 70, render: (v: DimensionScore[]) => {
                            if (!v || v.length === 0) return '-'
                            const tw = v.reduce((s, d) => s + (d.weight || 1), 0)
                            const ws = v.reduce((s, d) => s + d.score * (d.weight || 1), 0)
                            return tw > 0 ? (ws / tw).toFixed(1) : '-'
                          }},
                          { title: '延迟', dataIndex: 'responseTimeMs', width: 80, render: (v: number) => `${v}ms` },
                          { title: 'Token', dataIndex: 'totalTokens', width: 70 },
                        ]}
                        className="dark-table"
                      />
                    ),
                  }] : []),
                ]}
              />

              <Typography.Title level={5} className="!text-gray-300">Agent 输出</Typography.Title>
              <pre className="text-gray-300 text-xs bg-dark-700 p-3 rounded max-h-48 overflow-auto whitespace-pre-wrap">{detailCase.agentOutputs?.slice(0, 1000)}</pre>
            </div>
          )
        })()}
      </Drawer>
    </div>
  )
}

// 统计卡片组件
function StatCard({ title, value, suffix, color }: { title: string; value: string | number; suffix: string; color: string }) {
  return (
    <Card className="!bg-dark-800 !border-gray-700 !p-3 text-center">
      <div className="text-gray-400 text-xs">{title}</div>
      <div className="text-2xl font-bold mt-1" style={{ color }}>{value}<span className="text-sm text-gray-500 ml-1">{suffix}</span></div>
    </Card>
  )
}
