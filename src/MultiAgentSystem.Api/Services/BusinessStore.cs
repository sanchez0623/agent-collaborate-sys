// ============================================================
// BusinessStore - CRM/工单/审核/审计/用户（EF Core）
// ============================================================

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MultiAgentSystem.Api.Data;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class BusinessStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BusinessStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        
        SeedData(db);
    }

    private static void SeedData(MultiAgentDbContext db)
    {
        if (!db.Users.Any())
        {
            db.Users.Add(new User { Username = "admin", PasswordHash = Hash("admin123"), DisplayName = "管理员", Role = UserRole.Admin });
            db.Users.Add(new User { Username = "user", PasswordHash = Hash("user123"), DisplayName = "普通用户", Role = UserRole.User });
            db.SaveChanges();
        }
        if (!db.Customers.Any())
        {
            db.Customers.Add(new Customer { Name = "张三", Company = "腾讯科技", Phone = "13800138000", Email = "zhangsan@qq.com", Level = "战略", Owner = "admin", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.Customers.Add(new Customer { Name = "李四", Company = "阿里巴巴", Phone = "13900139000", Level = "重要", Owner = "user", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    // ===== Customer =====

    public async Task<List<Customer>> ListCustomersAsync(string? owner = null, string? keyword = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        var q = db.Customers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(owner)) q = q.Where(c => c.Owner == owner);
        if (!string.IsNullOrWhiteSpace(keyword))
            q = q.Where(c => (c.Name ?? "").Contains(keyword) || (c.Company ?? "").Contains(keyword));
        return await q.OrderByDescending(c => c.Id).ToListAsync();
    }

    public async Task<Customer?> GetCustomerAsync(int id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        return await db.Customers.FindAsync(id);
    }

    public async Task<int> CreateCustomerAsync(Customer c)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        c.CreatedAt = c.UpdatedAt = DateTime.UtcNow;
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        await AuditAsync(db, AuditLogType.DataChange, c.Owner, $"新建客户 #{c.Id} {c.Name}", $"{{\"customer\":\"{c.Name}\"}}");
        return c.Id;
    }

    public async Task<bool> UpdateCustomerAsync(Customer c)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        c.UpdatedAt = DateTime.UtcNow;
        db.Entry(c).State = EntityState.Modified;
        var rows = await db.SaveChangesAsync();
        if (rows > 0) await AuditAsync(db, AuditLogType.DataChange, c.Owner, $"修改客户 #{c.Id}", null);
        return rows > 0;
    }

    public async Task<bool> DeleteCustomerAsync(int id, string actor)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        var c = await db.Customers.FindAsync(id);
        if (c == null) return false;
        db.Customers.Remove(c);
        var rows = await db.SaveChangesAsync();
        if (rows > 0) await AuditAsync(db, AuditLogType.DataChange, actor, $"删除客户 #{id}", null);
        return rows > 0;
    }

    // ===== FollowUp =====

    public async Task<int> AddFollowUpAsync(FollowUp f)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        f.CreatedAt = DateTime.UtcNow;
        db.FollowUps.Add(f);
        await db.SaveChangesAsync();
        await AuditAsync(db, AuditLogType.DataChange, f.Operator, $"添加跟进 #{f.Id} 客户#{f.CustomerId}", f.Content);
        return f.Id;
    }

    public async Task<FollowUp?> GetFollowUpAsync(int id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        return await db.FollowUps.FindAsync(id);
    }

    public async Task<List<FollowUp>> ListFollowUpsAsync(int customerId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        return await db.FollowUps.Where(f => f.CustomerId == customerId)
            .OrderByDescending(f => f.CreatedAt).ToListAsync();
    }

    // ===== Ticket =====

    public async Task<List<Ticket>> ListTicketsAsync(TicketStatus? status = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        var q = db.Tickets.AsQueryable();
        if (status.HasValue) q = q.Where(t => t.Status == status.Value);
        return await q.OrderByDescending(t => t.Id).ToListAsync();
    }

    public async Task<int> CreateTicketAsync(Ticket t)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        t.CreatedAt = DateTime.UtcNow;
        db.Tickets.Add(t);
        await db.SaveChangesAsync();
        await AuditAsync(db, AuditLogType.DataChange, t.CreatedBy ?? "system", $"创建工单 #{t.Id} {t.Title}", null);
        return t.Id;
    }

    public async Task<bool> UpdateTicketStatusAsync(int id, TicketStatus status, string actor)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        var t = await db.Tickets.FindAsync(id);
        if (t == null) return false;
        t.Status = status;
        t.UpdatedAt = DateTime.UtcNow;
        var rows = await db.SaveChangesAsync();
        if (rows > 0) await AuditAsync(db, AuditLogType.DataChange, actor, $"工单 #{id} 状态→{status}", null);
        return rows > 0;
    }

    // ===== Approval =====

    public async Task<int> CreateApprovalAsync(ApprovalRequest a)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        a.CreatedAt = DateTime.UtcNow;
        db.Approvals.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    public async Task<ApprovalRequest?> GetApprovalAsync(int id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        return await db.Approvals.FindAsync(id);
    }

    public async Task<List<ApprovalRequest>> ListApprovalsAsync(ApprovalStatus? status = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        var q = db.Approvals.AsQueryable();
        if (status.HasValue) q = q.Where(a => a.Status == status.Value);
        return await q.OrderByDescending(a => a.Id).ToListAsync();
    }

    public async Task<bool> UpdateApprovalAsync(int id, ApprovalStatus status, string reviewer, string? comment)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        var a = await db.Approvals.FindAsync(id);
        if (a == null) return false;
        a.Status = status;
        a.Reviewer = reviewer;
        a.ReviewedAt = DateTime.UtcNow;
        a.ReviewComment = comment;
        var rows = await db.SaveChangesAsync();
        if (rows > 0) await AuditAsync(db, AuditLogType.Approval, reviewer, $"审核 #{id} → {status}", comment);
        return rows > 0;
    }

    // ===== User =====

    public async Task<User?> GetUserAsync(string username)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        return await db.Users.FindAsync(username);
    }

    // ===== Audit =====

    public async Task AuditAsync(AuditLogType type, string actor, string action, string? detail, string result = "success")
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        db.AuditLogs.Add(new AuditLog { Type = type, Actor = actor, Action = action, Detail = detail, Result = result, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task AuditAsync(MultiAgentDbContext db, AuditLogType type, string actor, string action, string? detail, string result = "success")
    {
        db.AuditLogs.Add(new AuditLog { Type = type, Actor = actor, Action = action, Detail = detail, Result = result, CreatedAt = DateTime.UtcNow });
        await Task.CompletedTask; // saved in the parent SaveChangesAsync
    }

    public async Task<List<AuditLog>> ListAuditLogsAsync(int limit = 100)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        return await db.AuditLogs.OrderByDescending(a => a.Id).Take(limit).ToListAsync();
    }

    // ===== Stats =====

    public async Task<Dictionary<string, int>> GetStatsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiAgentDbContext>();
        var today = DateTime.UtcNow.Date;
        return new Dictionary<string, int>
        {
            ["customers"] = await db.Customers.CountAsync(),
            ["tickets"] = await db.Tickets.CountAsync(),
            ["open_tickets"] = await db.Tickets.CountAsync(t => t.Status != TicketStatus.Done),
            ["pending_approvals"] = await db.Approvals.CountAsync(a => a.Status == ApprovalStatus.Pending),
            ["today_operations"] = await db.AuditLogs.CountAsync(a => a.CreatedAt >= today)
        };
    }

    public static bool VerifyPwd(string password, string? hash)
        => !string.IsNullOrWhiteSpace(hash) && hash == Hash(password);
}
