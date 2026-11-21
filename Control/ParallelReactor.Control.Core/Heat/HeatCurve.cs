using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelReactor.Control.Core.Heat;

/// <summary>曲线升温的一段：在 <see cref="Minutes"/> 分钟内匀速变化到 <see cref="TempC"/> ℃（同 HMI 的 ProfileSegment；目标和上一段相同 = 保温）。</summary>
public sealed record CurveSeg(double TempC, double Minutes);

/// <summary>
/// 一路的曲线升温现状（真机 = ChannelSnap.SpMode + Profile；演示 = DemoChannel）：升温方式是不是曲线、是否在走、各段、已走分钟、总分钟、是否走完。
/// 段 1 从启动时的实测温度开始 —— HMI 不上报这个起点，<see cref="StartC"/> 由数据源尽量给（演示知道；真机在第 1 段时由给定值反推，否则 null）。
/// </summary>
public sealed record CurveReading(bool CurveMode, bool Running, IReadOnlyList<CurveSeg> Segs, double ElapsedMin = 0, double TotalMin = 0, bool Complete = false, double? StartC = null)
{
    public static readonly CurveReading None = new(false, false, Array.Empty<CurveSeg>());
}

public enum CurveKind { Up, Hold, Down }

/// <summary>算好的一段：序号（1 起）、目标、用时、起点温度、开始于第几分钟、速率 ℃/min（带符号）、类别。</summary>
public sealed record CurveRow(int I, double TempC, double Minutes, double FromC, double AtMin, double Rate, CurveKind Kind)
{
    public double EndMin => AtMin + Minutes;
}

/// <summary>运行中的位置：当前段、本段完成比例、本段剩余分钟、此刻的给定值。</summary>
public sealed record CurveNow(CurveRow Row, double F, double LeftMin, double SpC);

/// <summary>
/// 曲线升温的计算与文字（设计稿 research/fusion-port/curve-design，2026-11-21 方向 B）。纯逻辑，单元测试直接调。
/// 范围同 HMI（ValueRules.ProfileSegment）：1–16 段、每段 0.1–6000 min、温度 0 … 设定上限且低于超温值。
/// </summary>
public static class HeatCurve
{
    public const int MaxSegs = 16;
    public const double MinMinutes = 0.1, MaxMinutes = 6000;
    /// <summary>升温快于这个值就提示「可能跟不上」（℃/min）。</summary>
    public const double FastRate = 5;
    /// <summary>没有曲线时给的一条（升到 80 ℃ 用 20 min，再保温 30 min）。</summary>
    public static IReadOnlyList<CurveSeg> Default(double tempMaxC) => new[] { new CurveSeg(Math.Min(80, tempMaxC), 20), new CurveSeg(Math.Min(80, tempMaxC), 30) };

    public static IReadOnlyList<CurveRow> Rows(IReadOnlyList<CurveSeg> segs, double startC)
    {
        var rows = new List<CurveRow>(segs.Count);
        double prev = startC, at = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            double m = s.Minutes > 0 ? s.Minutes : MinMinutes;
            var kind = Math.Abs(s.TempC - prev) < 0.05 ? CurveKind.Hold : s.TempC > prev ? CurveKind.Up : CurveKind.Down;
            rows.Add(new CurveRow(i + 1, s.TempC, s.Minutes, prev, at, (s.TempC - prev) / m, kind));
            prev = s.TempC;
            at += s.Minutes;
        }
        return rows;
    }

    public static double Total(IReadOnlyList<CurveSeg> segs) => segs.Sum(s => s.Minutes);
    public static double Max(IReadOnlyList<CurveSeg> segs) => segs.Count == 0 ? 0 : segs.Max(s => s.TempC);

    /// <summary>运行到第 <paramref name="elapsedMin"/> 分钟时的位置；走完后停在最后一段末尾。</summary>
    public static CurveNow? At(IReadOnlyList<CurveRow> rows, double elapsedMin)
    {
        if (rows.Count == 0) return null;
        foreach (var r in rows)
        {
            if (elapsedMin < r.EndMin)
            {
                double f = r.Minutes > 0 ? Math.Clamp((elapsedMin - r.AtMin) / r.Minutes, 0, 1) : 1;
                return new CurveNow(r, f, r.Minutes * (1 - f), r.FromC + (r.TempC - r.FromC) * f);
            }
        }
        var last = rows[^1];
        return new CurveNow(last, 1, 0, last.TempC);
    }

    /// <summary>真机没有上报起点：在第 1 段时由给定值反推（sp = from + (target − from)·f），否则 null。</summary>
    public static double? InferStart(IReadOnlyList<CurveSeg> segs, double elapsedMin, double? spC)
    {
        if (segs.Count == 0 || spC is not double sp || segs[0].Minutes <= 0) return null;
        double f = elapsedMin / segs[0].Minutes;
        if (f < 0 || f >= 0.98) return null;
        return (sp - segs[0].TempC * f) / (1 - f);
    }

    // ———— 检查 ————

    /// <summary>一段的问题（"" = 没问题）。<paramref name="overTempC"/> = 超温报警值（设定必须低于它；不知道给 null）。</summary>
    public static string SegError(CurveSeg s, double tempMaxC, double? overTempC = null)
    {
        if (!double.IsFinite(s.TempC)) return "请输入目标温度";
        if (!double.IsFinite(s.Minutes)) return "请输入用时";
        if (s.TempC < 0 || s.TempC > tempMaxC) return "目标温度须在 0 – " + N(tempMaxC) + " ℃";
        if (overTempC is double ot && s.TempC >= ot) return "目标温度须低于超温报警值 " + N(ot) + " ℃";
        if (s.Minutes < MinMinutes || s.Minutes > MaxMinutes) return "用时须在 0.1 – 6000 min";
        return "";
    }

    /// <summary>整条曲线的问题（"" = 可以下发）：「第 3 段：用时须在 0.1 – 6000 min」。</summary>
    public static string Error(IReadOnlyList<CurveSeg>? segs, double tempMaxC, double? overTempC = null)
    {
        if (segs is null || segs.Count == 0) return "曲线至少要有 1 段";
        if (segs.Count > MaxSegs) return "曲线最多 16 段";
        for (int i = 0; i < segs.Count; i++)
        {
            string e = SegError(segs[i], tempMaxC, overTempC);
            if (e.Length > 0) return "第 " + (i + 1) + " 段：" + e;
        }
        return "";
    }

    /// <summary>不拦的提示（升得太快 / 降温段）；没有为 ""。</summary>
    public static string Warn(CurveRow r) => r.Kind == CurveKind.Down ? "只能自然冷却，可能降不到" : r.Rate > FastRate ? "升得太快，实际可能跟不上" : "";

    // ———— 文字 ————

    /// <summary>「升 2.7 °C/min」/「保温」/「降 3.0 °C/min」。</summary>
    public static string RateText(CurveRow r) => r.Kind == CurveKind.Hold ? "保温" : (r.Kind == CurveKind.Up ? "升 " : "降 ") + Math.Abs(r.Rate).ToString("0.0", CultureInfo.InvariantCulture) + " °C/min";

    /// <summary>「升到 80 °C」/「保温 80 °C」/「降到 60 °C」。</summary>
    public static string StepText(CurveRow r) => (r.Kind == CurveKind.Hold ? "保温 " : r.Kind == CurveKind.Up ? "升到 " : "降到 ") + N(r.TempC) + " °C";

    /// <summary>「2 h 25 min」/「50 min」。</summary>
    public static string Dur(double minutes)
    {
        double m = Math.Round(minutes);
        return m >= 60 ? ((int)(m / 60)).ToString(CultureInfo.InvariantCulture) + " h " + ((int)(m % 60)).ToString("00", CultureInfo.InvariantCulture) + " min" : N(m) + " min";
    }

    /// <summary>「共 4 段 · 2 h 25 min」。</summary>
    public static string Head(IReadOnlyList<CurveSeg> segs) => "共 " + segs.Count + " 段 · " + Dur(Total(segs));

    /// <summary>「14:32」（本地钟点）。</summary>
    public static string Clock(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>数字：整数不带小数，否则 1 位。</summary>
    public static string N(double v) => Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);
}
