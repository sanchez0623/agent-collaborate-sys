// ============================================================
// ResearcherAgent - 研究员 Agent
// 职责：接收用户问题，调用搜索工具收集信息，输出结构化调研要点
// 这是流水线的第 1 步：研究 → 产出调研素材给 Writer
//
// 特点：是 MVP-1 中唯一带工具的 Agent（Tavily 搜索）
// Agent 可自主决定是否调用 search_web 工具获取实时信息
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Tools;

namespace MultiAgentSystem.Api.Agents;

public static class ResearcherAgent
{
    /// <summary>Agent 标识名（前端工作流面板据此显示）</summary>
    public const string Name = "Researcher";

    /// <summary>
    /// 创建研究员 Agent
    /// </summary>
    /// <param name="chatClient">共享的 IChatClient（连接 DeepSeek）</param>
    /// <param name="searchTool">Tavily 搜索工具；为 null 时不启用工具（纯知识回答）</param>
    public static ChatClientAgent Create(IChatClient chatClient, TavilySearchTool? searchTool)
    {
        var instructions = """
            你是一名严谨的研究员（Researcher）。你的任务：
            1. 分析用户问题，识别需要查证的关键信息点
            2. 当需要实时信息、事实数据或最新事件时，调用 search_web 工具搜索
            3. 综合搜索结果与自身知识，输出结构化的调研要点

            输出要求：
            - 用简洁的要点列表呈现关键事实和数据
            - 标注信息来源（如来自搜索结果）
            - 不要写完整文章，只提供素材，后续 Writer 会基于你的素材撰写回答
            - 控制在 300 字以内

            你只负责调研，不负责撰写最终回答。
            """;

        // 工具列表：有搜索工具则注册，否则为 null（Agent 仅用自身知识）
        IList<AITool>? tools = searchTool is not null ? [searchTool.AsAIFunction()] : null;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "研究员：收集信息、调研要点",
            tools: tools,
            loggerFactory: null,
            null);
    }
}
