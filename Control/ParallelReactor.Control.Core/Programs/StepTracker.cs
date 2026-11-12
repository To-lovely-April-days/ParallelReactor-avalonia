using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Api;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>
/// 观察到的一次程序执行（HMI 的 channels[i].program 的一个 startedAt）：反应釜号、HMI 启动号、开始时刻（UTC，设备时钟；= 这次执行的身份）、步骤数、由谁启动，
/// 以及本机看到的每一步的开始时刻（<see cref="Starts"/>，下标 = 步骤序号；null = 不知道：PC 中途连上、断线期间换的步）、
/// 第一次看到这一步的时刻（<see cref="FirstSeen"/>，开始时刻不知道时它是开始时刻的上界）、当前步、HMI 的阶段文字、结束时刻（知道时）、最后一次看到的时刻。
/// 不可变：每次变化整条换掉。
/// </summary>
public sealed record RunTrack
{
    public int N { get; init; }
    public string BootId { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public int StepCount { get; init; }
    public string? StartedBy { get; init; }
    /// <summary>当前步（0 起；准备中为 −1）。</summary>
    public int Index { get; init; } = -1;
    /// <summary>HMI 的 program.state：preparing / running / waitingGas / finishing / completed / failed / aborted。</summary>
    public string State { get; init; } = "";
    public IReadOnlyList<DateTime?> Starts { get; init; } = Array.Empty<DateTime?>();
    public IReadOnlyList<DateTime?> FirstSeen { get; init; } = Array.Empty<DateTime?>();
    /// <summary>结束（completed / failed / aborted）的时刻；不知道（断线期间结束的）为 null。</summary>
    public DateTime? EndedAt { get; init; }
    public DateTime LastSeen { get; init; }

    /// <summary>已经结束（completed / failed / aborted）。</summary>
    public bool Ended => State is ChannelProgramStates.Completed or ChannelProgramStates.Failed or ChannelProgramStates.Aborted;

    /// <summary>第 i 步的开始时刻（知道的）；越界 / 不知道为 null。</summary>
    public DateTime? StartOf(int i) => i >= 0 && i < Starts.Count ? Starts[i] : null;

    /// <summary>第 i 步第一次被看到的时刻；越界 / 没看到为 null。</summary>
    public DateTime? FirstSeenOf(int i) => i >= 0 && i < FirstSeen.Count ? FirstSeen[i] : null;
}

/// <summary>
/// 步骤计时（PORT-PLAN §3.2 第 5 条）：HMI 不给每一步的开始时刻（A1 补上之前），PC 在线时看 channels[i].program 的 stepIndex 怎么变，自己记。
/// <list type="bullet">
/// <item>一次执行的身份 = (启动号, 反应釜, program.startedAt)。startedAt 变了就是新的一次执行（同一个程序再跑一遍也是）。</item>
/// <item>第 0 步的开始 = startedAt（HMI 建运行对象后立刻开始第 0 步，ProgramRun.MarkStepStarted(0)）。</item>
/// <item>连续观察（相邻两帧相隔 ≤ <see cref="MaxGapSeconds"/>）时看到 stepIndex 前进：新一步的开始 = 两帧时刻的中点（误差 ≤ 半帧，约 0.6 s）；
///   一下跳过好几步时，中间那几步的开始不知道（null）。结束（completed / failed / aborted）同样取中点。</item>
/// <item>不连续（中途连上、断线后重连、PC 重启后从台账恢复）时看到的变化：开始时刻不知道（null），只记「第一次看到」。</item>
/// <item>HMI 重启（启动号变了）：之前的执行全部作废。</item>
/// </list>
/// 时刻一律是设备时钟的 UTC（帧的 Ts、program.startedAt）。不是线程安全的：在 UI 线程上用。
/// </summary>
public sealed class StepTracker
{
    private readonly Dictionary<int, RunTrack> _runs = new();
    private readonly Dictionary<int, DateTime> _seen = new();

    public StepTracker(double maxGapSeconds = 10) => MaxGapSeconds = maxGapSeconds > 0 ? maxGapSeconds : 10;

    /// <summary>相邻两次观察相隔不超过这么多秒才算连续（默认 10 s；HMI 约 1.2 s 一帧）。</summary>
    public double MaxGapSeconds { get; }

    /// <summary>当前的启动号（最后一次观察时的）。</summary>
    public string? BootId { get; private set; }

    /// <summary>每次记下的内容变了（新的执行 / 换步 / 结束 / 重启）加一；只是「最后看到的时刻」变了不算。</summary>
    public int Version { get; private set; }

    /// <summary>每个反应釜最近一次执行。</summary>
    public IReadOnlyCollection<RunTrack> Runs => _runs.Values;

    /// <summary>第 n 路最近一次执行；没有为 null。</summary>
    public RunTrack? Track(int n) => _runs.TryGetValue(n, out var r) ? r : null;

    /// <summary>第 n 路正在看的执行就是 HMI 报的这一次（启动号、开始时刻都对得上）。</summary>
    public RunTrack? TrackOf(int n, ChannelProgramSnap? p, string? bootId) =>
        p?.StartedAt is { } s && _runs.TryGetValue(n, out var r) && r.BootId == bootId && r.StartedAt == Utc(s) ? r : null;

    /// <summary>从台账恢复（PC 重启后）：之前记下的执行。<see cref="RunTrack.LastSeen"/> 是保存时的，第一帧一定算不连续。</summary>
    public void Restore(IEnumerable<RunTrack> runs, string? bootId)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs.Clear();
        _seen.Clear();
        BootId = bootId;
        foreach (var r in runs) if (r is not null && (bootId is null || r.BootId == bootId)) _runs[r.N] = r;
        Version++;
    }

    /// <summary>HMI 重启了（或换了设备）：之前的执行全部作废。返回是否有变化。</summary>
    public bool Reset(string? bootId)
    {
        bool changed = _runs.Count > 0 || BootId != bootId;
        _runs.Clear();
        _seen.Clear();
        BootId = bootId;
        if (changed) Version++;
        return changed;
    }

    /// <summary>一帧：每个通道的 program 各看一次。返回记下的内容有没有变化（台账据此决定要不要保存）。</summary>
    public bool Observe(string bootId, TelemetrySnapshot frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool changed = false;
        if (bootId != BootId) changed |= Reset(bootId);
        if (frame.Channels is null) return changed;
        foreach (var ch in frame.Channels)
            if (ch is not null) changed |= Observe(bootId, frame.Ts, ch.Id, ch.Program);
        return changed;
    }

    /// <summary>一个通道在 <paramref name="ts"/>（帧的时刻，UTC）的 program。没有 program / 没有开始时刻时什么都不记。返回记下的内容有没有变化。</summary>
    public bool Observe(string bootId, DateTime ts, int n, ChannelProgramSnap? p)
    {
        bool changed = false;
        if (bootId != BootId) changed |= Reset(bootId);
        if (p?.StartedAt is not { } s0) return changed;
        var t = Utc(ts);
        var startedAt = Utc(s0);
        int count = Math.Max(0, p.StepCount);
        int idx = Math.Min(p.StepIndex, count - 1);
        string state = p.State ?? "";
        if (!_runs.TryGetValue(n, out var r) || r.BootId != bootId || r.StartedAt != startedAt)
        {
            // 新的一次执行：第 0 步的开始 = startedAt；PC 中途连上时后面的步骤不知道，只记「第一次看到」
            var starts = new DateTime?[count];
            var seen = new DateTime?[count];
            if (count > 0) starts[0] = startedAt;
            if (idx >= 0) seen[idx] = t;
            _runs[n] = new RunTrack
            {
                N = n, BootId = bootId, StartedAt = startedAt, StepCount = count, StartedBy = p.StartedBy, Index = idx, State = state,
                Starts = starts, FirstSeen = seen, EndedAt = null, LastSeen = t,
            };
            _seen[n] = t;
            Version++;
            return true;
        }
        var last = _seen.TryGetValue(n, out var ls) ? ls : r.LastSeen;
        _seen[n] = t > last ? t : last;
        bool continuous = t >= last && (t - last).TotalSeconds <= MaxGapSeconds;
        var mid = last + TimeSpan.FromTicks(Math.Max(0, (t - last).Ticks) / 2);
        var next = r;
        if (idx > r.Index && idx >= 0)
        {
            var starts = r.Starts.ToArray();
            var seen = r.FirstSeen.ToArray();
            if (starts.Length < count) { Array.Resize(ref starts, count); Array.Resize(ref seen, count); }
            // 只有紧接着的下一步、而且是连续观察时才知道开始时刻；一下跳过的几步不知道。第 0 步的开始始终是 startedAt（准备中 → 第 0 步）
            if (continuous && idx == r.Index + 1 && idx > 0) starts[idx] = mid;
            seen[idx] ??= t;
            next = next with { Index = idx, Starts = starts, FirstSeen = seen };
        }
        if (state != r.State)
        {
            next = next with { State = state };
            if (IsEnd(state) && !r.Ended) next = next with { EndedAt = continuous ? mid : null };
        }
        if (ReferenceEquals(next, r)) return changed;
        _runs[n] = next with { LastSeen = _seen[n] };
        Version++;
        return true;
    }

    /// <summary>第 n 路最后一次看到 program 的时刻（UTC）；没看到为 null。</summary>
    public DateTime? LastSeenOf(int n) => _seen.TryGetValue(n, out var t) ? t : _runs.TryGetValue(n, out var r) ? r.LastSeen : null;

    private static bool IsEnd(string? state) => state is ChannelProgramStates.Completed or ChannelProgramStates.Failed or ChannelProgramStates.Aborted;

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
