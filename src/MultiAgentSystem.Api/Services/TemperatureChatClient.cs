// ============================================================
// TemperatureChatClient - 带温度参数的 IChatClient 包装器
// 用于 per-Agent temperature 配置，每个 Agent 获得独立的温度实例
// ============================================================

using Microsoft.Extensions.AI;

namespace MultiAgentSystem.Api.Services;

public class TemperatureChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly float _temperature;

    public TemperatureChatClient(IChatClient inner, float temperature)
    {
        _inner = inner;
        _temperature = temperature;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ChatOptions();
        options.Temperature = _temperature;
        return await _inner.GetResponseAsync(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ChatOptions();
        options.Temperature = _temperature;
        return _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public void Dispose() { /* inner 生命周期由 DI 管理，不释放 */ }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);
}
