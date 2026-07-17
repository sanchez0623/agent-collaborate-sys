// ============================================================
// 数据模型定义
// 包含：前端请求模型、对话消息模型、SSE事件模型
// ============================================================

namespace MultiAgentSystem.Api.Models;

/// <summary>
/// 聊天请求模型（前端 POST /api/chat 发送）
/// </summary>
/// <param name="SessionId">会话ID，用于区分不同对话窗口；首次为空则后端自动生成</param>
/// <param name="Message">用户输入的消息文本</param>
/// <param name="OrchestrationMode">编排模式：sequential/concurrent/handoff/groupchat/magentic（默认 sequential）</param>
public record ChatRequest(string? SessionId, string Message, string? OrchestrationMode = "sequential");

/// <summary>
/// 对话消息模型，对应数据库 messages 表的一条记录
/// 与 DeepSeek/OpenAI 的 message 格式兼容（role + content）
/// </summary>
/// <param name="Role">消息角色：system(系统提示)/user(用户)/assistant(助手)</param>
/// <param name="Content">消息文本内容</param>
public record ChatMessage(string Role, string Content);

/// <summary>
/// 流水线步骤状态（用于前端工作流可视化）
/// </summary>
public enum StepStatus
{
    /// <summary>等待执行</summary>
    Pending,
    /// <summary>正在执行</summary>
    Running,
    /// <summary>已完成</summary>
    Done,
    /// <summary>被审核员退回，需要重做</summary>
    Rejected
}
