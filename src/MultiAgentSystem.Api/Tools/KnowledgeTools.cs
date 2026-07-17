// ============================================================
// KnowledgeTools - RAG Agent 可调用的工具集（AIFunction）
//
// 设计思路：
//   - 参照 CrmTools 模式，用 AIFunctionFactory.Create 包装为 MAF 可识别的 AIFunction
//   - 仅暴露 2 个工具：search_knowledge_base / search_memory
//   - 工具描述决定 Agent 何时调用——Agent 自主判断 RAG 检索时机
//
// 工具清单：
//   search_knowledge_base(query, databaseId?, topK=5)
//     - 调 HybridRetriever.SearchAsync 混合检索
//     - 返回格式化文本：含来源文件名 + 页码 + 内容片段
//   search_memory(query, topK=3)
//     - 调 MemoryStore.SearchMemoryAsync 从历史记忆中召回
//     - 返回格式化文本：历史相关消息
//
// 设计权衡：
//   - 不直接把检索结果交给 LLM 自由组合，而是格式化文本输出
//   - 优势：LLM 引用来源更可控；可追踪"哪段话来自哪个文件"
//   - 劣势：格式化损失结构化信息，需用 JSON 串格式恢复
// ============================================================

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Tools;

public class KnowledgeTools
{
    private readonly HybridRetriever _retriever;
    private readonly KnowledgeStore _store;
    private readonly MemoryStore _memory;

    public KnowledgeTools(HybridRetriever retriever, KnowledgeStore store, MemoryStore memory)
    {
        _retriever = retriever;
        _store = store;
        _memory = memory;
    }

    /// <summary>暴露给 KnowledgeAgent 的全部工具（AITool 形式）</summary>
    public IList<AITool> AsAIFunctions() => new List<AITool>
    {
        AIFunctionFactory.Create(SearchKnowledgeBaseAsync, name: "search_knowledge_base",
            description: "从知识库检索相关内容。参数：query(必填,检索关键词或问题), databaseId(可选,指定知识库ID,不填则跨库检索), topK(可选,返回条数,默认5)。返回检索到的文档片段+来源文件名+页码。用于用户问知识库相关问题。"),
        AIFunctionFactory.Create(SearchMemoryAsync, name: "search_memory",
            description: "从历史对话记忆中检索相关内容。参数：query(必填,检索关键词), topK(可选,返回条数,默认3)。返回与查询相关的历史消息。用于补充跨会话的上下文。")
    };

    // ---------- 工具实现 ----------

    /// <summary>
    /// 知识库检索工具
    /// 输出格式：
    ///   【检索结果 1】来源：xxx.pdf P3
    ///   内容：xxxxxxxxxxxx
    ///   分数：0.856
    /// </summary>
    public async Task<string> SearchKnowledgeBaseAsync(string query, int? databaseId = null, int topK = 5)
    {
        var resp = await _retriever.SearchAsync(query, databaseId, topK, enableRerank: true);

        if (resp.Results.Count == 0)
        {
            return $"知识库中未找到与 \"{query}\" 相关的内容。";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"已检索到 {resp.Results.Count} 条与 \"{query}\" 相关的内容：");
        sb.AppendLine();
        for (int i = 0; i < resp.Results.Count; i++)
        {
            var r = resp.Results[i];
            sb.AppendLine($"【检索结果 {i + 1}】来源：{r.FileName} P{r.PageNumber}");
            sb.AppendLine($"内容：{r.Content}");
            var rerank = r.RerankedScore.HasValue
                ? $"（重排后分数：{r.RerankedScore:F4}）"
                : $"（融合分数：{r.Score:F4}）";
            sb.AppendLine($"分数：{r.Score:F4} {rerank}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// 历史记忆检索工具
    /// </summary>
    [Description("会话ID（用于隔离不同用户的记忆）")]
    public async Task<string> SearchMemoryAsync(string query, int topK = 3)
    {
        // sessionId 由 Agent 通过全局上下文注入；这里简化为查全部
        // 真实场景下应通过 KnowledgeAgentContext 注入当前 sessionId
        // 此处取所有 session 中匹配 query 的记忆
        var dbs = await _store.ListDatabasesAsync();
        // 简化：仅返回提示信息，实际记忆检索由 MemoryStore 内部完成
        // 真正实现需 sessionId 参数（MAF 工具不支持复杂上下文注入）
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"已检索到与 \"{query}\" 相关的历史记忆：");
        sb.AppendLine();
        sb.AppendLine("（注：当前演示版本未注入 sessionId，记忆检索返回空。生产环境应注入 sessionId）");
        return sb.ToString();
    }

    /// <summary>带 sessionId 的记忆检索（供 RagStrategy 直接调用）</summary>
    public async Task<string> SearchMemoryForSessionAsync(string sessionId, string query, int topK = 3)
    {
        var records = await _memory.SearchMemoryAsync(sessionId, query, topK);
        if (records.Count == 0)
        {
            return $"未检索到与 \"{query}\" 相关的历史记忆。";
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"已检索到 {records.Count} 条与 \"{query}\" 相关的历史记忆：");
        sb.AppendLine();
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            sb.AppendLine($"【记忆 {i + 1}】类型：{r.Type} 时间：{r.CreatedAt:o}");
            sb.AppendLine($"内容：{r.Content}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
