using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Models;

/// <summary>
/// 气路占用者（C8）。超压联锁与急停不是占用者：它们无条件中止任何占用者（MainViewModel.AbortAllGas）。
/// </summary>
public enum GasOwner { None, Program, Leak }

/// <summary>
/// 气路占用登记（C8 / C9，纯逻辑，只在 UI 线程使用）：
/// <list type="bullet">
/// <item>谁占用（Owner）、做什么（Task）、涉及哪些通道（Participants：会开 SV 或程序所在通道）、监视哪些通道超压（Watch）；</item>
/// <item>按占用者中止 / 释放：泄漏测试的「中止」不能停掉程序的升压，反之亦然（<see cref="CanAbort"/> / <see cref="TryEnd"/>）；</item>
/// <item>序列执行中某参与通道被停止 / 停用 / 报警时，周期安全检查（ThrowIfGasUnsafe）据 <see cref="FaultReason"/> 中止序列。</item>
/// </list>
/// </summary>
public sealed class GasLease
{
    private readonly HashSet<int> _invalid = new();
    private IReadOnlyDictionary<int, ReactorState> _start = new Dictionary<int, ReactorState>();

    public GasOwner Owner { get; private set; } = GasOwner.None;
    public string Task { get; private set; } = "";
    public IReadOnlyList<int> Participants { get; private set; } = Array.Empty<int>();
    public int[] Watch { get; private set; } = Array.Empty<int>();
    /// <summary>每次 <see cref="Begin"/> 递增（同一占用者先后两段租约可区分）。</summary>
    public long Generation { get; private set; }
    public bool Busy => Owner != GasOwner.None;

    /// <summary>
    /// 占用气路（覆盖上一段租约；调用方保证同一时间只有一个占用者，或同一占用者续租，如升压 → 稳压）。
    /// startStates：各参与通道开始时的状态，用于判断「之后被停止 / 停用」。返回本段租约的代号。
    /// 新占用者：清除失效标记。同一占用者续租：延续下来的参与通道保留失效标记与开始状态——
    /// 否则在续租那一刻被停止 / 停用的通道会被当成「开始时就已结束」而不再中止序列。
    /// </summary>
    public long Begin(GasOwner owner, string task, IEnumerable<int>? participants, IEnumerable<int>? watch,
        IReadOnlyDictionary<int, ReactorState>? startStates)
    {
        if (owner == GasOwner.None) throw new ArgumentException("占用者不能为 None", nameof(owner));
        bool renew = owner == Owner;
        var ids = (participants ?? Array.Empty<int>()).Distinct().ToArray();
        var start = startStates != null ? new Dictionary<int, ReactorState>(startStates) : new Dictionary<int, ReactorState>();
        if (renew)
        {
            foreach (var id in ids)
                if (_start.TryGetValue(id, out var s0)) start[id] = s0;
            _invalid.IntersectWith(ids);
        }
        else _invalid.Clear();
        Owner = owner;
        Task = task ?? "";
        Participants = ids;
        Watch = (watch ?? Array.Empty<int>()).Distinct().ToArray();
        _start = start;
        return ++Generation;
    }

    /// <summary>请求方能否按自己的身份中止当前序列：只有当前占用者本人可以（None 永远不行）。</summary>
    public bool CanAbort(GasOwner requester) => requester != GasOwner.None && requester == Owner;

    /// <summary>
    /// 释放气路：占用者一致（且 gen 为 0 或与当前租约一致）才释放，返回是否释放。
    /// 占用者不一致时什么都不做——避免程序收尾释放掉泄漏测试的占用。
    /// </summary>
    public bool TryEnd(GasOwner owner, long gen = 0)
    {
        if (owner == GasOwner.None || owner != Owner) return false;
        if (gen != 0 && gen != Generation) return false;
        Owner = GasOwner.None;
        Task = "";
        Participants = Array.Empty<int>();
        Watch = Array.Empty<int>();
        _start = new Dictionary<int, ReactorState>();
        _invalid.Clear();
        return true;
    }

    /// <summary>无条件释放（急停 / 超压联锁之后兜底、或测试用）。</summary>
    public void Clear() => TryEnd(Owner);

    /// <summary>通道是否参与当前序列。</summary>
    public bool Includes(int id) => Busy && Participants.Contains(id);

    /// <summary>参与通道被抽屉「停止 / 停用」：标记失效（下一次安全检查即中止序列）。不参与的通道忽略。</summary>
    public void Invalidate(int id)
    {
        if (Includes(id)) _invalid.Add(id);
    }

    public bool IsInvalidated(int id) => _invalid.Contains(id);

    /// <summary>
    /// 序列是否必须中止：任一参与通道已被标记失效、处于报警，或开始后转入了「停用 / 已结束」
    /// （开始时就是已结束的通道保持已结束不算——例如在已结束通道上做泄漏测试）。返回 null = 安全。
    /// </summary>
    public string? FaultReason(Func<int, ReactorState?> stateOf)
    {
        if (!Busy) return null;
        foreach (var id in Participants)
        {
            if (_invalid.Contains(id)) return $"RV{id} 已被停止 / 停用，序列中止";
            var st = stateOf(id);
            if (st == null) continue;
            if (st == ReactorState.Alarm) return $"RV{id} 报警，序列中止";
            if (st is ReactorState.Idle or ReactorState.Done
                && (!_start.TryGetValue(id, out var s0) || s0 != st))
                return st == ReactorState.Idle ? $"RV{id} 已被停用，序列中止" : $"RV{id} 已被停止，序列中止";
        }
        return null;
    }
}
