using System;

namespace ParallelReactor.Models;

/// <summary>
/// 搅拌传动换算（纯逻辑：测试工程直接链接本文件）。一台电机经齿轮带 8 个釜：电机 38 齿带釜上 85 齿，
/// 传动比 = 电机转速 ÷ 釜搅拌转速 = 85 / 38 ≈ 2.237（釜转得比电机慢）。界面、程序、接口的转速都是釜里的转速。
/// </summary>
public static class StirGear
{
    /// <summary>出厂传动比 85 / 38。</summary>
    public const double DefaultRatio = 85.0 / 38.0;

    /// <summary>釜搅拌转速 → 电机转速（四舍五入，限幅到 0..maxMotor；传动比非正时按 1）。</summary>
    public static int ToMotor(int vesselRpm, double ratio, int maxMotor)
        => (int)Math.Clamp(Math.Round(vesselRpm * (ratio > 0 ? ratio : 1)), 0, maxMotor);

    /// <summary>电机转速 → 釜搅拌转速。</summary>
    public static double ToVessel(double motorRpm, double ratio) => motorRpm / (ratio > 0 ? ratio : 1);
}
