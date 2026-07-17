// ============================================================
// HandoffStrategy - 链式移交编排
//
// 适用场景：客服/分级支持场景
//   例：用户问"我的代码报错" → Support 先尝试回答 → 搞不定 → [HANDOFF] Coder
//   例：用户问"销售数据怎么看" → Support → [HANDOFF] Analyst
//
// 设计思路：
//   1. Support 作为入口 Agent，尝试直接回答
//   2. Support 输出 [HANDOFF] AgentName 时，移交给目标 Agent
//   3. 目标 Agent 接管后给出最终答案（最多 2 次移交，防死循环）
//
// 与其他模式对比：
//   - vs Sequential：Handoff 是"干不了再转"，Sequential 是"必经流程"
//   - vs Magentic：Handoff 由 Agent 自己判断转不转，Magentic 由 Router 预判
// ============================================================

using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Agents;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Strategies;

public class HandoffStrategy : IOrchestrationStrategy
{
    private readonly AgentRegistry _registry;
    public OrchestrationMode Mode => OrchestrationMode.Handoff;

    /// <summary>最多移交次数（防死循环）</summary>
    private const int MaxHandoffs = 2;

    public HandoffStrategy(AgentRegistry registry) => _registry = registry;

    public async Task<string> ExecuteAsync(
        string userMessage,
        IReadOnlyList<Models.ChatMessage> history,
        Action<OrchestrationEvent> onEvent,
        CancellationToken ct)
    {
        var historyCtx = AgentRunner.FormatHistory(history);
        var currentAgentName = SupportAgent.Name;
        var currentInput = $"{historyCtx}用户问题：{userMessage}";
        var handoffChain = new List<string> { SupportAgent.Name }; // 移交路径回溯

        for (int hop = 0; hop <= MaxHandoffs; hop++)
        {
            var agent = _registry.Get(currentAgentName);

            onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
                Agent: currentAgentName, Status: "running", Round: hop + 1));

            var output = await AgentRunner.RunAsync(agent, currentInput, ct);

            // 检查是否要求移交
            if (SupportAgent.TryParseHandoff(output, out var targetAgent, out var reason)
                && _registry.TryGet(targetAgent, out var nextAgent)
                && !handoffChain.Contains(targetAgent)) // 防止 A→B→A 死循环
            {
                // 触发移交事件
                onEvent(new OrchestrationEvent(OrchestrationEventType.Handoff,
                    Agent: currentAgentName,
                    FromAgent: currentAgentName,
                    ToAgent: targetAgent,
                    Reason: reason,
                    Round: hop + 1));

                // 当前 Agent 也算完成（虽未给出最终答案）
                onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
                    Agent: currentAgentName, Status: "handoff", Output: $"→ 移交给 {targetAgent}", Round: hop + 1));

                // 切换到目标 Agent，把上下文带过去
                handoffChain.Add(targetAgent);
                currentAgentName = targetAgent;
                currentInput = BuildHandoffInput(userMessage, reason, handoffChain);
                continue;
            }

            // 未要求移交 → 当前 Agent 给出最终答案
            onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
                Agent: currentAgentName, Status: "done", Output: output, Round: hop + 1));

            // 若发生过移交，末尾附移交路径（方便前端展示链路图）
            if (handoffChain.Count > 1)
            {
                return $"{output}\n\n---\n*移交路径：{string.Join(" → ", handoffChain)}*";
            }
            return output;
        }

        // 达到最大移交次数仍未解决：让最后一个 Agent 强制给出答案
        var lastAgent = _registry.Get(currentAgentName);
        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
            Agent: currentAgentName, Status: "running", Round: MaxHandoffs + 2));

        var finalInput = $"{currentInput}\n\n[系统提示] 已达最大移交次数，请直接给出你能提供的最佳答案。";
        var finalOutput = await AgentRunner.RunAsync(lastAgent, finalInput, ct);

        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
            Agent: currentAgentName, Status: "done", Output: finalOutput, Round: MaxHandoffs + 2));

        return $"{finalOutput}\n\n---\n*移交路径：{string.Join(" → ", handoffChain)}*";
    }

    private static string BuildHandoffInput(string userMessage, string reason, List<string> chain)
    {
        return $"""
            用户原始问题：{userMessage}

            上一环节 {chain[^2]} 移交给你，原因：
            {reason}

            移交路径：{string.Join(" → ", chain)}

            请基于上述上下文给出最终答案。
            """;
    }
}
