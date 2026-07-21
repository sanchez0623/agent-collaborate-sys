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
                // Output 字段已包含结构化进度 JSON（type/done/total/percent/currentCase/currentMode）
                var json = evt.Output ?? JsonSerializer.Serialize(new { type = "progress", message = evt.Status });
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

        // 取消正在执行的评测任务
        app.MapPost("/api/eval/cancel/{taskId}", async (string taskId, EvalService evalService) =>
        {
            var ok = await evalService.CancelTaskAsync(taskId);
            return ok ? Results.Ok(new { cancelled = true }) : Results.NotFound(new { error = "任务不存在或已结束" });
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

        // 删除报告（含用例结果）
        app.MapDelete("/api/eval/reports/{taskId}", async (string taskId, EvalService evalService) =>
        {
            var ok = await evalService.DeleteReportAsync(taskId);
            return ok ? Results.Ok(new { deleted = true }) : Results.NotFound();
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

        // ===== 概览 =====
        sb.AppendLine("## 概览");
        sb.AppendLine();
        sb.AppendLine($"| 项目 | 值 |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| 任务 ID | {report.TaskId} |");
        sb.AppendLine($"| 状态 | {report.Status} |");
        sb.AppendLine($"| 编排模式 | {string.Join(", ", report.Modes)} |");
        sb.AppendLine($"| 用例数 | {report.TotalCases}（成功 {report.SuccessCases} / 失败 {report.FailedCases}） |");
        sb.AppendLine($"| 综合得分 | **{report.OverallScore:F2} / 10**（加权平均） |");
        sb.AppendLine($"| 平均响应 | {report.AvgResponseMs}ms |");
        sb.AppendLine($"| 总 Token | {report.TotalTokens:N0} |");
        sb.AppendLine($"| 创建时间 | {report.CreatedAt:yyyy-MM-dd HH:mm} |");
        sb.AppendLine();

        // ===== 6 维度评分 =====
        var successResults = report.CaseResults.Where(r => r.Success).ToList();
        var dimAvgs = new Dictionary<EvalDimension, double>();
        foreach (var dim in Enum.GetValues<EvalDimension>())
        {
            var scores = successResults.SelectMany(r => r.Dimensions).Where(d => d.Dimension == dim).ToList();
            dimAvgs[dim] = scores.Count > 0 ? Math.Round(scores.Average(d => d.Score), 1) : 0;
        }
        var strongest = dimAvgs.OrderByDescending(kv => kv.Value).FirstOrDefault();
        var weakest = dimAvgs.OrderBy(kv => kv.Value).FirstOrDefault();
        var weights = new EvalWeights();

        sb.AppendLine("## 维度评分");
        sb.AppendLine();
        sb.AppendLine($"最强维度：**{strongest.Key}**（{strongest.Value} 分） · 最弱维度：**{weakest.Key}**（{weakest.Value} 分）");
        sb.AppendLine();
        sb.AppendLine("| 维度 | 平均分 | 权重 | 加权分 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var dim in Enum.GetValues<EvalDimension>())
        {
            var score = dimAvgs[dim];
            var w = weights.GetWeight(dim);
            var marker = dim == strongest.Key ? " ↑" : dim == weakest.Key ? " ↓" : "";
            sb.AppendLine($"| {dim}{marker} | {score:F1} | {w} | {(score * w):F1} |");
        }
        sb.AppendLine();

        // ===== A/B 对比 =====
        if (report.Comparison != null && !string.IsNullOrWhiteSpace(report.Comparison.Summary))
        {
            sb.AppendLine("## A/B 模式对比");
            sb.AppendLine();
            sb.AppendLine(report.Comparison.Summary);
            sb.AppendLine();
            if (report.Comparison.ModeComparisons.Count > 0)
            {
                sb.AppendLine("| 模式 | 综合分 | 工具准确率 | 平均延迟 | Token | 成功率 |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var m in report.Comparison.ModeComparisons)
                    sb.AppendLine($"| {m.Mode} | {m.WeightedTotal:F2} | {m.AvgToolAccuracy:P0} | {m.AvgResponseMs}ms | {m.TotalTokens} | {m.SuccessCount}/{m.CasesRun} |");
                sb.AppendLine();
            }
        }

        // ===== RAG 对比 =====
        if (report.Comparison?.RagComparisons?.Count > 0)
        {
            sb.AppendLine("## RAG 开关对比");
            sb.AppendLine();
            sb.AppendLine("| 模式 | RAG | 综合分 | 准确性 | 低幻觉 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var r in report.Comparison.RagComparisons)
                sb.AppendLine($"| {r.Mode} | {(r.RagEnabled ? "开" : "关")} | {r.WeightedTotal:F2} | {r.AvgAccuracy:F1} | {r.AvgHallucination:F1} |");
            sb.AppendLine();
        }

        // ===== 失败用例 =====
        var failedCases = report.CaseResults.Where(r => !r.Success).ToList();
        if (failedCases.Count > 0)
        {
            sb.AppendLine("## 失败用例");
            sb.AppendLine();
            foreach (var f in failedCases)
            {
                sb.AppendLine($"- **#{f.TestCaseId}** [{f.Mode}]: {f.ErrorMessage ?? "未知错误"}");
            }
            sb.AppendLine();
        }

        // ===== 改进建议 =====
        if (report.Comparison != null && !string.IsNullOrWhiteSpace(report.Comparison.Improvement))
        {
            sb.AppendLine("## 改进建议");
            sb.AppendLine();
            sb.AppendLine(report.Comparison.Improvement);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
