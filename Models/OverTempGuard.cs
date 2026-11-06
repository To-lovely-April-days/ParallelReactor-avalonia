using System;

namespace ParallelReactor.Models;

/// <summary>超温保护在某个 Tick 对某通道的判定结果。</summary>
public enum OverTempAction
{
    None,       // 无动作
    TripSend,   // 上升沿：刚超温 → 记事件 + 置报警 + 下发切断
    RetrySend,  // 已超温、切断未确认 → 重发切断
    Confirmed,  // 切断已确认（轮询回读 At=4）：只在跳变时出现一次
    Reopened,   // 已确认后回读显示输出又被打开（At≠4）→ 重新置报警 + 记事件 + 重发切断
    Cleared,    // 温度回落到 限值 − 回差 以下 → 解除（重新布防）
}

/// <summary>
/// 超温切断判定（纯逻辑，无 UI / 总线依赖，单元测试直接链接）。每通道独立：
/// <list type="bullet">
/// <item>温控在线且 PV ≥ 限值的<b>上升沿</b>：<see cref="OverTempAction.TripSend"/>；</item>
/// <item>「已确认」以轮询<b>回读</b>的 At = 4 为准，不以写入回显为准（回显只说明帧收到了，不说明输出真的停了）。
///       回读模式未知（null）时退而以写入回显为准；</item>
/// <item>已触发、未确认、没有在途写入、距上次下发 ≥ <see cref="RetryInterval"/>：<see cref="OverTempAction.RetrySend"/>。
///       只要尚未解除（PV ≥ 限值 − 回差）就一直重发——首次写入失败后 PV 落到回差区内时输出状态未知，不能停发；
///       不刷总线：每 5 秒最多一帧；</item>
/// <item>已确认后回读 At ≠ 4（输出被重新启动 / 写入实际没生效）：按未确认处理，下一次允许下发时返回 <see cref="OverTempAction.Reopened"/>。
///       回读是确认之后才开始的轮询（轮询串行），不会是写入之前的旧值；</item>
/// <item>温控离线（PV 是旧值）：不触发、不下发，也不解除——保持原状态，等通讯恢复再判；</item>
/// <item>PV &lt; 限值 − <see cref="Hysteresis"/>：<see cref="OverTempAction.Cleared"/>，重新布防。</item>
/// </list>
/// 与通道状态无关：停用 / 已结束 / 自整定中的通道同样判定（加热器可能仍在出力）。
/// </summary>
public sealed class OverTempGuard
{
    /// <summary>未确认时的重发间隔。</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>解除回差 ℃：PV 回落到 限值 − 回差 以下才解除，避免在限值附近抖动反复触发。</summary>
    public const double Hysteresis = 2.0;

    /// <summary>连续下发这么多次仍未确认时，调用方应升级报警（提示急停）。</summary>
    public const int EscalateAfterSends = 3;

    private readonly bool[] _tripped, _confirmed, _inFlight, _acked, _reopenPending;
    private readonly int[] _sends;
    private readonly DateTime[] _lastSend;

    public OverTempGuard(int channels = 8)
    {
        _tripped = new bool[channels];
        _confirmed = new bool[channels];
        _inFlight = new bool[channels];
        _acked = new bool[channels];
        _reopenPending = new bool[channels];
        _sends = new int[channels];
        _lastSend = new DateTime[channels];
    }

    public int Count => _tripped.Length;

    /// <summary>该通道（0 起）超温已触发且尚未解除。</summary>
    public bool IsTripped(int ch) => _tripped[ch];

    /// <summary>该通道切断已确认（回读 At=4）。</summary>
    public bool IsConfirmed(int ch) => _confirmed[ch];

    /// <summary>该通道是否有在途的切断写入。</summary>
    public bool InFlight(int ch) => _inFlight[ch];

    /// <summary>本次触发（或复切）以来已下发、仍未确认的切断次数。</summary>
    public int UnconfirmedSends(int ch) => _sends[ch];

    /// <param name="ch">通道下标（0 起）。</param>
    /// <param name="pv">当前 PV ℃。</param>
    /// <param name="online">温控是否在线（离线时 PV 为旧值）。</param>
    /// <param name="limit">超温限值 ℃。</param>
    /// <param name="now">当前时刻（可注入，便于测试）。</param>
    /// <param name="outputStopped">轮询回读的模式是否为停止（At=4）；未知时 null。</param>
    public OverTempAction Evaluate(int ch, double pv, bool online, double limit, DateTime now, bool? outputStopped = null)
    {
        if (!online || double.IsNaN(pv)) return OverTempAction.None;

        if (!_tripped[ch])
        {
            if (pv < limit) return OverTempAction.None;
            _tripped[ch] = true;
            _confirmed[ch] = false;
            _acked[ch] = false;
            _reopenPending[ch] = false;
            return Send(ch, now, OverTempAction.TripSend);
        }

        if (pv < limit - Hysteresis)
        {
            _tripped[ch] = false;
            _confirmed[ch] = false;
            _inFlight[ch] = false;
            _acked[ch] = false;
            _reopenPending[ch] = false;
            _sends[ch] = 0;
            return OverTempAction.Cleared;
        }

        if (!_inFlight[ch])
        {
            if (!_confirmed[ch])
            {
                if (outputStopped == true || (outputStopped == null && _acked[ch]))
                {
                    _confirmed[ch] = true;
                    _reopenPending[ch] = false;
                    _sends[ch] = 0;
                    return OverTempAction.Confirmed;
                }
            }
            else if (outputStopped == false)
            {
                // 已确认后回读显示输出没停（有人重新启动了加热 / 写入实际未生效）：重新切断
                _confirmed[ch] = false;
                _acked[ch] = false;
                _reopenPending[ch] = true;
            }
        }

        if (_confirmed[ch] || _inFlight[ch] || now - _lastSend[ch] < RetryInterval) return OverTempAction.None;

        if (_reopenPending[ch])
        {
            _reopenPending[ch] = false;
            _sends[ch] = 0;
            return Send(ch, now, OverTempAction.Reopened);
        }
        return Send(ch, now, OverTempAction.RetrySend);
    }

    private OverTempAction Send(int ch, DateTime now, OverTempAction a)
    {
        _inFlight[ch] = true;
        _lastSend[ch] = now;
        _sends[ch]++;
        return a;
    }

    /// <summary>切断写入的结果回来时调用（ok = 写入回显正常）。已解除的通道忽略迟到的结果。
    /// 写入成功不等于已确认：确认以之后的轮询回读 At=4 为准。</summary>
    public void OnStopResult(int ch, bool ok, DateTime now)
    {
        _inFlight[ch] = false;
        if (!_tripped[ch]) return;
        if (ok) _acked[ch] = true;
    }
}
