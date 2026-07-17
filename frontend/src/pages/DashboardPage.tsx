// ============================================================
// DashboardPage - 仪表盘页面
// 顶部 4 个统计卡片 + 最近审计日志表格
// 数据源：GET /api/dashboard + GET /api/audit?limit=20
// ============================================================

import { useEffect, useState } from 'react'
import { Card, Row, Col, Statistic, Table, Tag, Button, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import {
  TeamOutlined, FlagOutlined, AuditOutlined, SolutionOutlined,
  ReloadOutlined,
} from '@ant-design/icons'
import { apiGet } from '../api'

// 仪表盘统计数据结构（对应 GET /api/dashboard 返回）
interface DashboardStats {
  customers: number
  today_followups: number
  pending_approvals: number
  tickets: number
  followups: number
}

// 审计日志条目类型（对应 GET /api/audit 返回数组元素）
interface AuditLog {
  id: number
  type: 'ToolCall' | 'Approval' | 'DataChange' | 'Auth'
  actor: string
  action: string
  detail: string
  result: string
  createdAt: string
}

// 审计类型 → Tag 颜色映射
const TYPE_COLOR: Record<AuditLog['type'], string> = {
  ToolCall: 'blue',
  Approval: 'orange',
  DataChange: 'green',
  Auth: 'purple',
}

export default function DashboardPage() {
  const [stats, setStats] = useState<DashboardStats | null>(null)
  const [logs, setLogs] = useState<AuditLog[]>([])
  const [loading, setLoading] = useState(false)

  // 拉取统计数据 + 审计日志（并发请求提升加载速度）
  const loadData = async () => {
    setLoading(true)
    try {
      const [s, l] = await Promise.all([
        apiGet<DashboardStats>('/api/dashboard'),
        apiGet<AuditLog[]>('/api/audit?limit=20'),
      ])
      setStats(s)
      setLogs(l)
    } catch (err: any) {
      message.error(`加载失败: ${err.message}`)
    } finally {
      setLoading(false)
    }
  }

  // 初始加载
  useEffect(() => {
    loadData()
  }, [])

  // 表格列定义：时间 / 类型(Tag) / 操作人 / 动作 / 结果
  const columns: ColumnsType<AuditLog> = [
    {
      title: '时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      // 时间字段格式化显示（toLocaleString）
      render: (t: string) => new Date(t).toLocaleString(),
    },
    {
      title: '类型',
      dataIndex: 'type',
      key: 'type',
      render: (t: AuditLog['type']) => (
        <Tag color={TYPE_COLOR[t]}>{t}</Tag>
      ),
    },
    {
      title: '操作人',
      dataIndex: 'actor',
      key: 'actor',
    },
    {
      title: '动作',
      dataIndex: 'action',
      key: 'action',
    },
    {
      title: '结果',
      dataIndex: 'result',
      key: 'result',
      // success → green Tag, 其它 → red Tag
      render: (r: string) => (
        <Tag color={r === 'success' ? 'green' : 'red'}>{r}</Tag>
      ),
    },
  ]

  return (
    <div className="p-6 space-y-6">
      {/* 页头：标题 + 刷新按钮 */}
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold text-white">仪表盘</h1>
        <Button
          icon={<ReloadOutlined />}
          onClick={loadData}
          loading={loading}
        >
          刷新
        </Button>
      </div>

      {/* 顶部 4 个统计卡片：客户总数 / 今日跟进 / 待审工单 / 工单总数 */}
      <Row gutter={[16, 16]}>
        <Col span={6}>
          <Card className="!bg-dark-800 !border-gray-800" loading={!stats && loading}>
            <Statistic
              title={<span className="text-gray-300">客户总数</span>}
              value={stats?.customers ?? 0}
              prefix={<TeamOutlined className="text-primary" />}
              valueStyle={{ color: '#4F46E5' }}
            />
          </Card>
        </Col>
        <Col span={6}>
          <Card className="!bg-dark-800 !border-gray-800" loading={!stats && loading}>
            <Statistic
              title={<span className="text-gray-300">今日跟进</span>}
              value={stats?.today_followups ?? 0}
              prefix={<FlagOutlined className="text-primary" />}
              valueStyle={{ color: '#4F46E5' }}
            />
          </Card>
        </Col>
        <Col span={6}>
          <Card className="!bg-dark-800 !border-gray-800" loading={!stats && loading}>
            <Statistic
              title={<span className="text-gray-300">待审工单</span>}
              value={stats?.pending_approvals ?? 0}
              prefix={<AuditOutlined className="text-primary" />}
              valueStyle={{ color: '#4F46E5' }}
            />
          </Card>
        </Col>
        <Col span={6}>
          <Card className="!bg-dark-800 !border-gray-800" loading={!stats && loading}>
            <Statistic
              title={<span className="text-gray-300">工单总数</span>}
              value={stats?.tickets ?? 0}
              prefix={<SolutionOutlined className="text-primary" />}
              valueStyle={{ color: '#4F46E5' }}
            />
          </Card>
        </Col>
      </Row>

      {/* 第二区域：最近审计日志表格 */}
      <Card
        title={<span className="text-white">最近审计日志</span>}
        className="!bg-dark-800 !border-gray-800"
      >
        <Table<AuditLog>
          columns={columns}
          dataSource={logs}
          rowKey="id"
          size="small"
          pagination={{ pageSize: 10, showSizeChanger: false }}
          loading={loading}
        />
      </Card>
    </div>
  )
}
