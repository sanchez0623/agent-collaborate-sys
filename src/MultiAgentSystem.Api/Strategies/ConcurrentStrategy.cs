// ============================================================
// ConcurrentStrategy - 并行编排
//
// 适用场景：需要多视角独立思考的问题
//   例：用户问"如何评估这个产品方案" → 分析师/程序员/顾问各自从自己角度回答
//
// 设计思路：
//   1. N 个专家 Agent 同时 Task.Run 并行执行（同一问题）
//   2. 所有 Agent 完成后，由 Coordinator(Synthesizer) 汇总
//   3. 通过 ParallelLane 字段区分泳道，前端渲染并行进度条
//
// 与其他模式对比：
//   - vs Sequential：Concurrent 无依赖，并行；Sequential 串联
//   - vs GroupChat：Concurrent 各自独立，无交流；GroupChat 互相讨论
// ============================================================

using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Agents;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Strategies;

public class ConcurrentStrategy : IOrchestrationStrategy
{
    private readonly AgentRegistry _registry;
    public OrchestrationMode Mode => OrchestrationMode.Concurrent;

    /// <summary>并行参与的专家 Agent（除 Coordinator 外的 4 个角色）</summary>
    private static readonly string[] ParallelAgents =
        { AnalystAgent.Name, CoderAgent.Name, ConsultantAgent.Name, ResearcherAgent.Name };

    public ConcurrentStrategy(AgentRegistry registry) => _registry = registry;

    public async Task<string> ExecuteAsync(
        string userMessage,
        IReadOnlyList<Models.ChatMessage> history,
        Action<OrchestrationEvent> onEvent,
        CancellationToken ct)
    {
        var historyCtx = AgentRunner.FormatHistory(history);
        var parallelInput = $"{historyCtx}{userMessage}\n\n请从你的专业角度独立给出观点（不要假设其他角色的存在）。";

        // ---------- 第 1 阶段：N 个专家并行执行 ----------
        // 每个 Agent 在独立 Task 中运行，完成后立即推送 AgentCompleted 事件
        // 用 ParallelLane 区分泳道，前端据此渲染并行进度条
        var lane = 0;
        var tasks = ParallelAgents.Select(name => Task.Run(async () =>
        {
            var agent = _registry.Get(name);
            var myLane = System.Threading.Interlocked.Increment(ref lane) - 1;

            onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
                Agent: name, Status: "running", ParallelLane: myLane));

            var output = await AgentRunner.RunAsync(agent, parallelInput, ct);

            onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
                Agent: name, Status: "done", Output: output, ParallelLane: myLane));

            return (name, output);
        }, ct)).ToList();

        var results = await Task.WhenAll(tasks);

        // ---------- 第 2 阶段：Coordinator 汇总 ----------
        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
            Agent: CoordinatorAgent.Name, Status: "running", Round: 2));

        var synthesizer = _registry.Get(CoordinatorAgent.Name);
        var synthInput = BuildSynthesizeInput(userMessage, results);
        var finalOutput = await AgentRunner.RunAsync(synthesizer, synthInput, ct);

        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
            Agent: CoordinatorAgent.Name, Status: "done", Output: finalOutput, Round: 2));

        return finalOutput;
    }

    private static string BuildSynthesizeInput(string userMessage, IReadOnlyList<(string name, string output)> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"用户问题：{userMessage}");
        sb.AppendLine();
        sb.AppendLine("以下是多个专家的独立观点，请整合为一份结构化最终回答：");
        sb.AppendLine();
        foreach (var (name, output) in results)
        {
            sb.AppendLine($"### {name} 的观点");
            sb.AppendLine(output);
            sb.AppendLine();
        }
        sb.AppendLine("请用 Markdown 整合以上观点，每段标注来源，末尾给综合结论。");
        return sb.ToString();
    }
}
