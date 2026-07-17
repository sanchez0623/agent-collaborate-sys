// ============================================================
// AgentRunner - Agent 执行辅助
// 作用：封装 ChatClientAgent.RunAsync + 文本提取的通用逻辑
// 所有编排策略共用，避免重复代码
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Services;

public static class AgentRunner
{
    /// <summary>
    /// 运行 Agent 并提取文本输出
    /// </summary>
    /// <param name="agent">要运行的 Agent</param>
    /// <param name="input">输入文本</param>
    /// <param name="ct">取消令牌</param>
    public static async Task<string> RunAsync(ChatClientAgent agent, string input, CancellationToken ct)
    {
        var response = await agent.RunAsync(input, cancellationToken: ct);
        return ExtractText(response);
    }

    /// <summary>
    /// 从 AgentResponse 提取文本（MAF 1.13.0 GA：优先用 .Text，备用 .Messages 拼接）
    /// </summary>
    public static string ExtractText(AgentResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Text)) return response.Text.Trim();

        var sb = new System.Text.StringBuilder();
        foreach (var msg in response.Messages)
        {
            if (!string.IsNullOrEmpty(msg.Text)) sb.Append(msg.Text);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 把历史消息列表格式化为简短上下文文本（供 Agent 输入参考）
    /// </summary>
    public static string FormatHistory(IReadOnlyList<Models.ChatMessage> history, int maxCount = 8)
    {
        if (history.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【对话历史】");
        foreach (var m in history.TakeLast(maxCount))
        {
            var role = m.Role switch
            {
                "user" => "用户",
                "assistant" => "助手",
                _ => "系统"
            };
            sb.AppendLine($"[{role}] {m.Content}");
        }
        sb.AppendLine("【当前问题】");
        return sb.ToString();
    }
}
