// ============================================================
// IExternalSystemAdapter - 外部业务系统适配器接口（适配器模式）
//
// 设计意图（面试重点）：
//   企业 Agent 落地必须对接多个业务系统（CRM/ERP/OA...），
//   每个系统的 SDK、协议、数据模型都不同。若 Agent 直接耦合
//   具体系统，换一个系统就要改 Agent 代码——违反开闭原则。
//
//   适配器模式：定义统一的 IExternalSystemAdapter 接口，
//   每个外部系统实现一个 Adapter。Agent 只依赖接口，
//   "接入新系统只需实现一个接口"，符合依赖倒置原则。
//
// 当前实现：
//   - CrmAdapter：CRM 系统适配器（MVP-4 首个实现）
//
// 预留占位（仅接口契约，未实现）：
//   - ErpAdapter：ERP（订单/库存）
//   - OaAdapter：OA（审批/公告）
//   真实接入时只需新增一个 Adapter 类，Agent 代码零改动。
// ============================================================

namespace MultiAgentSystem.Api.Adapters;

/// <summary>
/// 外部系统类型标识（用于路由到对应 Adapter）
/// </summary>
public enum ExternalSystemType
{
    /// <summary>CRM 客户关系管理</summary>
    CRM,
    /// <summary>ERP 企业资源计划（预留）</summary>
    ERP,
    /// <summary>OA 办公自动化（预留）</summary>
    OA
}

/// <summary>
/// 外部系统操作类型（审计日志/人审判断用）
/// </summary>
public enum ExternalOperationType
{
    Query,    // 查询（只读，低风险）
    Create,   // 新增
    Update,   // 修改
    Delete    // 删除（高风险，必触发人审）
}

/// <summary>
/// 外部系统统一操作请求
/// 所有系统遵循"资源 + 操作 + 参数"的统一模型
/// </summary>
/// <param name="Resource">资源名：customers / contacts / orders / approvals ...</param>
/// <param name="Operation">操作类型</param>
/// <param name="ResourceId">目标资源 ID（查询/修改/删除时用）</param>
/// <param name="Parameters">操作参数（JSON 字符串，由具体 Adapter 解析）</param>
public record ExternalOperationRequest(
    ExternalSystemType System,
    string Resource,
    ExternalOperationType Operation,
    string? ResourceId,
    string Parameters);

/// <summary>
/// 外部系统统一操作结果
/// </summary>
/// <param name="Success">是否成功</param>
/// <param name="Data">返回数据（JSON 字符串）</param>
/// <param name="Error">失败时的错误信息</param>
public record ExternalOperationResult(bool Success, string Data, string? Error);

/// <summary>
/// 外部业务系统适配器接口
/// 所有外部系统通过此接口暴露给 Agent 工具层
/// </summary>
public interface IExternalSystemAdapter
{
    /// <summary>系统类型标识</summary>
    ExternalSystemType System { get; }

    /// <summary>该适配器支持的资源列表（如 customers/contacts）</summary>
    IReadOnlyList<string> SupportedResources { get; }

    /// <summary>
    /// 执行一次业务操作
    /// 实现类负责：参数解析 → 调用底层存储/远程API → 返回统一结果
    /// </summary>
    Task<ExternalOperationResult> ExecuteAsync(ExternalOperationRequest request, CancellationToken ct = default);
}
