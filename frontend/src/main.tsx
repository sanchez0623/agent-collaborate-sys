import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { ConfigProvider, theme } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import AppLayout from './components/Layout'
import LoginPage from './pages/LoginPage'
import ChatPage from './pages/ChatPage'
import CustomersPage from './pages/CustomersPage'
import TicketsPage from './pages/TicketsPage'
import ApprovalsPage from './pages/ApprovalsPage'
import DashboardPage from './pages/DashboardPage'
import KnowledgePage from './pages/KnowledgePage'
import RetrievalTestPage from './pages/RetrievalTestPage'
import RagEvalPage from './pages/RagEvalPage'
import { isLoggedIn } from './auth'
import './index.css'

// 路由守卫：未登录跳 /login
function PrivateRoute({ children }: { children: React.ReactNode }) {
  if (!isLoggedIn()) return <Navigate to="/login" replace />
  return <>{children}</>
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ConfigProvider
      locale={zhCN}
      theme={{
        algorithm: theme.darkAlgorithm,
        token: {
          colorPrimary: '#4F46E5',
          colorBgBase: '#0f0f0f',
        },
      }}
    >
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route element={<PrivateRoute><AppLayout /></PrivateRoute>}>
            <Route path="/chat" element={<ChatPage />} />
            <Route path="/knowledge" element={<KnowledgePage />} />
            <Route path="/retrieval-test" element={<RetrievalTestPage />} />
            <Route path="/rag-eval" element={<RagEvalPage />} />
            <Route path="/customers" element={<CustomersPage />} />
            <Route path="/tickets" element={<TicketsPage />} />
            <Route path="/approvals" element={<ApprovalsPage />} />
            <Route path="/dashboard" element={<DashboardPage />} />
          </Route>
          <Route path="*" element={<Navigate to={isLoggedIn() ? "/chat" : "/login"} replace />} />
        </Routes>
      </BrowserRouter>
    </ConfigProvider>
  </React.StrictMode>,
)
