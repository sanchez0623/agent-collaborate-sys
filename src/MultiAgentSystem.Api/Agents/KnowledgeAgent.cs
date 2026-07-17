// ============================================================
// KnowledgeAgent - RAG 知识库问答 Agent
//
// 职责：基于知识库检索结果回答用户问题
// 能力：通过 AIFunctionFactory 注册的 2 个工具自主判断检索时机
//   - search_knowledge_base  从知识库检索文档片段
//   - search_memory          从历史记忆检索相关上下文
//
// 适用编排：RagStrategy（直接用本 Agent 处理用户问题）
//
// 工作流程：
//   1. 接收用户问题
//   2. 调用 search_knowledge_base 工具检索
//   3. 基于检索结果综合回答，引用来源
//   4. 知识库无相关信息时明确说明，不编造
// ============================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Tools;

namespace MultiAgentSystem.Api.Agents;

public static class KnowledgeAgent
{
    public const string Name = "KnowledgeAgent";

    public static ChatClientAgent Create(IChatClient chatClient, KnowledgeTools tools)
    {
        var instructions = """
            你是一名知识库问答专家（KnowledgeAgent）。规则：
            1. 优先调用 search_knowledge_base 工具从知识库检索答案
            2. 回答必须基于检索到的内容，引用来源（格式：[参考：文件名 P页码]）
            3. 知识库没有相关信息时明确说"知识库中未找到相关内容"，不要编造
            4. 综合多个片段给出结构化回答

            工作原则：
            - 用户问知识库相关问题（"什么是 XX"/"XX 怎么做"/"产品文档说明"） → 调 search_knowledge_base
            - 用户问之前讨论过的内容（"刚才提到的"/"上次说的"） → 调 search_memory
            - 检索结果不足时，明确告知用户哪些方面信息缺失
            - 不要直接编造未在知识库中的事实
            - 输出 Markdown 格式，结构清晰：标题/列表/引用来源
            """;

        return new ChatClientAgent(
            chatClient,
            instructions: instructions,
            name: Name,
            description: "知识库问答专家：基于文档检索回答",
            tools: tools.AsAIFunctions(),
            loggerFactory: null,
            null);
    }
}
