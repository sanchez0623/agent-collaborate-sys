// ============================================================
// TicketsPage - 工单看板页面
// 4 列看板：待处理 / 处理中 / 已完成 / 已驳回
// 管理员可流转状态，普通用户只读
// ============================================================

import { useEffect, useState } from 'react'
import {
  Button, Card, Modal, Form, Input, Select, Tag, message, Spin, Empty, Dropdown, Space, Typography,
} from 'antd'
import { PlusOutlined, ReloadOutlined, DownOutlined, UserOutlined, FieldTimeOutlined } from '@ant-design/icons'
import type { MenuProps } from 'antd'
import { apiGet, apiPost, apiPut } from '../api'
import { isAdmin, getCurrentUser } from '../auth'

// 工单状态枚举
type TicketStatus = 'Pending' | 'Processing' | 'Done' | 'Rejected'
// 优先级类型
type Priority = 'Low' | 'Medium' | 'High' | 'Urgent'

// 工单数据结构（本地定义，对应后端 Ticket 实体）
interface Ticket {
  id: number
  title: string
  description: string
  source: string
  assignee: string
  priority: Priority
  status: TicketStatus
  createdBy: string
  createdAt: string
  updatedAt: string
}

// 看板列配置：状态 -> 中文标题
const COLUMN_CONFIG: { status: TicketStatus; cn: string; color: string }[] = [
  { status: 'Pending',   cn: '待处理', color: 'gray'   },
  { status: 'Processing', cn: '处理中', color: 'blue'    },
  { status: 'Done',      cn: '已完成', color: 'green'  },
  { status: 'Rejected',  cn: '已驳回', color: 'red'    },
]

// 优先级配色映射
const PRIORITY_COLOR: Record<Priority, string> = {
  Low: 'default',
  Medium: 'blue',
  High: 'orange',
  Urgent: 'red',
}
const PRIORITY_CN: Record<Priority, string> = {
  Low: '低',
  Medium: '中',
  High: '高',
  Urgent: '紧急',
}

// 状态可流转的目标（管理员可选）
const NEXT_STATUS: Record<TicketStatus, TicketStatus[]> = {
  Pending:    ['Processing', 'Done', 'Rejected'],
  Processing: ['Done', 'Rejected', 'Pending'],
  Done:       ['Processing'],
  Rejected:   ['Pending'],
}

// 格式化时间
function fmt(s: string): string {
  if (!s) return '-'
  const d = new Date(s)
  if (isNaN(d.getTime())) return s
  return d.toLocaleString('zh-CN', { hour12: false })
}

export default function TicketsPage() {
  const [tickets, setTickets] = useState<Ticket[]>([])
  const [loading, setLoading] = useState(false)
  const [modalOpen, setModalOpen] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [form] = Form.useForm()
  const admin = isAdmin()
  const currentUser = getCurrentUser()

  // 加载工单列表
  const loadTickets = async () => {
    setLoading(true)
    try {
      const data = await apiGet<Ticket[]>('/api/tickets')
      setTickets(Array.isArray(data) ? data : [])
    } catch (e: any) {
      message.error(`加载工单失败: ${e?.message || e}`)
    } finally {
      setLoading(false)
    }
  }

  // 进入页面默认拉一次
  useEffect(() => { loadTickets() }, [])

  // 提交创建工单
  const onCreate = async (values: any) => {
    setSubmitting(true)
    try {
      const body = {
        title: values.title?.trim(),
        description: values.description?.trim() || '',
        assignee: values.assignee?.trim() || '',
        priority: values.priority || 'Medium',
        source: values.source?.trim() || '用户提交',
        createdBy: currentUser?.username || 'anonymous',
      }
      await apiPost('/api/tickets', body)
      message.success('工单创建成功')
      setModalOpen(false)
      form.resetFields()
      await loadTickets()
    } catch (e: any) {
      message.error(`创建失败: ${e?.message || e}`)
    } finally {
      setSubmitting(false)
    }
  }

  // 流转工单状态（仅管理员），status 通过 query 参数传递
  const onChangeStatus = async (ticket: Ticket, next: TicketStatus) => {
    try {
      // 后端签名：PUT /api/tickets/{id}/status?status=xxx
      await apiPut(`/api/tickets/${ticket.id}/status?status=${next}`)
      message.success(`已流转至「${COLUMN_CONFIG.find(c => c.status === next)?.cn}」`)
      // 本地乐观更新，避免等待
      setTickets(prev => prev.map(t =>
        t.id === ticket.id ? { ...t, status: next, updatedAt: new Date().toISOString() } : t
      ))
    } catch (e: any) {
      message.error(`流转失败: ${e?.message || e}`)
    }
  }

  // 构建流转下拉菜单
  const buildMenu = (ticket: Ticket): MenuProps => ({
    items: (NEXT_STATUS[ticket.status] || []).map(s => ({
      key: s,
      label: COLUMN_CONFIG.find(c => c.status === s)?.cn || s,
    })),
    onClick: ({ key }) => onChangeStatus(ticket, key as TicketStatus),
  })

  // 按状态分组
  const grouped = (status: TicketStatus) => tickets.filter(t => t.status === status)

  return (
    <div className="h-full flex flex-col bg-dark-900">
      {/* 顶部工具栏 */}
      <div className="flex items-center justify-between px-6 py-4 border-b border-gray-800 bg-dark-800/60">
        <div>
          <Typography.Title level={4} className="!text-white !mb-0">工单看板</Typography.Title>
          <Typography.Text className="!text-gray-500 text-xs">
            {admin ? '管理员模式 · 可流转状态' : '只读模式 · 仅可创建工单'}
            <span className="ml-2">共 {tickets.length} 条</span>
          </Typography.Text>
        </div>
        <Space>
          <Button icon={<ReloadOutlined />} onClick={loadTickets} loading={loading}>
            刷新
          </Button>
          <Button type="primary" icon={<PlusOutlined />} onClick={() => setModalOpen(true)} className="!bg-primary">
            创建工单
          </Button>
        </Space>
      </div>

      {/* 看板区：4 列横向排列，可横向滚动 */}
      <div className="flex-1 overflow-x-auto overflow-y-hidden">
        <Spin spinning={loading}>
          <div className="flex gap-4 p-4 h-full">
            {COLUMN_CONFIG.map(col => {
              const list = grouped(col.status)
              return (
                <div
                  key={col.status}
                  className="flex flex-col bg-dark-800/70 rounded-lg min-w-[260px] w-[280px] border border-gray-800"
                >
                  {/* 列头：状态中文 + 数量 */}
                  <div className="flex items-center justify-between px-3 py-2.5 border-b border-gray-800">
                    <Space size={6}>
                      <Tag color={col.color}>{col.cn}</Tag>
                    </Space>
                    <span className="text-xs text-gray-500">{list.length}</span>
                  </div>

                  {/* 列内工单卡片垂直排列，纵向滚动 */}
                  <div className="flex-1 overflow-y-auto p-2 space-y-2">
                    {list.length === 0 ? (
                      <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={<span className="text-gray-600 text-xs">暂无工单</span>} />
                    ) : (
                      list.map(t => (
                        <Card
                          key={t.id}
                          size="small"
                          className="!bg-dark-700 !border-gray-700 !text-gray-200"
                          styles={{ body: { padding: 10 } }}
                        >
                          {/* 标题 + 优先级 */}
                          <div className="flex items-start justify-between gap-2 mb-1.5">
                            <div className="font-semibold text-white text-sm leading-snug break-words flex-1">
                              {t.title}
                            </div>
                            <Tag color={PRIORITY_COLOR[t.priority]} className="!m-0 shrink-0">
                              {PRIORITY_CN[t.priority]}
                            </Tag>
                          </div>

                          {/* 描述：2 行截断 */}
                          {t.description && (
                            <div
                              className="text-xs text-gray-400 mb-2"
                              style={{
                                display: '-webkit-box',
                                WebkitLineClamp: 2,
                                WebkitBoxOrient: 'vertical',
                                overflow: 'hidden',
                              }}
                            >
                              {t.description}
                            </div>
                          )}

                          {/* 元信息 */}
                          <div className="text-[11px] text-gray-500 space-y-0.5 mb-2">
                            <div className="flex items-center gap-1">
                              <UserOutlined />
                              <span>{t.assignee || '未分派'}</span>
                            </div>
                            <div className="flex items-center gap-1">
                              <FieldTimeOutlined />
                              <span>{fmt(t.createdAt)}</span>
                            </div>
                            <div>来源: {t.source || '-'}</div>
                          </div>

                          {/* 管理员可流转状态 */}
                          {admin && (
                            <Dropdown menu={buildMenu(t)} trigger={['click']}>
                              <Button size="small" block className="!bg-dark-800 !border-gray-700 !text-gray-300">
                                <Space size={4}>
                                  流转状态
                                  <DownOutlined />
                                </Space>
                              </Button>
                            </Dropdown>
                          )}
                        </Card>
                      ))
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        </Spin>
      </div>

      {/* 创建工单 Modal */}
      <Modal
        title="创建工单"
        open={modalOpen}
        onCancel={() => { setModalOpen(false); form.resetFields() }}
        footer={null}
        width={520}
        destroyOnClose
      >
        <Form form={form} layout="vertical" onFinish={onCreate} initialValues={{ priority: 'Medium', source: '用户提交' }}>
          <Form.Item
            name="title"
            label="标题"
            rules={[{ required: true, message: '请输入工单标题' }]}
          >
            <Input placeholder="请输入工单标题" maxLength={100} />
          </Form.Item>

          <Form.Item name="description" label="描述">
            <Input.TextArea placeholder="详细描述（可选）" autoSize={{ minRows: 3, maxRows: 6 }} maxLength={500} />
          </Form.Item>

          <div className="flex gap-3">
            <Form.Item name="assignee" label="分派给" className="flex-1">
              <Input placeholder="处理人用户名（可选）" />
            </Form.Item>
            <Form.Item name="priority" label="优先级" className="w-32">
              <Select
                options={[
                  { value: 'Low',    label: '低' },
                  { value: 'Medium', label: '中' },
                  { value: 'High',   label: '高' },
                  { value: 'Urgent', label: '紧急' },
                ]}
              />
            </Form.Item>
          </div>

          <Form.Item name="source" label="来源">
            <Input placeholder="来源" />
          </Form.Item>

          <div className="flex justify-end gap-2">
            <Button onClick={() => { setModalOpen(false); form.resetFields() }}>取消</Button>
            <Button type="primary" htmlType="submit" loading={submitting} className="!bg-primary">提交</Button>
          </div>
        </Form>
      </Modal>
    </div>
  )
}
