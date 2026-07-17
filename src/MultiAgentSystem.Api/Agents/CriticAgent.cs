// ============================================================
// CriticAgent - 审核员 Agent
// 职责：审核 Writer 的输出，决定通过或退回
//
// 输出方式：优先 function calling（结构化 JSON），降级到文本正则
// 函数 submit_verdict(approved: bool, feedback: string)
// ============================================================

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MultiAgentSystem.Api.Agents;

public static class CriticAgent
{
    public const string Name = "Critic";
    public const string ApproveTag = "[APPROVE]";
    public const string RejectTag = "[REJECT]";

    /// <summary>Function calling 函数名</summary>
    public const string VerdictFunctionName = "submit_verdict";

    /// <summary>Verdict 结构化记录</summary>
    public record VerdictResult(bool Approved, string Feedback);

    /// <summary>
    /// 定义判决函数 —— agent 会调用此函数输出结构化判定
    /// </summary>
    public static AIFunction GetVerdictFunction()
    {
        return AIFunctionFactory.Create((bool approved, string feedback) =>
        {
            // 占位：实际在策略层解析 FunctionCallContent
            return $"verdict: approved={approved}, feedback={feedback}";
        }, new AIFunctionFactoryOptions
        {
            Name = VerdictFunctionName,
            Description = "提交审核判定：通过或退回并附带反馈意见",
        });
    }

    /// <summary>
    /// 创建审核员 Agent
    /// </summary>
    /// <param name="chatClient">共享的 IChatClient（连接 DeepSeek）</param>
    public static ChatClientAgent Create(IChatClient chatClient)
    {
        var instructions = """
            你是一名严格的审核员（Critic）。评估 Writer 的回答质量。

            审核维度：准确性、完整性、清晰度、相关性。
            判定原则：仅严重问题才退回（事实错误、严重遗漏、答非所问）；
            小瑕疵应通过，避免连续退回导致无法收敛。

            **输出格式（必须严格遵守，不可输出其他内容）：**
            通过：输出 [APPROVE]
            退回：输出 [REJECT]
            反馈写在标签后面同一行。

            示例：
            [APPROVE] 回答准确完整，符合要求
            [REJECT] 1.缺少数据支撑 2.第二段与素材不一致

            你的回复第一行必须是 [APPROVE] 或 [REJECT]，不能有任何其他内容在标签前面。
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "审核员：调用 submit_verdict 函数提交通过/退回判定",
            tools: [GetVerdictFunction()],
            loggerFactory: null,
            null);
    }

    /// <summary>
    /// 解析 Critic 输出：优先 function call JSON，降级到 [APPROVE]/[REJECT] 正则
    /// </summary>
    public static bool TryParseVerdict(string criticOutput, out string feedback, ILogger? logger = null)
    {
        feedback = "";
        if (string.IsNullOrWhiteSpace(criticOutput)) return false;

        // 路径 A：function calling JSON（优先）
        var json = TryParseJsonVerdict(criticOutput);
        if (json != null)
        {
            feedback = json.Feedback ?? "";
            logger?.LogInformation("Critic 判定: 路径=A(function calling) approved={Approved} feedback=\"{Feedback}\"",
                json.Approved, feedback);
            return json.Approved;
        }

        // 路径 B：正则匹配 [APPROVE]/[REJECT]（降级）
        var lines = criticOutput.Split('\n', StringSplitOptions.None);
        var verdictLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim()
                          ?? "";
        var isApproved = verdictLine.StartsWith(ApproveTag, StringComparison.OrdinalIgnoreCase);

        if (!isApproved)
        {
            feedback = string.Join('\n', lines.Skip(1)).Trim();
        }
        logger?.LogWarning("Critic 判定: 路径=B(正则降级) approved={Approved} raw第一行=\"{Line}\"",
            isApproved, verdictLine);
        return isApproved;
    }

    /// <summary>尝试从 JSON 中解析 verdict</summary>
    private static VerdictResult? TryParseJsonVerdict(string text)
    {
        // 查找 FunctionCallContent 或 JSON 中的 submit_verdict
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains(VerdictFunctionName) && !trimmed.Contains("approved"))
                continue;
            try
            {
                var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("approved", out var approvedEl))
                {
                    var approved = approvedEl.GetBoolean();
                    var fb = doc.RootElement.TryGetProperty("feedback", out var fbEl)
                        ? fbEl.GetString() : "";
                    return new VerdictResult(approved, fb ?? "");
                }
                // 也兼容 { "arguments": "{\"approved\":true,...}" } 格式
                if (doc.RootElement.TryGetProperty("arguments", out var args))
                {
                    var inner = JsonDocument.Parse(args.GetString() ?? "{}");
                    if (inner.RootElement.TryGetProperty("approved", out var ia))
                    {
                        var approved = ia.GetBoolean();
                        var fb = inner.RootElement.TryGetProperty("feedback", out var ifb)
                            ? ifb.GetString() : "";
                        return new VerdictResult(approved, fb ?? "");
                    }
                }
            }
            catch { /* 非 JSON 行，跳过 */ }
        }
        return null;
    }
}
