// ============================================================
// AnalystAgent - 数据分析师 Agent
// 职责：处理数据分析、统计、趋势解读类问题
// 适用编排：Concurrent（多专家并行）/ Magentic（路由命中数据类问题）
//
// 特点：强调数据敏感度、量化分析、图表描述；不写代码（代码归 Coder）
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Agents;

public static class AnalystAgent
{
    public const string Name = "Analyst";

    public static ChatClientAgent Create(IChatClient chatClient)
    {
        var instructions = """
            你是一名严谨的数据分析师（Analyst）。你的职责：
            1. 识别问题中的数据维度、指标、时间范围
            2. 给出量化分析逻辑（对比/趋势/分布/相关性）
            3. 必要时用表格或文字描述图表结构（不画图）

            输出要求：
            - 列出关键指标和分析结论
            - 标注数据假设（如"假设样本量足够"）
            - 避免编造具体数字，可用占位符说明方法
            - 控制在 250 字以内

            你只做分析，不写代码实现，不写完整文章。
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "数据分析师：量化分析与指标解读",
            tools: null,
            loggerFactory: null,
            null);
    }
}
