using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using Prog = ParallelReactor.Control.Core.Programs.Progress;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 演示数据里的一台设备（样稿 API.md §3.1 Device）：身份、型号、连接、控制权、急停、会话、共用搅拌、气路、程序库、反应釜（含未安装，
/// 数量 = 型号的通道数）、报警、事件（最新在前）、模块通讯、最后数据时刻。
/// <para>只读：状态只由所属的 <see cref="DemoFleet"/> 改（参数、每秒推进、操作），改完触发 DemoFleet.Changed。集合实例在设备的生命周期内不变
/// （内容就地更新），可以长期持有。不是线程安全的：与 DemoFleet 在同一个线程（UI 线程）上用。</para>
/// 读数 / 状态字 / 进度一律用这里的帮助方法（同样稿 PR.sim.reading / stateKey / stateText / progress …），不要在界面里自己判断。
/// </summary>
public sealed class DemoDevice
{
    internal readonly List<DemoChannel> ChannelList = new();
    internal readonly List<DemoAlarm> AlarmList = new();
    internal readonly List<DemoEvent> EventList = new();
    internal readonly List<DemoComm> CommList = new();
    internal readonly List<DemoSession> SessionList = new();
    internal readonly List<ProgramDef> ProgramList = new();
    internal readonly List<KeyValuePair<string, bool>> GasList = new();

    internal DemoDevice(DemoFleet fleet, int k, ModelProfile model)
    {
        Fleet = fleet;
        K = k;
        Model = model;
        Channels = ChannelList.AsReadOnly();
        Alarms = AlarmList.AsReadOnly();
        Events = EventList.AsReadOnly();
        Comm = CommList.AsReadOnly();
        Sessions = SessionList.AsReadOnly();
        Programs = ProgramList.AsReadOnly();
    }

    internal DemoFleet Fleet { get; }

    /// <summary>位置（1 起，设备列表里的序号；路由 #/device/&lt;k&gt; 用它）。</summary>
    public int K { get; }
    /// <summary>'d1' …</summary>
    public string Id => "d" + K;
    public string Alias { get; internal set; } = "";
    public ModelProfile Model { get; }
    public string ModelKey => Model.Key;
    public string Host { get; internal set; } = "";
    public int Port { get; internal set; } = 8080;
    public string Token { get; internal set; } = "";
    public string Sn { get; internal set; } = "";
    public string HmiVersion { get; internal set; } = "1.0.6";
    public string ApiVersion { get; internal set; } = "v1.2";
    public bool AutoConnect { get; internal set; } = true;

    public DemoConn Conn { get; } = new();
    public DemoLease Lease { get; } = new();
    public DemoEstop Estop { get; } = new();
    /// <summary>记录中的会话；没有为 null。</summary>
    public DemoSession? Session { get; internal set; }
    /// <summary>已结束的会话（最新在前）。</summary>
    public IReadOnlyList<DemoSession> Sessions { get; }
    /// <summary>全釜共用搅拌；独立搅拌的型号（HT-PR4）为 null。</summary>
    public DemoStir? Stir { get; internal set; }
    /// <summary>这台型号的程序库。</summary>
    public IReadOnlyList<ProgramDef> Programs { get; }
    /// <summary>全部反应釜（含未安装），按通道号。</summary>
    public IReadOnlyList<DemoChannel> Channels { get; }
    /// <summary>当前报警（未清除的，含已确认）。</summary>
    public IReadOnlyList<DemoAlarm> Alarms { get; }
    /// <summary>事件，最新在前，最多 200 条。</summary>
    public IReadOnlyList<DemoEvent> Events { get; }
    public IReadOnlyList<DemoComm> Comm { get; }
    /// <summary>最后收到数据的时刻（断线时停住；从未连上为 null）。</summary>
    public DateTime? DataAt { get; internal set; }

    /// <summary>默认控制权（样稿 SLOTS[k].lease）与默认是否断线（4 号位）；参数覆盖取消时恢复到它。</summary>
    internal LeaseStatus DefaultLease { get; set; }
    internal bool DefaultOffline { get; set; }

    // ———————————————————— 读取（样稿 PR.sim.*） ————————————————————

    /// <summary>已连接（样稿 online）。</summary>
    public bool Online => Conn.State == ConnStatus.Online;

    /// <summary>第 n 路；没有返回 null。</summary>
    public DemoChannel? Channel(int n)
    {
        foreach (var c in ChannelList) if (c.N == n) return c;
        return null;
    }

    /// <summary>气路 key 是否打开（进气 / 排空；只读，触屏操作）。</summary>
    public bool GasOpen(string key)
    {
        foreach (var kv in GasList) if (kv.Key == key) return kv.Value;
        return false;
    }

    /// <summary>气路状态（顺序同型号表：进气 + 排空）。</summary>
    public IReadOnlyList<KeyValuePair<string, bool>> Gas => GasList.AsReadOnly();

    /// <summary>程序库里的一条；没有返回 null。</summary>
    public ProgramDef? ProgramById(string? id) => id is null ? null : ProgramList.FirstOrDefault(p => p.Id == id);

    /// <summary>有效转速（样稿 rpmOf）：共用搅拌 = 设备转速（关着为 0），独立搅拌 = 本釜转速（关着为 0），未安装 0。</summary>
    public double RpmOf(DemoChannel? ch)
    {
        if (ch is null || !ch.Installed) return 0;
        if (Model.StirMode == StirMode.Shared) return Stir is { On: true } s ? s.Rpm : 0;
        return ch.StirOn ? ch.Rpm : 0;
    }

    /// <summary>显示用的读数（样稿 reading）：未安装 / 从未连上 / 没有数据为 null；断线时是最后的值（界面另标 <see cref="DataText"/>）。</summary>
    public double? Reading(DemoChannel? ch, DemoReading key)
    {
        if (ch is null || !ch.Installed || !Conn.EverOnline || DataAt is null) return null;
        return key switch
        {
            DemoReading.TempC => ch.TempC,
            DemoReading.PressureMPa => ch.PressureMPa,
            DemoReading.HeaterPct => ch.HeaterPct,
            DemoReading.Rpm => RpmOf(ch),
            DemoReading.Mmol => ch.Mmol,
            DemoReading.VolumeMl => ch.VolumeMl,
            DemoReading.SpC => ch.SpC,
            DemoReading.PTargetMPa => ch.PTargetMPa,
            _ => null,
        };
    }

    /// <summary>显示状态键（样稿 stateKey，颜色 / 样式用）：未安装 → Uninstalled；设备断线 → Offline；否则反应釜自己的状态。</summary>
    public StateKey StateKeyOf(DemoChannel? ch)
    {
        if (ch is null || !ch.Installed) return StateKey.Uninstalled;
        if (!Online) return StateKey.Offline;
        return ch.State;
    }

    /// <summary>状态字（样稿 stateText）：'未安装'；等待中且当前步要进气 → '等待进气'；其余按状态（断线时给最后的状态，「离线」由 <see cref="StateWord"/> 给）。</summary>
    public string StateText(DemoChannel? ch)
    {
        if (ch is null || !ch.Installed) return StateKeys.Word(StateKey.Uninstalled);
        if (ch.State == StateKey.Wait && !string.IsNullOrEmpty(ch.Program?.CurrentStep?.Gas)) return Prog.WaitGasWordDemo;
        return StateKeys.Word(ch.State);
    }

    /// <summary>界面上的状态字（样稿 ui.stateWord / stateBadge）：设备断线时「离线」（从未连上「未连接」），否则 <see cref="StateText"/>。</summary>
    public string StateWord(DemoChannel? ch) => StateKeyOf(ch) == StateKey.Offline ? StateKeys.OfflineText(Conn.EverOnline) : StateText(ch);

    /// <summary>反应釜全名（样稿 ui.rvName）：'1号机 · RV1'。</summary>
    public string RvName(int n) => Alias + " · RV" + n;

    /// <summary>算进度用的现场情况（断线冻结在 DataAt、反应釜状态、型号、气路）。</summary>
    public ProgressContext ProgressContextOf(DemoChannel? ch) =>
        new(Fleet.Now, Online, DataAt, ch?.State ?? StateKey.Idle, Model, GasOpen);

    /// <summary>程序进度（样稿 progress）；没有程序为 null。</summary>
    public ProgressInfo? Progress(DemoChannel? ch) => ch?.Program is { } p ? Prog.Of(p, ProgressContextOf(ch)) : null;

    /// <summary>进度文字（样稿 ui.progText）：'第 3/4 步 · 剩 42 min' / 短 '3/4 · 42 min'；没有程序 '无程序'。</summary>
    public string ProgText(DemoChannel? ch, bool shortForm = false) => Prog.Text(Progress(ch), shortForm);

    /// <summary>时间轴分段（样稿 segments）。</summary>
    public IReadOnlyList<ProgramSegment> Segments(DemoChannel? ch) => Prog.Segments(ch?.Program, ProgressContextOf(ch));

    /// <summary>曲线数据（样稿 series）：最近 <paramref name="minutes"/> 分钟的 (时刻, 值)，值缺失为 null。</summary>
    public IReadOnlyList<(DateTime T, double? V)> Series(int n, DemoReading key, double minutes = DemoHistory.HistoryMin)
    {
        var ch = Channel(n);
        var list = new List<(DateTime, double?)>();
        if (ch is null) return list;
        var from = DemoHistory.AddMs(Fleet.Now, -(minutes > 0 ? minutes : DemoHistory.HistoryMin) * 60000);
        foreach (var s in ch.HistoryList)
        {
            if (s.T < from) continue;
            list.Add((s.T, key switch
            {
                DemoReading.TempC => s.TempC,
                DemoReading.PressureMPa => s.PressureMPa,
                DemoReading.HeaterPct => s.HeaterPct,
                DemoReading.Rpm => s.Rpm,
                DemoReading.Mmol => s.Mmol,
                DemoReading.SpC => s.SpC,
                DemoReading.PTargetMPa => s.PTargetMPa,
                _ => null,
            }));
        }
        return list;
    }

    /// <summary>控制权文字（样稿 leaseInfo + ui.leaseBadge short）。</summary>
    public LeaseDisplay LeaseInfo => DeviceTexts.Lease(Lease.State, Lease.RemainS, Lease.GraceS, DemoFleet.Client, Lease.State == LeaseStatus.Other ? Lease.Holder : null);

    /// <summary>连接文字（样稿 connInfo）。</summary>
    public ConnDisplay ConnInfo => DeviceTexts.Conn(Conn.State, Conn.EverOnline, Conn.RetryS);

    /// <summary>'数据停在 10:11:40' / '尚未收到数据'（样稿 dataText）。</summary>
    public string DataText => DeviceTexts.DataText(DataAt, Conn.EverOnline);

    /// <summary>'急停中' / '断线前已急停'（样稿 estopText）。</summary>
    public string EstopText => DeviceTexts.EstopText(Online);

    /// <summary>统计（样稿 counts）。</summary>
    public DemoCounts Counts
    {
        get
        {
            int inst = 0, run = 0, en = 0;
            foreach (var c in ChannelList)
            {
                if (!c.Installed) continue;
                inst++;
                if (c.IsRunning) run++;
                if (c.State != StateKey.Idle) en++;
            }
            int unacked = 0, err = 0;
            foreach (var a in AlarmList) { if (!a.Acked) unacked++; if (a.Level == AlarmLevel.Err) err++; }
            return new DemoCounts(inst, run, en, AlarmList.Count, unacked, err);
        }
    }

    /// <summary>
    /// 计入「报警 / 提醒」计数的报警（样稿 ui.countedAlarms）：急停中的设备不数「急停已触发」那一条（各处已有红色「急停中」）。
    /// <paramref name="all"/> = true：和列出的卡片一一对应（设备页「当前报警」、检查器「报警」分区的标题），急停那条也算。
    /// </summary>
    public IEnumerable<DemoAlarm> CountedAlarms(bool all = false) => AlarmList.Where(a => all || !(Estop.Active && a.Code == "ESTOP"));

    /// <summary>报警计数（样稿 ui.alarmCounts）：错误报警（「报警」）、提醒、合计。</summary>
    public AlarmTally AlarmCounts(bool all = false)
    {
        int err = 0, total = 0;
        foreach (var a in CountedAlarms(all)) { total++; if (a.Level == AlarmLevel.Err) err++; }
        return new AlarmTally(err, total - err, total);
    }

    /// <summary>'报警 1 · 提醒 1' / '提醒 1' / <paramref name="none"/>（样稿 ui.alarmText）。</summary>
    public string AlarmText(bool all = false, string none = "")
    {
        var c = AlarmCounts(all);
        string t = string.Join(" · ", new[] { c.Err > 0 ? "报警 " + c.Err : "", c.Warn > 0 ? "提醒 " + c.Warn : "" }.Where(x => x.Length > 0));
        return t.Length > 0 ? t : none;
    }

    /// <summary>健康点（样稿 health）：断线 Off（用户断开 Mute）；急停或有错误报警 Err；有报警 Warn；否则 Ok。</summary>
    public DeviceHealth Health
    {
        get
        {
            if (!Online) return Conn.State == ConnStatus.Disconnected ? DeviceHealth.Mute : DeviceHealth.Off;
            if (Estop.Active || AlarmList.Any(a => a.Level == AlarmLevel.Err)) return DeviceHealth.Err;
            if (AlarmList.Count > 0) return DeviceHealth.Warn;
            return DeviceHealth.Ok;
        }
    }

    // ———————————————————— 守卫 ————————————————————

    /// <summary>这台设备此刻的守卫事实（ActionGuard.Why 的输入）。</summary>
    public GuardFacts Facts() => new()
    {
        Exists = Fleet.Contains(this),
        Alias = Alias,
        Conn = Conn.State,
        EverOnline = Conn.EverOnline,
        RetryS = Conn.RetryS,
        Lease = Lease.State,
        LeaseRemainS = Lease.RemainS,
        LeaseGraceS = Lease.GraceS,
        LeaseHolder = Lease.State == LeaseStatus.Other ? Lease.Holder : null,
        EstopActive = Estop.Active,
        Model = Model,
        SharedStir = Stir != null,
        StirOn = Stir?.On ?? false,
        Channels = ChannelList.Select(c => new ChannelFacts(c.N, c.Installed, c.State, c.Heating, c.StirOn, c.Program?.Status, c.SpC)).ToArray(),
        ProgramIds = ProgramList.Select(p => p.Id).ToArray(),
        SessionActive = Session is { Active: true },
        SessionId = Session?.Id,
        Alarms = AlarmList.Select(a => new AlarmFacts(a.Id, a.Acked)).ToArray(),
        Peers = Fleet.Devices.Where(d => d != this).Select(d => new PeerDevice(d.Alias, d.Host)).ToArray(),
    };

    /// <summary>操作是否可用：'' = 可用，否则为原因（样稿 why）。</summary>
    public string Why(DeviceAction action, ActionPayload? payload = null) => ActionGuard.Why(Facts(), action, payload);

    /// <summary>连接 / 急停 / 控制权的原因（样稿 guard(dev, kind)）。</summary>
    public string GuardText(GuardKind kind) => ActionGuard.GuardText(Facts(), kind);

    public override string ToString() => Alias + " · " + Model.Label;
}
