using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Format;

/// <summary>
/// 单位：界面以 MPa 为主、psi 为次要信息（D8）；HMI 线上一律 psi（PressurePsi / PressureSpPsi / 报警阈值），温度 ℃（units.temperature = "C"）。
/// 压力显示的小数位按型号量程：≥ 5 MPa 用 2 位（5.20），否则 3 位（0.349）——样稿 PR.sim.pDec / fmtP。
/// </summary>
public static class Units
{
    /// <summary>1 MPa = 145.037738 psi（与 HMI 的 Models/Units.cs、TelemetryCsv 一致）。</summary>
    public const double PsiPerMpa = 145.037738;

    /// <summary>界面上的单位写法（与样稿一致：温度一律 U+2103「℃」，不写「°C」）。</summary>
    public const string Celsius = "℃", Mpa = "MPa", Psi = "psi", Rpm = "rpm", Percent = "%", Ml = "mL", Minute = "min";

    public static double MpaToPsi(double mpa) => mpa * PsiPerMpa;

    public static double PsiToMpa(double psi) => psi / PsiPerMpa;

    /// <summary>null（无效读数）照样返回 null。</summary>
    public static double? MpaToPsi(double? mpa) => mpa * PsiPerMpa;

    /// <summary>null（无效读数）照样返回 null。</summary>
    public static double? PsiToMpa(double? psi) => psi / PsiPerMpa;

    /// <summary>压力小数位：量程 ≥ 5 MPa 用 2 位，否则 3 位。</summary>
    public static int PressureDecimals(double pressureMaxMPa) => pressureMaxMPa >= 5 ? 2 : 3;

    /// <summary>按型号格式化压力数值（不带单位）：样稿 PR.sim.fmtP；型号未知时 3 位。</summary>
    public static string FmtP(ModelProfile? model, double? mpa) => Fmt.Mpa(mpa, model?.PressureDecimals ?? 3);

    /// <summary>按型号格式化压力 + 单位：'0.350 MPa' / '5.20 MPa'。</summary>
    public static string FmtPU(ModelProfile? model, double? mpa) => Fmt.MpaU(mpa, model?.PressureDecimals ?? 3);

    /// <summary>HMI 的 psi 读数 → 按型号小数位的 MPa 数值文字。</summary>
    public static string FmtPFromPsi(ModelProfile? model, double? psi) => FmtP(model, PsiToMpa(psi));

    /// <summary>psi 读数原样显示（默认 1 位小数）：PT 标注、属性卡片的次要单位。HMI 本来就是 psi，不经 MPa 来回换算。</summary>
    public static string PsiRaw(double? psi, int decimals = 1) => Fmt.Num(psi, decimals);
}
