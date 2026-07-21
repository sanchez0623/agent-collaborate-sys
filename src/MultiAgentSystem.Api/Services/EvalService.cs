// ============================================================
// EvalService - 评测引擎（真实编排管道版）
// 不再 mock 调用，直接走 AgentOrchestrator 的 7 种策略
// 工具调用从 OrchestrationEvent 流中真实捕获
// ============================================================

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using ChatMsg = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using MultiAgentSystem.Api.Models;
using MultiAgentSystem.Api.Strategies;

namespace MultiAgentSystem.Api.Services;

public class EvalService
{
    private readonly IChatClient _chatClient;
    private readonly TestCaseStore _caseStore;
    private readonly EvalReportStore _reportStore;
    private readonly JudgeService _judge;
    private readonly MetricCalculator _metrics;
    private readonly ILogger<EvalService> _logger;
    private readonly ApprovalCoordinator _approvals;

    // 7 种编排策略
    private readonly SequentialStrategy _sequential;
    private readonly ConcurrentStrategy _concurrent;
    private readonly HandoffStrategy _handoff;
    private readonly GroupChatStrategy _groupChat;
    private readonly MagenticStrategy _magentic;
    private readonly CrmStrategy _crm;
    private readonly RagStrategy _rag;

    private static readonly ConcurrentDictionary<string, EvalTask> RunningTasks = new();
    private static readonly ConcurrentDictionary<string, Channel<OrchestrationEvent>> ProgressChannels = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> TaskCancellations = new();

    public EvalService(
        IChatClient chatClient, TestCaseStore caseStore, EvalReportStore reportStore,
        JudgeService judge, MetricCalculator metrics, ILogger<EvalService> logger,
        ApprovalCoordinator approvals,
        SequentialStrategy sequential, ConcurrentStrategy concurrent, HandoffStrategy handoff,
        GroupChatStrategy groupChat, MagenticStrategy magentic, CrmStrategy crm, RagStrategy rag)
    {
        _chatClient = chatClient; _caseStore = caseStore; _reportStore = reportStore;
        _judge = judge; _metrics = metrics; _logger = logger; _approvals = approvals;
        _sequential = sequential; _concurrent = concurrent; _handoff = handoff;
        _groupChat = groupChat; _magentic = magentic; _crm = crm; _rag = rag;

        // 启动清理：进程重启后，DB 中残留的 running 任务实际已死，标记为 interrupted
        _ = CleanupStaleTasksAsync();
    }

    private async Task CleanupStaleTasksAsync()
    {
        try
        {
            var count = await _reportStore.MarkStaleTasksInterruptedAsync();
            if (count > 0)
                _logger.LogWarning("启动清理: {Count} 个残留 running 任务已标记为 interrupted（进程重启导致中断）", count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "启动清理残留任务失败"); }
    }

    // ===== 公开 API =====

    public async Task<string> RunAsync(EvalRunRequest req)
    {
        var task = new EvalTask
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            CaseSet = req.CaseSet, Modes = req.Modes,
            Status = "running", CreatedAt = DateTime.UtcNow
        };
        var cts = new CancellationTokenSource();
        RunningTasks[task.Id] = task;
        ProgressChannels[task.Id] = Channel.CreateUnbounded<OrchestrationEvent>();
        TaskCancellations[task.Id] = cts;

        await _reportStore.SaveTaskAsync(task);
        _ = ExecuteAsync(task, req, ProgressChannels[task.Id], cts.Token);
        return task.Id;
    }

    /// <summary>取消正在执行的评测任务</summary>
    public async Task<bool> CancelTaskAsync(string taskId)
    {
        if (!TaskCancellations.TryGetValue(taskId, out var cts)) return false;
        cts.Cancel();
        await _reportStore.UpdateTaskProgressAsync(taskId, 0, 0, "cancelled");
        _logger.LogInformation("评测任务已取消: taskId={Id}", taskId);
        return true;
    }

    public ChannelReader<OrchestrationEvent>? GetProgressChannel(string taskId)
        => ProgressChannels.TryGetValue(taskId, out var ch) ? ch.Reader : null;

    public async Task<EvalTask?> GetTaskAsync(string taskId)
        => await _reportStore.GetTaskAsync(taskId);

    public async Task<EvalReport?> GetReportAsync(string taskId)
        => await _reportStore.GetReportAsync(taskId);

    public async Task<List<EvalReport>> ListReportsAsync(int limit = 20)
        => await _reportStore.ListReportsAsync(limit);

    public async Task<bool> DeleteReportAsync(string taskId)
        => await _reportStore.DeleteReportAsync(taskId);

    // ===== 后台执行主循环 =====

    private async Task ExecuteAsync(EvalTask task, EvalRunRequest req, Channel<OrchestrationEvent> channel, CancellationToken cancellationToken)
    {
        try
        {
            var cases = req.CaseSet == "all" || req.CaseSet == "full"
                ? await _caseStore.GetAllAsync()
                : await GetCasesBySetAsync(req.CaseSet);

            if (cases.Count == 0)
            {
                _logger.LogWarning("评测无可用用例: caseSet={Set}", req.CaseSet);
                channel.Writer.TryComplete();
                return;
            }

            var ragModes = new List<bool> { req.EnableRag };
            if (req.DisableRag) ragModes.Add(false);

            task.TotalCases = cases.Count;
            _logger.LogInformation("评测开始: taskId={Id} caseSet={Set} cases={Cases} modes={Modes} rag={Rag} abRag={AbRag} concurrency={Conc} timeout={T}s",
                task.Id, req.CaseSet, cases.Count, string.Join(",", req.Modes), req.EnableRag, req.DisableRag, req.MaxConcurrency, req.TimeoutSeconds);

            await _reportStore.UpdateTaskProgressAsync(task.Id, 0, 0);

            var allResults = new ConcurrentBag<EvalCaseResult>();
            int completed = 0, failed = 0;

            // 构建任务列表：(mode, testCase, ragOn) 的所有组合，跳过模式不适用的用例
            var workItems = req.Modes
                .SelectMany(mode => cases
                    .Where(tc => string.IsNullOrWhiteSpace(tc.ApplicableModes) || tc.ApplicableModes.Contains(mode))
                    .SelectMany(tc => ragModes.Select(ragOn => (mode, tc, ragOn))))
                .ToList();

            var skippedCount = cases.Count * req.Modes.Count * ragModes.Count - workItems.Count;
            if (skippedCount > 0)
                _logger.LogInformation("模式适配校验: 跳过 {Skipped} 个不适用组合（用例 ApplicableModes 不含所选模式）", skippedCount);

            int totalCombinations = workItems.Count;

            var maxConcurrency = Math.Clamp(req.MaxConcurrency, 1, 5);
            _logger.LogInformation("并发执行: maxConcurrency={Conc} totalItems={Items}", maxConcurrency, workItems.Count);

            await Parallel.ForEachAsync(workItems,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
                async (item, ct) =>
                {
                    var (mode, tc, ragOn) = item;
                    var retryCount = Math.Clamp(req.RetryCount, 0, 3);
                    EvalCaseResult result;

                    // 失败重试：超时/LLM 报错时整例重跑，最多 retryCount 次
                    int attempt = 0;
                    while (true)
                    {
                        try
                        {
                            if (attempt > 0)
                                _logger.LogWarning("评测用例重试 {Attempt}/{Max}: case=#{Id} mode={Mode}", attempt, retryCount, tc.Id, mode);
                            else
                                _logger.LogInformation("评测用例开始: case=#{Id} {Title} mode={Mode} rag={Rag}", tc.Id, tc.Title, mode, ragOn);

                            result = await ExecuteRealCaseAsync(tc, mode, ragOn, req.TimeoutSeconds, req.JudgeCount);
                            if (result.Success || attempt >= retryCount || ct.IsCancellationRequested) break;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogError(ex, "评测用例异常: case=#{Id} mode={Mode} attempt={Attempt}", tc.Id, mode, attempt);
                            result = new EvalCaseResult
                            {
                                TaskId = task.Id, TestCaseId = tc.Id, Mode = mode, RagEnabled = ragOn,
                                Success = false, ErrorMessage = ex.Message
                            };
                            if (attempt >= retryCount || ct.IsCancellationRequested) break;
                        }
                        attempt++;
                    }

                    result.TaskId = task.Id;
                    allResults.Add(result);

                    var done = result.Success ? Interlocked.Increment(ref completed) : Interlocked.Increment(ref failed);
                    var currentDone = completed + failed;
                    task.CompletedCases = completed;
                    task.FailedCases = failed;

                    await _reportStore.SaveCaseResultAsync(result);
                    await _reportStore.UpdateTaskProgressAsync(task.Id, completed, failed);

                    _logger.LogInformation("评测用例完成: case=#{Id} success={Ok} score={Score:F1} dims={Dims} ms={Ms} tokens={Tk} tools={Tl} retries={Retry} err={Err}",
                        tc.Id, result.Success,
                        result.Dimensions.Count > 0 ? result.Dimensions.Average(d => d.Score) : 0,
                        result.Dimensions.Count,
                        result.ResponseTimeMs, result.TotalTokens,
                        result.ToolCallLog, attempt,
                        result.ErrorMessage ?? "-");

                    PushProgress(channel, task, currentDone, totalCombinations, tc.Title, mode);
                });

            // A/B 对比 + LLM 分析
            var resultList = allResults.ToList();
            _logger.LogInformation("评测全部用例完成,生成A/B对比: totalResults={Count}", resultList.Count);
            var comparison = GenerateComparison(resultList, req.Modes);
            try { comparison.Summary = await AnalyzeBestModeAsync(comparison, resultList); }
            catch (Exception ex) { _logger.LogWarning(ex, "LLM分析最佳模式失败"); }
            try { comparison.Improvement = await GenerateImprovementAsync(comparison, resultList); }
            catch (Exception ex) { _logger.LogWarning(ex, "LLM生成改进建议失败"); }

            var successResults = resultList.Where(r => r.Success).ToList();
            var report = new EvalReport
            {
                TaskId = task.Id, CaseSet = task.CaseSet, Modes = req.Modes,
                Status = "completed", TotalCases = resultList.Count,
                SuccessCases = completed, FailedCases = failed,
                OverallScore = successResults.Count > 0 ? Math.Round(successResults.Average(r => r.WeightedAverage), 2) : 0,
                AvgResponseMs = resultList.Count > 0 ? (long)resultList.Average(r => r.ResponseTimeMs) : 0,
                TotalTokens = resultList.Sum(r => r.TotalTokens),
                CaseResults = resultList, Comparison = comparison,
                CreatedAt = task.CreatedAt, CompletedAt = DateTime.UtcNow
            };

            _logger.LogInformation("保存评测报告: taskId={Id} overall={Score:F1} success={Ok}/{Total} ms={Ms} tokens={Tk}",
                task.Id, report.OverallScore, completed, resultList.Count, report.AvgResponseMs, report.TotalTokens);

            await _reportStore.FinalizeTaskAsync(report);
            task.Status = "completed";
            task.CompletedAt = DateTime.UtcNow;
            _logger.LogInformation("评测任务完成: taskId={Id}", task.Id);
            channel.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("评测任务被取消: taskId={Id}", task.Id);
            task.Status = "cancelled";
            await _reportStore.UpdateTaskProgressAsync(task.Id, task.CompletedCases, task.FailedCases, "cancelled");
            channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "评测执行失败: taskId={Id}", task.Id);
            task.Status = "failed";
            await _reportStore.UpdateTaskProgressAsync(task.Id, task.CompletedCases, task.FailedCases, "failed");
            channel.Writer.TryComplete();
        }
        finally
        {
            RunningTasks.TryRemove(task.Id, out _);
            TaskCancellations.TryRemove(task.Id, out var cts);
            cts?.Dispose();
        }
    }

    // ===== 真实编排管道执行 =====

    private async Task<EvalCaseResult> ExecuteRealCaseAsync(EvalTestCase tc, string mode, bool ragOn, int timeoutSec, int judgeCount = 1)
    {
        var result = new EvalCaseResult
        {
            TaskId = "", TestCaseId = tc.Id, Mode = mode, RagEnabled = ragOn, Success = false
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var capturedEvents = new List<OrchestrationEvent>();

        IOrchestrationStrategy? strategy = null;
        string evalSessionId = "";
        TokenUsage strategyUsage = new();
        try
        {
            strategy = ResolveStrategy(mode, ragOn);
            evalSessionId = $"eval_{Guid.NewGuid().ToString("N")[..8]}";
            ApprovalCoordinator.CurrentSessionId = evalSessionId;

            // 注册人审自动决策（评测场景：第1次拒绝，后续自动同意）
            var approvalCount = 0;
            _approvals.RegisterSession(evalSessionId, async (req) =>
            {
                var decision = Interlocked.Increment(ref approvalCount) <= 1 ? ApprovalStatus.Rejected : ApprovalStatus.Approved;
                await _approvals.ResolveAsync(req.Id, decision, "system", "评测自动决策", null);
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

            _logger.LogDebug("策略开始执行: case=#{Id} mode={Mode} strategy={Strategy}",
                tc.Id, mode, strategy.GetType().Name);

            // 启动 token 计数会话（捕获本次用例所有 LLM 调用的 token）
            TokenCountingChatClient.StartSession(evalSessionId);

            var finalOutput = await strategy.ExecuteAsync(
                tc.Question,
                new List<MultiAgentSystem.Api.Models.ChatMessage>() as IReadOnlyList<MultiAgentSystem.Api.Models.ChatMessage>,
                e => { lock (capturedEvents) capturedEvents.Add(e); },
                cts.Token);

            strategyUsage = TokenCountingChatClient.EndSession(evalSessionId);

            _logger.LogDebug("策略执行完成: case=#{Id} events={Count} outputLen={Len} tokens={In}/{Out}",
                tc.Id, capturedEvents.Count, finalOutput.Length, strategyUsage.InputTokens, strategyUsage.OutputTokens);

            result.AgentOutputs = finalOutput;
            result.ConversationLog = BuildConversationLog(capturedEvents, tc.Question, finalOutput);

            // 提取真实工具调用（存为 List<string> 工具名，ParseToolList 才能正确解析）
            var toolEvents = capturedEvents.Where(e => e.Type == OrchestrationEventType.ToolCall).ToList();
            var toolNames = toolEvents.Select(t => t.ToolName ?? "unknown").ToList();
            result.ToolCallLog = JsonSerializer.Serialize(toolNames);
            result.ActualToolCount = toolNames.Count;

            // 提取审批事件
            var approvalEvents = capturedEvents.Where(e => e.Type is OrchestrationEventType.ApprovalRequired or OrchestrationEventType.ApprovalResult).ToList();

        }
        catch (OperationCanceledException)
        {
            result.ErrorMessage = $"超时 ({timeoutSec}s)";
            result.Dimensions = GetErrorDimensions(result.ErrorMessage);
            result.Success = false;
            TokenCountingChatClient.EndSession(evalSessionId);  // 清理
            _logger.LogWarning("用例超时: case=#{Id} mode={Mode}", tc.Id, mode);
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            result.Dimensions = GetErrorDimensions(ex.Message);
            TokenCountingChatClient.EndSession(evalSessionId);  // 清理
            _logger.LogError(ex, "用例执行失败: case=#{Id} mode={Mode} strategy={Strategy}",
                tc.Id, mode, strategy.GetType().Name);
            return result;
        }
        finally { sw.Stop(); }

        result.ResponseTimeMs = sw.ElapsedMilliseconds;
        result.InputTokens = strategyUsage.InputTokens;
        result.OutputTokens = strategyUsage.OutputTokens;
        result.TotalTokens = strategyUsage.TotalTokens;

        // 自动指标（基于真实数据）
        _metrics.CalculateMetrics(result, tc.ExpectedToolCalls, result.ToolCallLog, result.AgentOutputs, null);

        // LLM Judge 评分（基于真实对话记录）
        _logger.LogDebug("用例开始Judge评分: case=#{Id}", tc.Id);
        TokenCountingChatClient.StartSession($"{evalSessionId}_judge");
        var judgeScores = await _judge.JudgeAsync(tc, tc.Question, result.AgentOutputs, judgeCount);
        var judgeUsage = TokenCountingChatClient.EndSession($"{evalSessionId}_judge");
        // 总 token = 策略 + Judge
        result.InputTokens += judgeUsage.InputTokens;
        result.OutputTokens += judgeUsage.OutputTokens;
        result.TotalTokens += judgeUsage.TotalTokens;
        result.JudgeRawOutput = JsonSerializer.Serialize(judgeScores);
        result.Dimensions.AddRange(judgeScores);
        result.Success = true;

        _logger.LogDebug("用例Judge完成: case=#{Id} score={Avg:F1} dims={Count} tools={Actual}/{Expected} totalTokens={Tk}",
            tc.Id, judgeScores.Average(d => d.Score), judgeScores.Count,
            result.ActualToolCount, tc.ExpectedToolCalls?.Length ?? 0, result.TotalTokens);

        return result;
    }

    // ===== 策略选择 =====

    private IOrchestrationStrategy ResolveStrategy(string mode, bool ragOn)
        => mode switch
        {
            "Sequential" => _sequential,
            "Concurrent" => _concurrent,
            "Handoff" => _handoff,
            "GroupChat" => _groupChat,
            "Magentic" => _magentic,
            "Crm" => _crm,
            "Rag" => _rag,
            _ => _sequential
        };

    // ===== 对话日志构建 =====

    private static string BuildConversationLog(List<OrchestrationEvent> events, string question, string finalOutput)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[User] {question}");
        foreach (var e in events)
        {
            if (e.Type == OrchestrationEventType.AgentStarted)
                sb.AppendLine($"[Agent] {e.Agent} started (round {e.Round})");
            else if (e.Type == OrchestrationEventType.AgentCompleted)
                sb.AppendLine($"[Agent] {e.Agent} completed → {e.Output}");
            else if (e.Type == OrchestrationEventType.ToolCall)
                sb.AppendLine($"[Tool] {e.ToolName}({e.ToolArgs}) → {e.ToolResult}");
            else if (e.Type == OrchestrationEventType.Handoff)
                sb.AppendLine($"[Handoff] {e.FromAgent} → {e.ToAgent}");
        }
        sb.AppendLine($"[FinalAnswer] {finalOutput}");
        return sb.ToString();
    }

    // ===== 用例集 =====

    private async Task<List<EvalTestCase>> GetCasesBySetAsync(string setName)
    {
        var all = await _caseStore.GetAllAsync();
        var filtered = all.Where(tc => setName switch
        {
            "quick-smoke" => tc.Tags.Contains("快速"),
            "rag" => tc.Category == "RAG",
            "tool" => tc.Category == "工具调用",
            "crm" => tc.Category is "CRM" or "人审",
            _ => true
        }).ToList();

        _logger.LogInformation("用例筛选: set={Set} total={Total} filtered={Filtered}",
            setName, all.Count, filtered.Count);
        return filtered;
    }

    // ===== SSE 进度 + 其他工具方法 =====

    private static void PushProgress(Channel<OrchestrationEvent> channel, EvalTask task, int done, int total, string currentCase = "", string currentMode = "")
    {
        var percent = total > 0 ? Math.Round((double)done / total * 100, 1) : 0;
        channel.Writer.TryWrite(new OrchestrationEvent(
            OrchestrationEventType.AgentDelta,
            Status: $"{done}/{total}",
            Output: JsonSerializer.Serialize(new
            {
                type = "eval_progress",
                taskId = task.Id, done, total, percent,
                currentCase, currentMode,
                completed = task.CompletedCases, failed = task.FailedCases
            })));
    }

    private static List<DimensionScore> GetErrorDimensions(string reason)
        => new()
        {
            new() { Dimension = EvalDimension.Accuracy, Score = 0, Weight = 1.5, Reasoning = reason },
            new() { Dimension = EvalDimension.Completeness, Score = 0, Weight = 1.2, Reasoning = reason },
            new() { Dimension = EvalDimension.Relevance, Score = 0, Weight = 1.0, Reasoning = reason },
            new() { Dimension = EvalDimension.Hallucination, Score = 0, Weight = 2.0, Reasoning = reason },
            new() { Dimension = EvalDimension.ToolAccuracy, Score = 0, Weight = 1.2, Reasoning = reason },
            new() { Dimension = EvalDimension.Efficiency, Score = 0, Weight = 0.8, Reasoning = reason },
        };

    // ===== A/B 对比 =====

    private async Task<string> AnalyzeBestModeAsync(ABComparison comp, List<EvalCaseResult> results)
    {
        if (comp.ModeComparisons.Count < 2) return comp.Summary;
        var best = comp.ModeComparisons.OrderByDescending(m => m.WeightedTotal).First();
        var others = comp.ModeComparisons.Where(m => m.Mode != best.Mode).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("你是一个多Agent系统评测分析师。请用1-2句话分析为什么以下模式表现最佳：\n");
        sb.AppendLine($"## 最佳模式：{best.Mode}");
        sb.AppendLine($"加权总分 {best.WeightedTotal} | 延迟 {best.AvgResponseMs}ms | 工具准确率 {best.AvgToolAccuracy:P0}\n");
        sb.AppendLine("维度对比：");
        foreach (var dim in Enum.GetValues<EvalDimension>())
        {
            var bestScore = best.AvgScores.GetValueOrDefault(dim, 0);
            var otherAvg = others.Average(m => m.AvgScores.GetValueOrDefault(dim, 0));
            sb.AppendLine($"- {dim}: {best.Mode}={bestScore:F1} vs 其他平均={otherAvg:F1}");
        }
        sb.AppendLine($"\n其他模式：{string.Join(",", others.Select(m => $"{m.Mode}({m.WeightedTotal})"))}");
        sb.AppendLine("\n请分析该模式表现最优的**架构原理**，不是复述数字。输出纯文本，不要markdown格式。");
        var msg = new ChatMsg(ChatRole.User, sb.ToString());
        var resp = await _chatClient.GetResponseAsync([msg], new() { MaxOutputTokens = 256 });
        return resp.Text?.Trim() ?? comp.Summary;
    }

    /// <summary>基于评测结果生成可落地的改进建议（LLM）</summary>
    private async Task<string> GenerateImprovementAsync(ABComparison comp, List<EvalCaseResult> results)
    {
        var success = results.Where(r => r.Success).ToList();
        var failed = results.Where(r => !r.Success).ToList();
        if (results.Count == 0) return "";

        // 各维度平均分
        var dimAvg = new Dictionary<EvalDimension, double>();
        foreach (var dim in Enum.GetValues<EvalDimension>())
        {
            var scores = success.SelectMany(r => r.Dimensions).Where(d => d.Dimension == dim).ToList();
            dimAvg[dim] = scores.Count > 0 ? Math.Round(scores.Average(d => d.Score), 1) : 0;
        }
        var weakest = dimAvg.OrderBy(kv => kv.Value).First();
        var strongest = dimAvg.OrderByDescending(kv => kv.Value).First();

        var sb = new StringBuilder();
        sb.AppendLine("你是一个多Agent系统质量顾问。根据以下评测数据，给出 2-4 条具体、可落地的改进建议。\n");
        sb.AppendLine($"成功率：{success.Count}/{results.Count}");
        sb.AppendLine($"最弱维度：{weakest.Key}（{weakest.Value} 分） | 最强维度：{strongest.Key}（{strongest.Value} 分）");
        sb.AppendLine("各维度平均分：");
        foreach (var kv in dimAvg) sb.AppendLine($"- {kv.Key}: {kv.Value}");
        if (failed.Count > 0)
        {
            sb.AppendLine($"\n失败用例（{failed.Count} 个）错误摘要：");
            foreach (var f in failed.Take(5))
                sb.AppendLine($"- 用例#{f.TestCaseId} [{f.Mode}]: {(f.ErrorMessage?.Length > 80 ? f.ErrorMessage[..80] + "..." : f.ErrorMessage)}");
        }
        sb.AppendLine("\n要求：建议要针对最弱维度和失败原因，指出可能的架构/Prompt/工具层面的改进方向。");
        sb.AppendLine("输出纯文本，每条建议一行，用「1. 2. 3.」编号，不要 markdown 标题，总字数不超过 200 字。");

        var msg = new ChatMsg(ChatRole.User, sb.ToString());
        var resp = await _chatClient.GetResponseAsync([msg], new() { MaxOutputTokens = 400, Temperature = 0.3f });
        return resp.Text?.Trim() ?? "";
    }

    private ABComparison GenerateComparison(List<EvalCaseResult> allResults, List<string> modes)
    {
        var comparison = new ABComparison { Summary = "" };
        foreach (var mode in modes)
        {
            var allForMode = allResults.Where(r => r.Mode == mode).ToList();
            var modeResults = allForMode.Where(r => r.Success).ToList();
            if (allForMode.Count == 0) continue;
            var dimScores = new Dictionary<EvalDimension, double>();
            foreach (var dim in Enum.GetValues<EvalDimension>())
            {
                var scores = modeResults.SelectMany(r => r.Dimensions).Where(d => d.Dimension == dim).ToList();
                dimScores[dim] = scores.Count > 0 ? Math.Round(scores.Average(d => d.Score), 2) : 0;
            }
            comparison.ModeComparisons.Add(new ModeComparison
            {
                Mode = mode,
                WeightedTotal = Math.Round(modeResults.Count > 0 ? modeResults.Average(r => r.WeightedAverage) : 0, 2),
                AvgResponseMs = (long)allForMode.Average(r => r.ResponseTimeMs),
                AvgToolAccuracy = Math.Round(allForMode.Average(r => r.ToolCallAccuracy), 2),
                AvgScores = dimScores,
                CasesRun = allForMode.Count,
                SuccessCount = modeResults.Count,
                TotalTokens = (int)allForMode.Average(r => r.TotalTokens)
            });
        }
        if (comparison.ModeComparisons.Count > 1)
        {
            var best = comparison.ModeComparisons.OrderByDescending(m => m.WeightedTotal).First();
            var runner = comparison.ModeComparisons.OrderByDescending(m => m.WeightedTotal).Skip(1).First();
            comparison.Summary = $"{best.Mode}: 总分{best.WeightedTotal} 工具准确率{best.AvgToolAccuracy:P0} / {runner.Mode}: 总分{runner.WeightedTotal} 工具准确率{runner.AvgToolAccuracy:P0}";
        }

        // RAG 开关 A/B 对比：仅当同一模式同时存在 ragOn/ragOff 结果时填充
        foreach (var mode in modes)
        {
            foreach (var ragOn in new[] { true, false })
            {
                var group = allResults.Where(r => r.Mode == mode && r.RagEnabled == ragOn && r.Success).ToList();
                if (group.Count == 0) continue;
                var hasCounterpart = allResults.Any(r => r.Mode == mode && r.RagEnabled != ragOn && r.Success);
                if (!hasCounterpart) continue;

                var hallScores = group.SelectMany(r => r.Dimensions).Where(d => d.Dimension == EvalDimension.Hallucination).ToList();
                var accScores = group.SelectMany(r => r.Dimensions).Where(d => d.Dimension == EvalDimension.Accuracy).ToList();
                comparison.RagComparisons.Add(new RagComparison
                {
                    Mode = mode,
                    RagEnabled = ragOn,
                    WeightedTotal = Math.Round(group.Average(r => r.WeightedAverage), 2),
                    AvgHallucination = Math.Round(hallScores.Count > 0 ? hallScores.Average(d => d.Score) : 0, 1),
                    AvgAccuracy = Math.Round(accScores.Count > 0 ? accScores.Average(d => d.Score) : 0, 1)
                });
            }
        }

        return comparison;
    }
}
