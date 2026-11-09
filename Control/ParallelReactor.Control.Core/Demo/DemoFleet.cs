using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 演示数据：同时连接的几台设备（样稿 js/sim.js 的 C# 移植，PORT-PLAN §5 M2 Core）。Fleet → Devices → Channels，每台各自的型号、连接、
/// 控制权、急停、会话、报警、事件、程序、60 min 历史；状态与样稿逐项相同（测试用样稿 JS 在 JavaScriptCore 里算出的摘要逐行核对）。
/// <list type="bullet">
/// <item>构造：<c>new DemoFleet(DemoParams.Parse("devs=4&amp;freeze=1&amp;lease=2:requested"))</c>；参数语法同样稿 README（<see cref="DemoParams"/>）。
///   之后可以再 <see cref="Apply"/> 新参数：同样稿 applyParams 按差异套用（换 devs / stress 才重建全部数据）。</item>
/// <item>时钟从 2026-11-08 10:12:00（<see cref="T0"/>）开始；freeze=1 时 <see cref="Tick"/> 不推进（截图）。不冻结时由 App 的 1 s 定时器调 Tick。</item>
/// <item>读：<see cref="Devices"/> / <see cref="Device"/> / DemoDevice 的帮助方法（读数、状态字、进度、守卫）。</item>
/// <item>改：<see cref="Apply"/>、<see cref="Tick"/>、<see cref="Act"/> / <see cref="ActMany"/>、<see cref="AddDevice"/>、<see cref="EditDevice"/>；
///   每次改完触发 <see cref="Changed"/>，要给用户的提示走 <see cref="Toast"/>。</item>
/// </list>
/// 不是线程安全的：全部在同一个线程（UI 线程）上调用。随机数全部用固定种子（样稿的 Math.random 换成种子为 <see cref="NoiseSeed"/> 的 mulberry32），
/// 同样的参数与操作序列给出同样的数据。
/// </summary>
public sealed partial class DemoFleet
{
    /// <summary>演示时钟的起点：2026-11-08 周日 10:12:00（本地时间）。</summary>
    public static readonly DateTime T0 = new(2026, 11, 8, 10, 12, 0, DateTimeKind.Local);

    /// <summary>本机客户端名（样稿 CLIENT）。</summary>
    public const string Client = "PC-测试工位1";

    public const int HistoryMin = DemoHistory.HistoryMin, SampleS = DemoHistory.SampleS, KeepMin = 240;
    public const int LeaseWaitS = 60, LeaseGraceS = 30, RetryS = 12, ConnectS = 2, AutoConfirmS = 5;

    /// <summary>样稿每秒推进里用 Math.random 的地方（反应中的耗气增量）在这里用这个种子的 mulberry32。</summary>
    public const int NoiseSeed = 20261109;

    private const int LiveSeed = 20261108;
    private const double MinMs = 60000;

    private readonly List<DemoDevice> _devices = new();
    private int _alarmSeq = 1, _eventSeq = 1;
    private DemoRng _liveRand = new(LiveSeed), _noise = new(NoiseSeed);
    private Applied? _applied;

    /// <summary>用参数建一组演示设备（null = 默认 3 台、不冻结）。<paramref name="models"/> = 型号表（默认内置表）。</summary>
    public DemoFleet(DemoParams? p = null, ModelProfiles? models = null)
    {
        Models = models ?? ModelProfiles.Embedded;
        Devices = _devices.AsReadOnly();
        Apply(p ?? DemoParams.Default, raise: false);
    }

    /// <summary>按查询串建（'devs=4&amp;freeze=1'）。</summary>
    public static DemoFleet FromQuery(string? query, ModelProfiles? models = null) => new(DemoParams.Parse(query), models);

    public ModelProfiles Models { get; }

    /// <summary>全部设备（位置顺序；增删设备 / 换 devs 时内容变化，实例不变）。</summary>
    public IReadOnlyList<DemoDevice> Devices { get; }

    /// <summary>演示时钟。</summary>
    public DateTime Now { get; private set; } = T0;

    /// <summary>冻结（freeze=1）：Tick 不推进。</summary>
    public bool Frozen { get; private set; }

    /// <summary>推进过的秒数。</summary>
    public int TickCount { get; private set; }

    /// <summary>压力测试数据的级别：0 / 1 / 2。</summary>
    public int Stress { get; private set; }

    /// <summary>最后一次 <see cref="Apply"/> 的参数。</summary>
    public DemoParams Params { get; private set; } = DemoParams.Default;

    /// <summary>申请控制权后 5 s 自动当作触屏已确认（样稿演示行为）。关掉 = 一直等到超时。</summary>
    public bool AutoConfirmLease { get; set; } = true;

    /// <summary>
    /// 「全部停止」连搅拌一起停（默认取 <see cref="ControlOptions.StopAllStopsStirring"/>，全软件只有那一处设置；这里只给测试换值）。
    /// false = 样稿：搅拌保持当前状态；true = 共用搅拌和各釜搅拌都停，结果文字见 <see cref="StopAllText"/>。
    /// </summary>
    public bool StopAllStopsStirring { get; set; } = ControlOptions.StopAllStopsStirring;

    /// <summary>数据变了（每秒推进、操作、设备增删、参数）。在调用 Tick / Act / Apply 的线程上同步触发。</summary>
    public event EventHandler<DemoChangedEventArgs>? Changed;

    /// <summary>要给用户的提示（控制权申请超时、宽限期结束、操作结果……）。</summary>
    public event EventHandler<DemoToastEventArgs>? Toast;

    /// <summary>第 k 台；没有返回 null。</summary>
    public DemoDevice? Device(int k)
    {
        foreach (var d in _devices) if (d.K == k) return d;
        return null;
    }

    /// <summary>第 k 台的第 n 路；没有返回 null。</summary>
    public DemoChannel? Channel(int k, int n) => Device(k)?.Channel(n);

    /// <summary>设备还在这组里。</summary>
    public bool Contains(DemoDevice d) => _devices.Contains(d);

    /// <summary>全部设备汇总（样稿 totals）。</summary>
    public DemoTotals Totals() => new(
        _devices.Count,
        _devices.Count(d => d.Online),
        _devices.Count(d => d.Online && d.Lease.State == LeaseStatus.Remote),
        _devices.Count(d => d.Lease.State == LeaseStatus.Lost),
        _devices.Sum(d => d.AlarmList.Count),
        _devices.Where(d => d.Session is { Active: true }).Select(d => (d, d.Session!.Id)).ToArray());

    /// <summary>某型号的程序库（样稿 programLibrary）。</summary>
    public static IReadOnlyList<ProgramDef> ProgramLibrary(ModelProfile model) => DemoScenario.ProgramLibrary(model);

    // ———————————————————— 参数（样稿 init / applyParams） ————————————————————

    private sealed class Applied
    {
        public string Devs = "";
        public int Stress;
        public string? Lease, Alarm;
        public IReadOnlyList<string>? Offline;
        public bool OfflineGiven;
        public IReadOnlyList<string> Estop = Array.Empty<string>();
        public IReadOnlyList<KeyValuePair<string, string>> LeaseMap = Array.Empty<KeyValuePair<string, string>>(), AlarmMap = Array.Empty<KeyValuePair<string, string>>();
    }

    /// <summary>
    /// 套用参数（样稿 applyParams）：devs 或 stress 变了 → 重建全部数据（再套覆盖参数）；否则只按差异改控制权、断线、报警、急停（顺序同样稿）。
    /// </summary>
    public void Apply(DemoParams p) => Apply(p, raise: true);

    /// <summary>下一次 <see cref="Apply"/> 时重建全部数据（样稿演示条「重置」）。</summary>
    public void Reset()
    {
        _applied = null;
        Apply(Params);
    }

    private void Apply(DemoParams p, bool raise)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));
        bool rebuilt = false;
        if (_applied is null || (p.Devs ?? "") != _applied.Devs || p.Stress != _applied.Stress)
        {
            Init(p.Devs ?? "", p.Freeze, p.Stress);
            rebuilt = true;
        }
        var ap = _applied!;
        Frozen = p.Freeze;
        Params = p;

        // lease
        string lease = MapText(p.Lease);
        if (lease != (ap.Lease ?? ""))
        {
            var a = ap.LeaseMap;
            foreach (var d in _devices)
            {
                string k = Key(d.K);
                string? av = Lookup(a, k), bv = Lookup(p.Lease, k);
                if (av != bv) SetLease(d, string.IsNullOrEmpty(bv) ? DeviceTexts.LeaseCode(d.DefaultLease) : bv);
            }
            ap.Lease = lease;
            ap.LeaseMap = p.Lease;
        }

        // offline（参数缺省 = 默认：4 号位断线）
        if (!OfflineSame(p.Offline, ap))
        {
            foreach (var d in _devices)
            {
                bool a = ap.OfflineGiven && ap.Offline != null ? ap.Offline.Contains(Key(d.K)) : d.DefaultOffline;
                bool b = p.Offline != null ? p.Offline.Contains(Key(d.K)) : d.DefaultOffline;
                if (a != b) SetOffline(d, b);
            }
            ap.Offline = p.Offline;
            ap.OfflineGiven = p.Offline != null;
        }

        // alarm（1 = 加一条演示报警，0 = 清除全部报警，其它值 = 场景默认）
        string alarm = MapText(p.Alarm);
        if (alarm != (ap.Alarm ?? ""))
        {
            var a = ap.AlarmMap;
            foreach (var d in _devices)
            {
                string k = Key(d.K);
                string? av = Lookup(a, k), bv = Lookup(p.Alarm, k);
                if (av == bv) continue;
                if (bv == "1") RaiseDemoAlarm(d);
                else if (bv == "0") ClearAlarms(d);
                else RestoreAlarms(d);
            }
            ap.Alarm = alarm;
            ap.AlarmMap = p.Alarm;
        }

        // estop
        if (!ap.Estop.SequenceEqual(p.Estop))
        {
            foreach (var d in _devices)
            {
                string k = Key(d.K);
                bool a = ap.Estop.Contains(k), b = p.Estop.Contains(k);
                if (a == b) continue;
                if (b) DoEstop(d, "触屏 / 急停按钮");
                else ResetEstop(d);
            }
            ap.Estop = p.Estop;
        }

        if (raise) Changed?.Invoke(this, new DemoChangedEventArgs(rebuilt ? DemoChangeKind.Devices : DemoChangeKind.Params));
    }

    private static bool OfflineSame(IReadOnlyList<string>? b, Applied ap)
    {
        if (!ap.OfflineGiven) return b is null;
        if (b is null) return false;
        return ap.Offline!.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>样稿 init：重建全部设备（种子、序号、时钟复位）。</summary>
    private void Init(string devs, bool frozen, int stress)
    {
        _alarmSeq = 1;
        _eventSeq = 1;
        _liveRand = new DemoRng(LiveSeed);
        _noise = new DemoRng(NoiseSeed);
        Now = T0;
        Frozen = frozen;
        TickCount = 0;
        Stress = stress is 1 or 2 ? stress : 0;
        _devices.Clear();
        var keys = new DemoParams { Devs = devs.Length == 0 ? null : devs }.DeviceModels(Models);
        for (int i = 0; i < keys.Count; i++) _devices.Add(MakeDevice(i + 1, keys[i], 11, null));
        if (Stress != 0) foreach (var d in _devices) StressDevice(d, Stress);
        _applied = new Applied { Devs = devs, Stress = Stress };
    }

    // ———————————————————— 设备构建（样稿 makeDevice / makeChannel / makeProgram / seedEvents） ————————————————————

    private sealed record Slot(string Host, string Serial, LeaseStatus Lease, bool Session = false, bool Offline = false);

    /// <summary>每个位置（k）的默认身份与状态（样稿 SLOTS，brief §8）。</summary>
    private static readonly Dictionary<int, Slot> Slots = new()
    {
        [1] = new Slot("192.168.1.20", "0001", LeaseStatus.Remote, Session: true),
        [2] = new Slot("192.168.1.24", "0003", LeaseStatus.Local),
        [3] = new Slot("192.168.1.36", "0002", LeaseStatus.Local),
        [4] = new Slot("192.168.1.41", "0004", LeaseStatus.Local, Offline: true),
    };

    /// <summary>makeDevice 的可选项（样稿 opts）：blank = 「添加设备」新加的设备（不套演示场景）。</summary>
    private sealed record MakeOpts(string? Alias = null, string? Host = null, int? Port = null, string? Token = null, bool? AutoConnect = null, bool? Offline = null, bool Blank = false);

    private DemoDevice MakeDevice(int k, string modelKey, int seed, MakeOpts? opts)
    {
        opts ??= new MakeOpts();
        var m = Models.Get(modelKey) ?? Models.Get("PR8") ?? Models.All[0];
        Slots.TryGetValue(k, out var slot);
        var sc = opts.Blank ? DemoScenario.Blank(m) : DemoScenario.Scenario(m.Key == "PR8-4" ? "PR8-4" : m.Key);
        var rand = new DemoRng(seed + k * 7919);
        var now = Now;
        bool offline = opts.Offline ?? slot?.Offline ?? false;
        var lostAt = AddMs(now, -20000);
        var dataNow = offline ? lostAt : now;
        var lib = DemoScenario.ProgramLibrary(m);
        string snPrefix = m.Key == "PR8-4" ? "PR8" : m.Key;
        var dev = new DemoDevice(this, k, m)
        {
            Alias = !string.IsNullOrEmpty(opts.Alias) ? opts.Alias : k + "号机",
            Host = !string.IsNullOrEmpty(opts.Host) ? opts.Host : slot?.Host ?? "192.168.1." + (40 + k),
            Port = opts.Port is int port && port != 0 ? port : 8080,
            Token = !string.IsNullOrEmpty(opts.Token) ? opts.Token : "ht-" + (0x5a17 * k).ToString("x", CultureInfo.InvariantCulture) + "-" + snPrefix.ToLowerInvariant() + "-a9c4",
            Sn = snPrefix + "-2026-" + (slot?.Serial ?? k.ToString(CultureInfo.InvariantCulture).PadLeft(4, '0')),
            AutoConnect = opts.AutoConnect ?? true,
            DataAt = dataNow,
            DefaultLease = slot?.Lease ?? LeaseStatus.Local,
            DefaultOffline = offline,
        };
        var c = dev.Conn;
        c.State = offline ? ConnStatus.Offline : ConnStatus.Online;
        c.Since = offline ? lostAt : DemoScenario.At(7, 58, 12);
        c.LostAt = offline ? lostAt : null;
        c.RetryS = RetryS;
        c.ConnectS = 0;
        c.Forced = offline;
        c.Attempts = offline ? 1 : 0;
        c.EverOnline = true;
        dev.Lease.State = LeaseStatus.Local;
        dev.Lease.Holder = DeviceTexts.TouchScreen;
        dev.Lease.Since = DemoScenario.At(7, 58, 12);
        if (sc.Stir is { } st) dev.Stir = new DemoStir { On = st.On, Rpm = st.Rpm };
        foreach (var g in sc.Gas) dev.GasList.Add(new KeyValuePair<string, bool>(g.Key, g.Open));
        dev.ProgramList.AddRange(lib);

        foreach (var slotCh in m.Channels)
        {
            sc.Ch.TryGetValue(slotCh.N, out var spec);
            var ch = MakeChannel(dev, slotCh.N, slotCh.Installed, spec, lib, rand, now);
            dev.ChannelList.Add(ch);
            ch.HistoryList.AddRange(DemoHistory.Generate(dev, ch, spec, rand, now, dataNow));
        }
        foreach (var a in sc.Alarms) dev.AlarmList.Add(NewAlarm(a.Level, a.Ch, a.Code, a.Text, a.At, false));
        for (int i = 0; i < m.Comm.Count; i++)
        {
            var cm = m.Comm[i];
            dev.CommList.Add(new DemoComm(cm.Key, cm.Name, cm.Count)
            {
                Online = !offline,
                LastOk = AddMs(dataNow, -JsNumber.Round(rand.Next() * 1500) - i * 120),
            });
        }
        SetLease(dev, DeviceTexts.LeaseCode(dev.DefaultLease), DemoScenario.At(9, 28, 4));
        if (slot is { Session: true } && !offline)
        {
            var s = new DemoSession("PT-20261108-0930", DemoScenario.At(9, 30, 2));
            s.MarkerList.Add(new DemoMarker(DemoScenario.At(9, 42, 10), "RV1 开始反应", 1));
            s.MarkerList.Add(new DemoMarker(DemoScenario.At(9, 54, 0), "RV3 加样后升温", 3));
            s.MarkerList.Add(new DemoMarker(DemoScenario.At(10, 8, 0), "RV4 开始充压", 4));
            dev.Session = s;
        }
        SeedEvents(dev, now, offline ? lostAt : null);
        return dev;
    }

    private static readonly StateKey[] HeatingStates = { StateKey.Heat, StateKey.Press, StateKey.React, StateKey.Alarm, StateKey.Wait };

    private static DemoChannel MakeChannel(DemoDevice dev, int n, bool installed, ChSpec? spec, List<ProgramDef> lib, DemoRng rand, DateTime now)
    {
        var m = dev.Model;
        ChSpec s;
        if (!installed) s = new ChSpec();
        else if (spec != null) s = spec;
        else
        {
            double tempC = 23.8 + rand.Next() * 0.6;
            double p = 0.004 + rand.Next() * 0.002;
            s = new ChSpec { State = n <= 2 ? StateKey.Ready : StateKey.Idle, TempC = tempC, P = p };
        }
        var st = installed ? s.State : StateKey.Idle;
        var tl = s.Tl ?? TlSpec.Empty;
        double heatRate = installed
            ? (DemoHistory.Truthy(tl.Rate) ? tl.Rate!.Value : DemoHistory.Truthy(tl.HeatAt) && s.TempC != null ? (s.TempC.Value - (tl.From ?? double.NaN)) / tl.HeatAt!.Value : 6) / 60
            : 0.1;
        var ch = new DemoChannel(n, installed)
        {
            State = st,
            TempC = s.TempC ?? 24,
            SpC = s.SpC,
            PressureMPa = s.P ?? 0.005,
            PTargetMPa = s.PTarget,
            HeaterPct = DemoHistory.Or(s.Heater, 0),
            Heating = installed && Array.IndexOf(HeatingStates, st) >= 0 && s.SpC != null,
            Cooling = s.Cooling,
            Mmol = s.Mmol,
            VolumeMl = s.Vol ?? (installed ? JsNumber.Round(m.Vessel.MaxFillMl * 0.5) : 0),
            Sv = s.Sv,
            StirOn = s.StirOn,
            Rpm = DemoHistory.Or(s.Rpm, m.StirMode == StirMode.Independent ? 500 : 0),
            HeatRate = Clamp(heatRate, 0.03, 0.2),
            PRate = DemoHistory.Truthy(tl.PRate) ? tl.PRate!.Value / 60 : Math.Max(0.0005, DemoHistory.Or(s.PTarget, 0.35) / 300),
            Alarm = st == StateKey.Alarm,
        };
        if (installed && s.Prog != null)
        {
            var def = lib.FirstOrDefault(d => d.Id == s.Prog.Id);
            if (def != null) ch.Program = MakeProgram(def, s.Prog, tl, now);
        }
        return ch;
    }

    /// <summary>
    /// 样稿 makeProgram：p = { id, stepIndex, status, remainMin }；<paramref name="tl"/> = 场景时间线（null = 从现在开始 / 只是下发）。
    /// </summary>
    internal static ProgramRecord MakeProgram(ProgramDef def, ProgSpec? p, TlSpec? tl, DateTime now)
    {
        p ??= new ProgSpec(def.Id);
        var steps = def.Steps;
        var status = p.Status ?? ProgramStatus.Running;
        int idx = status == ProgramStatus.Done ? steps.Count - 1 : p.StepIndex ?? 0;
        var prog = new ProgramRecord { Id = def.Id, Name = def.Name, Note = def.Note, Steps = steps, StepIndex = idx, Status = status };
        if (status == ProgramStatus.Loaded) return prog;
        if (tl is null) return prog with { StartedAt = now, StepStartedAt = now, StepDurS = Progress.StepPlanS(steps.Count > 0 ? steps[0] : null) };
        var starts = DeriveStarts(def, tl, idx, out double? endedAgo);
        var log = new List<StepLogEntry>();
        for (int i = 0; i < idx; i++) log.Add(new StepLogEntry(i, AddMs(now, -starts[i] * MinMs), AddMs(now, -starts[i + 1] * MinMs)));
        var startedAt = AddMs(now, -starts[0] * MinMs);
        var stepStartedAt = AddMs(now, -starts[idx] * MinMs);
        double el = Ms(now, stepStartedAt) / 1000;
        DateTime? endedAt = null;
        double stepDurS;
        var cur = steps[idx];
        if (status == ProgramStatus.Done)
        {
            endedAt = AddMs(now, -(DemoHistory.Truthy(endedAgo) ? endedAgo!.Value : 1) * MinMs);
            log.Add(new StepLogEntry(idx, stepStartedAt, endedAt.Value));
            stepDurS = Ms(endedAt.Value, stepStartedAt) / 1000;
        }
        else if (cur.Type is StepKind.React or StepKind.Wait) stepDurS = (cur.Minutes ?? double.NaN) * 60;
        else stepDurS = el + (p.RemainMin is double rm ? rm * 60 : Math.Max(60, cur.EstMin * 60 - el));
        return prog with { Log = log.ToArray(), StartedAt = startedAt, StepStartedAt = stepStartedAt, StepDurS = stepDurS, EndedAt = endedAt };
    }

    /// <summary>样稿 deriveStarts：各步开始于几分钟前（held 时间线按反应步倒推，并给出结束于几分钟前）。</summary>
    private static double[] DeriveStarts(ProgramDef def, TlSpec tl, int idx, out double? endedAgo)
    {
        var steps = def.Steps;
        var output = new double[Math.Max(steps.Count, idx + 1)];
        endedAgo = null;
        if (tl.Held != null)
        {
            int ir = -1;
            for (int i = 0; i < steps.Count; i++) if (steps[i].Type == StepKind.React) { ir = i; break; }
            double until = tl.Until ?? double.NaN;
            if (ir >= 0)
            {
                output[ir] = until + (steps[ir].Minutes ?? double.NaN);
                for (int i = ir - 1; i >= 0; i--) output[i] = output[i + 1] + steps[i].EstMin;
            }
            double t = until;
            for (int i = ir + 1; i < steps.Count; i++) { output[i] = t; t -= steps[i].EstMin; }
            endedAgo = Math.Max(0.5, t);
            return output;
        }
        double?[] marks = { tl.HeatAt, tl.PressAt, tl.ReactAt };
        for (int i = 0; i <= idx; i++)
            output[i] = i < marks.Length && marks[i] is double mk ? mk : i > 0 ? output[i - 1] - steps[i - 1].EstMin : 1;
        return output;
    }

    /// <summary>样稿 seedEvents：初始事件（连接、控制权、会话、程序开始 / 换步 / 结束、报警、断线），只留不晚于现在的，最新在前（稳定排序）。</summary>
    private void SeedEvents(DemoDevice dev, DateTime now, DateTime? lostAt)
    {
        var ev = new List<DemoEvent>();
        void Push(DateTime t, EventLevel level, int? n, string text) => ev.Add(new DemoEvent("e" + _eventSeq++, t, level, n, text));
        Push(DemoScenario.At(7, 58, 12), EventLevel.Info, null, "已连接 " + dev.Host + ":" + dev.Port);
        if (dev.Lease.State == LeaseStatus.Remote) Push(DemoScenario.At(9, 28, 4), EventLevel.Op, null, "控制权：远程控制中（" + Client + "）");
        if (dev.Session != null) Push(dev.Session.StartAt, EventLevel.Op, null, "会话 " + dev.Session.Id + " 开始记录");
        foreach (var ch in dev.ChannelList)
        {
            var p = ch.Program;
            if (p?.StartedAt is not { } started) continue;
            Push(started, EventLevel.Op, ch.N, "RV" + ch.N + " 程序「" + p.Name + "」开始");
            foreach (var l in p.Log)
                if (l.I + 1 < p.Steps.Count) Push(l.T1, EventLevel.Info, ch.N, "RV" + ch.N + " 进入第 " + (l.I + 2) + " 步 " + StepText.Name(p.Steps[l.I + 1]));
            if (p.Status == ProgramStatus.Done && p.EndedAt is { } ended) Push(ended, EventLevel.Info, ch.N, "RV" + ch.N + " 程序已结束");
        }
        foreach (var a in dev.AlarmList) Push(a.At, a.Level == AlarmLevel.Err ? EventLevel.Err : EventLevel.Warn, a.Ch, a.Text);
        if (lostAt is { } lost) Push(lost, EventLevel.Err, null, "连接中断 · 正在重连");
        dev.EventList.Clear();
        dev.EventList.AddRange(ev.Where(e => e.At <= now).OrderByDescending(e => e.At));
    }

    private DemoAlarm NewAlarm(AlarmLevel level, int? ch, string code, string text, DateTime at, bool demo) =>
        new("a" + _alarmSeq++, level, ch, code, text, at, demo);

    // ———————————————————— 状态切换（样稿 setLease / setOffline / goOnline / 报警 / 急停） ————————————————————

    /// <summary>样稿 setLease：v 不是四种之一 → local；断线（连上过）的设备不可能远程控制中 / 申请中（→ 宽限 / 本地）。</summary>
    private void SetLease(DemoDevice dev, string? v, DateTime? since = null)
    {
        var l = dev.Lease;
        l.State = v switch
        {
            "requested" => LeaseStatus.Requested,
            "remote" => LeaseStatus.Remote,
            "lost" => LeaseStatus.Lost,
            _ => LeaseStatus.Local,
        };
        if (dev.Conn.State != ConnStatus.Online && dev.Conn.EverOnline)
        {
            if (l.State == LeaseStatus.Remote) l.State = LeaseStatus.Lost;
            else if (l.State == LeaseStatus.Requested) l.State = LeaseStatus.Local;
        }
        l.Since = since ?? Now;
        l.AutoAt = null;
        switch (l.State)
        {
            case LeaseStatus.Remote: l.Holder = Client; break;
            case LeaseStatus.Requested: l.Holder = DeviceTexts.TouchScreen; l.RemainS = LeaseWaitS - 15; break;
            case LeaseStatus.Lost: l.Holder = Client; l.GraceS = LeaseGraceS - 12; break;
            default: l.Holder = DeviceTexts.TouchScreen; break;
        }
    }

    private void SetOffline(DemoDevice dev, bool off)
    {
        var c = dev.Conn;
        if (off)
        {
            if (c.State == ConnStatus.Offline) return;
            c.State = ConnStatus.Offline;
            c.LostAt = Now;
            c.RetryS = RetryS;
            c.Forced = true;
            c.Attempts = 1;
            dev.DataAt = Now;
            foreach (var m in dev.CommList) m.Online = false;
            if (dev.Lease.State == LeaseStatus.Remote) { dev.Lease.State = LeaseStatus.Lost; dev.Lease.GraceS = LeaseGraceS; }
            Log(dev, EventLevel.Err, null, "连接中断 · 正在重连");
        }
        else
        {
            c.Forced = false;
            if (c.State == ConnStatus.Online) return;
            GoOnline(dev);
        }
    }

    private void GoOnline(DemoDevice dev)
    {
        var c = dev.Conn;
        if (!c.EverOnline)
        {
            // 首次连上（新添加的设备）：从这一刻起有读数；演示用一段平稳的室温历史（真机从 /snapshot 读取）
            var rand = new DemoRng(4049 + dev.K * 131);
            foreach (var ch in dev.ChannelList)
            {
                ch.HistoryList.Clear();
                if (ch.Installed) ch.HistoryList.AddRange(DemoHistory.Generate(dev, ch, null, rand, Now, Now));
            }
        }
        c.State = ConnStatus.Online;
        c.Since = Now;
        c.LostAt = null;
        c.Attempts = 0;
        c.EverOnline = true;
        dev.DataAt = Now;
        foreach (var m in dev.CommList) { m.Online = true; m.LastOk = Now; }
        if (dev.Lease.State == LeaseStatus.Lost) { dev.Lease.State = LeaseStatus.Remote; dev.Lease.Holder = Client; }
        Log(dev, EventLevel.Info, null, "已连接 " + dev.Host + ":" + dev.Port);
    }

    private static readonly StateKey[] Running = { StateKey.Heat, StateKey.Press, StateKey.React, StateKey.Alarm, StateKey.Wait, StateKey.Stir, StateKey.Cool };

    private void RaiseDemoAlarm(DemoDevice dev)
    {
        var ch = dev.ChannelList.FirstOrDefault(c => c.Installed && c.State == StateKey.React)
                 ?? dev.ChannelList.FirstOrDefault(c => c.Installed && Array.IndexOf(Running, c.State) >= 0)
                 ?? dev.ChannelList.FirstOrDefault(c => c.Installed);
        if (ch is null) return;
        double lim = dev.Model.Limits.PressureMaxMPa;
        double p = DemoHistory.Fixed(Math.Max(ch.PressureMPa, lim * 0.6) * 1.08, 3);
        double limit = DemoHistory.Fixed(Math.Max(ch.PressureMPa, lim * 0.6), 2);
        ch.State = StateKey.Alarm;
        ch.Alarm = true;
        ch.Sv = false;
        ch.PressureMPa = p;
        if (ch.Program is { Status: ProgramStatus.Running } pr) ch.Program = pr with { Status = ProgramStatus.Held };
        string text = "RV" + ch.N + " 超压 " + Fmt.Mpa(p) + " MPa ≥ " + JsNumber.ToFixed(limit, 2) + " MPa · 已关闭 SV" + ch.N;
        dev.AlarmList.Insert(0, NewAlarm(AlarmLevel.Err, ch.N, "P-HIGH", text, Now, true));
        Log(dev, EventLevel.Err, ch.N, text);
    }

    private static void ClearAlarms(DemoDevice dev)
    {
        dev.AlarmList.Clear();
        foreach (var ch in dev.ChannelList)
        {
            if (ch.State != StateKey.Alarm) continue;
            ch.Alarm = false;
            var step = ch.Program?.CurrentStep;
            ch.State = step != null ? StateForStep(step) : StateKey.Ready;
            if (ch.Program is { Status: ProgramStatus.Held } pr) ch.Program = pr with { Status = ProgramStatus.Running };
            if (ch.PTargetMPa is double pt && ch.PressureMPa > pt * 1.05) ch.PressureMPa = pt * 0.98;
        }
    }

    private void RestoreAlarms(DemoDevice dev)
    {
        ClearAlarms(dev);
        if (Stress != 0) { StressAlarms(dev); return; }   // 压力测试数据：恢复的是它自己的报警
        var sc = DemoScenario.Scenario(dev.ModelKey == "PR8-4" ? "PR8-4" : dev.ModelKey);
        foreach (var a in sc.Alarms)
        {
            dev.AlarmList.Add(NewAlarm(a.Level, a.Ch, a.Code, a.Text, a.At, false));
            var ch = a.Ch is int n ? dev.Channel(n) : null;
            if (ch != null && a.Ch is int nn && sc.Ch.TryGetValue(nn, out var spec) && spec.State == StateKey.Alarm)
            {
                ch.State = StateKey.Alarm;
                ch.Alarm = true;
                ch.Sv = false;
                ch.PressureMPa = spec.P ?? double.NaN;
                if (ch.Program is { Status: ProgramStatus.Running } pr) ch.Program = pr with { Status = ProgramStatus.Held };
            }
        }
    }

    private void DoEstop(DemoDevice dev, string? by = null)
    {
        if (dev.Estop.Active) return;
        string who = by ?? Client;
        // 断线的设备：本机不可能下发急停 —— 演示里表示「断线前已急停」，时间记在最后收到数据的时刻
        var t = dev.Conn.State != ConnStatus.Online && dev.DataAt is { } da ? da : Now;
        dev.Estop.Active = true;
        dev.Estop.At = t;
        dev.Estop.By = who;
        foreach (var ch in dev.ChannelList)
        {
            if (!ch.Installed) continue;
            bool running = Array.IndexOf(Running, ch.State) >= 0;
            ch.Heating = false;
            ch.Cooling = false;
            ch.HeaterPct = 0;
            ch.Sv = false;
            ch.StirOn = false;
            if (ch.Program is { Status: ProgramStatus.Running or ProgramStatus.Held } pr) ch.Program = pr with { Status = ProgramStatus.Aborted };
            if (running) ch.State = StateKey.Stopped;
        }
        if (dev.Stir != null) dev.Stir.On = false;
        for (int i = 0; i < dev.GasList.Count; i++) dev.GasList[i] = new KeyValuePair<string, bool>(dev.GasList[i].Key, false);
        dev.AlarmList.Insert(0, NewAlarm(AlarmLevel.Err, null, "ESTOP", "急停已触发 · 加热 / 进气 / 搅拌全部切断", t, false));
        Log(dev, EventLevel.Err, null, "急停已触发（" + who + "）", t);
    }

    private void ResetEstop(DemoDevice dev)
    {
        if (!dev.Estop.Active) return;
        dev.Estop.Active = false;
        dev.Estop.At = null;
        dev.Estop.By = null;
        dev.AlarmList.RemoveAll(a => a.Code == "ESTOP");
        Log(dev, EventLevel.Op, null, "急停已在触屏复位");
    }

    /// <summary>样稿 log：事件插到最前（给了时刻时按时刻重新稳定排序），最多留 200 条。</summary>
    private void Log(DemoDevice d, EventLevel level, int? n, string text, DateTime? at = null)
    {
        d.EventList.Insert(0, new DemoEvent("e" + _eventSeq++, at ?? Now, level, n, text));
        if (at != null)
        {
            var sorted = d.EventList.OrderByDescending(e => e.At).ToList();
            d.EventList.Clear();
            d.EventList.AddRange(sorted);
        }
        if (d.EventList.Count > 200) d.EventList.RemoveRange(200, d.EventList.Count - 200);
    }

    private void RaiseToast(string text, DemoToastKind kind) => Toast?.Invoke(this, new DemoToastEventArgs(text, kind));

    // ———————————————————— 压力测试数据（样稿 stress=1 | 2） ————————————————————

    private const string StressZh = "一二三四五六七八";
    private static readonly double StressElMs = 999 * MinMs;   // 当前步已用 999 min，还剩 999 min

    private static string StressAlias(DemoDevice dev, int level)
    {
        int i = (dev.K - 1) % 8;
        if (level == 2) return "ReactorLab-" + (dev.Model.Key switch { "PR4" => "PR4", "PR16" => "P16", _ => "PR8" }) + "-" + "ABCDEFGH"[i];
        return "高压加氢测试" + StressZh[i] + "号";
    }

    /// <summary>样稿 stressProgram：最长的程序名 + 按型号上限的步骤。</summary>
    private static ProgramDef StressProgram(ModelProfile m)
    {
        var v = StressValues.Of(m);
        string gas = (m.GasInlets.FirstOrDefault(g => g.Key is "a" or "h2") ?? m.GasInlets[0]).Key;
        string name = m.Key switch { "PR4" => "高压加氢-250-长时保温", "PR16" => "平行筛选-200-长时反应", _ => "催化评价-200-长时反应" };
        var steps = new List<ProgramStep>
        {
            new(StepKind.Heat) { Sp = v.Sp, Rpm = v.Rpm, End = new StepEnd(EndKind.Temp, v.Sp - 0.5, "≥"), EstMin = 45 },
            new(StepKind.Press) { Sp = v.Sp, Target = v.P, Gas = gas, Rpm = v.Rpm, End = new StepEnd(EndKind.Press, v.P, "≥"), EstMin = 15 },
            new(StepKind.React) { Sp = v.Sp, Target = v.P, Gas = gas, Minutes = 999, Rpm = v.Rpm, End = new StepEnd(EndKind.Time, 999), EstMin = 999 },
        };
        if (m.HasCooling) steps.Add(new ProgramStep(StepKind.Cool) { Sp = 40, Rpm = 300, End = new StepEnd(EndKind.Temp, 40, "≤"), EstMin = 60 });
        steps.Add(new ProgramStep(StepKind.End) { CoolTo = 40, Vent = true, Rpm = 0, End = new StepEnd(EndKind.Temp, 40, "<"), EstMin = 30 });
        return new ProgramDef("stress", name, JsNumber.ToString(v.Sp) + " ℃ · " + Fmt.Mpa(v.P, v.Dec) + " MPa " + m.GasName(gas) + " · 反应 999 min", steps);
    }

    private static string StressKind(ModelProfile m, int n)
    {
        int i = IndexOf(m.Installed, n);
        return i == m.Installed.Count - 1 ? "alarm" : new[] { "react", "press", "heat" }[i % 3];
    }

    private static ProgramRecord StressProg(ProgramDef def, int idx, DateTime refT)
    {
        var stepStartedAt = AddMs(refT, -StressElMs);
        var log = new List<StepLogEntry>();
        var t = stepStartedAt;
        for (int i = idx - 1; i >= 0; i--)
        {
            var t0 = AddMs(t, -def.Steps[i].EstMin * MinMs);
            log.Insert(0, new StepLogEntry(i, t0, t));
            t = t0;
        }
        return new ProgramRecord
        {
            Id = def.Id, Name = def.Name, Note = def.Note, Steps = def.Steps, StepIndex = idx, Status = ProgramStatus.Running,
            Log = log.ToArray(), StartedAt = t, StepStartedAt = stepStartedAt, StepDurS = StressElMs * 2 / 1000,
        };
    }

    /// <summary>样稿 stressSlide：程序推到「当前步已用 999 min」，整段程序跟着平移。</summary>
    private static ProgramRecord StressSlide(ProgramRecord p, DateTime refT)
    {
        if (p.StepStartedAt is not { } ss) return p;
        double dt = Ms(AddMs(refT, -StressElMs), ss);
        if (dt == 0) return p;
        var log = p.Log.Select(l => new StepLogEntry(l.I, AddMs(l.T0, dt), AddMs(l.T1, dt))).ToArray();
        return p with { StepStartedAt = AddMs(ss, dt), StartedAt = p.StartedAt is { } st ? AddMs(st, dt) : null, Log = log };
    }

    private void StressAlarms(DemoDevice dev)
    {
        var m = dev.Model;
        var v = StressValues.Of(m);
        var inst = m.Installed;
        var refT = dev.DataAt ?? Now;
        int nA = inst[^1];
        int nW = inst[0];
        for (int i = 0; i < inst.Count; i++) if (i % 3 == 2 && inst[i] != nA) { nW = inst[i]; break; }
        string lim = JsNumber.ToFixed(m.Limits.PressureMaxMPa * 0.95, 2);
        dev.AlarmList.Add(NewAlarm(AlarmLevel.Err, nA, "P-HIGH", "RV" + nA + " 超压 " + Fmt.Mpa(v.P, v.Dec) + " MPa ≥ " + lim + " MPa · 已关闭 SV" + nA, AddMs(refT, -2 * MinMs), false));
        dev.AlarmList.Add(NewAlarm(AlarmLevel.Warn, nW, "HEAT-SLOW", "RV" + nW + " 升温较慢 · 已用 999 min", AddMs(refT, -5 * MinMs), false));
        var ch = dev.Channel(nA);
        if (ch != null)
        {
            ch.State = StateKey.Alarm;
            ch.Alarm = true;
            ch.Sv = false;
            if (ch.Program is { Status: ProgramStatus.Running } pr) ch.Program = pr with { Status = ProgramStatus.Held };
        }
    }

    private void StressDevice(DemoDevice dev, int level)
    {
        var m = dev.Model;
        var v = StressValues.Of(m);
        var now = Now;
        var refT = dev.DataAt ?? now;
        var def = StressProgram(m);
        dev.Alias = StressAlias(dev, level);
        var rest = dev.ProgramList.Where(x => x.Id != def.Id).ToList();
        dev.ProgramList.Clear();
        dev.ProgramList.Add(def);
        dev.ProgramList.AddRange(rest);
        if (dev.Stir != null) dev.Stir = new DemoStir { On = true, Rpm = v.Rpm };
        SetGas(dev, def.Steps[1].Gas!, true);
        var rand = new DemoRng(97 + dev.K * 131);
        foreach (var ch in dev.ChannelList)
        {
            if (!ch.Installed) continue;
            string kind = StressKind(m, ch.N);
            int idx = kind == "heat" ? 0 : kind == "press" ? 1 : 2;
            ch.State = kind switch { "heat" => StateKey.Heat, "press" => StateKey.Press, _ => StateKey.React };
            ch.TempC = v.T;
            ch.SpC = v.Sp;
            ch.PressureMPa = v.P;
            ch.PTargetMPa = v.P;
            ch.HeaterPct = 100;
            ch.Heating = true;
            ch.Cooling = false;
            ch.Mmol = v.Mmol;
            ch.VolumeMl = v.Vol;
            ch.Sv = kind != "heat";
            ch.StirOn = true;
            ch.Rpm = v.Rpm;
            ch.Alarm = false;
            ch.Program = StressProg(def, idx, refT);
            ch.HistoryList.Clear();
            ch.HistoryList.AddRange(DemoHistory.Stress(v, rand, now, refT));
        }
        dev.AlarmList.Clear();
        StressAlarms(dev);
        SeedEvents(dev, now, dev.Conn.State == ConnStatus.Offline ? dev.Conn.LostAt : null);
    }

    /// <summary>样稿 stressPin：每秒把数值钉回最坏值（不跑物理模型）；程序保持「剩 999 min」。</summary>
    private void StressPin(DemoDevice dev)
    {
        var v = StressValues.Of(dev.Model);
        if (dev.Stir != null) dev.Stir.Rpm = v.Rpm;
        foreach (var ch in dev.ChannelList)
        {
            if (!ch.Installed) continue;
            ch.TempC = v.T;
            ch.SpC = v.Sp;
            ch.PressureMPa = v.P;
            ch.PTargetMPa = v.P;
            ch.HeaterPct = ch.Heating ? 100 : 0;
            ch.VolumeMl = v.Vol;
            ch.Mmol = v.Mmol;
            ch.Rpm = v.Rpm;
            if (ch.Program is { Status: ProgramStatus.Running or ProgramStatus.Held } p)
                ch.Program = StressSlide(p, Now) with { StepDurS = StressElMs * 2 / 1000 };
        }
    }

    /// <summary>设置气路（键不存在时追加到最后，同 JS 对象赋值）。</summary>
    private static void SetGas(DemoDevice dev, string key, bool open)
    {
        for (int i = 0; i < dev.GasList.Count; i++)
            if (dev.GasList[i].Key == key) { dev.GasList[i] = new KeyValuePair<string, bool>(key, open); return; }
        dev.GasList.Add(new KeyValuePair<string, bool>(key, open));
    }

    // ———————————————————— 小工具 ————————————————————

    internal static StateKey StateForStep(ProgramStep step) => step.Type switch
    {
        StepKind.Heat => StateKey.Heat,
        StepKind.Press => StateKey.Press,
        StepKind.React => StateKey.React,
        StepKind.Cool => StateKey.Cool,
        StepKind.End => StateKey.Cool,
        StepKind.Stir => StateKey.Stir,
        StepKind.Purge => StateKey.Press,
        StepKind.Wait => StateKey.Wait,
        _ => StateKey.Ready,
    };

    private static int IndexOf(IReadOnlyList<int> list, int n)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == n) return i;
        return -1;
    }

    private static string Key(int k) => k.ToString(CultureInfo.InvariantCulture);

    private static string? Lookup(IReadOnlyList<KeyValuePair<string, string>> map, string k)
    {
        foreach (var kv in map) if (kv.Key == k) return kv.Value;
        return null;
    }

    private static string MapText(IReadOnlyList<KeyValuePair<string, string>> map) => string.Join(",", map.Select(kv => kv.Key + ":" + kv.Value));

    private static double Clamp(double v, double a, double b) => Math.Max(a, Math.Min(b, v));

    internal static double Ms(DateTime a, DateTime b) => DemoHistory.Ms(a, b);

    internal static DateTime AddMs(DateTime t, double ms) => DemoHistory.AddMs(t, ms);
}
