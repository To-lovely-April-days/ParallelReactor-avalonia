using System;
using System.Globalization;

namespace ParallelReactor.Control.Core.Records;

/// <summary>会话状态标签的语气（样稿 rcState：进行中 = 运行绿带点、中断 / 完成 · 有提醒 = 琥珀、已结束 = 白底灰字）。</summary>
public enum RecTagTone { Run, Warn, Plain }

/// <summary>记录页的文字（样稿 PAGES.rec 的写法，纯函数）。</summary>
public static class RecTexts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>列表分组（样稿 rcDay）：今天 / 昨天 / 更早（按开始的日期，相对 <paramref name="today"/>）。</summary>
    public static string DayGroup(DateTime start, DateTime today)
    {
        var d = (today.Date - start.Date).TotalDays;
        return d <= 0 ? "今天" : d <= 1 ? "昨天" : "更早";
    }

    /// <summary>时长（样稿 dur）：「1 h 25 min」「4 h 05 min」「42 min」「0 min」。</summary>
    public static string Dur(TimeSpan t)
    {
        int min = Math.Max(0, (int)Math.Floor(t.TotalMinutes + 1e-9));
        int h = min / 60, m = min % 60;
        return h > 0 ? h.ToString(Inv) + " h " + m.ToString("00", Inv) + " min" : m.ToString(Inv) + " min";
    }

    /// <summary>「11-15」。</summary>
    public static string Day(DateTime t) => t.ToString("MM-dd", Inv);

    /// <summary>「13:05」。</summary>
    public static string Hm(DateTime t) => t.ToString("HH:mm", Inv);

    /// <summary>横轴刻度（样稿 rcClock）：开始时刻 + m 分钟（四舍五入到分钟）。</summary>
    public static string Clock(DateTime start, double m) => Hm(start.AddMinutes(Math.Round(m, MidpointRounding.AwayFromZero)));

    /// <summary>状态标签（样稿 rcState）。</summary>
    public static (string Text, RecTagTone Tone) Tag(RecSession s) => s.State switch
    {
        RecState.Active => ("进行中", RecTagTone.Run),
        RecState.Interrupted => ("中断", RecTagTone.Warn),
        _ when s.Warn != null => ("完成 · 有提醒", RecTagTone.Warn),
        _ => ("已结束", RecTagTone.Plain),
    };

    /// <summary>列表第二行的程序名（没有程序的泄漏测试写「泄漏测试」）。</summary>
    public static string ProgramText(RecSession s) => s.HasProgram ? s.Program : s.Hold == RecHold.Leak ? "泄漏测试" : "—";

    /// <summary>与平均相比（样稿 dvp）：「+27%」「−85%」（减号 U+2212，四舍五入到整数）。</summary>
    public static string Pct(double rel) => (rel >= 0 ? "+" : "−") + Format.JsNumber.ToFixed(Math.Abs(rel), 0) + "%";

    /// <summary>一位小数（吸气量 mmol）。同样稿 toFixed（按二进制的精确值舍入；.NET 的「0.0」先取 15 位有效数字，17.449999… 会变成 17.5）。</summary>
    public static string F1(double v) => Format.JsNumber.ToFixed(v, 1);

    /// <summary>两位小数（速率、压降）。</summary>
    public static string F2(double v) => Format.JsNumber.ToFixed(v, 2);

    /// <summary>保压类测试压力的说明：「惰性气体 · 约 50 psi」（psi 取整到 5）。</summary>
    public static string HoldGasLine(double mpa) =>
        "惰性气体 · 约 " + (Math.Round(mpa * RecCompute.PsiPerMPa / 5, MidpointRounding.AwayFromZero) * 5).ToString("0", Inv) + " psi";

    /// <summary>KPI「平均吸气」的说明：「按 6 路 · 当前值」。</summary>
    public static string MeanSub(int n, bool live) => "按 " + n.ToString(Inv) + " 路" + (live ? " · 当前值" : "");

    /// <summary>结果表统计行：「平均 · SD 6.7 · RSD 46.3 % · 6 路」+「（RV5 未运行，不计入）」。</summary>
    public static string StatsLine(RecStats st) =>
        "平均 · SD " + F1(st.Sd) + " · RSD " + F1(st.Rsd) + " % · " + st.N.ToString(Inv) + " 路";

    public static string SkipLine(System.Collections.Generic.IReadOnlyList<int> skipped) =>
        skipped.Count == 0 ? "" : "（" + string.Join("、", System.Linq.Enumerable.Select(skipped, n => "RV" + n.ToString(Inv))) + " 未运行，不计入）";
}
