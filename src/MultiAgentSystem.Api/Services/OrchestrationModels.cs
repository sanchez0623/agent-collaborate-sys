// ============================================================
// 编排抽象层 - 5 种编排模式的统一接口与事件模型
//
// 设计思路（策略模式）：
//   - IOrchestrationStrategy：所有编排模式实现同一接口
//   - OrchestrationMode：API 参数通过此枚举选择模式
//   - OrchestrationEvent：统一的 SSE 事件载体，覆盖 7 种事件类型
//   - 上层（Program.cs）只关心"选哪个策略 + 收事件推 SSE"，不关心具体编排逻辑
//
// 适用场景对比：
//   Sequential  顺序执行，上一个是下一个的输入（研究→写作→审核）
//   Concurrent  并行独立思考 + 汇总（多专家同时回答，再合并）
//   Handoff     链式移交，A 干不了转给 B（客服→技术支持→专家）
//   GroupChat   主持人调度多 Agent 轮流发言（自由讨论）
//   Magentic    根据意图自动路由到最合适的单个 Agent（无需人工指定）
// ============================================================

using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

/// <summary>
/// 编排模式枚举（API 参数 orchestrationMode 的取值）
/// </summary>
public enum OrchestrationMode
{
    /// <summary>顺序流水线：Researcher → Writer → Critic</summary>
    Sequential,
    /// <summary>并行多专家 + 汇总：N 个 Agent 同时跑 + Synthesizer 合并</summary>
    Concurrent,
    /// <summary>链式移交：当前 Agent 处理不了时主动转给下一个</summary>
    Handoff,
    /// <summary>群聊讨论：主持人选择下一个发言者，多轮自由讨论</summary>
    GroupChat,
    /// <summary>智能路由：根据用户意图自动选择最合适的单个 Agent</summary>
    Magentic,
    /// <summary>MVP-4：CRM 业务模式，CrmAgent 自主调用工具 + 人审</summary>
    Crm,
    /// <summary>MVP-3：RAG 知识库问答，KnowledgeAgent 检索知识库 + 引用来源</summary>
    Rag
}

/// <summary>
/// SSE 事件类型 - 统一事件，前端据此渲染不同 UI
/// MVP-2: 7 种编排事件；MVP-4 新增 tool_call/tool_result/approval_required/approval_result
/// </summary>
public enum OrchestrationEventType
{
    /// <summary>Agent 开始执行（含并行场景的多个同时触发）</summary>
    AgentStarted,
    /// <summary>Agent 增量产出（流式片段，本 MVP 暂用整段输出代替）</summary>
    AgentDelta,
    /// <summary>Agent 完成执行</summary>
    AgentCompleted,
    /// <summary>Handoff 移交：from → to，附原因</summary>
    Handoff,
    /// <summary>GroupChat 轮次切换：谁将要发言</summary>
    GroupTurn,
    /// <summary>Magentic 路由决策：选择哪个 Agent + 理由</summary>
    RouteDecision,
    /// <summary>整个编排流程结束</summary>
    OrchestrationDone,
    /// <summary>MVP-4：Agent 调用工具（工具名 + 参数）</summary>
    ToolCall,
    /// <summary>MVP-4：工具执行结果返回</summary>
    ToolResult,
    /// <summary>MVP-4：需要人工审核（前端弹审核卡片）</summary>
    ApprovalRequired,
    /// <summary>MVP-4：人审结果回传（通过/拒绝/修改）</summary>
    ApprovalResult
}

/// <summary>
/// 编排事件 - 推送给前端 SSE 的统一载体
/// 不同 EventType 用到的字段不同，未用字段留空/null
/// </summary>
/// <param name="Type">事件类型</param>
/// <param name="Agent">当前 Agent 名（AgentStarted/Completed/Delta 用）</param>
/// <param name="Status">状态：running/done/rejected（AgentStarted=running, AgentCompleted=done/rejected）</param>
/// <param name="Output">Agent 产出文本</param>
/// <param name="Round">轮次（群聊/重写场景用）</param>
/// <param name="FromAgent">Handoff：移交给谁</param>
/// <param name="ToAgent">Handoff：移交给谁</param>
/// <param name="Reason">Handoff/RouteDecision 的理由</param>
/// <param name="ParallelLane">Concurrent 并行泳道编号（0/1/2...）</param>
/// <param name="ToolName">ToolCall/ToolResult：工具名</param>
/// <param name="ToolArgs">ToolCall：工具参数（JSON）</param>
/// <param name="ToolResult">ToolResult：工具返回（JSON/文本）</param>
/// <param name="ApprovalId">ApprovalRequired/Result：审核请求 ID</param>
/// <param name="ApprovalAction">ApprovalRequired：待审操作描述</param>
/// <param name="ApprovalParams">ApprovalRequired：操作参数（JSON）</param>
/// <param name="ApprovalDecision">ApprovalResult：approved/rejected/modified</param>
public record OrchestrationEvent(
    OrchestrationEventType Type,
    string Agent = "",
    string Status = "",
    string Output = "",
    int Round = 0,
    string? FromAgent = null,
    string? ToAgent = null,
    string? Reason = null,
    int? ParallelLane = null,
    string? ToolName = null,
    string? ToolArgs = null,
    string? ToolResult = null,
    int? ApprovalId = null,
    string? ApprovalAction = null,
    string? ApprovalParams = null,
    string? ApprovalDecision = null);

/// <summary>
/// 编排策略接口 - 所有编排模式实现此接口
/// </summary>
public interface IOrchestrationStrategy
{
    /// <summary>模式标识</summary>
    OrchestrationMode Mode { get; }

    /// <summary>
    /// 执行编排
    /// </summary>
    /// <param name="userMessage">用户本次提问</param>
    /// <param name="history">历史对话（多轮上下文）</param>
    /// <param name="onEvent">事件回调（同步调用，推送 SSE）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>最终输出文本</returns>
    Task<string> ExecuteAsync(
        string userMessage,
        IReadOnlyList<Models.ChatMessage> history,
        Action<OrchestrationEvent> onEvent,
        CancellationToken ct);
}
