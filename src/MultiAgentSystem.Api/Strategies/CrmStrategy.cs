// ============================================================
// CrmStrategy - CRM 业务编排（MVP-4 核心模式）
//
// 适用场景：用户提出 CRM 类需求（查客户/建客户/跟进/删除）
//   例："帮我查一下张三" / "录入新客户李四" / "删除客户 #5"
//
// 设计思路：
//   1. 直接用 CrmAgent（带 5 个工具）处理用户问题
//   2. CrmAgent 自主决定调用哪个工具（LLM function calling）
//   3. 工具调用过程通过 onEvent 推 SSE：tool_call → tool_result
//   4. 敏感工具（delete_customer）内部触发 ApprovalCoordinator 人审
//      人审期间 Agent Task 阻塞，前端弹审核卡片，决策后恢复
//
// 工具调用捕获机制：
//   MAF 的 ChatClientAgent.RunAsync 内部会循环：LLM→工具调用→LLM
//   用 FunctionInvocationProcessor 拦截每次工具调用，
//   推 ToolCall 事件 → 执行工具 → 推 ToolResult 事件。
// ============================================================

using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Agents;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Strategies;

public class CrmStrategy : IOrchestrationStrategy
{
    private readonly AgentRegistry _registry;
    private readonly ApprovalCoordinator _approvals;
    public OrchestrationMode Mode => OrchestrationMode.Crm;

    public CrmStrategy(AgentRegistry registry, ApprovalCoordinator approvals)
    {
        _registry = registry;
        _approvals = approvals;
    }

    public async Task<string> ExecuteAsync(
        string userMessage,
        IReadOnlyList<Models.ChatMessage> history,
        Action<OrchestrationEvent> onEvent,
        CancellationToken ct)
    {
        var crmAgent = _registry.Get(CrmAgent.Name);
        var historyCtx = AgentRunner.FormatHistory(history);
        var input = $"{historyCtx}用户问题：{userMessage}";

        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentStarted,
            Agent: CrmAgent.Name, Status: "running", Round: 1));

        // 执行 CrmAgent（内部会自主调工具，工具内推 ToolCall/ToolResult 事件）
        // 敏感工具会通过 ApprovalCoordinator 阻塞等待人审
        var output = await AgentRunner.RunAsync(crmAgent, input, ct);

        onEvent(new OrchestrationEvent(OrchestrationEventType.AgentCompleted,
            Agent: CrmAgent.Name, Status: "done", Output: output, Round: 1));

        return output;
    }
}
