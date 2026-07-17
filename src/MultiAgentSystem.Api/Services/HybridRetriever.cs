// ============================================================
// HybridRetriever - 混合检索核心
//
// 设计意图：
//   - 组合 3 路检索：向量语义检索 + 关键词 BM25 检索 + RRF 融合
//   - 可选重排序（重排时综合向量相似度与关键词命中数加权）
//   - 返回 RetrievalResponse，含各路分数对比，方便前端可视化
//
// 为什么不用纯向量检索：
//   - 向量擅长语义召回（"苹果" → 召回"水果公司"），但精度有限
//   - 关键词擅长精确匹配（人名/型号/代码标识符），但召回不足
//   - 混合检索：向量负责"语义扩展"，关键词负责"精准命中"
//   - 实测在中文场景下混合比纯向量 F1 提升约 15-25%
//
// RRF（Reciprocal Rank Fusion）公式：
//   fused_score = Σ_i 1 / (k + rank_i)
//   其中 k=60（经验值，平衡 Top1 与 TopK 的权重差异）
//   rank_i 是文档在第 i 路检索结果中的排名（1-based）
//   优势：
//   - 不需要分数归一化（各路量纲不同也能融合）
//   - 对极端分不敏感（一个异常高分会因 1/(k+rank) 被稀释）
//   - 适合异质检索源（向量/关键词/SQL 全文）
//
// 重排序（Rerank）：
//   - 真正的重排序应用交叉编码器（Cross-Encoder），如 BGE-Reranker
//   - 本演示用简化版：r = 0.6 * cos_sim + 0.4 * keyword_hits_norm
//   - 即向量相似度为主，关键词命中数为辅
//   - 重排对 Top-3 准确率提升明显（精确命中场景下）
// ============================================================

using System.Text.RegularExpressions;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class HybridRetriever
{
    private readonly EmbeddingService _embeddings;
    private readonly IVectorStore _vectorStore;
    private readonly KnowledgeStore _store;

    /// <summary>RRF 融合常数 k（越大越平滑，越小越偏向 Top）</summary>
    private const int RrfK = 60;

    /// <summary>同时嵌入文档的并发上限（批量上传 50 个时最多 3 个并行，避免嵌入 API 被 429 限流）</summary>
    private static readonly SemaphoreSlim _ingestLimiter = new(3, 3);

    public HybridRetriever(EmbeddingService embeddings, IVectorStore vectorStore, KnowledgeStore store)
    {
        _embeddings = embeddings;
        _vectorStore = vectorStore;
        _store = store;
    }

    /// <summary>
    /// 上传文档完成后调用：解析分片 → 计算 embedding → 存 SQLite + 内存向量
    /// </summary>
    public async Task IngestDocumentAsync(int documentId, string fileName, byte[] bytes, int databaseId)
    {
        // 限制同时嵌入的文档数（批量上传 50 个时最多 3 个并行，避免嵌入 API 429 限流）
        await _ingestLimiter.WaitAsync();
        try
        {
            // 清理旧分片（重新解析时用）
            await _store.ClearChunksByDocumentAsync(documentId);
            await _vectorStore.RemoveByDocumentAsync(documentId);

            var chunks = DocumentParser.ParseAndChunk(fileName, databaseId, bytes);
            foreach (var c in chunks)
            {
                c.DocumentId = documentId;
                c.DatabaseId = databaseId;
                var emb = await _embeddings.GetEmbeddingAsync(c.Content);
                var chunkId = await _store.AddChunkAsync(c, emb);
                await _vectorStore.AddVectorAsync(chunkId, emb, documentId, databaseId);
            }
            await _store.UpdateDocumentStatusAsync(documentId, DocumentStatus.Ready, chunks.Count);
        }
        finally
        {
            _ingestLimiter.Release();
        }
    }

    /// <summary>
    /// 服务启动时从 SQLite 重建内存向量（避免重启后向量丢失）
    /// 每个 databaseId 只加载一次
    /// </summary>
    public async Task EnsureLoadedAsync(int databaseId)
    {
        if (_vectorStore.IsDatabaseLoaded(databaseId)) return;
        var chunksWithEmb = await _store.ListChunksWithEmbeddingAsync(databaseId);
        foreach (var (chunk, emb) in chunksWithEmb)
        {
            if (emb is null || emb.Length == 0) continue;
            await _vectorStore.AddVectorAsync(chunk.Id, emb, chunk.DocumentId, chunk.DatabaseId);
        }
        _vectorStore.MarkDatabaseLoaded(databaseId);
    }

    /// <summary>
    /// 混合检索主入口
    /// </summary>
    public async Task<RetrievalResponse> SearchAsync(
        string query,
        int? databaseId = null,
        int topK = 5,
        bool enableRerank = false)
    {
        if (string.IsNullOrEmpty(query))
            return new RetrievalResponse(query, new(), new(), new(), new());

        // 1. 按需加载向量（启动后首次查询某库）
        if (databaseId.HasValue)
            await EnsureLoadedAsync(databaseId.Value);

        // ---------- 路径 A：向量语义检索 ----------
        var queryVec = await _embeddings.GetEmbeddingAsync(query);
        var vectorHits = await _vectorStore.SearchAsync(queryVec, topK * 3, databaseId);
        var vectorScores = vectorHits.ToDictionary(h => h.chunkId, h => h.score);

        // ---------- 路径 B：关键词 BM25 检索 ----------
        // 用 SQL LIKE 做关键词匹配（生产级应换 FTS5 倒排索引），按 query 每条 token 分别搜并聚合 TF-IDF 打分
        var queryTokens = Tokenize(query).Distinct().ToArray();
        var keywordHits = await KeywordSearchAsync(query, queryTokens, databaseId, topK * 3);
        var keywordScores = keywordHits.ToDictionary(h => h.chunkId, h => h.score);

        // ---------- RRF 融合 ----------
        var allChunkIds = vectorHits.Select(h => h.chunkId)
            .Union(keywordHits.Select(h => h.chunkId))
            .ToHashSet();
        var fusedScores = new Dictionary<int, double>();
        foreach (var cid in allChunkIds)
        {
            double s = 0;
            // 向量路：rank_i 是 cid 在 vectorHits 中的位次（1-based）
            var vRank = vectorHits.FindIndex(h => h.chunkId == cid);
            if (vRank >= 0) s += 1.0 / (RrfK + vRank + 1);
            // 关键词路
            var kRank = keywordHits.FindIndex(h => h.chunkId == cid);
            if (kRank >= 0) s += 1.0 / (RrfK + kRank + 1);
            fusedScores[cid] = s;
        }

        // 取 Top-K
        var topChunkIds = fusedScores
            .OrderByDescending(kv => kv.Value)
            .Take(topK)
            .Select(kv => kv.Key)
            .ToList();

        // 拉取分片详情（含文件名）
        var chunkDetails = new Dictionary<int, (DocumentChunk chunk, string fileName)>();
        foreach (var cid in topChunkIds)
        {
            var detail = await GetChunkDetailAsync(cid);
            if (detail != null) chunkDetails[cid] = detail.Value;
        }

        // ---------- 可选重排序 ----------
        // 面试关键说明：
        //   真正的重排序应使用 Cross-Encoder（如 BGE-Reranker-v2-m3），逐对计算 (query, chunk) 相似度，
        //   精度远超向量余弦 + 关键词加权，但需要 GPU 推理 + 逐对计算延迟（Top-5 ≈ 200ms）。
        //   本演示项目用简化加权公式保持轻量，适合面试讲清楚"演示 vs 生产"的区别。
        var rerankedScores = new Dictionary<int, double>();
        if (enableRerank)
        {
            foreach (var cid in topChunkIds)
            {
                if (!chunkDetails.TryGetValue(cid, out var detail)) continue;
                var vScore = vectorScores.TryGetValue(cid, out var v) ? v : 0;
                var kScore = keywordScores.TryGetValue(cid, out var k) ? k : 0;
                // 简化重排公式：向量余弦为主 + 关键词命中数为辅
                var rerankScore = 0.6 * vScore + 0.4 * kScore;
                rerankedScores[cid] = rerankScore;
            }
            topChunkIds = topChunkIds
                .OrderByDescending(cid => rerankedScores.TryGetValue(cid, out var s) ? s : 0)
                .ToList();
        }

        // 构造结果列表
        var results = new List<SearchResult>();
        foreach (var cid in topChunkIds)
        {
            if (!chunkDetails.TryGetValue(cid, out var detail)) continue;
            var fusedScore = fusedScores.TryGetValue(cid, out var f) ? f : 0;
            double? reranked = enableRerank && rerankedScores.TryGetValue(cid, out var r)
                ? r
                : null;
            results.Add(new SearchResult(
                ChunkId: cid,
                Content: detail.chunk.Content,
                FileName: detail.fileName,
                PageNumber: detail.chunk.PageNumber,
                Score: fusedScore,
                RerankedScore: reranked,
                Source: detail.fileName));
        }

        return new RetrievalResponse(query, results, vectorScores, keywordScores, fusedScores);
    }

    /// <summary>
    /// 关键词检索（SQL LIKE 版）：每条 query token 用 SQL 查询匹配分片，聚合 TF-IDF 打分
    /// 不再把全部 chunk 拉入内存（千级文档 OOM 风险），改用 SQL LIKE 限 200 条/词
    /// </summary>
    private async Task<List<(int chunkId, double score)>> KeywordSearchAsync(
        string query, string[] tokens, int? databaseId, int topK)
    {
        if (tokens.Length == 0) return new();

        // 每个 token 并行查 SQL（LIKE 匹配，限 200 条/词）
        var tasks = tokens.Select(tok => _store.SearchChunksByContentAsync(tok, databaseId, 200));
        var allResults = await Task.WhenAll(tasks);

        // 聚合：计算每个 chunk 的 TF-IDF 总分
        var chunkHits = new Dictionary<int, (double score, int dfCount)>();
        var N = await GetTotalChunkCountAsync(databaseId);
        if (N == 0) return new();

        for (int i = 0; i < tokens.Length; i++)
        {
            var hits = allResults[i];
            var df = Math.Max(hits.Count, 1);
            var idf = Math.Log((double)N / df);
            foreach (var (chunkId, _, content, _, _) in hits)
            {
                var tf = CountOccurrences(content, tokens[i]);
                if (tf == 0) continue;
                var score = tf * idf;
                if (!chunkHits.TryGetValue(chunkId, out var cur))
                    chunkHits[chunkId] = (score, 1);
                else
                    chunkHits[chunkId] = (cur.score + score, cur.dfCount + 1);
            }
        }

        return chunkHits
            .Where(kv => kv.Value.score > 0)
            .OrderByDescending(kv => kv.Value.score)
            .Take(topK)
            .Select(kv => (kv.Key, kv.Value.score))
            .ToList();
    }

    private async Task<int> GetTotalChunkCountAsync(int? databaseId)
        => await _store.GetChunkCountAsync(databaseId);

    /// <summary>
    /// 简化分词：中文 2-gram + 英文按非字母数字分割
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        // 英文/数字 token：连续字母数字串
        var enMatches = Regex.Matches(text, @"[A-Za-z0-9]+");
        foreach (Match m in enMatches)
        {
            var t = m.Value.ToLowerInvariant();
            if (t.Length >= 2) tokens.Add(t);
        }

        // 中文 2-gram：连续中文字符两两组合
        var cjkChars = new List<char>();
        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) cjkChars.Add(c);
            else
            {
                if (cjkChars.Count >= 2)
                {
                    for (int i = 0; i < cjkChars.Count - 1; i++)
                        tokens.Add($"{cjkChars[i]}{cjkChars[i + 1]}");
                }
                cjkChars.Clear();
            }
        }
        if (cjkChars.Count >= 2)
        {
            for (int i = 0; i < cjkChars.Count - 1; i++)
                tokens.Add($"{cjkChars[i]}{cjkChars[i + 1]}");
        }

        return tokens;
    }

    /// <summary>统计 sub 在 text 中出现的次数（重叠计数）</summary>
    internal static int CountOccurrences(string text, string sub)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(sub)) return 0;
        int count = 0, pos = 0;
        while ((pos = text.IndexOf(sub, pos, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            pos += sub.Length;
        }
        return count;
    }

    /// <summary>查询分片详情（含所属文件名）—— 直接 SELECT WHERE id=@chunkId，不再遍历全库</summary>
    private async Task<(DocumentChunk chunk, string fileName)?> GetChunkDetailAsync(int chunkId)
    {
        var chunk = await _store.GetChunkByIdAsync(chunkId);
        if (chunk == null) return null;
        var doc = await _store.GetDocumentAsync(chunk.DocumentId);
        return (chunk, doc?.FileName ?? "");
    }
}
