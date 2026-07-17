// ============================================================
// Layout - 主工作台布局（顶栏导航 + 可伸缩侧边菜单 + 内容区）
// ============================================================

import { useState } from 'react'
import { Layout, Menu, Avatar, Dropdown, Tag, Space } from 'antd'
import {
  MessageOutlined, TeamOutlined, SolutionOutlined,
  AuditOutlined, DashboardOutlined, LogoutOutlined, RobotOutlined, UserOutlined,
  BookOutlined, SearchOutlined, ExperimentOutlined
} from '@ant-design/icons'
import { Outlet, useNavigate, useLocation } from 'react-router-dom'
import { getCurrentUser, logout, isAdmin } from '../auth'

const { Header, Sider, Content } = Layout

export default function AppLayout() {
  const nav = useNavigate()
  const loc = useLocation()
  const user = getCurrentUser()
  const [collapsed, setCollapsed] = useState(false)

  const menuItems = [
    { key: '/chat', icon: <MessageOutlined />, label: '对话' },
    // MVP-3 RAG 三件套：知识库 / 检索测试 / RAG 评测
    { key: '/knowledge', icon: <BookOutlined />, label: '知识库' },
    { key: '/retrieval-test', icon: <SearchOutlined />, label: '检索测试' },
    { key: '/rag-eval', icon: <ExperimentOutlined />, label: 'RAG评测' },
    { key: '/customers', icon: <TeamOutlined />, label: '客户管理' },
    { key: '/tickets', icon: <SolutionOutlined />, label: '工单看板' },
    ...(isAdmin()
      ? [{ key: '/approvals', icon: <AuditOutlined />, label: '审核待办' }]
      : []),
    { key: '/dashboard', icon: <DashboardOutlined />, label: '仪表盘' },
  ]

  const onMenu = (k: string) => nav(k)

  const onLogout = () => {
    logout()
    nav('/login')
  }

  return (
    <Layout className="h-screen">
      <Header className="!bg-dark-800 !px-4 flex items-center justify-between border-b border-gray-800">
        <div className="flex items-center gap-2">
          <RobotOutlined className="text-xl text-primary" />
          <span className="text-white font-semibold">MultiAgent System</span>
          <Tag color="blue" className="ml-2">MVP-3+4 · RAG+CRM</Tag>
        </div>
        <Dropdown menu={{
          items: [
            { key: 'logout', icon: <LogoutOutlined />, label: '退出登录', onClick: onLogout }
          ]
        }}>
          <Space className="cursor-pointer">
            <Avatar icon={<UserOutlined />} className="!bg-primary" size="small" />
            <span className="text-gray-200 text-sm">{user?.displayName || user?.username}</span>
            <Tag color={isAdmin() ? 'red' : 'green'}>{user?.role}</Tag>
          </Space>
        </Dropdown>
      </Header>
      <Layout>
        <Sider
          width={180}
          collapsedWidth={64}
          collapsible
          collapsed={collapsed}
          onCollapse={setCollapsed}
          className="!bg-dark-800/60 border-r border-gray-800"
        >
          <Menu
            mode="inline"
            selectedKeys={[loc.pathname]}
            items={menuItems}
            onClick={e => onMenu(e.key)}
            className="!bg-transparent !border-r-0"
            theme="dark"
            inlineCollapsed={collapsed}
          />
        </Sider>
        <Content className="!bg-dark-900 overflow-auto">
          <Outlet />
        </Content>
      </Layout>
    </Layout>
  )
}
