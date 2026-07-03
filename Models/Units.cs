using System;

namespace ParallelReactor.Models;

/// <summary>
/// 全局单位换算中心。压力内部一律以 <b>psi</b> 存储与判定（报警阈值、SP 等都按 psi），
/// 仅在<i>显示</i>时按用户选定单位换算，避免动到业务逻辑。
/// </summary>
public static class Units
{
    private const double PsiPerBar = 14.5037738;
    private const double PsiPerMpa = 145.037738;   // 1 MPa = 145.0377 psi

    /// <summary>当前压力显示单位："MPa" / "bar" / "psi"。默认 MPa。</summary>
    public static string Pressure { get; private set; } = "MPa";

    /// <summary>单位变化事件；界面订阅后刷新压力显示。</summary>
    public static event Action? Changed;

    /// <summary>设置压力显示单位（非法值归 MPa）。变化时触发 <see cref="Changed"/>。</summary>
    public static void SetPressure(string? unit)
    {
        var u = unit switch { "psi" => "psi", "bar" => "bar", _ => "MPa" };
        if (u == Pressure) return;
        Pressure = u;
        Changed?.Invoke();
    }

    /// <summary>把 psi 数值换算为当前显示单位的数值。</summary>
    public static double P(double psi) => Pressure switch
    {
        "bar" => psi / PsiPerBar,
        "MPa" => psi / PsiPerMpa,
        _ => psi
    };

    /// <summary>当前压力单位标签（"MPa" / "bar" / "psi"）。</summary>
    public static string PLabel => Pressure;

    /// <summary>当前压力单位的小数位数：MPa 3、bar 2、psi 1。</summary>
    public static int PDecimals => Pressure switch { "bar" => 2, "psi" => 1, _ => 3 };

    /// <summary>把 psi 换算为当前单位并格式化为数值字符串（不含单位）。如 "0.345" / "3.45" / "50.0"。</summary>
    public static string PValue(double psi)
        => P(psi).ToString(PDecimals == 0 ? "0" : "0." + new string('0', PDecimals));

    /// <summary>格式化压力（含单位）：MPa 三位小数、bar 两位、psi 一位。
    /// 如 "0.345 MPa" / "3.45 bar" / "50.0 psi"。</summary>
    public static string FormatP(double psi) => $"{PValue(psi)} {PLabel}";

    /// <summary>MPa → psi（用于把变送器 MPa 量程换算成内部 psi 满量程）。</summary>
    public static double MpaToPsi(double mpa) => mpa * PsiPerMpa;

    /// <summary>psi → MPa。</summary>
    public static double PsiToMpa(double psi) => psi / PsiPerMpa;
}
