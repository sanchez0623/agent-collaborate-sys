// ============================================================
// api - 统一 fetch 封装，自动携带 JWT + 处理 401
// ============================================================

import { getToken, logout } from './auth'

// 走 vite proxy（空串即代表当前 origin）
export const API_BASE = ''

const BASE = API_BASE // 走 vite proxy

// 仅返回认证头（用于 FormData 上传等不能预设 Content-Type 的场景）
export function getAuthHeaders(): Record<string, string> {
  const token = getToken()
  return token ? { 'Authorization': `Bearer ${token}` } : {}
}

export async function apiFetch(path: string, options: RequestInit = {}) {
  const token = getToken()
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(options.headers as Record<string, string> || {}),
  }
  if (token) headers['Authorization'] = `Bearer ${token}`

  const resp = await fetch(`${BASE}${path}`, { ...options, headers })
  if (resp.status === 401) {
    // token 失效，登出跳登录
    logout()
    window.location.href = '/login'
    throw new Error('未授权，请重新登录')
  }
  return resp
}

export async function apiGet<T = any>(path: string): Promise<T> {
  const r = await apiFetch(path)
  if (!r.ok) throw new Error(`请求失败: ${r.status}`)
  return r.json()
}

export async function apiPost<T = any>(path: string, body?: any): Promise<T> {
  const r = await apiFetch(path, { method: 'POST', body: body ? JSON.stringify(body) : undefined })
  if (!r.ok) throw new Error(`请求失败: ${r.status}`)
  return r.json()
}

export async function apiPut(path: string, body?: any) {
  const r = await apiFetch(path, { method: 'PUT', body: body ? JSON.stringify(body) : undefined })
  if (!r.ok) throw new Error(`请求失败: ${r.status}`)
  return r
}

export async function apiDelete(path: string) {
  const r = await apiFetch(path, { method: 'DELETE' })
  if (!r.ok) throw new Error(`请求失败: ${r.status}`)
  return r
}

// ============================================================
// RAG 知识库 API（MVP-3）
// ============================================================

// 知识库实体
export interface KbDatabase {
  id: number
  name: string
  description?: string
  documentCount: number
  chunkCount: number
  createdAt: string
}

// 知识库文档实体
export interface KbDocument {
  id: number
  databaseId: number
  fileName: string
  fileSize: number
  fileType: string        // pdf/docx/txt/md
  status: string          // pending/processing/ready/failed
  chunkCount: number
  errorMessage?: string   // 失败原因（status=failed 时有值）
  createdAt: string
}

// 检索单条结果
export interface KbSearchResult {
  chunkId: string | number
  content: string
  fileName: string
  pageNumber: number
  score: number           // 综合分数
  rerankedScore: number   // 重排序分数
  source?: string
}

// 搜索响应
export interface KbSearchResponse {
  query: string
  results: KbSearchResult[]
  vectorScores?: Record<string, number> | number[]
  keywordScores?: Record<string, number> | number[]
  fusedScores?: Record<string, number> | number[]
}

// 评测单条结果
export interface KbEvalDetail {
  question: string
  expectedAnswer: string
  actualAnswer: string
  retrieved: boolean
  correct: boolean
}

// 评测响应
export interface KbEvalResponse {
  totalCases: number
  recallRate: number
  accuracyRate: number
  details: KbEvalDetail[]
}

// 知识库统计
export interface KbStats {
  totalDatabases: number
  totalDocuments: number
  totalChunks: number
  totalSearches: number
}

export const kbApi = {
  // 列出所有知识库
  listDatabases: () => apiGet<KbDatabase[]>('/api/kb/databases'),
  // 新建知识库
  createDatabase: (name: string, description: string) =>
    apiPost<KbDatabase>('/api/kb/databases', { name, description }),
  // 删除知识库
  deleteDatabase: (id: number) => apiDelete(`/api/kb/databases/${id}`),
  // 列出某知识库下的文档
  listDocuments: (dbId: number) =>
    apiGet<KbDocument[]>(`/api/kb/databases/${dbId}/documents`),
  // 上传文档（multipart/form-data，需绕过 apiFetch 的 JSON Content-Type）
  uploadDocument: async (dbId: number, file: File) => {
    const formData = new FormData()
    formData.append('file', file)
    const r = await fetch(`${API_BASE}/api/kb/databases/${dbId}/upload`, {
      method: 'POST',
      headers: getAuthHeaders(),
      body: formData,
    })
    // 先读文本，再安全解析 —— 避免后端返回空 body 时 r.json() 抛 "Unexpected end of JSON input"
    const text = await r.text()
    if (!r.ok) {
      const detail = text || `HTTP ${r.status} ${r.statusText}`
      // 尝试从 JSON 错误体中提取 message
      try {
        const err = JSON.parse(text)
        throw new Error(err.message || err.error || detail)
      } catch {
        throw new Error(detail)
      }
    }
    if (!text.trim()) throw new Error('服务器返回空响应')
    return JSON.parse(text)
  },
  // 删除文档
  deleteDocument: (id: number) => apiDelete(`/api/kb/documents/${id}`),
  // 重新解析文档
  reparseDocument: (id: number) =>
    apiPost(`/api/kb/documents/${id}/reparse`, {}),
  // 检索（topK + 是否启用重排序）
  search: (query: string, databaseId: number, topK: number, rerank: boolean) =>
    apiPost<KbSearchResponse>('/api/kb/search', { query, databaseId, topK, rerank }),
  // 评测
  eval: (databaseId: number, testCases: { question: string; expectedAnswer: string }[]) =>
    apiPost<KbEvalResponse>('/api/kb/eval', { databaseId, testCases }),
  // 全局统计
  stats: () => apiGet<KbStats>('/api/kb/stats'),
}
