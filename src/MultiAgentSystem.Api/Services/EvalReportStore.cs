using System.Data.Common;
// ============================================================
// EvalReportStore - 评测报告仓储（SQLite）
// ============================================================

using System.Text.Json;
using MultiAgentSystem.Api.Data;
using Microsoft.Data.Sqlite;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class EvalReportStore
{
    private readonly IDbConnectionFactory _db;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public EvalReportStore(IDbConnectionFactory db)
    {
        _db = db;
        InitAsync().GetAwaiter().GetResult();
    }

    private async Task InitAsync()
    {
        await WithLockAsync(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS eval_reports (
                    task_id TEXT PRIMARY KEY,
                    case_set TEXT NOT NULL,
                    modes TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending',
                    total_cases INTEGER DEFAULT 0,
                    success_cases INTEGER DEFAULT 0,
                    failed_cases INTEGER DEFAULT 0,
                    overall_score REAL DEFAULT 0,
                    avg_response_ms INTEGER DEFAULT 0,
                    total_tokens INTEGER DEFAULT 0,
                    comparison_json TEXT,
                    created_at TEXT DEFAULT (datetime('now')),
                    completed_at TEXT
                );
                CREATE TABLE IF NOT EXISTS eval_case_results (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id TEXT NOT NULL,
                    testcase_id INTEGER NOT NULL,
                    mode TEXT NOT NULL,
                    rag_enabled INTEGER DEFAULT 1,
                    success INTEGER DEFAULT 0,
                    error_message TEXT,
                    dimensions_json TEXT,
                    response_time_ms INTEGER DEFAULT 0,
                    total_tokens INTEGER DEFAULT 0,
                    input_tokens INTEGER DEFAULT 0,
                    output_tokens INTEGER DEFAULT 0,
                    tool_accuracy REAL DEFAULT 0,
                    expected_tool_count INTEGER DEFAULT 0,
                    actual_tool_count INTEGER DEFAULT 0,
                    conversation_log TEXT,
                    agent_outputs TEXT,
                    tool_call_log TEXT,
                    judge_raw_output TEXT,
                    executed_at TEXT DEFAULT (datetime('now'))
                );
                CREATE INDEX IF NOT EXISTS idx_eval_results_task ON eval_case_results(task_id);
                """;
            await cmd.ExecuteNonQueryAsync();
        });
    }

    // ---------- Task CRUD ----------

    public async Task SaveTaskAsync(EvalTask task)
    {
        await WithLockAsync(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO eval_reports
                (task_id,case_set,modes,status,total_cases,success_cases,failed_cases,
                 overall_score,avg_response_ms,total_tokens,comparison_json,created_at,completed_at)
                VALUES (@id,@cs,@ms,@st,@tc,@sc,@fc,@os,@ar,@tt,@cj,@ca,@cp);
                """;
            cmd.AddParam("@id", task.Id);
            cmd.AddParam("@cs", task.CaseSet);
            cmd.AddParam("@ms", JsonSerializer.Serialize(task.Modes));
            cmd.AddParam("@st", task.Status);
            cmd.AddParam("@tc", task.TotalCases);
            cmd.AddParam("@sc", task.CompletedCases);
            cmd.AddParam("@fc", task.FailedCases);
            cmd.AddParam("@os", 0.0);
            cmd.AddParam("@ar", 0);
            cmd.AddParam("@tt", 0);
            cmd.AddParam("@cj", DBNull.Value);
            cmd.AddParam("@ca", task.CreatedAt.ToString("o"));
            cmd.AddParam("@cp", task.CompletedAt?.ToString("o") as object ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task UpdateTaskProgressAsync(string taskId, int completed, int failed, string status = "running")
    {
        await WithLockAsync(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE eval_reports SET completed_cases=success_cases=@c,failed_cases=@f,status=@st WHERE task_id=@id;";
            cmd.CommandText = "UPDATE eval_reports SET success_cases=@c, failed_cases=@f, status=@st WHERE task_id=@id;";
            cmd.AddParam("@c", completed);
            cmd.AddParam("@f", failed);
            cmd.AddParam("@st", status);
            cmd.AddParam("@id", taskId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task FinalizeTaskAsync(EvalReport report)
    {
        await WithLockAsync(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE eval_reports SET
                    status=@st, success_cases=@sc, failed_cases=@fc,
                    overall_score=@os, avg_response_ms=@ar, total_tokens=@tt,
                    comparison_json=@cj, completed_at=datetime('now')
                WHERE task_id=@id;
                """;
            cmd.AddParam("@st", "completed");
            cmd.AddParam("@sc", report.SuccessCases);
            cmd.AddParam("@fc", report.FailedCases);
            cmd.AddParam("@os", report.OverallScore);
            cmd.AddParam("@ar", (long)report.AvgResponseMs);
            cmd.AddParam("@tt", report.TotalTokens);
            cmd.AddParam("@cj", report.Comparison != null
                ? JsonSerializer.Serialize(report.Comparison) : (object)DBNull.Value);
            cmd.AddParam("@id", report.TaskId);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task<EvalTask?> GetTaskAsync(string taskId)
    {
        return await WithLock(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM eval_reports WHERE task_id=@id;";
            cmd.AddParam("@id", taskId);
            using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                var modes = new List<string>();
                try { modes = JsonSerializer.Deserialize<List<string>>(r.GetString(2)) ?? new(); } catch { }
                return new EvalTask
                {
                    Id = r.GetString(0),
                    CaseSet = r.GetString(1),
                    Modes = modes,
                    Status = r.GetString(3),
                    TotalCases = r.GetInt32(4),
                    CompletedCases = r.GetInt32(5),
                    FailedCases = r.GetInt32(6),
                    CreatedAt = SafeDate(r, 9),
                    CompletedAt = r.IsDBNull(10) ? null : SafeDate(r, 10)
                };
            }
            return null;
        });
    }

    public async Task<List<EvalReport>> ListReportsAsync(int limit = 20)
    {
        return await WithLock(async () =>
        {
            var list = new List<EvalReport>();
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM eval_reports ORDER BY created_at DESC LIMIT @lim;";
            cmd.AddParam("@lim", limit);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var modes = new List<string>();
                try { modes = JsonSerializer.Deserialize<List<string>>(r.GetString(2)) ?? new(); } catch { }
                list.Add(new EvalReport
                {
                    TaskId = r.GetString(0),
                    CaseSet = r.GetString(1),
                    Modes = modes,
                    Status = r.GetString(3),
                    TotalCases = r.GetInt32(4),
                    SuccessCases = r.GetInt32(5),
                    FailedCases = r.GetInt32(6),
                    OverallScore = r.GetDouble(7),
                    AvgResponseMs = r.GetInt64(8),
                    TotalTokens = r.GetInt32(9),
                    CreatedAt = SafeDate(r, 11),
                    CompletedAt = r.IsDBNull(12) ? null : SafeDate(r, 12)
                });
            }
            return list;
        });
    }

    // ---------- Case Results ----------

    public async Task SaveCaseResultAsync(EvalCaseResult result)
    {
        await WithLockAsync(async () =>
        {
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO eval_case_results
                (task_id,testcase_id,mode,rag_enabled,success,error_message,dimensions_json,
                 response_time_ms,total_tokens,input_tokens,output_tokens,tool_accuracy,
                 expected_tool_count,actual_tool_count,conversation_log,agent_outputs,tool_call_log,
                 judge_raw_output)
                VALUES (@ti,@tci,@m,@re,@s,@em,@dj,@rt,@tt,@it,@ot,@ta,@etc,@atc,@cl,@ao,@tl,@jr);
                """;
            cmd.AddParam("@ti", result.TaskId);
            cmd.AddParam("@tci", result.TestCaseId);
            cmd.AddParam("@m", result.Mode);
            cmd.AddParam("@re", result.RagEnabled ? 1 : 0);
            cmd.AddParam("@s", result.Success ? 1 : 0);
            cmd.AddParam("@em", result.ErrorMessage as object ?? DBNull.Value);
            cmd.AddParam("@dj", JsonSerializer.Serialize(result.Dimensions));
            cmd.AddParam("@rt", result.ResponseTimeMs);
            cmd.AddParam("@tt", result.TotalTokens);
            cmd.AddParam("@it", result.InputTokens);
            cmd.AddParam("@ot", result.OutputTokens);
            cmd.AddParam("@ta", result.ToolCallAccuracy);
            cmd.AddParam("@etc", result.ExpectedToolCount);
            cmd.AddParam("@atc", result.ActualToolCount);
            cmd.AddParam("@cl", result.ConversationLog);
            cmd.AddParam("@ao", result.AgentOutputs);
            cmd.AddParam("@tl", result.ToolCallLog);
            cmd.AddParam("@jr", result.JudgeRawOutput);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    public async Task<List<EvalCaseResult>> GetCaseResultsAsync(string taskId)
    {
        return await WithLock(async () =>
        {
            var list = new List<EvalCaseResult>();
            using var conn = _db.CreateConnection();
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM eval_case_results WHERE task_id=@id ORDER BY id;";
            cmd.AddParam("@id", taskId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var dims = new List<DimensionScore>();
                try { dims = JsonSerializer.Deserialize<List<DimensionScore>>(r.GetString(6)) ?? new(); } catch { }
                list.Add(new EvalCaseResult
                {
                    Id = r.GetInt32(0),
                    TaskId = r.GetString(1),
                    TestCaseId = r.GetInt32(2),
                    Mode = r.GetString(3),
                    RagEnabled = r.GetInt32(4) == 1,
                    Success = r.GetInt32(5) == 1,
                    ErrorMessage = r.IsDBNull(6) ? null : r.GetString(6),
                    Dimensions = dims,
                    ResponseTimeMs = r.GetInt64(7),
                    TotalTokens = r.GetInt32(8),
                    InputTokens = r.GetInt32(9),
                    OutputTokens = r.GetInt32(10),
                    ToolCallAccuracy = r.GetDouble(11),
                    ExpectedToolCount = r.GetInt32(12),
                    ActualToolCount = r.GetInt32(13),
                    ConversationLog = r.GetString(14),
                    AgentOutputs = r.GetString(15),
                    ToolCallLog = r.GetString(16),
                    JudgeRawOutput = r.GetString(17),
                    ExecutedAt = SafeDate(r, 18)
                });
            }
            return list;
        });
    }

    public async Task<EvalReport?> GetReportAsync(string taskId)
    {
        var task = await GetTaskAsync(taskId);
        if (task == null) return null;
        var caseResults = await GetCaseResultsAsync(taskId);
        double avgMs = caseResults.Count > 0 ? caseResults.Average(r => r.ResponseTimeMs) : 0;
        var allDims = caseResults.SelectMany(r => r.Dimensions).ToList();
        double overall = allDims.Count > 0 ? allDims.Average(d => d.Score) : 0;

        // 读取 A/B 对比数据
        ABComparison? comparison = null;
        using var connCmp = _db.CreateConnection();
        await connCmp.OpenAsync();
        using var cmdCmp = connCmp.CreateCommand();
        cmdCmp.CommandText = "SELECT comparison_json FROM eval_reports WHERE task_id=@id;";
        cmdCmp.AddParam("@id", taskId);
        var jsonStr = await cmdCmp.ExecuteScalarAsync() as string;
        if (!string.IsNullOrWhiteSpace(jsonStr))
            try { comparison = JsonSerializer.Deserialize<ABComparison>(jsonStr); } catch { }

        return new EvalReport
        {
            TaskId = task.Id,
            CaseSet = task.CaseSet,
            Modes = task.Modes,
            Status = task.Status,
            TotalCases = task.TotalCases,
            SuccessCases = task.CompletedCases,
            FailedCases = task.FailedCases,
            OverallScore = overall,
            AvgResponseMs = avgMs,
            TotalTokens = caseResults.Sum(r => r.TotalTokens),
            CaseResults = caseResults,
            Comparison = comparison,
            CreatedAt = task.CreatedAt,
            CompletedAt = task.CompletedAt
        };
    }

    private async Task<T> WithLock<T>(Func<Task<T>> action)
    {
        await _lock.WaitAsync();
        try { return await action(); }
        finally { _lock.Release(); }
    }

    private async Task WithLockAsync(Func<Task> action)
    {
        await _lock.WaitAsync();
        try { await action(); }
        finally { _lock.Release(); }
    }

    private static DateTime SafeDate(DbDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return DateTime.MinValue;
        try { return DateTime.Parse(r.GetString(ordinal)); }
        catch { return DateTime.MinValue; }
    }
}
