// ============================================================
// EvalDashboard - MVP-5 评测仪表盘
// 控制面板 + SSE进度 + 6维雷达图 + A/B对比表 + 历史报告
// ============================================================

import { useState, useEffect, useCallback, useRef } from 'react'
import {
  Card, Row, Col, Button, Select, Checkbox, InputNumber, Progress, Table,
  Drawer, Descriptions, Tag, message, Space, Typography, Divider, Spin, Empty
} from 'antd'
import {
  PlayCircleOutlined, HistoryOutlined, ExperimentOutlined,
  BarChartOutlined, DownloadOutlined, ReloadOutlined
} from '@ant-design/icons'
import ReactECharts from 'echarts-for-react'
import { apiGet, apiPost } from '../api'

// ---------- 类型 ----------
interface EvalRunRequest {
  caseSet: string; modes: string[]; enableRag: boolean; disableRag: boolean
  judgeCount: number; timeoutSeconds: number; maxConcurrency: number
}
interface EvalProgress { type: string; agent: string; message: string; percent: number }
interface DimensionScore { dimension: string; score: number; weighting: number; reasoning: string }
interface EvalCaseResult {
  id: number; testCaseId: number; mode: string; success: boolean; errorMessage?: string
  dimensions: DimensionScore[]; responseTimeMs: number; totalTokens: number
  toolAccuracy: number; conversationLog: string; agentOutputs: string; judgeRawOutput: string
}
interface ModeComparison { mode: string; casesRun: number; successCount: number; avgScores: Record<string,number>; weightedTotal: number; avgResponseMs: number; totalTokens: number; avgToolAccuracy: number }
interface ABComparison { summary: string; modeComparisons: ModeComparison[] }
interface EvalReport {
  taskId: string; caseSet: string; modes: string[]; status: string
  totalCases: number; successCases: number; failedCases: number
  overallScore: number; avgResponseMs: number; totalTokens: number
  caseResults: EvalCaseResult[]; comparison?: ABComparison
  createdAt: string; completedAt?: string
}
interface CaseSetItem { [key: string]: string[] }

// ---------- 常量 ----------
const CASE_SETS = [
  { label: '全量 (30用例)', value: 'full' },
  { label: '快速冒烟 (6用例)', value: 'quick-smoke' },
  { label: 'RAG专项', value: 'rag' },
  { label: '工具专项', value: 'tool' },
  { label: 'CRM专项', value: 'crm' },
]
const ALL_MODES = ['Sequential', 'Concurrent', 'Handoff', 'GroupChat', 'Magentic', 'Crm']

export default function EvalDashboard() {
  // 控制面板
  const [caseSet, setCaseSet] = useState('quick-smoke')
  const [modes, setModes] = useState<string[]>(['Sequential'])
  const [enableRag, setEnableRag] = useState(true)
  const [disableRag, setDisableRag] = useState(false)
  const [judgeCount, setJudgeCount] = useState(1)
  const [timeoutSec, setTimeoutSec] = useState(60)
  const [concurrency, setConcurrency] = useState(1)

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

  const eventSourceRef = useRef<EventSource | null>(null)

  // 加载历史报告
  const loadReports = useCallback(async () => {
    setLoading(true)
    try { setReports(await apiGet<EvalReport[]>('/api/eval/reports?limit=10')) }
    catch (e: any) { message.error(`加载失败: ${e.message}`) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => { loadReports() }, [loadReports])

  // 启动评测
  const onStartEval = async () => {
    if (modes.length === 0) { message.warning('至少选择一个编排模式'); return }
    setRunning(true)
    setProgress({ done: 0, total: 0, pct: 0, msg: '启动中...' })
    setReport(null)
    try {
      const req: EvalRunRequest = { caseSet, modes, enableRag, disableRag, judgeCount, timeoutSeconds: timeoutSec, maxConcurrency: concurrency }
      const res = await apiPost<{ taskId: string }>('/api/eval/run', req)
      setTaskId(res.taskId)

      // SSE 连接
      const sse = new EventSource(`/api/eval/progress/${res.taskId}`)
      eventSourceRef.current = sse
      sse.onmessage = (ev) => {
        try {
          const data: EvalProgress = JSON.parse(ev.data)
          if (data.type === 'progress') {
            setProgress({ done: parseInt(data.message?.split('/')[0] || '0'), total: parseInt(data.message?.split('/')[1] || '0'), pct: data.percent, msg: data.message })
          } else if (data.type === 'done') {
            sse.close()
            setRunning(false)
            loadReport(res.taskId)
          }
        } catch {}
      }
      sse.onerror = () => { sse.close(); setRunning(false); message.error('SSE 连接断开') }
    } catch (e: any) {
      setRunning(false)
      message.error(`启动失败: ${e.message}`)
    }
  }

  const onStop = () => {
    eventSourceRef.current?.close()
    setRunning(false)
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
          value: dims.map(d => m.avgScores[d] || 0)
        }))
      }]
    }
  }

  return (
    <div className="p-6 max-w-7xl mx-auto space-y-6">
      <Typography.Title level={4} className="!text-white">评测仪表盘 <Tag color="purple" className="ml-2">MVP-5</Tag></Typography.Title>

      {/* 控制面板 */}
      <Card className="!bg-dark-800 !border-gray-700" title={<span className="text-white"><ExperimentOutlined className="mr-2" />评测控制面板</span>}>
        <Row gutter={[16, 12]}>
          <Col xs={24} sm={8}>
            <Typography.Text className="!text-gray-400">用例集</Typography.Text>
            <Select value={caseSet} onChange={setCaseSet} options={CASE_SETS} className="w-full mt-1" />
          </Col>
          <Col xs={24} sm={10}>
            <Typography.Text className="!text-gray-400">编排模式</Typography.Text>
            <Checkbox.Group value={modes} onChange={v => setModes(v as string[])} className="mt-1">
              <Space wrap>{ALL_MODES.map(m => <Checkbox key={m} value={m} className="!text-gray-300">{m}</Checkbox>)}</Space>
            </Checkbox.Group>
          </Col>
          <Col xs={24} sm={6}>
            <Typography.Text className="!text-gray-400">Judge参数</Typography.Text>
            <Space className="mt-1 block">
              <span className="text-gray-400 text-sm">次数:</span>
              <InputNumber min={1} max={3} value={judgeCount} onChange={v => setJudgeCount(v || 1)} size="small" />
              <span className="text-gray-400 text-sm">超时:</span>
              <InputNumber min={10} max={300} value={timeoutSec} onChange={v => setTimeoutSec(v || 60)} size="small" addonAfter="s" />
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
                {report.comparison?.summary && (
                  <Typography.Paragraph className="!text-gray-300 !mb-3" ellipsis={{ rows: 2 }}>
                    {report.comparison.summary}
                  </Typography.Paragraph>
                )}
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
                { title: '评分', dataIndex: 'dimensions', width: 80, render: (v: DimensionScore[]) => (v.length > 0 ? (v.reduce((s, d) => s + d.score, 0) / v.length).toFixed(1) : '-') },
                { title: '工具', dataIndex: 'toolAccuracy', width: 70, render: (v: number) => `${(v * 100).toFixed(0)}%` },
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
        <Table
          size="small" pagination={false}
          dataSource={reports} rowKey="taskId"
          columns={[
            { title: '任务ID', dataIndex: 'taskId', width: 120, render: (v: string) => v.slice(0, 8) },
            { title: '用例集', dataIndex: 'caseSet', width: 100 },
            { title: '状态', dataIndex: 'status', width: 80, render: (v: string) => <Tag color={v === 'completed' ? 'green' : 'orange'}>{v}</Tag> },
            { title: '得分', dataIndex: 'overallScore', width: 70, render: (v: number) => v.toFixed(1) },
            { title: '用例', key: 'cases', width: 60, render: (_: any, r: EvalReport) => `${r.successCases}/${r.totalCases}` },
            { title: '时间', dataIndex: 'createdAt', width: 140, render: (v: string) => v?.replace('T', ' ').slice(0, 16) },
            { title: '操作', key: 'action', width: 120, render: (_: any, r: EvalReport) => (
              <Space>
                <Button size="small" type="link" onClick={() => onViewHistory(r.taskId)}>查看</Button>
                <Button size="small" type="link" onClick={() => onExport(r.taskId, 'markdown')}>导出</Button>
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
        width={600} className="dark-drawer"
      >
        {detailCase && (
          <div className="space-y-4">
            <Descriptions column={2} size="small" bordered>
              <Descriptions.Item label="模式">{detailCase.mode}</Descriptions.Item>
              <Descriptions.Item label="状态">{detailCase.success ? <Tag color="green">成功</Tag> : <Tag color="red">失败</Tag>}</Descriptions.Item>
              <Descriptions.Item label="延迟">{detailCase.responseTimeMs}ms</Descriptions.Item>
              <Descriptions.Item label="Token">{detailCase.totalTokens}</Descriptions.Item>
            </Descriptions>
            {detailCase.errorMessage && <Typography.Text type="danger">错误: {detailCase.errorMessage}</Typography.Text>}
            <Typography.Title level={5} className="!text-gray-300">维度评分</Typography.Title>
            {detailCase.dimensions.map(d => (
              <Card key={d.dimension} size="small" className="!bg-dark-700 !border-gray-700 !mb-2">
                <Space><strong className="text-primary">{d.dimension}</strong><Tag>{d.score}</Tag></Space>
                <div className="text-gray-400 text-sm mt-1">{d.reasoning}</div>
              </Card>
            ))}
            <Typography.Title level={5} className="!text-gray-300">Agent 输出</Typography.Title>
            <pre className="text-gray-300 text-xs bg-dark-700 p-3 rounded max-h-40 overflow-auto">{detailCase.agentOutputs?.slice(0, 500)}</pre>
          </div>
        )}
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
