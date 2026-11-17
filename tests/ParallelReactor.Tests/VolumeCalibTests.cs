using System;
using System.Collections.Generic;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>容积标定（气体膨胀法）的计算：<see cref="VolumeCalib"/>。</summary>
public class VolumeCalibTests
{
    /// <summary>按物理过程生成一遍的数据：总管 vm 充到 preg，放进气相 vg 的釜里；crack = 进气单向阀开启压力（总管比釜高这么多就停止进气）。</summary>
    private static List<(double, double)> Run(double vm, double vg, double preg, double p0 = 0, double crack = 0, int n = VolumeCalib.Steps,
        Random? noise = null, double noisePsi = 0)
    {
        var list = new List<(double, double)>();
        double p = p0;
        for (int k = 0; k < n; k++)
        {
            // Vm·(Preg − (Pk + crack)) = Vg·(Pk − P(k−1))
            double next = (vm * (preg - crack) + vg * p) / (vm + vg);
            double rd(double x) => noise == null ? x : x + (noise.NextDouble() - 0.5) * 2 * noisePsi;
            list.Add((rd(p), rd(next)));
            p = next;
        }
        return list;
    }

    [Fact]
    public void Fit_RecoversRatio_WithoutKnowingSupplyPressure()
    {
        var (fit, why) = VolumeCalib.FitSteps(Run(vm: 8, vg: 52.3, preg: 80));
        Assert.Null(why);
        Assert.Equal(52.3 / 8, fit!.Ratio, 9);
        Assert.Equal(80, fit.SupplyPsi, 6);
        Assert.Equal(1, fit.R2, 9);
    }

    [Fact]
    public void Fit_IgnoresCheckValveCrackingPressure_AndStartPressure()
    {
        // 单向阀开启压力 3 psi、釜初始 5 psi：只影响截距，比值不变
        var (fit, why) = VolumeCalib.FitSteps(Run(vm: 8, vg: 40, preg: 80, p0: 5, crack: 3));
        Assert.Null(why);
        Assert.Equal(5.0, fit!.Ratio, 9);
        Assert.Equal(77, fit.SupplyPsi, 6);   // 等效出口压力 = 80 − 3
    }

    [Fact]
    public void Fit_ToleratesSmallNoise()
    {
        var (fit, why) = VolumeCalib.FitSteps(Run(vm: 8, vg: 52, preg: 100, noise: new Random(3), noisePsi: 0.05));
        Assert.Null(why);
        Assert.InRange(fit!.Ratio, 6.5 * 0.98, 6.5 * 1.02);
    }

    [Fact]
    public void Fit_RejectsBadData()
    {
        Assert.NotNull(VolumeCalib.FitSteps(Run(8, 52, 80, n: 3)).Why);                       // 数据太少
        Assert.Contains("只升了", VolumeCalib.FitSteps(Run(8, 52, 0.5)).Why);                 // 减压阀几乎没压力
        Assert.NotNull(VolumeCalib.FitSteps(Run(8, 52, 80, noise: new Random(1), noisePsi: 3)).Why);   // 跳动太大
        var bad = Run(8, 52, 80);
        bad[2] = (bad[2].Item1, double.NaN);
        Assert.Equal("压力读数无效", VolumeCalib.FitSteps(bad).Why);
    }

    [Fact]
    public void TwoRuns_SolveManifoldAndEffectiveVolume()
    {
        const double vm = 7.5, veff = 52.4, water = 20;
        var (f1, _) = VolumeCalib.FitSteps(Run(vm, veff, 80));
        var (f2, _) = VolumeCalib.FitSteps(Run(vm, veff - water, 80));
        var (m, e, why) = VolumeCalib.SolveTwoRuns(f1!.Ratio, f2!.Ratio, water);
        Assert.Null(why);
        Assert.Equal(vm, m!.Value, 6);
        Assert.Equal(veff, e!.Value, 6);

        // 已知总管体积：另一路空釜一遍
        var (f3, _) = VolumeCalib.FitSteps(Run(vm, 49.1, 80));
        var (e3, why3) = VolumeCalib.SolveOneRun(f3!.Ratio, m.Value);
        Assert.Null(why3);
        Assert.Equal(49.1, e3!.Value, 6);
    }

    [Fact]
    public void TwoRuns_RejectInconsistentInputs()
    {
        Assert.NotNull(VolumeCalib.SolveTwoRuns(6, 6.2, 20).Why);    // 加水后比值没变小
        Assert.NotNull(VolumeCalib.SolveTwoRuns(6, 4, 0).Why);       // 加水体积 0
        Assert.NotNull(VolumeCalib.SolveTwoRuns(6, 5.99, 20).Why);   // 结果离谱（总管 2000 mL）
        Assert.NotNull(VolumeCalib.SolveOneRun(6, 0).Why);           // 没有总管体积
    }
}
