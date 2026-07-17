// ============================================================
// MultiAgentDbContext - EF Core 统一数据上下文
// 配置驱动：appsettings.json → Database:Provider 选 SQLite/PgSQL
// ============================================================

using Microsoft.EntityFrameworkCore;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Data;

public class MultiAgentDbContext : DbContext
{
    // CRM
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();

    // 审计 + 用户
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // 评测
    public DbSet<EvalTestCase> EvalTestCases => Set<EvalTestCase>();
    public DbSet<EvalReportEntity> EvalReports => Set<EvalReportEntity>();
    public DbSet<EvalCaseResultEntity> EvalCaseResults => Set<EvalCaseResultEntity>();

    public MultiAgentDbContext(DbContextOptions<MultiAgentDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder m)
    {
        // ===== CRM =====
        m.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Level).HasDefaultValue("普通");
        });

        m.Entity<FollowUp>(e =>
        {
            e.ToTable("followups");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("datetime('now')");
        });

        m.Entity<Ticket>(e =>
        {
            e.ToTable("tickets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasDefaultValue("未处理");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("datetime('now')");
        });

        m.Entity<ApprovalRequest>(e =>
        {
            e.ToTable("approval_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasDefaultValue("pending");
        });

        // ===== 审计 =====
        m.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("datetime('now')");
        });

        // ===== 评测 =====
        m.Entity<EvalTestCase>(e =>
        {
            e.ToTable("eval_testcases");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).IsRequired();
            e.Property(x => x.Weights).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<EvalWeights>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new());
            e.Ignore(x => x.IsPreset);
        });

        m.Entity<EvalReportEntity>(e =>
        {
            e.ToTable("eval_reports");
            e.HasKey(x => x.TaskId);
        });

        m.Entity<EvalCaseResultEntity>(e =>
        {
            e.ToTable("eval_case_results");
            e.HasKey(x => x.Id);
        });
    }
}

/// <summary>EF 映射用的评测报告实体</summary>
public class EvalReportEntity
{
    public string TaskId { get; set; } = "";
    public string CaseSet { get; set; } = "";
    public string ModesJson { get; set; } = "";
    public string Status { get; set; } = "";
    public int TotalCases { get; set; }
    public int SuccessCases { get; set; }
    public int FailedCases { get; set; }
    public double OverallScore { get; set; }
    public long AvgResponseMs { get; set; }
    public int TotalTokens { get; set; }
    public string? ComparisonJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>EF 映射用的评测单条结果实体</summary>
public class EvalCaseResultEntity
{
    public int Id { get; set; }
    public string TaskId { get; set; } = "";
    public int TestCaseId { get; set; }
    public string Mode { get; set; } = "";
    public bool RagEnabled { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string DimensionsJson { get; set; } = "";
    public long ResponseTimeMs { get; set; }
    public int TotalTokens { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double ToolAccuracy { get; set; }
    public int ExpectedToolCount { get; set; }
    public int ActualToolCount { get; set; }
    public string ConversationLog { get; set; } = "";
    public string AgentOutputs { get; set; } = "";
    public string ToolCallLog { get; set; } = "";
    public string JudgeRawOutput { get; set; } = "";
    public DateTime ExecutedAt { get; set; }
}
