import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Vite配置
// 前端开发服务器5173端口，API请求代理到后端5000端口
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true
      }
    }
  }
})
