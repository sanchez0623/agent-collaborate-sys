// ============================================================
// RateLimiter - 简易滑动窗口速率限制
// 基于 ConcurrentDictionary + 时间戳，按 IP 限制请求频率
// 无需 Redis 等外部依赖，适合单机部署
// ============================================================

using System.Collections.Concurrent;

namespace MultiAgentSystem.Api.Services;

public class RateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    private class Bucket
    {
        public int Count;
        public DateTime Reset;
    }

    public RateLimiter(int maxRequests = 100, int windowSeconds = 60)
    {
        _maxRequests = maxRequests;
        _window = TimeSpan.FromSeconds(windowSeconds);
    }

    /// <summary>
    /// 检查指定 key 是否被限流。返回 true 表示允许通过。
    /// 滑动窗口：到达 reset 时间后自动重置计数。
    /// </summary>
    public bool TryAcquire(string key)
    {
        var now = DateTime.UtcNow;
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket { Count = 0, Reset = now + _window });

        lock (bucket)
        {
            // 窗口已过期 → 重置
            if (now > bucket.Reset)
            {
                bucket.Count = 1;
                bucket.Reset = now + _window;
                return true;
            }

            // 窗口内计数递增
            bucket.Count++;
            return bucket.Count <= _maxRequests;
        }
    }
}
