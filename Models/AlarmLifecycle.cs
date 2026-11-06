using System.Collections.Generic;

namespace ParallelReactor.Models;

/// <summary>
/// 报警条目的状态流转（纯逻辑，单元测试直接链接）：
/// <list type="bullet">
/// <item><c>active</c>（未处理）—条件消失→ <c>cleared</c>（已解除，未确认）；</item>
/// <item><c>active</c> —人工确认→ <c>ack</c>（已确认，条件仍在）；</item>
/// <item><c>ack</c> —条件消失→ <c>done</c>；<c>cleared</c> —人工确认→ <c>done</c>；</item>
/// <item><c>done</c>（含 info 事件）不再变化。</item>
/// </list>
/// 报警清除不等于通道状态复位：通道仍保持「报警」，需要操作员停止 / 复位。
/// </summary>
public static class AlarmLifecycle
{
    public const string Active = "active";
    public const string Ack = "ack";
    public const string Cleared = "cleared";
    public const string Done = "done";

    /// <summary>报警条件消失（超温回落 / 超压回落 / 通讯恢复）。</summary>
    public static string OnCleared(string stat) => stat switch
    {
        Active => Cleared,
        Ack => Done,
        _ => stat,
    };

    /// <summary>操作员确认。</summary>
    public static string OnAck(string stat) => stat switch
    {
        Active => Ack,
        Cleared => Done,
        _ => stat,
    };
}

/// <summary>
/// 按条件键（如 <c>ot:3</c> 超温、<c>op:3</c> 超压、<c>comm:0</c> 温控掉线）登记未解除的报警条目，
/// 条件消失时一次取出该键下的全部条目去清除。取出后键为空：再次清除是空操作，再次报警重新登记。
/// </summary>
public sealed class AlarmIndex<T>
{
    private readonly Dictionary<string, List<T>> _map = new();

    public void Raise(string key, T item)
    {
        if (!_map.TryGetValue(key, out var list)) _map[key] = list = new List<T>();
        list.Add(item);
    }

    /// <summary>该键下登记的条目数。</summary>
    public int Count(string key) => _map.TryGetValue(key, out var l) ? l.Count : 0;

    /// <summary>取出并移除该键下的全部条目（没有时返回空表）。</summary>
    public IReadOnlyList<T> TakeActive(string key)
    {
        if (!_map.Remove(key, out var list)) return System.Array.Empty<T>();
        return list;
    }
}
