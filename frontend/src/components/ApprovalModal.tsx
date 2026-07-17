// ============================================================
// ApprovalModal - 人审弹窗（Human-in-the-Loop 核心 UI）
// 收到 approval_required 事件时弹出，管理员决策后回传后端
// ============================================================

import { Modal, Input, Radio, Space, Typography, Tag, message, Collapse, Descriptions } from 'antd'
import { useState, useEffect } from 'react'
import { ExclamationCircleOutlined } from '@ant-design/icons'
import { OrchestrationEventPayload } from '../types'
import { apiPost } from '../api'
import { isAdmin } from '../auth'

const { TextArea } = Input
const { Text, Paragraph } = Typography

// 参数 key 中文映射表（覆盖 CRM 常见字段，未命中原样返回）
const PARAM_CN: Record<string, string> = {
  customerId: '客户ID',
  name: '客户名',
  company: '公司',
  phone: '电话',
  email: '邮箱',
  level: '等级',
  owner: '负责人',
  reason: '原因',
  method: '跟进方式',
  content: '跟进内容',
  operator: '操作人',
  keyword: '搜索关键词'
}

// 等级中文映射（若值是潜在/普通/重要/战略 等）
const LEVEL_CN: Record<string, string> = {
  potential: '潜在', normal: '普通', important: '重要', strategic: '战略'
}

// 把 JSON 字符串解析为 {key, label, value} 列表，用于友好展示
function parseParams(json?: string | null): { label: string; value: string }[] {
  if (!json) return []
  try {
    const obj = JSON.parse(json)
    return Object.entries(obj).map(([k, v]) => {
      const label = PARAM_CN[k] ?? k
      let value = typeof v === 'object' ? JSON.stringify(v) : String(v)
      // 等级值翻译
      if (k === 'level' && LEVEL_CN[value.toLowerCase()]) value = LEVEL_CN[value.toLowerCase()]
      return { label, value }
    })
  } catch {
    return [{ label: '原始参数', value: json }]
  }
}

// 判断是否为"参数不可改"的操作（删除类：标识符不可改）
// 这类操作隐藏"修改参数后通过"选项
function isImmutableAction(action?: string | null): boolean {
  if (!action) return false
  return action.includes('删除') || action.toLowerCase().includes('delete')
}

export function ApprovalModal({
  approvalEvent,
  onClose
}: {
  approvalEvent: OrchestrationEventPayload | null
  onClose: () => void
}) {
  const [decision, setDecision] = useState<'approved' | 'rejected' | 'modified'>('approved')
  const [comment, setComment] = useState('')
  const [modifiedParams, setModifiedParams] = useState('')
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => {
    if (approvalEvent) {
      setDecision('approved')
      setComment('')
      setModifiedParams(approvalEvent.approvalParams || '')
    }
  }, [approvalEvent])

  if (!approvalEvent?.approvalId) return null

  const onSubmit = async () => {
    setSubmitting(true)
    try {
      await apiPost('/api/approvals/decide', {
        ApprovalId: approvalEvent.approvalId,
        Decision: decision,
        ModifiedParameters: decision === 'modified' ? modifiedParams : null,
        Comment: comment
      })
      message.success(`已${decision === 'approved' ? '通过' : decision === 'rejected' ? '拒绝' : '修改后通过'}`)
      onClose()
    } catch (e: any) {
      message.error('提交失败：' + e.message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Modal
      open={!!approvalEvent}
      onCancel={onClose}
      onOk={onSubmit}
      confirmLoading={submitting}
      okText="提交决策"
      cancelText="取消"
      title={
        <Space>
          <ExclamationCircleOutlined style={{ color: '#faad14' }} />
          <span>人工审核 - 敏感操作待确认</span>
        </Space>
      }
      width={560}
    >
      {!isAdmin() && (
        <div className="bg-red-900/20 border border-red-700/50 rounded p-2 mb-3 text-xs text-red-300">
          ⚠️ 仅管理员可执行审核决策。当前为普通用户，请联系管理员。
        </div>
      )}

      <div className="space-y-3">
        <div>
          <Text type="secondary">操作描述：</Text>
          <Paragraph className="!mb-0 !text-white font-medium">
            {approvalEvent.approvalAction}
          </Paragraph>
        </div>

        <div>
          <Text type="secondary">风险等级：</Text>
          <Tag color="red">{approvalEvent.reason || '高'}</Tag>
        </div>

        <div>
          <Text type="secondary">操作详情：</Text>
          {parseParams(approvalEvent.approvalParams).length > 0 ? (
            <Descriptions
              size="small"
              column={1}
              bordered
              className="mt-1 !bg-dark-900"
              items={parseParams(approvalEvent.approvalParams).map(p => ({
                label: p.label,
                children: <span className="text-white">{p.value}</span>
              }))}
            />
          ) : (
            <div className="text-xs text-gray-500 mt-1">无参数</div>
          )}
          {/* 原始 JSON 折叠，给技术用户/调试用 */}
          {approvalEvent.approvalParams && (
            <Collapse
              ghost
              size="small"
              className="!mt-1"
              items={[{
                key: 'raw',
                label: <span className="text-[10px] text-gray-600">查看原始 JSON</span>,
                children: (
                  <pre className="bg-dark-900 border border-gray-700 rounded p-2 text-[10px] text-gray-500 font-mono whitespace-pre-wrap break-all max-h-32 overflow-auto">
                    {approvalEvent.approvalParams}
                  </pre>
                )
              }]}
            />
          )}
        </div>

        <div>
          <Text type="secondary">审核决策：</Text>
          <Radio.Group
            value={decision}
            onChange={e => setDecision(e.target.value)}
            className="mt-1"
            disabled={!isAdmin()}
          >
            <Space direction="vertical">
              <Radio value="approved">✅ 通过 - 执行该操作</Radio>
              <Radio value="rejected">❌ 拒绝 - 不执行</Radio>
              {/* 仅对参数可改的操作（如新建/修改/发送）显示"修改后通过"，
                  删除类操作的参数（customerId 等标识符）不可改，隐藏此选项 */}
              {!isImmutableAction(approvalEvent.approvalAction) && (
                <Radio value="modified">✏️ 修改参数后通过</Radio>
              )}
            </Space>
          </Radio.Group>
          {isImmutableAction(approvalEvent.approvalAction) && (
            <div className="text-[10px] text-gray-500 mt-1">
              提示：删除类操作的参数不可修改，请选择通过或拒绝
            </div>
          )}
        </div>

        {decision === 'modified' && (
          <div>
            <Text type="secondary">修改后的参数：</Text>
            <TextArea
              value={modifiedParams}
              onChange={e => setModifiedParams(e.target.value)}
              rows={4}
              className="!bg-dark-700 !border-gray-700 !text-white mt-1 font-mono !text-xs"
            />
          </div>
        )}

        <div>
          <Text type="secondary">审核备注：</Text>
          <TextArea
            value={comment}
            onChange={e => setComment(e.target.value)}
            rows={2}
            placeholder="可选，填写审核意见"
            className="!bg-dark-700 !border-gray-700 !text-white mt-1"
          />
        </div>
      </div>
    </Modal>
  )
}
