using System;
using System.Collections.Generic;

namespace ParallelReactor.Control.Core.Unattended;

/// <summary>
/// 报警声音（融合版 F8）：放一声短的系统提示音。Windows = winmm PlaySound（系统「感叹号」提示音，柔和的一声；没有时系统默认提示音），
/// macOS = afplay 一个系统声音（App 的 Services/Unattended）。无头 / 截图 / 测试不出声（空实现或假的）。
/// </summary>
public interface IAlarmSound
{
    /// <summary>放一声（异步、不等放完、不抛）。</summary>
    void Play();
}

/// <summary>
/// 什么时候响（样稿「有未确认报警时每 30 s 响一次」）：声音开着、并且有未确认的<b>报警级</b>报警（提醒不响）时 ——
/// 新出现一条（以前没响过的键）马上响；之后每 <see cref="Interval"/> 再响一次，直到全部确认 / 解除。
/// 纯逻辑：外壳每秒调一次 <see cref="Tick"/>（时刻由调用方给，测试用假时钟）。
/// </summary>
public sealed class AlarmBeeper
{
    /// <summary>重复的间隔。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IAlarmSound _sound;
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private DateTime? _last;

    public AlarmBeeper(IAlarmSound sound) => _sound = sound ?? throw new ArgumentNullException(nameof(sound));

    /// <summary>响过几次（测试 / 诊断）。</summary>
    public int Count { get; private set; }

    /// <summary>
    /// <paramref name="unacked"/> = 此刻未确认、未解除的报警级报警的键（「设备:报警 id」）。返回这次有没有响。
    /// 声音关着时不响，但照样记下已有的键（重新打开时不把旧报警当新的立刻响 —— 只按 30 s 的节奏）。
    /// </summary>
    public bool Tick(DateTime now, bool enabled, IReadOnlyCollection<string> unacked)
    {
        ArgumentNullException.ThrowIfNull(unacked);
        if (unacked.Count == 0)
        {
            _known.Clear();
            _last = null;
            return false;
        }
        bool fresh = false;
        foreach (var k in unacked) if (_known.Add(k)) fresh = true;
        // 已经不在的键去掉（确认 / 解除之后再出现算新的）
        if (_known.Count > unacked.Count)
        {
            var keep = new HashSet<string>(unacked, StringComparer.Ordinal);
            _known.RemoveWhere(k => !keep.Contains(k));
        }
        if (!enabled) { if (fresh) _last ??= now; return false; }
        bool due = fresh || _last is null || now - _last.Value >= Interval || now < _last.Value;
        if (!due) return false;
        _last = now;
        Count++;
        _sound.Play();
        return true;
    }

    /// <summary>忘掉已有的报警（换数据源 / 测试）。</summary>
    public void Reset()
    {
        _known.Clear();
        _last = null;
    }
}
