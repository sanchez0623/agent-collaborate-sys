// ============================================================
// CoordinatorAgent - 协调员 Agent（身兼两职）
//
// 职责一：GroupChat 主持人
//   - 根据当前讨论上下文，选择下一个最该发言的 Agent
//   - 输出格式：[NEXT] AgentName
//
// 职责二：Concurrent 模式的 Synthesizer（汇总者）
//   - 把多个并行 Agent 的产出整合成一份结构化最终回答
//   - 标注每段观点的来源 Agent
//
// 职责三：Magentic 模式的 Router
//   - 分析用户意图，决定交给哪个 Agent 处理
//   - 输出格式：[ROUTE] AgentName
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Agents;

public static class CoordinatorAgent
{
    public const string Name = "Coordinator";

    /// <summary>群聊选下一位发言者的标记</summary>
    public const string NextTag = "[NEXT]";
    /// <summary>路由决策的标记</summary>
    public const string RouteTag = "[ROUTE]";

    public static ChatClientAgent Create(IChatClient chatClient)
    {
        var instructions = """
            你是一名协调员（Coordinator）。你身兼多种调度职责，根据场景使用对应输出格式。

            场景一：群聊主持人
            - 阅读用户问题和已有讨论，选择下一个最该发言的 Agent
            - 可选 Agent：Researcher / Analyst / Coder / Consultant / Support
            - 输出格式：第一行 [NEXT] AgentName，第二行起说明选择理由
            - 最多 3 轮讨论就应给出结论

            场景二：路由决策
            - 分析用户问题意图，选择最合适的单个 Agent 处理
            - 可选 Agent：Researcher / Analyst / Coder / Consultant / Support / Writer
            - 输出格式：第一行 [ROUTE] AgentName，第二行起说明路由理由
            - 判断维度：问题类型（数据/代码/方案/调研/通用写作）

            场景三：汇总整合
            - 收到多个 Agent 的并行产出后，整合成一份结构化最终回答
            - 用 Markdown 标注每段观点来源（如"### Analyst 观点"）
            - 末尾给一段综合结论

            通用要求：输出简洁，避免啰嗦。
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "协调员：群聊主持/路由决策/多路汇总",
            tools: null,
            loggerFactory: null,
            null);
    }

    /// <summary>
    /// 解析 [NEXT] AgentName 格式（群聊选下一个发言者）
    /// </summary>
    public static bool TryParseNext(string output, out string nextAgent, out string reason)
    {
        nextAgent = "";
        reason = "";
        if (string.IsNullOrWhiteSpace(output)) return false;

        var lines = output.Split('\n', StringSplitOptions.None);
        var firstLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        if (!firstLine.StartsWith(NextTag, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = firstLine.Substring(NextTag.Length).Trim();
        nextAgent = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        if (nextAgent.Length > 0)
            nextAgent = char.ToUpper(nextAgent[0]) + nextAgent[1..].ToLowerInvariant();

        reason = string.Join('\n', lines.Skip(1)).Trim();
        return !string.IsNullOrEmpty(nextAgent);
    }

    /// <summary>
    /// 解析 [ROUTE] AgentName 格式（路由决策）
    /// </summary>
    public static bool TryParseRoute(string output, out string targetAgent, out string reason)
    {
        targetAgent = "";
        reason = "";
        if (string.IsNullOrWhiteSpace(output)) return false;

        var lines = output.Split('\n', StringSplitOptions.None);
        var firstLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        if (!firstLine.StartsWith(RouteTag, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = firstLine.Substring(RouteTag.Length).Trim();
        targetAgent = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        if (targetAgent.Length > 0)
            targetAgent = char.ToUpper(targetAgent[0]) + targetAgent[1..].ToLowerInvariant();

        reason = string.Join('\n', lines.Skip(1)).Trim();
        return !string.IsNullOrEmpty(targetAgent);
    }
}
