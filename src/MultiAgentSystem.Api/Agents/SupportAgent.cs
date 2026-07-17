// ============================================================
// SupportAgent - 技术支持 Agent
// 职责：故障排查、FAQ 解答、问题分类（必要时 Handoff 给专家）
// 适用编排：Handoff（作为入口 Agent，搞不定时移交给 Coder/Analyst）
//
// 特点：擅长问答、定位问题；输出"是否需要移交"的判断
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Agents;

public static class SupportAgent
{
    public const string Name = "Support";

    /// <summary>需要移交给他人的关键字（Handoff 编排解析用）</summary>
    public const string HandoffTag = "[HANDOFF]";

    public static ChatClientAgent Create(IChatClient chatClient)
    {
        var instructions = """
            你是一名技术支持工程师（Support）。你的职责：
            1. 接收用户问题，尝试给出排查思路或答案
            2. 如果问题涉及写代码或复杂技术实现，移交（handoff）给 Coder
            3. 如果问题涉及数据分析，移交给 Analyst
            4. 如果问题涉及产品方案，移交给 Consultant

            输出格式：
            - 能自己回答：直接给答案（200 字内）
            - 需要移交：第一行写 [HANDOFF] 目标Agent名
              第二行起写移交原因和上下文摘要

            示例（自己回答）：
            请检查网络连接是否正常...

            示例（移交）：
            [HANDOFF] Coder
            用户需要一个 Python 实现的排序算法，涉及具体代码生成。
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "技术支持：故障排查与问题分流",
            tools: null,
            loggerFactory: null,
            null);
    }

    /// <summary>
    /// 解析 Support 输出是否要求移交
    /// </summary>
    /// <returns>true=需要移交，targetAgent 为目标；false=自己已回答</returns>
    public static bool TryParseHandoff(string output, out string targetAgent, out string reason)
    {
        targetAgent = "";
        reason = "";
        if (string.IsNullOrWhiteSpace(output)) return false;

        var lines = output.Split('\n', StringSplitOptions.None);
        var firstLine = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        if (!firstLine.StartsWith(HandoffTag, StringComparison.OrdinalIgnoreCase)) return false;

        // 格式：[HANDOFF] AgentName
        var rest = firstLine.Substring(HandoffTag.Length).Trim();
        // 取第一个单词作为 Agent 名
        targetAgent = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(targetAgent)) return false;

        // 首字大写规范化（"coder" → "Coder"）
        if (targetAgent.Length > 0)
            targetAgent = char.ToUpper(targetAgent[0]) + targetAgent[1..].ToLowerInvariant();

        reason = string.Join('\n', lines.Skip(1)).Trim();
        return true;
    }
}
