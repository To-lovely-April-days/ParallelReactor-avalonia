using System;
using System.Collections.Generic;
using System.Globalization;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 60 min 历史的生成（样稿 sim.js curveT / curveP / curveH / curveSp / curvePt / curveMmol / genHistory / stressHistory / sampleOf），
/// 运算顺序与 JS 一一对应（结果与样稿逐位相同，测试按样稿 JS 算出的摘要核对）。m = 几分钟前。
/// JS 的 undefined 在这里是 null：参与算术时按 NaN（null 按 0），「||」按 JS 的真假值（0 / NaN / null 为假）。
/// </summary>
internal static class DemoHistory
{
    public const int HistoryMin = 60, SampleS = 10;

    /// <summary>JS 数字的真假值：null / 0 / NaN 为假。</summary>
    public static bool Truthy(double? v) => v is double d && d != 0 && !double.IsNaN(d);

    /// <summary>JS 的 a || b（数字）。</summary>
    public static double Or(double? a, double b) => Truthy(a) ? a!.Value : b;

    private static double U(double? v) => v ?? double.NaN;   // JS undefined 参与算术

    /// <summary>JS 的 +x.toFixed(d)。</summary>
    public static double Fixed(double x, int d) => double.Parse(JsNumber.ToFixed(x, d), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static double Clamp(double v, double a, double b) => Math.Max(a, Math.Min(b, v));

    public static double CurveT(ChSpec s, double m)
    {
        var tl = s.Tl ?? TlSpec.Empty;
        double tempC = U(s.TempC);
        if (tl.Held is double held)
        {
            double until = U(tl.Until), coolTo = U(tl.CoolTo);
            if (m >= until) return held;
            double tau = until / Math.Log((held - coolTo) / Math.Max(0.3, tempC - coolTo));
            return coolTo + (held - coolTo) * Math.Exp(-(until - m) / tau);
        }
        if (tl.HeatAt is not double heatAt) return tempC;
        double from = U(tl.From);
        if (m >= heatAt) return from;
        double dt = heatAt - m;
        double rate = Truthy(tl.Rate) ? tl.Rate!.Value : (tempC - from) / heatAt;
        double sp = s.SpC ?? 0;   // JS: null - 8 = -8
        double knee = sp - 8;
        double lin = from + rate * dt;
        if (lin < knee || !Truthy(tl.Rate)) return lin;
        double dtKnee = (knee - from) / rate;
        return sp - 8 * Math.Exp(-(dt - dtKnee) / 1.6);
    }

    public static double CurveP(ChSpec s, double m)
    {
        var tl = s.Tl ?? TlSpec.Empty;
        double b = tl.PBase ?? 0.005;
        if (tl.Held != null)
        {
            if (m >= U(tl.Until)) return U(tl.PHeld);
            return b + (U(tl.PHeld) - b) * Math.Exp(-(U(tl.Until) - m) / 0.7);
        }
        if (tl.PressAt is not double pressAt || m >= pressAt) return b;
        double dt = pressAt - m;
        double rate = Truthy(tl.PRate) ? tl.PRate!.Value : (U(s.P) - b) / pressAt;
        double p = b + rate * dt;
        double pt = U(s.PTarget);
        if (tl.Overshoot || p < pt) return p;
        double amp = pt * 0.012;
        return pt - amp * (dt * 0.37 % 1);
    }

    public static double CurveH(ChSpec s, double m, double t)
    {
        var tl = s.Tl ?? TlSpec.Empty;
        double hold = Truthy(tl.HoldPct) ? tl.HoldPct!.Value : Or(s.Heater, 40);
        if (tl.Held != null) return m >= U(tl.Until) ? hold : 0;
        if (tl.HeatAt is not double heatAt || m >= heatAt) return 0;
        double sp = s.SpC ?? 0;
        if (t < sp - 5) return 100;
        return hold + (100 - hold) * Clamp((sp - t) / 5, 0, 1);
    }

    public static double? CurveSp(ChSpec s, double m)
    {
        var tl = s.Tl ?? TlSpec.Empty;
        if (tl.Held is double held) return m >= U(tl.Until) ? held : s.State == State.StateKey.Cool ? s.SpC : null;
        if (tl.HeatAt is not double heatAt || m >= heatAt) return null;
        return s.SpC;
    }

    public static double? CurvePt(ChSpec s, double m)
    {
        var tl = s.Tl ?? TlSpec.Empty;
        if (tl.Held != null) return m >= U(tl.Until) ? tl.PHeld : null;
        if (tl.PressAt is not double pressAt || m >= pressAt) return null;
        return s.PTarget;
    }

    public static double? CurveMmol(ChSpec s, double m)
    {
        var tl = s.Tl ?? TlSpec.Empty;
        if (s.Mmol is not double mmol) return null;
        if (tl.Held != null) return m < U(tl.Until) ? mmol : mmol * (1 - (m - U(tl.Until)) / 80);
        if (tl.ReactAt is not double reactAt) return null;
        if (m >= reactAt) return 0;
        return mmol * (reactAt - m) / reactAt;
    }

    /// <summary>
    /// 样稿 genHistory：从 60 min 前到 <paramref name="endT"/>（断线设备 = 断线时刻），每 10 s 一个采样；最后一个采样换成当前读数。
    /// <paramref name="spec"/> 为 null（新添加的设备首次连上）时用平稳的室温。未安装返回空表、不消耗随机数。
    /// </summary>
    public static List<DemoSample> Generate(DemoDevice dev, DemoChannel ch, ChSpec? spec, DemoRng rand, DateTime now, DateTime endT)
    {
        var output = new List<DemoSample>(HistoryMin * 60 / SampleS + 1);
        if (!ch.Installed) return output;
        var s = spec ?? new ChSpec { TempC = ch.TempC, P = ch.PressureMPa };
        bool shared = dev.Model.StirMode == StirMode.Shared;
        var tl = s.Tl ?? TlSpec.Empty;
        int lag = (int)JsNumber.Round(Ms(now, endT) / (SampleS * 1000.0));
        double Nz(double a) => (rand.Next() - 0.5) * 2 * a;
        for (int i = HistoryMin * 60 / SampleS; i >= lag; i--)
        {
            double m = i * SampleS / 60.0;
            DateTime t = AddMs(now, -(double)i * SampleS * 1000);
            double tv = CurveT(s, m), p = CurveP(s, m);
            double spOr = Truthy(s.SpC) ? s.SpC!.Value : tv;
            bool settled = Math.Abs(tv - spOr) < 1;
            tv += settled ? Nz(0.12) : Nz(0.05);
            p += Nz(Truthy(s.PTarget) ? s.PTarget!.Value * 0.002 : 0.0006);
            double h = CurveH(s, m, tv);
            if (h > 0 && h < 100) h = Clamp(h + Nz(2.5), 0, 100);
            double rpm;
            if (shared) rpm = dev.Stir is { On: true } st && m <= 58 ? st.Rpm : 0;
            else if (tl.Held != null) rpm = m >= U(tl.Until) ? (Truthy(tl.RpmBefore) ? tl.RpmBefore!.Value : Or(s.Rpm, 0)) : Or(s.Rpm, 0);
            else rpm = s.StirOn && m <= Or(tl.StirAt, 58) ? U(s.Rpm) : 0;
            output.Add(new DemoSample(t, Fixed(tv, 2), Fixed(Math.Max(0, p), 4), JsNumber.Round(h), Math.Max(0, Or(rpm, 0)), CurveMmol(s, m), CurveSp(s, m), CurvePt(s, m)));
        }
        if (output.Count > 0)
        {
            var last = output[^1];
            output[^1] = last with
            {
                TempC = ch.TempC, PressureMPa = ch.PressureMPa, HeaterPct = ch.HeaterPct, Rpm = dev.RpmOf(ch), Mmol = ch.Mmol,
                SpC = ch.Heating || ch.Cooling ? ch.SpC : null, PTargetMPa = ch.PTargetMPa,
            };
        }
        return output;
    }

    /// <summary>样稿 stressHistory：贴着最坏值的平稳 60 min 历史。</summary>
    public static List<DemoSample> Stress(StressValues v, DemoRng rand, DateTime now, DateTime endT)
    {
        var output = new List<DemoSample>(HistoryMin * 60 / SampleS + 1);
        int lag = (int)JsNumber.Round(Ms(now, endT) / (SampleS * 1000.0));
        for (int i = HistoryMin * 60 / SampleS; i >= lag; i--)
        {
            bool last = i == lag;
            double tv = last ? v.T : Fixed(v.T - rand.Next() * 0.15, 2);
            double p = last ? v.P : Fixed(v.P * (1 - rand.Next() * 0.002), 4);
            output.Add(new DemoSample(AddMs(now, -(double)i * SampleS * 1000), tv, p, 100, v.Rpm, v.Mmol, v.Sp, v.P));
        }
        return output;
    }

    /// <summary>样稿 sampleOf：每 10 s 追加的一个采样。</summary>
    public static DemoSample Sample(DemoDevice dev, DemoChannel ch, DateTime now) =>
        new(now, Fixed(ch.TempC, 2), Fixed(ch.PressureMPa, 4), JsNumber.Round(Or(ch.HeaterPct, 0)), dev.RpmOf(ch), ch.Mmol,
            (ch.Heating || ch.Cooling) && ch.SpC != null ? ch.SpC : null, ch.PTargetMPa);

    public static double Ms(DateTime a, DateTime b) => (a - b).Ticks / (double)TimeSpan.TicksPerMillisecond;

    public static DateTime AddMs(DateTime t, double ms) => t.AddTicks((long)Math.Round(ms * TimeSpan.TicksPerMillisecond));
}

/// <summary>压力测试数据的最坏值（样稿 stressVals）：温度、设定值、压力（按型号小数位）、小数位、转速、溶液、耗气。</summary>
public sealed record StressValues(double T, double Sp, double P, int Dec, double Rpm, double Vol, double Mmol)
{
    /// <summary>样稿 stressVals(m)。</summary>
    public static StressValues Of(ModelProfile m)
    {
        var l = m.Limits;
        int dec = l.PressureMaxMPa >= 5 ? 2 : 3;
        return new StressValues(DemoHistory.Fixed(l.TempMaxC - 0.1, 1), l.TempMaxC, DemoHistory.Fixed(l.PressureMaxMPa * 0.999, dec), dec, l.RpmMax, m.Vessel.MaxFillMl, 1234.5);
    }
}
