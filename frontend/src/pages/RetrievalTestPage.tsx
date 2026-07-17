// ============================================================
// RetrievalTestPage - 检索测试页（MVP-3 RAG）
// 功能：知识库选择 + 查询输入 + Top-K + 重排序开关 + 检索
// 结果展示：召回片段卡片 + 分数对比表格
// ============================================================

import { useState, useEffect, useCallback, Fragment } from 'react'
import type { ReactNode } from 'react'
import {
  Select, Button, Input, Slider, Switch, Card, Tag, Space, message,
  Empty, Spin, Table, Typography, Row, Col
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import {
  SearchOutlined, FileTextOutlined, ReloadOutlined, ThunderboltOutlined
} from '@ant-design/icons'
import { kbApi, KbDatabase, KbSearchResponse, KbSearchResult } from '../api'

const { Text, Paragraph } = Typography

// 4 类分数对应的 Tag 颜色
const SCORE_COLOR = {
  vector: 'blue',
  keyword: 'orange',
  fused: 'purple',
  reranked: 'green',
}

// 分数表行
interface ScoreRow {
  key: string | number
  chunkId: string | number
  vectorScore: number
  keywordScore: number
  fusedScore: number
  rerankedScore: number
  fileName: string
}

// 通用分数取值：兼容 Record<chunkId, score> / 数组对象 / 平行数组
function scoreOf(
  scores: Record<string, number> | number[] | undefined,
  chunkId: string | number,
  idx: number
): number {
  if (scores == null) return 0
  if (Array.isArray(scores)) {
    // 简单数字数组：按位置取
    if (typeof scores[idx] === 'number') return scores[idx] as number
    // 对象数组：[{chunkId, score}, ...]
    const found = (scores as any[]).find(
      (it: any) => it?.chunkId === chunkId || String(it?.chunkId) === String(chunkId)
    )
    return found?.score ?? 0
  }
  // 对象：{chunkId: score}
  return (scores as Record<string, number>)[String(chunkId)] ?? 0
}

// 关键词高亮（不区分大小写）
function highlight(text: string, keywords: string[]): ReactNode {
  if (!text) return null
  const valid = keywords.filter(k => k && k.trim().length > 0).map(k => k.trim())
  if (valid.length === 0) return <span>{text}</span>
  // 构建正则：转义特殊字符
  const pattern = valid.map(k => k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')
  const regex = new RegExp(`(${pattern})`, 'gi')
  const parts = text.split(regex)
  // 关键词小写集合用于判断片段是否命中
  const lowerSet = new Set(valid.map(k => k.toLowerCase()))
  return (
    <span>
      {parts.map((part, i) =>
        part && lowerSet.has(part.toLowerCase()) ? (
          <mark key={i} className="!bg-yellow-500/30 !text-yellow-200 !px-0.5 !rounded">
            {part}
          </mark>
        ) : (
          <Fragment key={i}>{part}</Fragment>
        )
      )}
    </span>
  )
}

export default function RetrievalTestPage() {
  // ---------- 状态 ----------
  const [databases, setDatabases] = useState<KbDatabase[]>([])
  const [dbId, setDbId] = useState<number | null>(null)
  const [query, setQuery] = useState('')
  const [topK, setTopK] = useState(5)
  const [rerank, setRerank] = useState(true)
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<KbSearchResponse | null>(null)

  // ============================================================
  // 加载知识库列表
  // ============================================================
  const loadDatabases = useCallback(async () => {
    try {
      const list = await kbApi.listDatabases()
      const arr = Array.isArray(list) ? list : []
      setDatabases(arr)
      if (arr.length > 0 && dbId == null) setDbId(arr[0].id)
    } catch (e: any) {
      message.error(`加载知识库失败: ${e.message}`)
    }
  }, [dbId])

  useEffect(() => {
    loadDatabases()
  }, [loadDatabases])

  // ============================================================
  // 执行检索
  // ============================================================
  const onSearch = async () => {
    const q = query.trim()
    if (!q) {
      message.warning('请输入查询内容')
      return
    }
    if (!dbId) {
      message.warning('请先选择知识库')
      return
    }
    setLoading(true)
    setResult(null)
    try {
      const resp = await kbApi.search(q, dbId, topK, rerank)
      setResult(resp)
    } catch (e: any) {
      message.error(`检索失败: ${e.message}`)
    } finally {
      setLoading(false)
    }
  }

  // 关键词拆分（用于高亮）
  const keywords = query.trim().split(/\s+/).filter(Boolean)

  // 构建分数对比表数据（按融合分数降序）
  const scoreRows: ScoreRow[] = (result?.results || []).map((r, idx) => {
    const vectorScore = scoreOf(result?.vectorScores, r.chunkId, idx)
    const keywordScore = scoreOf(result?.keywordScores, r.chunkId, idx)
    const fusedScore = scoreOf(result?.fusedScores, r.chunkId, idx)
    return {
      key: r.chunkId,
      chunkId: r.chunkId,
      vectorScore,
      keywordScore,
      fusedScore,
      rerankedScore: r.rerankedScore ?? 0,
      fileName: r.fileName,
    }
  })
  scoreRows.sort((a, b) => b.fusedScore - a.fusedScore)

  // 分数对比表列定义
  const scoreColumns: ColumnsType<ScoreRow> = [
    { title: 'chunkId', dataIndex: 'chunkId', key: 'chunkId', width: 100 },
    {
      title: '向量分数',
      dataIndex: 'vectorScore',
      key: 'vectorScore',
      width: 110,
      render: (v: number) => <Tag color={SCORE_COLOR.vector}>{v?.toFixed(4)}</Tag>,
    },
    {
      title: '关键词分数',
      dataIndex: 'keywordScore',
      key: 'keywordScore',
      width: 110,
      render: (v: number) => <Tag color={SCORE_COLOR.keyword}>{v?.toFixed(4)}</Tag>,
    },
    {
      title: '融合分数',
      dataIndex: 'fusedScore',
      key: 'fusedScore',
      width: 110,
      sorter: (a, b) => a.fusedScore - b.fusedScore,
      defaultSortOrder: 'descend',
      render: (v: number) => <Tag color={SCORE_COLOR.fused}>{v?.toFixed(4)}</Tag>,
    },
    {
      title: '重排序分数',
      dataIndex: 'rerankedScore',
      key: 'rerankedScore',
      width: 110,
      render: (v: number) => <Tag color={SCORE_COLOR.reranked}>{v?.toFixed(4)}</Tag>,
    },
    {
      title: '来源文件',
      dataIndex: 'fileName',
      key: 'fileName',
      ellipsis: true,
      render: (n: string) => <Text className="!text-gray-300">{n}</Text>,
    },
  ]

  return (
    <div className="h-full flex flex-col bg-dark-900">
      {/* 顶部工具栏 */}
      <div className="flex items-center gap-3 px-6 py-4 border-b border-gray-800 bg-dark-800/60 flex-wrap">
        <div className="flex items-center gap-2 text-white">
          <SearchOutlined className="text-primary text-lg" />
          <span className="font-semibold">检索测试</span>
        </div>
        <Select
          value={dbId ?? undefined}
          onChange={setDbId}
          placeholder="选择知识库"
          className="w-64"
          options={databases.map(d => ({
            value: d.id,
            label: `${d.name} (${d.documentCount} 文档)`,
          }))}
          notFoundContent="暂无知识库"
        />
        <Input
          allowClear
          value={query}
          onChange={e => setQuery(e.target.value)}
          onPressEnter={onSearch}
          placeholder="输入检索查询..."
          prefix={<SearchOutlined className="text-gray-500" />}
          className="!bg-dark-700 !border-gray-700 !text-white flex-1 min-w-[240px]"
        />
        <div className="flex items-center gap-2 text-gray-300">
          <span className="text-xs">Top-K: <span className="text-primary font-medium">{topK}</span></span>
          <Slider
            min={1}
            max={10}
            value={topK}
            onChange={setTopK}
            className="!w-32 !mb-0"
            tooltip={{ open: false }}
          />
        </div>
        <div className="flex items-center gap-2 text-gray-300">
          <ThunderboltOutlined className="text-yellow-500" />
          <span className="text-xs">重排序</span>
          <Switch checked={rerank} onChange={setRerank} size="small" />
        </div>
        <Button
          type="primary"
          icon={<SearchOutlined />}
          onClick={onSearch}
          loading={loading}
          className="!bg-primary"
        >
          检索
        </Button>
        <Button icon={<ReloadOutlined />} onClick={() => { setResult(null); setQuery('') }}>
          清空
        </Button>
      </div>

      {/* 结果区 */}
      <div className="flex-1 overflow-auto p-6 space-y-4">
        {loading ? (
          <div className="flex justify-center py-20">
            <Spin size="large" tip="检索中..." />
          </div>
        ) : !result ? (
          <Empty
            className="mt-20"
            description={<span className="text-gray-500">输入查询并点击检索按钮查看召回结果</span>}
          />
        ) : (
          <>
            {/* 召回片段卡片列表 */}
            <div>
              <div className="flex items-center justify-between mb-3">
                <h3 className="text-white text-sm font-semibold">
                  召回片段（{result.results?.length || 0} 条）
                </h3>
                <Text className="!text-gray-500 text-xs">查询：{result.query}</Text>
              </div>
              {result.results?.length === 0 ? (
                <Empty description="无召回结果" />
              ) : (
                <Row gutter={[12, 12]}>
                  {result.results.map((r: KbSearchResult, idx: number) => {
                    const vectorScore = scoreOf(result.vectorScores, r.chunkId, idx)
                    const keywordScore = scoreOf(result.keywordScores, r.chunkId, idx)
                    const fusedScore = scoreOf(result.fusedScores, r.chunkId, idx)
                    return (
                      <Col span={24} key={String(r.chunkId)}>
                        <Card
                          size="small"
                          className="!bg-dark-800 !border-gray-800"
                          styles={{ body: { padding: 12 } }}
                        >
                          <div className="flex items-start justify-between gap-3 mb-2">
                            <Space size="small" wrap>
                              <Tag color="blue" className="!m-0">#{idx + 1}</Tag>
                              <Space size={4}>
                                <FileTextOutlined className="text-gray-400" />
                                <Text className="!text-gray-200 text-sm">{r.fileName}</Text>
                              </Space>
                              <Tag className="!m-0">P.{r.pageNumber ?? 1}</Tag>
                              {r.source && <Tag color="cyan" className="!m-0">{r.source}</Tag>}
                            </Space>
                            <Space size={4} wrap>
                              <Tag color={SCORE_COLOR.vector} className="!m-0">向量 {vectorScore.toFixed(4)}</Tag>
                              <Tag color={SCORE_COLOR.keyword} className="!m-0">关键词 {keywordScore.toFixed(4)}</Tag>
                              <Tag color={SCORE_COLOR.fused} className="!m-0">融合 {fusedScore.toFixed(4)}</Tag>
                              <Tag color={SCORE_COLOR.reranked} className="!m-0">重排 {r.rerankedScore?.toFixed(4) ?? '0.0000'}</Tag>
                            </Space>
                          </div>
                          <Paragraph className="!text-gray-200 !mb-0 text-sm leading-relaxed whitespace-pre-wrap">
                            {highlight(r.content || '', keywords)}
                          </Paragraph>
                        </Card>
                      </Col>
                    )
                  })}
                </Row>
              )}
            </div>

            {/* 分数对比表格 */}
            <Card
              title={<span className="text-white">分数对比表</span>}
              className="!bg-dark-800 !border-gray-800"
              size="small"
            >
              <Table<ScoreRow>
                columns={scoreColumns}
                dataSource={scoreRows}
                rowKey="key"
                size="small"
                pagination={false}
                scroll={{ x: 700 }}
              />
            </Card>
          </>
        )}
      </div>
    </div>
  )
}
