// ============================================================
// EvalReportStore - 评测报告仓储（EF Core + IDbContextFactory）
// IDbContextFactory 直接创建独立 context 实例，无需手动 CreateScope，无泄漏
// ============================================================

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MultiAgentSystem.Api.Data;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class EvalReportStore
{
    private readonly IDbContextFactory<MultiAgentDbContext> _contextFactory;

    public EvalReportStore(IDbContextFactory<MultiAgentDbContext> contextFactory)
        => _contextFactory = contextFactory;

    private MultiAgentDbContext Db() => _contextFactory.CreateDbContext();

    public async Task SaveTaskAsync(EvalTask task)
    {
        using var db = Db();
        db.EvalReports.Add(new EvalReportEntity {
            TaskId = task.Id, CaseSet = task.CaseSet, ModesJson = JsonSerializer.Serialize(task.Modes),
            Status = task.Status, TotalCases = task.TotalCases, SuccessCases = task.CompletedCases,
            FailedCases = task.FailedCases, CreatedAt = task.CreatedAt });
        await db.SaveChangesAsync();
    }

    public async Task UpdateTaskProgressAsync(string taskId, int completed, int failed, string status = "running")
    {
        using var db = Db();
        var e = await db.EvalReports.FindAsync(taskId);
        if (e != null) { e.SuccessCases = completed; e.FailedCases = failed; e.Status = status; await db.SaveChangesAsync(); }
    }

    /// <summary>启动清理：将所有残留 running 状态的任务标记为 interrupted（进程重启后它们不可能还活着）</summary>
    public async Task<int> MarkStaleTasksInterruptedAsync()
    {
        using var db = Db();
        var stale = await db.EvalReports.Where(x => x.Status == "running").ToListAsync();
        foreach (var e in stale) e.Status = "interrupted";
        await db.SaveChangesAsync();
        return stale.Count;
    }

    /// <summary>删除评测报告及其用例结果</summary>
    public async Task<bool> DeleteReportAsync(string taskId)
    {
        using var db = Db();
        var report = await db.EvalReports.FindAsync(taskId);
        if (report == null) return false;
        var caseResults = await db.EvalCaseResults.Where(x => x.TaskId == taskId).ToListAsync();
        db.EvalCaseResults.RemoveRange(caseResults);
        db.EvalReports.Remove(report);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task FinalizeTaskAsync(EvalReport report)
    {
        using var db = Db();
        var e = await db.EvalReports.FindAsync(report.TaskId);
        if (e != null)
        {
            e.Status = "completed"; e.TotalCases = report.TotalCases;
            e.SuccessCases = report.SuccessCases; e.FailedCases = report.FailedCases;
            e.OverallScore = report.OverallScore; e.AvgResponseMs = (long)report.AvgResponseMs;
            e.TotalTokens = report.TotalTokens;
            e.ComparisonJson = report.Comparison != null ? JsonSerializer.Serialize(report.Comparison) : null;
            e.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task<EvalTask?> GetTaskAsync(string taskId)
    {
        using var db = Db();
        var e = await db.EvalReports.FindAsync(taskId);
        if (e == null) return null;
        var modes = new List<string>();
        try { modes = JsonSerializer.Deserialize<List<string>>(e.ModesJson) ?? new(); } catch { }
        return new EvalTask { Id = e.TaskId, CaseSet = e.CaseSet, Modes = modes, Status = e.Status, TotalCases = e.TotalCases, CompletedCases = e.SuccessCases, FailedCases = e.FailedCases, CreatedAt = e.CreatedAt, CompletedAt = e.CompletedAt };
    }

    public async Task<List<EvalReport>> ListReportsAsync(int limit = 20)
    {
        using var db = Db();
        var entities = await db.EvalReports.OrderByDescending(x => x.CreatedAt).Take(limit).ToListAsync();
        return entities.Select(e => {
            var modes = new List<string>();
            try { modes = JsonSerializer.Deserialize<List<string>>(e.ModesJson) ?? new(); } catch { }
            return new EvalReport { TaskId = e.TaskId, CaseSet = e.CaseSet, Modes = modes, Status = e.Status, TotalCases = e.TotalCases, SuccessCases = e.SuccessCases, FailedCases = e.FailedCases, OverallScore = e.OverallScore, AvgResponseMs = e.AvgResponseMs, TotalTokens = e.TotalTokens, CreatedAt = e.CreatedAt, CompletedAt = e.CompletedAt };
        }).ToList();
    }

    public async Task SaveCaseResultAsync(EvalCaseResult r)
    {
        using var db = Db();
        db.EvalCaseResults.Add(new EvalCaseResultEntity {
            TaskId = r.TaskId, TestCaseId = r.TestCaseId, Mode = r.Mode, RagEnabled = r.RagEnabled,
            Success = r.Success, ErrorMessage = r.ErrorMessage, DimensionsJson = JsonSerializer.Serialize(r.Dimensions),
            ResponseTimeMs = r.ResponseTimeMs, TotalTokens = r.TotalTokens, InputTokens = r.InputTokens,
            OutputTokens = r.OutputTokens, ToolAccuracy = r.ToolCallAccuracy, ExpectedToolCount = r.ExpectedToolCount,
            ActualToolCount = r.ActualToolCount, ConversationLog = r.ConversationLog, AgentOutputs = r.AgentOutputs,
            ToolCallLog = r.ToolCallLog, JudgeRawOutput = r.JudgeRawOutput });
        await db.SaveChangesAsync();
    }

    public async Task<List<EvalCaseResult>> GetCaseResultsAsync(string taskId)
    {
        using var db = Db();
        var entities = await db.EvalCaseResults.Where(x => x.TaskId == taskId).OrderBy(x => x.Id).ToListAsync();
        return entities.Select(e => {
            var dims = new List<DimensionScore>();
            try { dims = JsonSerializer.Deserialize<List<DimensionScore>>(e.DimensionsJson) ?? new(); } catch { }
            return new EvalCaseResult { Id = e.Id, TaskId = e.TaskId, TestCaseId = e.TestCaseId, Mode = e.Mode, RagEnabled = e.RagEnabled, Success = e.Success, ErrorMessage = e.ErrorMessage, Dimensions = dims, ResponseTimeMs = e.ResponseTimeMs, TotalTokens = e.TotalTokens, InputTokens = e.InputTokens, OutputTokens = e.OutputTokens, ToolCallAccuracy = e.ToolAccuracy, ExpectedToolCount = e.ExpectedToolCount, ActualToolCount = e.ActualToolCount, ConversationLog = e.ConversationLog, AgentOutputs = e.AgentOutputs, ToolCallLog = e.ToolCallLog, JudgeRawOutput = e.JudgeRawOutput, ExecutedAt = e.ExecutedAt };
        }).ToList();
    }

    public async Task<EvalReport?> GetReportAsync(string taskId)
    {
        var task = await GetTaskAsync(taskId);
        if (task == null) return null;
        var caseResults = await GetCaseResultsAsync(taskId);
        double avgMs = caseResults.Count > 0 ? caseResults.Average(r => r.ResponseTimeMs) : 0;
        // 统一口径：仅对成功用例取 WeightedAverage（0-10 加权平均）
        var successResults = caseResults.Where(r => r.Success).ToList();
        double overall = successResults.Count > 0
            ? Math.Round(successResults.Average(r => r.WeightedAverage), 2) : 0;
        ABComparison? comparison = null;
        using (var db = Db()) {
            var entity = await db.EvalReports.FindAsync(taskId);
            if (entity?.ComparisonJson != null)
                try { comparison = JsonSerializer.Deserialize<ABComparison>(entity.ComparisonJson); } catch { }
        }
        return new EvalReport { TaskId = task.Id, CaseSet = task.CaseSet, Modes = task.Modes, Status = task.Status, TotalCases = task.TotalCases, SuccessCases = task.CompletedCases, FailedCases = task.FailedCases, OverallScore = overall, AvgResponseMs = avgMs, TotalTokens = caseResults.Sum(r => r.TotalTokens), CaseResults = caseResults, Comparison = comparison, CreatedAt = task.CreatedAt, CompletedAt = task.CompletedAt };
    }
}
