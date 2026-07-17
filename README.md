# 🥟 MultiAgent System — 多 Agent 协同系统

> .NET 11 + Microsoft Agent Framework + React · 个人作品集项目
>
> 一个从单 Agent 对话逐步演进到 11 Agent + 7 种编排模式 + RAG 知识库 + CRM 人审的多 Agent 平台。

## 当前进度：MVP-4 ✅

- [x] **MVP-0**：单 Agent 对话 + SSE 流式 + 暗黑主题 + SQLite
- [x] **MVP-1**：3 Agent 流水线协作（Researcher → Writer → Critic）+ 退回重写
- [x] **MVP-2**：7 种编排模式（Sequential / Concurrent / Handoff / GroupChat / Magentic 等）
- [x] **MVP-3**：RAG 知识库（文档上传 → 解析 → 嵌入 → 混合检索 → 评测）
- [x] **MVP-4**：CRM 集成 + JWT 鉴权 + 人审机制 + 工单 + 审计日志
- [ ] MVP-5：评测体系完善（RAG 评测雏形已具备，待扩展为多维度基准）

---

## ✨ 核心能力一览

| 能力域 | 说明 |
|--------|------|
| 🤖 **11 个 Agent** | Researcher / Writer / Critic / Analyst / Coder / Consultant / Support / Coordinator / CrmAgent / Approver / KnowledgeAgent |
| 🧩 **7 种编排模式** | Sequential / Concurrent / Handoff / GroupChat / Magentic / Crm / Rag，聊天时可实时切换 |
| 🔐 **JWT 鉴权** | User / Admin 双角色，敏感接口 `[Authorize]` 保护，工单流转/人审决策限 Admin |
| 📇 **CRM 模块** | 客户 / 联系人 / 跟进记录 CRUD REST API，按归属人隔离 |
| 🗳️ **人审机制** | 敏感操作暂停 → 前端弹窗 → 人决策（批准/驳回/改参数）→ Agent 恢复执行 |
| 🧾 **工单系统** | 状态流转 Pending → Processing → Done / Rejected |
| 📜 **审计日志** | 所有工具调用 / 人审 / 数据变更留痕，Admin 可查 |
| 📚 **RAG 知识库** | 文档上传 → 解析分片 → 向量嵌入 → 混合检索（向量 + 关键词 + RRF 融合 + 可选重排）→ 评测 |
| 🧠 **长期记忆** | MemoryStore 维护对话历史 + 用户画像，跨会话召回 |
| 🔌 **适配器模式** | `IExternalSystemAdapter` + `CrmAdapter`，预留 ERP / OA 扩展 |
| 📡 **SSE 流式** | 模式切换 / Agent 步骤 / 工具调用 / 人审请求 等事件实时推送 |

---

## 🏗️ 架构概览

```
┌─────────────────────────────────────────────────────────────┐
│                      前端 (React + Vite 5173)               │
│  Login / Chat / Knowledge / RetrievalTest / RagEval         │
│  Customers / Tickets / Approvals / Dashboard               │
│                     (暗黑主题 + AntD 5)                      │
└──────────────────────────┬──────────────────────────────────┘
                           │ /api/* (Vite proxy → 5000)
┌──────────────────────────┴──────────────────────────────────┐
│                  后端 (ASP.NET Core 11 :5000)               │
│                                                             │
│  ┌─────────────┐   ┌──────────────┐   ┌─────────────────┐   │
│  │  JWT 鉴权   │   │  REST API    │   │  SSE 流式聊天   │   │
│  │ User/Admin  │   │  CRM/工单/审计│   │  7 模式可切换   │   │
│  └─────────────┘   └──────────────┘   └────────┬────────┘   │
│                                                │            │
│         ┌──────────────────────────────────────┘            │
│         ▼                                                   │
│  ┌─────────────┐  ┌──────────────┐  ┌──────────────────┐    │
│  │ AgentRegistry│ │ 编排策略 x7  │  │ ApprovalCoord.   │    │
│  │ 11 个 Agent │→ │ (策略模式)   │  │ (人审协调器)     │    │
│  └─────────────┘  └──────────────┘  └──────────────────┘    │
│         │                   │               │               │
│  ┌──────┴──────┐  ┌─────────┴─────┐  ┌──────┴──────┐        │
│  │   Tools     │  │  RAG 检索     │  │ Adapters    │        │
│  │ Tavily/Crm/ │  │ HybridRetriever│ │ CrmAdapter  │        │
│  │ Knowledge   │  │ VectorStore   │  │ (预留ERP/OA)│        │
│  └─────────────┘  └───────────────┘  └─────────────┘        │
│                           │                                 │
│                   ┌───────┴────────┐                        │
│                   │  SQLite 统一库  │                        │
│                   │ multiagent.db  │                        │
│                   │ (对话/CRM/工单/ │                        │
│                   │  审计/RAG/记忆) │                        │
│                   └────────────────┘                        │
└─────────────────────────────────────────────────────────────┘
        │                              │
   DeepSeek API (LLM)           SiliconFlow / Embedding
   Tavily API (搜索工具)        (RAG 向量化)
```

---

## 🚀 快速启动

### 前置要求

- **.NET 11 SDK**（preview，11.0.100-preview.6）
- **Node.js 18+**
- **DeepSeek API Key**（[获取](https://platform.deepseek.com/)）
- **Tavily API Key**（可选，[获取](https://tavily.com/)，不填则 Researcher 退化为纯知识回答）
- **Embedding API Key**（RAG 功能用，示例为 SiliconFlow，[获取](https://siliconflow.cn/)）

### 1. 配置 API Key

后端配置位于 `src/MultiAgentSystem.Api/appsettings.json`（**该文件含密钥，已被 .gitignore 排除，不入库**）。
首次使用可参考模板：

```bash
cp src/MultiAgentSystem.Api/appsettings.example.json src/MultiAgentSystem.Api/appsettings.json
# 然后编辑 appsettings.json，填入真实的 ApiKey
```

```json
{
  "LLM":      { "ApiKey": "sk-...", "BaseUrl": "https://api.deepseek.com", "ModelId": "deepseek-v4-flash" },
  "Embedding":{ "ApiKey": "sk-...", "BaseUrl": "https://api.siliconflow.cn", "ModelId": "BAAI/bge-large-zh-v1.5", "Dimension": 1024 },
  "Tavily":   { "ApiKey": "tvly-...", "Enabled": true },
  "Jwt":      { "Secret": "替换为强随机字符串", "Issuer": "MultiAgentSystem" }
}
```

> ⚠️ **安全提示**：`appsettings.json` 含真实密钥，已被 `.gitignore` 忽略。请勿将其提交到 Git 仓库，生产环境建议使用 .NET User Secrets 或环境变量。

### 2. 启动后端

```bash
cd src/MultiAgentSystem.Api
dotnet run
```

- 后端：http://localhost:5000
- Swagger 文档：http://localhost:5000/swagger

### 3. 启动前端（新开终端）

```bash
cd frontend
npm install
npm run dev
```

- 前端：http://localhost:5173（Vite 自动代理 `/api` → 5000）

### 4. 打开浏览器

访问 http://localhost:5173，使用默认账号登录后即可体验：
- 默认管理员：`admin` / `admin123`（Admin 角色，可操作工单/人审/审计）
- 默认普通用户：`user` / `user123`（仅可见自有数据）

---

## 📁 项目结构

```
MultiAgentSystem/
├── src/
│   └── MultiAgentSystem.Api/              # 后端（ASP.NET Core 11）
│       ├── Adapters/                      # 外部系统适配器
│       │   ├── IExternalSystemAdapter.cs  # 适配器接口（预留 ERP/OA）
│       │   └── CrmAdapter.cs              # CRM 适配器实现
│       ├── Agents/                        # 11 个 Agent 定义
│       │   ├── ResearcherAgent.cs         # 研究员：调搜索工具收集信息
│       │   ├── WriterAgent.cs             # 写作者：基于素材撰写回答
│       │   ├── CriticAgent.cs             # 审核员：通过或退回重写
│       │   ├── AnalystAgent.cs            # 数据分析师
│       │   ├── CoderAgent.cs              # 编码助手
│       │   ├── ConsultantAgent.cs         # 咨询顾问
│       │   ├── SupportAgent.cs            # 客服支持
│       │   ├── CoordinatorAgent.cs        # 协调员（GroupChat/Magentic）
│       │   ├── CrmAgent.cs                # CRM 操作 Agent（调 CrmTools）
│       │   ├── ApproverAgent.cs           # 风险审核 Agent
│       │   └── KnowledgeAgent.cs          # RAG 知识问答 Agent
│       ├── Strategies/                    # 7 种编排策略（策略模式）
│       │   ├── SequentialStrategy.cs      # 顺序流水线 + Critic 退回重写
│       │   ├── ConcurrentStrategy.cs      # 并行执行
│       │   ├── HandoffStrategy.cs         # 接力移交
│       │   ├── GroupChatStrategy.cs       # 群聊协作
│       │   ├── MagenticStrategy.cs        # 路由/调度
│       │   ├── CrmStrategy.cs             # CRM + 人审编排
│       │   └── RagStrategy.cs             # RAG 检索增强生成
│       ├── Services/                      # 业务服务
│       │   ├── ChatClientFactory.cs       # DeepSeek 的 IChatClient 工厂
│       │   ├── AgentRegistry.cs           # Agent 注册中心
│       │   ├── AgentRunner.cs             # Agent 执行器
│       │   ├── AgentOrchestrator.cs       # 编排服务
│       │   ├── OrchestrationModels.cs     # 编排事件模型
│       │   ├── ConversationStore.cs       # 对话历史持久化
│       │   ├── BusinessStore.cs           # CRM/工单/审计/用户 统一存储
│       │   ├── ApprovalCoordinator.cs     # 人审协调器（暂停/恢复）
│       │   ├── JwtService.cs              # JWT 签发与校验
│       │   ├── EmbeddingService.cs        # 向量嵌入（含降级）
│       │   ├── VectorStore.cs             # 内存向量存储
│       │   ├── KnowledgeStore.cs          # RAG 知识库持久化
│       │   ├── HybridRetriever.cs         # 混合检索（向量+关键词+RRF+重排）
│       │   ├── DocumentParser.cs          # 文档解析分片
│       │   └── MemoryStore.cs             # 长期记忆 + 用户画像
│       ├── Tools/                         # Agent 工具
│       │   ├── TavilySearchTool.cs        # Tavily 搜索
│       │   ├── CrmTools.cs                # CRM 操作工具集
│       │   └── KnowledgeTools.cs          # RAG 检索工具集
│       ├── Models/                        # 数据模型
│       │   ├── ChatModels.cs              # 聊天请求/消息模型
│       │   ├── CrmModels.cs               # CRM 实体模型
│       │   └── RagModels.cs               # RAG 实体模型
│       ├── Program.cs                     # 启动入口（DI + 端点）
│       ├── appsettings.json               # 配置（含密钥，已 gitignore）
│       ├── appsettings.example.json       # 配置模板（脱敏，可入库）
│       └── MultiAgentSystem.Api.csproj
├── frontend/                              # 前端（React 18 + Vite 6）
│   ├── src/
│   │   ├── pages/                         # 9 个页面
│   │   │   ├── LoginPage.tsx              # 登录
│   │   │   ├── ChatPage.tsx               # 多 Agent 聊天
│   │   │   ├── KnowledgePage.tsx          # 知识库管理
│   │   │   ├── RetrievalTestPage.tsx      # 检索测试
│   │   │   ├── RagEvalPage.tsx            # RAG 评测
│   │   │   ├── CustomersPage.tsx          # 客户管理
│   │   │   ├── TicketsPage.tsx            # 工单管理
│   │   │   ├── ApprovalsPage.tsx          # 人审待办
│   │   │   └── DashboardPage.tsx          # 仪表盘
│   │   ├── components/
│   │   │   ├── Layout.tsx                 # 主布局 + 侧边栏
│   │   │   ├── Panels.tsx                 # Agent 流水线可视化
│   │   │   ├── CrmPanel.tsx               # CRM 面板
│   │   │   └── ApprovalModal.tsx          # 人审决策弹窗
│   │   ├── api.ts                         # Axios 封装
│   │   ├── auth.ts                        # 登录态 + JWT 管理
│   │   ├── types.ts                       # 类型定义
│   │   ├── main.tsx                       # React 入口 + 路由
│   │   └── index.css                      # 全局样式（含 shake/pulse-x 动画）
│   ├── package.json
│   ├── vite.config.ts                     # 5173 → 5000 代理
│   ├── tsconfig.json
│   ├── tailwind.config.js
│   └── postcss.config.js
├── .gitignore
└── README.md
```

---

## 🧩 7 种编排模式

聊天接口 `/api/chat` 通过请求体 `orchestrationMode` 字段切换：

| 模式 | 关键字 | 适用场景 |
|------|--------|----------|
| **Sequential** | `sequential`（默认） | 流水线：Researcher → Writer → Critic，含退回重写（最多 2 轮） |
| **Concurrent** | `concurrent` | 多 Agent 并行执行，结果汇总 |
| **Handoff** | `handoff` | 接力移交，前一个 Agent 决定下一个执行者 |
| **GroupChat** | `groupchat` | 群聊协作，多 Agent 轮流发言 |
| **Magentic** | `magentic` / `route` | 路由调度，由 Coordinator 分派任务 |
| **Crm** | `crm` | CRM 操作 + 人审，敏感操作暂停等人决策 |
| **Rag** | `rag` / `knowledge` | 检索增强生成，基于知识库作答 |

---

## 📡 API 端点速查

完整交互文档见 Swagger：http://localhost:5000/swagger

| 分类 | 方法 | 路径 | 说明 |
|------|------|------|------|
| 系统 | GET | `/api/health` | 健康检查，返回已注册 Agent 与可用模式 |
| 认证 | POST | `/api/auth/login` | 登录，返回 JWT |
| 认证 | GET | `/api/auth/me` | 当前用户信息 |
| 聊天 | POST | `/api/chat` | SSE 流式聊天，7 模式可切换 |
| CRM客户 | GET/POST/PUT/DELETE | `/api/crm/customers[/{id}]` | 客户 CRUD，按归属人隔离 |
| CRM跟进 | GET/POST | `/api/crm/customers/{id}/followups` | 跟进记录 |
| 工单 | GET/POST | `/api/tickets` | 工单列表/创建 |
| 工单 | PUT | `/api/tickets/{id}/status` | 工单状态流转（Admin） |
| 人审 | GET | `/api/approvals[/{id}]` | 人审待办 |
| 人审 | POST | `/api/approvals/decide` | 人审决策（Admin） |
| 审计 | GET | `/api/audit` | 审计日志（Admin） |
| 仪表盘 | GET | `/api/dashboard` | 统计数据 |
| RAG库 | GET/POST/DELETE | `/api/kb/databases[/{id}]` | 知识库管理 |
| RAG文档 | GET/DELETE | `/api/kb/databases/{id}/documents` | 文档管理 |
| RAG上传 | POST | `/api/kb/databases/{id}/upload` | 文档上传（异步解析嵌入） |
| RAG检索 | POST | `/api/kb/search` | 混合检索测试 |
| RAG评测 | POST | `/api/kb/eval` | 召回率/准确率评测 |
| RAG统计 | GET | `/api/kb/stats` | 知识库与向量统计 |
| 记忆 | GET | `/api/kb/memory/{sessionId}` | 长期记忆查询 |

### SSE 事件类型

| 事件 | 字段 | 说明 |
|------|------|------|
| `session` | `sessionId` | 会话建立 |
| `orchestration_start` | `mode` | 编排开始，告知当前模式 |
| `orchestration_event` | `eventType, agent, status, output, round, ...` | 编排过程事件（Agent 步骤/工具调用/人审等） |
| `token` | `content` | 终稿分块（打字机效果，2 字符/块） |
| `done` | `content` | 流结束 + 完整终稿 |
| `error` | `message` | 异常 |

`orchestration_event.eventType` 取值：`agent_step` / `tool_call` / `tool_result` / `approval_required` / `approval_result` / `handoff` 等。

---

## 🛠️ 技术栈

### 后端
- **.NET 11**（preview）
- **Microsoft Agent Framework 1.13.0 GA**（`Microsoft.Agents.AI` + `Microsoft.Agents.AI.OpenAI`）
- **Microsoft.Extensions.AI**（统一 `IChatClient` 接口）
- **OpenAI SDK**（兼容 DeepSeek）
- **Microsoft.Data.Sqlite 11.0.0-preview**（统一持久化）
- **System.IdentityModel.Tokens.Jwt 8.3.0**（JWT 鉴权）
- **Swashbuckle.AspNetCore 7.0.0**（Swagger 文档）

### 前端
- **React 18** + **TypeScript 5.6**
- **Ant Design 5.22**（暗黑主题）
- **TailwindCSS 3.4**
- **Vite 6**
- **react-router-dom 7**（路由 + 守卫）
- **react-markdown 10** + **rehype-highlight** + **remark-gfm**（Markdown 渲染）
- **axios 1.7**

### 外部服务
- **DeepSeek API**（LLM，OpenAI 兼容）
- **Tavily Search API**（Agent 搜索工具）
- **SiliconFlow / Embedding**（RAG 向量化，可配置）

---

## 🧪 验证

- `dotnet build` — 0 错误 0 警告
- `tsc --noEmit` — 0 错误
- 端到端：7 种编排模式均可切换；RAG 文档上传 → 检索 → 评测链路打通；CRM + 人审闭环验证

---

## 🗺️ MVP 路线图

| 阶段 | 内容 | 状态 |
|------|------|------|
| MVP-0 | 单 Agent 对话 + 流式输出 | ✅ 完成 |
| MVP-1 | 3 Agent + Sequential 流水线 | ✅ 完成 |
| MVP-2 | 7 种编排模式 | ✅ 完成 |
| MVP-3 | RAG 知识库 | ✅ 完成 |
| MVP-4 | CRM 集成 + 人审 + JWT | ✅ 完成 |
| MVP-5 | 评测体系完善 | 🔨 RAG 评测雏形已具备，待扩展多维度基准 |
| 打磨 | 注解 + 文档 + 部署 | 🔨 进行中 |

---

## 🔒 安全说明

- `appsettings.json` 含真实 API Key，已加入 `.gitignore`，**不会入库**。
- 仓库提供 `appsettings.example.json` 脱敏模板供首次配置参考。
- JWT Secret 默认值为演示用，**生产环境务必替换**为强随机值。
- 人审决策、工单状态流转、审计日志查看、客户删除等敏感操作限 Admin 角色。
- 如不慎将密钥提交，请立即在各服务后台轮换 Key。

---

## 📜 历史版本记录（CHANGELOG）

### MVP-4 · 2026-07-16 · CRM 集成 + 人审 + JWT 鉴权
**新增**
- JWT 鉴权：登录 / 角色（User / Admin），`[Authorize]` 保护敏感接口
- CRM 模块：客户 / 联系人 / 跟进 CRUD REST API，按归属人隔离
- CRM Agent + Approver Agent：工具调用 + 风险审核
- 人审机制：`ApprovalCoordinator` 协调敏感操作暂停 → 前端弹窗 → 人决策 → 恢复执行
- 工单系统：状态流转（Pending / Processing / Done / Rejected）
- 审计日志：所有工具调用 / 人审 / 数据变更留痕
- 适配器模式：`IExternalSystemAdapter` + `CrmAdapter`（ERP / OA 预留）
- SSE 新事件：`tool_call` / `tool_result` / `approval_required` / `approval_result`
- 前端新增页面：Login / Customers / Tickets / Approvals / Dashboard
- `appsettings.json` 新增 `Jwt` 配置段

### MVP-3 · 2026-07-16 · RAG 知识库
**新增**
- 知识库 CRUD：`KnowledgeStore` 复用 `multiagent.db`，新增 5 张表
- 文档上传：异步处理（解析 → 分片 → 嵌入 → 存 SQLite + VectorStore）
- 向量嵌入：`EmbeddingService` 调 Embedding API，失败降级为哈希向量
- 混合检索：`HybridRetriever`（向量 + 关键词 + RRF 融合 + 可选重排）
- 长期记忆：`MemoryStore` 对话历史 + 用户画像
- KnowledgeAgent + KnowledgeTools：RAG 问答 Agent
- RagStrategy 编排模式
- 前端新增页面：Knowledge / RetrievalTest / RagEval
- RAG 评测接口：召回率 + 准确率

### MVP-2 · 2026-07-16 · 7 种编排模式
**新增**
- 策略模式抽象 `IOrchestrationStrategy`
- 5 个新 Agent：Analyst / Coder / Consultant / Support / Coordinator
- 6 个新编排策略：Concurrent / Handoff / GroupChat / Magentic / Crm / Rag
- `AgentRegistry` 集中管理 11 个 Agent
- 聊天接口支持 `orchestrationMode` 参数切换模式

### MVP-1 · 2026-07-16 · 3 Agent 流水线协作
**新增**
- 后端分层架构：Agents / Tools / Services / Models 目录
- 引入 Microsoft Agent Framework 1.13.0 GA
- 3 个 ChatClientAgent：Researcher / Writer / Critic
- Sequential 编排服务 `AgentOrchestrator`，含 Critic 退回重写循环（最多 2 轮）
- Tavily 搜索工具（`AIFunctionFactory.Create`）
- SQLite 对话历史存储（`Microsoft.Data.Sqlite`）
- SSE 新增 `agent_step` 事件类型
- 前端左侧流水线可视化面板（running 脉冲 / done 绿勾 / rejected 震动）

**重构**
- `Program.cs` 从单文件改为 DI 注入 + 分层调用
- DeepSeek 接入方式：原生 HttpClient → `OpenAIClient` + `AsIChatClient()` + `ChatClientAgent`
- 对话历史：`ConcurrentDictionary` → SQLite 持久化

**关键 bug 修复**
- `ChatMessage` 歧义（`Models.ChatMessage` vs `Microsoft.Extensions.AI.ChatMessage`）→ 用全限定名
- `ChatClientAgent` 构造函数无 `serviceProvider` 命名参数 → 改位置参数 `null`
- `AgentResponse` 不支持 `foreach` → 改用 `.Messages` 迭代
- `Progress<T>` 在无 SyncContext 时异步 Post 到线程池导致 Critic done 事件丢失 → 改用同步 `Action<AgentStepEvent>` 回调

### MVP-0 · 2026-07-15 · 单 Agent 对话基线
**功能清单**
- 暗黑主题聊天界面（React + Ant Design + TailwindCSS）
- SSE 流式输出，打字机效果，闪烁光标（`cursor-blink::after`）
- DeepSeek API 接入（OpenAI 兼容格式）
- 多轮对话历史（内存版 `ConcurrentDictionary`，MVP-1 已迁移到 SQLite）
- 新建对话、session 管理（localStorage 持久化 sessionId）
- 后端 Swagger（含中文 summary / description）
- 每个文件都有详细中文注释

**技术栈**
- .NET 11 preview SDK
- 原生 `HttpClient` 直调 DeepSeek `/chat/completions`（流式 `stream:true`）
- `HttpCompletionOption.ResponseHeadersRead` + `ReadLineAsync` 实现真正流式
- 前端 Vite 5173 代理到后端 5000

**关键 bug 修复**
- `PostAsync` 无 `HttpCompletionOption` 重载 → 改用 `SendAsync` + `HttpRequestMessage`
- `reader.EndOfStream` 在 async 方法中触发 CA2024 → 改用 `ReadLineAsync(ct)`
- 配置 key 从 `DeepSeek:ApiKey` 统一为 `LLM:ApiKey`
- 404 错误根因：前端 `/api/chat` vs 后端 `/api/chat/stream` 路径不匹配 → 统一为 `/api/chat`
- SSE 事件字段命名对齐：`delta+text` → `token+content`，增加 `session+sessionId` 事件
- `deepseek-v4-flash` 是有效模型名（`deepseek-chat` 将于 2026/07/24 弃用）
- 顶级语句不支持 `///` XML 注释 → 改用 `//` 注释，元数据走 `WithSummary`/`WithDescription`
