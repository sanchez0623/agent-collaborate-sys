// ============================================================
// ApprovalsPage - 审核待办页（管理员审核敏感操作请求）
// 顶部 Tab 切换状态，列表展示审核请求，支持通过/拒绝/修改后通过
// ============================================================

import { useState, useEffect, useCallback } from 'react'
import {
  Tabs, List, Card, Tag, Button, Space, Spin, Modal, Input,
  Typography, message, Empty, Tooltip,
} from 'antd'
import {
  CheckOutlined, CloseOutlined, EditOutlined, ReloadOutlined,
  AuditOutlined, ClockCircleOutlined,
} from '@ant-design/icons'
import { apiGet, apiPost } from '../api'
import { isAdmin } from '../auth'

const { Text, Paragraph } = Typography
const { TextArea } = Input

// ------------------------------------------------------------
// 类型定义：审核请求
// ------------------------------------------------------------
interface ApprovalRequest {
  id: number
  sessionId: string
  agent: string
  system: string
  action: string              // 操作描述（对应 SSE 的 approvalAction）
  parameters: string          // 操作参数 JSON 字符串
  riskLevel: '低' | '中' | '高' | string
  status: 'Pending' | 'Approved' | 'Rejected' | 'Modified'
  reviewer: string | null
  reviewComment: string | null
  createdAt: string
  reviewedAt: string | null
}

// Tab 状态过滤选项
type StatusFilter = 'Pending' | 'Approved' | 'Rejected' | 'All'

// 决策类型
type Decision = 'approved' | 'rejected' | 'modified'

// ------------------------------------------------------------
// 工具函数：风险等级 → Tag 颜色
// ------------------------------------------------------------
function riskTagColor(risk: string): string {
  if (risk === '低') return 'green'
  if (risk === '中') return 'orange'
  if (risk === '高') return 'red'
  return 'default'
}

// 状态 → Tag 颜色（antd 内置语义色）
function statusTagColor(status: ApprovalRequest['status']): string {
  switch (status) {
    case 'Pending':   return 'processing' // orange
    case 'Approved':  return 'success'    // green
    case 'Rejected':  return 'error'      // red
    case 'Modified':  return 'warning'    // yellow
    default:          return 'default'
  }
}

// 状态中文文案
function statusLabel(status: ApprovalRequest['status']): string {
  switch (status) {
    case 'Pending':   return '待审核'
    case 'Approved':  return '已通过'
    case 'Rejected':  return '已拒绝'
    case 'Modified':  return '已修改通过'
    default:          return status
  }
}

// 格式化时间
function fmtTime(iso: string | null): string {
  if (!iso) return '-'
  try {
    return new Date(iso).toLocaleString('zh-CN', { hour12: false })
  } catch {
    return iso
  }
}

// ============================================================
// ApprovalsPage 主组件
// ============================================================
export default function ApprovalsPage() {
  // 非管理员直接显示提示
  const admin = isAdmin()

  const [activeTab, setActiveTab] = useState<StatusFilter>('Pending')
  const [list, setList] = useState<ApprovalRequest[]>([])
  const [loading, setLoading] = useState(false)

  // "修改后通过" 弹窗状态
  const [editModal, setEditModal] = useState<{
    open: boolean
    item: ApprovalRequest | null
    params: string
    comment: string
    submitting: boolean
  }>({ open: false, item: null, params: '', comment: '', submitting: false })

  // ----------------------------------------------------------
  // 拉取列表：根据 activeTab 拼接 status 查询参数
  // ----------------------------------------------------------
  const fetchList = useCallback(async () => {
    setLoading(true)
    try {
      const query = activeTab === 'All' ? '' : `?status=${activeTab}`
      const data = await apiGet<ApprovalRequest[] | { items: ApprovalRequest[] }>(
        `/api/approvals${query}`
      )
      // 兼容数组或 { items: [] } 两种返回结构
      const items = Array.isArray(data) ? data : (data?.items ?? [])
      setList(items)
    } catch (e: any) {
      message.error('加载审核列表失败：' + e.message)
      setList([])
    } finally {
      setLoading(false)
    }
  }, [activeTab])

  // Tab 变化或首次进入时拉取
  useEffect(() => {
    if (admin) fetchList()
  }, [admin, fetchList])

  // ----------------------------------------------------------
  // 提交审核决策（通过/拒绝/修改后通过 共用）
  // ----------------------------------------------------------
  const submitDecision = async (
    item: ApprovalRequest,
    decision: Decision,
    modifiedParameters?: string,
    comment?: string
  ) => {
    try {
      await apiPost('/api/approvals/decide', {
        ApprovalId: item.id,
        Decision: decision,
        ModifiedParameters: decision === 'modified' ? modifiedParameters : null,
        Comment: comment ?? null,
      })
      const label =
        decision === 'approved' ? '已通过' :
        decision === 'rejected' ? '已拒绝' : '已修改后通过'
      message.success(`操作成功：${label}`)
      // 操作完成后刷新列表
      await fetchList()
    } catch (e: any) {
      message.error('提交决策失败：' + e.message)
      throw e
    }
  }

  // ----------------------------------------------------------
  // 通过 / 拒绝 直接调用
  // ----------------------------------------------------------
  const handleApprove = (item: ApprovalRequest) => {
    submitDecision(item, 'approved').catch(() => {})
  }

  const handleReject = (item: ApprovalRequest) => {
    Modal.confirm({
      title: '确认拒绝该审核请求？',
      content: `操作：${item.action}`,
      okText: '确认拒绝',
      cancelText: '取消',
      okButtonProps: { danger: true },
      onOk: () => submitDecision(item, 'rejected').then(() => {}).catch(() => {}),
    })
  }

  // ----------------------------------------------------------
  // 修改后通过：打开 Modal 编辑参数
  // ----------------------------------------------------------
  const openEditModal = (item: ApprovalRequest) => {
    setEditModal({
      open: true,
      item,
      params: item.parameters || '{}',
      comment: '',
      submitting: false,
    })
  }

  const closeEditModal = () => {
    setEditModal(prev => ({ ...prev, open: false, submitting: false }))
  }

  // 提交"修改后通过"
  const submitModified = async () => {
    const { item, params, comment } = editModal
    if (!item) return
    // 校验 JSON 合法性
    try {
      JSON.parse(params)
    } catch {
      message.error('参数 JSON 格式不正确，请检查')
      return
    }
    setEditModal(prev => ({ ...prev, submitting: true }))
    try {
      await submitDecision(item, 'modified', params, comment)
      closeEditModal()
    } catch {
      setEditModal(prev => ({ ...prev, submitting: false }))
    }
  }

  // ----------------------------------------------------------
  // 非管理员提示
  // ----------------------------------------------------------
  if (!admin) {
    return (
      <div className="h-full flex items-center justify-center bg-dark-900 p-6">
        <Card className="!bg-dark-800 !border-gray-700 max-w-md w-full text-center">
          <div className="text-5xl mb-4">🔒</div>
          <Typography.Title level={3} className="!text-white !mb-2">权限不足</Typography.Title>
          <Typography.Paragraph className="!text-gray-400">
            仅管理员可审核。当前账号无审核权限，请联系系统管理员。
          </Typography.Paragraph>
        </Card>
      </div>
    )
  }

  // ----------------------------------------------------------
  // 渲染单个审核请求 Card
  // ----------------------------------------------------------
  const renderItem = (item: ApprovalRequest) => {
    // 参数 JSON 折叠展示
    let prettyParams = item.parameters || '{}'
    try { prettyParams = JSON.stringify(JSON.parse(item.parameters), null, 2) } catch { /* 保持原样 */ }

    return (
      <Card
        key={item.id}
        className="!bg-dark-800 !border-gray-700 mb-3"
        styles={{ body: { padding: 16 } }}
      >
        {/* 头部：操作描述 + 状态/风险 Tag */}
        <div className="flex items-start justify-between gap-3 mb-3">
          <div className="flex-1 min-w-0">
            <div className="flex items-center gap-2 mb-1">
              <AuditOutlined className="text-primary" />
              <Text className="!text-white font-medium truncate">{item.action}</Text>
            </div>
            <Space size="small" wrap>
              <Tag color="blue">#{item.id}</Tag>
              <Tag color={statusTagColor(item.status)}>{statusLabel(item.status)}</Tag>
              <Tag color={riskTagColor(item.riskLevel)}>风险：{item.riskLevel}</Tag>
            </Space>
          </div>
        </div>

        {/* 元信息：Agent / 系统 / 时间 */}
        <div className="grid grid-cols-2 gap-2 text-xs text-gray-400 mb-3">
          <div>🤖 Agent：<span className="text-gray-200">{item.agent || '-'}</span></div>
          <div>🧩 系统：<span className="text-gray-200">{item.system || '-'}</span></div>
          <div>
            <ClockCircleOutlined className="mr-1" />
            创建：<span className="text-gray-200">{fmtTime(item.createdAt)}</span>
          </div>
          <div>
            审核：<span className="text-gray-200">{fmtTime(item.reviewedAt)}</span>
          </div>
          <div className="col-span-2">
            会话：<span className="text-gray-200 font-mono">{item.sessionId?.slice(0, 16) || '-'}...</span>
          </div>
          {item.reviewer && (
            <div>审核人：<span className="text-gray-200">{item.reviewer}</span></div>
          )}
          {item.reviewComment && (
            <div className="col-span-2">
              审核备注：<span className="text-gray-200">{item.reviewComment}</span>
            </div>
          )}
        </div>

        {/* 参数 JSON 折叠展示 */}
        <Paragraph className="!mb-3 !text-xs" type="secondary">
          操作参数：
        </Paragraph>
        <pre className="bg-dark-900 border border-gray-700 rounded p-2 text-xs text-gray-300 font-mono whitespace-pre-wrap break-all max-h-40 overflow-auto mb-3">
          {prettyParams}
        </pre>

        {/* 待审核状态显示操作按钮 */}
        {item.status === 'Pending' && (
          <Space wrap>
            <Button
              type="primary" size="small" icon={<CheckOutlined />}
              onClick={() => handleApprove(item)}
              className="!bg-green-600 !border-green-600"
            >通过</Button>
            <Button
              danger size="small" icon={<CloseOutlined />}
              onClick={() => handleReject(item)}
            >拒绝</Button>
            <Button
              size="small" icon={<EditOutlined />}
              onClick={() => openEditModal(item)}
            >修改后通过</Button>
          </Space>
        )}
      </Card>
    )
  }

  // ----------------------------------------------------------
  // 页面渲染
  // ----------------------------------------------------------
  return (
    <div className="h-full flex flex-col bg-dark-900">
      {/* 顶部标题栏 */}
      <header className="h-16 border-b border-gray-800 flex items-center justify-between px-6 bg-dark-800/80 backdrop-blur">
        <div className="flex items-center gap-3">
          <AuditOutlined className="text-2xl text-primary" />
          <div>
            <h1 className="text-base font-semibold text-white">审核待办</h1>
            <span className="text-[10px] text-gray-500">人工审核 · Human-in-the-Loop</span>
          </div>
        </div>
        <Space>
          <Tag color="blue">共 {list.length} 条</Tag>
          <Tooltip title="刷新">
            <Button
              size="small" type="text" icon={<ReloadOutlined />}
              onClick={fetchList} loading={loading}
              className="!text-gray-400 hover:!text-primary"
            />
          </Tooltip>
        </Space>
      </header>

      {/* Tab 切换 */}
      <div className="border-b border-gray-800 bg-dark-800/40 px-6">
        <Tabs
          activeKey={activeTab}
          onChange={(k) => setActiveTab(k as StatusFilter)}
          items={[
            { key: 'Pending',   label: '待审核 (Pending)' },
            { key: 'Approved',  label: '已通过 (Approved)' },
            { key: 'Rejected',  label: '已拒绝 (Rejected)' },
            { key: 'All',       label: '全部' },
          ]}
          className="!mb-0"
        />
      </div>

      {/* 列表区域 */}
      <main className="flex-1 overflow-y-auto px-6 py-4">
        <div className="max-w-4xl mx-auto">
          {loading ? (
            <div className="flex justify-center py-12">
              <Spin size="large" />
            </div>
          ) : list.length === 0 ? (
            <Empty
              description={<span className="text-gray-500">暂无审核请求</span>}
              className="mt-16"
            />
          ) : (
            <List
              dataSource={list}
              renderItem={renderItem}
              split={false}
            />
          )}
        </div>
      </main>

      {/* "修改后通过" 参数编辑 Modal */}
      <Modal
        open={editModal.open}
        onCancel={closeEditModal}
        onOk={submitModified}
        confirmLoading={editModal.submitting}
        okText="提交修改后通过"
        cancelText="取消"
        title={
          <Space>
            <EditOutlined style={{ color: '#faad14' }} />
            <span>修改参数后通过 - 审核 #{editModal.item?.id}</span>
          </Space>
        }
        width={620}
      >
        {editModal.item && (
          <div className="space-y-3">
            <div>
              <Text type="secondary">操作描述：</Text>
              <Paragraph className="!mb-0 !text-white font-medium">
                {editModal.item.action}
              </Paragraph>
            </div>
            <div>
              <Text type="secondary">风险等级：</Text>
              <Tag color={riskTagColor(editModal.item.riskLevel)}>{editModal.item.riskLevel}</Tag>
            </div>
            <div>
              <Text type="secondary">修改参数（JSON）：</Text>
              <TextArea
                value={editModal.params}
                onChange={(e) => setEditModal(prev => ({ ...prev, params: e.target.value }))}
                rows={8}
                className="!bg-dark-700 !border-gray-700 !text-white mt-1 font-mono !text-xs"
              />
              <Text type="secondary" className="!text-[11px]">
                请确保 JSON 格式合法，提交后将以此参数执行操作。
              </Text>
            </div>
            <div>
              <Text type="secondary">审核备注：</Text>
              <TextArea
                value={editModal.comment}
                onChange={(e) => setEditModal(prev => ({ ...prev, comment: e.target.value }))}
                rows={2}
                placeholder="可选，填写修改说明或审核意见"
                className="!bg-dark-700 !border-gray-700 !text-white mt-1"
              />
            </div>
          </div>
        )}
      </Modal>
    </div>
  )
}
