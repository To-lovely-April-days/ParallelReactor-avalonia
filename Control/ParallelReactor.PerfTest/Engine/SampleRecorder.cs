using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.PerfTest.Measure;

namespace ParallelReactor.PerfTest.Engine;

/// <summary>一帧里一个通道的紧凑样本（长表一行）。</summary>
public readonly record struct Sample(
    DateTime Ts, long Seq, int Rv, string State, double? TempC, double TempSpC, double? HeatOutputPct,
    double? PressurePsi, int? PressureRaw, bool ValveOpen, bool StirOn, int RpmSp, double? BusVoltageV,
    bool EstopActive, string? ProgramState, int? StepIndex)
{
    /// <summary>按信号名取值。</summary>
    public double? Get(string signal) => signal switch
    {
        "tempC" => TempC,
        "pressurePsi" => PressurePsi,
        "pressureRaw" => PressureRaw,
        "heatOutputPct" => HeatOutputPct,
        "rpm" or "rpmSp" => StirOn ? RpmSp : 0,   // 搅拌转速是设定值（DM2C 无转速反馈），不是实测
        "busVoltageV" => BusVoltageV,
        "tempSpC" => TempSpC,
        _ => null,
    };
}

/// <summary>运行期间全部样本（内存，长表），供 WaitStable / Measure 查询；原始 CSV 由 ResultStore 同步写。</summary>
public sealed class SampleRecorder
{
    private readonly object _lock = new();
    private readonly List<Sample> _samples = new();
    private long _lastSeq = -1;

    public int FrameCount { get; private set; }
    public int Count { get { lock (_lock) return _samples.Count; } }

    /// <summary>加入一帧（seq 去重）；返回拆出的样本（空 = 重复帧）。</summary>
    public IReadOnlyList<Sample> Add(TelemetrySnapshot f)
    {
        var rows = new List<Sample>(f.Channels.Length);
        lock (_lock)
        {
            if (f.Seq != 0 && f.Seq <= _lastSeq) return rows;
            _lastSeq = Math.Max(_lastSeq, f.Seq);
            FrameCount++;
            foreach (var c in f.Channels)
            {
                var s = new Sample(f.Ts, f.Seq, c.Id, c.State, c.TempC, c.TempSpC, c.HeatOutputPct, c.PressurePsi, c.PressureRaw, c.ValveOpen,
                    f.Stir.On, f.Stir.RpmSp, f.Stir.BusVoltageV, f.Estop.Active, c.Program?.State, c.Program?.StepIndex);
                _samples.Add(s);
                rows.Add(s);
            }
        }
        return rows;
    }

    /// <summary>某通道在 [from, to] 内的信号序列（相对 from 的秒）。null 值跳过。</summary>
    public List<Pt> Series(int rv, string signal, DateTime from, DateTime to)
    {
        lock (_lock)
        {
            var list = new List<Pt>();
            foreach (var s in _samples)
            {
                if (s.Rv != rv || s.Ts < from || s.Ts > to) continue;
                var v = s.Get(signal);
                if (v.HasValue && !double.IsNaN(v.Value)) list.Add(new Pt((s.Ts - from).TotalSeconds, v.Value));
            }
            return list;
        }
    }

    /// <summary>最近 window 内的序列（相对窗口起点）。</summary>
    public List<Pt> Recent(int rv, string signal, DateTime now, TimeSpan window) => Series(rv, signal, now - window, now);

    /// <summary>某通道最新样本。</summary>
    public Sample? Latest(int rv)
    {
        lock (_lock)
        {
            for (int i = _samples.Count - 1; i >= 0; i--) if (_samples[i].Rv == rv) return _samples[i];
            return null;
        }
    }

    /// <summary>某通道在时间段内的 SP 均值（WaitStable reference=sp 用最新 SP）。</summary>
    public double? LatestSp(int rv) => Latest(rv)?.TempSpC;

    /// <summary>导出时间段内样本（报告 SVG 用）。</summary>
    public List<Sample> Range(DateTime from, DateTime to, int? rv = null)
    {
        lock (_lock) return _samples.Where(s => s.Ts >= from && s.Ts <= to && (rv == null || s.Rv == rv)).ToList();
    }
}
