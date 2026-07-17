// ============================================================
// EvalEndpoints - MVP-5 评测体系 API
// ============================================================

using System.Text.Json;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Services;

namespace MultiAgentSystem.Api.Endpoints;

public static class EvalEndpoints
{
    public static void MapEvalEndpoints(this WebApplication app)
    {
        // ========== 评测任务控制 ==========

        // 启动评测
        app.MapPost("/api/eval/run", async (EvalRunRequest req, EvalService evalService) =>
        {
            if (req.Modes.Count == 0)
                return Results.BadRequest(new { error = "至少选择一个编排模式" });
            var taskId = await evalService.RunAsync(req);
            return Results.Ok(new { taskId });
        }).WithTags("评测");

        // SSE 实时进度
        app.MapGet("/api/eval/progress/{taskId}", async (string taskId, HttpContext ctx, EvalService evalService) =>
        {
            var reader = evalService.GetProgressChannel(taskId);
            if (reader == null) return Results.NotFound(new { error = "任务不存在" });

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            await foreach (var evt in reader.ReadAllAsync(ctx.RequestAborted))
            {
                var json = JsonSerializer.Serialize(new { type = "progress", agent = evt.Agent, message = evt.Output, percent = evt.Round });
                await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            await ctx.Response.WriteAsync("data: {\"type\":\"done\"}\n\n", ctx.RequestAborted);
            return Results.Empty;
        }).WithTags("评测");

        // 任务状态
        app.MapGet("/api/eval/tasks/{taskId}", async (string taskId, EvalService evalService) =>
        {
            var task = await evalService.GetTaskAsync(taskId);
            return task != null ? Results.Ok(task) : Results.NotFound();
        }).WithTags("评测");

        // ========== 报告查询 ==========

        // 历史报告列表
        app.MapGet("/api/eval/reports", async (int? limit, EvalService evalService) =>
            Results.Ok(await evalService.ListReportsAsync(limit ?? 20))).WithTags("评测");

        // 报告详情
        app.MapGet("/api/eval/reports/{taskId}", async (string taskId, EvalService evalService) =>
        {
            var report = await evalService.GetReportAsync(taskId);
            return report != null ? Results.Ok(report) : Results.NotFound();
        }).WithTags("评测");

        // 导出报告（Markdown）
        app.MapGet("/api/eval/reports/{taskId}/export", async (string taskId, string? format, EvalService evalService) =>
        {
            var report = await evalService.GetReportAsync(taskId);
            if (report == null) return Results.NotFound();

            var fmt = format ?? "markdown";
            var content = fmt == "json"
                ? JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
                : BuildMarkdownReport(report);

            var contentType = fmt == "json" ? "application/json" : "text/markdown; charset=utf-8";
            return Results.File(System.Text.Encoding.UTF8.GetBytes(content), contentType,
                $"eval-report-{taskId}.{fmt}");
        }).WithTags("评测");

        // ========== 用例管理 ==========

        // 用例列表
        app.MapGet("/api/eval/testcases", async (string? category, TestCaseStore store) =>
            Results.Ok(await store.GetAllAsync(category))).WithTags("评测");

        // 用例分组
        app.MapGet("/api/eval/testcases/sets", async (TestCaseStore store) =>
            Results.Ok(await store.GetCaseSetsAsync())).WithTags("评测");

        // 添加自定义用例
        app.MapPost("/api/eval/testcases", async (EvalTestCase tc, TestCaseStore store) =>
        {
            var id = await store.AddAsync(tc);
            return Results.Ok(new { id });
        }).WithTags("评测");

        // 删除自定义用例
        app.MapDelete("/api/eval/testcases/{id}", async (int id, TestCaseStore store) =>
        {
            var ok = await store.DeleteAsync(id);
            return ok ? Results.Ok(new { deleted = true }) : Results.NotFound();
        }).WithTags("评测");
    }

    private static string BuildMarkdownReport(EvalReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# 评测报告 — {report.CaseSet}");
        sb.AppendLine();
        sb.AppendLine($"- **任务 ID**: {report.TaskId}");
        sb.AppendLine($"- **状态**: {report.Status}");
        sb.AppendLine($"- **用例数**: {report.TotalCases} (成功 {report.SuccessCases} / 失败 {report.FailedCases})");
        sb.AppendLine($"- **总体得分**: {report.OverallScore:F1}");
        sb.AppendLine($"- **平均响应**: {report.AvgResponseMs}ms");
        sb.AppendLine($"- **总 Token**: {report.TotalTokens}");
        sb.AppendLine();

        if (report.Comparison != null)
        {
            sb.AppendLine("## A/B 对比");
            sb.AppendLine(report.Comparison.Summary);
            sb.AppendLine();
        }

        sb.AppendLine("## 各维度汇总");
        sb.AppendLine("| 维度 | 平均分 |");
        sb.AppendLine("|---|---|");
        foreach (var dim in Enum.GetValues<EvalDimension>())
        {
            var avg = report.CaseResults.SelectMany(r => r.Dimensions)
                .Where(d => d.Dimension == dim).ToList();
            sb.AppendLine($"| {dim} | {(avg.Count > 0 ? avg.Average(d => d.Score).ToString("F1") : "N/A")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 用例详情");
        foreach (var r in report.CaseResults.Take(10))
        {
            sb.AppendLine($"### #{r.TestCaseId} ({r.Mode})");
            sb.AppendLine($"- 成功: {r.Success}");
            if (!string.IsNullOrWhiteSpace(r.ErrorMessage)) sb.AppendLine($"- 错误: {r.ErrorMessage}");
            sb.AppendLine($"- 响应时间: {r.ResponseTimeMs}ms");
            sb.AppendLine($"- Token: {r.TotalTokens}");
            sb.AppendLine($"- 工具准确率: {r.ToolCallAccuracy:P1}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
