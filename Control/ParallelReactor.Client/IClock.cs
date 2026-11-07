using System;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelReactor.Client;

/// <summary>时间注入：心跳 / 轮询 / 退避都经它等待，单测用假时钟手动推进。</summary>
public interface IClock
{
    DateTime UtcNow { get; }
    Task Delay(TimeSpan delay, CancellationToken ct);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct);
}
