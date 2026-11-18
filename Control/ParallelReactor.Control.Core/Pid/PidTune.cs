using System;
using System.Collections.Generic;
using System.Globalization;

namespace ParallelReactor.Control.Core.Pid;

/// <summary>
/// 一个温度档（GET /pid 的 bands[]，HMI TempService.Bands）：下标、中文名、温区 [loC, hiC)；最高档另外包含 ≥ hiC 的温度（HMI 超出上限归最高档）。
/// </summary>
public sealed record PidBand(int Index, string Name, double LoC, double HiC, bool Top);

/// <summary>
/// 自整定温度的可选范围（设置 → 温控 / PID 的「整定温度」）：本档温区 ∩ [40, TMaxSp]（HMI §7.6：40 ≤ tempC ≤ TMaxSp；给了 band 时 tempC 须落在该档温区内，
/// 非最高档 [loC, hiC)，最高档 [loC, TMaxSp]）。<see cref="MaxInclusive"/> = false：上限本身不行（属于下一档）。
/// </summary>
public sealed record PidTuneRange(double Min, double Max, bool MaxInclusive)
{
    /// <summary>这一档能整定（下限 &lt; 本机温度上限，同样稿 b.lo &lt; tmax）。</summary>
    public bool Usable { get; init; } = true;

    public bool Contains(double t) => double.IsFinite(t) && t >= Min - 1e-9 && (MaxInclusive ? t <= Max + 1e-9 : t < Max - 1e-9);

    /// <summary>「100 – 200 °C」（样稿「本档 100 – 200 °C」的后半）。</summary>
    public string Text => PidTexts.Num(Min) + " – " + PidTexts.Num(Max) + " °C";
}

/// <summary>
/// 自整定的纯逻辑（融合版 · 设置 → 温控 / PID 的自整定，样稿 pidBody / pidSeries / pidCycles）：档位的整定温度范围与默认值、
/// 振荡计数（AI-8 不报告整定进度，只能看曲线数）、整定前后 PID 的对比文字、演示的整定曲线。
/// </summary>
public static class PidTune
{
    /// <summary>HMI 的最低整定温度（§7.6：40 ≤ tempC）。</summary>
    public const double MinTempC = 40;

    /// <summary>AI-8 一般 2–3 个完整振荡后算出 PID：界面按 3 次显示进度。</summary>
    public const int Cycles = 3;

    /// <summary>
    /// 这一档的整定温度范围（<paramref name="tmaxSp"/> = 本机温度 SP 上限）。非最高档：[max(40, lo), hi)，hi 超过上限时 [.., tmax]；最高档 [max(40, lo), tmax]。
    /// 下限 ≥ 本机上限时这一档不能用（<see cref="PidTuneRange.Usable"/> = false，样稿「超过本机上限 200 °C」）。
    /// </summary>
    public static PidTuneRange Range(PidBand b, double tmaxSp)
    {
        double min = Math.Max(MinTempC, b.LoC);
        bool capped = b.Top || b.HiC > tmaxSp;
        double max = capped ? tmaxSp : b.HiC;
        return new PidTuneRange(min, max, capped) { Usable = b.LoC < tmaxSp && min <= max };
    }

    /// <summary>
    /// 整定温度不行的原因（null = 可以）：没填、不是数、不在本档里。文字给「开始自整定」按钮的禁用原因用。
    /// </summary>
    public static string? TempReason(PidBand b, double tmaxSp, double? t)
    {
        var r = Range(b, tmaxSp);
        if (!r.Usable) return "超过本机温度上限";
        if (t is not double v || !double.IsFinite(v)) return "先填整定温度";
        if (r.Contains(v)) return null;
        if (!r.MaxInclusive && Math.Abs(v - r.Max) < 1e-9) return "整定温度须低于 " + PidTexts.Num(r.Max) + " °C（" + PidTexts.Num(r.Max) + " °C 属于下一档）";
        return "整定温度不在本档区间内（" + r.Text + "）";
    }

    /// <summary>样稿 PIDB 的默认整定温度（低温档 80、中温档 150、高温档 250）。</summary>
    private static readonly double[] MockDefaults = { 80, 150, 250 };

    /// <summary>
    /// 默认整定温度：样稿的 80 / 150 / 250 落在这一档的范围里就用它，否则取范围中间（取整）；不能用的档给下限。
    /// </summary>
    public static double DefaultTemp(PidBand b, double tmaxSp)
    {
        var r = Range(b, tmaxSp);
        if (!r.Usable) return r.Min;
        if (b.Index >= 0 && b.Index < MockDefaults.Length && r.Contains(MockDefaults[b.Index])) return MockDefaults[b.Index];
        double mid = Math.Round((r.Min + r.Max) / 2);
        return r.Contains(mid) ? mid : r.Min;
    }

    /// <summary>温度 <paramref name="t"/> 属于哪一档（HMI BandIndexForTemp：[lo, hi)，超出最高档上限的归最高档）；没有档位为 null。</summary>
    public static int? BandFor(IReadOnlyList<PidBand> bands, double t)
    {
        if (bands.Count == 0) return null;
        foreach (var b in bands) if (t >= b.LoC && t < b.HiC) return b.Index;
        return t >= bands[^1].LoC ? bands[^1].Index : bands[0].Index;
    }

    /// <summary>
    /// 档位卡片的第三行（样稿 pidtab .st）：超过本机上限 → 「超过本机上限 200 °C」；全部整定过 →「7 路已整定」；部分 →「5/7 路已整定」；都没有 →「都未整定」。
    /// <paramref name="all"/> = 启用的通道数（停用的不算）。
    /// </summary>
    public static string BandStatus(int tuned, int all, bool usable, double tmaxSp) =>
        !usable ? "超过本机上限 " + PidTexts.Num(tmaxSp) + " °C"
        : all > 0 && tuned >= all ? all.ToString(CultureInfo.InvariantCulture) + " 路已整定"
        : tuned > 0 ? tuned.ToString(CultureInfo.InvariantCulture) + "/" + all.ToString(CultureInfo.InvariantCulture) + " 路已整定"
        : "都未整定";
}

/// <summary>一个曲线采样：距整定开始的分钟、测量温度 PV（°C）、加热输出 %（缺失为 null）。</summary>
public readonly record struct TuneSample(double M, double? Pv, double? Out);

/// <summary>振荡计数的结果：PV 是否已经到过整定温度（何时，分钟）、完成了几次振荡（封顶 3）。</summary>
public readonly record struct OscCount(bool Reached, double? ReachedAt, int Completed)
{
    /// <summary>正在第几次振荡（1–3，样稿「第 k/3 次振荡」：完成数 + 1，封顶 3）。</summary>
    public int Current => Math.Min(PidTune.Cycles, Completed + 1);
}

/// <summary>
/// 振荡计数（AI-8 不报告整定进度：只能看曲线）。PV 第一次到达整定温度 SP 之后，每当 PV 先往下穿过 SP、再往上穿回 SP，算完成一次振荡
/// （同样稿 pidCycles：振荡周期从到达 SP 起算，一个整周期 = 一次）。用 ±<c>hyst</c> 的回差防噪声：低于 SP − hyst 才算「到了下面」，
/// 高于 SP + hyst 才算「回到上面」（到达本身按 PV ≥ SP − hyst）。没有 PV 读数时退而看加热输出：到达 = 第一次关断（输出从 ≥ 50 % 降到 &lt; 50 %），
/// 之后每一次「先开再关」算一次。纯函数，采样按时间排好。
/// </summary>
public static class OscillationCounter
{
    public const double DefaultHysteresis = 0.2;

    public static OscCount Count(IReadOnlyList<TuneSample> samples, double sp, double hyst = DefaultHysteresis)
    {
        ArgumentNullException.ThrowIfNull(samples);
        bool anyPv = false;
        foreach (var s in samples) if (s.Pv is double v && double.IsFinite(v)) { anyPv = true; break; }
        return anyPv ? ByPv(samples, sp, Math.Max(0, hyst)) : ByOutput(samples);
    }

    private static OscCount ByPv(IReadOnlyList<TuneSample> samples, double sp, double h)
    {
        bool reached = false, below = false;
        double? at = null;
        int done = 0;
        foreach (var s in samples)
        {
            if (s.Pv is not double v || !double.IsFinite(v)) continue;
            if (!reached)
            {
                if (v >= sp - h) { reached = true; at = s.M; }
                continue;
            }
            if (!below) { if (v < sp - h) below = true; }
            else if (v > sp + h)
            {
                below = false;
                if (++done >= PidTune.Cycles) break;
            }
        }
        return new OscCount(reached, at, Math.Min(done, PidTune.Cycles));
    }

    private static OscCount ByOutput(IReadOnlyList<TuneSample> samples)
    {
        // 关断沿（输出从 ≥ 50 % 降到 < 50 %）：第一个 = 到达整定温度，之后每一个 = 又完成一次（中间必然开过一次）
        bool? on = null;
        double? at = null;
        int edges = 0;
        foreach (var s in samples)
        {
            if (s.Out is not double o || !double.IsFinite(o)) continue;
            bool now = o >= 50;
            if (on == true && !now && ++edges == 1) at = s.M;
            on = now;
        }
        return new OscCount(edges > 0, at, Math.Min(Math.Max(0, edges - 1), PidTune.Cycles));
    }
}

/// <summary>一组 PID（P 比例带、I 积分时间 s、D 微分时间 s）。</summary>
public readonly record struct PidValues(double P, double I, double D);

/// <summary>整定结果的一项对比（样稿 pidres：「P 15.1 → 14.0」，I / D 带「s」）。</summary>
public sealed record PidDiffPart(string Key, string Old, string New, string Unit);

/// <summary>设置 → 温控 / PID 的文字（数字格式、整定前后对比、说明）。</summary>
public static class PidTexts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>温度 / 范围的数：整数不带小数，否则一位（「150」「72.5」）。</summary>
    public static string Num(double v) => Math.Round(v, 1).ToString("0.#", Inv);

    /// <summary>P 一位小数（样稿 toFixed(1)：「13.0」）。</summary>
    public static string P(double v) => v.ToString("0.0", Inv);

    /// <summary>I / D：整数不带小数，否则一位（样稿是整数；HMI 的 D 可以是 21.4）。</summary>
    public static string Sec(double v) => Math.Round(v, 1).ToString("0.#", Inv);

    /// <summary>整定前后的三项（P · I s · D s）。</summary>
    public static IReadOnlyList<PidDiffPart> DiffParts(PidValues old, PidValues now) => new[]
    {
        new PidDiffPart("P", P(old.P), P(now.P), ""),
        new PidDiffPart("I", Sec(old.I), Sec(now.I), " s"),
        new PidDiffPart("D", Sec(old.D), Sec(now.D), " s"),
    };

    /// <summary>「P 15.1 → 14.0 · I 218 → 182 s · D 33 → 28 s · 已存入中温档」（结果卡的一行，也是测试 / 无障碍的文字）。</summary>
    public static string Diff(PidValues old, PidValues now, string bandName)
    {
        var parts = DiffParts(old, now);
        var s = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (s.Length > 0) s.Append(" · ");
            s.Append(p.Key).Append(' ').Append(p.Old).Append(" → ").Append(p.New).Append(p.Unit);
        }
        return s.Append(" · 已存入").Append(bandName).ToString();
    }

    /// <summary>结果卡下面的绿色说明（样稿 pidres .note2）。</summary>
    public static string OkNote(string bandName, double lo, double hi) =>
        "新 PID 已写入仪表并存入" + bandName + "；以后这一档温度区间（" + Num(lo) + "–" + Num(hi) + " °C）里加热都用它。整定后通道已停止加热，正在自然降温。";

    /// <summary>整定结束了但设备没有存入新 PID（被急停 / 超温切断打断，或仪表没算出来）。</summary>
    public const string NotSaved = "整定没有完成 · 设备没有存入新 PID，这一档保持原来的值";

    /// <summary>「RV5」「RV1、RV3」。</summary>
    public static string Rvs(IEnumerable<int> ns)
    {
        var s = new System.Text.StringBuilder();
        foreach (var n in ns) { if (s.Length > 0) s.Append('、'); s.Append("RV").Append(n.ToString(Inv)); }
        return s.ToString();
    }
}

/// <summary>
/// 演示的整定曲线（样稿 pidSeries，逐项照抄；实际 HMI 的曲线来自遥测）：先按 8 °C/min 升到整定温度，再在它附近通断振荡，振幅 4.2·e^(−x/26) + 0.6 逐次变小，
/// 周期 7 + (n mod 3)·0.6 min；输出 100 / 0 方波（升温段 100）。<paramref name="t0"/> = 开始时的温度（样稿 25 + 0.6n）。
/// </summary>
public static class PidDemoCurve
{
    public const double Rate = 8;

    public static double Period(int n) => 7 + (n % 3) * 0.6;

    public static double Ramp(double sp, double t0) => Math.Max(0, (sp - t0) / Rate);

    /// <summary>第 m 分钟的 (PV, 输出 %)。</summary>
    public static (double T, double Out) At(double m, double sp, double t0, int n)
    {
        double ramp = Ramp(sp, t0), per = Period(n);
        if (m < ramp) return (t0 + Rate * m, 100);
        double x = m - ramp, amp = 4.2 * Math.Exp(-x / 26) + 0.6, ph = Math.Sin(2 * Math.PI * x / per);
        double t = sp + amp * ph + 0.9 * Math.Sin(x * 1.7 + n) * 0.15;
        double o = Math.Cos(2 * Math.PI * x / per) > 0 ? 0 : 100;
        return (t, o);
    }

    /// <summary>
    /// 演示的整定在第几分钟结束：升温 + 3 个整周期之后、PV 再一次往上穿过整定温度（同 <see cref="OscillationCounter"/> 数到 3 的时刻）。
    /// </summary>
    public static double DoneAt(double sp, double t0, int n) => Ramp(sp, t0) + PidTune.Cycles * Period(n) + 0.5;
}
