// ============================================================
// 系统端点：健康检查、维护
// ============================================================

using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Endpoints;

public static class SystemEndpoints
{
    public static WebApplication MapSystemEndpoints(this WebApplication app, bool tavilyActive)
    {
        app.MapGet("/api/health", () => new
        {
            Status = "ok",
            Time = DateTime.Now,
            TavilyEnabled = tavilyActive,
            Agents = new[] { "Researcher", "Writer", "Critic", "Analyst", "Coder", "Consultant", "Support", "Coordinator", "CrmAgent", "Approver", "KnowledgeAgent" },
            Modes = new[] { "sequential", "concurrent", "handoff", "groupchat", "magentic", "crm", "rag" }
        }).WithTags("系统");

        app.MapPost("/api/system/maintenance", async (KnowledgeStore kStore) =>
        {
            var count = await kStore.GetChunkCountAsync(null);
            return Results.Ok(new { TotalChunks = count, Message = $"当前共 {count} 个分片" });
        }).WithTags("系统");

        return app;
    }
}
