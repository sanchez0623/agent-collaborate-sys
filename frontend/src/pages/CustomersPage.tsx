// ============================================================
// CustomersPage - 客户管理页面
// 功能：客户列表 / 关键字搜索 / 新建&编辑 Modal / 详情抽屉(含跟进记录) / 管理员删除
// ============================================================

import { useState, useEffect, useCallback } from 'react'
import {
  Table, Button, Input, Modal, Form, Select, Tag, Space,
  Drawer, Descriptions, message, Popconfirm, List, Divider, Typography
} from 'antd'
import {
  PlusOutlined, SearchOutlined, EyeOutlined, EditOutlined,
  DeleteOutlined, TeamOutlined
} from '@ant-design/icons'
import { apiGet, apiPost, apiPut, apiDelete } from '../api'
import { isAdmin, getCurrentUser } from '../auth'

// ---------- 本地类型定义（对应后端 CrmModels）----------
// 客户实体
export interface Customer {
  id: number
  name: string
  company: string
  phone: string
  email: string
  level: string        // 潜在/普通/重要/战略
  owner: string        // 负责人用户名
  remark?: string | null
  createdAt: string
  updatedAt: string
}

// 跟进记录实体
export interface FollowUp {
  id: number
  customerId: number
  method: string       // 电话/拜访/微信/邮件
  content: string      // 跟进内容摘要
  operator: string     // 跟进人用户名
  createdAt: string
}

// ---------- 等级配置：值 + Tag 颜色 ----------
const LEVEL_OPTIONS = [
  { value: '潜在', color: 'default' as const },
  { value: '普通', color: 'blue' as const },
  { value: '重要', color: 'orange' as const },
  { value: '战略', color: 'red' as const },
]

// 跟进方式可选项
const METHOD_OPTIONS = ['电话', '拜访', '微信', '邮件']

// 根据等级名取 Tag 颜色
function levelColor(level: string) {
  return LEVEL_OPTIONS.find(o => o.value === level)?.color ?? 'default'
}

export default function CustomersPage() {
  // ---------- 状态 ----------
  const [customers, setCustomers] = useState<Customer[]>([])   // 客户列表
  const [loading, setLoading] = useState(false)                // 表格加载中
  const [keyword, setKeyword] = useState('')                    // 搜索关键字

  // 新建/编辑 Modal
  const [modalOpen, setModalOpen] = useState(false)
  const [editing, setEditing] = useState<Customer | null>(null) // null=新建 否则编辑
  const [submitting, setSubmitting] = useState(false)
  const [form] = Form.useForm()

  // 详情抽屉
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [current, setCurrent] = useState<Customer | null>(null)  // 当前查看的客户
  const [followups, setFollowups] = useState<FollowUp[]>([])     // 跟进记录列表
  const [followupsLoading, setFollowupsLoading] = useState(false)
  const [followSubmitting, setFollowSubmitting] = useState(false)
  const [followForm] = Form.useForm()

  // ============================================================
  // 加载客户列表（带关键字）
  // ============================================================
  const loadCustomers = useCallback(async (kw?: string) => {
    setLoading(true)
    try {
      const path = kw ? `/api/crm/customers?keyword=${encodeURIComponent(kw)}` : '/api/crm/customers'
      const list = await apiGet<Customer[]>(path)
      setCustomers(Array.isArray(list) ? list : [])
    } catch (e: any) {
      message.error(`加载客户列表失败: ${e.message}`)
    } finally {
      setLoading(false)
    }
  }, [])

  // 默认进入加载一次
  useEffect(() => {
    loadCustomers()
  }, [loadCustomers])

  // ---------- 搜索 ----------
  const onSearch = () => {
    loadCustomers(keyword.trim())
  }

  // ============================================================
  // 打开新建 Modal
  // ============================================================
  const onNew = () => {
    setEditing(null)
    form.resetFields()
    // 默认等级普通、负责人默认当前用户
    form.setFieldsValue({ level: '普通', owner: getCurrentUser()?.username || '' })
    setModalOpen(true)
  }

  // 打开编辑 Modal
  const onEdit = (record: Customer) => {
    setEditing(record)
    form.setFieldsValue({
      name: record.name,
      company: record.company,
      phone: record.phone,
      email: record.email,
      level: record.level,
      owner: record.owner,
    })
    setModalOpen(true)
  }

  // ============================================================
  // 提交新建/编辑表单
  // ============================================================
  const onSubmit = async () => {
    try {
      const values = await form.validateFields()
      setSubmitting(true)
      if (editing) {
        // 编辑：PUT /api/crm/customers/{id}
        await apiPut(`/api/crm/customers/${editing.id}`, values)
        message.success('客户信息已更新')
      } else {
        // 新建：POST /api/crm/customers
        await apiPost('/api/crm/customers', values)
        message.success('客户创建成功')
      }
      setModalOpen(false)
      loadCustomers(keyword.trim())
    } catch (e: any) {
      // validateFields 抛出的不是网络错误时直接忽略
      if (e?.errorFields) return
      message.error(`保存失败: ${e.message}`)
    } finally {
      setSubmitting(false)
    }
  }

  // ============================================================
  // 删除客户（仅管理员）
  // ============================================================
  const onDelete = async (record: Customer) => {
    try {
      await apiDelete(`/api/crm/customers/${record.id}`)
      message.success(`已删除客户：${record.name}`)
      loadCustomers(keyword.trim())
    } catch (e: any) {
      message.error(`删除失败: ${e.message}`)
    }
  }

  // ============================================================
  // 打开详情抽屉 + 加载跟进记录
  // ============================================================
  const onViewDetail = async (record: Customer) => {
    setCurrent(record)
    setDrawerOpen(true)
    setFollowups([])
    await loadFollowups(record.id)
  }

  const loadFollowups = async (customerId: number) => {
    setFollowupsLoading(true)
    try {
      // GET /api/crm/customers/{id}/followups
      const list = await apiGet<FollowUp[]>(`/api/crm/customers/${customerId}/followups`)
      setFollowups(Array.isArray(list) ? list : [])
    } catch (e: any) {
      message.error(`加载跟进记录失败: ${e.message}`)
    } finally {
      setFollowupsLoading(false)
    }
  }

  // ============================================================
  // 添加跟进记录
  // ============================================================
  const onAddFollowup = async () => {
    if (!current) return
    try {
      const values = await followForm.validateFields()
      setFollowSubmitting(true)
      // POST /api/crm/customers/{id}/followups
      await apiPost(`/api/crm/customers/${current.id}/followups`, {
        method: values.method,
        content: values.content,
        operator: getCurrentUser()?.username || '',
      })
      message.success('跟进记录已添加')
      followForm.resetFields()
      followForm.setFieldsValue({ method: '电话' })
      await loadFollowups(current.id)
    } catch (e: any) {
      if (e?.errorFields) return
      message.error(`添加跟进失败: ${e.message}`)
    } finally {
      setFollowSubmitting(false)
    }
  }

  // ============================================================
  // 表格列定义
  // ============================================================
  const columns = [
    { title: 'ID', dataIndex: 'id', width: 70 },
    { title: '姓名', dataIndex: 'name', width: 120 },
    { title: '公司', dataIndex: 'company', width: 160 },
    { title: '电话', dataIndex: 'phone', width: 140 },
    { title: '邮箱', dataIndex: 'email', width: 200 },
    {
      title: '等级',
      dataIndex: 'level',
      width: 90,
      // 等级渲染为彩色 Tag
      render: (level: string) => <Tag color={levelColor(level)}>{level || '普通'}</Tag>,
    },
    { title: '负责人', dataIndex: 'owner', width: 120 },
    {
      title: '操作',
      key: 'action',
      width: 220,
      fixed: 'right' as const,
      render: (_: any, record: Customer) => (
        <Space size="small">
          <Button
            type="link"
            size="small"
            icon={<EyeOutlined />}
            onClick={() => onViewDetail(record)}
          >
            查看详情
          </Button>
          <Button
            type="link"
            size="small"
            icon={<EditOutlined />}
            onClick={() => onEdit(record)}
          >
            编辑
          </Button>
          {/* 仅管理员显示删除按钮 */}
          {isAdmin() && (
            <Popconfirm
              title="确认删除"
              description={`删除客户「${record.name}」？此操作不可恢复`}
              okText="删除"
              cancelText="取消"
              okButtonProps={{ danger: true }}
              onConfirm={() => onDelete(record)}
            >
              <Button type="link" size="small" danger icon={<DeleteOutlined />}>
                删除
              </Button>
            </Popconfirm>
          )}
        </Space>
      ),
    },
  ]

  // ============================================================
  // 渲染
  // ============================================================
  return (
    <div className="h-full flex flex-col bg-dark-900">
      {/* 顶部工具栏：搜索 + 新建 */}
      <div className="flex items-center justify-between gap-3 px-6 py-4 border-b border-gray-800 bg-dark-800/60">
        <div className="flex items-center gap-2 text-white">
          <TeamOutlined className="text-primary text-lg" />
          <span className="font-semibold">客户管理</span>
          <Tag color="blue" className="ml-2">共 {customers.length} 条</Tag>
        </div>
        <Space>
          <Input
            allowClear
            value={keyword}
            onChange={e => setKeyword(e.target.value)}
            onPressEnter={onSearch}
            placeholder="搜索姓名 / 公司 / 电话..."
            prefix={<SearchOutlined className="text-gray-500" />}
            className="!bg-dark-700 !border-gray-700 !text-white w-72"
          />
          <Button onClick={onSearch} icon={<SearchOutlined />}>搜索</Button>
          <Button type="primary" icon={<PlusOutlined />} onClick={onNew} className="!bg-primary">
            新建客户
          </Button>
        </Space>
      </div>

      {/* 客户表格 */}
      <div className="flex-1 overflow-auto p-6">
        <Table<Customer>
          rowKey="id"
          columns={columns as any}
          dataSource={customers}
          loading={loading}
          pagination={{ pageSize: 10, showSizeChanger: true, showTotal: t => `共 ${t} 条` }}
          scroll={{ x: 1100 }}
          className="!bg-dark-800 !border !border-gray-800 rounded-lg"
          // 暗色主题样式覆盖
          style={{ background: '#1a1a2e' }}
        />
      </div>

      {/* 新建/编辑 Modal */}
      <Modal
        title={editing ? `编辑客户 #${editing.id}` : '新建客户'}
        open={modalOpen}
        onOk={onSubmit}
        onCancel={() => setModalOpen(false)}
        confirmLoading={submitting}
        okText="保存"
        cancelText="取消"
        destroyOnClose
        width={520}
      >
        <Form form={form} layout="vertical" preserve={false}>
          <Form.Item
            name="name"
            label="姓名"
            rules={[{ required: true, message: '请输入姓名' }]}
          >
            <Input placeholder="请输入客户姓名" className="!bg-dark-700 !border-gray-700 !text-white" />
          </Form.Item>
          <Form.Item name="company" label="公司">
            <Input placeholder="公司名称" className="!bg-dark-700 !border-gray-700 !text-white" />
          </Form.Item>
          <Form.Item name="phone" label="电话">
            <Input placeholder="联系电话" className="!bg-dark-700 !border-gray-700 !text-white" />
          </Form.Item>
          <Form.Item name="email" label="邮箱">
            <Input placeholder="邮箱地址" className="!bg-dark-700 !border-gray-700 !text-white" />
          </Form.Item>
          <Form.Item name="level" label="等级" rules={[{ required: true, message: '请选择等级' }]}>
            <Select placeholder="选择客户等级">
              {LEVEL_OPTIONS.map(o => (
                <Select.Option key={o.value} value={o.value}>
                  <Tag color={o.color}>{o.value}</Tag>
                </Select.Option>
              ))}
            </Select>
          </Form.Item>
          <Form.Item name="owner" label="负责人">
            <Input placeholder="负责人用户名" className="!bg-dark-700 !border-gray-700 !text-white" />
          </Form.Item>

          {/* 编辑时只读展示时间字段 */}
          {editing && (
            <>
              <Divider className="!border-gray-700" />
              <Descriptions size="small" column={1} className="!text-gray-400">
                <Descriptions.Item label="创建时间">
                  <span className="text-gray-300">{editing.createdAt?.replace('T', ' ').slice(0, 19)}</span>
                </Descriptions.Item>
                <Descriptions.Item label="更新时间">
                  <span className="text-gray-300">{editing.updatedAt?.replace('T', ' ').slice(0, 19)}</span>
                </Descriptions.Item>
              </Descriptions>
            </>
          )}
        </Form>
      </Modal>

      {/* 详情抽屉：客户信息 + 跟进记录 + 添加跟进 */}
      <Drawer
        title={current ? `客户详情 #${current.id} · ${current.name}` : '客户详情'}
        open={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        width={560}
        destroyOnClose
      >
        {current && (
          <div className="space-y-5">
            {/* 客户基本信息 */}
            <Descriptions
              title={<Typography.Text className="!text-white">基本信息</Typography.Text>}
              bordered
              size="small"
              column={2}
              className="!bg-dark-800"
              labelStyle={{ background: '#16213e', color: '#9ca3af', width: 90 }}
              contentStyle={{ color: '#e5e7eb' }}
            >
              <Descriptions.Item label="姓名">{current.name}</Descriptions.Item>
              <Descriptions.Item label="等级">
                <Tag color={levelColor(current.level)}>{current.level || '普通'}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label="公司">{current.company || '-'}</Descriptions.Item>
              <Descriptions.Item label="负责人">{current.owner || '-'}</Descriptions.Item>
              <Descriptions.Item label="电话">{current.phone || '-'}</Descriptions.Item>
              <Descriptions.Item label="邮箱">{current.email || '-'}</Descriptions.Item>
              <Descriptions.Item label="创建时间" span={2}>
                {current.createdAt?.replace('T', ' ').slice(0, 19)}
              </Descriptions.Item>
              <Descriptions.Item label="更新时间" span={2}>
                {current.updatedAt?.replace('T', ' ').slice(0, 19)}
              </Descriptions.Item>
            </Descriptions>

            {/* 跟进记录列表 */}
            <div>
              <div className="flex items-center justify-between mb-2">
                <Typography.Text className="!text-white font-semibold">
                  跟进记录 ({followups.length})
                </Typography.Text>
                <Button
                  size="small"
                  type="link"
                  onClick={() => loadFollowups(current.id)}
                >
                  刷新
                </Button>
              </div>
              <List<FollowUp>
                size="small"
                loading={followupsLoading}
                dataSource={followups}
                locale={{ emptyText: '暂无跟进记录' }}
                renderItem={item => (
                  <List.Item className="!border-gray-700 !px-2">
                    <div className="w-full">
                      <div className="flex items-center justify-between">
                        <Space>
                          <Tag color="blue">{item.method}</Tag>
                          <span className="text-gray-300 text-sm">{item.operator}</span>
                        </Space>
                        <span className="text-xs text-gray-500">
                          {item.createdAt?.replace('T', ' ').slice(0, 16)}
                        </span>
                      </div>
                      <div className="text-gray-200 text-sm mt-1 whitespace-pre-wrap">
                        {item.content}
                      </div>
                    </div>
                  </List.Item>
                )}
              />
            </div>

            {/* 添加跟进表单 */}
            <Divider className="!border-gray-700 !my-3" />
            <Typography.Text className="!text-white font-semibold">添加跟进</Typography.Text>
            <Form form={followForm} layout="vertical" initialValues={{ method: '电话' }}>
              <Form.Item name="method" label="跟进方式" rules={[{ required: true }]}>
                <Select>
                  {METHOD_OPTIONS.map(m => (
                    <Select.Option key={m} value={m}>{m}</Select.Option>
                  ))}
                </Select>
              </Form.Item>
              <Form.Item
                name="content"
                label="跟进内容"
                rules={[{ required: true, message: '请输入跟进内容' }]}
              >
                <Input.TextArea
                  rows={3}
                  placeholder="记录本次跟进的关键信息..."
                  className="!bg-dark-700 !border-gray-700 !text-white"
                />
              </Form.Item>
              <Button
                type="primary"
                onClick={onAddFollowup}
                loading={followSubmitting}
                className="!bg-primary"
              >
                添加跟进
              </Button>
            </Form>
          </div>
        )}
      </Drawer>
    </div>
  )
}
