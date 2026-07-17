// ============================================================
// ConversationStore - SQLite 对话历史存储
// 作用：替代 MVP-0 的内存 ConcurrentDictionary，实现持久化存储
// 重启服务后对话历史不丢失
//
// 表结构：
//   conversations - 会话表（id, created_at）
//   messages      - 消息表（id, conversation_id, role, content, created_at）
//
// 使用 Microsoft.Data.Sqlite，零配置、文件型数据库（multiagent.db）
// ============================================================

using Microsoft.Data.Sqlite;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class ConversationStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1); // SQLite 单写并发控制

    public ConversationStore(string dbPath = "multiagent.db")
    {
        dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? dbPath;
        // 数据源=文件路径；Pooling=false 避免多连接写冲突
        _connectionString = $"Data Source={dbPath}";
        // 启动时自动建表
        EnsureCreated();
    }

    /// <summary>
    /// 创建表结构（如不存在）
    /// 使用 IF NOT EXISTS 保证可重复执行
    /// </summary>
    private void EnsureCreated()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // 会话表
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS conversations (
                    id TEXT PRIMARY KEY,
                    created_at TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // 消息表
        // conversation_id 建立索引加速按会话查询
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (conversation_id) REFERENCES conversations(id)
                );
                CREATE INDEX IF NOT EXISTS idx_messages_conversation ON messages(conversation_id);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 创建新会话，返回会话ID
    /// </summary>
    public async Task<string> CreateConversationAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO conversations (id, created_at) VALUES (@id, @time);";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
        return id;
    }

    /// <summary>
    /// 追加一条消息到指定会话
    /// </summary>
    public async Task AddMessageAsync(string conversationId, string role, string content)
    {
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO messages (conversation_id, role, content, created_at)
                VALUES (@cid, @role, @content, @time);
                """;
            cmd.Parameters.AddWithValue("@cid", conversationId);
            cmd.Parameters.AddWithValue("@role", role);
            cmd.Parameters.AddWithValue("@content", content);
            cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// 获取指定会话的全部历史消息（按时间正序）
    /// 用于把历史上下文传给 Agent
    /// </summary>
    public async Task<List<ChatMessage>> GetHistoryAsync(string conversationId)
    {
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT role, content FROM messages
                WHERE conversation_id = @cid
                ORDER BY id ASC;
                """;
            cmd.Parameters.AddWithValue("@cid", conversationId);

            var list = new List<ChatMessage>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new ChatMessage(reader.GetString(0), reader.GetString(1)));
            }
            return list;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// 检查会话是否存在（不存在则自动创建）
    /// 保证首次请求传入的 sessionId 能被正常持久化
    /// </summary>
    public async Task EnsureConversationAsync(string conversationId)
    {
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO conversations (id, created_at) VALUES (@id, @time);
                """;
            cmd.Parameters.AddWithValue("@id", conversationId);
            cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// 清空所有历史对话（conversations + messages 级联删除）
    /// </summary>
    public async Task DeleteAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // 先删消息（FK 约束不一定开启），再删会话
            cmd.CommandText = "DELETE FROM messages;";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "DELETE FROM conversations;";
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }
}
