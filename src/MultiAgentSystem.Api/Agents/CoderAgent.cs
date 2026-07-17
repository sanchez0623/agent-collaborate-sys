// ============================================================
// CoderAgent - 程序员 Agent
// 职责：代码生成、技术问题解答、Bug 排查
// 适用编排：Concurrent / Handoff（技术支持转给程序员）/ Magentic
//
// 特点：输出带语言标注的代码块；解释简洁，重在代码可运行
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Agents;

public static class CoderAgent
{
    public const string Name = "Coder";

    public static ChatClientAgent Create(IChatClient chatClient)
    {
        var instructions = """
            你是一名资深程序员（Coder）。你的职责：
            1. 直接给出可运行的代码（带语言标注的 ```代码块```）
            2. 简要说明关键设计点（不超过 3 条）
            3. 必要时给出复杂度或边界情况提示

            输出要求：
            - 代码优先，解释精简
            - 不写伪代码，给真实可编译的语法
            - 默认使用主流语言（Python/JS/C#），除非用户指定
            - 总长度控制在 400 字以内

            你只负责代码与技术解答，不做产品方案或数据分析。
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "程序员：代码生成与技术解答",
            tools: null,
            loggerFactory: null,
            null);
    }
}
