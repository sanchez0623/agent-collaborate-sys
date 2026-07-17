// ============================================================
// ResilientChatClient - IChatClient 装饰器
// 对底层 LLM 调用加重试、超时、友好降级
//
// 非流式：指数退避重试 3 次，失败返回降级文本
// 流式：捕获异常降级，防止 SSE 流断开
// ============================================================

using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Services;

public class ResilientChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly int _maxRetries;
    private readonly ILogger? _logger;

    public ResilientChatClient(IChatClient inner, int maxRetries = 3, ILogger? logger = null)
    {
        _inner = inner;
        _maxRetries = maxRetries;
        _logger = logger;
    }

    /// <summary>
    /// 非流式调用：指数退避重试 3 次 (1s→2s→4s)，重试耗尽返回降级文本。
    /// 记录每次成功调用的耗时与 token 使用量。
    /// </summary>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var msgCount = messages.Count();
        Exception? lastEx = null;
        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = await _inner.GetResponseAsync(messages, options, cancellationToken);
                sw.Stop();

                // 结构化日志：LLM 调用成功指标
                var usage = resp.Usage;
                _logger?.LogInformation(
                    "LLM 调用成功 | 耗时 {ElapsedMs}ms | 消息数 {MsgCount} | " +
                    "输入 tokens {InputTokens} | 输出 tokens {OutputTokens} | attempt {Attempt}",
                    sw.ElapsedMilliseconds,
                    msgCount,
                    usage?.InputTokenCount ?? 0,
                    usage?.OutputTokenCount ?? 0,
                    attempt + 1);
                return resp;
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < _maxRetries)
            {
                lastEx = ex;
                var delay = TimeSpan.FromMilliseconds(1000 * Math.Pow(2, attempt));
                _logger?.LogWarning(ex,
                    "LLM 调用失败 (attempt {Attempt}/{MaxRetries})，{Delay}ms 后重试...",
                    attempt + 1, _maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        _logger?.LogError(lastEx, "LLM 调用重试 {MaxRetries} 次后仍失败，返回降级响应", _maxRetries);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            $"抱歉，AI 服务暂时不可用（已重试 {_maxRetries} 次）。请稍后再试。"));
    }

    /// <summary>
    /// 流式调用：捕获异常降级为错误消息，防止 SSE 流中断
    /// </summary>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return SafeStreamAsync(messages, options, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> SafeStreamAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var inner = _inner.GetStreamingResponseAsync(messages, options, ct);
        await using var enumerator = inner.GetAsyncEnumerator(ct);

        while (true)
        {
            var (hasNext, current, error) = await TryMoveNextAsync(enumerator);
            if (error is not null)
            {
                _logger?.LogError(error, "LLM 流式调用失败，返回降级提示");
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    "⚠️ AI 服务暂时不可用，请稍后再试。");
                yield break;
            }
            if (!hasNext) yield break;
            yield return current!;
        }
    }

    private static async Task<(bool hasNext, ChatResponseUpdate? current, Exception? error)>
        TryMoveNextAsync(IAsyncEnumerator<ChatResponseUpdate> enumerator)
    {
        try
        {
            if (await enumerator.MoveNextAsync())
                return (true, enumerator.Current, null);
            return (false, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, null, ex);
        }
    }

    public void Dispose() => _inner.Dispose();

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    /// <summary>
    /// 判断异常是否可重试：HTTP 5xx、429限流、网络超时
    /// </summary>
    private static bool IsRetryable(Exception ex) => ex switch
    {
        HttpRequestException hre => hre.StatusCode is null
            || (int)hre.StatusCode >= 500
            || (int)hre.StatusCode == 429,
        TaskCanceledException => true,
        System.ClientModel.ClientResultException cre => cre.Status == 429 || cre.Status >= 500,
        _ => false
    };
}
