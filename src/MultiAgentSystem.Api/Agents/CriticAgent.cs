// ============================================================
// CriticAgent - 审核员 Agent
// 职责：审核 Writer 的输出，决定通过或退回
//
// 输出方式：优先 function calling → 结构化 JSON，降级 [APPROVE]/[REJECT] 文本
// 调用链：DirectEvaluateAsync（IChatClient 直连 + ChatOptions.Tools）→ TryParseVerdict
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
    public const string VerdictFunctionName = "submit_verdict";

    public record VerdictResult(bool Approved, string Feedback);

    private const string Instructions = """
        你是一名严格的审核员（Critic）。评估 Writer 的回答质量。

        审核维度：准确性、完整性、清晰度、相关性。
        判定原则：仅严重问题才退回（事实错误、严重遗漏、答非所问）；
        小瑕疵应通过，避免连续退回导致无法收敛。

        通过审核则 approved=true，退回则 approved=false 并给出具体反馈。
        """;
    /// <summary>
    /// 创建审核员 Agent（供 AgentRegistry 注册用，返回 ChatClientAgent 以兼容框架）
    /// 注意：实际审核调用走 EvaluateAsync 直接方法，不经过 ChatClientAgent.RunAsync。
    /// </summary>
    public static ChatClientAgent Create(IChatClient chatClient)
    {
        return new ChatClientAgent(chatClient,
            instructions: Instructions,
            name: Name,
            description: "审核员：评估 Writer 输出并给出通过/退回判定",
            tools: [GetVerdictFunction()]);
    }

    private static AIFunction GetVerdictFunction()
    {
        return AIFunctionFactory.Create((bool approved, string feedback) =>
            $"verdict: approved={approved}, feedback={feedback}",
            new AIFunctionFactoryOptions
            {
                Name = VerdictFunctionName,
                Description = "提交审核判定：通过或退回并附带反馈意见"
            });
    }

    /// <summary>
    /// 直接调用 IChatClient.GetResponseAsync + ChatOptions.Tools
    /// 绕过 ChatClientAgent 框架层，确保 tools 被正确传递给 DeepSeek API。
    /// </summary>
    public static async Task<string> EvaluateAsync(IChatClient chatClient, string input, ILogger? logger = null)
    {
        var tool = GetVerdictFunction();
        var options = new ChatOptions { Tools = [tool] };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, Instructions),
            new(ChatRole.User, input)
        };

        logger?.LogDebug("Critic 发送 tool_calls 请求: tool={ToolName}", VerdictFunctionName);

        try
        {
            var response = await chatClient.GetResponseAsync(messages, options, CancellationToken.None);
            var output = ExtractResponse(response);
            logger?.LogDebug("Critic 原始输出: {Output}", output.Truncate(500));
            return output;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Critic tool_calls 调用失败，回退到纯文本模式");
            // 回退：不带 tools 重新调用
            var textResponse = await chatClient.GetResponseAsync(messages, cancellationToken: CancellationToken.None);
            return ExtractResponse(textResponse);
        }
    }

    /// <summary>从 ChatResponse 提取完整文本内容</summary>
    private static string ExtractResponse(ChatResponse response)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var msg in response.Messages)
        {
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent tc:
                        sb.AppendLine(tc.Text);
                        break;
                    case FunctionCallContent fc:
                        var argsJson = JsonSerializer.Serialize(fc.Arguments);
                        sb.AppendLine($"{{\"name\":\"{fc.Name}\",\"arguments\":{argsJson}}}");
                        break;
                }
            }
        }
        return sb.ToString().Trim();
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
        var verdictLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        var isApproved = verdictLine.StartsWith(ApproveTag, StringComparison.OrdinalIgnoreCase);
        if (!isApproved) feedback = string.Join('\n', lines.Skip(1)).Trim();

        logger?.LogWarning("Critic 判定: 路径=B(正则降级) approved={Approved} raw第一行=\"{Line}\"",
            isApproved, verdictLine);
        return isApproved;
    }

    /// <summary>尝试从 JSON 中解析 verdict</summary>
    private static VerdictResult? TryParseJsonVerdict(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains(VerdictFunctionName) && !trimmed.Contains("approved")) continue;
            try
            {
                var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.TryGetProperty("approved", out var approvedEl))
                {
                    return new VerdictResult(
                        approvedEl.GetBoolean(),
                        doc.RootElement.TryGetProperty("feedback", out var fb) ? fb.GetString() ?? "" : "");
                }
                if (doc.RootElement.TryGetProperty("arguments", out var args))
                {
                    var inner = JsonDocument.Parse(args.GetString() ?? "{}");
                    if (inner.RootElement.TryGetProperty("approved", out var ia))
                        return new VerdictResult(ia.GetBoolean(),
                            inner.RootElement.TryGetProperty("feedback", out var ifb) ? ifb.GetString() ?? "" : "");
                }
            }
            catch { }
        }
        return null;
    }
}

/// <summary>字符串截断扩展</summary>
file static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
