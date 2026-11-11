using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.History;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>
/// 估算用的速率（<b>示例参数 · 为假设</b>，同型号表的假设值：拿到真机的升温 / 降温 / 充压曲线后改这里或用 <see cref="FromSettings"/>）：
/// 升温 ℃/min、自然降温 ℃/min、充压 MPa/min、到位后的稳定时间（min）、结束步骤降温之后的收尾（泄压 / 密封，min）。
/// 实测速率（<see cref="EtaEstimator.ObservedRate"/>，最近 3 min 的曲线斜率）够大时优先用实测的。
/// </summary>
public sealed record EtaRates(double HeatCPerMin = 3, double CoolCPerMin = 1, double FillMPaPerMin = 0.5, double SettleMin = 1, double EndTailMin = 1)
{
    public static readonly EtaRates Default = new();

    /// <summary>按 HMI 的逼近设置（/settings 的 approach）：升温速率取 FastRateCPerMin（远离目标时的速率；没有时 RateCPerMin），稳定时间取 SettleSec。</summary>
    public static EtaRates FromSettings(SettingsDto? s)
    {
        if (s?.Approach is not { } a) return Default;
        double heat = a.FastRateCPerMin > 0 ? a.FastRateCPerMin : a.RateCPerMin > 0 ? a.RateCPerMin : Default.HeatCPerMin;
        double settle = a.SettleSec > 0 ? a.SettleSec / 60.0 : Default.SettleMin;
        return Default with { HeatCPerMin = heat, SettleMin = settle };
    }
}

/// <summary>剩余时间的依据：HMI 的倒计时 / 升温曲线进度（准）、按读数和速率估的（只能估）、什么都不知道（按计划时长）。</summary>
public enum EtaBasis { Timer, Profile, Estimate, Unknown }

/// <summary>估算当前步用的读数：温度、压力（MPa）、实测速率（℃/min、MPa/min；null = 不知道）、升温曲线进度（HMI 的 profile，曲线升温时才有）。</summary>
public sealed record EtaReading(double? TempC = null, double? PressureMPa = null, double? TempRate = null, double? PressureRate = null, ProfileSnap? Profile = null)
{
    public static readonly EtaReading None = new();
}

/// <summary>
/// 当前步的估算：开始时刻（知道的，或按倒计时反推的，或「第一次看到」—— 实际开始不会更晚）、从开始算的预计时长（秒，= ProgramRecord.StepDurS）、
/// 还剩多少秒（在估算的时刻）、依据。
/// </summary>
public readonly record struct StepEta(DateTime? StepStart, double StepDurS, double RemainS, EtaBasis Basis);

/// <summary>
/// 剩余时间与 ETA（PORT-PLAN §3.2 第 6 条）：真机的步骤没有剩余时间字段（A1 之前），这里按 HMI 实际的执行方式估：
/// <list type="bullet">
/// <item><b>定时的</b>（反应 time、等待 time、搅拌 dur、升温到位后的「平衡」wait）：HMI 的 program.message 里有倒计时「剩 12.5 分」「平衡剩 3 分」
///   （ProgramViewModel.Run.cs LeftMin，0.1 min 精度）—— 直接用，PC 中途连上也准；没有时按开始时刻 + 时长。</item>
/// <item><b>曲线升温</b>：HMI 的 profile（TotalMinutes − ElapsedMin），准。</item>
/// <item><b>按条件结束的</b>（定值升温的升温段：|温度 − 目标| ≤ 2 ℃；充压到目标；结束步骤降温到 ℃）：只能估 = 距离 ÷ 速率（实测优先，否则 <see cref="EtaRates"/>）。
///   估计用完还没到时剩余为 0 —— 充压 / 结束步骤的结束条件是压力 / 温度，Progress 据此写「当前 · 等条件」「等压力 ≥ 0.350 MPa」（样稿 overdue）；
///   升温步骤还有平衡段，剩余不低于平衡时长。</item>
/// <item>吹扫：message 里的「2/3」循环计数按比例；否则按计划时长。</item>
/// </list>
/// 时刻一律是设备时钟的 UTC；「估算的时刻」= 最新一帧的时刻（离线时 Progress 停在 DataAt，倒计时不再走）。纯函数。
/// </summary>
public static class EtaEstimator
{
    private static readonly Regex Remain = new(@"剩\s*([0-9]+(?:\.[0-9]+)?)\s*分", RegexOptions.CultureInvariant);
    private static readonly Regex Cycle = new(@"\s([0-9]+)/([0-9]+)\s·", RegexOptions.CultureInvariant);

    /// <summary>HMI program.message 里的倒计时（分钟）：「… · 反应中 · 剩 12.5 分」「… · 已到 150℃ · 平衡剩 3 分」→ 12.5 / 3；没有为 null。</summary>
    public static double? RemainFromMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = Remain.Match(message);
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>升温步骤已经到位、在「平衡」倒计时（message 里有「平衡剩」）。</summary>
    public static bool Balancing(string? message) => message is not null && message.Contains("平衡剩", StringComparison.Ordinal);

    /// <summary>吹扫的循环计数：「01/04 吹扫 · 惰性气体 2/3 · 充气」→ (2, 3)；没有为 null。</summary>
    public static (int Cycle, int Total)? PurgeCycle(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = Cycle.Match(message);
        if (!m.Success) return null;
        int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return b > 0 && a >= 1 && a <= b ? (a, b) : null;
    }

    /// <summary>
    /// 实测速率（每分钟）：<paramref name="h"/> 里最近 <paramref name="minutes"/> 分钟的 <paramref name="field"/>（缺失跳过）做最小二乘直线的斜率；
    /// 点不够（&lt; 4 个，或跨度 &lt; 1 min）为 null。<paramref name="nowUtc"/> = 估算的时刻。
    /// </summary>
    public static double? ObservedRate(ChannelHistory? h, HistoryField field, DateTime nowUtc, double minutes = 3)
    {
        if (h is null || h.Count == 0) return null;
        var from = Utc(nowUtc) - TimeSpan.FromMinutes(minutes);
        int i0 = h.LowerBound(from);
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, x0 = double.NaN, xMin = double.PositiveInfinity, xMax = double.NegativeInfinity;
        for (int i = i0; i < h.Count; i++)
        {
            var s = h[i];
            if (s.T > Utc(nowUtc)) break;
            if (s.Get(field) is not double v || !double.IsFinite(v)) continue;
            if (double.IsNaN(x0)) x0 = s.T.Ticks;
            double x = (s.T.Ticks - x0) / TimeSpan.TicksPerMinute;
            n++; sx += x; sy += v; sxx += x * x; sxy += x * v;
            xMin = Math.Min(xMin, x); xMax = Math.Max(xMax, x);
        }
        if (n < 4 || xMax - xMin < 1) return null;
        double den = n * sxx - sx * sx;
        return den <= 0 ? null : (n * sxy - sx * sy) / den;
    }

    /// <summary>
    /// 每一步的计划时长（ProgramStep.EstMin，Progress 用它排后面的步骤、算全程剩余 / ETA）：按条件结束的步骤按前一步留下的温度 / 压力估：
    /// 定值升温 = |目标 − 前一步温度| ÷ 升温（降温）速率 + 稳定 + 平衡时长；充压 = 差值 ÷ 充压速率（至少 0.5 min）；结束 = 降温 + 收尾；
    /// 降温（演示型号）= 差值 ÷ 降温速率。定时的步骤不变。<paramref name="startTempC"/> / <paramref name="startPressureMPa"/>：第一步之前的温度 / 压力（不知道按 25 ℃ / 0）。
    /// </summary>
    public static IReadOnlyList<ProgramStep> Plan(IReadOnlyList<ProgramStep> steps, double? startTempC = null, double? startPressureMPa = null, EtaRates? rates = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var r = rates ?? EtaRates.Default;
        var list = new ProgramStep[steps.Count];
        double temp = startTempC ?? 25, pres = startPressureMPa ?? 0;
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            double? est = null;
            switch (s.Type)
            {
                case StepKind.Heat when !s.HeatCurve && s.Sp is double sp:
                    est = RampMin(temp, sp, r) + (Math.Abs(sp - temp) > 2 ? r.SettleMin : 0) + (s.End is { Kind: EndKind.Time } e ? e.V : 0);
                    temp = sp;
                    break;
                case StepKind.Heat when s.Sp is double sp2:
                    temp = sp2;
                    break;
                case StepKind.Press when s.Target is double tg:
                    est = Math.Max(0.5, Math.Max(0, tg - pres) / r.FillMPaPerMin);
                    pres = tg;
                    break;
                case StepKind.Cool when s.Sp is double c:
                    est = Math.Max(0, temp - c) / r.CoolCPerMin;
                    temp = c;
                    break;
                case StepKind.Purge:
                    pres = 0;
                    break;
                case StepKind.End:
                    if (s.CoolTo is double ct) { est = Math.Max(0, temp - ct) / r.CoolCPerMin + r.EndTailMin; temp = Math.Min(temp, ct); }
                    else est = r.EndTailMin;
                    if (s.Vent == true) pres = 0;
                    break;
            }
            list[i] = est is double m && double.IsFinite(m) ? s with { EstMin = Math.Round(m, 1) } : s;
        }
        return list;
    }

    /// <summary>
    /// 当前步的估算。<paramref name="nowUtc"/> = 估算的时刻（最新一帧的时刻）；<paramref name="startUtc"/> = 步骤计时记下的开始（不知道为 null）；
    /// <paramref name="firstSeenUtc"/> = 第一次看到这一步的时刻；<paramref name="message"/> = HMI 的 program.message。
    /// </summary>
    public static StepEta Current(ProgramStep step, DateTime nowUtc, DateTime? startUtc, DateTime? firstSeenUtc, string? message, EtaReading? reading = null, EtaRates? rates = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        var r = rates ?? EtaRates.Default;
        var rd = reading ?? EtaReading.None;
        var now = Utc(nowUtc);
        DateTime? start = startUtc is { } s0 ? Utc(s0) : null;
        double? elapsed = start is { } st ? Math.Max(0, (now - st).TotalSeconds) : null;
        double? msg = RemainFromMessage(message);
        double remainS;
        EtaBasis basis;
        double? timedMin = null;   // 定时步骤的时长（倒计时反推开始时刻用）

        switch (step.Type)
        {
            case StepKind.Heat when step.HeatCurve:
                if (rd.Profile is { Running: true, TotalMinutes: > 0 } pf)
                {
                    remainS = Math.Max(0, pf.TotalMinutes - pf.ElapsedMin) * 60;
                    basis = EtaBasis.Profile;
                    timedMin = pf.TotalMinutes;
                }
                else { remainS = Planned(step, elapsed); basis = EtaBasis.Unknown; }
                break;
            case StepKind.Heat:
            {
                double wait = step.End is { Kind: EndKind.Time } e ? e.V : 0;
                if (Balancing(message) && msg is double bal) { remainS = bal * 60; basis = EtaBasis.Timer; }
                else if (rd.TempC is double t && step.Sp is double sp)
                {
                    double dist = Math.Abs(sp - t);
                    double rate = RateToward(rd.TempRate, sp - t, sp >= t ? r.HeatCPerMin : r.CoolCPerMin, 0.2);
                    double ramp = dist <= 2 ? 0 : (dist - 2) / rate + r.SettleMin;
                    remainS = (ramp + wait) * 60;
                    basis = EtaBasis.Estimate;
                }
                else { remainS = Planned(step, elapsed); basis = EtaBasis.Unknown; }
                break;
            }
            case StepKind.Press:
                if (rd.PressureMPa is double p && step.Target is double tg)
                {
                    double gap = tg - p;
                    remainS = gap <= 0 ? 0 : gap / RateToward(rd.PressureRate, gap, r.FillMPaPerMin, 0.005) * 60;
                    basis = EtaBasis.Estimate;
                }
                else { remainS = Planned(step, elapsed); basis = EtaBasis.Unknown; }
                break;
            case StepKind.React or StepKind.Wait or StepKind.Stir:
            {
                double min = step.Minutes ?? (step.End is { Kind: EndKind.Time } e ? e.V : step.EstMin);
                timedMin = min;
                if (msg is double left) { remainS = left * 60; basis = EtaBasis.Timer; }
                else if (elapsed is double el) { remainS = Math.Max(0, min * 60 - el); basis = EtaBasis.Timer; }
                else { remainS = min * 60; basis = EtaBasis.Unknown; }
                break;
            }
            case StepKind.Purge:
            {
                double min = step.EstMin > 0 ? step.EstMin : step.End is { Kind: EndKind.Time } e ? e.V : 1;
                timedMin = min;
                if (PurgeCycle(message) is { } cy) { remainS = min * 60 * (cy.Total - cy.Cycle + 1) / cy.Total; basis = EtaBasis.Estimate; }
                else if (elapsed is double el) { remainS = Math.Max(0, min * 60 - el); basis = EtaBasis.Estimate; }
                else { remainS = min * 60; basis = EtaBasis.Unknown; }
                break;
            }
            case StepKind.End:
                if (rd.TempC is double te && step.CoolTo is double ct)
                {
                    double cool = te <= ct ? 0 : (te - ct) / RateToward(rd.TempRate, ct - te, r.CoolCPerMin, 0.1);
                    remainS = cool * 60;
                    basis = EtaBasis.Estimate;
                }
                else { remainS = Planned(step, elapsed); basis = EtaBasis.Unknown; }
                break;
            case StepKind.Cool:
                if (rd.TempC is double tc && step.Sp is double sc)
                {
                    remainS = tc <= sc ? 0 : (tc - sc) / RateToward(rd.TempRate, sc - tc, r.CoolCPerMin, 0.1) * 60;
                    basis = EtaBasis.Estimate;
                }
                else { remainS = Planned(step, elapsed); basis = EtaBasis.Unknown; }
                break;
            default:
                remainS = Planned(step, elapsed);
                basis = EtaBasis.Unknown;
                break;
        }
        if (!double.IsFinite(remainS) || remainS < 0) remainS = 0;

        // 开始时刻：记下的 > 定时步骤按倒计时反推（PC 中途连上也有）> 第一次看到（实际开始不会更晚）> 现在
        DateTime? begin = start;
        if (begin is null && basis is EtaBasis.Timer or EtaBasis.Profile && timedMin is double tm)
            begin = now - TimeSpan.FromSeconds(Math.Clamp(tm * 60 - remainS, 0, tm * 60));
        begin ??= firstSeenUtc is { } fs ? Utc(fs) : now;
        if (begin > now) begin = now;
        double dur = (now - begin.Value).TotalSeconds + remainS;
        return new StepEta(begin, dur, remainS, basis);
    }

    /// <summary>
    /// 把估算放进一条程序记录（真机的台账记录）：后面步骤的计划时长（<see cref="Plan"/>，从当前的温度 / 压力往后推），当前步的开始时刻与预计时长。
    /// 只对运行中的记录（Running / Held）改当前步；其它状态只改计划时长。<paramref name="nowUtc"/> = 最新一帧的时刻。
    /// </summary>
    public static ProgramRecord Apply(ProgramRecord p, DateTime nowUtc, DateTime? stepStartUtc, DateTime? firstSeenUtc, string? message, EtaReading? reading = null, EtaRates? rates = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        var rd = reading ?? EtaReading.None;
        var steps = Plan(p.Steps, null, null, rates);
        if (p.IsActive && p.StepIndex >= 0 && p.StepIndex < steps.Count)
        {
            // 当前步之后的步骤从现在的读数往后推
            var tail = Plan(Slice(p.Steps, p.StepIndex + 1), rd.TempC ?? AfterTemp(p.Steps, p.StepIndex), rd.PressureMPa, rates);
            var merged = new ProgramStep[steps.Count];
            for (int i = 0; i < merged.Length; i++) merged[i] = i <= p.StepIndex ? steps[i] : tail[i - p.StepIndex - 1];
            // 当前步自己按「到这一步之前」的温度估计划时长（时间轴分段的长度）；剩余按实测读数
            var eta = Current(p.Steps[p.StepIndex], nowUtc, stepStartUtc, firstSeenUtc, message, rd, rates);
            return p with
            {
                Steps = Array.AsReadOnly(merged),
                StepStartedAt = eta.StepStart?.ToLocalTime(),
                StepDurS = eta.StepDurS,
            };
        }
        return p with { Steps = Array.AsReadOnly(Copy(steps)) };
    }

    /// <summary>步骤 i 执行之后留下的温度（往前找最近一个设定了温度的步骤；没有 = null）。</summary>
    private static double? AfterTemp(IReadOnlyList<ProgramStep> steps, int i)
    {
        for (int k = Math.Min(i, steps.Count - 1); k >= 0; k--)
        {
            var s = steps[k];
            if (s.Type == StepKind.End && s.CoolTo is double c) return c;
            if (s.Sp is double sp) return sp;
        }
        return null;
    }

    private static double RampMin(double from, double to, EtaRates r)
    {
        double d = to - from;
        if (Math.Abs(d) <= 2) return 0;
        return (Math.Abs(d) - 2) / (d > 0 ? r.HeatCPerMin : r.CoolCPerMin);
    }

    /// <summary>朝目标方向的实测速率够大（≥ <paramref name="min"/>）就用它，否则用默认的。</summary>
    private static double RateToward(double? observed, double direction, double fallback, double min)
    {
        if (observed is double o && double.IsFinite(o) && Math.Sign(o) == Math.Sign(direction) && Math.Abs(o) >= min) return Math.Abs(o);
        return fallback > 0 ? fallback : 1;
    }

    /// <summary>按计划时长（Progress.StepPlanS）减去已经过去的。</summary>
    private static double Planned(ProgramStep step, double? elapsed) => Math.Max(0, Progress.StepPlanS(step) - (elapsed ?? 0));

    private static ProgramStep[] Slice(IReadOnlyList<ProgramStep> s, int from)
    {
        var a = new ProgramStep[Math.Max(0, s.Count - from)];
        for (int i = 0; i < a.Length; i++) a[i] = s[from + i];
        return a;
    }

    private static ProgramStep[] Copy(IReadOnlyList<ProgramStep> s)
    {
        var a = new ProgramStep[s.Count];
        for (int i = 0; i < a.Length; i++) a[i] = s[i];
        return a;
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
