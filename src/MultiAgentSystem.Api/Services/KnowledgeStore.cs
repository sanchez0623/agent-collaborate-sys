using System.Data.Common;
// ============================================================
// KnowledgeStore - RAG 知识库 SQLite 数据层
//
// 设计意图：
//   - 复用 MVP-1/2/4 的 multiagent.db 文件（同一物理库新增表）
//   - 参照 BusinessStore 模式：Microsoft.Data.Sqlite + SemaphoreSlim 锁
//   - 5 张表：knowledge_bases / knowledge_documents / document_chunks
//            memory_records / user_profiles
//   - embedding 字段以 BLOB JSON 持久化（float[] 序列化文本）
//   - 提供 CRUD + 记忆/画像的统一访问入口
//
// 为什么复用同一 SQLite 文件：
//   - 演示项目避免多文件管理复杂度
//   - SQLite 多表共存性能开销可忽略（业务量级 < 10 万行）
//   - 跨模块联表查询便利（如审计日志与文档操作关联）
// ============================================================

using MultiAgentSystem.Api.Data;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using MultiAgentSystem.Api.Data;
using System.Text.Json;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class KnowledgeStore
{
    private readonly IDbConnectionFactory _db;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public KnowledgeStore(IDbConnectionFactory db)
    {
        _db = db;
        EnsureCreated();
    }

    // ---------- 建表 ----------
    private void EnsureCreated()
    {
        using var conn = _db.CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        // 注：embedding 字段存 JSON 文本（float[] 序列化），便于调试查看
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS knowledge_bases (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS knowledge_documents (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                database_id INTEGER NOT NULL,
                file_name TEXT NOT NULL,
                file_size INTEGER NOT NULL DEFAULT 0,
                file_type TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL DEFAULT 'Pending',
                chunk_count INTEGER NOT NULL DEFAULT 0,
                error_message TEXT,
                created_at TEXT NOT NULL,
                FOREIGN KEY (database_id) REFERENCES knowledge_bases(id)
            );
            CREATE TABLE IF NOT EXISTS document_chunks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                document_id INTEGER NOT NULL,
                database_id INTEGER NOT NULL,
                content TEXT NOT NULL,
                page_number INTEGER NOT NULL DEFAULT 1,
                chunk_index INTEGER NOT NULL DEFAULT 0,
                token_count INTEGER NOT NULL DEFAULT 0,
                embedding TEXT,
                FOREIGN KEY (document_id) REFERENCES knowledge_documents(id)
            );
            CREATE TABLE IF NOT EXISTS memory_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                type TEXT NOT NULL DEFAULT 'Message',
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS user_profiles (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_doc_db ON knowledge_documents(database_id);
            CREATE INDEX IF NOT EXISTS idx_chunk_doc ON document_chunks(document_id);
            CREATE INDEX IF NOT EXISTS idx_chunk_db ON document_chunks(database_id);
            CREATE INDEX IF NOT EXISTS idx_memory_session ON memory_records(session_id);
            CREATE INDEX IF NOT EXISTS idx_profile_session ON user_profiles(session_id);
            """;
        cmd.ExecuteNonQuery();

        // 兼容旧表：若 knowledge_documents 缺 error_message 列则补上（新表已含）
        try
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE knowledge_documents ADD COLUMN error_message TEXT;";
            alterCmd.ExecuteNonQuery();
        }
        catch (SqliteException) { /* 列已存在，忽略 */ }
    }

    // ============================================================
    // 通用锁辅助
    // ============================================================
    private async Task<T> WithLock<T>(Func<Task<T>> fn)
    {
        await _lock.WaitAsync();
        try { return await fn(); }
        finally { _lock.Release(); }
    }

    private async Task WithLock(Func<Task> fn)
    {
        await _lock.WaitAsync();
        try { await fn(); }
        finally { _lock.Release(); }
    }

    // ============================================================
    // 知识库 CRUD
    // ============================================================
    public async Task<List<KnowledgeBase>> ListDatabasesAsync()
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // 聚合统计：每个库的文档数 + 分片数
            cmd.CommandText = """
                SELECT kb.id, kb.name, kb.description, kb.created_at,
                    (SELECT COUNT(*) FROM knowledge_documents d WHERE d.database_id = kb.id) AS doc_cnt,
                    (SELECT COUNT(*) FROM document_chunks c WHERE c.database_id = kb.id) AS chunk_cnt
                FROM knowledge_bases kb
                ORDER BY kb.id DESC;
                """;
            var list = new List<KnowledgeBase>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new KnowledgeBase
                {
                    Id = r.GetInt32(0),
                    Name = r.GetString(1),
                    Description = r.IsDBNull(2) ? "" : r.GetString(2),
                    CreatedAt = DateTime.Parse(r.GetString(3)),
                    DocumentCount = r.GetInt32(4),
                    ChunkCount = r.GetInt32(5)
                });
            }
            return list;
        });
    }

    public async Task<KnowledgeBase?> GetDatabaseAsync(int id)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT kb.id, kb.name, kb.description, kb.created_at,
                    (SELECT COUNT(*) FROM knowledge_documents d WHERE d.database_id = kb.id),
                    (SELECT COUNT(*) FROM document_chunks c WHERE c.database_id = kb.id)
                FROM knowledge_bases kb WHERE kb.id=@id;
                """;
            cmd.AddParam("@id", id);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new KnowledgeBase
            {
                Id = r.GetInt32(0),
                Name = r.GetString(1),
                Description = r.IsDBNull(2) ? "" : r.GetString(2),
                CreatedAt = DateTime.Parse(r.GetString(3)),
                DocumentCount = r.GetInt32(4),
                ChunkCount = r.GetInt32(5)
            };
        });
    }

    public async Task<int> CreateDatabaseAsync(string name, string description)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO knowledge_bases (name, description, created_at)
                VALUES (@name, @desc, @t);
                SELECT last_insert_rowid();
                """;
            cmd.AddParam("@name", name);
            cmd.AddParam("@desc", description ?? "");
            cmd.AddParam("@t", DateTime.UtcNow.ToString("o"));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });
    }

    public async Task<bool> DeleteDatabaseAsync(int id)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            // 级联删除：分片 → 文档 → 知识库（SQLite 无 ON DELETE CASCADE，需手动）
            using var tx = conn.BeginTransaction();
            try
            {
                using var c1 = conn.CreateCommand();
                c1.Transaction = tx;
                c1.CommandText = "DELETE FROM document_chunks WHERE database_id=@id;";
                c1.AddParam("@id", id);
                await c1.ExecuteNonQueryAsync();

                using var c2 = conn.CreateCommand();
                c2.Transaction = tx;
                c2.CommandText = "DELETE FROM knowledge_documents WHERE database_id=@id;";
                c2.AddParam("@id", id);
                await c2.ExecuteNonQueryAsync();

                using var c3 = conn.CreateCommand();
                c3.Transaction = tx;
                c3.CommandText = "DELETE FROM knowledge_bases WHERE id=@id;";
                c3.AddParam("@id", id);
                var rows = await c3.ExecuteNonQueryAsync();

                tx.Commit();
                return rows > 0;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        });
    }

    // ============================================================
    // 文档 CRUD
    // ============================================================
    public async Task<List<KnowledgeDocument>> ListDocumentsAsync(int databaseId)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, database_id, file_name, file_size, file_type, status, chunk_count, created_at, error_message
                FROM knowledge_documents WHERE database_id=@db ORDER BY id DESC;
                """;
            cmd.AddParam("@db", databaseId);
            var list = new List<KnowledgeDocument>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(ReadDocument(r));
            return list;
        });
    }

    public async Task<KnowledgeDocument?> GetDocumentAsync(int id)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, database_id, file_name, file_size, file_type, status, chunk_count, created_at, error_message
                FROM knowledge_documents WHERE id=@id;
                """;
            cmd.AddParam("@id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? ReadDocument(r) : null;
        });
    }

    public async Task<int> CreateDocumentAsync(KnowledgeDocument doc)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO knowledge_documents (database_id, file_name, file_size, file_type, status, chunk_count, created_at, error_message)
                VALUES (@db, @name, @size, @type, @st, 0, @t, @err);
                SELECT last_insert_rowid();
                """;
            cmd.AddParam("@db", doc.DatabaseId);
            cmd.AddParam("@name", doc.FileName);
            cmd.AddParam("@size", doc.FileSize);
            cmd.AddParam("@type", doc.FileType);
            cmd.AddParam("@st", doc.Status.ToString());
            cmd.AddParam("@t", DateTime.UtcNow.ToString("o"));
            cmd.AddParam("@err", doc.ErrorMessage ?? "");
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });
    }

    public async Task UpdateDocumentStatusAsync(int id, DocumentStatus status, int chunkCount, string? errorMessage = null)
    {
        await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // 失败时写入 error_message；成功/处理中清空旧错误（避免残留）
            cmd.CommandText = "UPDATE knowledge_documents SET status=@st, chunk_count=@cc, error_message=@err WHERE id=@id;";
            cmd.AddParam("@id", id);
            cmd.AddParam("@st", status.ToString());
            cmd.AddParam("@cc", chunkCount);
            cmd.AddParam("@err", (object?)errorMessage ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task<bool> DeleteDocumentAsync(int id)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using var c1 = conn.CreateCommand();
                c1.Transaction = tx;
                c1.CommandText = "DELETE FROM document_chunks WHERE document_id=@id;";
                c1.AddParam("@id", id);
                await c1.ExecuteNonQueryAsync();

                using var c2 = conn.CreateCommand();
                c2.Transaction = tx;
                c2.CommandText = "DELETE FROM knowledge_documents WHERE id=@id;";
                c2.AddParam("@id", id);
                var rows = await c2.ExecuteNonQueryAsync();

                tx.Commit();
                return rows > 0;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        });
    }

    // ============================================================
    // 分片 CRUD + embedding 持久化
    // ============================================================
    public async Task<int> AddChunkAsync(DocumentChunk chunk, float[]? embedding)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO document_chunks (document_id, database_id, content, page_number, chunk_index, token_count, embedding)
                VALUES (@doc, @db, @content, @page, @idx, @tc, @emb);
                SELECT last_insert_rowid();
                """;
            cmd.AddParam("@doc", chunk.DocumentId);
            cmd.AddParam("@db", chunk.DatabaseId);
            cmd.AddParam("@content", chunk.Content);
            cmd.AddParam("@page", chunk.PageNumber);
            cmd.AddParam("@idx", chunk.ChunkIndex);
            cmd.AddParam("@tc", chunk.TokenCount);
            cmd.AddParam("@emb", embedding is null || embedding.Length == 0
                ? DBNull.Value
                : JsonSerializer.Serialize(embedding));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });
    }

    /// <summary>列出指定文档的全部分片（用于重新解析或批量加载向量）</summary>
    public async Task<List<DocumentChunk>> ListChunksByDocumentAsync(int documentId)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, document_id, database_id, content, page_number, chunk_index, token_count
                FROM document_chunks WHERE document_id=@doc ORDER BY chunk_index ASC;
                """;
            cmd.AddParam("@doc", documentId);
            var list = new List<DocumentChunk>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(ReadChunk(r));
            return list;
        });
    }

    /// <summary>列出指定知识库的全部分片（用于批量加载向量到内存）</summary>
    public async Task<List<(DocumentChunk chunk, float[]? embedding)>> ListChunksWithEmbeddingAsync(int databaseId)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT c.id, c.document_id, c.database_id, c.content, c.page_number, c.chunk_index, c.token_count, c.embedding,
                       d.file_name
                FROM document_chunks c
                LEFT JOIN knowledge_documents d ON d.id = c.document_id
                WHERE c.database_id=@db
                ORDER BY c.id ASC;
                """;
            cmd.AddParam("@db", databaseId);
            var list = new List<(DocumentChunk, float[]?, string)>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var ch = ReadChunk(r);
                float[]? emb = null;
                if (!r.IsDBNull(7))
                {
                    try { emb = JsonSerializer.Deserialize<float[]>(r.GetString(7)); }
                    catch { /* 损坏的 embedding 直接忽略，重新计算 */ }
                }
                var fileName = r.IsDBNull(8) ? "" : r.GetString(8);
                list.Add((ch, emb, fileName));
            }
            // 转换为返回类型（不含 fileName，调用方按需另查文档）
            return list.Select(t => (t.Item1, t.Item2)).ToList();
        });
    }

    /// <summary>删除指定文档的全部分片（重新解析前清理旧分片）</summary>
    public async Task ClearChunksByDocumentAsync(int documentId)
    {
        await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM document_chunks WHERE document_id=@doc;";
            cmd.AddParam("@doc", documentId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    /// <summary>按 ID 查询单个分片（O(1) 替代遍历全库）</summary>
    public async Task<DocumentChunk?> GetChunkByIdAsync(int chunkId)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT c.id, c.document_id, c.database_id, c.content, c.page_number, c.chunk_index, c.token_count
                FROM document_chunks c WHERE c.id=@id;
                """;
            cmd.AddParam("@id", chunkId);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? ReadChunk(r) : null;
        });
    }

    /// <summary>获取指定知识库的分片总数（用于关键词 TF-IDF 的 N 值）</summary>
    public async Task<int> GetChunkCountAsync(int? databaseId)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            if (databaseId.HasValue)
            {
                cmd.CommandText = "SELECT COUNT(*) FROM document_chunks WHERE database_id=@db;";
                cmd.AddParam("@db", databaseId.Value);
            }
            else
            {
                cmd.CommandText = "SELECT COUNT(*) FROM document_chunks;";
            }
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });
    }

    /// <summary>按关键词搜索分片（轻量：仅 id+content，不含 embedding）</summary>
    public async Task<List<(int chunkId, int databaseId, string content, int documentId, string fileName)>> SearchChunksByContentAsync(
        string keyword, int? databaseId = null, int limit = 50)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // 用 LIKE 做简易关键词匹配；生产级应用换 FTS5 全文索引
            var like = $"%{keyword}%";
            if (databaseId.HasValue)
            {
                cmd.CommandText = """
                    SELECT c.id, c.database_id, c.content, c.document_id, COALESCE(d.file_name,'')
                    FROM document_chunks c
                    LEFT JOIN knowledge_documents d ON d.id = c.document_id
                    WHERE c.database_id = @db AND c.content LIKE @kw
                    ORDER BY c.id LIMIT @limit;
                    """;
                cmd.AddParam("@db", databaseId.Value);
            }
            else
            {
                cmd.CommandText = """
                    SELECT c.id, c.database_id, c.content, c.document_id, COALESCE(d.file_name,'')
                    FROM document_chunks c
                    LEFT JOIN knowledge_documents d ON d.id = c.document_id
                    WHERE c.content LIKE @kw
                    ORDER BY c.id LIMIT @limit;
                    """;
            }
            cmd.AddParam("@kw", like);
            cmd.AddParam("@limit", limit);
            var list = new List<(int, int, string, int, string)>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetString(4)));
            return list;
        });
    }

    // ============================================================
    // 长期记忆 memory_records
    // ============================================================
    public async Task<int> AddMemoryAsync(string sessionId, MemoryType type, string content)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO memory_records (session_id, type, content, created_at)
                VALUES (@sid, @type, @content, @t);
                SELECT last_insert_rowid();
                """;
            cmd.AddParam("@sid", sessionId);
            cmd.AddParam("@type", type.ToString());
            cmd.AddParam("@content", content);
            cmd.AddParam("@t", DateTime.UtcNow.ToString("o"));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });
    }

    public async Task<List<MemoryRecord>> GetMemoryBySessionAsync(string sessionId, MemoryType? type = null, int limit = 50)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, session_id, type, content, created_at FROM memory_records WHERE session_id=@sid";
            cmd.AddParam("@sid", sessionId);
            if (type.HasValue)
            {
                cmd.CommandText += " AND type=@t";
                cmd.AddParam("@t", type.Value.ToString());
            }
            cmd.CommandText += " ORDER BY id ASC LIMIT @lim;";
            cmd.AddParam("@lim", limit);
            var list = new List<MemoryRecord>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MemoryRecord
                {
                    Id = r.GetInt32(0),
                    SessionId = r.GetString(1),
                    Type = Enum.Parse<MemoryType>(r.GetString(2)),
                    Content = r.GetString(3),
                    CreatedAt = DateTime.Parse(r.GetString(4))
                });
            return list;
        });
    }

    public async Task<List<MemoryRecord>> SearchMemoryByKeywordAsync(string sessionId, string keyword, int limit = 10)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, session_id, type, content, created_at FROM memory_records
                WHERE session_id=@sid AND content LIKE @kw
                ORDER BY id DESC LIMIT @lim;
                """;
            cmd.AddParam("@sid", sessionId);
            cmd.AddParam("@kw", $"%{keyword}%");
            cmd.AddParam("@lim", limit);
            var list = new List<MemoryRecord>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new MemoryRecord
                {
                    Id = r.GetInt32(0),
                    SessionId = r.GetString(1),
                    Type = Enum.Parse<MemoryType>(r.GetString(2)),
                    Content = r.GetString(3),
                    CreatedAt = DateTime.Parse(r.GetString(4))
                });
            return list;
        });
    }

    // ============================================================
    // 用户画像 user_profiles
    // ============================================================
    public async Task UpsertProfileAsync(string sessionId, string key, string value)
    {
        await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            // 简单策略：同 session+key 已存在则更新 value，否则插入
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO user_profiles (session_id, key, value, created_at)
                VALUES (@sid, @k, @v, @t)
                ON CONFLICT(session_id, key) DO UPDATE SET value=@v;
                """;
            // 注意：SQLite UPSERT 需要 UNIQUE 约束。这里 session_id+key 未建 UNIQUE，
            // 故退化为先 DELETE 再 INSERT，保证幂等
            cmd.CommandText = """
                DELETE FROM user_profiles WHERE session_id=@sid AND key=@k;
                INSERT INTO user_profiles (session_id, key, value, created_at)
                VALUES (@sid, @k, @v, @t);
                """;
            cmd.AddParam("@sid", sessionId);
            cmd.AddParam("@k", key);
            cmd.AddParam("@v", value);
            cmd.AddParam("@t", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task<List<UserProfile>> GetProfilesAsync(string sessionId)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, session_id, key, value, created_at FROM user_profiles WHERE session_id=@sid ORDER BY id ASC;";
            cmd.AddParam("@sid", sessionId);
            var list = new List<UserProfile>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new UserProfile
                {
                    Id = r.GetInt32(0),
                    SessionId = r.GetString(1),
                    Key = r.GetString(2),
                    Value = r.GetString(3),
                    CreatedAt = DateTime.Parse(r.GetString(4))
                });
            return list;
        });
    }

    // ============================================================
    // 仪表盘统计
    // ============================================================
    public async Task<Dictionary<string, int>> GetRagStatsAsync()
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            var stats = new Dictionary<string, int>();
            using var cmd = conn.CreateCommand();
            string[] tables = { "knowledge_bases", "knowledge_documents", "document_chunks", "memory_records", "user_profiles" };
            foreach (var t in tables)
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM {t};";
                stats[t] = Convert.ToInt32(cmd.ExecuteScalar());
            }
            cmd.CommandText = "SELECT COUNT(*) FROM knowledge_documents WHERE status='Ready';";
            stats["ready_documents"] = Convert.ToInt32(cmd.ExecuteScalar());
            cmd.CommandText = "SELECT COUNT(*) FROM knowledge_documents WHERE status='Pending' OR status='Processing';";
            stats["pending_documents"] = Convert.ToInt32(cmd.ExecuteScalar());
            cmd.CommandText = "SELECT COUNT(*) FROM knowledge_documents WHERE status='Failed';";
            stats["failed_documents"] = Convert.ToInt32(cmd.ExecuteScalar());
            return stats;
        });
    }

    // ---------- Reader 映射 ----------
    private static KnowledgeDocument ReadDocument(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        DatabaseId = r.GetInt32(1),
        FileName = r.GetString(2),
        FileSize = r.GetInt64(3),
        FileType = r.IsDBNull(4) ? "" : r.GetString(4),
        Status = Enum.Parse<DocumentStatus>(r.GetString(5)),
        ChunkCount = r.GetInt32(6),
        CreatedAt = DateTime.Parse(r.GetString(7)),
        ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8)
    };

    private static DocumentChunk ReadChunk(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        DocumentId = r.GetInt32(1),
        DatabaseId = r.GetInt32(2),
        Content = r.GetString(3),
        PageNumber = r.GetInt32(4),
        ChunkIndex = r.GetInt32(5),
        TokenCount = r.GetInt32(6)
    };
}
