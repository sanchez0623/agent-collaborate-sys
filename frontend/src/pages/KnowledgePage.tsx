// ============================================================
// KnowledgePage - 知识库管理页（MVP-3 RAG）
// 功能：知识库切换 / 新建知识库 / 文档列表 / 拖拽上传 / 删除&重解析
// 文档状态轮询：pending → processing → ready/failed
// ============================================================

import { useState, useEffect, useRef, useCallback } from 'react'
import {
  Select, Button, Card, Table, Tag, Space, message, Popconfirm,
  Modal, Form, Input, Upload, Empty, Typography, Tooltip
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import {
  PlusOutlined, DeleteOutlined, ReloadOutlined, BookOutlined,
  SyncOutlined, InboxOutlined, FileTextOutlined
} from '@ant-design/icons'
import { kbApi, KbDatabase, KbDocument } from '../api'

const { Dragger } = Upload
const { Text } = Typography

// 文件类型 → Tag 颜色
const FILE_TYPE_COLOR: Record<string, string> = {
  pdf: 'red',
  docx: 'blue',
  doc: 'blue',
  txt: 'default',
  md: 'gold',
}

// 文档状态 → Tag 颜色
const DOC_STATUS_COLOR: Record<string, string> = {
  pending: 'default',
  processing: 'processing',
  ready: 'success',
  failed: 'error',
}

// 文档状态中文标签
const DOC_STATUS_LABEL: Record<string, string> = {
  pending: '待处理',
  processing: '解析中',
  ready: '就绪',
  failed: '失败',
}

// 文件大小人类可读格式
function formatFileSize(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  const i = Math.floor(Math.log(bytes) / Math.log(1024))
  return `${(bytes / Math.pow(1024, i)).toFixed(1)} ${units[i]}`
}

// 从文件名提取扩展名
function extOf(fileName: string): string {
  const dot = fileName.lastIndexOf('.')
  return dot >= 0 ? fileName.slice(dot + 1).toLowerCase() : ''
}

export default function KnowledgePage() {
  // ---------- 状态 ----------
  const [databases, setDatabases] = useState<KbDatabase[]>([])
  const [currentDb, setCurrentDb] = useState<KbDatabase | null>(null)
  const [documents, setDocuments] = useState<KbDocument[]>([])
  const [loadingDb, setLoadingDb] = useState(false)
  const [loadingDocs, setLoadingDocs] = useState(false)

  // 新建知识库 Modal
  const [createOpen, setCreateOpen] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [form] = Form.useForm()

  // 轮询定时器引用
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  // ============================================================
  // 上传并发控制（信号量：最多 3 个文件同时上传）
  // ============================================================
  const uploadSemaphore = useRef({
    current: 0,
    max: 3,
    queue: [] as (() => void)[],
    acquire(): Promise<void> {
      if (this.current < this.max) { this.current++; return Promise.resolve() }
      return new Promise(resolve => { this.queue.push(resolve) })
    },
    release() {
      this.current--
      const next = this.queue.shift()
      if (next) { this.current++; next() }
    },
  })

  // ============================================================
  // 加载知识库列表
  // ============================================================
  const loadDatabases = useCallback(async () => {
    setLoadingDb(true)
    try {
      const list = await kbApi.listDatabases()
      const arr = Array.isArray(list) ? list : []
      setDatabases(arr)
      // 若当前已选知识库仍存在，沿用新数据（不是旧引用，避免 chunkCount 过期）
      setCurrentDb(prev => {
        if (prev) {
          const found = arr.find(d => d.id === prev.id)
          if (found) return found  // ← 返回新对象，含最新 chunkCount
        }
        return arr[0] || null
      })
    } catch (e: any) {
      message.error(`加载知识库列表失败: ${e.message}`)
    } finally {
      setLoadingDb(false)
    }
  }, [])

  // ============================================================
  // 加载当前知识库下的文档列表
  // ============================================================
  const loadDocuments = useCallback(async (dbId: number | null) => {
    if (!dbId) {
      setDocuments([])
      return
    }
    setLoadingDocs(true)
    try {
      const list = await kbApi.listDocuments(dbId)
      setDocuments(Array.isArray(list) ? list : [])
    } catch (e: any) {
      message.error(`加载文档列表失败: ${e.message}`)
    } finally {
      setLoadingDocs(false)
    }
  }, [])

  // 初始加载知识库列表
  useEffect(() => {
    loadDatabases()
  }, [loadDatabases])

  // 当前知识库切换时重新加载文档
  useEffect(() => {
    loadDocuments(currentDb?.id ?? null)
  }, [currentDb?.id, loadDocuments])

  // ============================================================
  // 文档状态轮询：只要存在 pending/processing 文档，每 2 秒刷新一次
  // ============================================================
  useEffect(() => {
    // 清除上一次的轮询
    if (pollRef.current) {
      clearInterval(pollRef.current)
      pollRef.current = null
    }
    if (!currentDb) return

    const hasPending = documents.some(
      d => d.status === 'pending' || d.status === 'processing'
    )
    if (!hasPending) return

    pollRef.current = setInterval(async () => {
      try {
        const list = await kbApi.listDocuments(currentDb.id)
        const arr = Array.isArray(list) ? list : []
        setDocuments(arr)
        // 全部进入终态后停止轮询
        const stillPending = arr.some(
          d => d.status === 'pending' || d.status === 'processing'
        )
        if (!stillPending && pollRef.current) {
          clearInterval(pollRef.current)
          pollRef.current = null
          // 刷新知识库统计（chunkCount 等），避免卡片显示过期数据
          loadDatabases()
          const failed = arr.filter(d => d.status === 'failed')
          if (failed.length > 0) {
            message.warning(`解析完成：${failed.length} 个失败，${arr.length - failed.length} 个成功。悬停失败标签查看原因`)
          } else {
            message.success(`${arr.length} 个文档全部解析完成`)
          }
        }
      } catch {
        /* 轮询失败静默忽略 */
      }
    }, 2000)

    return () => {
      if (pollRef.current) {
        clearInterval(pollRef.current)
        pollRef.current = null
      }
    }
  }, [documents, currentDb])

  // ============================================================
  // 切换知识库
  // ============================================================
  const onSelectDb = (id: number) => {
    const db = databases.find(d => d.id === id) || null
    setCurrentDb(db)
  }

  // ============================================================
  // 新建知识库
  // ============================================================
  const onCreate = () => {
    form.resetFields()
    setCreateOpen(true)
  }

  const onCreateSubmit = async () => {
    try {
      const values = await form.validateFields()
      setSubmitting(true)
      const created = await kbApi.createDatabase(values.name, values.description || '')
      message.success(`知识库「${values.name}」已创建`)
      setCreateOpen(false)
      // 重新拉取列表，并直接选中新创建的
      await loadDatabases()
      if (created && created.id) {
        setCurrentDb({
          id: created.id,
          name: values.name,
          description: values.description,
          documentCount: 0,
          chunkCount: 0,
          createdAt: new Date().toISOString(),
        })
      }
    } catch (e: any) {
      if (e?.errorFields) return
      message.error(`创建失败: ${e.message}`)
    } finally {
      setSubmitting(false)
    }
  }

  // ============================================================
  // 删除知识库（带二次确认）
  // ============================================================
  const onDeleteDb = async () => {
    if (!currentDb) return
    try {
      await kbApi.deleteDatabase(currentDb.id)
      message.success(`已删除知识库「${currentDb.name}」`)
      setDocuments([])
      await loadDatabases()
    } catch (e: any) {
      message.error(`删除失败: ${e.message}`)
    }
  }

  // ============================================================
  // 上传文档（手动触发，不走 antd Upload 内置 xhr）
  // 返回 false 阻止 antd 自动上传
  // ============================================================
  const onUpload = async (file: any) => {
    if (!currentDb) {
      message.warning('请先选择知识库')
      return false
    }
    const ext = extOf(file.name || '')
    if (!['pdf', 'docx', 'doc', 'txt', 'md'].includes(ext)) {
      message.error('仅支持 PDF / Word / TXT / MD 文件')
      return false
    }

    // 排队：并发上限 3，超出的等待
    await uploadSemaphore.current.acquire()
    try {
      const dbId = currentDb.id  // 闭包捕获，避免上传期间切换知识库
      await kbApi.uploadDocument(dbId, file as File)
      message.success(`「${file.name}」已上传，开始解析...`)
      await loadDocuments(dbId)
      loadDatabases() // 刷新知识库统计（文档数+分片数）
    } catch (e: any) {
      message.error(`「${file.name}」上传失败: ${e.message}`)
    } finally {
      uploadSemaphore.current.release()
    }
    return false // 阻止 antd 自动上传
  }

  // ============================================================
  // 删除文档
  // ============================================================
  const onDeleteDoc = async (doc: KbDocument) => {
    try {
      await kbApi.deleteDocument(doc.id)
      message.success(`已删除「${doc.fileName}」`)
      await loadDocuments(currentDb?.id ?? null)
    } catch (e: any) {
      message.error(`删除失败: ${e.message}`)
    }
  }

  // ============================================================
  // 重新解析文档
  // ============================================================
  const onReparse = async (doc: KbDocument) => {
    try {
      await kbApi.reparseDocument(doc.id)
      message.success(`「${doc.fileName}」已提交重新解析`)
      await loadDocuments(currentDb?.id ?? null)
    } catch (e: any) {
      message.error(`重新解析失败: ${e.message}`)
    }
  }

  // ============================================================
  // 文档表格列定义
  // ============================================================
  const columns: ColumnsType<KbDocument> = [
    {
      title: '文件名',
      dataIndex: 'fileName',
      key: 'fileName',
      ellipsis: true,
      render: (name: string) => (
        <Space size={4}>
          <FileTextOutlined className="text-gray-400" />
          <Text className="!text-gray-200">{name}</Text>
        </Space>
      ),
    },
    {
      title: '大小',
      dataIndex: 'fileSize',
      key: 'fileSize',
      width: 100,
      render: (size: number) => (
        <span className="text-gray-400">{formatFileSize(size)}</span>
      ),
    },
    {
      title: '类型',
      dataIndex: 'fileType',
      key: 'fileType',
      width: 80,
      render: (ft: string) => (
        <Tag color={FILE_TYPE_COLOR[ft?.toLowerCase()] || 'default'}>
          {(ft || '?').toUpperCase()}
        </Tag>
      ),
    },
    {
      title: '状态',
      dataIndex: 'status',
      key: 'status',
      width: 100,
      render: (s: string, record: KbDocument) => (
        <Tooltip title={s === 'failed' && record.errorMessage ? record.errorMessage : undefined}>
          <Tag color={DOC_STATUS_COLOR[s] || 'default'} style={s === 'failed' ? { cursor: 'help' } : undefined}>
            {DOC_STATUS_LABEL[s] || s}
          </Tag>
        </Tooltip>
      ),
    },
    {
      title: '分片数',
      dataIndex: 'chunkCount',
      key: 'chunkCount',
      width: 80,
      render: (n: number) => <span className="text-gray-300">{n ?? 0}</span>,
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 160,
      render: (t: string) => (
        <span className="text-gray-400 text-xs">
          {t ? t.replace('T', ' ').slice(0, 19) : '-'}
        </span>
      ),
    },
    {
      title: '操作',
      key: 'action',
      width: 160,
      fixed: 'right',
      render: (_: any, record: KbDocument) => (
        <Space size="small">
          <Tooltip title="重新解析">
            <Button
              type="link"
              size="small"
              icon={<SyncOutlined />}
              onClick={() => onReparse(record)}
            />
          </Tooltip>
          <Popconfirm
            title="确认删除"
            description={`删除「${record.fileName}」？此操作不可恢复`}
            okText="删除"
            cancelText="取消"
            okButtonProps={{ danger: true }}
            onConfirm={() => onDeleteDoc(record)}
          >
            <Button type="link" size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ]

  return (
    <div className="h-full flex flex-col bg-dark-900">
      {/* 顶部工具栏：知识库选择 + 新建 + 删除 */}
      <div className="flex items-center justify-between gap-3 px-6 py-4 border-b border-gray-800 bg-dark-800/60">
        <div className="flex items-center gap-3 text-white">
          <BookOutlined className="text-primary text-lg" />
          <span className="font-semibold">知识库管理</span>
          <Select
            loading={loadingDb}
            value={currentDb?.id}
            onChange={onSelectDb}
            placeholder="选择知识库"
            className="w-64"
            options={databases.map(d => ({
              value: d.id,
              label: `${d.name} (${d.documentCount} 文档 / ${d.chunkCount} 分片)`,
            }))}
            notFoundContent="暂无知识库"
          />
          <Button
            icon={<ReloadOutlined />}
            onClick={() => {
              loadDatabases()
              loadDocuments(currentDb?.id ?? null)
            }}
          >
            刷新
          </Button>
        </div>
        <Space>
          <Button type="primary" icon={<PlusOutlined />} onClick={onCreate} className="!bg-primary">
            新建知识库
          </Button>
          {currentDb && (
            <Popconfirm
              title="删除知识库"
              description={`将删除知识库「${currentDb.name}」及其所有文档与分片，不可恢复`}
              okText="确认删除"
              cancelText="取消"
              okButtonProps={{ danger: true }}
              onConfirm={onDeleteDb}
            >
              <Button danger icon={<DeleteOutlined />}>删除知识库</Button>
            </Popconfirm>
          )}
        </Space>
      </div>

      {/* 主体：左侧文档列表 + 右侧上传区 */}
      <div className="flex-1 flex overflow-hidden">
        {/* 左侧：文档列表 */}
        <div className="flex-1 overflow-auto p-6">
          {!currentDb ? (
            <Empty
              className="mt-20"
              description={<span className="text-gray-500">请先选择或创建一个知识库</span>}
            />
          ) : (
            <Card
              title={
                <Space>
                  <span className="text-white">{currentDb.name}</span>
                  <Tag color="blue">{documents.length} 个文档</Tag>
                  <Tag color="purple">
                    {currentDb.chunkCount ?? 0} 个分片
                  </Tag>
                </Space>
              }
              className="!bg-dark-800 !border-gray-800"
              styles={{ body: { padding: 0 } }}
            >
              <Table<KbDocument>
                columns={columns}
                dataSource={documents}
                rowKey="id"
                size="small"
                loading={loadingDocs}
                pagination={{ pageSize: 10, showSizeChanger: true, showTotal: t => `共 ${t} 条` }}
                scroll={{ x: 900 }}
                locale={{ emptyText: '暂无文档，请上传' }}
              />
            </Card>
          )}
        </div>

        {/* 右侧：拖拽上传区 */}
        <aside className="w-96 border-l border-gray-800 bg-dark-800/60 p-6 overflow-y-auto">
          <h3 className="text-sm font-semibold text-white mb-3">上传文档</h3>
          {!currentDb ? (
            <div className="text-gray-500 text-sm">
              请先选择一个知识库后再上传文档。
            </div>
          ) : (
            <>
              <Dragger
                accept=".pdf,.docx,.doc,.txt,.md"
                multiple
                showUploadList={false}
                beforeUpload={onUpload}
                className="!bg-dark-700 !border-gray-700 !rounded-lg"
              >
                <p className="ant-upload-drag-icon">
                  <InboxOutlined className="text-primary text-4xl" />
                </p>
                <p className="ant-upload-text text-gray-200">点击或拖拽文件到此处上传</p>
                <p className="ant-upload-hint text-gray-500 text-xs">
                  支持 PDF / Word / TXT / MD，单次可选多个
                </p>
              </Dragger>
              <div className="mt-4 text-xs text-gray-500 space-y-1">
                <p>· 文档上传后状态会自动从 <Tag color="default">待处理</Tag> → <Tag color="processing">解析中</Tag> → <Tag color="success">就绪</Tag></p>
                <p>· 每 2 秒自动轮询一次，无需手动刷新</p>
                <p>· 解析失败的文档可点击重新解析</p>
              </div>
            </>
          )}
        </aside>
      </div>

      {/* 新建知识库 Modal */}
      <Modal
        title="新建知识库"
        open={createOpen}
        onOk={onCreateSubmit}
        onCancel={() => setCreateOpen(false)}
        confirmLoading={submitting}
        okText="创建"
        cancelText="取消"
        destroyOnClose
      >
        <Form form={form} layout="vertical" preserve={false}>
          <Form.Item
            name="name"
            label="知识库名称"
            rules={[{ required: true, message: '请输入知识库名称' }]}
          >
            <Input placeholder="例如：产品手册" className="!bg-dark-700 !border-gray-700 !text-white" />
          </Form.Item>
          <Form.Item name="description" label="描述（可选）">
            <Input.TextArea
              rows={3}
              placeholder="知识库用途说明..."
              className="!bg-dark-700 !border-gray-700 !text-white"
            />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}
