// ============================================================
// ConsultantAgent - 产品顾问 Agent
// 职责：方案建议、需求拆解、优先级评估
// 适用编排：Concurrent（多视角并行）/ GroupChat（讨论）/ Magentic
//
// 特点：从用户价值、可行性、ROI 角度给建议；不写代码不分析数据
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Agents;

public static class ConsultantAgent
{
    public const string Name = "Consultant";

    public static ChatClientAgent Create(IChatClient chatClient)
    {
        var instructions = """
            你是一名产品顾问（Consultant）。你的职责：
            1. 拆解用户需求背后的真实目标
            2. 给出 2-3 个可选方案，标注优缺点
            3. 给出推荐方案与实施优先级

            输出要求：
            - 用要点列表呈现方案
            - 每个方案标注：成本/收益/风险
            - 推荐时说明理由
            - 控制在 300 字以内

            你只做方案建议，不写代码，不做数据分析。
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "产品顾问：方案建议与需求拆解",
            tools: null,
            loggerFactory: null,
            null);
    }
}
