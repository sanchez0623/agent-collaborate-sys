// ============================================================
// ApprovalCoordinator - 人审（Human-in-the-Loop）协调器
//
// 核心机制（面试重点）：
//   Agent 调用敏感工具时不直接执行，而是：
//     1. CreateApprovalAsync 写入 approval_requests 表（status=Pending）
//     2. 通过该会话专属的 SSE 回调推 approval_required 事件给前端
//     3. WaitForDecisionAsync 用 TaskCompletionSource 阻塞当前 Agent Task
//     4. 前端收到 approval_required → 弹审核卡片 → 人决策 → POST /api/approvals/decide
//     5. 后端 ResolveAsync 更新 DB 状态 + SetResult 释放 TaskCompletionSource
//     6. Agent 拿到决策结果，继续执行（通过则真正删除，拒绝则中止）
//
// 并发安全设计（修复多租户状态污染）：
//   - ApprovalCoordinator 是 Singleton，但不再持有可变实例字段 CurrentSessionId / OnApprovalRequired
//   - SSE 回调改为 ConcurrentDictionary<sessionId, callback> 按 会话隔离：
//       A 用户的审核请求只会推到 A 的 SSE 流，不会串到 B
//   - 当前会话 ID 通过 AsyncLocal<string> 在异步调用链中传播：
//       每个 HTTP 请求流有独立的 AsyncLocal 值，无并发污染
//       （比 ThreadLocal 更适合 async/await 链路）
//
// 超时保护（修复 TaskCompletionSource 内存泄漏）：
//   - WaitForDecisionAsync 关联「请求取消令牌 + 超时令牌」
//   - 超时（默认 10 分钟）自动驳回：TrySetResult(Rejected) + 更新 DB
//   - 用户中止（ct 取消）自动释放：TrySetCanceled 向上抛 OperationCanceledException
//   - _pending 字典条目必然被移除，不再只增不减
// ============================================================

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using MultiAgentSystem.Api.Models;

namespace MultiAgentSystem.Api.Services;

public class ApprovalCoordinator
{
    private readonly BusinessStore _store;

    /// <summary>待审核任务的同步句柄池：approvalId → PendingApproval（含超时控制 + 会话归属）</summary>
    private readonly ConcurrentDictionary<int, PendingApproval> _pending = new();

    /// <summary>按 sessionId 隔离的 SSE 回调池：多用户并发时 A 的审核不会推到 B 的流</summary>
    private readonly ConcurrentDictionary<string, Func<ApprovalRequest, Task>> _sessionCallbacks = new();

    /// <summary>
    /// 当前异步流的 sessionId（AsyncLocal 在 async/await 链中自动传播）。
    /// SSE 端点在编排开始前设置，工具层（如 CrmTools）即可读到当前会话。
    /// 相比实例字段，AsyncLocal 每个异步流独立，无并发污染。
    /// </summary>
    private static readonly AsyncLocal<string?> _currentSessionId = new();
    public static string? CurrentSessionId
    {
        get => _currentSessionId.Value;
        set => _currentSessionId.Value = value;
    }

    /// <summary>默认人审超时：10 分钟（超时后系统自动驳回，防止 Task 永久阻塞）</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public ApprovalCoordinator(BusinessStore store) => _store = store;

    /// <summary>
    /// 注册某会话的 SSE 回调（SSE 流开始时调用）。
    /// 替代原先的可变实例字段 OnApprovalRequired，实现多会话隔离。
    /// </summary>
    public void RegisterSession(string sessionId, Func<ApprovalRequest, Task> callback)
        => _sessionCallbacks[sessionId] = callback;

    /// <summary>
    /// 注销会话回调（SSE 流结束时调用，防止回调字典无限增长）。
    /// </summary>
    public void UnregisterSession(string sessionId)
        => _sessionCallbacks.TryRemove(sessionId, out _);

    /// <summary>
    /// 1. 创建审核请求并通知前端（按 sessionId 路由到对应的 SSE 流）
    /// </summary>
    public async Task<int> RequestApprovalAsync(ApprovalRequest req)
    {
        // 兜底：若调用方未显式设置 SessionId，从 AsyncLocal 取当前流上下文
        if (string.IsNullOrEmpty(req.SessionId))
            req.SessionId = CurrentSessionId ?? "";

        var id = await _store.CreateApprovalAsync(req);
        req.Id = id;

        // 按 sessionId 找到该会话专属的 SSE 回调（多用户隔离的核心）
        if (!string.IsNullOrEmpty(req.SessionId)
            && _sessionCallbacks.TryGetValue(req.SessionId, out var callback))
        {
            await callback(req);
        }
        return id;
    }

    /// <summary>
    /// 2. Agent 阻塞等待人审结果（含超时保护）
    ///
    /// 超时/取消处理：
    ///   - 用户主动中止（ct 取消）→ TrySetCanceled → 向上抛 OperationCanceledException
    ///   - 超时未响应 → TrySetResult(Rejected) → Agent 收到驳回继续执行
    ///   - 两种情况都会从 _pending 移除条目，不再泄漏
    /// </summary>
    /// <param name="approvalId">审核请求 ID</param>
    /// <param name="ct">请求取消令牌（用户中止 SSE 时触发）</param>
    public async Task<ApprovalDecision> WaitForDecisionAsync(int approvalId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // 关联「请求取消令牌 + 超时令牌」：任一触发即释放等待
        var linkedCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        linkedCts.CancelAfter(DefaultTimeout);

        var sessionId = CurrentSessionId ?? "";
        var pending = new PendingApproval(tcs, linkedCts, sessionId, DateTime.UtcNow);
        _pending[approvalId] = pending;

        // 注册超时/取消回调：触发时移除条目并给出结论
        var registration = linkedCts.Token.Register(() =>
        {
            if (!_pending.TryRemove(approvalId, out var p)) return;

            if (ct.IsCancellationRequested)
            {
                // 用户主动中止 → 向上抛取消异常，让 Agent Task 终止
                p.Tcs.TrySetCanceled(ct);
            }
            else
            {
                // 超时 → 系统自动驳回（best-effort 更新 DB，不阻塞释放流程）
                _ = TryMarkTimeoutAsync(approvalId);
                p.Tcs.TrySetResult(new ApprovalDecision(
                    ApprovalStatus.Rejected, "system-timeout",
                    "人审超时未响应，系统自动驳回", null));
            }
        });

        try
        {
            return await tcs.Task;
        }
        finally
        {
            // 释放超时计时器与回调注册（无论正常完成/取消/超时都会执行）
            registration.Dispose();
            linkedCts.Dispose();
        }
    }

    /// <summary>超时驳回时异步更新 DB 状态（best-effort，失败仅记日志不影响释放）</summary>
    private async Task TryMarkTimeoutAsync(int approvalId)
    {
        try
        {
            await _store.UpdateApprovalAsync(approvalId, ApprovalStatus.Rejected,
                "system-timeout", "人审超时未响应，系统自动驳回");
        }
        catch
        {
            // DB 更新失败不阻塞 Agent 释放流程
        }
    }

    /// <summary>
    /// 3. 前端回传决策 → 更新 DB + 释放 Agent 等待
    /// </summary>
    public async Task<bool> ResolveAsync(int approvalId, ApprovalStatus status, string reviewer, string? comment, string? modifiedParams)
    {
        var ok = await _store.UpdateApprovalAsync(approvalId, status, reviewer, comment);
        if (!ok) return false;

        if (_pending.TryRemove(approvalId, out var pending))
        {
            // 取消超时计时器，防止它后续误触发（registration 回调会因 TryRemove 失败而 return）
            pending.TimeoutCts.Cancel();
            pending.Tcs.TrySetResult(new ApprovalDecision(status, reviewer, comment, modifiedParams));
        }
        return true;
    }

    /// <summary>检查指定会话是否还有未决审核（SSE 结束前用）</summary>
    public bool HasPendingForSession(string sessionId)
        => _pending.Any(kv => kv.Value.SessionId == sessionId);

    /// <summary>
    /// 取消指定会话的所有待审（用户点"停止"时调用）。
    /// 替代原先的 CancelAllPending()，只清理当前会话，不影响其他并发用户。
    /// </summary>
    public void CancelPendingForSession(string sessionId)
    {
        foreach (var kv in _pending)
        {
            if (kv.Value.SessionId == sessionId && _pending.TryRemove(kv.Key, out var p))
            {
                p.Tcs.TrySetCanceled();
            }
        }
    }
}

/// <summary>待审任务句柄（含超时控制句柄 + 会话归属，用于按会话批量取消）</summary>
internal sealed record PendingApproval(
    TaskCompletionSource<ApprovalDecision> Tcs,
    CancellationTokenSource TimeoutCts,
    string SessionId,
    DateTime CreatedAt);

/// <summary>人审决策结果（传给 Agent）</summary>
public record ApprovalDecision(
    ApprovalStatus Status,
    string Reviewer,
    string? Comment,
    string? ModifiedParameters);
