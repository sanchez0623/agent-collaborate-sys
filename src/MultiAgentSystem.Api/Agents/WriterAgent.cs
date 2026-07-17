// ============================================================
// WriterAgent - 写作者 Agent
// 职责：基于研究员（Researcher）的素材，撰写结构化的最终回答
// 这是流水线的第 2 步：研究素材 → 撰写回答
//
// 特点：无工具调用，纯文本生成；专注"组织表达"而非"获取信息"
// 当被 Critic 退回时，会带上反馈意见再次撰写（重写轮次由编排服务控制）
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Agents;

public static class WriterAgent
{
    /// <summary>Agent 标识名（前端工作流面板据此显示）</summary>
    public const string Name = "Writer";

    /// <summary>
    /// 创建写作者 Agent
    /// </summary>
    /// <param name="chatClient">共享的 IChatClient（连接 DeepSeek）</param>
    public static ChatClientAgent Create(IChatClient chatClient)
    {
        var instructions = """
            你是一名专业的写作者（Writer）。你的任务：
            1. 阅读研究员提供的调研素材（要点、数据、来源）
            2. 基于素材撰写结构清晰、表达准确的回答
            3. 必要时使用 Markdown 格式（标题、列表、代码块等）增强可读性

            撰写要求：
            - 直接输出回答正文，不要复述"基于研究素材..."等元信息
            - 观点必须有素材支撑，不要凭空编造未在素材中出现的数据
            - 语言简洁专业，避免冗长和重复
            - 如果素材不足以下结论，明确说明信息缺口而非臆测
            - 长度适中：通常 200-600 字，复杂问题可适当加长

            如果收到审核员（Critic）的修改意见，请：
            - 仔细阅读意见，针对性改进
            - 输出全新的完整回答（不要只输出修改片段）
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "写作者：基于素材撰写结构化回答",
            tools: null,                // Writer 无工具，专注撰写
            loggerFactory: null,
            null);
    }
}
