using System;
using System.Collections.Generic;

namespace ParallelReactor.Models;

/// <summary>
/// 气体消耗（吸气量）计量：按釜内压降折算 mmol（理想气体，n = P绝 · V气 / (R · T)）。纯逻辑：测试工程直接链接本文件。
/// <list type="bullet">
/// <item>V气 = 釜容积 − 溶液体积 + 管路死体积（<see cref="GasVolumeMl"/>）；P 用表压 + 1 atm 换成绝压；T 用釜温（气相按与液相同温近似）。</item>
/// <item>每个采样周期累加「上一次的气相 mmol − 这一次的气相 mmol」。相邻差值累加后只剩首尾两项，读数噪声不会越积越多。</item>
/// <item>进气阀 SV 打开（升压、稳压补气、手动开阀、排空）会改变釜内气量，但不是反应消耗：SV 开着时不计；
///   SV 打开过（<c>svGen</c> 变了）就断开基准，等 SV 关闭满 <see cref="SettleSec"/> 秒、压力稳定后重新取基准接着累加。
///   因此稳压补进去的气不会算成「产生」，补压前后的消耗照样累计。每次补压最多漏计约 SettleSec + 一个采样周期的消耗。</item>
/// <item>压力或温度读数无效（模块离线）的周期跳过，但不断开基准：恢复后的压差会把离线期间的消耗一并算上（期间 SV 没开过时）。</item>
/// <item>溶液体积或管路死体积在计量中被修改：气相体积变了，断开基准重新取（不把体积变化算成消耗）。</item>
/// </list>
/// 局限（写进界面说明）：溶剂蒸气压随温度变化、气相实际温度与釜温不同、温度没稳定时开始计量，都会带来偏差；
/// 所以计量从升压完成后开始，建议先升温到位再升压。
/// </summary>
public sealed class GasUptakeMeter
{
    /// <summary>表压 → 绝压：1 标准大气压（psi）。</summary>
    public const double AtmPsi = 14.6959;
    /// <summary>1 psi = 6894.757 Pa。</summary>
    public const double PaPerPsi = 6894.757293;
    /// <summary>摩尔气体常数 J/(mol·K)。</summary>
    public const double R = 8.314462618;
    /// <summary>SV 关闭后静置多久才重新取基准（秒）：等补气后的压力波动过去。</summary>
    public const double SettleSec = 2.0;
    /// <summary>速率按最近这么多秒计算。</summary>
    public const double RateWindowSec = 60;
    /// <summary>窗口里至少有这么多秒的数据才给出速率。</summary>
    public const double RateMinSpanSec = 10;
    /// <summary>气相体积下限（mL）：低于它视为体积参数不合理，不计量。</summary>
    public const double MinGasVolumeMl = 0.5;

    /// <summary>正在计量（升压完成后开始，程序结束步骤 / 收尾时停止）。</summary>
    public bool Counting { get; private set; }
    /// <summary>本次读数是实测值（开始计量后为 true，<see cref="Reset"/> 后为 false）。停止计量后保留最后的值。</summary>
    public bool Valid { get; private set; }
    /// <summary>累计吸气量 mmol（正 = 气体被消耗）。</summary>
    public double Mmol { get; private set; }
    /// <summary>最近 <see cref="RateWindowSec"/> 秒的平均吸气速率 mmol/min；数据不够或未在计量时为 null。</summary>
    public double? RateMmolPerMin { get; private set; }
    /// <summary>计量期间 SV 打开过的次数（补压 / 手动开阀），用于事件说明。</summary>
    public int Refills { get; private set; }
    /// <summary>当前使用的气相体积 mL（最近一次有效采样）。</summary>
    public double? GasVolume { get; private set; }

    private double? _prevN;          // 上一次气相 mmol（基准）；null = 需要重新取基准
    private long _gen;               // 开始 / 上次采样时的 SV 打开计数
    private double? _closedSince;    // SV 关闭（且计数未变）从何时开始；null = SV 开着
    private readonly Queue<(double T, double M)> _hist = new();

    /// <summary>气相体积 mL = 釜容积 − 溶液体积 + 管路死体积。</summary>
    public static double GasVolumeMl(double vesselMl, double solutionMl, double lineMl) => vesselMl - Math.Max(0, solutionMl) + Math.Max(0, lineMl);

    /// <summary>气相中的气体量 mmol（表压 psi、温度 ℃、体积 mL）。</summary>
    public static double GasMmol(double psiGauge, double tempC, double volMl)
        => (psiGauge + AtmPsi) * PaPerPsi * volMl * 1e-6 / (R * (tempC + 273.15)) * 1000.0;

    /// <summary>压力分辨率 <paramref name="dPsi"/> 对应的 mmol（25 ℃），界面说明用。</summary>
    public static double ResolutionMmol(double dPsi, double volMl) => dPsi * PaPerPsi * volMl * 1e-6 / (R * 298.15) * 1000.0;

    /// <summary>开始计量：清零并以当前时刻为起点（SV 需关闭满 <see cref="SettleSec"/> 秒才取基准）。已在计量时不做任何事（连续升压只是补气）。</summary>
    public bool Start(double tSec, long svGen)
    {
        if (Counting) return false;
        Counting = true;
        Valid = true;
        Mmol = 0;
        RateMmolPerMin = null;
        Refills = 0;
        GasVolume = null;
        _prevN = null;
        _gen = svGen;
        _closedSince = tSec;
        _hist.Clear();
        return true;
    }

    /// <summary>停止计量，保留最后的读数（程序结束 / 中止）。</summary>
    public void Stop()
    {
        Counting = false;
        RateMmolPerMin = null;
        _prevN = null;
        _hist.Clear();
    }

    /// <summary>停止并清空（新程序开始）：读数回到「—」。</summary>
    public void Reset()
    {
        Stop();
        Valid = false;
        Mmol = 0;
        Refills = 0;
        GasVolume = null;
    }

    /// <summary>
    /// 一个采样周期。<paramref name="readingsOk"/> = 温度、压力读数都有效；<paramref name="svOpen"/> = 该通道 SV 当前是开的；
    /// <paramref name="svGen"/> = 该通道 SV 被打开的累计次数（任何打开 SV 的写入都加一，见 ValveService.GetSvOpenGen）。
    /// 返回本周期是否更新了累计值。
    /// </summary>
    public bool Sample(double tSec, bool readingsOk, double psiGauge, double tempC, double gasVolMl, bool svOpen, long svGen)
    {
        if (!Counting) return false;
        if (svGen != _gen)
        {
            Refills++;
            _gen = svGen;
            _prevN = null;
            _closedSince = svOpen ? null : tSec;
            return false;
        }
        if (svOpen) { _prevN = null; _closedSince = null; return false; }
        if (_closedSince is null) { _closedSince = tSec; return false; }
        if (tSec - _closedSince.Value < SettleSec) return false;
        if (!readingsOk || !double.IsFinite(psiGauge) || !double.IsFinite(tempC) || tempC <= -273) return false;
        if (!double.IsFinite(gasVolMl) || gasVolMl < MinGasVolumeMl) { _prevN = null; return false; }
        if (GasVolume is double v && Math.Abs(v - gasVolMl) > 1e-9) _prevN = null;   // 体积参数改了：重新取基准
        GasVolume = gasVolMl;

        double n = GasMmol(psiGauge, tempC, gasVolMl);
        bool moved = false;
        if (_prevN is double prev) { Mmol += prev - n; moved = true; }
        _prevN = n;

        _hist.Enqueue((tSec, Mmol));
        while (_hist.Count > 1 && tSec - _hist.Peek().T > RateWindowSec) _hist.Dequeue();
        var first = _hist.Peek();
        double span = tSec - first.T;
        RateMmolPerMin = span >= RateMinSpanSec ? (Mmol - first.M) / span * 60.0 : null;
        return moved;
    }
}
