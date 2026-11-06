using System;

namespace ParallelReactor.Models;

/// <summary>某个执行器（阀门 / 加热）在一次 <see cref="ActuatorRetry.Step"/> 里的判定。</summary>
public enum ActuatorStep
{
    None,       // 无动作
    Retry,      // 未确认 → 重发
    Confirmed,  // 刚确认（只在跳变时出现一次）
    Reopened,   // 已确认后又被打开 → 重发
}

/// <summary>
/// 单个执行器的「下发 → 确认 → 未确认重发 / 被重新打开复发」状态（纯逻辑），语义与 <see cref="OverTempGuard"/> 内部一致：
/// <list type="bullet">
/// <item>确认以 <c>done</c>（回读 / 界面阀态）为准；<c>done</c> 未知（null）时退而以写入回显（<see cref="Acked"/>）为准；</item>
/// <item>未确认、无在途写入、距上次下发 ≥ <see cref="OverTempGuard.RetryInterval"/> 时重发；</item>
/// <item>已确认后 <c>done == false</c>：按未确认处理，下一次允许下发时返回 <see cref="ActuatorStep.Reopened"/>。</item>
/// </list>
/// </summary>
public sealed class ActuatorRetry
{
    public bool InFlight { get; private set; }
    public bool Acked { get; private set; }
    public bool Confirmed { get; private set; }
    public int Sends { get; private set; }
    private DateTime _lastSend;
    private bool _reopenPending;

    /// <summary>当前在途写入的序号（每次下发 +1，Reset 也 +1）：结果回来时序号不符即为过期结果（解除前发出的那一帧），忽略。</summary>
    public int Seq { get; private set; }

    public void Reset()
    {
        Seq++;
        InFlight = false;
        Acked = false;
        Confirmed = false;
        Sends = 0;
        _reopenPending = false;
        _lastSend = default;
    }

    /// <summary>触发时立即下发（不受重发间隔限制）。</summary>
    public void SendNow(DateTime now)
    {
        Seq++;
        InFlight = true;
        _lastSend = now;
        Sends++;
    }

    /// <param name="done">执行器是否已处于安全态（回读）；未知时 null。</param>
    public ActuatorStep Step(bool? done, DateTime now)
    {
        if (!InFlight)
        {
            if (!Confirmed)
            {
                if (done == true || (done == null && Acked))
                {
                    Confirmed = true;
                    _reopenPending = false;
                    Sends = 0;
                    return ActuatorStep.Confirmed;
                }
            }
            else if (done == false)
            {
                Confirmed = false;
                Acked = false;
                _reopenPending = true;
            }
        }

        if (Confirmed || InFlight || now - _lastSend < OverTempGuard.RetryInterval) return ActuatorStep.None;

        if (_reopenPending)
        {
            _reopenPending = false;
            Sends = 0;
            SendNow(now);
            return ActuatorStep.Reopened;
        }
        SendNow(now);
        return ActuatorStep.Retry;
    }

    /// <summary>写入结果回来（ok = 写入回显正常）。active=false（已解除）时只清在途标记。
    /// seq：下发时的 <see cref="Seq"/>；与当前不符（解除 / 重新触发之后才回来的旧结果）时整体忽略，
    /// 不能让旧帧的回显确认新一次触发。null = 不校验（兼容）。</summary>
    public void OnResult(bool ok, bool active, int? seq = null)
    {
        if (seq is { } s && s != Seq) return;
        InFlight = false;
        if (active && ok) Acked = true;
    }
}

/// <summary>超压联锁在某个 Tick 对某通道的判定（可同时有多项）。</summary>
public struct OverPressureActions
{
    public bool Trip;             // 上升沿：刚超压 → 置报警 + 中止气路 + 关阀 + 停加热
    public bool Cleared;          // 压力回落到 限值 × 98% 以下 → 解除（重新布防）
    public bool SendValves;       // 本 Tick 需下发关阀帧（触发 / 重发 / 复关）
    public bool SendHeat;         // 本 Tick 需下发 At=4
    public bool ValvesRetry, HeatRetry;          // SendX 的原因是「未确认重发」
    public bool ValvesConfirmed, HeatConfirmed;  // 刚确认
    public bool ValvesReopened, HeatReopened;    // 已确认后又被打开 → 复关（SendX 同时为 true）

    public readonly bool Any => Trip || Cleared || SendValves || SendHeat || ValvesConfirmed || HeatConfirmed;
}

/// <summary>
/// 超压联锁判定（纯逻辑，无 UI / 总线依赖，单元测试直接链接）。每通道独立，与通道状态无关：
/// <list type="bullet">
/// <item>压力在线且 P ≥ 限值的<b>上升沿</b>：Trip，同时下发关阀（该通道 SV + 全部气源，排空保持）与停加热（<c>heatEnabled</c> 时）；</item>
/// <item>阀门「已确认」= 写入回显正常 且 界面阀态里该 SV 与三路气源均为关；之后界面又显示打开（序列 / 手动的一帧落在联锁之后）→ 复关；</item>
/// <item>加热「已确认」= 回读 At=4；回读未知时以写入回显为准；之后回读 At≠4 → 复切；</item>
/// <item>未确认时各执行器每 5 秒最多重发一帧，在途时不发（不刷总线）；连续 <see cref="OverTempGuard.EscalateAfterSends"/> 次未确认由调用方升级提示急停；</item>
/// <item>压力离线（读数是旧值）或 NaN：不触发、不下发、不解除，保持原状态；</item>
/// <item>P &lt; 限值 × <see cref="RearmRatio"/>：Cleared，重新布防。</item>
/// </list>
/// </summary>
public sealed class OverPressureGuard
{
    /// <summary>解除比例：压力回落到 限值 × 98% 以下才解除，避免在限值附近抖动反复触发。</summary>
    public const double RearmRatio = 0.98;

    private readonly bool[] _tripped;
    private readonly ActuatorRetry[] _valves, _heat;

    public OverPressureGuard(int channels = 8)
    {
        _tripped = new bool[channels];
        _valves = new ActuatorRetry[channels];
        _heat = new ActuatorRetry[channels];
        for (int i = 0; i < channels; i++)
        {
            _valves[i] = new ActuatorRetry();
            _heat[i] = new ActuatorRetry();
        }
    }

    public int Count => _tripped.Length;

    /// <summary>该通道（0 起）超压已触发且尚未解除。</summary>
    public bool IsTripped(int ch) => _tripped[ch];

    public bool IsValvesConfirmed(int ch) => _valves[ch].Confirmed;
    public bool IsHeatConfirmed(int ch) => _heat[ch].Confirmed;
    public bool ValvesInFlight(int ch) => _valves[ch].InFlight;
    public bool HeatInFlight(int ch) => _heat[ch].InFlight;

    /// <summary>当前关阀 / At=4 写入的序号：调用方在 Evaluate 返回 SendX 后立即读取，结果回来时原样传回 OnXResult。</summary>
    public int ValveSeq(int ch) => _valves[ch].Seq;
    public int HeatSeq(int ch) => _heat[ch].Seq;

    /// <summary>本次触发（或复关）以来已下发、仍未确认的关阀次数。</summary>
    public int ValveSends(int ch) => _valves[ch].Sends;

    /// <summary>本次触发（或复切）以来已下发、仍未确认的 At=4 次数。</summary>
    public int HeatSends(int ch) => _heat[ch].Sends;

    /// <param name="ch">通道下标（0 起）。</param>
    /// <param name="p">当前压力（与 limit 同单位）。</param>
    /// <param name="online">压力采集是否在线（离线时为旧值）。</param>
    /// <param name="limit">超压报警值。</param>
    /// <param name="now">当前时刻（可注入）。</param>
    /// <param name="valvesClosedShown">界面阀态（已确认写入的）里该通道 SV 与三路气源是否全关。</param>
    /// <param name="valvesEnabled">阀门模块已配置（未配置时不下发、不判阀门）。</param>
    /// <param name="heatStopped">回读该通道是否 At=4；未知（温控离线 / 未读到）时 null。</param>
    /// <param name="heatEnabled">超压时停止加热（设置开关 且 温控已配置）。</param>
    public OverPressureActions Evaluate(int ch, double p, bool online, double limit, DateTime now,
        bool valvesClosedShown, bool valvesEnabled, bool? heatStopped, bool heatEnabled)
    {
        var a = new OverPressureActions();
        if (!online || double.IsNaN(p)) return a;

        if (!_tripped[ch])
        {
            if (p < limit) return a;
            _tripped[ch] = true;
            _valves[ch].Reset();
            _heat[ch].Reset();
            a.Trip = true;
            if (valvesEnabled) { _valves[ch].SendNow(now); a.SendValves = true; }
            if (heatEnabled) { _heat[ch].SendNow(now); a.SendHeat = true; }
            return a;
        }

        if (p < limit * RearmRatio)
        {
            _tripped[ch] = false;
            _valves[ch].Reset();
            _heat[ch].Reset();
            a.Cleared = true;
            return a;
        }

        if (valvesEnabled)
        {
            // 阀门没有独立回读：界面显示打开 = 明确未关（false）；显示全关时以写入回显为准（null）
            switch (_valves[ch].Step(valvesClosedShown ? null : false, now))
            {
                case ActuatorStep.Confirmed: a.ValvesConfirmed = true; break;
                case ActuatorStep.Retry: a.SendValves = a.ValvesRetry = true; break;
                case ActuatorStep.Reopened: a.SendValves = a.ValvesReopened = true; break;
            }
        }
        if (heatEnabled)
        {
            switch (_heat[ch].Step(heatStopped, now))
            {
                case ActuatorStep.Confirmed: a.HeatConfirmed = true; break;
                case ActuatorStep.Retry: a.SendHeat = a.HeatRetry = true; break;
                case ActuatorStep.Reopened: a.SendHeat = a.HeatReopened = true; break;
            }
        }
        return a;
    }

    /// <summary>关阀写入结果回来（ok = 写入回显正常）。已解除的通道忽略迟到的结果；
    /// seq 与当前不符（解除后又重新触发，回来的是上一次触发的帧）也忽略。</summary>
    public void OnValveResult(int ch, bool ok, int? seq = null) => _valves[ch].OnResult(ok, _tripped[ch], seq);

    /// <summary>At=4 写入结果回来。已解除 / 序号不符的迟到结果忽略。</summary>
    public void OnHeatResult(int ch, bool ok, int? seq = null) => _heat[ch].OnResult(ok, _tripped[ch], seq);
}
