// ============================================================
// RagEvalPage - RAG 评测页（MVP-3 RAG）
// 功能：知识库选择 + 测试用例 CRUD + 运行评测 + 结果统计 + 详情表
// ============================================================

import { useState, useEffect, useCallback } from 'react'
import {
  Select, Button, Input, Card, Table, Tag, Space, message,
  Empty, Statistic, Row, Col, Modal, Form, Typography, Popconfirm
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import {
  ExperimentOutlined, PlusOutlined, DeleteOutlined, PlayCircleOutlined,
  ReloadOutlined
} from '@ant-design/icons'
import { kbApi, KbDatabase, KbEvalResponse, KbEvalDetail } from '../api'

const { Text, Paragraph } = Typography

// 测试用例
interface TestCase {
  id: string
  question: string
  expectedAnswer: string
}

export default function RagEvalPage() {
  // ---------- 状态 ----------
  const [databases, setDatabases] = useState<KbDatabase[]>([])
  const [dbId, setDbId] = useState<number | null>(null)
  const [testCases, setTestCases] = useState<TestCase[]>([])
  const [result, setResult] = useState<KbEvalResponse | null>(null)
  const [running, setRunning] = useState(false)

  // 添加用例 Modal
  const [addOpen, setAddOpen] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [form] = Form.useForm()

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
  // 添加测试用例 Modal
  // ============================================================
  const onAddCase = () => {
    form.resetFields()
    setAddOpen(true)
  }

  const onAddSubmit = async () => {
    try {
      const values = await form.validateFields()
      setSubmitting(true)
      setTestCases(prev => [
        ...prev,
        {
          id: crypto.randomUUID(),
          question: values.question,
          expectedAnswer: values.expectedAnswer,
        },
      ])
      setAddOpen(false)
      message.success('测试用例已添加')
    } catch (e: any) {
      if (e?.errorFields) return
      message.error(`添加失败: ${e.message}`)
    } finally {
      setSubmitting(false)
    }
  }

  // ============================================================
  // 删除测试用例
  // ============================================================
  const onDeleteCase = (id: string) => {
    setTestCases(prev => prev.filter(c => c.id !== id))
  }

  // ============================================================
  // 清空全部测试用例
  // ============================================================
  const onClearAll = () => {
    setTestCases([])
    setResult(null)
  }

  // ============================================================
  // 运行评测：POST /api/kb/eval
  // ============================================================
  const onRunEval = async () => {
    if (!dbId) {
      message.warning('请先选择知识库')
      return
    }
    if (testCases.length === 0) {
      message.warning('请先添加测试用例')
      return
    }
    setRunning(true)
    setResult(null)
    try {
      const resp = await kbApi.eval(
        dbId,
        testCases.map(c => ({ question: c.question, expectedAnswer: c.expectedAnswer }))
      )
      setResult(resp)
      message.success('评测完成')
    } catch (e: any) {
      message.error(`评测失败: ${e.message}`)
    } finally {
      setRunning(false)
    }
  }

  // ============================================================
  // 用例表格列定义
  // ============================================================
  const caseColumns: ColumnsType<TestCase> = [
    {
      title: '#',
      width: 50,
      render: (_: any, __: TestCase, idx: number) => (
        <span className="text-gray-400">{idx + 1}</span>
      ),
    },
    {
      title: 'Question',
      dataIndex: 'question',
      key: 'question',
      render: (q: string) => <Text className="!text-gray-200">{q}</Text>,
    },
    {
      title: 'ExpectedAnswer',
      dataIndex: 'expectedAnswer',
      key: 'expectedAnswer',
      render: (a: string) => (
        <Paragraph className="!text-gray-400 !mb-0 text-xs" ellipsis={{ rows: 2 }}>
          {a}
        </Paragraph>
      ),
    },
    {
      title: '操作',
      key: 'action',
      width: 80,
      render: (_: any, record: TestCase) => (
        <Button
          type="link"
          size="small"
          danger
          icon={<DeleteOutlined />}
          onClick={() => onDeleteCase(record.id)}
        />
      ),
    },
  ]

  // ============================================================
  // 评测详情表格列定义
  // ============================================================
  const detailColumns: ColumnsType<KbEvalDetail> = [
    {
      title: 'Question',
      dataIndex: 'question',
      key: 'question',
      width: 220,
      render: (q: string) => <Text className="!text-gray-200 text-xs">{q}</Text>,
    },
    {
      title: 'ExpectedAnswer',
      dataIndex: 'expectedAnswer',
      key: 'expectedAnswer',
      width: 200,
      render: (a: string) => (
        <Paragraph className="!text-gray-400 !mb-0 text-xs" ellipsis={{ rows: 2 }}>
          {a}
        </Paragraph>
      ),
    },
    {
      title: 'ActualAnswer',
      dataIndex: 'actualAnswer',
      key: 'actualAnswer',
      render: (a: string) => (
        <Paragraph className="!text-gray-300 !mb-0 text-xs" ellipsis={{ rows: 3, expandable: true, symbol: '展开' }}>
          {a || '-'}
        </Paragraph>
      ),
    },
    {
      title: '召回',
      dataIndex: 'retrieved',
      key: 'retrieved',
      width: 80,
      render: (v: boolean) => (
        <Tag color={v ? 'success' : 'error'}>{v ? '是' : '否'}</Tag>
      ),
    },
    {
      title: '准确',
      dataIndex: 'correct',
      key: 'correct',
      width: 80,
      render: (v: boolean) => (
        <Tag color={v ? 'success' : 'error'}>{v ? '是' : '否'}</Tag>
      ),
    },
  ]

  // 召回率/准确率百分比显示
  const recallPct = result ? (result.recallRate ?? 0) * 100 : 0
  const accuracyPct = result ? (result.accuracyRate ?? 0) * 100 : 0

  return (
    <div className="h-full flex flex-col bg-dark-900">
      {/* 顶部工具栏 */}
      <div className="flex items-center justify-between gap-3 px-6 py-4 border-b border-gray-800 bg-dark-800/60 flex-wrap">
        <div className="flex items-center gap-3 text-white">
          <ExperimentOutlined className="text-primary text-lg" />
          <span className="font-semibold">RAG 评测</span>
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
          <Tag color="blue">{testCases.length} 个用例</Tag>
        </div>
        <Space>
          <Button icon={<PlusOutlined />} onClick={onAddCase}>添加测试用例</Button>
          {testCases.length > 0 && (
            <Popconfirm
              title="清空全部用例？"
              okText="清空"
              cancelText="取消"
              okButtonProps={{ danger: true }}
              onConfirm={onClearAll}
            >
              <Button danger icon={<DeleteOutlined />}>清空</Button>
            </Popconfirm>
          )}
          <Button
            type="primary"
            icon={<PlayCircleOutlined />}
            onClick={onRunEval}
            loading={running}
            className="!bg-primary"
          >
            运行评测
          </Button>
        </Space>
      </div>

      {/* 主体 */}
      <div className="flex-1 overflow-auto p-6 space-y-4">
        {/* 顶部统计卡片：仅在结果存在时显示 */}
        {result && (
          <Row gutter={[16, 16]}>
            <Col span={8}>
              <Card className="!bg-dark-800 !border-gray-800">
                <Statistic
                  title={<span className="text-gray-300">总用例数</span>}
                  value={result.totalCases ?? 0}
                  prefix={<ExperimentOutlined className="text-primary" />}
                  valueStyle={{ color: '#4F46E5' }}
                />
              </Card>
            </Col>
            <Col span={8}>
              <Card className="!bg-dark-800 !border-gray-800">
                <Statistic
                  title={<span className="text-gray-300">召回率</span>}
                  value={recallPct}
                  precision={2}
                  suffix="%"
                  valueStyle={{ color: '#52c41a' }}
                />
              </Card>
            </Col>
            <Col span={8}>
              <Card className="!bg-dark-800 !border-gray-800">
                <Statistic
                  title={<span className="text-gray-300">准确率</span>}
                  value={accuracyPct}
                  precision={2}
                  suffix="%"
                  valueStyle={{ color: '#13c2c2' }}
                />
              </Card>
            </Col>
          </Row>
        )}

        {/* 测试用例列表 */}
        <Card
          title={<span className="text-white">测试用例</span>}
          className="!bg-dark-800 !border-gray-800"
          size="small"
          extra={
            <Button
              size="small"
              icon={<ReloadOutlined />}
              onClick={() => setResult(null)}
              disabled={!result}
            >
              清除结果
            </Button>
          }
        >
          <Table<TestCase>
            columns={caseColumns}
            dataSource={testCases}
            rowKey="id"
            size="small"
            pagination={false}
            locale={{ emptyText: '暂无测试用例，点击右上角"添加测试用例"' }}
          />
        </Card>

        {/* 评测详情表格 */}
        {result && (
          <Card
            title={<span className="text-white">评测详情</span>}
            className="!bg-dark-800 !border-gray-800"
            size="small"
          >
            <Table<KbEvalDetail>
              columns={detailColumns}
              dataSource={result.details || []}
              rowKey={(r, idx) => `${r.question}_${idx}`}
              size="small"
              pagination={{ pageSize: 10, showSizeChanger: true, showTotal: t => `共 ${t} 条` }}
              scroll={{ x: 900 }}
              locale={{ emptyText: <Empty description="无详情数据" /> }}
            />
          </Card>
        )}
      </div>

      {/* 添加测试用例 Modal */}
      <Modal
        title="添加测试用例"
        open={addOpen}
        onOk={onAddSubmit}
        onCancel={() => setAddOpen(false)}
        confirmLoading={submitting}
        okText="添加"
        cancelText="取消"
        destroyOnClose
        width={600}
      >
        <Form form={form} layout="vertical" preserve={false}>
          <Form.Item
            name="question"
            label="问题"
            rules={[{ required: true, message: '请输入问题' }]}
          >
            <Input.TextArea
              rows={2}
              placeholder="例如：产品 A 的最大并发用户数是多少？"
              className="!bg-dark-700 !border-gray-700 !text-white"
            />
          </Form.Item>
          <Form.Item
            name="expectedAnswer"
            label="期望答案"
            rules={[{ required: true, message: '请输入期望答案' }]}
          >
            <Input.TextArea
              rows={3}
              placeholder="例如：500"
              className="!bg-dark-700 !border-gray-700 !text-white"
            />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}
