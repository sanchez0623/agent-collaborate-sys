// ============================================================
// BusinessStore - CRM/工单/审核/审计/用户 统一 SQLite 存储
//
// 设计说明：
//   - 复用 MVP-1/2 的 multiagent.db 文件，新增 7 张业务表
//   - 所有写入操作自动记录审计日志（DataChange 类）
//   - 简单 SHA256 密码哈希（演示用；生产应换 BCrypt + 加盐）
//   - 预置一个 admin / 一个 user 账户 + 若干示例客户，方便面试演示
//
// 表清单：
//   customers / contacts / followups / tickets /
//   approval_requests / audit_logs / users
// ============================================================

using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class BusinessStore
{
    private readonly string _connStr;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BusinessStore(string dbPath = "multiagent.db")
    {
        dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? dbPath;
        _connStr = $"Data Source={dbPath}";
        EnsureCreated();
        SeedData();
    }

    // ---------- 建表 ----------
    private void EnsureCreated()
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS customers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                company TEXT NOT NULL DEFAULT '',
                phone TEXT NOT NULL DEFAULT '',
                email TEXT NOT NULL DEFAULT '',
                level TEXT NOT NULL DEFAULT '普通',
                owner TEXT NOT NULL DEFAULT '',
                remark TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS contacts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                customer_id INTEGER NOT NULL,
                name TEXT NOT NULL DEFAULT '',
                position TEXT NOT NULL DEFAULT '',
                phone TEXT NOT NULL DEFAULT '',
                email TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                FOREIGN KEY (customer_id) REFERENCES customers(id)
            );
            CREATE TABLE IF NOT EXISTS followups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                customer_id INTEGER NOT NULL,
                method TEXT NOT NULL DEFAULT '电话',
                content TEXT NOT NULL DEFAULT '',
                operator TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL,
                FOREIGN KEY (customer_id) REFERENCES customers(id)
            );
            CREATE TABLE IF NOT EXISTS tickets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                source TEXT NOT NULL DEFAULT '用户提交',
                assignee TEXT NOT NULL DEFAULT '',
                priority TEXT NOT NULL DEFAULT '中',
                status TEXT NOT NULL DEFAULT 'Pending',
                created_by TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS approval_requests (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                agent TEXT NOT NULL DEFAULT '',
                system TEXT NOT NULL DEFAULT '',
                action TEXT NOT NULL DEFAULT '',
                parameters TEXT NOT NULL DEFAULT '{}',
                risk_level TEXT NOT NULL DEFAULT '中',
                status TEXT NOT NULL DEFAULT 'Pending',
                reviewer TEXT,
                review_comment TEXT,
                created_at TEXT NOT NULL,
                reviewed_at TEXT
            );
            CREATE TABLE IF NOT EXISTS audit_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                type TEXT NOT NULL,
                actor TEXT NOT NULL DEFAULT '',
                action TEXT NOT NULL DEFAULT '',
                detail TEXT,
                result TEXT NOT NULL DEFAULT 'success',
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                username TEXT NOT NULL UNIQUE,
                password_hash TEXT NOT NULL,
                display_name TEXT NOT NULL DEFAULT '',
                role TEXT NOT NULL DEFAULT 'User',
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_customers_owner ON customers(owner);
            CREATE INDEX IF NOT EXISTS idx_followups_customer ON followups(customer_id);
            CREATE INDEX IF NOT EXISTS idx_approval_status ON approval_requests(status);
            CREATE INDEX IF NOT EXISTS idx_audit_created ON audit_logs(created_at);
            """;
        cmd.ExecuteNonQuery();
    }

    // ---------- 预置演示数据 ----------
    private void SeedData()
    {
        using var conn = new SqliteConnection(_connStr);
        conn.Open();
        // 仅在 users 为空时插入
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM users;";
        var cnt = Convert.ToInt32(check.ExecuteScalar());
        if (cnt > 0) return;

        var now = DateTime.UtcNow.ToString("o");
        // 两个账户：admin（审核人）/ alice（普通用户）
        var adminHash = HashPwd("admin123");
        var userHash = HashPwd("user123");
        using var seed = conn.CreateCommand();
        seed.CommandText = """
            INSERT INTO users (username, password_hash, display_name, role, created_at) VALUES
              ('admin', @ah, '系统管理员', 'Admin', @t),
              ('alice', @uh, 'Alice', 'User', @t);
            INSERT INTO customers (name, company, phone, email, level, owner, remark, created_at, updated_at) VALUES
              ('张三', '字节跳动', '13800000001', 'zhangsan@bytedance.com', '重要', 'alice', '技术决策人', @t, @t),
              ('李四', '美团', '13800000002', 'lisi@meituan.com', '普通', 'alice', NULL, @t, @t),
              ('王五', '腾讯', '13800000003', 'wangwu@tencent.com', '战略', 'admin', '高层关系', @t, @t);
            INSERT INTO followups (customer_id, method, content, operator, created_at) VALUES
              (1, '电话', '沟通了技术方案选型，对方感兴趣', 'alice', @t),
              (3, '拜访', '拜访王总，确认下季度合作意向', 'admin', @t);
            INSERT INTO tickets (title, description, source, assignee, priority, status, created_by, created_at, updated_at) VALUES
              ('为张三准备技术方案', '客户要求提供详细技术方案', '用户提交', 'Coder', '高', 'Processing', 'alice', @t, @t),
              ('跟进腾讯合作', '战略客户需要持续维护', 'Agent 创建', 'Consultant', '紧急', 'Pending', 'admin', @t, @t);
            """;
        seed.Parameters.AddWithValue("@ah", adminHash);
        seed.Parameters.AddWithValue("@uh", userHash);
        seed.Parameters.AddWithValue("@t", now);
        seed.ExecuteNonQuery();
    }

    private static string HashPwd(string pwd)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pwd));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool VerifyPwd(string pwd, string hash) => HashPwd(pwd) == hash;

    // ============================================================
    // 通用执行辅助
    // ============================================================
    private async Task<T> WithLock<T>(Func<Task<T>> fn)
    {
        await _lock.WaitAsync();
        try { return await fn(); }
        finally { _lock.Release(); }
    }

    // ============================================================
    // 客户 CRUD
    // ============================================================
    public async Task<List<Customer>> ListCustomersAsync(string? owner = null, string? keyword = null)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            var where = new List<string>();
            if (!string.IsNullOrEmpty(owner)) { where.Add("owner = @owner"); cmd.Parameters.AddWithValue("@owner", owner); }
            if (!string.IsNullOrEmpty(keyword)) { where.Add("(name LIKE @kw OR company LIKE @kw OR phone LIKE @kw)"); cmd.Parameters.AddWithValue("@kw", $"%{keyword}%"); }
            cmd.CommandText = "SELECT id,name,company,phone,email,level,owner,remark,created_at,updated_at FROM customers";
            if (where.Count > 0) cmd.CommandText += " WHERE " + string.Join(" AND ", where);
            cmd.CommandText += " ORDER BY id DESC;";
            var list = new List<Customer>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(ReadCustomer(r));
            return list;
        });
    }

    public async Task<Customer?> GetCustomerAsync(int id)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,name,company,phone,email,level,owner,remark,created_at,updated_at FROM customers WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? ReadCustomer(r) : null;
        });
    }

    public async Task<int> CreateCustomerAsync(Customer c)
    {
        return await WithLock(async () =>
        {
            var now = DateTime.UtcNow;
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO customers (name,company,phone,email,level,owner,remark,created_at,updated_at)
                VALUES (@name,@company,@phone,@email,@level,@owner,@remark,@t,@t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@name", c.Name);
            cmd.Parameters.AddWithValue("@company", c.Company);
            cmd.Parameters.AddWithValue("@phone", c.Phone);
            cmd.Parameters.AddWithValue("@email", c.Email);
            cmd.Parameters.AddWithValue("@level", c.Level);
            cmd.Parameters.AddWithValue("@owner", c.Owner);
            cmd.Parameters.AddWithValue("@remark", (object?)c.Remark ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@t", now.ToString("o"));
            var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            await AuditAsync(AuditLogType.DataChange, c.Owner, $"新建客户 #{id} {c.Name}", $"{{\"customer\":\"{c.Name}\"}}");
            return id;
        });
    }

    public async Task<bool> UpdateCustomerAsync(Customer c)
    {
        return await WithLock(async () =>
        {
            var now = DateTime.UtcNow;
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE customers SET name=@name,company=@company,phone=@phone,email=@email,
                    level=@level,owner=@owner,remark=@remark,updated_at=@t WHERE id=@id;
                """;
            cmd.Parameters.AddWithValue("@id", c.Id);
            cmd.Parameters.AddWithValue("@name", c.Name);
            cmd.Parameters.AddWithValue("@company", c.Company);
            cmd.Parameters.AddWithValue("@phone", c.Phone);
            cmd.Parameters.AddWithValue("@email", c.Email);
            cmd.Parameters.AddWithValue("@level", c.Level);
            cmd.Parameters.AddWithValue("@owner", c.Owner);
            cmd.Parameters.AddWithValue("@remark", (object?)c.Remark ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@t", now.ToString("o"));
            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows > 0) await AuditAsync(AuditLogType.DataChange, c.Owner, $"修改客户 #{c.Id}", null);
            return rows > 0;
        });
    }

    public async Task<bool> DeleteCustomerAsync(int id, string actor)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM customers WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", id);
            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows > 0) await AuditAsync(AuditLogType.DataChange, actor, $"删除客户 #{id}", null);
            return rows > 0;
        });
    }

    // ---------- 跟进记录 ----------
    public async Task<int> AddFollowUpAsync(FollowUp f)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO followups (customer_id,method,content,operator,created_at)
                VALUES (@cid,@m,@c,@o,@t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@cid", f.CustomerId);
            cmd.Parameters.AddWithValue("@m", f.Method);
            cmd.Parameters.AddWithValue("@c", f.Content);
            cmd.Parameters.AddWithValue("@o", f.Operator);
            cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("o"));
            var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            await AuditAsync(AuditLogType.DataChange, f.Operator, $"添加跟进 #{id} 客户#{f.CustomerId}", f.Content);
            return id;
        });
    }

    public async Task<List<FollowUp>> ListFollowUpsAsync(int customerId)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,customer_id,method,content,operator,created_at FROM followups WHERE customer_id=@cid ORDER BY id DESC;";
            cmd.Parameters.AddWithValue("@cid", customerId);
            var list = new List<FollowUp>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FollowUp
                {
                    Id = r.GetInt32(0),
                    CustomerId = r.GetInt32(1),
                    Method = r.GetString(2),
                    Content = r.GetString(3),
                    Operator = r.GetString(4),
                    CreatedAt = DateTime.Parse(r.GetString(5))
                });
            return list;
        });
    }

    // ============================================================
    // 工单 CRUD + 状态流转
    // ============================================================
    public async Task<List<Ticket>> ListTicketsAsync(TicketStatus? status = null)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,title,description,source,assignee,priority,status,created_by,created_at,updated_at FROM tickets";
            if (status.HasValue) { cmd.CommandText += " WHERE status=@s"; cmd.Parameters.AddWithValue("@s", status.Value.ToString()); }
            cmd.CommandText += " ORDER BY id DESC;";
            var list = new List<Ticket>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(ReadTicket(r));
            return list;
        });
    }

    public async Task<int> CreateTicketAsync(Ticket t)
    {
        return await WithLock(async () =>
        {
            var now = DateTime.UtcNow;
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tickets (title,description,source,assignee,priority,status,created_by,created_at,updated_at)
                VALUES (@title,@desc,@src,@asg,@pri,@st,@by,@t,@t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@title", t.Title);
            cmd.Parameters.AddWithValue("@desc", t.Description);
            cmd.Parameters.AddWithValue("@src", t.Source);
            cmd.Parameters.AddWithValue("@asg", t.Assignee);
            cmd.Parameters.AddWithValue("@pri", t.Priority);
            cmd.Parameters.AddWithValue("@st", t.Status.ToString());
            cmd.Parameters.AddWithValue("@by", (object?)t.CreatedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@t", now.ToString("o"));
            var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            await AuditAsync(AuditLogType.DataChange, t.CreatedBy ?? "system", $"创建工单 #{id} {t.Title}", null);
            return id;
        });
    }

    public async Task<bool> UpdateTicketStatusAsync(int id, TicketStatus status, string actor)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE tickets SET status=@s, updated_at=@t WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@s", status.ToString());
            cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("o"));
            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows > 0) await AuditAsync(AuditLogType.DataChange, actor, $"工单 #{id} 状态→{status}", null);
            return rows > 0;
        });
    }

    // ============================================================
    // 人审请求
    // ============================================================
    public async Task<int> CreateApprovalAsync(ApprovalRequest a)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO approval_requests (session_id,agent,system,action,parameters,risk_level,status,created_at)
                VALUES (@sid,@agent,@sys,@act,@param,@risk,'Pending',@t);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@sid", a.SessionId);
            cmd.Parameters.AddWithValue("@agent", a.Agent);
            cmd.Parameters.AddWithValue("@sys", a.System);
            cmd.Parameters.AddWithValue("@act", a.Action);
            cmd.Parameters.AddWithValue("@param", a.Parameters);
            cmd.Parameters.AddWithValue("@risk", a.RiskLevel);
            cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("o"));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });
    }

    public async Task<ApprovalRequest?> GetApprovalAsync(int id)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,session_id,agent,system,action,parameters,risk_level,status,reviewer,review_comment,created_at,reviewed_at FROM approval_requests WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? ReadApproval(r) : null;
        });
    }

    public async Task<List<ApprovalRequest>> ListApprovalsAsync(ApprovalStatus? status = null)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,session_id,agent,system,action,parameters,risk_level,status,reviewer,review_comment,created_at,reviewed_at FROM approval_requests";
            if (status.HasValue) { cmd.CommandText += " WHERE status=@s"; cmd.Parameters.AddWithValue("@s", status.Value.ToString()); }
            cmd.CommandText += " ORDER BY id DESC;";
            var list = new List<ApprovalRequest>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(ReadApproval(r));
            return list;
        });
    }

    public async Task<bool> UpdateApprovalAsync(int id, ApprovalStatus status, string reviewer, string? comment)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE approval_requests SET status=@s, reviewer=@r, review_comment=@c, reviewed_at=@t WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@s", status.ToString());
            cmd.Parameters.AddWithValue("@r", reviewer);
            cmd.Parameters.AddWithValue("@c", (object?)comment ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("o"));
            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows > 0) await AuditAsync(AuditLogType.Approval, reviewer, $"审核 #{id} → {status}", comment);
            return rows > 0;
        });
    }

    // ============================================================
    // 用户
    // ============================================================
    public async Task<User?> GetUserAsync(string username)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,username,password_hash,display_name,role,created_at FROM users WHERE username=@u;";
            cmd.Parameters.AddWithValue("@u", username);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new User
            {
                Id = r.GetInt32(0),
                Username = r.GetString(1),
                PasswordHash = r.GetString(2),
                DisplayName = r.GetString(3),
                Role = Enum.Parse<UserRole>(r.GetString(4)),
                CreatedAt = DateTime.Parse(r.GetString(5))
            };
        });
    }

    // ============================================================
    // 审计日志
    // ============================================================
    public async Task AuditAsync(AuditLogType type, string actor, string action, string? detail, string result = "success")
    {
        // 审计日志内部写入，避免与外层锁冲突，开独立连接
        try
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO audit_logs (type,actor,action,detail,result,created_at) VALUES (@t,@a,@act,@d,@r,@time);";
            cmd.Parameters.AddWithValue("@t", type.ToString());
            cmd.Parameters.AddWithValue("@a", actor);
            cmd.Parameters.AddWithValue("@act", action);
            cmd.Parameters.AddWithValue("@d", (object?)detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@r", result);
            cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* 审计日志失败不应影响主流程 */ }
    }

    public async Task<List<AuditLog>> ListAuditLogsAsync(int limit = 100)
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,type,actor,action,detail,result,created_at FROM audit_logs ORDER BY id DESC LIMIT @lim;";
            cmd.Parameters.AddWithValue("@lim", limit);
            var list = new List<AuditLog>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new AuditLog
                {
                    Id = r.GetInt32(0),
                    Type = Enum.Parse<AuditLogType>(r.GetString(1)),
                    Actor = r.GetString(2),
                    Action = r.GetString(3),
                    Detail = r.IsDBNull(4) ? null : r.GetString(4),
                    Result = r.GetString(5),
                    CreatedAt = DateTime.Parse(r.GetString(6))
                });
            return list;
        });
    }

    // ============================================================
    // 仪表盘统计
    // ============================================================
    public async Task<Dictionary<string, int>> GetStatsAsync()
    {
        return await WithLock(async () =>
        {
            using var conn = new SqliteConnection(_connStr);
            await conn.OpenAsync();
            var stats = new Dictionary<string, int>();
            using var cmd = conn.CreateCommand();
            string[] queries = { "customers", "tickets", "approval_requests", "followups" };
            foreach (var q in queries)
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM {q};";
                stats[q] = Convert.ToInt32(cmd.ExecuteScalar());
            }
            // 今日跟进
            cmd.CommandText = "SELECT COUNT(*) FROM followups WHERE date(created_at)=date('now');";
            stats["today_followups"] = Convert.ToInt32(cmd.ExecuteScalar());
            // 待审核
            cmd.CommandText = "SELECT COUNT(*) FROM approval_requests WHERE status='Pending';";
            stats["pending_approvals"] = Convert.ToInt32(cmd.ExecuteScalar());
            return stats;
        });
    }

    // ---------- Reader 映射 ----------
    private static Customer ReadCustomer(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Name = r.GetString(1),
        Company = r.IsDBNull(2) ? "" : r.GetString(2),
        Phone = r.IsDBNull(3) ? "" : r.GetString(3),
        Email = r.IsDBNull(4) ? "" : r.GetString(4),
        Level = r.IsDBNull(5) ? "普通" : r.GetString(5),
        Owner = r.IsDBNull(6) ? "" : r.GetString(6),
        Remark = r.IsDBNull(7) ? null : r.GetString(7),
        CreatedAt = DateTime.Parse(r.GetString(8)),
        UpdatedAt = DateTime.Parse(r.GetString(9))
    };

    private static Ticket ReadTicket(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Title = r.GetString(1),
        Description = r.IsDBNull(2) ? "" : r.GetString(2),
        Source = r.IsDBNull(3) ? "" : r.GetString(3),
        Assignee = r.IsDBNull(4) ? "" : r.GetString(4),
        Priority = r.IsDBNull(5) ? "中" : r.GetString(5),
        Status = Enum.Parse<TicketStatus>(r.GetString(6)),
        CreatedBy = r.IsDBNull(7) ? null : r.GetString(7),
        CreatedAt = DateTime.Parse(r.GetString(8)),
        UpdatedAt = DateTime.Parse(r.GetString(9))
    };

    private static ApprovalRequest ReadApproval(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        SessionId = r.GetString(1),
        Agent = r.GetString(2),
        System = r.GetString(3),
        Action = r.GetString(4),
        Parameters = r.GetString(5),
        RiskLevel = r.GetString(6),
        Status = Enum.Parse<ApprovalStatus>(r.GetString(7)),
        Reviewer = r.IsDBNull(8) ? null : r.GetString(8),
        ReviewComment = r.IsDBNull(9) ? null : r.GetString(9),
        CreatedAt = DateTime.Parse(r.GetString(10)),
        ReviewedAt = r.IsDBNull(11) ? null : DateTime.Parse(r.GetString(11))
    };
}
