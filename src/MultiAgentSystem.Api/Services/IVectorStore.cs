// ============================================================
// IVectorStore - 向量存储抽象接口
//
// 设计意图：
//   - 解耦向量存储实现，支持 InMemory / Qdrant / Milvus 等后端切换
//   - 语义搜索（余弦相似度）统一为异步接口
//   - 级联删除按 documentId / databaseId 过滤
//   - 加载标记用于内存版避免重复从 SQLite 重建；Qdrant 天然持久化，标记为 no-op
// ============================================================

namespace MultiAgentSystem.Api.Services;

public interface IVectorStore
{
    /// <summary>添加单个向量（chunkId 为业务主键，Qdrant 中作为 payload 的 point id）</summary>
    Task AddVectorAsync(int chunkId, float[] vector, int documentId, int databaseId);

    /// <summary>批量添加（初始化加载 / 批量导入）</summary>
    Task AddRangeAsync(IEnumerable<(int chunkId, float[] vector, int documentId, int databaseId)> items);

    /// <summary>余弦相似度 Top-K 检索（按 databaseId 可选过滤）</summary>
    Task<List<(int chunkId, double score)>> SearchAsync(float[] queryVector, int topK, int? databaseId = null);

    /// <summary>按文档删除全部向量，返回删除数量</summary>
    Task<int> RemoveByDocumentAsync(int documentId);

    /// <summary>按知识库删除全部向量，返回删除数量</summary>
    Task<int> RemoveByDatabaseAsync(int databaseId);

    /// <summary>标记某 databaseId 已加载到内存（Qdrant 中为 no-op：持久化无需显式加载）</summary>
    void MarkDatabaseLoaded(int databaseId);

    /// <summary>判断某 databaseId 是否已加载（Qdrant 中始终返回 true）</summary>
    bool IsDatabaseLoaded(int databaseId);

    /// <summary>当前向量总数</summary>
    int Count { get; }

    /// <summary>清空所有向量</summary>
    Task ClearAsync();
}
