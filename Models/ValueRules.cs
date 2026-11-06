using System.Globalization;
using ParallelReactor.Commands;

namespace ParallelReactor.Models;

/// <summary>数值越界：错误码 + 中文消息 + detail（min / max / value）。</summary>
public sealed record RuleViolation(string Code, string Message, double Min, double Max, double Value)
{
    /// <summary>对外 error.detail。</summary>
    public object Detail => new { min = Min, max = Max, value = Value };
}

/// <summary>
/// 各数值字段的范围校验（C5）。命令层统一调用：本地键盘 / 步进器已限幅（不会触发），远程靠这里拒绝。
/// 返回 null = 合法。NaN / ∞ 一律拒绝。纯逻辑：测试工程直接链接本文件。
/// 范围与今天各键盘 / 步进器的限幅一致（抽屉 TSp 10..TMaxSp、体积 1..VolMax、PID 0..3200 / 0..3200 / 0..327……）。
/// </summary>
public static class ValueRules
{
    public const double TempSpMin = 10;
    public const double VolumeMin = 1;
    public const double PidPMax = 3200, PidIMax = 3200, PidDMax = 327;
    public const int ProfileSegMin = 1;
    public const double SegMinutesMin = 0.1, SegMinutesMax = 6000;
    public const double AutotuneSpMin = 40;
    public const double BandBoundMin = 10, BandBoundMax = 390, BandGap = 10;

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>通用闭区间检查。unit 为空时不带单位。</summary>
    public static RuleViolation? Range(string label, double v, double min, double max, string unit = "")
    {
        if (double.IsFinite(v) && v >= min && v <= max) return null;
        string u = unit.Length > 0 ? " " + unit : "";
        return new RuleViolation(OpCodes.ParamOutOfRange, $"{label}须在 {N(min)}–{N(max)}{u} 之间", min, max, v);
    }

    /// <summary>抽屉 / 定值温度 SP：10..TMaxSp °C。</summary>
    public static RuleViolation? TempSp(double v, double tMaxSp) => Range("温度 SP ", v, TempSpMin, tMaxSp, "°C");

    /// <summary>严格守卫：温度 SP 须低于超温报警值（否则一到目标就超温切断）。</summary>
    public static RuleViolation? StrictTempSp(double v, double overTemp)
        => double.IsFinite(v) && v < overTemp ? null
           : new RuleViolation(OpCodes.ParamOutOfRange, $"温度 SP 须低于超温报警值 {N(overTemp)} °C", double.NegativeInfinity, overTemp, v);

    /// <summary>溶液体积：1..VolMax mL。</summary>
    public static RuleViolation? Volume(double ml, double volMax) => Range("溶液体积", ml, VolumeMin, volMax, "mL");

    /// <summary>全局搅拌转速：0..RpmMax rpm。</summary>
    public static RuleViolation? Rpm(double rpm, double rpmMax) => Range("搅拌转速", rpm, 0, rpmMax, "rpm");

    /// <summary>PID：档位 0..bandCount−1，P 0..3200、I 0..3200 s、D 0..327 s。</summary>
    public static RuleViolation? Pid(int band, double p, double i, double d, int bandCount = 3)
        => Range("PID 档位", band, 0, bandCount - 1)
           ?? Range("比例带 P ", p, 0, PidPMax)
           ?? Range("积分时间 I ", i, 0, PidIMax, "s")
           ?? Range("微分时间 D ", d, 0, PidDMax, "s");

    /// <summary>曲线段数：1..16。</summary>
    public static RuleViolation? ProfileSegCount(double n, int max = 16) => Range("曲线段数", n, ProfileSegMin, max, "段");

    /// <summary>曲线某段：温度 0..TMaxSp °C，时长 0.1..6000 分钟（给 null 的项不检查）。</summary>
    public static RuleViolation? ProfileSegment(double? tempC, double? minutes, double tMaxSp)
        => (tempC is { } t ? Range("段目标温度", t, 0, tMaxSp, "°C") : null)
           ?? (minutes is { } m ? Range("段时长", m, SegMinutesMin, SegMinutesMax, "分钟") : null);

    /// <summary>自整定目标温度：40..TMaxSp °C。</summary>
    public static RuleViolation? AutotuneSp(double v, double tMaxSp) => Range("整定目标温度", v, AutotuneSpMin, tMaxSp, "°C");

    /// <summary>
    /// 档位分界：which = 1 为「低温 / 中温」分界（10..中温上限−10），which = 2 为「中温 / 高温」分界（低温上限+10..390）。
    /// lowHi = 当前低温档上限（分界 1），midHi = 当前中温档上限（分界 2）。
    /// </summary>
    public static RuleViolation? BandBound(int which, double value, double lowHi, double midHi) => which switch
    {
        1 => Range("低温 / 中温 分界", value, BandBoundMin, midHi - BandGap, "℃"),
        2 => Range("中温 / 高温 分界", value, lowHi + BandGap, BandBoundMax, "℃"),
        _ => Range("档位分界编号", which, 1, 2),
    };
}
