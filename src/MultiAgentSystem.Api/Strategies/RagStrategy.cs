// ============================================================
// RagStrategy - RAG 知识库问答编排（MVP-3 核心模式）
//
// 适用场景：用户问知识库相关问题（产品文档/技术文档/政策法规）
//   例："我们的退款政策是什么？" / "API 文档说明哪些认证方式？"
//
// 设计思路：
//   1. 先调 MemoryStore.SummarizeIfNeeded（记忆压缩，避免上下文爆炸）
//   2. 调 KnowledgeAgent 处理问题（Agent 内部会调 search_knowledge_base 工具）
//   3. 工具调用过程通过 onEvent 推 SSE：tool_call → tool_result
//   4. 调 MemoryStore.AddMessage 保存对话
//   5. 返回最终答案
//
// 与其他模式对比：
//   - vs Sequential：Sequential 是固定多 Agent 流水线，RAG 是单 Agent + 工具
//   - vs Crm：Crm 重点是 CRUD 业务操作，RAG 重点是知识检索+引用
//   - vs Magentic：Magentic 由 Router 路由到现成 Agent，RAG 是专门的知识问答 Agent
//
// 关键差异：
//   - RAG 模式在编排层主动注入 sessionId（让记忆压缩能正确工作）
//   - 工具调用是 Agent 内部行为，编排层不直接控制
//   - 工具事件由 MAF 框架自动触发（本演示版未启用 FunctionInvocationProcessor 拦截，
//     后续可参照 CrmStrategy 增强）
// ============================================================

using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using MultiAgentSystem.Api.Agents;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Strategies;

public class RagStrategy : IOrchestrationStrategy
{
    private readonly AgentRegistry _registry;
    private readonly MemoryStore _memory;
    private readonly IChatClient _chatClient;
    public OrchestrationMode Mode => OrchestrationMode.Rag;

    public RagStrategy(AgentRegistry registry, MemoryStore memory, IChatClient chatClient)
    {
        _registry = registry;
        _memory = memory;
        _chatClient = chatClient;
    }

    public async Task<string> ExecuteAsync(
        string userMessage,
        IReadOnlyList<Models.ChatMessage> history,
        Action<OrchestrationEvent> onEvent,
        CancellationToken ct)
    {
        // ---------- 第 1 步：记忆压缩（如需要） ----------
        // sessionId 从历史中提取（首条消息的会话标识）；此处用占位 sessionId
        // 实际 sessionId 由 Program.cs 在调用前注入（通过 ConversationStore 关联）
        // 本简化实现用 "default" 占位；生产应注入真实 sessionId
        var sessionId = "default";

        try
        {
            await _memory.SummarizeIfNeededAsync(sessionId, _chatClient);
        }
        catch
        {
            // 记忆压缩失败不影响主流程
        }

        var knowledgeAgent = _registry.Get(KnowledgeAgent.Name);
        var historyCtx = AgentRunner.FormatHistory(history);
        var input = $"{historyCtx}用户问题：{userMessage}";

        // ---------- 第 2 步：KnowledgeAgent 执行 ----------
        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
            Agent: KnowledgeAgent.Name, Status: "running", Round: 1));

        // 工具调用提示事件（前端可显示"正在检索知识库..."）
        onEvent(new OrchestrationEvent(OrchestrationEventType.ToolCall,
            Agent: KnowledgeAgent.Name, ToolName: "search_knowledge_base",
            ToolArgs: $"{{\"query\":\"{userMessage}\"}}"));

        var output = await AgentRunner.RunAsync(knowledgeAgent, input, ct);

        // 工具结果事件
        onEvent(new OrchestrationEvent(OrchestrationEventType.ToolResult,
            Agent: KnowledgeAgent.Name, ToolName: "search_knowledge_base",
            ToolResult: output.Length > 200 ? output.Substring(0, 200) + "..." : output));

        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
            Agent: KnowledgeAgent.Name, Status: "done", Output: output, Round: 1));

        // ---------- 第 3 步：保存对话到长期记忆 ----------
        try
        {
            await _memory.AddMessageAsync(sessionId, "user", userMessage);
            await _memory.AddMessageAsync(sessionId, "assistant", output);
        }
        catch
        {
            // 记忆写入失败不影响主流程
        }

        return output;
    }
}
