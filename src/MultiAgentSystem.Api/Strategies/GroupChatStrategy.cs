// ============================================================
// GroupChatStrategy - 群聊编排
//
// 适用场景：需要多角色自由讨论、观点碰撞的开放性问题
//   例：用户问"如何设计这个功能" → 主持人选 Analyst 发言 → 选 Consultant 反驳 → 选 Coder 补充 → 结束
//
// 设计思路（参考 AutoGen GroupChat）：
//   1. Coordinator 作为主持人，根据上下文选择下一个发言者
//   2. 被选中的 Agent 看到完整讨论历史，给出自己的观点
//   3. 循环 N 轮（默认 3 轮）后，Coordinator 汇总成最终答案
//
// 与其他模式对比：
//   - vs Concurrent：GroupChat 有交流（看到彼此发言），Concurrent 完全独立
//   - vs Sequential：GroupChat 主持人动态选人，Sequential 固定顺序
// ============================================================

using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Agents;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Strategies;

public class GroupChatStrategy : IOrchestrationStrategy
{
    private readonly AgentRegistry _registry;
    public OrchestrationMode Mode => OrchestrationMode.GroupChat;

    /// <summary>群聊最大轮次（防止无限讨论）</summary>
    private const int MaxTurns = 3;

    /// <summary>可参与群聊的 Agent（不含 Coordinator 主持人）</summary>
    private static readonly string[] Participants =
        { ResearcherAgent.Name, AnalystAgent.Name, CoderAgent.Name, ConsultantAgent.Name, SupportAgent.Name };

    public GroupChatStrategy(AgentRegistry registry) => _registry = registry;

    public async Task<string> ExecuteAsync(
        string userMessage,
        IReadOnlyList<Models.ChatMessage> history,
        Action<OrchestrationEvent> onEvent,
        CancellationToken ct)
    {
        var historyCtx = AgentRunner.FormatHistory(history);
        var transcript = new List<(string agent, string speech)>(); // 群聊记录

        var coordinator = _registry.Get(CoordinatorAgent.Name);

        for (int turn = 1; turn <= MaxTurns; turn++)
        {
            // ---------- 主持人选择下一个发言者 ----------
            onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
                Agent: CoordinatorAgent.Name, Status: "running", Round: turn));

            var coordinatorInput = BuildCoordinatorInput(userMessage, transcript, turn == MaxTurns);
            var coordinatorOutput = await AgentRunner.RunAsync(coordinator, coordinatorInput, ct);

            // 最后一轮：让 Coordinator 直接汇总，不再选人
            if (turn == MaxTurns || !CoordinatorAgent.TryParseNext(coordinatorOutput, out var nextAgent, out var reason)
                || !Participants.Contains(nextAgent, StringComparer.OrdinalIgnoreCase))
            {
                onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
                    Agent: CoordinatorAgent.Name, Status: "done", Output: coordinatorOutput, Round: turn));

                // Coordinator 的最后一轮输出即最终汇总
                if (turn == MaxTurns)
                {
                    var synthInput = BuildFinalizeInput(userMessage, transcript);
                    var final = await AgentRunner.RunAsync(coordinator, synthInput, ct);
                    onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
                        Agent: CoordinatorAgent.Name, Status: "done", Output: final, Round: turn));
                    return final;
                }
                // 主持人提前结束：直接用其输出作为最终答案
                return coordinatorOutput;
            }

            onEvent(new OrchestrationEvent(OrchestrationEventType.GroupTurn,
                Agent: nextAgent, Reason: reason, Round: turn));

            // ---------- 被选中的 Agent 发言 ----------
            onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
                Agent: nextAgent, Status: "running", Round: turn));

            var speaker = _registry.Get(nextAgent);
            var speakerInput = BuildSpeakerInput(userMessage, nextAgent, transcript);
            var speech = await AgentRunner.RunAsync(speaker, speakerInput, ct);

            transcript.Add((nextAgent, speech));

            onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
                Agent: nextAgent, Status: "done", Output: speech, Round: turn));
        }

        // 理论上不会到达（循环内已 return）
        return "群聊已结束，但未生成汇总。";
    }

    private static string BuildCoordinatorInput(string userMessage, List<(string agent, string speech)> transcript, bool isLastTurn)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"用户问题：{userMessage}");
        sb.AppendLine($"可选发言者：{string.Join(" / ", Participants)}");
        sb.AppendLine();
        if (transcript.Count == 0)
        {
            sb.AppendLine("【当前状态】讨论尚未开始，请选择第一个发言者。");
        }
        else
        {
            sb.AppendLine("【已有讨论】");
            foreach (var (agent, speech) in transcript)
            {
                sb.AppendLine($"[{agent}] {speech}");
            }
        }
        if (isLastTurn)
        {
            sb.AppendLine("\n这是最后一轮，请直接汇总以上讨论，输出结构化最终答案（不要再 [NEXT]）。");
        }
        else
        {
            sb.AppendLine("\n请选择下一个最该发言的 Agent，输出格式：[NEXT] AgentName");
        }
        return sb.ToString();
    }

    private static string BuildSpeakerInput(string userMessage, string selfName, List<(string agent, string speech)> transcript)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"用户问题：{userMessage}");
        sb.AppendLine($"你是 {selfName}，请从你的专业角度发言。");
        sb.AppendLine();
        if (transcript.Count > 0)
        {
            sb.AppendLine("【已有讨论】");
            foreach (var (agent, speech) in transcript)
            {
                sb.AppendLine($"[{agent}] {speech}");
            }
            sb.AppendLine("\n请基于以上讨论，补充你的观点（可以赞同/反驳/补充），200 字以内。");
        }
        else
        {
            sb.AppendLine("你是第一个发言者，请给出你的初步观点，200 字以内。");
        }
        return sb.ToString();
    }

    private static string BuildFinalizeInput(string userMessage, List<(string agent, string speech)> transcript)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"用户问题：{userMessage}");
        sb.AppendLine("【完整讨论记录】");
        foreach (var (agent, speech) in transcript)
        {
            sb.AppendLine($"[{agent}] {speech}");
        }
        sb.AppendLine("\n请汇总以上讨论，输出结构化最终答案，每段标注来源 Agent，末尾给综合结论。");
        return sb.ToString();
    }
}
