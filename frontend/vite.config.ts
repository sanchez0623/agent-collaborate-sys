import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Vite配置
// 前端开发服务器5173端口，API请求代理到后端5000端口
// Docker 中通过 VITE_API_BASE 环境变量指向 backend 容器
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: process.env.VITE_API_BASE || 'http://localhost:5000',
        changeOrigin: true
      }
    }
  }
})
