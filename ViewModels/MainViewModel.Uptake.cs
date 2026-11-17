using System.Diagnostics;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 气体消耗（吸气量）计量（见 <see cref="GasUptakeMeter"/>）：每个实时 Tick 用各通道的压力、温度、气相体积和 SV 打开计数更新一次。
/// 程序引擎在升压完成时开始计量、在结束步骤 / 收尾时停止；触屏抽屉「清零」从当前时刻重新开始计量（手动实验用）。
/// 只读写 <see cref="Reactor.Gas"/> / <see cref="Reactor.GasValid"/> / <see cref="Reactor.GasRate"/>，不写设备。
/// </summary>
public partial class MainViewModel
{
    /// <summary>计量用的单调时钟（秒）：不受系统改时间影响。</summary>
    private readonly Stopwatch _uptakeClock = Stopwatch.StartNew();

    private double UptakeNow => _uptakeClock.Elapsed.TotalSeconds;

    /// <summary>
    /// 该通道的气相体积 mL：做过容积标定 → 有效容积 − 溶液体积；没标定 → 釜容积 − 溶液体积 + 管路死体积（设置页「气体消耗计算」）。
    /// </summary>
    public double GasVolumeMl(Reactor c) => EffectiveVolumeOf(c.Id) is double eff
        ? eff - System.Math.Max(0, c.Vol)
        : GasUptakeMeter.GasVolumeMl(Settings.VesselVolume, c.Vol, Settings.GasLineVolume);

    /// <summary>该通道标定过的有效容积 mL（未标定 null）。</summary>
    public double? EffectiveVolumeOf(int id) => Settings.EffectiveVolume is { } a && id >= 1 && id <= a.Length ? a[id - 1] : null;

    private string VolumeBasis(Reactor c) => EffectiveVolumeOf(c.Id) is double eff
        ? $"有效容积 {eff:0.#}（已标定）− 溶液 {c.Vol}"
        : $"釜 {Settings.VesselVolume:0.#} − 溶液 {c.Vol} + 管路 {Settings.GasLineVolume:0.#}（未标定）";

    /// <summary>Tick：更新一个通道的吸气量（只在计量中时累加；停止后保留最后读数）。</summary>
    private void UpdateUptake(Reactor c, bool realT, bool realP)
    {
        var m = c.Uptake;
        if (!m.Counting) return;
        m.Sample(UptakeNow, realT && realP, c.P, c.T, GasVolumeMl(c), c.Valve, _valves.GetSvOpenGen(c.Id));
        PublishUptake(c);
    }

    private static void PublishUptake(Reactor c)
    {
        var m = c.Uptake;
        c.Gas = m.Mmol;
        c.GasValid = m.Valid;
        c.GasRate = m.RateMmolPerMin;
    }

    /// <summary>
    /// 开始计量（升压完成 / 抽屉清零）。已在计量时返回 false（连续升压只是补气，累计值接着算）。
    /// 气相体积不合理（溶液体积 ≥ 釜容积 + 管路）时不开始，返回 false 并记一条 warn。
    /// </summary>
    public bool StartUptake(Reactor c, string why)
    {
        if (c.Uptake.Counting) return false;
        double v = GasVolumeMl(c);
        if (v < GasUptakeMeter.MinGasVolumeMl)
        {
            Alarm.Log("warn", "系统", $"RV{c.Id} 吸气量未计量",
                $"气相体积 {v:0.#} mL 不合理（{VolumeBasis(c)} mL）· 请检查容积标定、设置与溶液体积");
            return false;
        }
        c.Uptake.Start(UptakeNow, _valves.GetSvOpenGen(c.Id));
        PublishUptake(c);
        Alarm.Log("info", "系统", $"RV{c.Id} 开始计量吸气量",
            $"{why} · 气相体积 {v:0.#} mL（{VolumeBasis(c)}）"
            + (c.Vol <= 0 ? " · 溶液体积为 0，按空釜计算" : ""));
        return true;
    }

    /// <summary>停止计量并保留读数（结束步骤开始 / 程序收尾）。没在计量时什么也不做。</summary>
    public void StopUptake(Reactor c, string why)
    {
        if (!c.Uptake.Counting) return;
        c.Uptake.Stop();
        PublishUptake(c);
        Alarm.Log("info", "系统", $"RV{c.Id} 吸气量计量结束",
            $"{why} · 累计 {c.Uptake.Mmol:0.00} mmol" + (c.Uptake.Refills > 0 ? $" · 期间补气 / 开阀 {c.Uptake.Refills} 次" : ""));
    }

    /// <summary>清空读数（新程序开始）：显示回到「—」。</summary>
    public void ResetUptake(Reactor c)
    {
        c.Uptake.Reset();
        PublishUptake(c);
    }
}
