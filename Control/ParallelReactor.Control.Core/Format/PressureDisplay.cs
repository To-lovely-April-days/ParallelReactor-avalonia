using System;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Format;

/// <summary>压力的显示单位。</summary>
public enum PressureUnit { MPa, Psi }

/// <summary>
/// 界面上压力的主 / 次单位（决定 D8：MPa 为主、psi 为次）。<b>全界面只有这一处设置</b>：步骤文字、条件框、剩余时间里的
/// 「→ 0.350 MPa」、读数、提示都经这里格式化。用户要改成 psi 为主时，只改 <see cref="Primary"/> 这一行。
/// MPa 按型号的小数位（量程 ≥ 5 MPa 用 2 位，否则 3 位，同样稿 PR.sim.fmtP）；psi 一律 1 位小数，按 <see cref="Units.PsiPerMpa"/> 换算。
/// 演示数据里的原文（报警 / 事件句子、程序说明）是数据，不经这里改写。
/// 取整后是 0 的负读数（真机静置时传感器的零点漂移，例如 −0.0004 MPa）写成「0.000」，不写「-0.000」；「-0.001」这类不是 0 的照写（触屏也这样写）。
/// </summary>
public static class PressureDisplay
{
    /// <summary>主单位。改成 <c>PressureUnit.Psi</c> = psi 为主、MPa 为次（只改这一行）。</summary>
    public static readonly PressureUnit Primary = PressureUnit.MPa;

    /// <summary>次单位（与主单位相反）。</summary>
    public static PressureUnit Secondary => Primary == PressureUnit.MPa ? PressureUnit.Psi : PressureUnit.MPa;

    /// <summary>主单位的写法：'MPa' / 'psi'。</summary>
    public static string Unit => UnitText(Primary);

    /// <summary>次单位的写法。</summary>
    public static string SecondaryUnit => UnitText(Secondary);

    public static string UnitText(PressureUnit unit) => unit == PressureUnit.Psi ? Units.Psi : Units.Mpa;

    /// <summary>按主单位格式化（不带单位）：'0.350' / '5.20'（MPa）或 '50.8'（psi）。null → '—'。</summary>
    public static string Value(ModelProfile? model, double? mpa) => Format(Primary, model, mpa);

    /// <summary>按主单位格式化 + 单位：'0.350 MPa'。null → '—'。</summary>
    public static string WithUnit(ModelProfile? model, double? mpa) => FormatU(Primary, model, mpa);

    /// <summary>次单位的数值（不带单位）。</summary>
    public static string SecondaryValue(ModelProfile? model, double? mpa) => Format(Secondary, model, mpa);

    /// <summary>次单位的数值 + 单位：'50.8 psi'。</summary>
    public static string SecondaryWithUnit(ModelProfile? model, double? mpa) => FormatU(Secondary, model, mpa);

    /// <summary>指定单位格式化（不带单位）。</summary>
    public static string Format(PressureUnit unit, ModelProfile? model, double? mpa) =>
        NoNegativeZero(unit == PressureUnit.Psi ? Fmt.Num(Units.MpaToPsi(mpa), 1) : Units.FmtP(model, mpa));

    /// <summary>「-0.000」「-0.00」「-0.0」→ 去掉负号（JS toFixed 对很小的负数保留负号，Fmt 照样稿逐字节保留；压力读数在这里去掉）。</summary>
    private static string NoNegativeZero(string s) =>
        s.Length > 1 && s[0] == '-' && s.AsSpan(1).IndexOfAnyExcept('0', '.') < 0 ? s[1..] : s;

    /// <summary>指定单位格式化 + 单位；null → '—'。</summary>
    public static string FormatU(PressureUnit unit, ModelProfile? model, double? mpa)
    {
        string v = Format(unit, model, mpa);
        return v == Fmt.Dash ? v : v + " " + UnitText(unit);
    }
}
