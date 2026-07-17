// ============================================================
// MagenticStrategy - 智能路由编排（MAF 特色模式）
//
// 适用场景：不确定该用哪种 Agent 的开放性问题
//   例：用户问"帮我看看这段代码" → Router 判断 → 路由给 Coder
//   例：用户问"销售下滑怎么办" → Router 判断 → 路由给 Analyst 或 Consultant
//
// 设计思路：
//   1. Coordinator 作为 Router，分析用户意图
//   2. 输出 [ROUTE] AgentName 决策，附理由
//   3. 被选中的 Agent 直接给出最终答案
//
// 与其他模式对比：
//   - vs Handoff：Magentic 由 Router 预判路由，Handoff 由 Agent 自己事后判断
//   - vs GroupChat：Magentic 只选一个 Agent，GroupChat 多个 Agent 讨论
// ============================================================

using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Agents;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Strategies;

public class MagenticStrategy : IOrchestrationStrategy
{
    private readonly AgentRegistry _registry;
    public OrchestrationMode Mode => OrchestrationMode.Magentic;

    /// <summary>Router 可路由到的 Agent 池</summary>
    private static readonly string[] RoutableAgents =
        { ResearcherAgent.Name, AnalystAgent.Name, CoderAgent.Name, ConsultantAgent.Name, SupportAgent.Name, WriterAgent.Name };

    public MagenticStrategy(AgentRegistry registry) => _registry = registry;

    public async Task<string> ExecuteAsync(
        string userMessage,
        IReadOnlyList<Models.ChatMessage> history,
        Action<OrchestrationEvent> onEvent,
        CancellationToken ct)
    {
        var historyCtx = AgentRunner.FormatHistory(history);
        var router = _registry.Get(CoordinatorAgent.Name);

        // ---------- 第 1 步：Router 决策 ----------
        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
            Agent: CoordinatorAgent.Name, Status: "running", Round: 1));

        var routerInput = BuildRouterInput(userMessage, historyCtx);
        var routerOutput = await AgentRunner.RunAsync(router, routerInput, ct);

        // 解析路由决策
        if (!CoordinatorAgent.TryParseRoute(routerOutput, out var targetAgent, out var reason)
            || !RoutableAgents.Contains(targetAgent, StringComparer.OrdinalIgnoreCase))
        {
            // 路由失败：兜底用 Writer 回答
            targetAgent = WriterAgent.Name;
            reason = "路由解析失败，降级到通用 Writer";
        }

        // 推送路由决策事件（前端渲染决策树动画）
        onEvent(new OrchestrationEvent(OrchestrationEventType.RouteDecision,
            Agent: targetAgent, Reason: reason, Output: routerOutput, Round: 1));

        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
            Agent: CoordinatorAgent.Name, Status: "done", Output: routerOutput, Round: 1));

        // ---------- 第 2 步：被路由到的 Agent 执行 ----------
        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
            Agent: targetAgent, Status: "running", Round: 2));

        var executor = _registry.Get(targetAgent);
        var execInput = $"{historyCtx}用户问题：{userMessage}\n\n请直接给出你的专业回答。";
        var finalOutput = await AgentRunner.RunAsync(executor, execInput, ct);

        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
            Agent: targetAgent, Status: "done", Output: finalOutput, Round: 2));

        // 末尾附路由信息，方便前端展示
        return $"{finalOutput}\n\n---\n*路由决策：{CoordinatorAgent.Name} → {targetAgent}（{reason}）*";
    }

    private static string BuildRouterInput(string userMessage, string historyCtx)
    {
        return $"""
            {historyCtx}用户问题：{userMessage}

            可路由的 Agent：
            - Researcher：需要查证事实、实时信息、调研
            - Analyst：数据分析、统计、指标解读
            - Coder：代码生成、技术实现
            - Consultant：产品方案、需求拆解、优先级
            - Support：故障排查、FAQ、问题分类
            - Writer：通用写作、内容创作

            请分析用户意图，选择最合适的一个 Agent 处理。
            输出格式：
            [ROUTE] AgentName
            <路由理由，2-3 句话>
            """;
    }
}
