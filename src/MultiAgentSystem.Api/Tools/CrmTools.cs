// ============================================================
// CrmTools - CRM Agent 可调用的工具集（AIFunction）
//
// 设计思路：
//   - 每个工具用 AIFunctionFactory.Create 包装为 MAF 可识别的 AIFunction
//   - 工具描述（description）决定 Agent 何时调用——这是 Agent 自主判断的关键
//   - 敏感操作（删除/修改金额）不直接执行，而是创建审核请求并触发人审
//
// 工具清单：
//   search_customers  - 查询客户（低风险，免审）
//   create_customer   - 新建客户（中风险，记录但不触发人审）
//   add_followup      - 添加跟进记录（低风险，免审）
//   delete_customer   - 删除客户（高风险，必须人审）
//
// 人审触发机制：
//   delete_customer 调用 ApprovalCoordinator.RequestApprovalAsync，
//   该方法会阻塞当前 Task 直到前端回传审核结果，再决定是否真正执行删除。
//   这就是 Human-in-the-Loop 的核心：Agent 提议，人拍板，再执行。
// ============================================================

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Adapters;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Tools;

public class CrmTools
{
    private readonly CrmAdapter _adapter;
    private readonly ApprovalCoordinator _approvals;
    private readonly BusinessStore _store;

    public CrmTools(CrmAdapter adapter, ApprovalCoordinator approvals, BusinessStore store)
    {
        _adapter = adapter;
        _approvals = approvals;
        _store = store;
    }

    /// <summary>暴露给 CRM Agent 的全部工具（AITool 形式，供 ChatClientAgent 使用）</summary>
    public IList<AITool> AsAIFunctions() => new List<AITool>
    {
        AIFunctionFactory.Create(SearchCustomersAsync, name: "search_customers",
            description: "查询客户列表。参数：keyword(可选,客户名/公司/电话模糊搜索)。返回客户列表 JSON。用于用户问'有哪些客户''查一下某某客户'。"),
        AIFunctionFactory.Create(GetCustomerDetailAsync, name: "get_customer_detail",
            description: "查询单个客户详情含跟进记录。参数：customerId(整数,客户ID)。返回客户+跟进列表 JSON。用于用户问'看下这个客户的情况'。"),
        AIFunctionFactory.Create(CreateCustomerAsync, name: "create_customer",
            description: "新建客户。参数：name(必填,客户名), company(公司), phone(电话), email(邮箱), level(等级:潜在/普通/重要/战略,默认普通), owner(负责人用户名,可选)。返回新建客户ID。用于用户说'帮我录入一个新客户'。"),
        AIFunctionFactory.Create(AddFollowUpAsync, name: "add_followup",
            description: "为客户添加跟进记录。参数：customerId(必填,客户ID), content(必填,跟进内容), method(方式:电话/拜访/微信/邮件,默认电话), operator(跟进人,可选)。返回跟进记录ID。用于用户说'记录一下今天跟张三的沟通'。"),
        AIFunctionFactory.Create(DeleteCustomerAsync, name: "delete_customer",
            description: "删除客户（敏感操作，需人工审核）。参数：customerId(必填,客户ID), reason(必填,删除原因)。会触发人审流程，审核通过后才真正删除。")
    };

    // ---------- 工具实现 ----------

    [Description("客户名/公司/电话模糊搜索关键词")]
    public async Task<string> SearchCustomersAsync(string? keyword = null)
    {
        var req = new ExternalOperationRequest(ExternalSystemType.CRM, "customers",
            ExternalOperationType.Query, null,
            JsonSerializer.Serialize(new { keyword }));
        var res = await _adapter.ExecuteAsync(req);
        await _store.AuditAsync(AuditLogType.ToolCall, "CrmAgent", "调用 search_customers",
            JsonSerializer.Serialize(new { keyword }), res.Success ? "success" : "failure");
        return FormatResult(res, "客户列表");
    }

    public async Task<string> GetCustomerDetailAsync(int customerId)
    {
        var customer = await _store.GetCustomerAsync(customerId);
        if (customer == null) return $"未找到客户 #{customerId}";
        var followups = await _store.ListFollowUpsAsync(customerId);
        await _store.AuditAsync(AuditLogType.ToolCall, "CrmAgent", "调用 get_customer_detail",
            customerId.ToString());
        return JsonSerializer.Serialize(new { customer, followups }, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> CreateCustomerAsync(string name, string company = "", string phone = "",
        string email = "", string level = "普通", string owner = "")
    {
        var param = JsonSerializer.Serialize(new { name, company, phone, email, level, owner });
        var req = new ExternalOperationRequest(ExternalSystemType.CRM, "customers",
            ExternalOperationType.Create, null, param);
        var res = await _adapter.ExecuteAsync(req);
        await _store.AuditAsync(AuditLogType.ToolCall, "CrmAgent", "调用 create_customer",
            param, res.Success ? "success" : "failure");
        return FormatResult(res, "新建客户");
    }

    public async Task<string> AddFollowUpAsync(int customerId, string content,
        string method = "电话", string @operator = "")
    {
        var param = JsonSerializer.Serialize(new { customerId, content, method, @operator });
        var req = new ExternalOperationRequest(ExternalSystemType.CRM, "followups",
            ExternalOperationType.Create, null, param);
        var res = await _adapter.ExecuteAsync(req);
        await _store.AuditAsync(AuditLogType.ToolCall, "CrmAgent", "调用 add_followup",
            param, res.Success ? "success" : "failure");
        return FormatResult(res, "添加跟进");
    }

    public async Task<string> DeleteCustomerAsync(int customerId, string reason)
    {
        // ---------- 敏感操作：触发人审 ----------
        // 不直接删除，而是请求人审并等待结果
        var approval = new ApprovalRequest
        {
            // 从 AsyncLocal 取当前异步流的 sessionId（多会话并发隔离，无实例字段污染）
            SessionId = ApprovalCoordinator.CurrentSessionId ?? "",
            Agent = "CrmAgent",
            System = "CRM",
            Action = $"删除客户 #{customerId}",
            Parameters = JsonSerializer.Serialize(new { customerId, reason }),
            RiskLevel = "高"
        };
        var approvalId = await _approvals.RequestApprovalAsync(approval);

        // 阻塞等待人审决策（ApprovalCoordinator 在前端回传后释放）
        var decision = await _approvals.WaitForDecisionAsync(approvalId);

        if (decision.Status == ApprovalStatus.Rejected)
        {
            return $"❌ 人审拒绝：删除客户 #{customerId} 未执行。审核人备注：{decision.Comment ?? "无"}";
        }

        // 通过 / 修改后通过 均执行删除
        // 注意：删除操作的参数（customerId+reason）不可改——customerId 是标识符，
        // 改它等于删错对象。审核人若想"改参数"实质上应改为"拒绝后重新发起"。
        // 这里对 Modified 状态按原 customerId 执行，但把审核人的修改意见记入审计。
        var actor = decision.Reviewer ?? "approver";
        var ok = await _store.DeleteCustomerAsync(customerId, actor);
        await _store.AuditAsync(AuditLogType.ToolCall, "CrmAgent", "调用 delete_customer",
            $"customerId={customerId}, approved by {actor}, decision={decision.Status}", ok ? "success" : "failure");
        return ok
            ? $"✅ 人审通过，已删除客户 #{customerId}（审核人：{actor}）"
            : $"删除失败：客户 #{customerId} 不存在";
    }

    private static string FormatResult(ExternalOperationResult res, string label)
    {
        if (!res.Success) return $"❌ {label}失败：{res.Error}";
        return string.IsNullOrEmpty(res.Data) ? $"✅ {label}成功" : res.Data;
    }
}
