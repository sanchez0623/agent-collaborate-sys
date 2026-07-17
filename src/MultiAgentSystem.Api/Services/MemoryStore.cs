// ============================================================
// MemoryStore - 长期记忆存储
//
// 设计意图：
//   - 复用 KnowledgeStore 的 memory_records / user_profiles 表
//   - 对话超过 6 轮自动摘要压缩，避免上下文爆炸
//   - 从历史对话中提取用户画像，跨会话复用
//   - 支持按关键词检索历史记忆，增强 RAG 上下文召回
//
// 记忆压缩策略：
//   - 阈值：6 轮对话（12 条消息）
//   - 触发：超过阈值后调 LLM 总结历史，生成摘要写入 memory_records
//   - 优势：摘要保留长程语义，原始消息可降级为只读归档
//   - 注意：本实现保留原消息不删，仅追加 Summary 类型记录
//   - 后续若需精简，可保留"最近 N 条 + 1 条摘要"策略
//
// 用户画像提取策略：
//   - 从对话中识别稳定特征：职业 / 偏好 / 关注领域 / 技术栈
//   - LLM 输出 JSON：{"key": "value", ...}
//   - 写入 user_profiles（UPSERT 语义，同 key 覆盖）
//   - 跨会话复用：下次会话开始时加载画像，注入 system prompt
// ============================================================

using System.Text.Json;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class MemoryStore
{
    private readonly KnowledgeStore _store;
    private readonly EmbeddingService _embeddings;
    private readonly VectorStore _vectors;

    /// <summary>触发摘要的消息数阈值（6 轮对话 = 12 条消息）</summary>
    private const int SummaryThreshold = 12;

    public MemoryStore(KnowledgeStore store, EmbeddingService embeddings, VectorStore vectors)
    {
        _store = store;
        _embeddings = embeddings;
        _vectors = vectors;
    }

    /// <summary>添加对话消息到长期记忆</summary>
    public async Task AddMessageAsync(string sessionId, string role, string content)
    {
        // role 直接拼入 content，便于检索时区分上下文
        var tagged = $"[{role}] {content}";
        await _store.AddMemoryAsync(sessionId, MemoryType.Message, tagged);
    }

    /// <summary>取最近 N 条消息（用于上下文构建）</summary>
    public async Task<List<MemoryRecord>> GetRecentMessagesAsync(string sessionId, int maxCount = 20)
    {
        return await _store.GetMemoryBySessionAsync(sessionId, MemoryType.Message, maxCount);
    }

    /// <summary>取摘要（如有）</summary>
    public async Task<string?> GetSummaryAsync(string sessionId)
    {
        var summaries = await _store.GetMemoryBySessionAsync(sessionId, MemoryType.Summary, 5);
        if (summaries.Count == 0) return null;
        // 取最新一条摘要
        return summaries.Last().Content;
    }

    /// <summary>
    /// 若对话超过阈值，调 LLM 生成摘要并写入 memory_records
    /// 幂等：已存在摘要且未达下一次触发量则跳过
    /// </summary>
    public async Task SummarizeIfNeededAsync(string sessionId, IChatClient chatClient)
    {
        var messages = await _store.GetMemoryBySessionAsync(sessionId, MemoryType.Message, 1000);
        var summaries = await _store.GetMemoryBySessionAsync(sessionId, MemoryType.Summary, 100);

        // 简化：直接基于全部消息判断；已存在摘要时，只对"超出阈值"的增量部分做二次摘要
        if (messages.Count < SummaryThreshold) return;

        // 已存在摘要：若新增消息不足以触发二次摘要，跳过
        // 阈值：自上次摘要后又累积了 SummaryThreshold 条消息
        if (summaries.Count > 0 && messages.Count < summaries.Count * SummaryThreshold + SummaryThreshold)
            return;

        var dialogueText = string.Join("\n", messages.Select(m => m.Content));
        var prompt = $"""
            请将以下对话历史压缩为不超过 200 字的中文摘要，保留：
            - 用户的核心诉求
            - 已确认的关键事实（如人名/项目/时间）
            - 未解决的问题

            对话历史：
            {dialogueText}

            直接输出摘要文本，不要加额外说明。
            """;

        try
        {
            var msgs = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System, "你是对话压缩助手，擅长提炼长对话的核心信息。"),
                new(Microsoft.Extensions.AI.ChatRole.User, prompt)
            };
            var resp = await chatClient.GetResponseAsync(msgs);
            var summaryText = resp.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(summaryText))
            {
                await _store.AddMemoryAsync(sessionId, MemoryType.Summary, summaryText);
            }
        }
        catch
        {
            // LLM 调用失败不影响主流程
        }
    }

    /// <summary>
    /// 从对话中提取用户画像（LLM 调用）
    /// 输出 JSON：{"职业": "...", "关注领域": "...", ...}
    /// </summary>
    public async Task ExtractUserProfileAsync(string sessionId, IChatClient chatClient)
    {
        var messages = await _store.GetMemoryBySessionAsync(sessionId, MemoryType.Message, 1000);
        if (messages.Count == 0) return;

        var dialogueText = string.Join("\n", messages.Select(m => m.Content));
        var prompt = $"""
            从以下对话中提取用户的稳定画像特征。
            输出 JSON 格式，键为特征名（如"职业"/"关注领域"/"技术栈"/"偏好"），值为对应内容。
            无明确信息时该键省略。仅输出 JSON，不要其他文字。

            对话内容：
            {dialogueText}
            """;

        try
        {
            var msgs = new List<Microsoft.Extensions.AI.ChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System, "你是用户画像分析助手，仅输出 JSON。"),
                new(Microsoft.Extensions.AI.ChatRole.User, prompt)
            };
            var resp = await chatClient.GetResponseAsync(msgs);
            var json = resp.Text?.Trim() ?? "";
            // 去除可能的 markdown 代码块包裹
            if (json.StartsWith("```"))
            {
                json = json.Replace("```json", "").Replace("```", "").Trim();
            }
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();
                await _store.UpsertProfileAsync(sessionId, prop.Name, value);
            }
        }
        catch
        {
            // 画像提取失败不影响主流程
        }
    }

    /// <summary>
    /// 从历史记忆中检索与 query 相关的内容
    /// 简化策略：用关键词 LIKE 匹配（向量检索可作后续增强）
    /// </summary>
    public async Task<List<MemoryRecord>> SearchMemoryAsync(string sessionId, string query, int topK = 3)
    {
        // 取 query 的关键词进行 LIKE 搜索
        // 简化：直接用 query 全文做 LIKE
        return await _store.SearchMemoryByKeywordAsync(sessionId, query, topK);
    }

    /// <summary>获取用户画像（用于注入 system prompt）</summary>
    public async Task<List<UserProfile>> GetProfilesAsync(string sessionId)
    {
        return await _store.GetProfilesAsync(sessionId);
    }
}
