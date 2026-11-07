using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ParallelReactor.Api;

/// <summary><see cref="IdempotencyStore.Begin"/> 的结果。</summary>
public enum IdemBegin
{
    /// <summary>新键：调用方执行命令，完成后 <see cref="IdempotencyStore.Complete"/>。</summary>
    New,
    /// <summary>同键同请求体且已有结果：直接重放（响应头 Idempotent-Replay: true）。</summary>
    Replay,
    /// <summary>同键不同请求体：409 IDEMPOTENCY_MISMATCH。</summary>
    Mismatch,
    /// <summary>同键同请求体但仍在执行：调用方等最多 5 s 后 409 IDEMPOTENCY_IN_PROGRESS。</summary>
    InProgress,
}

/// <summary>幂等表的一条：请求体哈希、状态、已完成时的响应。</summary>
public sealed class IdemEntry
{
    public string Key { get; init; } = "";
    public string BodyHash { get; init; } = "";
    public string OpId { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public volatile bool Done;
    public int Status;
    public string? Body;
    /// <summary>完成信号（InProgress 的等待方用）。</summary>
    public readonly System.Threading.Tasks.TaskCompletionSource Completed = new(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// 幂等表（设计稿 §13.3）：最近 <see cref="Ttl"/>（10 分钟）、最多 <see cref="Capacity"/>（1000）个 <c>(令牌, Idempotency-Key) → (请求体哈希, 状态, 响应)</c>。
/// 键按令牌隔离（<see cref="ScopedKey"/>）：另一令牌拿同一个键看不到、也占不住别人的条目。只在内存里（HMI 重启后丢失，客户端按 bootId 判断）。
/// 纯逻辑、线程安全、时间注入（测试直接传 now）。
/// </summary>
public sealed class IdempotencyStore
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    public const int Capacity = 1000;

    private readonly ConcurrentDictionary<string, IdemEntry> _m = new(StringComparer.Ordinal);
    private readonly object _trimLock = new();

    public int Count => _m.Count;

    /// <summary>表内键：令牌名 + 换行 + 客户端给的 Idempotency-Key（§13.3 按令牌隔离）。</summary>
    public static string ScopedKey(string tokenName, string key) => tokenName + "\n" + key;

    /// <summary>请求体哈希：方法 + 路径 + 正文的 SHA-256（同键不同路径也算 mismatch）。</summary>
    public static string Hash(string method, string path, string body)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n" + path + "\n" + body))).ToLowerInvariant();

    /// <summary>登记 / 查询。返回条目（New 时为新建的 InProgress 条目；Replay / InProgress 为已有条目；Mismatch 为已有条目）。</summary>
    public IdemBegin Begin(string key, string bodyHash, string opId, DateTime now, out IdemEntry entry)
    {
        Trim(now);
        while (true)
        {
            if (_m.TryGetValue(key, out var e))
            {
                if (now - e.CreatedAt > Ttl && _m.TryRemove(key, out _)) continue;   // 过期条目：当新键
                entry = e;
                if (e.BodyHash != bodyHash) return IdemBegin.Mismatch;
                return e.Done ? IdemBegin.Replay : IdemBegin.InProgress;
            }
            var fresh = new IdemEntry { Key = key, BodyHash = bodyHash, OpId = opId, CreatedAt = now };
            if (_m.TryAdd(key, fresh)) { entry = fresh; return IdemBegin.New; }
        }
    }

    /// <summary>命令完成：写入响应，唤醒等待方。OutcomeUnknown 的条目保持 InProgress，直到内层任务完成后再调用。</summary>
    public void Complete(IdemEntry entry, int status, string body)
    {
        entry.Status = status;
        entry.Body = body;
        entry.Done = true;
        entry.Completed.TrySetResult();
    }

    /// <summary>
    /// 命令没能登记结果（例如绑定失败、异常）：撤销条目，让同键重发得以执行。只应由新建该条目的那次请求调用（别人的 in-progress 条目不能被撤）。
    /// 同时唤醒正在等它的同键请求（它们看到取消后重新 Begin，而不是再等 5 s 后报 IN_PROGRESS）。
    /// </summary>
    public void Abandon(IdemEntry entry)
    {
        _m.TryRemove(new KeyValuePair<string, IdemEntry>(entry.Key, entry));
        entry.Completed.TrySetCanceled();
    }

    /// <summary>过期修剪 + 超容时淘汰最旧（每次最多扫一遍）。</summary>
    private void Trim(DateTime now)
    {
        if (_m.Count < Capacity) return;
        lock (_trimLock)
        {
            foreach (var kv in _m) if (now - kv.Value.CreatedAt > Ttl) _m.TryRemove(kv.Key, out _);
            if (_m.Count < Capacity) return;
            foreach (var kv in _m.OrderBy(kv => kv.Value.CreatedAt).Take(_m.Count - Capacity + 1))
                _m.TryRemove(kv.Key, out _);
        }
    }
}
