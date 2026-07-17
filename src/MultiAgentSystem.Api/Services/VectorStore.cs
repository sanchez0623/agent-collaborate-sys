// ============================================================
// InMemoryVectorStore - 内存向量存储（演示/降级用）
//
// 设计意图：
//   - 用 ConcurrentDictionary 在进程内维护 chunkId → float[] 映射
//   - 支持余弦相似度 Top-K 检索
//   - 维护 chunkId → documentId / databaseId 反向映射，支持级联删除
//
// 适用场景：
//   - 演示项目、开发环境（无需额外 Docker 依赖）
//   - Qdrant 不可用时的自动降级方案
//   - 业务量级 < 10 万分片，O(N·D) 余弦扫描延迟可接受（<100ms）
//
// 替换路径：在 DI 容器替换注册为 services.AddSingleton<IVectorStore, QdrantVectorStore>()
//  其余代码对 IVectorStore 编程，无需改动。
// ============================================================

using System.Collections.Concurrent;

namespace MultiAgentSystem.Api.Services;

public class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<int, float[]> _vectors = new();
    private readonly ConcurrentDictionary<int, int> _chunkToDoc = new();
    private readonly ConcurrentDictionary<int, int> _chunkToDb = new();
    private readonly HashSet<int> _loadedDatabases = new();
    private readonly object _loadLock = new();

    /// <summary>添加单个向量</summary>
    public Task AddVectorAsync(int chunkId, float[] vector, int documentId, int databaseId)
    {
        _vectors[chunkId] = vector;
        _chunkToDoc[chunkId] = documentId;
        _chunkToDb[chunkId] = databaseId;
        return Task.CompletedTask;
    }

    /// <summary>批量添加（初始化 / 批量导入）</summary>
    public Task AddRangeAsync(IEnumerable<(int chunkId, float[] vector, int documentId, int databaseId)> items)
    {
        foreach (var (cid, vec, docId, dbId) in items)
        {
            _vectors[cid] = vec;
            _chunkToDoc[cid] = docId;
            _chunkToDb[cid] = dbId;
        }
        return Task.CompletedTask;
    }

    /// <summary>余弦相似度 Top-K 检索（内存暴力扫描 O(N·D)）</summary>
    public Task<List<(int chunkId, double score)>> SearchAsync(float[] queryVector, int topK, int? databaseId = null)
    {
        if (_vectors.IsEmpty || queryVector.Length == 0) return Task.FromResult(new List<(int, double)>());
        var qNorm = Math.Sqrt(queryVector.Sum(v => (double)v * v));
        if (qNorm == 0) return Task.FromResult(new List<(int, double)>());

        var results = new List<(int chunkId, double score)>();
        foreach (var kv in _vectors)
        {
            if (databaseId.HasValue && _chunkToDb.TryGetValue(kv.Key, out var dbId) && dbId != databaseId.Value)
                continue;

            var vec = kv.Value;
            if (vec.Length != queryVector.Length) continue;

            var dot = 0.0;
            var vNorm = 0.0;
            for (int i = 0; i < vec.Length; i++)
            {
                dot += vec[i] * queryVector[i];
                vNorm += (double)vec[i] * vec[i];
            }
            if (vNorm == 0) continue;
            results.Add((kv.Key, dot / (qNorm * Math.Sqrt(vNorm))));
        }

        return Task.FromResult(results
            .OrderByDescending(r => r.score)
            .Take(topK)
            .ToList());
    }

    /// <summary>按文档删除全部向量</summary>
    public Task<int> RemoveByDocumentAsync(int documentId)
    {
        var chunkIds = _chunkToDoc
            .Where(kv => kv.Value == documentId)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var cid in chunkIds)
        {
            _vectors.TryRemove(cid, out _);
            _chunkToDoc.TryRemove(cid, out _);
            _chunkToDb.TryRemove(cid, out _);
        }
        return Task.FromResult(chunkIds.Count);
    }

    /// <summary>按知识库删除全部向量</summary>
    public Task<int> RemoveByDatabaseAsync(int databaseId)
    {
        var chunkIds = _chunkToDb
            .Where(kv => kv.Value == databaseId)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var cid in chunkIds)
        {
            _vectors.TryRemove(cid, out _);
            _chunkToDoc.TryRemove(cid, out _);
            _chunkToDb.TryRemove(cid, out _);
        }
        lock (_loadLock) _loadedDatabases.Remove(databaseId);
        return Task.FromResult(chunkIds.Count);
    }

    public void MarkDatabaseLoaded(int databaseId)
    {
        lock (_loadLock) _loadedDatabases.Add(databaseId);
    }

    public bool IsDatabaseLoaded(int databaseId)
    {
        lock (_loadLock) return _loadedDatabases.Contains(databaseId);
    }

    public int Count => _vectors.Count;

    public Task ClearAsync()
    {
        _vectors.Clear();
        _chunkToDoc.Clear();
        _chunkToDb.Clear();
        lock (_loadLock) _loadedDatabases.Clear();
        return Task.CompletedTask;
    }
}
