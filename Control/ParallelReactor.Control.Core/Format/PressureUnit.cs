using System;

namespace ParallelReactor.Control.Core.Format;

/// <summary>压力的显示单位（设置 → 常规「压力单位」，同触屏的三种）。</summary>
public enum PressureUnit { MPa, Bar, Psi }

/// <summary>
/// 压力单位的换算与写法（纯函数；当前选的单位与格式化在 <see cref="PressureDisplay"/>）。
/// <b>电脑端内部一律 MPa</b>（HMI 线上的 psi 在数据源边上换成 MPa：ChannelHistory.AppendFrame、ChannelProjection、ProgramDocs）；
/// 只在显示 / 输入时换成选的单位。换算同 HMI 的 Models/Units.cs：1 MPa = 10 bar = 145.037738 psi（1 bar = 14.5037738 psi）。
/// </summary>
public static class PressureUnits
{
    /// <summary>1 MPa = 10 bar。</summary>
    public const double BarPerMpa = 10;

    /// <summary>1 bar = 14.5037738 psi（HMI Models/Units.cs 的 PsiPerBar）。</summary>
    public const double PsiPerBar = 14.5037738;

    /// <summary>三种单位（设置页分段的顺序）。</summary>
    public static readonly PressureUnit[] All = { PressureUnit.MPa, PressureUnit.Bar, PressureUnit.Psi };

    /// <summary>界面写法 = 设置里存的键：'MPa' / 'bar' / 'psi'。</summary>
    public static string Text(PressureUnit u) => u switch { PressureUnit.Bar => "bar", PressureUnit.Psi => Units.Psi, _ => Units.Mpa };

    /// <summary>设置里的键 → 单位（不分大小写；认不出的按 MPa，同触屏的 Units.SetPressure）。</summary>
    public static PressureUnit Parse(string? key) =>
        string.Equals(key, "bar", StringComparison.OrdinalIgnoreCase) ? PressureUnit.Bar
        : string.Equals(key, "psi", StringComparison.OrdinalIgnoreCase) ? PressureUnit.Psi
        : PressureUnit.MPa;

    /// <summary>认得这个键（'MPa' / 'bar' / 'psi'，不分大小写）。</summary>
    public static bool IsKnown(string? key) =>
        string.Equals(key, "MPa", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "bar", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "psi", StringComparison.OrdinalIgnoreCase);

    /// <summary>键规范成界面写法（'mpa' → 'MPa'；认不出的 → 'MPa'）。</summary>
    public static string Normalize(string? key) => Text(Parse(key));

    /// <summary>1 MPa 等于多少个这种单位（MPa 1、bar 10、psi 145.037738）。</summary>
    public static double PerMpa(PressureUnit u) => u switch { PressureUnit.Bar => BarPerMpa, PressureUnit.Psi => Units.PsiPerMpa, _ => 1 };

    /// <summary>MPa → 这种单位。</summary>
    public static double FromMpa(double mpa, PressureUnit u) => u == PressureUnit.MPa ? mpa : mpa * PerMpa(u);

    /// <summary>MPa → 这种单位（null 照样 null）。</summary>
    public static double? FromMpa(double? mpa, PressureUnit u) => mpa is double v ? FromMpa(v, u) : null;

    /// <summary>这种单位 → MPa（输入框的值换回内部单位）。</summary>
    public static double ToMpa(double v, PressureUnit u) => u == PressureUnit.MPa ? v : v / PerMpa(u);

    /// <summary>psi（HMI 线上）→ 这种单位。psi 直接原样（不经 MPa 来回换算）。</summary>
    public static double FromPsi(double psi, PressureUnit u) => u == PressureUnit.Psi ? psi : FromMpa(Units.PsiToMpa(psi), u);

    /// <summary>这种单位 → psi（下发给 HMI）。psi 直接原样。</summary>
    public static double ToPsi(double v, PressureUnit u) => u == PressureUnit.Psi ? v : Units.MpaToPsi(ToMpa(v, u));

    /// <summary>
    /// 读数的小数位：MPa 照调用处给的（型号量程 &lt; 5 MPa 3 位、否则 2 位；表格 / 弹窗 2 位）；bar 2 位；psi 1 位。
    /// 三者的分辨率相当（0.001 MPa = 0.01 bar ≈ 0.15 psi），MPa 的写法与改单位之前逐字节相同。
    /// </summary>
    public static int ValueDecimals(PressureUnit u, int mpaDecimals) => u switch { PressureUnit.Bar => 2, PressureUnit.Psi => 1, _ => mpaDecimals };

    /// <summary>输入框的小数位（程序页的目标压力）：MPa 2（步长 0.01）、bar 1（0.1）、psi 0（1）。</summary>
    public static int InputDecimals(PressureUnit u) => u switch { PressureUnit.Bar => 1, PressureUnit.Psi => 0, _ => 2 };

    /// <summary>输入框的步长：MPa 0.01、bar 0.1、psi 1。</summary>
    public static double InputStep(PressureUnit u) => Math.Pow(10, -InputDecimals(u));

    /// <summary>
    /// 量程 / 上限这类「整数字」的写法（不补尾零）：MPa 按 JS String(number) 原样（'4.14'，与改单位之前相同）、bar 取 1 位（'41.4'）、psi 取整（'600'）。
    /// </summary>
    public static string LimitText(double mpa, PressureUnit u) => u switch
    {
        PressureUnit.Bar => JsNumber.ToString(Math.Round(mpa * BarPerMpa, 1, MidpointRounding.AwayFromZero)),
        PressureUnit.Psi => JsNumber.ToString(Math.Round(Units.MpaToPsi(mpa), MidpointRounding.AwayFromZero)),
        _ => JsNumber.ToString(mpa),
    };
}
