// ============================================================
// AgentOrchestrator - 3 Agent 流水线编排服务
// 顺序：Researcher → Writer → Critic
//
// 核心流程：
//   1. Researcher 调研用户问题 → 产出"调研素材"
//   2. Writer 基于素材撰写回答 → 产出"草稿"
//   3. Critic 审核草稿：
//        - APPROVE → 输出终稿，结束
//        - REJECT  → 把反馈回喂给 Writer 重写 → 回到步骤 3
//   4. 最多重写 MaxRewriteRounds 轮；仍 REJECT 则输出最后一版草稿
//
// 通过 IProgress<AgentStepEvent> 回调把每步进度推给 SSE 端点
// 前端据此实时显示"哪个 Agent 在工作 + 当前产出"
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

/// <summary>
/// 单个 Agent 步骤的进度事件（推送给前端 SSE 的 agent_step）
/// </summary>
/// <param name="Agent">Agent 名称：Researcher / Writer / Critic</param>
/// <param name="Status">状态：running / done / rejected</param>
/// <param name="Output">该步骤的产出文本（running 时可为空）</param>
/// <param name="Round">当前写作轮次（首次=1，每次退回+1）</param>
public record AgentStepEvent(string Agent, string Status, string Output, int Round);

public class AgentOrchestrator
{
    /// <summary>最大重写轮次（首次写作 + 最多 2 轮重写）</summary>
    private const int MaxRewriteRounds = 2;

    private readonly ChatClientAgent _researcher;
    private readonly ChatClientAgent _writer;
    private readonly ChatClientAgent _critic;

    public AgentOrchestrator(ChatClientAgent researcher, ChatClientAgent writer, ChatClientAgent critic)
    {
        _researcher = researcher;
        _writer = writer;
        _critic = critic;
    }

    /// <summary>
    /// 运行完整流水线
    /// </summary>
    /// <param name="userMessage">用户本次提问</param>
    /// <param name="history">历史对话（用于多轮上下文）</param>
    /// <param name="onStep">进度回调（同步调用，每个 Agent 步骤推送一次或多次）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>最终输出（Critic 通过的终稿，或最后一版草稿）</returns>
    public async Task<string> RunAsync(
        string userMessage,
        IReadOnlyList<Models.ChatMessage> history,
        Action<AgentStepEvent>? onStep,
        CancellationToken ct)
    {
        // ---------- 第 1 步：Researcher 调研 ----------
        onStep?.Invoke(new AgentStepEvent(Agents.ResearcherAgent.Name, "running", "", 1));

        // 把历史上下文 + 当前问题一起交给 Researcher
        // 历史 ChatMessage 列表直接作为 RunAsync 的 messages 参数传入
        var researchInput = BuildResearchInput(userMessage, history);
        var researchResult = await _researcher.RunAsync(researchInput, cancellationToken: ct);
        var researchOutput = ExtractText(researchResult);

        onStep?.Invoke(new AgentStepEvent(Agents.ResearcherAgent.Name, "done", researchOutput, 1));

        // ---------- 第 2-3 步：Writer 撰写 → Critic 审核（可循环） ----------
        string draft = "";
        for (int round = 1; round <= MaxRewriteRounds; round++)
        {
            // --- Writer 撰写 ---
            onStep?.Invoke(new AgentStepEvent(Agents.WriterAgent.Name, "running", "", round));

            var writerInput = BuildWriterInput(userMessage, researchOutput, draft, round == 1 ? null : LastFeedback);
            var writerResult = await _writer.RunAsync(writerInput, cancellationToken: ct);
            draft = ExtractText(writerResult);

            onStep?.Invoke(new AgentStepEvent(Agents.WriterAgent.Name, "done", draft, round));

            // --- Critic 审核 ---
            onStep?.Invoke(new AgentStepEvent(Agents.CriticAgent.Name, "running", "", round));

            var criticInput = BuildCriticInput(userMessage, researchOutput, draft);
            var criticResult = await _critic.RunAsync(criticInput, cancellationToken: ct);
            var criticOutput = ExtractText(criticResult);

            // 解析审核结论
            bool approved = Agents.CriticAgent.TryParseVerdict(criticOutput, out var feedback);
            LastFeedback = feedback;

            if (approved)
            {
                // 审核通过：推送 Critic 完成 → 返回终稿
                onStep?.Invoke(new AgentStepEvent(Agents.CriticAgent.Name, "done", criticOutput, round));
                return draft;
            }
            else
            {
                // 审核退回：推送 rejected 事件（前端可显示回退动画）
                onStep?.Invoke(new AgentStepEvent(Agents.CriticAgent.Name, "rejected", criticOutput, round));

                // 如果还有重写机会，继续循环；否则直接返回最后一版草稿
                if (round == MaxRewriteRounds)
                {
                    // 已达最大轮次仍不通过：把 Critic 意见附在末尾作为终稿输出
                    return $"{draft}\n\n---\n*注：本回答已重写 {MaxRewriteRounds} 轮，审核员仍有以下意见：*\n{feedback}";
                }
                // 否则进入下一轮：Writer 会基于 feedback 重写
            }
        }

        // 理论上不会执行到这里（for 循环内已 return）
        return draft;
    }

    /// <summary>暂存最近一次 Critic 反馈（重写时喂给 Writer）</summary>
    private string? LastFeedback { get; set; }

    // ---------- 输入构造：每个 Agent 收到的指令文本 ----------

    private static string BuildResearchInput(string userMessage, IReadOnlyList<Models.ChatMessage> history)
    {
        // 若有多轮历史，把历史简要列出让 Researcher 知道上下文
        if (history.Count == 0)
        {
            return $"用户问题：{userMessage}";
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("以下是之前的对话历史（供参考）：");
        foreach (var m in history.TakeLast(8)) // 最多保留最近8条，避免上下文过长
        {
            var role = m.Role == "user" ? "用户" : (m.Role == "assistant" ? "助手" : "系统");
            sb.AppendLine($"[{role}] {m.Content}");
        }
        sb.AppendLine();
        sb.AppendLine($"当前用户问题：{userMessage}");
        sb.AppendLine();
        sb.AppendLine("请针对当前问题进行调研。");
        return sb.ToString();
    }

    private static string BuildWriterInput(string userMessage, string researchOutput, string previousDraft, string? feedback)
    {
        if (string.IsNullOrEmpty(previousDraft))
        {
            // 首次写作
            return $"""
                用户问题：{userMessage}

                研究员提供的调研素材：
                {researchOutput}

                请基于上述素材撰写回答。
                """;
        }
        else
        {
            // 重写：带上 Critic 反馈
            return $"""
                用户问题：{userMessage}

                研究员提供的调研素材：
                {researchOutput}

                你上一版的回答：
                {previousDraft}

                审核员给出的修改意见：
                {feedback}

                请根据审核意见重写一个改进后的完整回答。
                """;
        }
    }

    private static string BuildCriticInput(string userMessage, string researchOutput, string draft)
    {
        return $"""
            用户问题：{userMessage}

            研究员的调研素材：
            {researchOutput}

            Writer 提交的回答：
            {draft}

            请按照你的审核标准评估上述回答，并输出 [APPROVE] 或 [REJECT] 判定。
            """;
    }

    /// <summary>
    /// 从 AgentResponse 提取文本输出
    /// MAF 1.13.0 GA：AgentResponse.Text 聚合所有 Messages 的文本
    /// </summary>
    private static string ExtractText(AgentResponse response)
    {
        // 优先用聚合 Text 属性（官方推荐）
        if (!string.IsNullOrWhiteSpace(response.Text)) return response.Text.Trim();

        // 备用：从 Messages 手工拼接
        var sb = new System.Text.StringBuilder();
        foreach (var msg in response.Messages)
        {
            if (!string.IsNullOrEmpty(msg.Text)) sb.Append(msg.Text);
        }
        return sb.ToString().Trim();
    }
}
