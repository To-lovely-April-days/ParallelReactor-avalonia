using System;

namespace ParallelReactor.Models;

/// <summary>
/// 全局单位换算中心。压力内部一律以 <b>psi</b> 存储与判定（报警阈值、SP 等都按 psi），
/// 仅在<i>显示</i>时按用户选定单位换算，避免动到业务逻辑。
/// </summary>
public static class Units
{
    private const double PsiPerBar = 14.5037738;

    /// <summary>当前压力显示单位："psi" 或 "bar"。</summary>
    public static string Pressure { get; private set; } = "psi";

    /// <summary>单位变化事件；界面订阅后刷新压力显示。</summary>
    public static event Action? Changed;

    /// <summary>设置压力显示单位（非法值归 psi）。变化时触发 <see cref="Changed"/>。</summary>
    public static void SetPressure(string? unit)
    {
        var u = unit == "bar" ? "bar" : "psi";
        if (u == Pressure) return;
        Pressure = u;
        Changed?.Invoke();
    }

    /// <summary>把 psi 数值换算为当前显示单位的数值。</summary>
    public static double P(double psi) => Pressure == "bar" ? psi / PsiPerBar : psi;

    /// <summary>当前压力单位标签（"psi" / "bar"）。</summary>
    public static string PLabel => Pressure;

    /// <summary>格式化压力（含单位）：bar 用两位小数、psi 用一位。如 "3.45 bar" / "50.0 psi"。</summary>
    public static string FormatP(double psi) =>
        Pressure == "bar" ? $"{psi / PsiPerBar:0.00} bar" : $"{psi:0.0} psi";
}
