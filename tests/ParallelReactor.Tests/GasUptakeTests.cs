using System;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>吸气量计量（<see cref="GasUptakeMeter"/>）：压降 → mmol、补气不计入、噪声不累积、温度补偿、离线 / 改体积 / 速率。</summary>
public class GasUptakeTests
{
    private const double V = 40;   // 气相体积 mL

    /// <summary>理想气体：同一 P、T、V 下气相 mmol 之差（psi 表压）。</summary>
    private static double Dn(double p0, double t0, double p1, double t1, double v = V)
        => GasUptakeMeter.GasMmol(p0, t0, v) - GasUptakeMeter.GasMmol(p1, t1, v);

    /// <summary>开始计量并越过静置期：返回下一个采样时刻。</summary>
    private static double Begin(GasUptakeMeter m, double p, double t, long gen = 0)
    {
        Assert.True(m.Start(0, gen));
        for (double s = 0; s <= GasUptakeMeter.SettleSec; s += 1) m.Sample(s, true, p, t, V, false, gen);
        return GasUptakeMeter.SettleSec + 1;
    }

    [Fact]
    public void GasMmol_MatchesIdealGas()
    {
        // 50 mL、表压 0（1 atm）、25 ℃：n = 101325 × 50e-6 / (8.314462618 × 298.15) = 2.0437 mmol
        Assert.Equal(2.0437, GasUptakeMeter.GasMmol(0, 25, 50), 3);
        Assert.Equal(46.0, GasUptakeMeter.GasVolumeMl(50, 6, 2));
        Assert.Equal(52.0, GasUptakeMeter.GasVolumeMl(50, -3, 2));   // 负的溶液体积按 0
        // 0.001 MPa（0.145 psi）在 40 mL、25 ℃ 下约 0.016 mmol
        Assert.Equal(0.0161, GasUptakeMeter.ResolutionMmol(0.145038, 40), 3);
    }

    [Fact]
    public void ConstantPressure_NoUptake()
    {
        var m = new GasUptakeMeter();
        double t = Begin(m, 300, 100);
        for (int i = 0; i < 100; i++) m.Sample(t + i, true, 300, 100, V, false, 0);
        Assert.True(m.Valid);
        Assert.Equal(0, m.Mmol, 9);
    }

    [Fact]
    public void PressureDrop_ValveClosed_CountsAsUptake()
    {
        var m = new GasUptakeMeter();
        double t = Begin(m, 300, 100);
        for (int i = 1; i <= 100; i++) m.Sample(t + i, true, 300 - 0.1 * i, 100, V, false, 0);
        Assert.Equal(Dn(300, 100, 290, 100), m.Mmol, 9);
        Assert.Equal(0.8889, m.Mmol, 3);   // 10 psi × 40 mL @ 100 ℃
    }

    [Fact]
    public void Refill_IsNotCounted_ButConsumptionBeforeAndAfterIs()
    {
        var m = new GasUptakeMeter();
        double t = Begin(m, 300, 100);
        // 消耗 10 psi
        for (int i = 1; i <= 10; i++) m.Sample(t++, true, 300 - i, 100, V, false, 0);
        double before = m.Mmol;
        // 稳压补气：SV 打开一次（计数 +1），压力回到 300；补气发生在两次采样之间（采样时 SV 已关）
        m.Sample(t++, true, 300, 100, V, false, 1);
        Assert.Equal(before, m.Mmol, 9);
        Assert.Equal(1, m.Refills);
        // 静置期内的读数（补气后的压力波动）不计
        m.Sample(t++, true, 299.5, 100, V, false, 1);
        m.Sample(t++, true, 300, 100, V, false, 1);
        Assert.Equal(before, m.Mmol, 9);
        // 静置后重新取基准，再消耗 5 psi
        m.Sample(t++, true, 300, 100, V, false, 1);
        for (int i = 1; i <= 5; i++) m.Sample(t++, true, 300 - i, 100, V, false, 1);
        Assert.Equal(Dn(300, 100, 290, 100) + Dn(300, 100, 295, 100), m.Mmol, 9);
    }

    [Fact]
    public void ValveOpenDuringSample_IsSkipped()
    {
        var m = new GasUptakeMeter();
        double t = Begin(m, 300, 100);
        m.Sample(t++, true, 295, 100, V, false, 0);
        double a = m.Mmol;
        m.Sample(t++, true, 280, 100, V, true, 0);    // SV 开着：排空 / 手动开阀，压降不是消耗
        m.Sample(t++, true, 260, 100, V, true, 0);
        Assert.Equal(a, m.Mmol, 9);
        for (int i = 0; i <= 3; i++) m.Sample(t++, true, 260, 100, V, false, 0);   // 关阀、静置、取基准
        Assert.Equal(a, m.Mmol, 9);
        m.Sample(t++, true, 258, 100, V, false, 0);
        Assert.Equal(a + Dn(260, 100, 258, 100), m.Mmol, 9);
    }

    [Fact]
    public void Noise_DoesNotAccumulate()
    {
        var m = new GasUptakeMeter();
        double t = Begin(m, 300, 100);
        var rnd = new Random(7);
        double last = 300;
        for (int i = 0; i < 20000; i++) { last = 300 + (rnd.NextDouble() - 0.5); m.Sample(t + i, true, last, 100, V, false, 0); }
        // 相邻差值累加只剩首尾：结果 = 基准时刻与最后一次读数之差，不随采样数增长
        Assert.True(Math.Abs(m.Mmol) <= Dn(300.5, 100, 299.5, 100) + 1e-9, $"噪声累积到 {m.Mmol}");
    }

    [Fact]
    public void Heating_AtConstantGasAmount_IsCompensated()
    {
        // 密闭升温：气量不变，P绝 ∝ T —— 不能算成「产生气体」
        var m = new GasUptakeMeter();
        double pAbs0 = 300 + GasUptakeMeter.AtmPsi;
        double t = Begin(m, 300, 25);
        for (int i = 1; i <= 50; i++)
        {
            double tc = 25 + i;
            double pAbs = pAbs0 * (tc + 273.15) / (25 + 273.15);
            m.Sample(t + i, true, pAbs - GasUptakeMeter.AtmPsi, tc, V, false, 0);
        }
        Assert.Equal(0, m.Mmol, 9);
    }

    [Fact]
    public void Offline_SkipsSamples_ButKeepsBaseline()
    {
        var m = new GasUptakeMeter();
        double t = Begin(m, 300, 100);
        m.Sample(t++, false, double.NaN, double.NaN, V, false, 0);   // 模块离线：读数无效
        m.Sample(t++, false, 0, 0, V, false, 0);
        m.Sample(t++, true, 296, 100, V, false, 0);                  // 恢复：离线期间的压降一并计入
        Assert.Equal(Dn(300, 100, 296, 100), m.Mmol, 9);
    }

    [Fact]
    public void VolumeChange_Rebaselines_WithoutJump()
    {
        var m = new GasUptakeMeter();
        double t = Begin(m, 300, 100);
        m.Sample(t++, true, 298, 100, V, false, 0);
        double a = m.Mmol;
        m.Sample(t++, true, 298, 100, V - 5, false, 0);   // 溶液体积改了：气相体积变小，不能算成消耗
        Assert.Equal(a, m.Mmol, 9);
        m.Sample(t++, true, 297, 100, V - 5, false, 0);
        Assert.Equal(a + Dn(298, 100, 297, 100, V - 5), m.Mmol, 9);
        m.Sample(t++, true, 297, 100, 0.1, false, 0);     // 不合理的体积：不计
        m.Sample(t++, true, 290, 100, 0.1, false, 0);
        Assert.Equal(a + Dn(298, 100, 297, 100, V - 5), m.Mmol, 9);
    }

    [Fact]
    public void Rate_IsLastMinuteAverage()
    {
        var m = new GasUptakeMeter();
        double t = Begin(m, 300, 100);
        Assert.Null(m.RateMmolPerMin);
        // 每秒降 0.05 psi，持续 3 分钟：速率 = 3 psi/min 对应的 mmol/min
        for (int i = 1; i <= 180; i++) m.Sample(t + i, true, 300 - 0.05 * i, 100, V, false, 0);
        Assert.NotNull(m.RateMmolPerMin);
        Assert.Equal(Dn(300, 100, 297, 100), m.RateMmolPerMin!.Value, 6);
    }

    [Fact]
    public void Lifecycle_StartStopReset()
    {
        var m = new GasUptakeMeter();
        Assert.False(m.Valid);
        Assert.False(m.Sample(0, true, 300, 100, V, false, 0));   // 没开始：不计
        double t = Begin(m, 300, 100, gen: 5);
        Assert.False(m.Start(t, 5));                              // 已在计量：连续升压只是补气，不清零
        m.Sample(t++, true, 295, 100, V, false, 5);
        double a = m.Mmol;
        Assert.True(a > 0);
        m.Stop();                                                 // 结束步骤：停止计量，保留读数
        Assert.False(m.Counting);
        Assert.True(m.Valid);
        m.Sample(t++, true, 250, 100, V, false, 5);
        Assert.Equal(a, m.Mmol, 9);
        Assert.Null(m.RateMmolPerMin);
        m.Reset();                                                // 新程序：回到「—」
        Assert.False(m.Valid);
        Assert.Equal(0, m.Mmol);
        Assert.True(m.Start(t, 5));
        Assert.Equal(0, m.Mmol);
    }
}
