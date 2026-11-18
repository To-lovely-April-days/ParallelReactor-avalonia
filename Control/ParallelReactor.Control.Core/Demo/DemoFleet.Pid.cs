using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Pid;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 演示数据的档位 PID 与自整定（设置 → 温控 / PID；样稿 PIDB / PIDS / pidSeries，2026-11-18 用户确认的自整定界面）：
/// <list type="bullet">
/// <item>档位 = 样稿 PIDB：低温档 0–100、中温档 100–200、高温档 200–400 °C。</item>
/// <item>各路各档的 P / I / D = 样稿 PIDS 的公式（k = 7n + 3·档）；高温档都没整定过，低温档 RV3 / RV6 / … 没整定过（出厂值 P 12 · I 200 · D 30，来历为空）；
///   中温档整定于 11-10、低温档 11-03（10:00），整定温度 = 档的默认温度。</item>
/// <item>自整定（<see cref="StartAutotune"/>）：守卫同 HMI §7.6（远程控制、通道空闲、不在整定、温度落在档里）；之后每秒按 <see cref="PidDemoCurve"/>
///   给 PV 和加热输出（8 °C/min 升温，再在整定温度附近振荡，周期约 7 min，输出 100 / 0 方波）；3 次振荡后结束：新 PID = 样稿的 P × 0.93、I × 0.835、D × 0.85，
///   存入该档并记来历（整定温度、完成时刻），通道停止加热。<see cref="CancelAutotune"/>：停止加热，PID 不改。</item>
/// <item>截图 / 走查：<see cref="SeedAutotune"/> 把整定往前挪（曲线补进历史），<see cref="FinishAutotune"/> 立即结束（样稿 tune=run / tune=done）。</item>
/// </list>
/// 读取 = <see cref="PidOf"/>（同 GET /pid 的形状）。
/// </summary>
public sealed partial class DemoFleet
{
    /// <summary>样稿 PIDB：下标、名字、温区、默认整定温度。</summary>
    public static readonly IReadOnlyList<PidBandSnap> DemoPidBands = new[]
    {
        new PidBandSnap(0, "低温档", 0, 100), new PidBandSnap(1, "中温档", 100, 200), new PidBandSnap(2, "高温档", 200, 400),
    };

    /// <summary>出厂值（样稿 pidBody 里没整定过时的 { p: 12, i: 200, d: 30 }）。</summary>
    public static readonly PidValues DemoFactoryPid = new(12, 200, 30);

    private sealed class PidCell
    {
        public double P, I, D;
        public double? TunedSpC;
        public DateTime? TunedAt, ManualAt;
    }

    private sealed class PidRun
    {
        public int Band;
        public double Sp, T0;
        public DateTime StartAt;
    }

    private sealed class PidBook
    {
        public readonly Dictionary<(int N, int Band), PidCell> Cells = new();
        public readonly Dictionary<int, PidRun> Runs = new();
        public long Seq = 1;
    }

    private readonly ConditionalWeakTable<DemoDevice, PidBook> _pid = new();

    private PidBook Book(DemoDevice d) => _pid.GetValue(d, Seed);

    private static PidBook Seed(DemoDevice d)
    {
        var b = new PidBook();
        foreach (var ch in d.Channels)
        {
            if (!ch.Installed) continue;
            foreach (var band in DemoPidBands)
            {
                int n = ch.N, k = n * 7 + band.Index * 3;
                bool tuned = band.Index != 2 && !(band.Index == 0 && n % 3 == 0);
                if (!tuned)
                {
                    b.Cells[(n, band.Index)] = new PidCell { P = DemoFactoryPid.P, I = DemoFactoryPid.I, D = DemoFactoryPid.D };
                    continue;
                }
                var at = new DateTime(2026, 11, band.Index == 1 ? 10 : 3, 10, 0, 0, DateTimeKind.Local).ToUniversalTime();
                b.Cells[(n, band.Index)] = new PidCell
                {
                    P = Math.Round(10 + band.Index * 3 + k % 5 * 0.7, 1),
                    I = 150 + band.Index * 30 + k % 40,
                    D = 25 + band.Index * 6 + k % 9,
                    TunedSpC = band.Index switch { 0 => 80, 1 => 150, _ => 250 },
                    TunedAt = at,
                };
            }
        }
        return b;
    }

    /// <summary>这台设备的档位 PID（同 GET /pid：档位定义 + 每路各档 P / I / D 与来历 + 自整定状态）。</summary>
    public PidDto PidOf(DemoDevice d)
    {
        var b = Book(d);
        var chans = new List<PidChannelSnap>();
        foreach (var ch in d.Channels)
        {
            if (!ch.Installed) continue;
            b.Runs.TryGetValue(ch.N, out var run);
            var bands = DemoPidBands.Select(x =>
            {
                var c = b.Cells.TryGetValue((ch.N, x.Index), out var cell) ? cell : new PidCell { P = DemoFactoryPid.P, I = DemoFactoryPid.I, D = DemoFactoryPid.D };
                return new PidBandValueSnap(x.Index, c.P, c.I, c.D, c.TunedSpC, c.TunedAt, c.ManualAt);
            }).ToArray();
            chans.Add(new PidChannelSnap(ch.N, run != null, run?.Band, run?.Sp, bands));
        }
        return new PidDto(b.Seq, Now.ToUniversalTime(), DemoPidBands.ToArray(), chans.ToArray());
    }

    /// <summary>第 n 路正在自整定。</summary>
    public bool Autotuning(DemoDevice d, int n) => Book(d).Runs.ContainsKey(n);

    /// <summary>第 n 路这次整定开始的时刻（本地，演示时钟）；没在整定为 null。</summary>
    public DateTime? AutotuneStartedAt(DemoDevice d, int n) => Book(d).Runs.TryGetValue(n, out var r) ? r.StartAt : null;

    /// <summary>
    /// 开始自整定（同 HMI POST /channels/{n}/autotune {tempC, band}）：先查守卫，不行返回原因（不改任何东西）；可以返回 ""。
    /// 守卫：设备在线、没急停、本机远程控制中、通道已安装且启用、没在运行 / 报警、没在整定、档位存在且 tempC 落在档里（40 ≤ tempC ≤ TMaxSp）。
    /// </summary>
    public string StartAutotune(DemoDevice d, int n, double tempC, int band)
    {
        var why = AutotuneWhy(d, n, tempC, band);
        if (why.Length > 0) return why;
        var ch = d.Channel(n)!;
        Book(d).Runs[n] = new PidRun { Band = band, Sp = tempC, T0 = ch.TempC, StartAt = Now };
        ch.Heating = true;
        ch.SpC = tempC;
        Bump(d);
        Log(d, EventLevel.Op, n, "RV" + n + " 开始自整定 · " + DemoPidBands[band].Name + " · " + PidTexts.Num(tempC) + " °C（" + Client + "）");
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
        return "";
    }

    private string AutotuneWhy(DemoDevice d, int n, double tempC, int band)
    {
        var ch = d.Channel(n);
        if (!d.Online) return d.Alias + "未连接";
        if (d.Estop.Active) return d.Alias + "急停中";
        if (d.Lease.State != LeaseStatus.Remote) return "先申请控制权";
        if (ch is null || !ch.Installed) return "RV" + n + " 未安装";
        if (ch.State == StateKey.Idle) return "RV" + n + " 已停用";
        if (ch.IsRunning) return "RV" + n + " 正在运行 · 不能整定";
        if (Book(d).Runs.ContainsKey(n)) return "RV" + n + " 已在整定";
        if (band < 0 || band >= DemoPidBands.Count) return "没有这个档位";
        var b = DemoPidBands[band];
        return PidTune.TempReason(new PidBand(b.Index, b.Name, b.LoC, b.HiC, band == DemoPidBands.Count - 1), d.Model.Limits.TempMaxC, tempC) ?? "";
    }

    /// <summary>取消自整定（停止类，不要控制权）：停止加热，PID 不改。没在整定返回原因。</summary>
    public string CancelAutotune(DemoDevice d, int n)
    {
        if (!d.Online) return d.Alias + "未连接";
        if (!Book(d).Runs.Remove(n)) return "RV" + n + " 没有在整定";
        StopHeat(d.Channel(n));
        Bump(d);
        Log(d, EventLevel.Warn, n, "RV" + n + " 已取消自整定 · PID 未改");
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
        return "";
    }

    /// <summary>手改一档 PID（同 PUT /channels/{n}/pid/{band}）：要控制权；P &gt; 0、I / D ≥ 0。数值确有变化时记手改时刻（整定来历保留）。</summary>
    public string SetPid(DemoDevice d, int n, int band, double p, double i, double dd)
    {
        if (!d.Online) return d.Alias + "未连接";
        if (d.Lease.State != LeaseStatus.Remote) return "先申请控制权";
        if (band < 0 || band >= DemoPidBands.Count) return "没有这个档位";
        if (!(p > 0) || !(i >= 0) || !(dd >= 0)) return "P 须大于 0，I、D 不能小于 0";
        var b = Book(d);
        if (!b.Cells.TryGetValue((n, band), out var c)) b.Cells[(n, band)] = c = new PidCell();
        if (c.P == p && c.I == i && c.D == dd) return "";
        c.P = p; c.I = i; c.D = dd;
        c.ManualAt = Now.ToUniversalTime();
        Bump(d);
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
        return "";
    }

    /// <summary>
    /// 截图 / 走查（样稿 tune=run|done）：第 n 路的自整定当作 <paramref name="minutesAgo"/> 分钟前开始（起始温度同样稿 25 + 0.6n），
    /// 这段的历史换成整定曲线（PV、加热输出、设定值），当前读数 = 曲线此刻的值。不查守卫（运行中的通道照样摆出来）。
    /// </summary>
    public void SeedAutotune(DemoDevice d, int n, double tempC, int band, double minutesAgo)
    {
        var ch = d.Channel(n);
        if (ch is null || !ch.Installed) return;
        var start = AddMs(Now, -minutesAgo * MinMs);
        var run = new PidRun { Band = band, Sp = tempC, T0 = 25 + n * 0.6, StartAt = start };
        Book(d).Runs[n] = run;
        var h = ch.HistoryList;
        for (int i = 0; i < h.Count; i++)
        {
            if (h[i].T < start) continue;
            var (t, o) = PidDemoCurve.At(Ms(h[i].T, start) / MinMs, tempC, run.T0, n);
            h[i] = h[i] with { TempC = DemoHistory.Fixed(t, 2), HeaterPct = o, SpC = tempC };
        }
        var (tn, on) = PidDemoCurve.At(minutesAgo, tempC, run.T0, n);
        ch.TempC = DemoHistory.Fixed(tn, 2);
        ch.HeaterPct = on;
        ch.Heating = true;
        ch.SpC = tempC;
        Bump(d);
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
    }

    /// <summary>截图 / 走查：第 n 路的自整定立即结束（同 3 次振荡后的结束：存入新 PID、停止加热）。没在整定时什么都不做。</summary>
    public void FinishAutotune(DemoDevice d, int n)
    {
        if (!Book(d).Runs.ContainsKey(n)) return;
        CompleteTune(d, n);
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
    }

    /// <summary>每秒推进时（DemoFleet.Tick）：在整定的通道按整定曲线走，到 3 次振荡后结束。返回 true = 这一路已处理（不再跑别的物理）。</summary>
    private bool TickAutotune(DemoDevice d, DemoChannel ch)
    {
        if (!_pid.TryGetValue(d, out var b) || !b.Runs.TryGetValue(ch.N, out var run)) return false;
        if (d.Estop.Active)
        {
            // 急停打断整定（同 HMI：不记来历、PID 不改）
            b.Runs.Remove(ch.N);
            StopHeat(ch);
            Bump(d);
            Log(d, EventLevel.Warn, ch.N, "RV" + ch.N + " 自整定被急停打断 · PID 未改");
            return false;
        }
        double m = Ms(Now, run.StartAt) / MinMs;
        var (t, o) = PidDemoCurve.At(m, run.Sp, run.T0, ch.N);
        ch.TempC = t;
        ch.HeaterPct = o;
        ch.Heating = true;
        ch.SpC = run.Sp;
        if (m >= PidDemoCurve.DoneAt(run.Sp, run.T0, ch.N)) CompleteTune(d, ch.N);
        return true;
    }

    /// <summary>整定结束：新 PID（样稿：P × 0.93 一位小数、I × 0.835、D × 0.85 取整）存入该档并记来历，停止加热。</summary>
    private void CompleteTune(DemoDevice d, int n)
    {
        var b = Book(d);
        var run = b.Runs[n];
        b.Runs.Remove(n);
        if (!b.Cells.TryGetValue((n, run.Band), out var c)) b.Cells[(n, run.Band)] = c = new PidCell { P = DemoFactoryPid.P, I = DemoFactoryPid.I, D = DemoFactoryPid.D };
        c.P = Math.Round(c.P * 0.93, 1);
        c.I = Math.Round(c.I * 0.835);
        c.D = Math.Round(c.D * 0.85);
        c.TunedSpC = run.Sp;
        c.TunedAt = Now.ToUniversalTime();
        c.ManualAt = null;
        StopHeat(d.Channel(n));
        Bump(d);
        Log(d, EventLevel.Info, n, "RV" + n + " 自整定完成 · 新 PID 已存入" + DemoPidBands[run.Band].Name);
    }

    private static void StopHeat(DemoChannel? ch)
    {
        if (ch is null) return;
        ch.Heating = false;
        ch.HeaterPct = 0;
        ch.SpC = null;
    }

    private void Bump(DemoDevice d) => Book(d).Seq++;

}
