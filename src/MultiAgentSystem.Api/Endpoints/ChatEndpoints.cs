// ============================================================
// 聊天端点：SSE 流式对话 + 历史记录
// ============================================================

using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;
using MultiAgentSystem.Api.Strategies;

namespace MultiAgentSystem.Api.Endpoints;

public static class ChatEndpoints
{
    public static WebApplication MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/chat", async (
            ChatRequest req,
            ConversationStore store, ApprovalCoordinator approvals,
            HttpContext context) =>
        {
            var sessionId = req.SessionId ?? Guid.NewGuid().ToString("N");
            await store.EnsureConversationAsync(sessionId);

            var history = await store.GetHistoryAsync(sessionId);
            await store.AddMessageAsync(sessionId, "user", req.Message);

            var strategy = EndpointHelpers.ResolveStrategy(req.OrchestrationMode,
                new()
                {
                    ["concurrent"] = context.RequestServices.GetRequiredService<ConcurrentStrategy>(),
                    ["handoff"] = context.RequestServices.GetRequiredService<HandoffStrategy>(),
                    ["groupchat"] = context.RequestServices.GetRequiredService<GroupChatStrategy>(),
                    ["magentic"] = context.RequestServices.GetRequiredService<MagenticStrategy>(),
                    ["crm"] = context.RequestServices.GetRequiredService<CrmStrategy>(),
                    ["rag"] = context.RequestServices.GetRequiredService<RagStrategy>()
                }, context.RequestServices.GetRequiredService<SequentialStrategy>());

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var ct = context.RequestAborted;

            async Task SendEventAsync(object payload)
            {
                var json = JsonSerializer.Serialize(payload);
                await context.Response.WriteAsync($"data: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }

            await SendEventAsync(new { type = "session", sessionId });
            await SendEventAsync(new { type = "orchestration_start", mode = strategy.Mode.ToString().ToLowerInvariant() });

            var channel = Channel.CreateUnbounded<OrchestrationEvent>();
            Action<OrchestrationEvent> onEvent = e => channel.Writer.TryWrite(e);

            approvals.RegisterSession(sessionId, async (ar) =>
            {
                onEvent(new OrchestrationEvent(OrchestrationEventType.ApprovalRequired,
                    Agent: ar.Agent, ApprovalId: ar.Id, ApprovalAction: ar.Action,
                    ApprovalParams: ar.Parameters, Reason: ar.RiskLevel));
            });

            var runTask = Task.Run(async () =>
            {
                ApprovalCoordinator.CurrentSessionId = sessionId;
                try { return await strategy.ExecuteAsync(req.Message, history, onEvent, ct); }
                finally { channel.Writer.Complete(); }
            }, ct);

            try
            {
                await foreach (var e in channel.Reader.ReadAllAsync(ct))
                {
                    await SendEventAsync(new
                    {
                        type = "orchestration_event",
                        eventType = EndpointHelpers.ToSnakeCase(e.Type.ToString()),
                        agent = e.Agent, status = e.Status, output = e.Output,
                        round = e.Round, fromAgent = e.FromAgent, toAgent = e.ToAgent,
                        reason = e.Reason, parallelLane = e.ParallelLane,
                        toolName = e.ToolName, toolArgs = e.ToolArgs, toolResult = e.ToolResult,
                        approvalId = e.ApprovalId, approvalAction = e.ApprovalAction,
                        approvalParams = e.ApprovalParams, approvalDecision = e.ApprovalDecision
                    });
                }
            }
            catch (OperationCanceledException)
            {
                approvals.CancelPendingForSession(sessionId);
                approvals.UnregisterSession(sessionId);
                return;
            }

            string finalContent;
            try { finalContent = await runTask; }
            catch (OperationCanceledException)
            {
                approvals.CancelPendingForSession(sessionId);
                approvals.UnregisterSession(sessionId);
                return;
            }
            catch (Exception ex)
            {
                await SendEventAsync(new { type = "error", message = $"编排执行失败：{ex.Message}" });
                return;
            }

            try
            {
                for (int i = 0; i < finalContent.Length; i += 2)
                {
                    var len = Math.Min(2, finalContent.Length - i);
                    await SendEventAsync(new { type = "token", content = finalContent.Substring(i, len) });
                }
                await SendEventAsync(new { type = "done", content = finalContent });
                await store.AddMessageAsync(sessionId, "assistant", finalContent);
            }
            catch (OperationCanceledException) { }

            approvals.UnregisterSession(sessionId);
        }).WithTags("聊天");

        app.MapGet("/api/chat/sessions/{sessionId}/history", async (string sessionId, ConversationStore store) =>
            Results.Ok(await store.GetHistoryAsync(sessionId)))
            .WithTags("聊天");

        return app;
    }
}
