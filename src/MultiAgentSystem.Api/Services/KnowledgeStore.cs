// ============================================================
// KnowledgeStore - 知识库仓储（EF Core）
// ============================================================

using Microsoft.EntityFrameworkCore;
using MultiAgentSystem.Api.Data;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class KnowledgeStore
{
    private readonly IDbContextFactory<MultiAgentDbContext> _dbFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public KnowledgeStore(IDbContextFactory<MultiAgentDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        using var db = dbFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    // ===== KnowledgeBase =====

    public async Task<List<KnowledgeBase>> ListDatabasesAsync()
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.KnowledgeBases.OrderByDescending(x => x.Id).ToListAsync();
    }

    public async Task<KnowledgeBase?> GetDatabaseAsync(int id)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.KnowledgeBases.FindAsync(id);
    }

    public async Task<int> CreateDatabaseAsync(string name, string description)
    {
        using var db = _dbFactory.CreateDbContext();
        var kb = new KnowledgeBase { Name = name, Description = description, CreatedAt = DateTime.UtcNow };
        db.KnowledgeBases.Add(kb);
        await db.SaveChangesAsync();
        return kb.Id;
    }

    public async Task<bool> DeleteDatabaseAsync(int id)
    {
        using var db = _dbFactory.CreateDbContext();
        var kb = await db.KnowledgeBases.FindAsync(id);
        if (kb == null) return false;

        // 级联删除：chunks → documents → kb
        var chunkIds = await db.DocumentChunks.Where(c => c.DatabaseId == id).Select(c => c.Id).ToListAsync();
        foreach (var chunkId in chunkIds) { var c = new DocumentChunk { Id = chunkId }; db.DocumentChunks.Remove(c); }

        var docIds = await db.KnowledgeDocuments.Where(d => d.DatabaseId == id).Select(d => d.Id).ToListAsync();
        foreach (var docId in docIds) { var d = new KnowledgeDocument { Id = docId }; db.KnowledgeDocuments.Remove(d); }

        db.KnowledgeBases.Remove(kb);
        await db.SaveChangesAsync();
        return true;
    }

    // ===== Document =====

    public async Task<List<KnowledgeDocument>> ListDocumentsAsync(int databaseId)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.KnowledgeDocuments.Where(d => d.DatabaseId == databaseId)
            .OrderByDescending(d => d.CreatedAt).ToListAsync();
    }

    public async Task<KnowledgeDocument?> GetDocumentAsync(int id)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.KnowledgeDocuments.FindAsync(id);
    }

    public async Task<int> CreateDocumentAsync(KnowledgeDocument doc)
    {
        using var db = _dbFactory.CreateDbContext();
        doc.CreatedAt = DateTime.UtcNow;
        db.KnowledgeDocuments.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    public async Task UpdateDocumentStatusAsync(int id, DocumentStatus status, int chunkCount, string? errorMessage = null)
    {
        using var db = _dbFactory.CreateDbContext();
        var doc = await db.KnowledgeDocuments.FindAsync(id);
        if (doc == null) return;
        doc.Status = status;
        doc.ChunkCount = chunkCount;
        doc.ErrorMessage = errorMessage;
        await db.SaveChangesAsync();
    }

    public async Task<bool> DeleteDocumentAsync(int id)
    {
        using var db = _dbFactory.CreateDbContext();
        var doc = await db.KnowledgeDocuments.FindAsync(id);
        if (doc == null) return false;
        await db.DocumentChunks.Where(c => c.DocumentId == id).ExecuteDeleteAsync();
        db.KnowledgeDocuments.Remove(doc);
        await db.SaveChangesAsync();
        return true;
    }

    // ===== Chunk =====

    public async Task<int> AddChunkAsync(DocumentChunk chunk, float[]? embedding)
    {
        using var db = _dbFactory.CreateDbContext();
        chunk.Embedding = embedding?.ToList();
        db.DocumentChunks.Add(chunk);
        await db.SaveChangesAsync();
        return chunk.Id;
    }

    public async Task<List<DocumentChunk>> ListChunksByDocumentAsync(int documentId)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.DocumentChunks.Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.ChunkIndex).ToListAsync();
    }

    public async Task<List<(DocumentChunk chunk, float[]? embedding)>> ListChunksWithEmbeddingAsync(int databaseId)
    {
        using var db = _dbFactory.CreateDbContext();
        var chunks = await db.DocumentChunks.Where(c => c.DatabaseId == databaseId).ToListAsync();
        return chunks.Select(c => (c, c.Embedding?.ToArray())).ToList();
    }

    public async Task ClearChunksByDocumentAsync(int documentId)
    {
        using var db = _dbFactory.CreateDbContext();
        await db.DocumentChunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync();
    }

    public async Task<DocumentChunk?> GetChunkByIdAsync(int chunkId)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.DocumentChunks.FindAsync(chunkId);
    }

    public async Task<int> GetChunkCountAsync(int? databaseId)
    {
        using var db = _dbFactory.CreateDbContext();
        return databaseId.HasValue
            ? await db.DocumentChunks.CountAsync(c => c.DatabaseId == databaseId.Value)
            : await db.DocumentChunks.CountAsync();
    }

    public async Task<List<(int chunkId, int databaseId, string content, int documentId, string fileName)>>
        SearchChunksByContentAsync(string keyword, int? databaseId = null, int limit = 20)
    {
        using var db = _dbFactory.CreateDbContext();
        var q = db.DocumentChunks.AsQueryable();
        if (databaseId.HasValue) q = q.Where(c => c.DatabaseId == databaseId.Value);
        q = q.Where(c => c.Content.Contains(keyword));
        var chunks = await q.Take(limit).ToListAsync();

        var docIds = chunks.Select(c => c.DocumentId).Distinct().ToList();
        var docs = await db.KnowledgeDocuments.Where(d => docIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id);

        return chunks.Select(c => (c.Id, c.DatabaseId, c.Content, c.DocumentId,
            docs.TryGetValue(c.DocumentId, out var d) ? d.FileName : "unknown")).ToList();
    }

    // ===== Memory =====

    public async Task<int> AddMemoryAsync(string sessionId, MemoryType type, string content)
    {
        using var db = _dbFactory.CreateDbContext();
        var m = new MemoryRecord { SessionId = sessionId, Type = type, Content = content, CreatedAt = DateTime.UtcNow };
        db.MemoryRecords.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    public async Task<List<MemoryRecord>> GetMemoryBySessionAsync(string sessionId, MemoryType? type = null, int limit = 50)
    {
        using var db = _dbFactory.CreateDbContext();
        var q = db.MemoryRecords.Where(m => m.SessionId == sessionId);
        if (type.HasValue) q = q.Where(m => m.Type == type.Value);
        return await q.OrderByDescending(m => m.CreatedAt).Take(limit).ToListAsync();
    }

    public async Task<List<MemoryRecord>> SearchMemoryByKeywordAsync(string sessionId, string keyword, int limit = 10)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.MemoryRecords
            .Where(m => m.SessionId == sessionId && m.Content.Contains(keyword))
            .OrderByDescending(m => m.CreatedAt).Take(limit).ToListAsync();
    }

    public async Task UpsertProfileAsync(string sessionId, string key, string value)
    {
        // UserProfile uses raw SQL in original - keep simple with raw SQL fallback
        using var db = _dbFactory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO user_profiles (session_id, key, value) VALUES ({0}, {1}, {2}) ON CONFLICT(session_id, key) DO UPDATE SET value={2}",
            sessionId, key, value);
    }

    public async Task<List<UserProfile>> GetProfilesAsync(string sessionId)
    {
        using var db = _dbFactory.CreateDbContext();
        // Use raw query since UserProfile is not an EF entity (table only)
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT session_id, key, value FROM user_profiles WHERE session_id=@sid;";
        var p = cmd.CreateParameter(); p.ParameterName = "@sid"; p.Value = sessionId; cmd.Parameters.Add(p);
        var list = new List<UserProfile>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new UserProfile { Id = 0, SessionId = r.GetString(0), Key = r.GetString(1), Value = r.GetString(2) });
        return list;
    }

    public async Task<Dictionary<string, int>> GetRagStatsAsync()
    {
        using var db = _dbFactory.CreateDbContext();
        return new Dictionary<string, int>
        {
            ["databases"] = await db.KnowledgeBases.CountAsync(),
            ["documents"] = await db.KnowledgeDocuments.CountAsync(),
            ["chunks"] = await db.DocumentChunks.CountAsync(),
            ["memories"] = await db.MemoryRecords.CountAsync()
        };
    }
}
