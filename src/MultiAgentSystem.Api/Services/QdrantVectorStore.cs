// ============================================================
// QdrantVectorStore - Qdrant 向量数据库适配器
// ============================================================

using Google.Protobuf.Collections;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace MultiAgentSystem.Api.Services;

public class QdrantVectorStore : IVectorStore, IDisposable
{
    private readonly QdrantClient _client;
    private readonly string _collectionName = "multiagent_chunks";
    private readonly uint _vectorSize;
    private readonly ILogger<QdrantVectorStore> _logger;
    private bool _collectionReady;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public QdrantVectorStore(IConfiguration config, ILogger<QdrantVectorStore> logger)
    {
        var qdrantUrl = config.GetValue<string>("Qdrant:Url") ?? "http://localhost:6334";
        _vectorSize = config.GetValue<uint>("Embedding:Dimension", 1024);
        _client = new QdrantClient(new Uri(qdrantUrl));
        _logger = logger;
    }

    private async Task EnsureCollectionAsync()
    {
        if (_collectionReady) return;
        await _initLock.WaitAsync();
        try
        {
            if (_collectionReady) return;
            var exists = await _client.CollectionExistsAsync(_collectionName);
            if (!exists)
            {
                _logger.LogInformation("创建 Qdrant 集合 {Collection}，维度={Dim}", _collectionName, _vectorSize);
                await _client.CreateCollectionAsync(_collectionName,
                    new VectorParams { Size = _vectorSize, Distance = Distance.Cosine });
            }
            _collectionReady = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static MapField<string, Value> MakePayload(int documentId, int databaseId)
    {
        var payload = new MapField<string, Value>();
        payload["documentId"] = new Value { IntegerValue = documentId };
        payload["databaseId"] = new Value { IntegerValue = databaseId };
        return payload;
    }

    private static Filter MakeFilter(string key, long value)
    {
        return new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = key,
                        Match = new Match { Integer = value }
                    }
                }
            }
        };
    }

    // ---------- IVectorStore ----------

    public async Task AddVectorAsync(int chunkId, float[] vector, int documentId, int databaseId)
    {
        await EnsureCollectionAsync();
        var point = new PointStruct
        {
            Id = (ulong)chunkId,
            Vectors = new Vectors { Vector = new Vector { Data = { vector } } },
            Payload = { MakePayload(documentId, databaseId) }
        };
        await _client.UpsertAsync(_collectionName, new[] { point });
    }

    public async Task AddRangeAsync(IEnumerable<(int chunkId, float[] vector, int documentId, int databaseId)> items)
    {
        await EnsureCollectionAsync();
        var points = new List<PointStruct>();
        foreach (var item in items)
        {
            points.Add(new PointStruct
            {
                Id = (ulong)item.chunkId,
                Vectors = new Vectors { Vector = new Vector { Data = { item.vector } } },
                Payload = { MakePayload(item.documentId, item.databaseId) }
            });
        }
        if (points.Count > 0)
            await _client.UpsertAsync(_collectionName, points);
    }

    public async Task<List<(int chunkId, double score)>> SearchAsync(
        float[] queryVector, int topK, int? databaseId = null)
    {
        await EnsureCollectionAsync();

        Filter? filter = databaseId.HasValue
            ? MakeFilter("databaseId", databaseId.Value)
            : null;

        var results = await _client.SearchAsync(
            _collectionName,
            queryVector,
            filter,
            limit: (ulong)topK);

        return results
            .Select(r => ((int)r.Id.Num, (double)r.Score))
            .ToList();
    }

    public async Task<int> RemoveByDocumentAsync(int documentId)
    {
        await EnsureCollectionAsync();
        await _client.DeleteAsync(_collectionName, MakeFilter("documentId", documentId));
        // DeleteAsync returns void, can't get exact count - return -1 to indicate "unknown"
        return -1;
    }

    public async Task<int> RemoveByDatabaseAsync(int databaseId)
    {
        await EnsureCollectionAsync();
        await _client.DeleteAsync(_collectionName, MakeFilter("databaseId", databaseId));
        _logger.LogInformation("Qdrant 按知识库删除：databaseId={DbId}", databaseId);
        return -1;
    }

    public void MarkDatabaseLoaded(int databaseId) { }
    public bool IsDatabaseLoaded(int databaseId) => true;

    public int Count
    {
        get
        {
            try
            {
                var info = _client.GetCollectionInfoAsync(_collectionName).GetAwaiter().GetResult();
                return (int)(info?.PointsCount ?? 0);
            }
            catch
            {
                return 0;
            }
        }
    }

    public async Task ClearAsync()
    {
        await _client.DeleteCollectionAsync(_collectionName);
        _collectionReady = false;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _initLock?.Dispose();
    }
}
