// ============================================================
// RAG 知识库端点：CRUD / 上传 / 检索 / 评测 / 统计 / 记忆
// ============================================================

using Microsoft.Extensions.AI;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Endpoints;

public static class KbEndpoints
{
    public static WebApplication MapKbEndpoints(this WebApplication app)
    {
        // ---------- 知识库 CRUD ----------
        app.MapGet("/api/kb/databases", async (KnowledgeStore store) =>
            Results.Ok(await store.ListDatabasesAsync())).WithTags("RAG知识库");

        app.MapPost("/api/kb/databases", async (KbCreateRequest req, KnowledgeStore store) =>
        {
            var id = await store.CreateDatabaseAsync(req.Name, req.Description ?? "");
            var kb = await store.GetDatabaseAsync(id);
            return Results.Created($"/api/kb/databases/{id}", kb);
        }).WithTags("RAG知识库");

        app.MapDelete("/api/kb/databases/{id}", async (int id, KnowledgeStore store, IVectorStore vectors) =>
        {
            await vectors.RemoveByDatabaseAsync(id);
            var ok = await store.DeleteDatabaseAsync(id);
            return ok ? Results.Ok() : Results.NotFound();
        }).WithTags("RAG知识库");

        // ---------- 文档管理 ----------
        app.MapGet("/api/kb/databases/{id}/documents", async (int id, KnowledgeStore store) =>
            Results.Ok(await store.ListDocumentsAsync(id))).WithTags("RAG文档");

        app.MapDelete("/api/kb/documents/{id}", async (int id, KnowledgeStore store, IVectorStore vectors) =>
        {
            await vectors.RemoveByDocumentAsync(id);
            var ok = await store.DeleteDocumentAsync(id);
            return ok ? Results.Ok() : Results.NotFound();
        }).WithTags("RAG文档");

        // ---------- 上传 + 异步处理 ----------
        app.MapPost("/api/kb/databases/{id}/upload", async (
            int id, HttpRequest request, KnowledgeStore store, HybridRetriever retriever,
            RateLimiter rateLimiter, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("RAG.上传");
            if (!request.HasFormContentType) return Results.BadRequest("需要 multipart/form-data");

            var ip = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!rateLimiter.TryAcquire(ip))
                return Results.Json(new { error = true, message = "上传过于频繁（每分钟最多100次），请稍后再试" }, statusCode: 429);

            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest("未提供文件");

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var doc = new KnowledgeDocument
            {
                DatabaseId = id, FileName = file.FileName, FileSize = file.Length,
                FileType = ext, Status = DocumentStatus.Pending
            };
            var docId = await store.CreateDocumentAsync(doc);

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();

            await store.UpdateDocumentStatusAsync(docId, DocumentStatus.Processing, 0);

            _ = Task.Run(async () =>
            {
                try { await retriever.IngestDocumentAsync(docId, file.FileName, bytes, id); }
                catch (Exception ex)
                {
                    await store.UpdateDocumentStatusAsync(docId, DocumentStatus.Failed, 0, ex.Message);
                    logger.LogError(ex, "文档 {DocId}（{FileName}）处理失败", docId, file.FileName);
                }
            });

            return Results.Accepted($"/api/kb/documents/{docId}", new { id = docId, status = "processing", message = "文档已上传，正在后台解析与嵌入" });
        }).WithTags("RAG文档").DisableAntiforgery();

        // ---------- 重新解析 ----------
        app.MapPost("/api/kb/documents/{id}/reparse", async (int id, KnowledgeStore store, HybridRetriever retriever, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("RAG.重解析");
            var doc = await store.GetDocumentAsync(id);
            if (doc == null) return Results.NotFound();

            var chunks = await store.ListChunksByDocumentAsync(id);
            if (chunks.Count == 0) return Results.BadRequest("无原始分片可重新嵌入");

            await store.UpdateDocumentStatusAsync(id, DocumentStatus.Processing, chunks.Count);
            _ = Task.Run(async () =>
            {
                try
                {
                    await retriever.EnsureLoadedAsync(doc.DatabaseId);
                    await store.UpdateDocumentStatusAsync(id, DocumentStatus.Ready, chunks.Count);
                }
                catch (Exception ex)
                {
                    await store.UpdateDocumentStatusAsync(id, DocumentStatus.Failed, chunks.Count, ex.Message);
                    logger.LogError(ex, "文档 {DocId} 重新嵌入失败", id);
                }
            });
            return Results.Accepted($"/api/kb/documents/{id}", new { id, status = "reprocessing" });
        }).WithTags("RAG文档");

        // ---------- 检索测试 ----------
        app.MapPost("/api/kb/search", async (SearchTestRequest req, HybridRetriever retriever) =>
        {
            var resp = await retriever.SearchAsync(req.Query, req.DatabaseId, req.TopK, req.Rerank);
            return Results.Ok(resp);
        }).WithTags("RAG检索");

        // ---------- RAG 评测 ----------
        app.MapPost("/api/kb/eval", async (EvalTestRequest req, HybridRetriever retriever, IChatClient chatClient) =>
        {
            var details = new List<RAGCaseResult>();
            int recalled = 0, correct = 0;

            foreach (var tc in req.TestCases)
            {
                var searchResp = await retriever.SearchAsync(tc.Question, req.DatabaseId, topK: 3, enableRerank: true);
                bool isRetrieved = searchResp.Results.Count > 0;
                if (isRetrieved) recalled++;

                string actualAnswer;
                try
                {
                    var context = string.Join("\n---\n", searchResp.Results.Select(r => r.Content));
                    var prompt = $"基于以下检索内容回答问题。\n\n检索内容：\n{context}\n\n问题：{tc.Question}\n\n回答：";
                    var msgs = new List<Microsoft.Extensions.AI.ChatMessage>
                    {
                        new(Microsoft.Extensions.AI.ChatRole.User, prompt)
                    };
                    var llmResp = await chatClient.GetResponseAsync(msgs);
                    actualAnswer = llmResp.Text?.Trim() ?? "(无回答)";
                }
                catch { actualAnswer = "(LLM 调用失败)"; }

                bool isCorrect = !string.IsNullOrEmpty(tc.ExpectedKeyPoints)
                    && actualAnswer.Contains(tc.ExpectedKeyPoints, StringComparison.OrdinalIgnoreCase);
                if (isCorrect) correct++;

                details.Add(new RAGCaseResult(tc.Question, tc.ExpectedKeyPoints ?? "", actualAnswer, isRetrieved, isCorrect));
            }

            var total = req.TestCases.Count;
            return Results.Ok(new RAGResult(
                TotalCases: total,
                RecallRate: total > 0 ? (double)recalled / total : 0,
                AccuracyRate: total > 0 ? (double)correct / total : 0,
                Details: details));
        }).WithTags("RAG评测");

        // ---------- 知识库统计 ----------
        app.MapGet("/api/kb/stats", async (KnowledgeStore store, IVectorStore vectors, EmbeddingService emb) =>
        {
            var stats = await store.GetRagStatsAsync();
            return Results.Ok(new
            {
                tables = stats,
                vectorCount = vectors.Count,
                embeddingProvider = emb.Provider,
                embeddingFallback = emb.IsFallback,
                embeddingDimension = emb.Dimension
            });
        }).WithTags("RAG知识库");

        // ---------- 长期记忆 ----------
        app.MapGet("/api/kb/memory/{sessionId}", async (string sessionId, MemoryStore memory) =>
        {
            var messages = await memory.GetRecentMessagesAsync(sessionId, 100);
            var profiles = await memory.GetProfilesAsync(sessionId);
            var summary = await memory.GetSummaryAsync(sessionId);
            return Results.Ok(new { messages, profiles, summary });
        }).WithTags("RAG记忆");

        return app;
    }
}
