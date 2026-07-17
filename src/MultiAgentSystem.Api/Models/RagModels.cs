// ============================================================
// RagModels - MVP-3 RAG 知识库与检索优化相关数据模型
//
// 设计意图：
//   - 统一定义 RAG 模块所需的全部数据结构，对应 SQLite 各张表
//   - 区分"持久化实体"（KnowledgeBase/Document/Chunk/Memory）与
//     "传输对象"（SearchResult/RetrievalResponse/Eval*）
//   - 支持混合检索的三路分数对比（向量/关键词/融合），方便前端可视化
//
// 模块组成：
//   - KnowledgeBase   知识库（一个库 → 多个文档 → 多个分片）
//   - KnowledgeDocument 文档（上传文件）
//   - DocumentChunk   分片（文档切片后的最小检索单元）
//   - SearchResult    检索单条结果
//   - RetrievalResponse 检索响应（含各路分数对比）
//   - EvalTestCase/EvalResult RAG 评测
//   - UserProfile/MemoryRecord 长期记忆与用户画像
// ============================================================

using System.Text.Json.Serialization;

namespace MultiAgentSystem.Api.Models;

// ===================== 知识库核心实体 =====================

/// <summary>
/// 知识库 - 一个知识库包含多个文档
/// 对应 knowledge_bases 表
/// </summary>
public class KnowledgeBase
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>文档数（动态聚合，非存储字段）</summary>
    public int DocumentCount { get; set; }
    /// <summary>分片数（动态聚合，非存储字段）</summary>
    public int ChunkCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 文档处理状态
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentStatus
{
    /// <summary>待处理（刚上传）</summary>
    Pending,
    /// <summary>处理中（解析/嵌入）</summary>
    Processing,
    /// <summary>就绪（已嵌入，可检索）</summary>
    Ready,
    /// <summary>失败（解析/嵌入异常）</summary>
    Failed
}

/// <summary>
/// 知识文档 - 上传到知识库的文件
/// 对应 knowledge_documents 表
/// </summary>
public class KnowledgeDocument
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    /// <summary>文件类型：txt/md/pdf/docx</summary>
    public string FileType { get; set; } = "";
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;
    public int ChunkCount { get; set; }
    /// <summary>失败原因（Status=Failed 时填充，供前端展示为什么失败）</summary>
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 文档分片 - 文档切片后的最小检索单元
/// 对应 document_chunks 表
/// embedding 字段以 BLOB JSON 形式持久化（float[] 序列化）
/// </summary>
public class DocumentChunk
{
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public int DatabaseId { get; set; }
    public string Content { get; set; } = "";
    public int PageNumber { get; set; }
    public int ChunkIndex { get; set; }
    public int TokenCount { get; set; }
    /// <summary>嵌入向量（JSON序列化存储，EF Core ValueConverter自动转换）</summary>
    public List<float>? Embedding { get; set; }
}

// ===================== 检索结果与响应 =====================

/// <summary>
/// 单条检索结果
/// Score = 融合后分数；RerankedScore = 重排序后分数（未重排则为 null）
/// </summary>
public record SearchResult(
    int ChunkId,
    string Content,
    string FileName,
    int PageNumber,
    double Score,
    double? RerankedScore,
    string Source);

/// <summary>
/// 检索响应 - 含三路分数对比，方便前端展示混合检索效果
/// </summary>
public record RetrievalResponse(
    string Query,
    List<SearchResult> Results,
    Dictionary<int, double> VectorScores,
    Dictionary<int, double> KeywordScores,
    Dictionary<int, double> FusedScores);

// ===================== RAG 评测 =====================

/// <summary>
/// 评测总结果 - 召回率/准确率（RAG 简易版，MVP-5 完整版见 EvalModels.cs）
/// </summary>
public record RAGResult(
    int TotalCases,
    double RecallRate,
    double AccuracyRate,
    List<RAGCaseResult> Details);

/// <summary>RAG 简易评测单条结果</summary>
public record RAGCaseResult(
    string Question,
    string ExpectedAnswer,
    string ActualAnswer,
    bool Retrieved,
    bool Correct);

// ===================== 长期记忆与用户画像 =====================

/// <summary>
/// 用户画像条目 - 从对话中 LLM 提取的稳定特征
/// 对应 user_profiles 表
/// </summary>
public class UserProfile
{
    public int Id { get; set; }
    public string SessionId { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 记忆类型 - 区分原始消息/摘要/画像
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MemoryType
{
    /// <summary>原始对话消息</summary>
    Message,
    /// <summary>对话摘要（超长对话压缩后产生）</summary>
    Summary,
    /// <summary>用户画像条目</summary>
    Profile
}

/// <summary>
/// 长期记忆记录 - 用于跨会话的上下文召回
/// 对应 memory_records 表
/// </summary>
public class MemoryRecord
{
    public int Id { get; set; }
    public string SessionId { get; set; } = "";
    public MemoryType Type { get; set; } = MemoryType.Message;
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

// ===================== API 请求模型 =====================

/// <summary>
/// 检索测试请求（POST /api/kb/search）
/// </summary>
/// <param name="Query">检索查询文本</param>
/// <param name="DatabaseId">可选：限定知识库 ID；不填则跨库</param>
/// <param name="TopK">返回前 K 条，默认 5</param>
/// <param name="Rerank">是否启用重排序，默认 false</param>
public record SearchTestRequest(string Query, int? DatabaseId = null, int TopK = 5, bool Rerank = false);

/// <summary>创建知识库请求体（record 而非 ValueTuple，确保 JSON 正确绑定）</summary>
public record KbCreateRequest(string Name, string Description);

/// <summary>
/// RAG 评测请求（POST /api/kb/eval）
/// </summary>
/// <param name="DatabaseId">目标知识库 ID</param>
/// <param name="TestCases">评测用例列表</param>
public record EvalTestRequest(int DatabaseId, List<EvalTestCase> TestCases);
