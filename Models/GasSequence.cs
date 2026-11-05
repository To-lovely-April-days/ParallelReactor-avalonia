using System;
using System.Collections.Generic;

namespace ParallelReactor.Models;

/// <summary>
/// 12 路阀门的一帧「开/关」状态，下标与 IO16R 线圈一致：
/// 0-7 = RV1-8 进气阀 SV，8 = 惰性气体，9 = 气体 A，10 = 气体 B，11 = 排空 Vent。
/// 吹扫 / 泄漏测试只通过这里拼帧、再整帧下发，保证不会出现「气源与排空同时开」。
/// </summary>
public static class ValveFrame
{
    public const int Count = 12;
    public const int GasBase = 8;   // 惰性=8, A=9, B=10
    public const int Vent = 11;

    /// <summary>全关。</summary>
    public static bool[] AllClosed() => new bool[Count];

    /// <summary>只开一路气源（0=惰性 1=A 2=B），可同时开若干 RV 的 SV（rvIds 为 1..8）。</summary>
    public static bool[] Gas(int gas, params int[] rvIds)
    {
        var f = WithSv(rvIds);
        f[GasBase + Math.Clamp(gas, 0, 2)] = true;
        return f;
    }

    /// <summary>只开排空阀，可同时开若干 RV 的 SV。</summary>
    public static bool[] VentOnly(params int[] rvIds)
    {
        var f = WithSv(rvIds);
        f[Vent] = true;
        return f;
    }

    /// <summary>安全约束：气源最多开一路，且气源与排空不能同时开。</summary>
    public static bool IsSafe(bool[] f)
    {
        if (f.Length != Count) return false;
        int gas = 0;
        for (int i = GasBase; i < GasBase + 3; i++) if (f[i]) gas++;
        return gas <= 1 && !(gas == 1 && f[Vent]);
    }

    private static bool[] WithSv(int[] rvIds)
    {
        var f = AllClosed();
        foreach (var id in rvIds)
            if (id >= 1 && id <= 8) f[id - 1] = true;
        return f;
    }
}

/// <summary>吹扫序列的一个动作：整帧写入阀态后保持 Seconds 秒。</summary>
public sealed record PurgeAction(int Cycle, string Phase, bool[] Valves, double Seconds);

/// <summary>
/// 管路吹扫（只吹扫共用总管）：8 路 SV 全程关闭，釜内气体不受影响。
/// 每次循环：开气源充气 → 全关 → 开排空排气 → 全关。气源与排空之间总隔一个全关死区，
/// 避免切换瞬间两阀同开（气体直通排空、冲击减压阀）。以排气结束，总管最后是常压的吹扫气。
/// </summary>
public static class PurgePlan
{
    public const double DeadSec = 0.5;   // 阀态切换死区

    /// <summary>步骤里的气体名 → 气源下标（0=惰性 1=A 2=B）。</summary>
    public static int GasIndex(string label) => label switch { "气体 A" => 1, "气体 B" => 2, _ => 0 };

    public static List<PurgeAction> Build(int gas, int cycles, double fillSec, double ventSec)
    {
        // 先全关一个死区：开始前若有阀开着（如排空），不与第一帧充气在切换瞬间重叠
        var list = new List<PurgeAction> { new(1, "准备", ValveFrame.AllClosed(), DeadSec) };
        for (int c = 1; c <= cycles; c++)
        {
            list.Add(new PurgeAction(c, "充气", ValveFrame.Gas(gas), fillSec));
            list.Add(new PurgeAction(c, "切换", ValveFrame.AllClosed(), DeadSec));
            list.Add(new PurgeAction(c, "排气", ValveFrame.VentOnly(), ventSec));
            list.Add(new PurgeAction(c, "切换", ValveFrame.AllClosed(), DeadSec));
        }
        return list;
    }
}

/// <summary>泄漏率计算：温度补偿（等容理想气体，绝对压力 / 绝对温度恒定）+ 最小二乘斜率。</summary>
public static class LeakMath
{
    public const double AtmPsi = 14.696;

    /// <summary>把温度 tC 下的表压 psig 折算到参考温度 refC。</summary>
    public static double Compensate(double psig, double tC, double refC)
        => (psig + AtmPsi) * (refC + 273.15) / (tC + 273.15) - AtmPsi;

    /// <summary>压降速率 psi/hr（正值 = 压力在降）。样本为 (分钟, psi)，少于 2 个点返回 0。</summary>
    public static double DropRatePerHour(IReadOnlyList<(double Min, double Psi)> s)
    {
        int n = s.Count;
        if (n < 2) return 0;
        double mx = 0, my = 0;
        foreach (var (x, y) in s) { mx += x; my += y; }
        mx /= n; my /= n;
        double sxx = 0, sxy = 0;   // 去均值后再累加，避免大数相减丢精度
        foreach (var (x, y) in s) { sxx += (x - mx) * (x - mx); sxy += (x - mx) * (y - my); }
        if (sxx < 1e-12) return 0;
        return -sxy / sxx * 60.0;
    }
}

/// <summary>气路自动序列中的硬件 / 安全故障：序列中止并全关阀。</summary>
public sealed class GasFaultException : Exception
{
    public GasFaultException(string message) : base(message) { }
}
