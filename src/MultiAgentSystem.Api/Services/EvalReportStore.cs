// ============================================================
// EvalReportStore - 评测报告仓储（EF Core）
// ============================================================

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MultiAgentSystem.Api.Data;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class EvalReportStore
{
    private readonly MultiAgentDbContext _db;

    public EvalReportStore(MultiAgentDbContext db)
    {
        _db = db;
        _db.Database.EnsureCreated();
    }

    public async Task SaveTaskAsync(EvalTask task)
    {
        var entity = new EvalReportEntity
        {
            TaskId = task.Id, CaseSet = task.CaseSet,
            ModesJson = JsonSerializer.Serialize(task.Modes),
            Status = task.Status, TotalCases = task.TotalCases,
            SuccessCases = task.CompletedCases, FailedCases = task.FailedCases,
            CreatedAt = task.CreatedAt
        };
        _db.EvalReports.Add(entity);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateTaskProgressAsync(string taskId, int completed, int failed, string status = "running")
    {
        var entity = await _db.EvalReports.FindAsync(taskId);
        if (entity != null)
        {
            entity.SuccessCases = completed;
            entity.FailedCases = failed;
            entity.Status = status;
            await _db.SaveChangesAsync();
        }
    }

    public async Task FinalizeTaskAsync(EvalReport report)
    {
        var entity = await _db.EvalReports.FindAsync(report.TaskId);
        if (entity != null)
        {
            entity.Status = "completed";
            entity.SuccessCases = report.SuccessCases;
            entity.FailedCases = report.FailedCases;
            entity.OverallScore = report.OverallScore;
            entity.AvgResponseMs = (long)report.AvgResponseMs;
            entity.TotalTokens = report.TotalTokens;
            entity.ComparisonJson = report.Comparison != null ? JsonSerializer.Serialize(report.Comparison) : null;
            entity.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task<EvalTask?> GetTaskAsync(string taskId)
    {
        var e = await _db.EvalReports.FindAsync(taskId);
        if (e == null) return null;
        var modes = new List<string>();
        try { modes = JsonSerializer.Deserialize<List<string>>(e.ModesJson) ?? new(); } catch { }
        return new EvalTask
        {
            Id = e.TaskId, CaseSet = e.CaseSet, Modes = modes,
            Status = e.Status, TotalCases = e.TotalCases,
            CompletedCases = e.SuccessCases, FailedCases = e.FailedCases,
            CreatedAt = e.CreatedAt, CompletedAt = e.CompletedAt
        };
    }

    public async Task<List<EvalReport>> ListReportsAsync(int limit = 20)
    {
        var entities = await _db.EvalReports.OrderByDescending(x => x.CreatedAt).Take(limit).ToListAsync();
        return entities.Select(e =>
        {
            var modes = new List<string>();
            try { modes = JsonSerializer.Deserialize<List<string>>(e.ModesJson) ?? new(); } catch { }
            return new EvalReport
            {
                TaskId = e.TaskId, CaseSet = e.CaseSet, Modes = modes,
                Status = e.Status, TotalCases = e.TotalCases,
                SuccessCases = e.SuccessCases, FailedCases = e.FailedCases,
                OverallScore = e.OverallScore, AvgResponseMs = e.AvgResponseMs,
                TotalTokens = e.TotalTokens, CreatedAt = e.CreatedAt, CompletedAt = e.CompletedAt
            };
        }).ToList();
    }

    public async Task SaveCaseResultAsync(EvalCaseResult result)
    {
        _db.EvalCaseResults.Add(new EvalCaseResultEntity
        {
            TaskId = result.TaskId, TestCaseId = result.TestCaseId,
            Mode = result.Mode, RagEnabled = result.RagEnabled,
            Success = result.Success, ErrorMessage = result.ErrorMessage,
            DimensionsJson = JsonSerializer.Serialize(result.Dimensions),
            ResponseTimeMs = result.ResponseTimeMs, TotalTokens = result.TotalTokens,
            InputTokens = result.InputTokens, OutputTokens = result.OutputTokens,
            ToolAccuracy = result.ToolCallAccuracy,
            ExpectedToolCount = result.ExpectedToolCount,
            ActualToolCount = result.ActualToolCount,
            ConversationLog = result.ConversationLog,
            AgentOutputs = result.AgentOutputs, ToolCallLog = result.ToolCallLog,
            JudgeRawOutput = result.JudgeRawOutput
        });
        await _db.SaveChangesAsync();
    }

    public async Task<List<EvalCaseResult>> GetCaseResultsAsync(string taskId)
    {
        var entities = await _db.EvalCaseResults.Where(x => x.TaskId == taskId).OrderBy(x => x.Id).ToListAsync();
        return entities.Select(e =>
        {
            var dims = new List<DimensionScore>();
            try { dims = JsonSerializer.Deserialize<List<DimensionScore>>(e.DimensionsJson) ?? new(); } catch { }
            return new EvalCaseResult
            {
                Id = e.Id, TaskId = e.TaskId, TestCaseId = e.TestCaseId,
                Mode = e.Mode, RagEnabled = e.RagEnabled,
                Success = e.Success, ErrorMessage = e.ErrorMessage,
                Dimensions = dims,
                ResponseTimeMs = e.ResponseTimeMs, TotalTokens = e.TotalTokens,
                InputTokens = e.InputTokens, OutputTokens = e.OutputTokens,
                ToolCallAccuracy = e.ToolAccuracy,
                ExpectedToolCount = e.ExpectedToolCount, ActualToolCount = e.ActualToolCount,
                ConversationLog = e.ConversationLog, AgentOutputs = e.AgentOutputs,
                ToolCallLog = e.ToolCallLog, JudgeRawOutput = e.JudgeRawOutput,
                ExecutedAt = e.ExecutedAt
            };
        }).ToList();
    }

    public async Task<EvalReport?> GetReportAsync(string taskId)
    {
        var task = await GetTaskAsync(taskId);
        if (task == null) return null;
        var caseResults = await GetCaseResultsAsync(taskId);
        double avgMs = caseResults.Count > 0 ? caseResults.Average(r => r.ResponseTimeMs) : 0;
        var allDims = caseResults.SelectMany(r => r.Dimensions).ToList();
        double overall = allDims.Count > 0 ? allDims.Average(d => d.Score) : 0;

        ABComparison? comparison = null;
        var entity = await _db.EvalReports.FindAsync(taskId);
        if (entity?.ComparisonJson != null)
            try { comparison = JsonSerializer.Deserialize<ABComparison>(entity.ComparisonJson); } catch { }

        return new EvalReport
        {
            TaskId = task.Id, CaseSet = task.CaseSet, Modes = task.Modes,
            Status = task.Status, TotalCases = task.TotalCases,
            SuccessCases = task.CompletedCases, FailedCases = task.FailedCases,
            OverallScore = overall, AvgResponseMs = avgMs,
            TotalTokens = caseResults.Sum(r => r.TotalTokens),
            CaseResults = caseResults, Comparison = comparison,
            CreatedAt = task.CreatedAt, CompletedAt = task.CompletedAt
        };
    }
}
