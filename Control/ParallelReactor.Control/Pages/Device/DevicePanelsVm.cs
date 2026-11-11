using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Events;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using CoreKeep = ParallelReactor.Control.Core.Text.Keep;

namespace ParallelReactor.Control.Pages;

// ============================================================================
// 设备页下排的三块面板（样稿 device.js resPanel / infoPanel / logPanel + css/device.css）：安静的列表 / 表格，
// 行高 36–40、line-faint 分隔、标签 t-3 14、数值 t-1 15。文字逐字照样稿；全部就地更新（列表按身份增删，R10）。
// ============================================================================

/// <summary>
/// 「共用资源」（device.js resPanel）：只读状态（设置在检查器 / 触屏）—— 急停、搅拌（共用：运行中 · 300 rpm + 「设置」跳到检查器的共用转速；
/// 独立：每釜一行，「设置」悬停 / 聚焦时出现）、气路（开 / 关，只能在触屏操作）、进气阀 SV（每一项不断开，放不下时在项与项之间换行）。
/// </summary>
public sealed partial class DeviceResVm : ObservableObject
{
    private readonly DevicePageVm _page;

    public DeviceResVm(DevicePageVm page)
    {
        _page = page;
        StirSetCommand = new RelayCommand(() =>
        {
            _page.Shell.Select(_page.K);
            _page.Shell.Inspector.RequestFocus("rpm");
        });
    }

    /// <summary>共用搅拌的「设置」：选中这台设备并把检查器滚到「共用搅拌」、聚焦转速框（设置只在检查器里改）。</summary>
    public ICommand StirSetCommand { get; }

    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private bool _isOff;
    /// <summary>内容区窄（≤ 350）：气路两列从标签处开始（device.css:261）。</summary>
    [ObservableProperty] private bool _narrow;

    // —— 急停 ——
    [ObservableProperty] private bool _estopOn;
    /// <summary>急停中：「急停中」/「断线前已急停」（600 t-1，前面红点）。</summary>
    [ObservableProperty] private string _estopWord = "";
    /// <summary>急停中第二行：「10:09:42 触发 · 复位只能在触屏」。</summary>
    [ObservableProperty] private string _estopCap = "";
    /// <summary>没有急停：「未触发」/「未知 · 断线」/「未知 · 尚未连接」。</summary>
    [ObservableProperty] private string _estopValue = "";
    [ObservableProperty] private bool _estopMuted;

    // —— 搅拌 ——
    [ObservableProperty] private bool _sharedStir = true;
    /// <summary>共用：没连上过（「—」）。</summary>
    [ObservableProperty] private bool _stirUnknown;
    [ObservableProperty] private StateKey _stirDot = StateKey.Stopped;
    /// <summary>「运行中」/「断线前运行中」/「已停止」。</summary>
    [ObservableProperty] private string _stirWord = "";
    [ObservableProperty] private bool _stirOn;
    [ObservableProperty] private string _stirRpm = "";
    /// <summary>停止时「设定 300 rpm」。</summary>
    [ObservableProperty] private string _stirNote = "";
    /// <summary>第二行「全釜共用 · 磁力搅拌」（提示「全釜共用 RV1–RV8 · 磁力搅拌」）。</summary>
    [ObservableProperty] private string _stirCap = "";
    [ObservableProperty] private string _stirCapTip = "";
    /// <summary>「设置」（在线才有）。</summary>
    [ObservableProperty] private bool _stirLink;
    /// <summary>独立搅拌的标题行：「各釜独立 · 顶置机械搅拌」。</summary>
    [ObservableProperty] private string _stirHead = "";
    /// <summary>独立搅拌：每釜一行。</summary>
    public ObservableCollection<ResStirRowVm> StirRows { get; } = new();

    // —— 气路 ——
    [ObservableProperty] private string _gasTip = "";
    public ObservableCollection<ResGasItemVm> GasItems { get; } = new();
    /// <summary>
    /// 进气阀：「SV1 开 · SV2 开 · SV3 关 · SV4 开」（每项经 Core 的 Keep.NoBreak 不断开 —— 插 U+2060，视图经 TextConverters.Joiners 换成当前字体的连接符；
    /// 项与项之间可以换行）。
    /// </summary>
    [ObservableProperty] private string _svText = "";
    [ObservableProperty] private string _svTip = "";

    internal void Refresh(DeviceVm d)
    {
        var src = d.Source;
        var m = d.Model;
        bool on = !d.IsOffline, known = d.EverOnline;
        IsOff = !on;
        Sub = on ? "只读 · 设置在检查器" : d.DataText;

        // 急停（device.js estopRow）
        var estop = src.Estop;
        EstopOn = estop.Active;
        if (estop.Active)
        {
            EstopWord = d.EstopText;
            EstopCap = (estop.At is { } at ? Fmt.Time(at) + " 触发 · " : "") + "复位只能在触屏";
        }
        EstopValue = !known ? "未知 · 尚未连接" : !on ? "未知 · 断线" : "未触发";
        EstopMuted = !known || !on;

        // 搅拌（device.js stirRows）
        var stir = src.Stir;
        SharedStir = m.StirMode == StirMode.Shared && stir != null;
        if (SharedStir)
        {
            StirUnknown = !known;
            StirOn = stir!.On;
            StirDot = !on ? StateKey.Offline : stir.On ? StateKey.Stir : StateKey.Stopped;
            StirWord = stir.On ? (on ? "运行中" : "断线前运行中") : "已停止";
            StirRpm = Fmt.Rpm(stir.Rpm);
            StirNote = stir.On ? "" : "设定 " + Fmt.Rpm(stir.Rpm) + " rpm";
            StirCap = "全釜共用 · " + m.StirKind;
            StirCapTip = "全釜共用 RV1–RV" + m.ChannelCount.ToString(CultureInfo.InvariantCulture) + " · " + m.StirKind;
            StirLink = on;
            if (StirRows.Count > 0) StirRows.Clear();
        }
        else
        {
            StirHead = "各釜独立 · " + m.StirKind;
            var want = m.Channels.Where(c => c.Installed).Select(c => c.N).ToList();
            for (int i = StirRows.Count - 1; i >= 0; i--) if (!want.Contains(StirRows[i].N)) StirRows.RemoveAt(i);
            for (int i = 0; i < want.Count; i++)
            {
                int at = -1;
                for (int j = 0; j < StirRows.Count; j++) if (StirRows[j].N == want[i]) { at = j; break; }
                if (at < 0) StirRows.Insert(i, new ResStirRowVm(_page, want[i]));
                else if (at != i) StirRows.Move(at, i);
            }
            foreach (var row in StirRows) row.Refresh(d, on, known);
        }

        // 气路（device.js gasRows）：进气 + 排空，开 600 t-1 / 关 t-3 / 没连上过「—」
        GasTip = "进气 " + m.GasInlets.Count.ToString(CultureInfo.InvariantCulture) + " 路" + (m.Vent is { } vent ? " · " + vent.Name : "") + " · 阀门只能在设备触屏上操作";
        var lines = m.GasInlets.Concat(m.Vent is { } v2 ? new[] { v2 } : Array.Empty<GasLine>()).ToList();
        while (GasItems.Count > lines.Count) GasItems.RemoveAt(GasItems.Count - 1);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i >= GasItems.Count) GasItems.Add(new ResGasItemVm());
            GasItems[i].Apply(lines[i].Name, src.GasOpen(lines[i].Key), on, known);
        }

        // 进气阀（device.js gasRows svs）：≤ 8 路逐个「SV1 开」；更多时「开 SV1–SV6、SV9–SV10 · 其余关」/「全部关闭」
        var inst = d.Reactors.Where(r => r.Installed).ToList();
        if (!known) { SvText = Fmt.Dash; SvTip = Fmt.Dash; }
        else if (inst.Count <= 8)
        {
            var items = inst.Select(r => "SV" + r.N.ToString(CultureInfo.InvariantCulture) + " " + (r.Valve ? "开" : "关")).ToList();
            SvTip = string.Join(" · ", items);
            SvText = string.Join(Sep, items.Select(CoreKeep.NoBreak));
        }
        else
        {
            var open = inst.Where(r => r.Valve).Select(r => r.N).ToList();
            var runs = open.Count > 0 ? ModelProfile.RangeText(open, "SV").Split('、') : Array.Empty<string>();
            SvTip = open.Count > 0 ? "开 " + string.Join("、", runs) + " · 其余关" : "全部关闭";
            SvText = open.Count > 0
                ? CoreKeep.NoBreak("开 " + runs[0]) + string.Concat(runs.Skip(1).Select(r => "、" + CoreKeep.NoBreak(r))) + Sep + CoreKeep.NoBreak("其余关")
                : CoreKeep.NoBreak("全部关闭");
        }
    }

    /// <summary>进气阀列表项之间的分隔：「不换行空格 · 空格」—— 只在「·」后面断行（device.js SEP）。</summary>
    private const string Sep = " · ";
}

/// <summary>独立搅拌（HT-PR4）每釜一行（device.js stirRows 的 rows）：「RV1 · ● 800 rpm」/「● 已停」/「—」+ 「设置」（在线才有，行悬停 / 聚焦时出现）。</summary>
public sealed partial class ResStirRowVm : ObservableObject
{
    public ResStirRowVm(DevicePageVm page, int n)
    {
        N = n;
        Label = "RV" + n.ToString(CultureInfo.InvariantCulture);
        SetCommand = new RelayCommand(() =>
        {
            page.Shell.Select(page.K, N);
            page.Shell.Inspector.RequestFocus("rpm");
        });
    }

    public int N { get; }
    public string Label { get; }
    /// <summary>选中这个反应釜并聚焦检查器里的转速框。</summary>
    public ICommand SetCommand { get; }

    [ObservableProperty] private bool _unknown;
    [ObservableProperty] private bool _on;
    [ObservableProperty] private StateKey _dot = StateKey.Stopped;
    [ObservableProperty] private string _rpm = "";
    [ObservableProperty] private bool _link;
    [ObservableProperty] private string _linkTip = "";

    internal void Refresh(DeviceVm d, bool online, bool known)
    {
        var r = d.Reactor(N);
        bool stirOn = r?.Extras?.StirOn ?? (r?.Rpm ?? 0) > 0;
        Unknown = !known;
        On = stirOn;
        Dot = !online ? StateKey.Offline : stirOn ? StateKey.Stir : StateKey.Stopped;
        Rpm = r?.Extras?.Rpm is double v ? Fmt.Rpm(v) : r?.Rpm is double v2 ? Fmt.Rpm(v2) : Fmt.Dash;
        Link = online;
        LinkTip = "在检查器里设置" + d.Alias + " · RV" + N.ToString(CultureInfo.InvariantCulture) + " 的转速";
    }
}

/// <summary>气路的一项：名称（t-2）+ 开（600 t-1）/ 关（t-3）/ —。</summary>
public sealed partial class ResGasItemVm : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _word = "";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _tip = "";

    internal void Apply(string name, bool open, bool online, bool known)
    {
        Name = name;
        Word = known ? (open ? "开" : "关") : Fmt.Dash;
        // 断线时「开」不再加粗（device.css .device-res.is-off .device-gi b.is-open）
        IsOpen = open && known && online;
        Tip = name + "：" + (known ? (open ? "开" : "关") : "未知") + (online ? "" : "（断线前）") + " · 阀门只能在触屏上操作";
    }
}

/// <summary>
/// 「设备信息」（device.js infoPanel / infoBody / commStat）：键值表（型号 + 「示例型号 · 参数为假设」、通道、釜、量程 + 「触屏操作」、地址、SN、版本）
/// + 「模块通讯」表（模块 / 数量 / 状态 / 最近成功）。标题栏右侧「模块在线 4/4」（有模块离线时前面一个红点）/ 断线时「模块状态未知」。
/// </summary>
public sealed partial class DeviceInfoVm : ObservableObject
{
    private readonly DevicePageVm _page;

    public DeviceInfoVm(DevicePageVm page) => _page = page;

    /// <summary>内容区窄（≤ 339）：「示例型号 · 参数为假设」只留图标（device.css:275）。</summary>
    [ObservableProperty] private bool _narrow;

    // —— 标题栏右侧 ——
    [ObservableProperty] private bool _online;
    [ObservableProperty] private string _statCount = "";
    [ObservableProperty] private string _statTotal = "";
    [ObservableProperty] private bool _statDot;
    [ObservableProperty] private string _statTip = "";

    // —— 键值 ——
    [ObservableProperty] private string _modelText = "";
    /// <summary>示例型号（参数是假设值）：型号旁写「示例型号 · 参数为假设」。</summary>
    [ObservableProperty] private bool _sample;
    [ObservableProperty] private string _sampleTip = "";
    [ObservableProperty] private string _channelsText = "";
    [ObservableProperty] private string _vesselText = "";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _sn = "";
    [ObservableProperty] private string _hmiVersion = "";
    [ObservableProperty] private string _apiVersion = "";
    [ObservableProperty] private string _versionTip = "";

    // —— 模块通讯 ——
    /// <summary>「每秒轮询」/ 断线时「数据停在 10:09:42」。</summary>
    [ObservableProperty] private string _commSub = "";
    public ObservableCollection<CommRowVm> Comm { get; } = new();

    /// <summary>模块通讯表的全部行：表头（<see cref="CommHeaderVm"/>）+ 各模块（同一个表格面板按全部行的内容定列宽）。</summary>
    public ObservableCollection<object> CommTable { get; } = new() { CommHeaderVm.Instance };

    /// <summary>模块通讯表的提示与示例说明的常量。</summary>
    public const string SampleNote = ModelProfiles.SampleNote;

    internal void Refresh(DeviceVm d)
    {
        var src = d.Source;
        var m = d.Model;
        bool on = !d.IsOffline;
        Online = on;
        var mods = src.Modules;
        int nOn = on ? mods.Count(c => c.Online) : 0;
        StatCount = nOn.ToString(CultureInfo.InvariantCulture);
        StatTotal = "/" + mods.Count.ToString(CultureInfo.InvariantCulture);
        StatDot = on && nOn < mods.Count;
        StatTip = !on ? "断线时无法得知各模块的状态" : nOn < mods.Count ? "有模块离线 · 见下方模块通讯" : "全部模块通讯正常";

        ModelText = m.Label;
        Sample = m.Hypothetical;
        SampleTip = ModelProfiles.SampleNote + (m.Note.Length > 0 ? " · " + m.Note : "");
        ChannelsText = m.ChannelCount.ToString(CultureInfo.InvariantCulture) + " 路 · 已装 " + m.InstalledText;
        VesselText = m.Vessel.Kind + " · " + m.Vessel.Note;
        RangeText = m.RangeNote;
        Address = d.Host + ":" + d.Port.ToString(CultureInfo.InvariantCulture);
        Sn = d.Sn.Length > 0 ? d.Sn : Fmt.Dash;
        HmiVersion = src.HmiVersion;
        ApiVersion = src.ApiVersion;
        VersionTip = "HMI " + HmiVersion + " · 接口 " + ApiVersion;
        CommSub = on ? "每秒轮询" : d.DataText;

        var now = d.Fleet.Now;
        while (Comm.Count > mods.Count) { Comm.RemoveAt(Comm.Count - 1); CommTable.RemoveAt(CommTable.Count - 1); }
        for (int i = 0; i < mods.Count; i++)
        {
            if (i >= Comm.Count) { var row = new CommRowVm(); Comm.Add(row); CommTable.Add(row); }
            Comm[i].Apply(mods[i], on, d.EverOnline, now);
        }
    }
}

/// <summary>模块通讯表的表头（模块 · 数量 · 状态 · 最近成功）。</summary>
public sealed class CommHeaderVm
{
    public static readonly CommHeaderVm Instance = new();
}

/// <summary>模块通讯表的一行（device.js infoBody rows）：名称 · 「× 1」· 状态（在线 / 离线（红点，600 t-1）/ 未知 / 未连接（空心圈））· 最近成功「10:11:59 刚刚」。</summary>
public sealed partial class CommRowVm : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _count = "";
    /// <summary>「在线」/「离线」/「未知」/「未连接」。</summary>
    [ObservableProperty] private string _state = "";
    /// <summary>在线不画点；离线红点；未知 / 未连接空心圈。</summary>
    [ObservableProperty] private bool _showDot;
    [ObservableProperty] private StateKey _dot = StateKey.Offline;
    [ObservableProperty] private bool _isErr;
    [ObservableProperty] private bool _isOff;
    [ObservableProperty] private string _last = Fmt.Dash;
    [ObservableProperty] private string _ago = "";

    internal void Apply(ModuleComm c, bool online, bool ever, DateTime now)
    {
        Name = c.Name;
        Count = "× " + c.Count.ToString(CultureInfo.InvariantCulture);
        bool ok = online && c.Online;
        (string kind, string text) = !ever ? ("off", "未连接") : !online ? ("off", "未知") : ok ? ("ok", "在线") : ("err", "离线");
        State = text;
        ShowDot = kind != "ok";
        Dot = kind == "err" ? StateKey.Alarm : StateKey.Offline;
        IsErr = kind == "err";
        IsOff = kind == "off";
        Last = c.LastOk is { } t ? Fmt.Time(t) : Fmt.Dash;
        Ago = c.LastOk is { } t2 ? Fmt.Ago((now - t2).TotalMilliseconds) : "";
    }
}

/// <summary>
/// 「报警与事件」（device.js logPanel / logBody / unackSub）：当前报警（未确认在前、错误报警在前、新的在前；错误报警 = 浅红条，提醒 = 中性 + ⚠，已确认 = 灰；
/// 「确认」= 停止类，连接正常就可用 —— 这台设备报警屏幕上唯一的「确认」）+ 最近事件（「全部 / 报警/提醒 / 操作」分段筛选，最多 60 条）。
/// </summary>
public sealed partial class DeviceLogVm : ObservableObject
{
    private readonly DevicePageVm _page;
    private int _eventVersion = -1;
    private EventFilter _shownFilter = (EventFilter)(-1);
    /// <summary>上次同步的事件簿（演示数据重建 = 换了数据源、换了簿：事件 Key 会重复，整份重新同步）。</summary>
    private EventBook? _eventBook;

    public DeviceLogVm(DevicePageVm page)
    {
        _page = page;
        SegAll = new SegOption { Value = "all", Tip = EventTexts.SegTip(EventFilter.All) };
        SegAlarm = new SegOption { Value = "alarm", Tip = EventTexts.SegTip(EventFilter.Alarm) };
        SegOp = new SegOption { Value = "op", Tip = EventTexts.SegTip(EventFilter.Op) };
        SegOptions = new[] { SegAll, SegAlarm, SegOp };
    }

    /// <summary>面板矮（内容 ≤ 259）：当前报警与最近事件整块一起滚动。</summary>
    [ObservableProperty] private bool _logShort;
    /// <summary>面板内容区的高（标题栏以下）：当前报警最多占 58 %，事件列表铺满剩下的。</summary>
    [ObservableProperty] private double _bodyHeight;
    /// <summary>窄工作区（页签面板）：分段每段左右 9（宽工作区 10）。</summary>
    [ObservableProperty] private bool _tabbedLayout;

    // —— 标题栏副标题：「1 条未确认」（有未确认的错误报警时前面一个红点；页签面板里不加：页签上已经有红点） ——
    [ObservableProperty] private string _unackedText = "";
    [ObservableProperty] private bool _unackedDot;

    // —— 当前报警 ——
    [ObservableProperty] private string _head = "当前报警";
    [ObservableProperty] private string _headSub = "";
    [ObservableProperty] private bool _hasAlarms;
    public ObservableCollection<LogAlarmVm> Alarms { get; } = new();

    // —— 最近事件 ——
    /// <summary>筛选：'all' / 'alarm' / 'op'（SegControl 的 Value，双向）。</summary>
    [ObservableProperty] private string _filterValue = "all";
    public SegOption SegAll { get; }
    public SegOption SegAlarm { get; }
    public SegOption SegOp { get; }
    /// <summary>
    /// 事件筛选分段的三项（SegControl 的 OptionsSource；实例不变，计数就地改）：「报警与事件」的模板不管什么时候实例化
    /// （宽工作区的面板、窄工作区的页签、自动切换、跨过 1100 改窗口尺寸）都绑定到这一组。
    /// </summary>
    public IReadOnlyList<SegOption> SegOptions { get; }
    public ObservableCollection<LogEventVm> Events { get; } = new();
    [ObservableProperty] private bool _hasEvents;
    [ObservableProperty] private string _emptyText = "";

    public EventFilter Filter => FilterValue switch { "alarm" => EventFilter.Alarm, "op" => EventFilter.Op, _ => EventFilter.All };

    partial void OnFilterValueChanged(string value)
    {
        if (_page.D is { } d) RefreshEvents(d, force: true);
        FilterPicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>换了筛选（视图把焦点留在点过的分段上）。</summary>
    public event EventHandler? FilterPicked;

    /// <summary>新打开（演示参数换了）：筛选回到「全部」。</summary>
    internal void ResetFilter()
    {
        if (FilterValue != "all") FilterValue = "all";
    }

    internal void Refresh(DeviceVm d)
    {
        var list = d.AlarmList;
        UnackedText = AlarmTexts.UnackedText(list);
        UnackedDot = AlarmTexts.UnackedDot(list);
        Head = AlarmTexts.LogHead(list);
        HeadSub = AlarmTexts.LogSub(list);
        var ordered = AlarmTexts.LogOrder(list);
        HasAlarms = ordered.Count > 0;
        // 按身份就地增删 / 移动（确认之后卡片换样式、排到后面，不重建）
        for (int i = Alarms.Count - 1; i >= 0; i--) if (!ordered.Any(a => a.Key == Alarms[i].Key)) Alarms.RemoveAt(i);
        for (int i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];
            int at = -1;
            for (int j = 0; j < Alarms.Count; j++) if (Alarms[j].Key == a.Key) { at = j; break; }
            if (at < 0) Alarms.Insert(i, new LogAlarmVm(d, a));
            else if (at != i) Alarms.Move(at, i);
        }
        foreach (var vm in Alarms) vm.Refresh(d, ordered.First(a => a.Key == vm.Key));
        RefreshEvents(d, force: false);
    }

    private void RefreshEvents(DeviceVm d, bool force)
    {
        var book = d.Source.EventBook;
        var f = Filter;
        bool newBook = !ReferenceEquals(book, _eventBook);
        if (!force && !newBook && book.Version == _eventVersion && f == _shownFilter) return;
        // 换了数据源（演示数据重建）：新簿的 Key（演示 'e13'）会与旧的重复、行又不可变 —— 先清掉，不留上一份数据的事件
        if (newBook) { _eventBook = book; if (Events.Count > 0) Events.Clear(); }
        _eventVersion = book.Version;
        _shownFilter = f;
        var c = book.Counts;
        SegAll.Label = EventTexts.SegLabel(EventFilter.All, c);
        SegAlarm.Label = EventTexts.SegLabel(EventFilter.Alarm, c);
        SegOp.Label = EventTexts.SegLabel(EventFilter.Op, c);
        var list = book.Filter(f, EventBook.ListMax);
        HasEvents = list.Count > 0;
        EmptyText = EventTexts.Empty(f);
        for (int i = Events.Count - 1; i >= 0; i--) if (!list.Any(e => e.Key == Events[i].Key)) Events.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
        {
            int at = -1;
            for (int j = i; j < Events.Count; j++) if (Events[j].Key == list[i].Key) { at = j; break; }
            if (at < 0) Events.Insert(i, new LogEventVm(list[i]));
            else if (at != i) Events.Move(at, i);
        }
    }
}

/// <summary>
/// 「报警与事件」里的一张报警卡（device.js alarmHTML）：图标（错误 alert 红 / 提醒 ⚠ t-1 / 已确认 t-3）· 文字（15/600，可以折行，断行保护）·
/// 「10:09:42 · 报警 · 未确认」（悬停全文带 RV 与代码）· 「确认」（未确认才有；原因 = 守卫，可用时提示「确认报警不需要控制权，连接正常就可用」）。
/// </summary>
public sealed partial class LogAlarmVm : ObservableObject
{
    private readonly DeviceVm _d;

    public LogAlarmVm(DeviceVm d, AlarmEntry a)
    {
        _d = d;
        Key = a.Key;
        Id = a.Id;
        AckCommand = new AsyncRelayCommand(() => _d.Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: Id)));
    }

    public string Key { get; }
    public string Id { get; }
    public ICommand AckCommand { get; }

    [ObservableProperty] private bool _isErr;
    [ObservableProperty] private bool _acked;
    [ObservableProperty] private string _icon = "warn";
    /// <summary>原文（视图经 TextConverters.Keep 加断行保护：数字与单位、「3.12 MPa ≥ 3.00 MPa」「已关闭 SV11」不拆开）。</summary>
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _textTip = "";
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private string _metaFull = "";
    [ObservableProperty] private string? _ackReason;
    [ObservableProperty] private string? _ackTip;

    internal void Refresh(DeviceVm d, AlarmEntry a)
    {
        IsErr = a.IsErr;
        Acked = a.Acked;
        Icon = a.IsErr ? "alert" : "warn";
        Text = a.Text;
        TextTip = a.Text;
        Meta = a.Meta;
        MetaFull = a.MetaFull;
        var why = d.Actions.Why(DeviceAction.AlarmAck, new ActionPayload(Id: a.Id));
        AckReason = why.Length == 0 ? null : why;
        AckTip = why.Length == 0 ? AlarmTexts.AckTip : null;
    }
}

/// <summary>
/// 最近事件的一行（device.js logBody 的 .device-ev）：时刻（13 t-3）· 图标（错误 alert 红 / 提醒 ⚠ t-1 / 操作 人形 t-3 / 其它灰点）·
/// 「RV3」（14/600 t-2；整机 400 t-3）· 文字（去掉开头的「RVn」；报警 / 提醒 500 t-1，错误报警可以折成两行，断行保护）。不可变：事件不会改。
/// </summary>
public sealed class LogEventVm
{
    public LogEventVm(EventEntry e)
    {
        Key = e.Key;
        Time = e.AtText;
        Icon = e.IconName;
        HasIcon = e.IconName.Length > 0;
        Rv = e.RvText;
        IsDevice = e.IsDevice;
        IsErr = e.Level == EventLevel.Err;
        IsWarn = e.Level == EventLevel.Warn;
        Text = IsErr ? CoreKeep.Apply(e.ShortText) : e.ShortText;
        Tip = e.Text;
    }

    public string Key { get; }
    public string Time { get; }
    public string Icon { get; }
    public bool HasIcon { get; }
    public string Rv { get; }
    public bool IsDevice { get; }
    public bool IsErr { get; }
    public bool IsWarn { get; }
    /// <summary>显示的文字：错误报警经 Core 的 Keep.Apply 加断行保护（插 U+2060，视图经 TextConverters.Joiners 换成当前字体的连接符），其余是原文（单行省略）。</summary>
    public string Text { get; }
    public string Tip { get; }
}
