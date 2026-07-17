// ============================================================
// CRM + 工单 + 人审 + 审计 + 用户 数据模型
// 统一定义在 CrmModels，对应 SQLite 各张表
// ============================================================

using System.Text.Json.Serialization;

namespace MultiAgentSystem.Api.Models;

// ===================== 客户关系管理 =====================

/// <summary>
/// 客户表 - CRM 核心实体
/// 对应 customers 表
/// </summary>
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>客户等级：潜在/普通/重要/战略</summary>
    public string Level { get; set; } = "普通";
    /// <summary>负责该客户的用户名（关联 users 表）</summary>
    public string Owner { get; set; } = "";
    public string? Remark { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 联系人表 - 一个客户可有多个联系人
/// 对应 contacts 表
/// </summary>
public class Contact
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 跟进记录表 - 销售对客户的每次跟进
/// 对应 followups 表
/// </summary>
public class FollowUp
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    /// <summary>跟进方式：电话/拜访/微信/邮件</summary>
    public string Method { get; set; } = "电话";
    /// <summary>跟进内容摘要</summary>
    public string Content { get; set; } = "";
    /// <summary>跟进人用户名</summary>
    public string Operator { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

// ===================== 工单系统 =====================

/// <summary>
/// 工单状态流转：Pending → Processing → Done / Rejected
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TicketStatus
{
    /// <summary>待处理</summary>
    Pending,
    /// <summary>处理中</summary>
    Processing,
    /// <summary>已完成</summary>
    Done,
    /// <summary>已驳回</summary>
    Rejected
}

/// <summary>
/// 工单表 - 类 OA 审批流，支持多部门协作分派
/// 对应 tickets 表
/// </summary>
public class Ticket
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>来源：用户提交 / Agent 创建</summary>
    public string Source { get; set; } = "用户提交";
    /// <summary>分派给谁（Agent 名或用户名）</summary>
    public string Assignee { get; set; } = "";
    /// <summary>优先级：低/中/高/紧急</summary>
    public string Priority { get; set; } = "中";
    public TicketStatus Status { get; set; } = TicketStatus.Pending;
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// ===================== 人工审核 =====================

/// <summary>
/// 审核请求状态
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalStatus
{
    /// <summary>等待人审</summary>
    Pending,
    /// <summary>已通过</summary>
    Approved,
    /// <summary>已拒绝</summary>
    Rejected,
    /// <summary>已修改后通过</summary>
    Modified
}

/// <summary>
/// 审核请求表 - Human-in-the-Loop 的核心实体
/// Agent 执行敏感操作前先创建审核请求，等待人决策
/// 对应 approval_requests 表
/// </summary>
public class ApprovalRequest
{
    public int Id { get; set; }
    /// <summary>所属会话 ID（关联 SSE 流）</summary>
    public string SessionId { get; set; } = "";
    /// <summary>发起审核的 Agent 名</summary>
    public string Agent { get; set; } = "";
    /// <summary>目标系统：CRM/ERP/OA</summary>
    public string System { get; set; } = "";
    /// <summary>敏感操作描述：如"删除客户 #12"</summary>
    public string Action { get; set; } = "";
    /// <summary>原始参数（JSON）</summary>
    public string Parameters { get; set; } = "{}";
    /// <summary>风险等级：低/中/高</summary>
    public string RiskLevel { get; set; } = "中";
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    /// <summary>审核人用户名</summary>
    public string? Reviewer { get; set; }
    /// <summary>人审备注/修改后的参数</summary>
    public string? ReviewComment { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

// ===================== 审计日志 =====================

/// <summary>
/// 审计日志类型
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditLogType
{
    /// <summary>Agent 工具调用</summary>
    ToolCall,
    /// <summary>人工审核决策</summary>
    Approval,
    /// <summary>业务数据变更（CRM CRUD）</summary>
    DataChange,
    /// <summary>用户登录</summary>
    Auth,
    /// <summary>外部系统集成（邮件/ERP/OA）</summary>
    Integration
}

/// <summary>
/// 审计日志表 - 合规要求：谁在什么时间做了什么
/// 对应 audit_logs 表
/// </summary>
public class AuditLog
{
    public int Id { get; set; }
    public AuditLogType Type { get; set; }
    /// <summary>操作人：用户名 或 Agent 名</summary>
    public string Actor { get; set; } = "";
    /// <summary>操作动作描述</summary>
    public string Action { get; set; } = "";
    /// <summary>操作详情（JSON）</summary>
    public string? Detail { get; set; }
    /// <summary>结果：success/failure</summary>
    public string Result { get; set; } = "success";
    public DateTime CreatedAt { get; set; }
}

// ===================== 用户与鉴权 =====================

/// <summary>
/// 用户角色
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserRole
{
    /// <summary>普通用户：可对话、查看自己的客户</summary>
    User,
    /// <summary>管理员/审核人：可审核、查看全部数据、操作工单</summary>
    Admin
}

/// <summary>
/// 用户表 - 对应 users 表（密码哈希存储）
/// </summary>
public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    /// <summary>SHA256 哈希后的密码（演示用，生产应用 BCrypt）</summary>
    public string PasswordHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.User;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 登录请求
/// </summary>
public record LoginRequest(string Username, string Password);

/// <summary>
/// 登录响应 - 返回 JWT
/// </summary>
public record LoginResponse(string Token, string Username, string Role, string DisplayName);

// ===================== 人审交互请求 =====================

/// <summary>
/// 前端提交人审决策
/// </summary>
/// <param name="ApprovalId">审核请求 ID</param>
/// <param name="Decision">approved / rejected / modified</param>
/// <param name="ModifiedParameters">修改后的参数（modified 时用）</param>
/// <param name="Comment">备注</param>
public record ApprovalDecisionRequest(int ApprovalId, string Decision, string? ModifiedParameters, string? Comment);
