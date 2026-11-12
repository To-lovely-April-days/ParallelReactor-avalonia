using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Fleet;

/// <summary>资源管理器设备头第二行的报警标记（样稿 devAlarmHTML）：急停（红图标 +「急停中」）&gt; 错误报警（红点 +「报警 n」）&gt; 提醒（描边 ⚠ + 数）。</summary>
public enum AlarmMarker { None, Estop, Err, Warn }

/// <summary>
/// 一台设备（PORT-PLAN §5 M2「DeviceVm」；样稿 API.md §3.1 Device）：身份 = 位置 <see cref="K"/>（1号机 = 1，页签、路由、选择都用它），生命周期内不变；
/// 数据源（<see cref="Source"/>）可以整个换掉（演示数据按 devs / stress 重建时），视图模型照旧。<see cref="Refresh"/> 从数据源就地更新全部文字和标记，
/// 反应釜（<see cref="Reactors"/>，含未安装，数量 = 型号的通道数）、报警、事件按身份就地增删，不重建集合。
/// 同时实现外框要的 <see cref="IFleetDevice"/>（菜单、页签、状态栏、新页签弹出框）。操作在 <see cref="Actions"/>（守卫 + 执行 + 提示）。
/// </summary>
public sealed partial class DeviceVm : ObservableObject, IFleetDevice
{
    internal DeviceVm(FleetVm fleet, int k, IDeviceSource source)
    {
        Fleet = fleet;
        K = k;
        Source = source;
        Model = source.Model;
        Actions = new DeviceActions(this);
        RebuildReactors();
    }

    public FleetVm Fleet { get; }
    public int K { get; }
    public IDeviceSource Source { get; private set; }
    public DeviceActions Actions { get; }
    public ModelProfile Model { get; private set; }

    /// <summary>全部位置（含未安装），按 RV 号；实例在型号不变时不变。</summary>
    public ObservableCollection<ReactorVm> Reactors { get; } = new();
    /// <summary>当前报警（未清除，含已确认），身份 = Id。</summary>
    public ObservableCollection<AlarmVm> Alarms { get; } = new();
    /// <summary>事件，最新在前（最多 200 条）。</summary>
    public ObservableCollection<EventVm> Events { get; } = new();

    IReadOnlyList<IFleetChannel> IFleetDevice.Channels => Reactors;

    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _modelLabel = "";
    [ObservableProperty] private string _installedText = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int _port;
    [ObservableProperty] private string _sn = "";
    [ObservableProperty] private int _channelCount;

    [ObservableProperty] private ConnState _conn = ConnState.Connecting;
    [ObservableProperty] private ConnStatus _connStatus = ConnStatus.Connecting;
    [ObservableProperty] private bool _everOnline;
    [ObservableProperty] private bool _isOffline = true;
    /// <summary>
    /// 急停 / 全部停止 / 停止类现在能发：在线，或者真机只是 WebSocket 没连上、而 REST 确实还通（ConnInfo.StopReachable：要有证据，见 LiveDeviceSource.StopReachable；
    /// 这时界面写「正在重连…」/「连接中断 · 重连中 N s」，停止类照样尽力发送）。主机真的不通时为 false。全局停止格、急停 / 全部停止对话框按它挑设备。
    /// </summary>
    [ObservableProperty] private bool _canStop;
    /// <summary>新添加、还从未连上（样稿 connInfo.first）。</summary>
    [ObservableProperty] private bool _isFirst;
    [ObservableProperty] private string _connText = "";
    [ObservableProperty] private string _connShort = "";
    [ObservableProperty] private string _dataText = "";
    /// <summary>断线细节条（样稿 .pr-xoff）：「连接中断 · 重连中 12 s」/「已断开」/「正在重连…」/ 从未连上「正在连接…」。</summary>
    [ObservableProperty] private string _offText = "";
    [ObservableProperty] private string _offIcon = "wifioff";
    /// <summary>断线且控制权在宽限中：「控制权宽限 18 s · 超时交回触屏」；否则 null。</summary>
    [ObservableProperty] private string? _graceText;

    [ObservableProperty] private LeaseState _lease;
    [ObservableProperty] private LeaseStatus _leaseStatus;
    /// <summary>完整文字（'远程控制中' / '申请中 · 45 s' / '本地 · 只读' …）。</summary>
    [ObservableProperty] private string _leaseText = "";
    /// <summary>树 / 列表的短徽标（'远程控制' / '申请中 45 s' / '宽限 18 s' / '本地只读'）。</summary>
    [ObservableProperty] private string _leaseShort = "";
    /// <summary>
    /// 资源管理器设备头第二行的徽标（最窄的一处）：同 <see cref="LeaseShort"/>，只是第 5 种控制权不带持有者 ——「其他客户端」（M3 复核：
    /// 「其他客户端 PC-工位2」在 300 px 的资源管理器里只剩「其他客户端…」）；是谁写在悬停提示（<see cref="LeaseText"/>）里。
    /// </summary>
    [ObservableProperty] private string _leaseTreeShort = "";

    [ObservableProperty] private bool _estopActive;
    [ObservableProperty] private string _estopText = "";

    [ObservableProperty] private int _running;
    [ObservableProperty] private int _installed;
    [ObservableProperty] private int _errAlarms;
    [ObservableProperty] private int _warnAlarms;
    /// <summary>'报警 1 · 提醒 1' / '提醒 1' / ''。</summary>
    [ObservableProperty] private string _alarmText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMarker), nameof(IsEstopMarker), nameof(IsErrMarker), nameof(IsWarnMarker))] private AlarmMarker _marker;
    public bool HasMarker => Marker != AlarmMarker.None;
    public bool IsEstopMarker => Marker == AlarmMarker.Estop;
    public bool IsErrMarker => Marker == AlarmMarker.Err;
    public bool IsWarnMarker => Marker == AlarmMarker.Warn;
    [ObservableProperty] private string _markerText = "";
    [ObservableProperty] private string _markerTip = "";
    [ObservableProperty] private string? _sessionId;
    /// <summary>'运行 4/4'；断线时 null（不画）。</summary>
    [ObservableProperty] private string? _runText;
    /// <summary>设备头的悬停提示（别名 · 型号（已安装）/ 地址 · SN / 连接 · 会话）。</summary>
    [ObservableProperty] private string _headTip = "";
    [ObservableProperty] private StirInfo? _stir;
    [ObservableProperty] private DeviceHealth _health;
    /// <summary>反应釜号有两位数（≥ 10 路：资源管理器 RV 列 40 px）。</summary>
    [ObservableProperty] private bool _wideRvNames;

    private GuardFacts? _facts;

    /// <summary>
    /// 守卫的输入（这台设备此刻的事实，显示用）：取一次、在数据源变化 / 刷新时作废（<see cref="InvalidateFacts"/>），一次刷新里所有按钮、
    /// 菜单项、时间轴行、报警卡共用（R10：以前每问一个按钮就重取一次，时间轴一次刷新 60–120 KiB）。执行操作时不用它（DeviceActions.RunAsync 取最新的）。
    /// </summary>
    public GuardFacts Facts => _facts ??= Source.Facts();

    /// <summary>数据源变了：下一次问守卫时重取事实。</summary>
    internal void InvalidateFacts() => _facts = null;

    /// <summary>
    /// 演示数据重建：换数据源（视图模型的身份不变）。事件按 Id 增量同步，而新数据源的事件 Id 会与旧的重复（演示数据的 'e13'）——
    /// 先清掉，整份重新同步（复核：以前留着上一份演示数据的事件，检查器的「最近事件」因此跟截图顺序有关）。
    /// </summary>
    internal void SetSource(IDeviceSource source)
    {
        Source = source;
        _facts = null;
        if (Events.Count > 0) Events.Clear();
        Refresh(Fleet.Now);
    }

    private void RebuildReactors()
    {
        Model = Source.Model;
        int count = Model.ChannelCount;
        ChannelCount = count;
        WideRvNames = count >= 10;
        while (Reactors.Count > count) Reactors.RemoveAt(Reactors.Count - 1);
        for (int n = Reactors.Count + 1; n <= count; n++) Reactors.Add(new ReactorVm(this, n));
    }

    public ReactorVm? Reactor(int n) => n >= 1 && n <= Reactors.Count ? Reactors[n - 1] : null;

    /// <summary>从数据源就地刷新（UI 线程）。</summary>
    public void Refresh(DateTime now)
    {
        _facts = null;
        var s = Source;
        if (!ReferenceEquals(s.Model, Model) || s.Model.ChannelCount != Reactors.Count) RebuildReactors();
        var cfg = s.Config;
        var conn = s.Conn;
        var cd = conn.Display;
        var lease = s.Lease;
        var estop = s.Estop;
        bool online = conn.Online;

        Alias = cfg.Alias;
        ModelLabel = Model.Label;
        InstalledText = Model.InstalledText;
        Host = cfg.Host;
        Port = cfg.Port;
        Sn = s.Sn;
        ConnStatus = conn.State;
        Conn = conn.State switch
        {
            ConnStatus.Online => ConnState.Online,
            ConnStatus.Connecting => ConnState.Connecting,
            ConnStatus.Offline => ConnState.Offline,
            _ => ConnState.Disconnected,
        };
        EverOnline = conn.EverOnline;
        IsOffline = !online;
        CanStop = conn.CanStop;
        IsFirst = cd.First;
        ConnText = cd.Text;
        ConnShort = cd.Short;
        var dataText = Core.State.DeviceTexts.DataText(s.DataAt, conn.EverOnline);
        DataText = online ? "" : dataText;
        OffText = cd.First ? cd.Text : conn.State == ConnStatus.Disconnected ? "已断开" : conn.State == ConnStatus.Connecting ? "正在重连…" : "重连中 " + conn.RetryS + " s";
        OffIcon = cd.First ? "link" : "wifioff";
        GraceText = !online && lease.State == LeaseStatus.Lost ? "控制权宽限 " + lease.GraceS + " s · 超时交回触屏" : null;

        LeaseStatus = lease.State;
        Lease = lease.State switch
        {
            LeaseStatus.Remote => LeaseState.Remote,
            LeaseStatus.Requested => LeaseState.Requested,
            LeaseStatus.Lost => LeaseState.Lost,
            LeaseStatus.Other => LeaseState.Other,   // 第 5 种（真机）：被其他客户端持有，自己的徽标「其他客户端 X」
            _ => LeaseState.Local,
        };
        LeaseText = lease.Display.Text;
        LeaseShort = lease.Display.BadgeShort;
        LeaseTreeShort = lease.State == LeaseStatus.Other && lease.Display.BadgeShort != Core.State.DeviceTexts.OrphanShort
            ? Core.State.DeviceTexts.OtherShort(null) : lease.Display.BadgeShort;
        EstopActive = estop.Active;
        EstopText = estop.Active ? Core.State.DeviceTexts.EstopText(online) : "";
        Stir = s.Stir;

        // 反应釜：读数、状态、程序、进度（演示的等待进气看进气口；真机的等待气路是排队，§3.5）
        int inst = 0, run = 0;
        foreach (var r in Reactors)
        {
            var rd = s.Read(r.N);
            var prog = s.Program(r.N);
            var pr = prog is null ? null : Progress.Of(prog, ContextFor(rd, now));
            r.Apply(rd, Model, online, conn.EverOnline, cfg.Alias, dataText, prog, pr, s.Extras(r.N));
            if (rd.Installed) { inst++; if (rd.IsRunning) run++; }
        }
        Installed = inst;
        Running = run;

        // 报警（计数不数急停中设备的「急停已触发」那一条，样稿 ui.countedAlarms）
        var alarms = s.Alarms;
        SyncAlarms(alarms);
        int err = 0, warn = 0;
        foreach (var a in alarms)
        {
            if (estop.Active && a.Code == "ESTOP") continue;
            if (a.Level == AlarmLevel.Err) err++; else warn++;
        }
        ErrAlarms = err;
        WarnAlarms = warn;
        AlarmText = ShellText.AlarmText(err, warn);
        if (estop.Active) { Marker = AlarmMarker.Estop; MarkerText = EstopText; MarkerTip = EstopText + " · 复位只能在触屏" + (AlarmText.Length > 0 ? " · " + AlarmText : ""); }
        else if (err > 0) { Marker = AlarmMarker.Err; MarkerText = "报警 " + err; MarkerTip = AlarmText; }
        else if (warn > 0) { Marker = AlarmMarker.Warn; MarkerText = warn.ToString(System.Globalization.CultureInfo.InvariantCulture); MarkerTip = AlarmText; }
        else { Marker = AlarmMarker.None; MarkerText = ""; MarkerTip = ""; }
        SyncEvents(s.Events);

        var session = s.Session;
        SessionId = session is { Active: true } ? session.Id : null;
        RunText = online ? "运行 " + run + "/" + inst : null;
        // 真机没连上时多一行失败原因（LiveDeviceSource.LastError：「网络错误：…」「WebSocket 连接数已达上限 · …（HTTP 503 TOO_MANY_CLIENTS）」）——
        // 样稿只有连接状态文字；原因只放悬停提示（连接失败时另有一次提示），不占版面
        var why = !online && s is LiveDeviceSource { LastError: { Length: > 0 } le } ? "\n" + le : "";
        HeadTip = cfg.Alias + " · " + Model.Label + "（" + Model.InstalledText + " 已安装）\n" + cfg.Host + ":" + cfg.Port + " · SN " + (s.Sn.Length > 0 ? s.Sn : Core.Format.Fmt.Dash) + "\n" + cd.Text
            + (SessionId != null ? " · 会话 " + SessionId + " 记录中" : "") + why;
        Health = !online ? (conn.State == ConnStatus.Disconnected ? DeviceHealth.Mute : DeviceHealth.Off)
            : estop.Active || err > 0 ? DeviceHealth.Err : alarms.Count > 0 ? DeviceHealth.Warn : DeviceHealth.Ok;
        foreach (var a in Alarms) a.RefreshReason();
    }

    private void SyncAlarms(IReadOnlyList<AlarmItem> items)
    {
        var ids = new HashSet<string>(items.Select(a => a.Id), StringComparer.Ordinal);
        for (int i = Alarms.Count - 1; i >= 0; i--) if (!ids.Contains(Alarms[i].Id)) Alarms.RemoveAt(i);
        for (int i = 0; i < items.Count; i++)
        {
            var a = items[i];
            int at = IndexOf(Alarms, a.Id);
            if (at < 0) Alarms.Insert(Math.Min(i, Alarms.Count), new AlarmVm(this, a));
            else
            {
                Alarms[at].Apply(a);
                if (at != i && i < Alarms.Count) Alarms.Move(at, i);
            }
        }
    }

    private static int IndexOf(ObservableCollection<AlarmVm> list, string id)
    {
        for (int i = 0; i < list.Count; i++) if (list[i].Id == id) return i;
        return -1;
    }

    /// <summary>事件只会在前面追加（最新在前）、从后面淘汰：按 Id 增量同步。</summary>
    private void SyncEvents(IReadOnlyList<EventItem> items)
    {
        if (items.Count == 0) { if (Events.Count > 0) Events.Clear(); return; }
        if (Events.Count > 0 && Events[0].Id == items[0].Id && Events.Count == items.Count) return;
        var have = new HashSet<string>(Events.Select(e => e.Id), StringComparer.Ordinal);
        var want = new HashSet<string>(items.Select(e => e.Id), StringComparer.Ordinal);
        for (int i = Events.Count - 1; i >= 0; i--) if (!want.Contains(Events[i].Id)) Events.RemoveAt(i);
        for (int i = 0; i < items.Count; i++)
            if (!have.Contains(items[i].Id)) Events.Insert(Math.Min(i, Events.Count), new EventVm(items[i]));
    }

    // ———————————————————— 报警（页面文字用 Core 的 AlarmEntry 形状） ————————————————————

    /// <summary>
    /// 当前报警（顺序同 <see cref="IDeviceSource.Alarms"/> = 样稿 dev.alarms），Core 的 <see cref="Core.Alarms.AlarmEntry"/> 形状：
    /// 页面的报警文字（Core.Alarms.AlarmTexts：总览分区头的报警小块、顶部报警条 AlarmStrip、设备页「报警与事件」）都读它。
    /// = 数据源报警簿（Core 的 AlarmBook，M4）的 Current：身份真机 = bootId/id、演示 = 'a1'；确认用 Id。
    /// </summary>
    public IReadOnlyList<Core.Alarms.AlarmEntry> AlarmList => Source.AlarmBook.Current;

    // ———————————————————— 程序进度 / 时间轴 ————————————————————

    /// <summary>
    /// 算进度 / 分段用的现场情况（同 DemoDevice.ProgressContextOf）：演示时钟或本地时间、连接（断线时冻结在最后数据时刻）、反应釜自己的状态、型号、气路；
    /// 等待进气的文字：演示「等待进气」，真机「等待气路」（排队等共用气路，§3.5）。
    /// </summary>
    public ProgressContext ContextFor(int n) => ContextFor(Source.Read(n), Fleet.Now);

    private ProgressContext ContextFor(ChannelReading rd, DateTime now)
    {
        var s = Source;
        // 真机：程序台账给的时刻是设备时钟（帧的 Ts、program.startedAt）—— 剩余时间、预计结束按设备时钟的「现在」算（IChannelHistory.Now：本机 UTC + HMI 时钟偏差）
        var t = s.IsDemo ? now : s.History.Now;
        return new ProgressContext(t, s.Conn.Online, s.DataAt, rd.Own, Model, s.GasOpen, s.IsDemo ? Progress.WaitGasWordDemo : StateMap.WaitGasWord);
    }

    /// <summary>
    /// 真机：此刻持有共用气路的反应釜（帧的 programs.gasHolder）；演示 / 不知道为 null。等待气路的说明「等待气路（RV3 占用中）」用
    /// （真机的 waitingGas 是排队等共用气路，不是样稿的进气口关闭，PORT-PLAN §3.5 / R6）。
    /// </summary>
    public int? GasHolder => Source is LiveDeviceSource l ? l.Latest?.Programs?.GasHolder : null;

    /// <summary>时间轴分段（样稿 PR.sim.segments）：没有程序（真机在 M5 的程序台账之前）为空。</summary>
    public IReadOnlyList<ProgramSegment> Segments(int n) => Source.Program(n) is { } p ? Progress.Segments(p, ContextFor(n)) : Array.Empty<ProgramSegment>();

    /// <summary>步骤条（样稿 ui.stepBar）：没有程序为空。</summary>
    public IReadOnlyList<StepBarSegment> StepBar(int n)
    {
        if (Source.Program(n) is not { } p) return Array.Empty<StepBarSegment>();
        return Progress.Bar(p, Progress.Of(p, ContextFor(n)), Source.Conn.Online);
    }

    // ———————————————————— IFleetDevice ————————————————————

    /// <summary>这个操作现在为什么不能做（样稿代码，'leaseRequest' …）；能做返回空串。</summary>
    public string Why(string action) => Actions.Why(action);

    /// <summary>第一条（计入计数的）错误报警或提醒所在的反应釜；设备级报警或没有时 0。</summary>
    public int AlarmRv(bool error)
    {
        foreach (var a in Source.Alarms)
        {
            if (EstopActive && a.Code == "ESTOP") continue;
            if ((a.Level == AlarmLevel.Err) != error) continue;
            return a.Ch ?? 0;
        }
        return 0;
    }

    public override string ToString() => Alias + " · " + ModelLabel;
}
