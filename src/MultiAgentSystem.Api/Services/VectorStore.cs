// ============================================================
// VectorStore - 内存向量存储
//
// 设计意图：
//   - 用 ConcurrentDictionary 在进程内维护 chunkId → float[] 映射
//   - 支持余弦相似度 Top-K 检索
//   - 维护 chunkId → documentId / databaseId 反向映射，支持级联删除
//
// 为什么用内存向量存储而不是 Qdrant / Milvus：
//   - 演示项目避免 Docker 依赖，降低部署门槛
//   - 业务量级 < 10 万分片，内存 O(N·D) 余弦扫描延迟可接受（<100ms）
//   - 接口抽象清晰（AddVector/Search/RemoveBy*），后续可平滑替换为 Qdrant
//   - 服务重启后从 SQLite 重建向量即可（见 HybridRetriever.EnsureLoadedAsync）
//
// 替换路径：
//   1. 实现 IVectorStore 接口（与本类同名方法签名一致）
//   2. 在 DI 容器替换注册：services.AddSingleton<IVectorStore, QdrantVectorStore>()
//   3. 其余代码无感知
// ============================================================

using System.Collections.Concurrent;

namespace MultiAgentSystem.Api.Services;

public class VectorStore
{
    /// <summary>chunkId → 向量</summary>
    private readonly ConcurrentDictionary<int, float[]> _vectors = new();

    /// <summary>chunkId → documentId 反向映射（删除文档时用）</summary>
    private readonly ConcurrentDictionary<int, int> _chunkToDoc = new();

    /// <summary>chunkId → databaseId 反向映射（删除知识库时用）</summary>
    private readonly ConcurrentDictionary<int, int> _chunkToDb = new();

    /// <summary>已加载过的 databaseId 集合，避免重复加载</summary>
    private readonly HashSet<int> _loadedDatabases = new();
    private readonly object _loadLock = new();

    /// <summary>添加向量</summary>
    public void AddVector(int chunkId, float[] vector, int documentId, int databaseId)
    {
        _vectors[chunkId] = vector;
        _chunkToDoc[chunkId] = documentId;
        _chunkToDb[chunkId] = databaseId;
    }

    /// <summary>批量添加（用于初始化加载）</summary>
    public void AddRange(IEnumerable<(int chunkId, float[] vector, int documentId, int databaseId)> items)
    {
        foreach (var (cid, vec, docId, dbId) in items)
            AddVector(cid, vec, docId, dbId);
    }

    /// <summary>
    /// 余弦相似度 Top-K 检索
    ///
    /// 公式：cos(a,b) = (a·b) / (|a|·|b|)
    /// 范围：[-1, 1]，越大越相似
    /// 注意：本实现未做预归一化，每次查询都做归一化（演示量级可接受）
    /// </summary>
    public List<(int chunkId, double score)> Search(float[] queryVector, int topK, int? databaseId = null)
    {
        if (_vectors.IsEmpty || queryVector.Length == 0) return new();
        var qNorm = Math.Sqrt(queryVector.Sum(v => (double)v * v));
        if (qNorm == 0) return new();

        var results = new List<(int chunkId, double score)>();
        foreach (var kv in _vectors)
        {
            // 可选：按 databaseId 过滤（多知识库场景）
            if (databaseId.HasValue && _chunkToDb.TryGetValue(kv.Key, out var dbId) && dbId != databaseId.Value)
                continue;

            var vec = kv.Value;
            if (vec.Length != queryVector.Length)
            {
                // 维度不一致：降级方案下查询向量为 128 维、库内向量可能不同维度
                // 跳过该向量（避免越界）
                continue;
            }
            var dot = 0.0;
            var vNorm = 0.0;
            for (int i = 0; i < vec.Length; i++)
            {
                dot += vec[i] * queryVector[i];
                vNorm += (double)vec[i] * vec[i];
            }
            if (vNorm == 0) continue;
            var score = dot / (qNorm * Math.Sqrt(vNorm));
            results.Add((kv.Key, score));
        }

        return results
            .OrderByDescending(r => r.score)
            .Take(topK)
            .ToList();
    }

    /// <summary>按文档删除全部向量</summary>
    public int RemoveByDocument(int documentId)
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
        return chunkIds.Count;
    }

    /// <summary>按知识库删除全部向量</summary>
    public int RemoveByDatabase(int databaseId)
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
        // 清除加载标记，下次查询时会重新加载
        lock (_loadLock) _loadedDatabases.Remove(databaseId);
        return chunkIds.Count;
    }

    /// <summary>标记某 databaseId 已加载（避免重复加载）</summary>
    public void MarkDatabaseLoaded(int databaseId)
    {
        lock (_loadLock) _loadedDatabases.Add(databaseId);
    }

    /// <summary>判断某 databaseId 是否已加载</summary>
    public bool IsDatabaseLoaded(int databaseId)
    {
        lock (_loadLock) return _loadedDatabases.Contains(databaseId);
    }

    /// <summary>当前向量总数</summary>
    public int Count => _vectors.Count;

    /// <summary>清空所有向量（重置/演示用）</summary>
    public void Clear()
    {
        _vectors.Clear();
        _chunkToDoc.Clear();
        _chunkToDb.Clear();
        lock (_loadLock) _loadedDatabases.Clear();
    }
}
