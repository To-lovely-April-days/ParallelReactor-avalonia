using System;
using System.Globalization;

namespace ParallelReactor.Control.Core.Format;

/// <summary>
/// 样稿 PR.fmt（js/core.js）的移植：同样的输入给出逐字节相同的字符串（测试用样稿函数算出的期望值逐条对照）。
/// <list type="bullet">
/// <item>缺失值（null / NaN / ±∞）一律「—」。</item>
/// <item>带 U 后缀的返回「值 + 普通空格 + 单位」；断行保护（U+00A0）交给 <see cref="Text.Keep"/>，与样稿一样。</item>
/// <item>压力输入一律 MPa（HMI 的 psi 先用 <see cref="Units.PsiToMpa(double)"/> 换算）；按型号的小数位见 <see cref="Units.FmtP"/>。</item>
/// <item>日期按本地时间：Kind = Utc 的先转本地，其余按日历字段原样输出（同 JS Date 的 getHours 等）。</item>
/// </list>
/// </summary>
public static class Fmt
{
    public const string Dash = "—";

    /// <summary>样稿 PR.fmt.PSI（只有 4 位小数）。只给 <see cref="Psi"/> / <see cref="PsiU"/> 用，保证与样稿逐字节一致；其它换算一律用 <see cref="Units.PsiPerMpa"/>。</summary>
    public const double MockPsiPerMpa = 145.0377;

    private static readonly string[] Week = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    private static bool Ok(double? v) => v.HasValue && double.IsFinite(v.Value);

    /// <summary>d 位小数（默认 1）。</summary>
    public static string Num(double? v, int? d = null) => Ok(v) ? JsNumber.ToFixed(v!.Value, d ?? 1) : Dash;

    /// <summary>温度数值（默认 1 位小数）：119.8。</summary>
    public static string Temp(double? v, int? d = null) => Num(v, d);

    /// <summary>温度 + 单位：119.8 ℃。</summary>
    public static string TempU(double? v, int? d = null) => Ok(v) ? Num(v, d) + " ℃" : Dash;

    /// <summary>压力（MPa）：默认 3 位小数，|v| ≥ 10 时 2 位。</summary>
    public static string Mpa(double? v, int? d = null) => Ok(v) ? JsNumber.ToFixed(v!.Value, d ?? (Math.Abs(v.Value) >= 10 ? 2 : 3)) : Dash;

    public static string MpaU(double? v, int? d = null) => Ok(v) ? Mpa(v, d) + " MPa" : Dash;

    /// <summary>MPa → psi（默认 1 位小数），按样稿的 145.0377。</summary>
    public static string Psi(double? mpa, int? d = null) => Ok(mpa) ? JsNumber.ToFixed(mpa!.Value * MockPsiPerMpa, d ?? 1) : Dash;

    public static string PsiU(double? mpa, int? d = null) => Ok(mpa) ? Psi(mpa, d) + " psi" : Dash;

    /// <summary>转速（整数，JS Math.round）。</summary>
    public static string Rpm(double? v) => Ok(v) ? Int(v!.Value) : Dash;

    public static string RpmU(double? v) => Ok(v) ? Int(v!.Value) + " rpm" : Dash;

    /// <summary>百分比（整数）。</summary>
    public static string Pct(double? v) => Ok(v) ? Int(v!.Value) : Dash;

    public static string PctU(double? v) => Ok(v) ? Int(v!.Value) + " %" : Dash;

    /// <summary>耗气 mmol（默认 2 位小数）。</summary>
    public static string Mmol(double? v, int? d = null) => Ok(v) ? JsNumber.ToFixed(v!.Value, d ?? 2) : Dash;

    /// <summary>体积 mL（整数）。</summary>
    public static string Ml(double? v) => Ok(v) ? Int(v!.Value) : Dash;

    /// <summary>分钟 → '42 min' / '1 h 12 min'（小时后的分钟补两位：'1 h 05 min'）。负数按 0。</summary>
    public static string Minutes(double? m)
    {
        if (!Ok(m)) return Dash;
        double r = Math.Max(0, JsNumber.Round(m!.Value));
        return r < 60 ? JsNumber.ToString(r) + " min" : JsNumber.ToString(Math.Floor(r / 60)) + " h " + Pad2(r % 60) + " min";
    }

    /// <summary>秒 → 'MM:SS' 或 'H:MM:SS'。负数按 0。</summary>
    public static string Dur(double? sec)
    {
        if (!Ok(sec)) return Dash;
        double s = Math.Max(0, JsNumber.Round(sec!.Value)), h = Math.Floor(s / 3600), m = Math.Floor(s % 3600 / 60);
        return (h != 0 ? JsNumber.ToString(h) + ":" + Pad2(m) : Pad2(m)) + ":" + Pad2(s % 60);
    }

    /// <summary>'HH:MM:SS'。</summary>
    public static string Time(DateTime? d)
    {
        if (d is not { } v) return Dash;
        var t = Local(v);
        return Pad2(t.Hour) + ":" + Pad2(t.Minute) + ":" + Pad2(t.Second);
    }

    /// <summary>'HH:MM'。</summary>
    public static string Hm(DateTime? d)
    {
        if (d is not { } v) return Dash;
        var t = Local(v);
        return Pad2(t.Hour) + ":" + Pad2(t.Minute);
    }

    /// <summary>'YYYY-MM-DD'（年份不补位，同 JS getFullYear）。</summary>
    public static string Date(DateTime? d)
    {
        if (d is not { } v) return Dash;
        var t = Local(v);
        return t.Year.ToString(CultureInfo.InvariantCulture) + "-" + Pad2(t.Month) + "-" + Pad2(t.Day);
    }

    /// <summary>'YYYY-MM-DD HH:MM:SS'（样稿 datetime）。</summary>
    public static string Datetime(DateTime? d) => d is { } v ? Date(v) + " " + Time(v) : Dash;

    /// <summary>'周日' … '周六'。</summary>
    public static string Weekday(DateTime? d) => d is { } v ? Week[(int)Local(v).DayOfWeek] : Dash;

    /// <summary>距今多久（毫秒）：'刚刚'（&lt; 5 s）/ '42 s 前' / '25 min 前' / '1 h 05 min 前'。</summary>
    public static string Ago(double? ms)
    {
        if (!Ok(ms)) return Dash;
        double s = Math.Max(0, JsNumber.Round(ms!.Value / 1000));
        if (s < 5) return "刚刚";
        if (s < 60) return JsNumber.ToString(s) + " s 前";
        double m = Math.Floor(s / 60);
        if (m < 60) return JsNumber.ToString(m) + " min 前";
        return JsNumber.ToString(Math.Floor(m / 60)) + " h " + Pad2(m % 60) + " min 前";
    }

    /// <summary>PR.pad2：String(n).padStart(2, '0')。</summary>
    public static string Pad2(double n) => JsNumber.ToString(n).PadLeft(2, '0');

    private static string Int(double v) => JsNumber.ToString(JsNumber.Round(v));

    private static DateTime Local(DateTime t) => t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
}
