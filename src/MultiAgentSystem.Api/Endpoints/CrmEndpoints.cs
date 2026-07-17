// ============================================================
// CRM + 工单 + 人审 + 审计 + 仪表盘 端点
// ============================================================

using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Endpoints;

public static class CrmEndpoints
{
    public static WebApplication MapCrmEndpoints(this WebApplication app)
    {
        // ---------- 客户 CRUD ----------
        app.MapGet("/api/crm/customers", async (HttpContext ctx, BusinessStore store, string? keyword) =>
        {
            var (user, role) = EndpointHelpers.GetCurrentUser(ctx);
            var owner = role == "Admin" ? null : user;
            return Results.Ok(await store.ListCustomersAsync(owner, keyword));
        }).WithTags("CRM客户");

        app.MapGet("/api/crm/customers/{id}", async (int id, BusinessStore store) =>
        {
            var c = await store.GetCustomerAsync(id);
            return c == null ? Results.NotFound() : Results.Ok(c);
        }).WithTags("CRM客户");

        app.MapPost("/api/crm/customers", async (HttpContext ctx, Customer c, BusinessStore store) =>
        {
            var (user, _) = EndpointHelpers.GetCurrentUser(ctx);
            if (string.IsNullOrEmpty(c.Owner)) c.Owner = user;
            var id = await store.CreateCustomerAsync(c);
            return Results.Created($"/api/crm/customers/{id}", new { id });
        }).WithTags("CRM客户");

        app.MapPut("/api/crm/customers/{id}", async (int id, Customer c, BusinessStore store) =>
        {
            c.Id = id;
            var ok = await store.UpdateCustomerAsync(c);
            return ok ? Results.Ok() : Results.NotFound();
        }).WithTags("CRM客户");

        app.MapDelete("/api/crm/customers/{id}", async (int id, HttpContext ctx, BusinessStore store, JwtService jwt) =>
        {
            var (user, role) = jwt.ParseUser(ctx.Request.Headers.Authorization);
            if (role != "Admin") return Results.Forbid();
            var ok = await store.DeleteCustomerAsync(id, user);
            return ok ? Results.Ok() : Results.NotFound();
        }).WithTags("CRM客户");

        // ---------- 跟进记录 ----------
        app.MapGet("/api/crm/customers/{id}/followups", async (int id, BusinessStore store) =>
            Results.Ok(await store.ListFollowUpsAsync(id))).WithTags("CRM跟进");

        app.MapPost("/api/crm/customers/{id}/followups", async (int id, HttpContext ctx, FollowUp f, BusinessStore store) =>
        {
            var (user, _) = EndpointHelpers.GetCurrentUser(ctx);
            f.CustomerId = id;
            if (string.IsNullOrEmpty(f.Operator)) f.Operator = user;
            var fid = await store.AddFollowUpAsync(f);
            return Results.Created($"/api/crm/followups/{fid}", new { id = fid });
        }).WithTags("CRM跟进");

        // ---------- 工单 ----------
        app.MapGet("/api/tickets", async (TicketStatus? status, BusinessStore store) =>
            Results.Ok(await store.ListTicketsAsync(status))).WithTags("工单");

        app.MapPost("/api/tickets", async (HttpContext ctx, Ticket t, BusinessStore store) =>
        {
            var (user, _) = EndpointHelpers.GetCurrentUser(ctx);
            t.CreatedBy = user;
            var id = await store.CreateTicketAsync(t);
            return Results.Created($"/api/tickets/{id}", new { id });
        }).WithTags("工单");

        app.MapPut("/api/tickets/{id}/status", async (int id, TicketStatus status, HttpContext ctx, BusinessStore store, JwtService jwt) =>
        {
            var (user, role) = jwt.ParseUser(ctx.Request.Headers.Authorization);
            if (role != "Admin") return Results.Forbid();
            var ok = await store.UpdateTicketStatusAsync(id, status, user);
            return ok ? Results.Ok() : Results.NotFound();
        }).WithTags("工单");

        // ---------- 人审 ----------
        app.MapGet("/api/approvals", async (ApprovalStatus? status, BusinessStore store) =>
            Results.Ok(await store.ListApprovalsAsync(status))).WithTags("人审");

        app.MapGet("/api/approvals/{id}", async (int id, BusinessStore store) =>
        {
            var a = await store.GetApprovalAsync(id);
            return a == null ? Results.NotFound() : Results.Ok(a);
        }).WithTags("人审");

        app.MapPost("/api/approvals/decide", async (ApprovalDecisionRequest req, HttpContext ctx, ApprovalCoordinator coord, JwtService jwt) =>
        {
            var (user, role) = jwt.ParseUser(ctx.Request.Headers.Authorization);
            if (role != "Admin") return Results.Forbid();
            var status = req.Decision.ToLowerInvariant() switch
            {
                "approved" => ApprovalStatus.Approved,
                "rejected" => ApprovalStatus.Rejected,
                "modified" => ApprovalStatus.Modified,
                _ => ApprovalStatus.Approved
            };
            var comment = req.Comment;
            if (status == ApprovalStatus.Modified && !string.IsNullOrEmpty(req.ModifiedParameters))
                comment = $"修改后参数：{req.ModifiedParameters}";

            var ok = await coord.ResolveAsync(req.ApprovalId, status, user, comment, req.ModifiedParameters);
            return ok ? Results.Ok(new { resolved = true }) : Results.NotFound();
        }).WithTags("人审");

        // ---------- 审计（Admin only） ----------
        app.MapGet("/api/audit", async (int? limit, HttpContext ctx, BusinessStore store, JwtService jwt) =>
        {
            var (_, role) = jwt.ParseUser(ctx.Request.Headers.Authorization);
            if (role != "Admin") return Results.Forbid();
            return Results.Ok(await store.ListAuditLogsAsync(limit ?? 100));
        }).WithTags("审计");

        // ---------- 仪表盘 ----------
        app.MapGet("/api/dashboard", async (BusinessStore store) =>
            Results.Ok(await store.GetStatsAsync())).WithTags("仪表盘");

        // ========== 集成 Demo：跟进邮件 ==========
        app.MapPost("/api/crm/followups/{id}/generate-email", async (int id, EmailService emailService) =>
        {
            var draft = await emailService.GenerateFollowUpEmailAsync(id);
            if (draft == null) return Results.NotFound(new { error = "跟进记录或客户不存在，或LLM生成失败" });
            return Results.Ok(draft);
        }).WithTags("集成Demo");

        app.MapPost("/api/crm/followups/{id}/send-email", async (int id, EmailService emailService) =>
        {
            var draft = await emailService.GenerateFollowUpEmailAsync(id);
            if (draft == null) return Results.NotFound(new { error = "无法生成邮件" });
            var sent = await emailService.SendEmailAsync(draft);
            return Results.Ok(new { sent, to = draft.CustomerEmail, subject = draft.Subject });
        }).WithTags("集成Demo");

        return app;
    }
}
