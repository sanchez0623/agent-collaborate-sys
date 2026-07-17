// ============================================================
// MultiAgentDbContext - EF Core 统一数据上下文
// 配置驱动：appsettings.json → Database:Provider 选 SQLite/PgSQL
// ============================================================

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Data;

public class MultiAgentDbContext : DbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<ApprovalRequest> Approvals => Set<ApprovalRequest>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<User> Users => Set<User>();
    public DbSet<KnowledgeBase> KnowledgeBases => Set<KnowledgeBase>();
    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<MemoryRecord> MemoryRecords => Set<MemoryRecord>();
    public DbSet<EvalTestCase> EvalTestCases => Set<EvalTestCase>();
    public DbSet<EvalReportEntity> EvalReports => Set<EvalReportEntity>();
    public DbSet<EvalCaseResultEntity> EvalCaseResults => Set<EvalCaseResultEntity>();

    public MultiAgentDbContext(DbContextOptions<MultiAgentDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder m)
    {
        var jo = new JsonSerializerOptions();

        m.Entity<Customer>(e => { e.ToTable("customers"); e.HasKey(x => x.Id); e.Property(x => x.Name).IsRequired(); e.Property(x => x.Level).HasDefaultValue("普通"); });
        m.Entity<FollowUp>(e => { e.ToTable("followups"); e.HasKey(x => x.Id); e.Property(x => x.CreatedAt).HasDefaultValueSql("datetime('now')"); });
        m.Entity<Ticket>(e => { e.ToTable("tickets"); e.HasKey(x => x.Id); e.Property(x => x.Status).HasConversion<string>().HasDefaultValue("未处理"); e.Property(x => x.CreatedAt).HasDefaultValueSql("datetime('now')"); });
        m.Entity<ApprovalRequest>(e => { e.ToTable("approval_requests"); e.HasKey(x => x.Id); e.Property(x => x.Status).HasConversion<string>().HasDefaultValue("pending"); });
        m.Entity<AuditLog>(e => { e.ToTable("audit_logs"); e.HasKey(x => x.Id); e.Property(x => x.Type).HasConversion<string>(); e.Property(x => x.CreatedAt).HasDefaultValueSql("datetime('now')"); });
        m.Entity<User>(e => { e.ToTable("users"); e.HasKey(x => x.Username); e.Property(x => x.Role).HasConversion<string>().HasDefaultValue("User"); });
        m.Entity<KnowledgeBase>(e => { e.ToTable("knowledge_bases"); e.HasKey(x => x.Id); e.Property(x => x.Name).IsRequired(); e.Property(x => x.CreatedAt).HasDefaultValueSql("datetime('now')"); e.Ignore(x => x.DocumentCount); e.Ignore(x => x.ChunkCount); });
        m.Entity<KnowledgeDocument>(e => { e.ToTable("knowledge_documents"); e.HasKey(x => x.Id); e.Property(x => x.Status).HasConversion<string>(); e.Property(x => x.CreatedAt).HasDefaultValueSql("datetime('now')"); });
        m.Entity<DocumentChunk>(e => { e.ToTable("document_chunks"); e.HasKey(x => x.Id); e.Property(x => x.Embedding).HasConversion(v => JsonSerializer.Serialize(v, jo), v => JsonSerializer.Deserialize<List<float>>(v, jo) ?? new()); e.Property(x => x.PageNumber).HasDefaultValue(1); });
        m.Entity<MemoryRecord>(e => { e.ToTable("memory_records"); e.HasKey(x => x.Id); e.Property(x => x.Type).HasConversion<string>(); e.Property(x => x.CreatedAt).HasDefaultValueSql("datetime('now')"); });
        m.Entity<EvalTestCase>(e => { e.ToTable("eval_testcases"); e.HasKey(x => x.Id); e.Property(x => x.Weights).HasConversion(v => JsonSerializer.Serialize(v, jo), v => JsonSerializer.Deserialize<EvalWeights>(v, jo) ?? new()); e.Ignore(x => x.IsPreset); });
        m.Entity<EvalReportEntity>(e => { e.ToTable("eval_reports"); e.HasKey(x => x.TaskId); });
        m.Entity<EvalCaseResultEntity>(e => { e.ToTable("eval_case_results"); e.HasKey(x => x.Id); });
    }
}

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
