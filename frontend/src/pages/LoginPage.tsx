// ============================================================
// LoginPage - 登录页（JWT 获取）
// 预置账户：admin/admin123（管理员） / alice/user123（普通用户）
// ============================================================

import { useState } from 'react'
import { Card, Input, Button, Form, message, Typography, Tag } from 'antd'
import { RobotOutlined, LockOutlined, UserOutlined } from '@ant-design/icons'
import { useNavigate } from 'react-router-dom'
import { apiPost } from '../api'
import { saveAuth, isLoggedIn } from '../auth'

export default function LoginPage() {
  const nav = useNavigate()
  const [loading, setLoading] = useState(false)

  // 已登录直接跳工作台
  if (isLoggedIn()) {
    nav('/chat')
    return null
  }

  const onSubmit = async (values: { username: string; password: string }) => {
    setLoading(true)
    try {
      const resp = await apiPost<{ token: string; username: string; role: string; displayName: string }>(
        '/api/auth/login', values
      )
      saveAuth({
        token: resp.token,
        username: resp.username,
        role: resp.role as 'User' | 'Admin',
        displayName: resp.displayName
      })
      message.success(`欢迎，${resp.displayName}`)
      nav('/chat')
    } catch (e: any) {
      message.error('登录失败：用户名或密码错误')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="h-screen flex items-center justify-center bg-dark-900">
      <Card className="!bg-dark-800 !border-gray-700 w-96 shadow-2xl">
        <div className="text-center mb-6">
          <RobotOutlined className="text-4xl text-primary mb-2" />
          <Typography.Title level={3} className="!text-white !mb-1">MultiAgent System</Typography.Title>
          <Typography.Text className="!text-gray-500 text-sm">MVP-4 · CRM集成 + 人工审核</Typography.Text>
        </div>
        <Form onFinish={onSubmit} size="large">
          <Form.Item name="username" rules={[{ required: true, message: '请输入用户名' }]}>
            <Input prefix={<UserOutlined />} placeholder="用户名" className="!bg-dark-700 !border-gray-700 !text-white" />
          </Form.Item>
          <Form.Item name="password" rules={[{ required: true, message: '请输入密码' }]}>
            <Input.Password prefix={<LockOutlined />} placeholder="密码" className="!bg-dark-700 !border-gray-700 !text-white" />
          </Form.Item>
          <Button type="primary" htmlType="submit" loading={loading} block className="!bg-primary !h-10">登录</Button>
        </Form>
        <div className="mt-4 text-center">
          <Tag color="red">管理员 admin / admin123</Tag>
          <Tag color="green">普通用户 alice / user123</Tag>
        </div>
      </Card>
    </div>
  )
}
