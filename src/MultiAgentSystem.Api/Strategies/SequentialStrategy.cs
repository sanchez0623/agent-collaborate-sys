// ============================================================
// SequentialStrategy - 顺序编排（MVP-1 流水线的策略化封装）
//
// 适用场景：研究 → 写作 → 审核 这类天然串行的任务
// 设计思路：上一个 Agent 的输出作为下一个 Agent 的输入
// 与其他模式对比：
//   - vs Concurrent：Sequential 强调依赖链，Concurrent 各路独立
//   - vs GroupChat：Sequential 固定顺序，GroupChat 由主持人动态选人
//
// 流程：Researcher → Writer → Critic
//   Critic APPROVE → 输出终稿
//   Critic REJECT  → 回到 Writer 重写（最多 2 轮）
// ============================================================

using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Agents;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Strategies;

public class SequentialStrategy : IOrchestrationStrategy
{
    private readonly AgentRegistry _registry;
    public OrchestrationMode Mode => OrchestrationMode.Sequential;

    /// <summary>最大重写轮次（首次 + 最多 2 轮重写）</summary>
    private const int MaxRewriteRounds = 2;

    public SequentialStrategy(AgentRegistry registry) => _registry = registry;

    public async Task<string> ExecuteAsync(
        string userMessage,
        IReadOnlyList<Models.ChatMessage> history,
        Action<OrchestrationEvent> onEvent,
        CancellationToken ct)
    {
        var researcher = _registry.Get(ResearcherAgent.Name);
        var writer = _registry.Get(WriterAgent.Name);
        var critic = _registry.Get(CriticAgent.Name);

        // ---------- 第 1 步：Researcher 调研 ----------
        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
            Agent: ResearcherAgent.Name, Status: "running", Round: 1));

        var researchInput = BuildResearchInput(userMessage, history);
        var researchOutput = await AgentRunner.RunAsync(researcher, researchInput, ct);

        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
            Agent: ResearcherAgent.Name, Status: "done", Output: researchOutput, Round: 1));

        // ---------- 第 2-3 步：Writer 撰写 → Critic 审核（可循环） ----------
        string draft = "";
        string? lastFeedback = null;
        for (int round = 1; round <= MaxRewriteRounds; round++)
        {
            // Writer
            onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
                Agent: WriterAgent.Name, Status: "running", Round: round));

            var writerInput = BuildWriterInput(userMessage, researchOutput, draft, lastFeedback);
            draft = await AgentRunner.RunAsync(writer, writerInput, ct);

            onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
                Agent: WriterAgent.Name, Status: "done", Output: draft, Round: round));

            // Critic
            onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
                Agent: CriticAgent.Name, Status: "running", Round: round));

            var criticInput = BuildCriticInput(userMessage, researchOutput, draft);
            var criticOutput = await AgentRunner.RunAsync(critic, criticInput, ct);

            bool approved = CriticAgent.TryParseVerdict(criticOutput, out var feedback);
            lastFeedback = feedback;

            if (approved)
            {
                onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
                    Agent: CriticAgent.Name, Status: "done", Output: criticOutput, Round: round));
                return draft;
            }
            else
            {
                onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
                    Agent: CriticAgent.Name, Status: "rejected", Output: criticOutput, Round: round));

                if (round == MaxRewriteRounds)
                {
                    return $"{draft}\n\n---\n*注：已重写 {MaxRewriteRounds} 轮，审核员仍有意见：*\n{feedback}";
                }
            }
        }
        return draft;
    }

    // ---------- 输入构造（与 MVP-1 逻辑一致，保持兼容） ----------

    private static string BuildResearchInput(string userMessage, IReadOnlyList<Models.ChatMessage> history)
    {
        if (history.Count == 0) return $"用户问题：{userMessage}";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(AgentRunner.FormatHistory(history));
        sb.AppendLine(userMessage);
        sb.AppendLine("\n请针对当前问题进行调研。");
        return sb.ToString();
    }

    private static string BuildWriterInput(string userMessage, string researchOutput, string previousDraft, string? feedback)
    {
        if (string.IsNullOrEmpty(previousDraft))
        {
            return $"""
                用户问题：{userMessage}

                研究员提供的调研素材：
                {researchOutput}

                请基于上述素材撰写回答。
                """;
        }
        return $"""
            用户问题：{userMessage}

            研究员的调研素材：
            {researchOutput}

            你上一版的回答：
            {previousDraft}

            审核员的修改意见：
            {feedback}

            请根据审核意见重写一个改进后的完整回答。
            """;
    }

    private static string BuildCriticInput(string userMessage, string researchOutput, string draft)
    {
        return $"""
            用户问题：{userMessage}

            研究员的调研素材：
            {researchOutput}

            Writer 提交的回答：
            {draft}

            请按照你的审核标准评估，输出 [APPROVE] 或 [REJECT] 判定。
            """;
    }
}
