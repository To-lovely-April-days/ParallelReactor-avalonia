using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Models;

/// <summary>
/// 全机共用一台搅拌器的程序引用计数（纯逻辑，只在 UI 线程使用）：每个程序的搅拌步骤登记自己的转速请求，
/// 步骤结束 / 程序中止时释放；最后一个释放者才真正停搅拌。登记时若改写了另一程序的转速，返回被改写者供 warn 事件。
/// 持有者列表按<b>最近登记</b>排序（重复登记移到末尾）：释放最近登记者后，全局转速应恢复为上一位登记者的请求值（<see cref="Current"/>）。
/// 操作员手动开停搅拌不经这里（操作员意志优先）。
/// </summary>
public sealed class StirShare
{
    private readonly List<(int Rv, int Rpm)> _holders = new();

    /// <summary>
    /// 登记 rv 的转速请求（已登记则更新并移到末尾）。返回**此前设定全局转速的另一程序**（转速不同才非 null）——
    /// 即本次请求改写了谁的转速；同转速或没有其他持有者返回 null。
    /// </summary>
    public (int Rv, int Rpm)? Request(int rv, int rpm)
    {
        (int Rv, int Rpm)? prev = LastRpm is { } last && last != rpm && _lastSetter is { } ls && ls != rv ? (ls, last) : null;
        _holders.RemoveAll(h => h.Rv == rv);
        _holders.Add((rv, rpm));
        LastRpm = rpm;
        _lastSetter = rv;
        return prev;
    }

    private int? _lastSetter;

    /// <summary>最近一次经程序设定的全局转速（释放最近设定者后回退为剩余持有者中最近登记者的转速）。</summary>
    public int? LastRpm { get; private set; }

    /// <summary>剩余持有者中最近登记的那一位（通道, 转速）；没有持有者为 null。释放后全局转速应以它为准。</summary>
    public (int Rv, int Rpm)? Current => _holders.Count > 0 ? _holders[^1] : null;

    /// <summary>
    /// 释放 rv 的请求。返回是否仍有其他持有者（true = 不要停搅拌）。未登记的 rv 也按当前持有者判断。
    /// 被释放者正是最近设定者时，「最近设定」回退为剩余持有者中最近登记的那一位（含其转速），避免 warn 事件张冠李戴。
    /// </summary>
    public bool Release(int rv)
    {
        _holders.RemoveAll(h => h.Rv == rv);
        if (_lastSetter == rv)
        {
            if (_holders.Count > 0) { _lastSetter = _holders[^1].Rv; LastRpm = _holders[^1].Rpm; }
            else { _lastSetter = null; LastRpm = null; }
        }
        return _holders.Count > 0;
    }

    public bool Holds(int rv) => _holders.Any(h => h.Rv == rv);
    public int Count => _holders.Count;
    public IReadOnlyList<int> Holders => _holders.Select(h => h.Rv).ToList();

    /// <summary>全部停止 / 急停：清空全部登记。</summary>
    public void Clear()
    {
        _holders.Clear();
        _lastSetter = null;
        LastRpm = null;
    }
}
