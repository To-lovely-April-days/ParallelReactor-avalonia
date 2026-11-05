using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ParallelReactor.Models;

/// <summary>定值加热防超调逼近的可调参数（来自 <see cref="AppSettings"/>）。</summary>
/// <param name="Delta">预留温差 Δ（℃）：先升到 目标−Δ。须大于实测最大冲温；≤0 关闭逼近（直接写目标）。</param>
/// <param name="Rate">末段慢推速率（℃/min），离目标 ≤ <paramref name="FinalZone"/> 时使用。</param>
/// <param name="SettleBand">判稳范围（±℃）：PV 持续落在 中间SP±此值 内即可开始逼近（判峰的兜底）。</param>
/// <param name="SettleSec">判稳时长（秒）。</param>
/// <param name="FastRate">快段逼近速率（℃/min），离目标 &gt; <paramref name="FinalZone"/> 时使用。</param>
/// <param name="FinalZone">末段范围（℃）：只有最后这一段才慢推、并在 PV 偏高时暂停。</param>
public readonly record struct ApproachParams(double Delta, double Rate, double SettleBand, double SettleSec,
    double FastRate, double FinalZone);

public enum ApproachPhase
{
    Off,      // 未运行（未启动加热 / 已停止 / 曲线模式）
    Settle,   // SP=目标−Δ，满功率冲到安全位置并等待稳定
    Creep,    // 从中间 SP 小步慢推到目标
    Hold,     // SP=目标，恒温
}

public enum ApproachEvent
{
    None,
    PreheatDone, // 预热段结束（判峰或判稳），可读 PreheatOvershoot 得到本次实测冲温
    ExoPause,    // 逼近中检测到放热（输出≈0 仍在升温）→ 暂停上推
    OverLimit,   // PV > 目标+0.1 且输出≈0 持续一段时间（无冷却，软件已无法压回）
}

/// <summary>
/// 定值加热防超调逼近（每个反应釜一份，由 MainViewModel.Tick 周期推进）。
/// <para>
/// 宇电 APID 在 SP 附近小偏差调节可稳在 ±0.1℃，超调只来自大阶跃（长时间满功率 → 加热套蓄热 + 积分累积）。
/// 因此不改 PID，只控制下发的 SP：先写 目标−Δ 让它冲（冲温到不了目标），PV 过峰（不再上升）后从当前 PV 起步，
/// 离目标较远时快推、PV 领先就跟随；只在最后 FinalZone℃ 内慢推——每一步都是仪表擅长的小偏差工况。
/// </para>
/// 无冷却手段：末段 PV 高于当前 SP、SP 超前 PV 过多、或判定放热时暂停上推；放热超出控温能力时只能提示。
/// </summary>
public partial class HeatApproach : ObservableObject
{
    // —— 内置常数（现场一般无需调）——
    public const double PauseMargin = 0.05;     // PV 高于当前 SP 超过此值 → 暂停上推
    public const double MaxLead = 0.5;          // SP 超前 PV 超过此值 → 暂停上推（保证始终是小偏差）
    public const double PeakSlope = 0.02;       // 预热段 PV 斜率 ≤ 此值（℃/min）视为已过峰
    public const double ExoOutPct = 2.0;        // 输出 ≤ 此值视为"加热已关"
    public const double ExoRisePerMin = 0.05;   // 输出已关时 PV 仍以 ≥ 此斜率上升 → 判为放热
    public const double OverLimit = 0.1;        // 不允许超过 目标 + 此值
    public static readonly TimeSpan OverLimitHold = TimeSpan.FromSeconds(10);   // 超限持续此时长才上报（滤读数抖动）
    private static readonly TimeSpan SlopeWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxStepGap = TimeSpan.FromSeconds(5);      // 单周期最多推进 5s（防卡顿后跳变）
    private static readonly TimeSpan ForceRewriteEvery = TimeSpan.FromSeconds(30);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Active))]
    private ApproachPhase _phase = ApproachPhase.Off;

    [ObservableProperty] private string _statusText = "";

    public bool Active => Phase != ApproachPhase.Off;

    /// <summary>最终目标温度。</summary>
    public double Target { get; private set; }

    /// <summary>逼近算出的当前给定值（未取整）。</summary>
    public double CurrentSp { get; private set; }

    /// <summary>应下发到仪表的 SP（寄存器分辨率 0.1℃，向下取整避免先写高）。</summary>
    public double SpToWrite => Phase == ApproachPhase.Hold ? Target : Floor1(CurrentSp);

    /// <summary>串口写入进行中（避免慢串口下同一通道写请求堆叠）。</summary>
    public bool WriteInFlight { get; set; }

    /// <summary>最近一次预热段实测冲温（峰值 − 中间 SP，℃），用于现场设定 Δ 与末段范围。</summary>
    public double PreheatOvershoot { get; private set; }

    private readonly List<(DateTime t, double pv)> _hist = new();
    private DateTime? _inBandSince;
    private DateTime? _reachedAt;     // 预热段 PV 首次到达 中间SP−判稳范围 的时刻
    private double _peak;             // 预热段 PV 峰值
    private DateTime? _overSince;
    private bool _overReported;
    private bool _exo;
    private DateTime _lastStep;
    private double _lastWritten = double.NaN;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    /// <summary>启动加热：按当前 PV 规划逼近，返回首个应写入的 SP。</summary>
    public double Start(double target, double pv, DateTime now, ApproachParams p)
    {
        Phase = ApproachPhase.Off;
        _hist.Clear();
        _overSince = null;
        _overReported = false;
        _exo = false;
        _lastWritten = double.NaN;
        Plan(target, pv, now, p);
        return SpToWrite;
    }

    /// <summary>加热中修改目标温度：从当前状态重新规划（往低改直接恒温；往高改差得多则重走预热段）。</summary>
    public void Retarget(double target, double pv, DateTime now, ApproachParams p)
    {
        if (!Active) return;
        _overSince = null;
        _overReported = false;
        Plan(target, pv, now, p);
    }

    public void Stop()
    {
        Phase = ApproachPhase.Off;
        StatusText = "";
        WriteInFlight = false;
    }

    private void Plan(double target, double pv, DateTime now, ApproachParams p)
    {
        var prev = Phase;
        Target = target;
        _lastStep = now;

        // Off/Settle 以 PV 为基准；Creep/Hold 以当前 SP 为基准（PV 已贴着 SP）
        bool spAnchored = prev is ApproachPhase.Creep or ApproachPhase.Hold;
        double anchor = spAnchored ? CurrentSp : pv;

        // 关闭逼近 / 往低改 / 已在目标之上：直接恒温（只加热，降温不会向上过冲）
        if (p.Delta <= 0 || target <= anchor + PauseMargin || target <= pv)
        {
            EnterHold();
            return;
        }

        double mid = Floor1(target - p.Delta);
        if (mid > anchor + p.SettleBand)
        {
            if (prev != ApproachPhase.Settle || Math.Abs(mid - CurrentSp) > 1e-6)
            {
                _inBandSince = null;
                _reachedAt = null;
                _peak = double.MinValue;
            }
            CurrentSp = mid;
            Phase = ApproachPhase.Settle;
            StatusText = $"预热至 {mid:0.0}℃";
            return;
        }

        // 离目标已在 Δ 以内：直接逼近
        if (!spAnchored) CurrentSp = Math.Min(Floor1(pv), target);
        Phase = ApproachPhase.Creep;
        StatusText = $"逼近中 {CurrentSp:0.0} → {target:0.0}℃";
    }

    private void EnterCreep(double pv)
    {
        // 从 max(中间SP, 当前PV) 起步：冲过头的那部分直接利用，不等它自然降回
        CurrentSp = Math.Min(Math.Max(CurrentSp, pv), Target);
        if (CurrentSp >= Target - 1e-9) { EnterHold(); return; }
        Phase = ApproachPhase.Creep;
        StatusText = $"逼近中 {CurrentSp:0.0} → {Target:0.0}℃";
    }

    private void EnterHold()
    {
        CurrentSp = Target;
        Phase = ApproachPhase.Hold;
        StatusText = $"恒温 {Target:0.0}℃";
    }

    /// <summary>每个轮询周期推进一次。pv / outPct 取仪表实测（输出 0..100%）。</summary>
    public ApproachEvent Step(DateTime now, double pv, double outPct, ApproachParams p)
    {
        if (!Active) return ApproachEvent.None;

        _hist.Add((now, pv));
        _hist.RemoveAll(h => now - h.t > SlopeWindow);

        var dt = now - _lastStep;
        if (dt > MaxStepGap) dt = MaxStepGap;
        if (dt < TimeSpan.Zero) dt = TimeSpan.Zero;
        _lastStep = now;

        bool heaterOff = outPct <= ExoOutPct;
        var evt = ApproachEvent.None;

        // —— 超限监测（各阶段通用）：PV > 目标+0.1 且加热已关，持续一段时间才上报，回到目标以下复位 ——
        if (pv > Target + OverLimit && heaterOff)
        {
            _overSince ??= now;
            if (!_overReported && now - _overSince >= OverLimitHold)
            {
                _overReported = true;
                evt = ApproachEvent.OverLimit;
            }
        }
        else
        {
            _overSince = null;
            if (pv <= Target) _overReported = false;
        }

        switch (Phase)
        {
            case ApproachPhase.Settle:
            {
                _peak = Math.Max(_peak, pv);
                if (_reachedAt == null && pv >= CurrentSp - p.SettleBand) _reachedAt = now;

                if (Math.Abs(pv - CurrentSp) <= p.SettleBand) _inBandSince ??= now;
                else _inBandSince = null;

                double stableSec = _inBandSince is { } since ? (now - since).TotalSeconds : 0;
                // 判峰：已到中间 SP 附近、且最近 60s 不再上升 → 蓄热已释放，可以开始逼近
                bool peaked = _reachedAt is { } r && (now - r).TotalSeconds >= 30 && SlopePerMin() <= PeakSlope;
                if (stableSec >= p.SettleSec || peaked)
                {
                    PreheatOvershoot = Math.Max(0, _peak - CurrentSp);
                    EnterCreep(pv);
                    if (evt == ApproachEvent.None) evt = ApproachEvent.PreheatDone;
                }
                else if (_inBandSince != null)
                    StatusText = $"预热至 {CurrentSp:0.0}℃ · 稳定中 {stableSec:0}/{p.SettleSec:0}s";
                else if (heaterOff && pv > CurrentSp + p.SettleBand)
                    StatusText = $"预热至 {CurrentSp:0.0}℃ · 输出已关，等待回落";
                else
                    StatusText = $"预热至 {CurrentSp:0.0}℃";
                break;
            }

            case ApproachPhase.Creep:
            {
                bool exo = heaterOff && SlopePerMin() >= ExoRisePerMin;
                if (exo && !_exo && evt == ApproachEvent.None) evt = ApproachEvent.ExoPause;
                _exo = exo;

                // 末段：PV 或 SP 任一已进入 目标−FinalZone 以内
                bool final = Target - Math.Max(CurrentSp, pv) <= p.FinalZone;

                if (exo) { StatusText = $"放热中 · 暂停上推（SP {CurrentSp:0.0}℃）"; break; }

                // 快段：PV 领先就把 SP 跟到 PV（不进末段），不必停下等
                if (!final && pv > CurrentSp) CurrentSp = Math.Min(pv, Target - p.FinalZone);

                if (final && pv > CurrentSp + PauseMargin)
                {
                    // 末段 PV 高于 SP：还在上升 → 暂停（蓄热未释放）；已不再上升 → SP 跟到 PV（不会额外加热，只是不再空等）
                    if (SlopePerMin() > PeakSlope)
                        StatusText = $"末段 · PV 偏高 · 暂停上推（SP {CurrentSp:0.0}℃）";
                    else
                    {
                        CurrentSp = Math.Min(pv, Target);
                        if (CurrentSp >= Target - 1e-9) EnterHold();
                        else StatusText = $"末段慢推 {CurrentSp:0.0} → {Target:0.0}℃";
                    }
                }
                else if (CurrentSp - pv > MaxLead)
                    StatusText = $"等待 PV 跟上（SP {CurrentSp:0.0}℃）";
                else
                {
                    double rate = final ? p.Rate : p.FastRate;
                    CurrentSp = Math.Min(Target, CurrentSp + rate * dt.TotalMinutes);
                    if (CurrentSp >= Target - 1e-9) EnterHold();
                    else StatusText = $"{(final ? "末段慢推" : "快速逼近")} {CurrentSp:0.0} → {Target:0.0}℃";
                }
                break;
            }

            case ApproachPhase.Hold:
                StatusText = heaterOff && pv > Target + OverLimit
                    ? $"恒温 {Target:0.0}℃ · 放热超出控温能力"
                    : $"恒温 {Target:0.0}℃";
                break;
        }

        return evt;
    }

    /// <summary>最近 60s PV 的最小二乘斜率（℃/min），样本不足 30s 返回 0。</summary>
    private double SlopePerMin()
    {
        if (_hist.Count < 3) return 0;
        var t0 = _hist[0].t;
        if ((_hist[^1].t - t0).TotalSeconds < 30) return 0;
        double n = _hist.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (t, pv) in _hist)
        {
            double x = (t - t0).TotalMinutes;
            sx += x; sy += pv; sxx += x * x; sxy += x * pv;
        }
        double den = n * sxx - sx * sx;
        return den <= 1e-12 ? 0 : (n * sxy - sx * sy) / den;
    }

    /// <summary>是否需要下发：SP 变化 ≥0.05℃，或距上次成功下发超过 30s（自愈串口偶发失败/面板被改）。</summary>
    public bool ShouldWrite(DateTime now)
    {
        if (!Active || WriteInFlight) return false;
        if (double.IsNaN(_lastWritten)) return true;
        if (Math.Abs(SpToWrite - _lastWritten) >= 0.05) return true;
        return now - _lastWriteUtc >= ForceRewriteEvery;
    }

    /// <summary>记录一次成功下发（失败不调用，下周期自动重试）。</summary>
    public void MarkWritten(double sp, DateTime now)
    {
        _lastWritten = sp;
        _lastWriteUtc = now;
    }

    private static double Floor1(double x) => Math.Floor(x * 10 + 1e-6) / 10;
}
