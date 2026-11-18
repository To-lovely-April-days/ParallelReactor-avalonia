using System;

namespace ParallelReactor.Control.Core.Unattended;

/// <summary>
/// 操作系统的「别睡眠」（融合版 F8 无人值守）：Windows = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)，
/// macOS = caffeinate -i -w &lt;本进程&gt;（App 的 Services/Unattended）。无头 / 截图 / 演示用模拟的（不调系统），测试用假的。
/// </summary>
public interface ISleepInhibitor
{
    /// <summary>这个实现叫什么（日志 / 设置页的说明：「Windows 电源请求」「macOS caffeinate」「模拟」）。</summary>
    string Name { get; }

    /// <summary>开始阻止系统睡眠；成功返回 true。失败不抛（返回 false，原因见 <see cref="LastError"/>）。</summary>
    bool Acquire();

    /// <summary>不再阻止（恢复系统设置）；没有持有时什么都不做。不抛。</summary>
    void Release();

    /// <summary>最近一次失败的原因（没有为 null）。</summary>
    string? LastError { get; }
}

/// <summary>
/// 运行中阻止电脑睡眠的决定（融合版 F8，样稿 SETP.nosleep）：设置开着、并且有设备在运行时持有 <see cref="ISleepInhibitor"/>；否则释放。
/// <list type="bullet">
/// <item>每次刷新调 <see cref="Update"/>（外壳每秒一次）：只在要的状态变了时才调系统（不每秒重复申请）。</item>
/// <item>申请失败：记下原因，<see cref="RetryAfter"/> 之后再试（不每秒都试一次）；状态栏只在<b>真的</b>持有时写「运行中 · 已阻止睡眠」（<see cref="Held"/>）。</item>
/// <item>退出 / 释放：<see cref="Dispose"/> 一定释放。</item>
/// </list>
/// 纯逻辑（时钟可换），在 UI 线程上用。
/// </summary>
public sealed class SleepGuard : IDisposable
{
    /// <summary>申请失败后多久再试。</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    private readonly ISleepInhibitor _os;
    private readonly Func<DateTime> _now;
    private DateTime? _failedAt;

    public SleepGuard(ISleepInhibitor os, Func<DateTime>? now = null)
    {
        _os = os ?? throw new ArgumentNullException(nameof(os));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>现在真的在阻止睡眠（系统接受了申请）。</summary>
    public bool Held { get; private set; }

    /// <summary>想要阻止（设置开 + 有设备在运行），不管系统接没接受。</summary>
    public bool Wanted { get; private set; }

    /// <summary>最近一次申请失败的原因（成功 / 不需要时为 null）。</summary>
    public string? Error { get; private set; }

    /// <summary>实现的名字（见 <see cref="ISleepInhibitor.Name"/>）。</summary>
    public string Mechanism => _os.Name;

    /// <summary>
    /// 按设置和运行状态申请 / 释放。返回 <see cref="Held"/> 是否变了（状态栏跟着刷新）。
    /// </summary>
    public bool Update(bool enabled, bool anyRunning)
    {
        bool before = Held;
        Wanted = enabled && anyRunning;
        if (Wanted && !Held)
        {
            var now = _now();
            if (_failedAt is { } f && now - f < RetryAfter) return false;
            if (_os.Acquire()) { Held = true; Error = null; _failedAt = null; }
            else { _failedAt = now; Error = _os.LastError ?? "系统没有接受阻止睡眠的请求"; }
        }
        else if (!Wanted)
        {
            if (Held) _os.Release();
            Held = false;
            _failedAt = null;
            Error = null;
        }
        return before != Held;
    }

    public void Dispose()
    {
        if (Held) _os.Release();
        Held = false;
        Wanted = false;
    }
}
