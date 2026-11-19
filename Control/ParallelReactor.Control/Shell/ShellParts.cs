using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Activity;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Shell;

// 融合版 F2 的外框各部分（样稿 main-fusion.html：.titlebar 深蓝条、.menubar 菜单栏 + 运行胶囊 + 报警摘要 + 本机、.tbar 工具条、#pDev 设备面板、.status 状态栏）。
// 都只读 ShellVm（Fleet 数据 + Nav 路由 + Layout 布局），ShellVm.RefreshChrome 时就地刷新文字和标记，不重建集合。

/// <summary>菜单栏的一个页面（样稿 .mi：24 px 细体，当前页品牌蓝常规字重；「报警」带未确认数徽标）。</summary>
public sealed partial class MenuPageVm : ObservableObject
{
    public MenuPageVm(AppPageInfo info, Action<AppPage> go)
    {
        Info = info;
        GoCommand = new RelayCommand(() => go(info.Page));
    }

    public AppPageInfo Info { get; }
    public string Title => Info.Title;
    public ICommand GoCommand { get; }
    /// <summary>当前页面。</summary>
    [ObservableProperty] private bool _isOn;
    /// <summary>徽标数字（「报警」= 未确认的报警条数）；null = 不画。</summary>
    [ObservableProperty] private string? _badge;
}

/// <summary>
/// 菜单栏（样稿 .menubar）：六个页面 + 右侧运行胶囊、报警摘要按钮、本机。「报警」的徽标 = 全部设备未确认的报警条数（样稿 unacked().length，提醒也算）。
/// </summary>
public sealed partial class MenubarVm : ObservableObject
{
    private readonly ShellVm _shell;

    public MenubarVm(ShellVm shell)
    {
        _shell = shell;
        Pages = AppPages.Menu.Select(i => new MenuPageVm(i, p => shell.Go(p))).ToList();
    }

    public IReadOnlyList<MenuPageVm> Pages { get; }

    /// <summary>未确认的报警条数（菜单栏「报警」徽标；0 = 不画）。</summary>
    [ObservableProperty] private int _unacked;
    [ObservableProperty] private string _clientName = "";

    /// <summary>未确认的报警条数（全部设备，计入计数的报警）。</summary>
    public static int UnackedOf(IEnumerable<DeviceVm> devices) => devices.Sum(d => d.UnackedAlarms);

    internal void Refresh()
    {
        Unacked = UnackedOf(_shell.Fleet.Devices);
        ClientName = _shell.Fleet.ClientName;
        var page = _shell.Nav.Page;
        foreach (var p in Pages)
        {
            p.IsOn = p.Info.Page == page;
            p.Badge = p.Info.Page == AppPage.Alarms && Unacked > 0 ? Unacked.ToString(CultureInfo.InvariantCulture) : null;
        }
    }
}

/// <summary>
/// 运行胶囊（样稿 .cap：黑底 46 高，设备名 + 启动 / 全部停止 / 记录三个圆钮 + 红色「急停」）。命令语义沿用 pc-v3：
/// <list type="bullet">
/// <item>启动 = 这台设备「全部运行」（DeviceActions；真机先列清单确认 runall:k，同检查器）。不选设备时带原因「先选一台设备」。</item>
/// <item>全部停止 = 全部停止对话框（stopall，多选设备，不需要控制权）。</item>
/// <item>记录 = 这台设备开始记录（会话 sessionStart）；已在记录时带原因（结束在检查器 / 记录页）。</item>
/// <item>急停 = 急停对话框（estop，多选设备）。有设备急停中时外加一圈 r-line。</item>
/// </list>
/// 全部停止 / 急停在没有已连接的设备时带原因（同 pc-v3 的全局停止格）。
/// </summary>
public sealed partial class CapsuleVm : ObservableObject
{
    private readonly ShellVm _shell;

    public CapsuleVm(ShellVm shell)
    {
        _shell = shell;
        StartCommand = new RelayCommand(Start);
        StopAllCommand = new RelayCommand(() => shell.OpenDialog("stopall"));
        RecordCommand = new RelayCommand(() => { if (Device is { } d) _ = d.Actions.RunAsync(DeviceAction.SessionStart); });
        EstopCommand = new RelayCommand(() => shell.OpenDialog("estop"));
    }

    public ICommand StartCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand RecordCommand { get; }
    public ICommand EstopCommand { get; }

    public const string StopAllTip = "全部停止：中止所有程序，停加热和搅拌，关全部阀门 · 不放空 · 不需要控制权";
    public const string EstopTip = "急停（远程停机）：切断并锁定加热和搅拌，关全部阀门 · 不放空 · 复位只能在触屏 · 不能代替设备上的急停按钮";

    /// <summary>胶囊左边的名字：选中的设备别名，不选设备时「全部设备」。</summary>
    [ObservableProperty] private string _label = "全部设备";
    [ObservableProperty] private string? _startReason;
    [ObservableProperty] private string _startTip = "";
    [ObservableProperty] private string? _recordReason;
    [ObservableProperty] private string _recordTip = "开始记录";
    [ObservableProperty] private bool _recording;
    /// <summary>没有已连接的设备时全部停止 / 急停的原因。</summary>
    [ObservableProperty] private string? _stopReason;
    [ObservableProperty] private bool _anyEstop;

    private DeviceVm? Device => _shell.Nav.DeviceK > 0 ? _shell.Fleet.Device(_shell.Nav.DeviceK) : null;

    /// <summary>
    /// 启动（融合版 F3，样稿胶囊的绿色按钮「启动所选通道的程序」）：选中的那一路能启动就启动它；否则「全部运行」（真机先打开清单对话框，
    /// 列出能运行的反应釜；演示直接运行）；一路也启动不了时提示原因。
    /// </summary>
    private void Start()
    {
        if (Device is not { } d) return;
        int n = _shell.Nav.ChannelOf(d.K);
        if (d.Reactor(n) is { Installed: true } r && !r.IsRunning && d.Actions.Why(DeviceAction.Start, ActionPayload.Ch(n)).Length == 0)
        {
            _ = d.Actions.RunAsync(DeviceAction.Start, new ActionPayload(N: n, Sp: r.SpC));
            return;
        }
        var why = d.Actions.Why(DeviceAction.RunAll);
        if (why.Length > 0) { _shell.Toast(d.Alias + "：没有可以启动的通道 · " + why, Services.ToastKind.Info); return; }
        if (d.Source is Services.LiveDeviceSource && _shell.Dialogs.IsRegistered("runall")) _shell.OpenDialog("runall", d.K.ToString(CultureInfo.InvariantCulture));
        else _ = d.Actions.RunAsync(DeviceAction.RunAll);
    }

    internal void Refresh()
    {
        var d = Device;
        Label = d?.Alias ?? "全部设备";
        if (d == null)
        {
            StartReason = "启动程序：先选一台设备";
            StartTip = StartReason;
            RecordReason = "开始记录：先选一台设备";
            Recording = false;
        }
        else
        {
            // 可用：本机持有控制权、没在急停、连着（维护模式没有接口，触屏上进入时设备会拒绝启动）
            StartReason = d.IsOffline ? d.Alias + " " + d.ConnText
                : d.EstopActive ? d.Alias + "急停中 · 复位只能在触屏上"
                : d.Lease != LeaseState.Remote ? d.Alias + "为本地只读 · 先申请控制权" : null;
            int n = _shell.Nav.ChannelOf(d.K);
            StartTip = d.Reactor(n) is { Installed: true, IsRunning: false } ? "启动所选通道的程序（RV" + n + "）" : "启动" + d.Alias + "上可以运行的通道（全部运行）";
            Recording = d.SessionId != null;
            RecordReason = Recording ? d.Alias + "正在记录 · 会话 " + d.SessionId : Nz(d.Actions.Why(DeviceAction.SessionStart));
            RecordTip = "开始记录（" + d.Alias + "）";
        }
        var ds = _shell.Fleet.Devices;
        StopReason = ds.Any(x => x.CanStop) ? null : "没有已连接的设备";
        AnyEstop = ds.Any(x => x.EstopActive);
    }

    private static string? Nz(string s) => s.Length == 0 ? null : s;
}

/// <summary>报警列表弹出层的一行（样稿 #almPop .arow）：设备 · RV、时间、报警名、说明、确认状态 + 定位 / 确认。</summary>
public sealed partial class AlarmRowVm : ObservableObject
{
    public AlarmRowVm(DeviceVm d, AlarmVm a, Action<int, int?> locate)
    {
        D = d;
        A = a;
        LocateCommand = new RelayCommand(() => locate(d.K, a.Ch));
    }

    public DeviceVm D { get; }
    public AlarmVm A { get; }
    public ICommand LocateCommand { get; }
    public ICommand AckCommand => A.AckCommand;

    [ObservableProperty] private string _where = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _more = "";
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _isErr;
    [ObservableProperty] private bool _acked;
    [ObservableProperty] private string _time = "";
    [ObservableProperty] private string? _ackReason;

    internal void Refresh()
    {
        var a = A;
        Where = D.Alias + " · " + (a.Ch is int n ? "RV" + n.ToString(CultureInfo.InvariantCulture) : "整机");
        // 报警文字「RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11」：去掉开头的 RVn，第一段是名字，其余是说明（样稿 r2 / r3）
        var text = a.Text;
        if (a.Ch is int c && text.StartsWith("RV" + c.ToString(CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal)) text = text[(text.IndexOf(' ') + 1)..];
        int dot = text.IndexOf(" · ", StringComparison.Ordinal);
        Name = dot > 0 ? text[..dot] : text;
        More = dot > 0 ? text[(dot + 3)..] : "";
        HasMore = More.Length > 0;
        IsErr = a.IsErr;
        Acked = a.Acked;
        Time = a.AtText;
        AckReason = a.AckReason;
    }
}

/// <summary>
/// 报警摘要按钮 + 报警列表（样稿 almBtn / renderAlarm）：按钮写「报警 n · 提醒 m」和「未确认 k」（有未确认时红字 600、铃铛上红色气泡）；
/// 点开是全部设备的报警列表（筛选：全部 / 未确认 / 报警 / 提醒；每条定位 / 确认；底部全部确认、报警历史 → 报警页）。点外面或 Esc 关掉。
/// </summary>
public sealed partial class AlarmSummaryVm : ObservableObject
{
    private readonly ShellVm _shell;
    private readonly Dictionary<AlarmVm, AlarmRowVm> _rows = new();

    public AlarmSummaryVm(ShellVm shell)
    {
        _shell = shell;
        ToggleCommand = new RelayCommand(() => IsOpen = !IsOpen);
        CloseCommand = new RelayCommand(() => IsOpen = false);
        FilterCommand = new RelayCommand<string>(f => { if (f is { Length: > 0 }) Filter = f; });
        AckAllCommand = new RelayCommand(AckAll);
        HistoryCommand = new RelayCommand(() => { IsOpen = false; shell.Go(AppPage.Alarms); });
    }

    public ICommand ToggleCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand FilterCommand { get; }
    public ICommand AckAllCommand { get; }
    public ICommand HistoryCommand { get; }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _err;
    [ObservableProperty] private int _warn;
    [ObservableProperty] private int _unacked;
    /// <summary>「报警 2 · 提醒 1」。</summary>
    [ObservableProperty] private string _countText = "";
    /// <summary>「未确认 3」/「已全部确认」。</summary>
    [ObservableProperty] private string _ackText = "";
    [ObservableProperty] private string _tip = "";
    [ObservableProperty] private bool _hasUnacked;
    /// <summary>all / ua / hi / lo（样稿 ALF）。</summary>
    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private int _active;
    [ObservableProperty] private string _ackAllText = "全部确认";
    [ObservableProperty] private bool _isEmpty;

    public ObservableCollection<AlarmRowVm> Rows { get; } = new();

    partial void OnIsOpenChanged(bool value) { if (value) Refresh(); }
    partial void OnFilterChanged(string value) => SyncRows();

    private void AckAll()
    {
        foreach (var d in _shell.Fleet.Devices)
            foreach (var a in d.Alarms.Where(a => !a.Acked).ToList())
                if (a.AckReason == null && a.AckCommand.CanExecute(null)) a.AckCommand.Execute(null);
    }

    internal void Refresh()
    {
        var ds = _shell.Fleet.Devices;
        Err = ds.Sum(d => d.ErrAlarms);
        Warn = ds.Sum(d => d.WarnAlarms);
        Unacked = MenubarVm.UnackedOf(ds);
        HasUnacked = Unacked > 0;
        CountText = "报警 " + Err.ToString(CultureInfo.InvariantCulture) + " · 提醒 " + Warn.ToString(CultureInfo.InvariantCulture);
        AckText = HasUnacked ? "未确认 " + Unacked.ToString(CultureInfo.InvariantCulture) : "已全部确认";
        Tip = CountText + " · " + AckText;
        AckAllText = HasUnacked ? "全部确认（" + Unacked.ToString(CultureInfo.InvariantCulture) + "）" : "全部确认";
        if (IsOpen) SyncRows();
    }

    /// <summary>列表：先未确认、再报警级、再最新（样稿 sort）；就地增删，行对象按报警身份复用。</summary>
    private void SyncRows()
    {
        var all = new List<AlarmRowVm>();
        foreach (var d in _shell.Fleet.Devices)
            foreach (var a in d.Alarms)
            {
                if (d.EstopActive && a.Code == "ESTOP") continue;
                if (!_rows.TryGetValue(a, out var r)) _rows[a] = r = new AlarmRowVm(d, a, Locate);
                r.Refresh();
                all.Add(r);
            }
        foreach (var gone in _rows.Keys.Where(k => !all.Any(r => ReferenceEquals(r.A, k))).ToList()) _rows.Remove(gone);
        Active = all.Count;
        var list = all.Where(r => Filter switch { "ua" => !r.Acked, "hi" => r.IsErr, "lo" => !r.IsErr, _ => true })
            .OrderBy(r => r.Acked).ThenBy(r => r.IsErr ? 0 : 1).ThenByDescending(r => r.A.At).ToList();
        for (int i = Rows.Count - 1; i >= 0; i--) if (!list.Contains(Rows[i])) Rows.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
        {
            int at = Rows.IndexOf(list[i]);
            if (at < 0) Rows.Insert(i, list[i]);
            else if (at != i) Rows.Move(at, i);
        }
        IsEmpty = Rows.Count == 0;
    }

    /// <summary>定位（样稿 locate）：关掉列表，主页选中这台设备的这一路。</summary>
    private void Locate(int k, int? n)
    {
        IsOpen = false;
        _shell.Go(AppPage.Home);
        if (n is int nn) _shell.SelectChannel(k, nn); else _shell.SelectDevice(k);
    }
}

/// <summary>工具条设备下拉的一项（K = 0：总览 · 全部设备 / 全部设备）。</summary>
public sealed record DeviceOption(int K, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 工具条（样稿 renderRibbon）：左「设备：」下拉 + 量程（窄屏不写）+ 竖线 +「控制权：」小块与按钮；右边是当前页面的按钮（<see cref="Pages.PageVm.Tools"/>）。
/// 控制权：远程控制中（a-soft 小块）+ 释放 · 申请中 · n s（+ 取消申请）· 宽限（+ 释放）· 本地只读 / 其他客户端（锁）+ 主要按钮「申请控制」（打开控制权对话框）· 断线写连接状态。
/// </summary>
public sealed partial class RibbonVm : ObservableObject
{
    private readonly ShellVm _shell;
    private bool _syncing;

    public RibbonVm(ShellVm shell)
    {
        _shell = shell;
        ReleaseCommand = new RelayCommand(() => Run(DeviceAction.LeaseRelease));
        CancelCommand = new RelayCommand(() => Run(DeviceAction.LeaseCancel));
        RequestCommand = new RelayCommand(() =>
        {
            if (Device is not { } d) return;
            if (shell.Dialogs.IsRegistered("lease")) shell.OpenDialog("lease", d.K.ToString(CultureInfo.InvariantCulture));
            else Run(DeviceAction.LeaseRequest);
        });
    }

    public ICommand ReleaseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RequestCommand { get; }

    public ObservableCollection<DeviceOption> Options { get; } = new();
    [ObservableProperty] private DeviceOption? _selected;
    [ObservableProperty] private bool _hasDevice;
    /// <summary>写量程（选了设备、宽屏）。</summary>
    [ObservableProperty] private bool _showModel;
    /// <summary>「8 通道 · 量程 ≤ 200 °C · ≤ 4.14 MPa」（窄屏不写）。</summary>
    [ObservableProperty] private string _modelText = "";
    [ObservableProperty] private bool _isRemote;
    [ObservableProperty] private string _leaseText = "";
    [ObservableProperty] private string _leaseTip = "";
    [ObservableProperty] private string _leaseIcon = "f-lock";
    /// <summary>行内按钮：release / cancel / request / null。</summary>
    [ObservableProperty] private string? _button;
    [ObservableProperty] private string? _buttonReason;
    [ObservableProperty] private string _buttonTip = "";

    public bool ShowRelease => Button == "release";
    public bool ShowCancel => Button == "cancel";
    public bool ShowRequest => Button == "request";
    partial void OnButtonChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowRelease));
        OnPropertyChanged(nameof(ShowCancel));
        OnPropertyChanged(nameof(ShowRequest));
    }

    /// <summary>页面的按钮组（当前页面的 <see cref="Pages.PageVm.Tools"/>）。</summary>
    [ObservableProperty] private IReadOnlyList<RibbonGroup> _tools = Array.Empty<RibbonGroup>();
    /// <summary>画工具条用的组（第一组前面没有竖线）。</summary>
    [ObservableProperty] private IReadOnlyList<RibbonGroupView> _groups = Array.Empty<RibbonGroupView>();

    partial void OnToolsChanged(IReadOnlyList<RibbonGroup> value) =>
        Groups = value.Select((g, i) => new RibbonGroupView(g, i == 0)).ToList();

    private DeviceVm? Device => _shell.Nav.DeviceK > 0 ? _shell.Fleet.Device(_shell.Nav.DeviceK) : null;

    partial void OnSelectedChanged(DeviceOption? value)
    {
        if (_syncing || value == null) return;
        _shell.SelectDevice(value.K);
    }

    private void Run(DeviceAction a)
    {
        if (Device is { } d) _ = d.Actions.RunAsync(a);
    }

    /// <summary>「≤ 200 °C · ≤ 4.14 MPa」（样稿写 °C，Core 的 RangeNote 是 ℃；压力按设置的单位：≤ 41.4 bar / ≤ 600 psi）。</summary>
    public static string RangeText(ModelProfile m) =>
        "≤ " + JsNumber.ToString(m.Limits.TempMaxC) + " °C · ≤ " + PressureDisplay.LimitU(m.Limits.PressureMaxMPa);

    internal void Refresh()
    {
        var scope = _shell.Nav.Scope;
        var ds = _shell.Fleet.Devices;
        var want = new List<DeviceOption>();
        if (scope != PageScope.One) want.Add(new DeviceOption(0, scope == PageScope.Main ? "总览 · 全部设备" : "全部设备"));
        foreach (var x in ds) want.Add(new DeviceOption(x.K, x.Alias + " · " + x.Model.Name));
        _syncing = true;
        try
        {
            if (!want.SequenceEqual(Options))
            {
                Options.Clear();
                foreach (var o in want) Options.Add(o);
            }
            Selected = Options.FirstOrDefault(o => o.K == _shell.Nav.DeviceK);
        }
        finally { _syncing = false; }

        var d = Device;
        HasDevice = d != null;
        ShowModel = d != null && _shell.Layout.IsWide;
        if (d == null) { Button = null; return; }
        ModelText = d.ChannelCount.ToString(CultureInfo.InvariantCulture) + " 通道 · 量程 " + RangeText(d.Model);
        IsRemote = d.Lease == LeaseState.Remote && !d.IsOffline;
        string? btn = null;
        if (d.IsOffline)
        {
            LeaseText = d.ConnShort.Length > 0 ? d.ConnShort : d.ConnText;
            LeaseIcon = "f-lock";
            LeaseTip = d.ConnText;
        }
        else
        {
            switch (d.Lease)
            {
                case LeaseState.Remote:
                    LeaseText = "远程控制中"; LeaseIcon = "f-plug"; LeaseTip = "本机正在远程控制" + d.Alias; btn = "release"; break;
                case LeaseState.Requested:
                    LeaseText = d.LeaseShort; LeaseIcon = "f-plug"; LeaseTip = "等待设备触屏允许 · " + d.LeaseText; btn = "cancel"; break;
                case LeaseState.Lost:
                    LeaseText = d.LeaseShort; LeaseIcon = "f-plug"; LeaseTip = d.LeaseText; btn = "release"; break;
                case LeaseState.Other:
                    LeaseText = d.LeaseShort; LeaseIcon = "f-lock"; LeaseTip = d.LeaseText; btn = "request"; break;
                default:
                    LeaseText = "本地只读"; LeaseIcon = "f-lock"; LeaseTip = "本机只能查看" + d.Alias; btn = "request"; break;
            }
        }
        Button = btn;
        (ButtonReason, ButtonTip) = btn switch
        {
            "release" => (Nz(d.Actions.Why(DeviceAction.LeaseRelease)), "释放" + d.Alias + "的控制权 · 回到本地只读"),
            "cancel" => (Nz(d.Actions.Why(DeviceAction.LeaseCancel)), "取消控制权申请"),
            "request" => (Nz(d.Actions.Why(DeviceAction.LeaseRequest)), "向" + d.Alias + "申请控制权 · 需在设备触屏上允许"),
            _ => ((string?)null, ""),
        };
    }

    internal void SetTools(IReadOnlyList<RibbonGroup> tools) { if (!ReferenceEquals(tools, Tools)) Tools = tools; }

    private static string? Nz(string s) => s.Length == 0 ? null : s;
}

/// <summary>工具条上画的一组（<see cref="First"/>：第一组，前面不画竖线）。</summary>
public sealed record RibbonGroupView(RibbonGroup Group, bool First)
{
    public IReadOnlyList<RibbonTool> Tools => Group.Tools;
    public bool Compact => Group.CompactWhenNarrow;
}

/// <summary>设备信息（设备面板底部，样稿 devInfo）的一行。<see cref="Sub"/> = 值下面一行灰字（样稿气源下的「接什么气由工艺决定」）。</summary>
public sealed record InfoRow(string Key, string Value, bool Dot = false, string? Sub = null)
{
    public bool HasSub => !string.IsNullOrEmpty(Sub);
}

/// <summary>
/// 设备面板的一行（样稿 .dv）：左侧 5 px 状态色条（报警 / 急停红、运行绿、其余灰）、别名（20 px 细体，选中时品牌蓝常规字重）+ 型号 + 控制权（远程 = a-soft 小块，否则锁），
/// 第二行「运行 6/8 · 报警 1 · 记录中」（断线：「连接中断 · 数据停在 …」；急停：「急停」，红 600），第三行「最早 hh:mm · 最晚 hh:mm 结束」。
/// K = 0 是第一行「总览 / 全部设备」（网格图标 + 17 px「总览」+「3 台 · 运行 20/28」）。
/// </summary>
public sealed partial class DeviceRowVm : ObservableObject
{
    public DeviceRowVm(int k, Action<int> select)
    {
        K = k;
        SelectCommand = new RelayCommand(() => select(K));
    }

    public int K { get; }
    public bool IsAll => K == 0;
    public string KText => K.ToString(CultureInfo.InvariantCulture);
    public ICommand SelectCommand { get; }

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _tip = "";
    /// <summary>run / err / 空（灰）。</summary>
    [ObservableProperty] private string _bar = "";
    [ObservableProperty] private bool _isRemote;
    /// <summary>第二行：普通部分 + 红色部分（「报警 1」/「急停」/「连接中断 …」）+ 之后的普通部分。</summary>
    [ObservableProperty] private string _line1 = "";
    [ObservableProperty] private string _line1Red = "";
    [ObservableProperty] private string _line1Tail = "";
    [ObservableProperty] private string _eta = "";
    [ObservableProperty] private bool _hasEta;
    /// <summary>
    /// v0.8.1 活动行（样稿 act.js axLines，v2 去掉框线）：急停没确认（红）· 搅拌电机故障（红）· 泄漏测试 / 容积标定（品牌蓝，等人操作 / 结束有问题琥珀）·
    /// PID 自整定（品牌蓝）· 触屏手动阀控（琥珀）。能点的行右边有 ›。
    /// </summary>
    [ObservableProperty] private IReadOnlyList<ActLineVm> _lines = Array.Empty<ActLineVm>();
    [ObservableProperty] private bool _hasLines;

    /// <summary>竖条上的小方块的点：err / run / 空。</summary>
    public bool BarErr => Bar == "err";
    public bool BarRun => Bar == "run";
    partial void OnBarChanged(string value) { OnPropertyChanged(nameof(BarErr)); OnPropertyChanged(nameof(BarRun)); }
}

/// <summary>
/// 设备面板（样稿 renderDevbar）：灰带「设备 n 台 · m 路」+ 添加 / 重新扫描两个圆钮；行（第一行总览 / 全部设备，一次看一台的页面没有）；选中设备的「设备信息」。
/// 收起时竖条上每台设备一个设备号小方块（选中的品牌蓝，右下角状态点）。
/// </summary>
public sealed partial class DevicePaneVm : ObservableObject
{
    private readonly ShellVm _shell;
    private readonly Dictionary<int, DeviceRowVm> _byK = new();

    public DevicePaneVm(ShellVm shell)
    {
        _shell = shell;
        AddCommand = new RelayCommand(() => shell.OpenDialog("add"));
        RescanCommand = new RelayCommand(() => shell.OpenDialog("devices"));
        ToggleCommand = new RelayCommand(() => shell.Layout.Toggle(DockPaneId.Devices));
    }

    public ICommand AddCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand ToggleCommand { get; }

    /// <summary>面板的行（含第一行总览 / 全部设备）。</summary>
    public ObservableCollection<DeviceRowVm> Rows { get; } = new();
    /// <summary>竖条上的设备号小方块（不含总览）。</summary>
    public ObservableCollection<DeviceRowVm> Minis { get; } = new();

    [ObservableProperty] private string _count = "0";
    [ObservableProperty] private string _channels = "0";
    [ObservableProperty] private string _infoTitle = "";
    [ObservableProperty] private bool _hasInfo;
    [ObservableProperty] private IReadOnlyList<InfoRow> _info = Array.Empty<InfoRow>();

    private DeviceRowVm Row(int k)
    {
        if (!_byK.TryGetValue(k, out var r)) _byK[k] = r = new DeviceRowVm(k, _shell.SelectDevice);
        return r;
    }

    internal void Refresh()
    {
        var nav = _shell.Nav;
        var ds = _shell.Fleet.Devices;
        int chs = ds.Sum(d => d.ChannelCount), run = ds.Sum(d => d.Running), inst = ds.Sum(d => d.Installed);
        Count = ds.Count.ToString(CultureInfo.InvariantCulture);
        Channels = chs.ToString(CultureInfo.InvariantCulture);

        var want = new List<DeviceRowVm>();
        if (nav.Scope != PageScope.One)
        {
            var all = Row(0);
            all.Name = nav.Scope == PageScope.Main ? "总览" : "全部设备";
            all.Model = ds.Count.ToString(CultureInfo.InvariantCulture) + " 台 · 运行 " + run.ToString(CultureInfo.InvariantCulture) + "/" + inst.ToString(CultureInfo.InvariantCulture);
            all.Tip = nav.Scope == PageScope.Main ? "总览：所有设备" : "全部设备";
            all.IsOn = nav.DeviceK == 0;
            want.Add(all);
        }
        foreach (var d in ds)
        {
            var r = Row(d.K);
            r.IsOn = nav.DeviceK == d.K;
            r.Name = d.Alias;
            r.Model = d.Model.Name;
            r.IsRemote = d.Lease == LeaseState.Remote && !d.IsOffline;
            r.Tip = d.Alias + " · " + d.Model.Name + " · " + (r.IsRemote ? "本机远程控制中" : d.IsOffline ? d.ConnText : "本地只读");
            r.Bar = d.EstopActive || d.ErrAlarms > 0 ? "err" : d.Running > 0 && !d.IsOffline ? "run" : "";
            // 断线（样稿 r2：「连接中断 · 数据停在 14:28:41」；重连倒计时写在提示和状态栏，从未连上的写连接状态）
            if (d.IsOffline) { r.Line1 = ""; r.Line1Red = d.EverOnline ? "连接中断" + (d.DataText.Length > 0 ? " · " + d.DataText : "") : d.ConnText; r.Line1Tail = ""; }
            else if (d.EstopActive) { r.Line1 = ""; r.Line1Red = "急停"; r.Line1Tail = ""; }
            else
            {
                var head = "运行 " + d.Running.ToString(CultureInfo.InvariantCulture) + "/" + d.Installed.ToString(CultureInfo.InvariantCulture);
                var tail = d.SessionId != null ? " · 记录中" : "";
                if (d.ErrAlarms > 0) { r.Line1 = head + " · "; r.Line1Red = "报警 " + d.ErrAlarms.ToString(CultureInfo.InvariantCulture); r.Line1Tail = tail; }
                else { r.Line1 = head + tail; r.Line1Red = ""; r.Line1Tail = ""; }
            }
            r.Eta = d.EtaText;
            r.HasEta = d.EtaText.Length > 0;
            var lines = ActLines(d);
            if (!lines.SequenceEqual(r.Lines)) r.Lines = lines;
            r.HasLines = lines.Count > 0;
            want.Add(r);
        }
        Sync(Rows, want);
        Sync(Minis, want.Where(r => !r.IsAll).ToList());
        foreach (var k in _byK.Keys.Where(k => k != 0 && ds.All(d => d.K != k)).ToList()) _byK.Remove(k);

        var sel = nav.DeviceK > 0 ? _shell.Fleet.Device(nav.DeviceK) : null;
        HasInfo = sel != null;
        if (sel != null)
        {
            InfoTitle = sel.Alias + " · 设备信息";
            var rows = InfoRows(sel);
            if (!rows.SequenceEqual(Info)) Info = rows;
        }
    }

    /// <summary>
    /// 一台设备的活动行（样稿 axLines 的顺序）：急停没确认 → 搅拌电机故障 → 泄漏测试 / 标定（面板里显示着的才列）→ PID 自整定 → 触屏手动阀控。
    /// </summary>
    internal IReadOnlyList<ActLineVm> ActLines(DeviceVm d)
    {
        var m = d.Marks;
        var list = new List<ActLineVm>();
        int k = d.K;
        var now = _shell.Fleet.Now;
        if (d.EstopActive && m.Estop is { } es && es.AnyUnconfirmed(now))
        {
            var bad = es.Rows(now).First(r => r.Bad);
            list.Add(new ActLineVm("r", "f-err-f", bad.Done + " · 到设备旁确认", "急停后设备没有收到这个部件的确认 · 点开看详情", true, () => _shell.AlarmPop.ShowEstop(k), k + ":estop"));
        }
        if (m.HasStirFault)
            list.Add(new ActLineVm("r", "f-err-f", "搅拌电机故障 · 在触屏清除", "电机故障 " + (m.StirCodeText ?? "") + (m.StirFault is { } f ? " · " + f : "") + " · 只能在设备触屏上清除",
                false, null, k + ":stir"));
        if (d.Act is { } a && _shell.Activity.Shown(k) != null)
        {
            var (tone, icon, text) = ActivityText.DevLine(a);
            list.Add(new ActLineVm(tone == ActTone.Amb ? "a" : "b", icon, text, ActivityText.Name(a) + " · " + (a.Live ? "点击打开浮动面板" : "点击打开结果"), true, () => _shell.Activity.Expand(k), k + ":act"));
        }
        if (m.Autotune.Count > 0)
        {
            var sp = m.Autotune[0].SpC;
            list.Add(new ActLineVm("b", "f-trend", "PID 自整定 " + ActivityText.Range(m.Autotune.Select(x => x.N).ToList()) + (sp is double v ? " · " + v.ToString("0", CultureInfo.InvariantCulture) + " °C" : ""),
                "在「设置 → 温控 / PID」里查看", true, () => { _shell.SelectDevice(k); _shell.OpenSettings("pid"); }, k + ":tune"));
        }
        if (m.ManualValve)
            list.Add(new ActLineVm("a", "f-wrench", "触屏手动阀控中", "有人在设备触屏上手动开关阀门 · 程序升压会排队", false, null, k + ":manual"));
        return list;
    }

    /// <summary>气源一行下面的说明（样稿 devInfo、阀门小块的悬停）。</summary>
    public const string GasSourceNote = "接什么气由工艺决定";

    /// <summary>设备信息（样稿 devInfo：地址、主机名、量程、压力报警、气源、搅拌、通讯、会话）。主机名接口没有（先写 SN）；压力报警 = 设备设置里的上限（F4）。</summary>
    public static IReadOnlyList<InfoRow> InfoRows(DeviceVm d)
    {
        var m = d.Model;
        var list = new List<InfoRow>
        {
            new("地址", d.Host + ":" + d.Port.ToString(CultureInfo.InvariantCulture)),
            new("SN", d.Sn.Length > 0 ? d.Sn : Fmt.Dash),
            new("量程", RibbonVm.RangeText(m)),
            // 压力报警上限（设备设置 alarms，PlantInfo；融合版 F4 补上，样稿 devInfo「压力报警」）
            new("压力报警", PressureDisplay.FixedU(d.Source.Plant.OverPressureMPa, 2)),
            // 只写气路名，接什么气由工艺决定（2026-11-18 用户确认；样稿 devInfo：名称用「、」连成一行，下面一行灰字）
            m.GasInlets.Count > 0 ? new("气源", string.Join("、", m.GasInlets.Select(g => g.Name)), Sub: GasSourceNote) : new("气源", Fmt.Dash),
            new("搅拌", m.StirMode == StirMode.Shared
                ? (d.Stir is { On: true } s ? JsNumber.ToString(Math.Round(s.Rpm)) + " rpm · 全部釜共用" : "停止 · 全部釜共用")
                : "各釜独立"),
            new("通讯", d.IsOffline ? d.ConnText : "已连接", Dot: !d.IsOffline),
            new("会话", d.SessionId is { } sid ? sid + "\n记录中" : "未记录"),
        };
        return list;
    }

    private static void Sync(ObservableCollection<DeviceRowVm> col, IReadOnlyList<DeviceRowVm> want)
    {
        for (int i = col.Count - 1; i >= 0; i--) if (!want.Contains(col[i])) col.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            int at = col.IndexOf(want[i]);
            if (at < 0) col.Insert(i, want[i]);
            else if (at != i) col.Move(at, i);
        }
    }
}

/// <summary>
/// 设备栏的一行活动（v0.8.1）：语气 b（品牌蓝）/ a（琥珀，要人去做事）/ r（红，故障）、图标、文字、悬停、能不能点（右边 ›）、点了做什么。
/// 相等只比显示的内容（<see cref="Id"/> + 语气 + 文字），刷新时内容没变就不换。
/// </summary>
public sealed class ActLineVm : IEquatable<ActLineVm>
{
    public ActLineVm(string tone, string icon, string text, string tip, bool clickable, Action? go, string id)
    {
        Tone = tone; Icon = icon; Text = text; Tip = tip; Clickable = clickable; Id = id;
        Command = new RelayCommand(() => go?.Invoke());
    }

    public string Tone { get; }
    public string Icon { get; }
    public string Text { get; }
    public string Tip { get; }
    public bool Clickable { get; }
    public string Id { get; }
    public ICommand Command { get; }
    public bool IsB => Tone == "b";
    public bool IsA => Tone == "a";
    public bool IsR => Tone == "r";

    public bool Equals(ActLineVm? o) => o != null && o.Id == Id && o.Tone == Tone && o.Text == Text && o.Icon == Icon && o.Tip == Tip;
    public override bool Equals(object? obj) => Equals(obj as ActLineVm);
    public override int GetHashCode() => HashCode.Combine(Id, Tone, Text);
}

/// <summary>状态栏里一项收起了的设备活动（v0.8.1，样稿 .status .axst）：「1号机 · 泄漏测试 · 平衡 · 16 %」，等人操作琥珀；点它展开浮动面板。</summary>
public sealed class ActStatusVm : IEquatable<ActStatusVm>
{
    public ActStatusVm(int k, string text, bool amb, string icon, string tip, ActivityPanelVm panel)
    {
        K = k; Text = text; Amb = amb; Icon = icon; Tip = tip;
        Command = new RelayCommand(() => panel.Expand(k));
    }

    public int K { get; }
    public string Text { get; }
    public bool Amb { get; }
    public string Icon { get; }
    public string Tip { get; }
    public ICommand Command { get; }

    public bool Equals(ActStatusVm? o) => o != null && o.K == K && o.Text == Text && o.Amb == Amb && o.Icon == Icon;
    public override bool Equals(object? obj) => Equals(obj as ActStatusVm);
    public override int GetHashCode() => HashCode.Combine(K, Text);
}

/// <summary>状态栏的一项（样稿 .status .it：13 px 图标 + 文字；红字粗体的一段放在 <see cref="Red"/>）。</summary>
public sealed record StatusItem(string Icon, string Text, string Red = "", string Tail = "", string? Tip = null, bool Alert = false);

/// <summary>
/// 状态栏（样稿 renderStatus，26 高、bg-bar）：左 已连接 k/n 台（断线时红：「已连接 2/3 台 · 3号机重连中」）| <see cref="Extra"/>（F3：运行中 · 已阻止睡眠 / 重连中）|
/// 远程控制 n 台 | 记录 k/n 台 | 报警 a · 提醒 b · 未确认 n（红 600）| 选中的通道「1号机 · RV1 · 反应中」；右 示例型号 · 参数为假设 | 采样 1 s | 日期时间。
/// </summary>
public sealed partial class StatusBarVm : ObservableObject
{
    private readonly ShellVm _shell;

    public StatusBarVm(ShellVm shell)
    {
        _shell = shell;
        Extra.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasExtra));
    }

    /// <summary>有 F3 的状态项（没有时那一格不占位置）。</summary>
    public bool HasExtra => Extra.Count > 0;

    [ObservableProperty] private StatusItem _conn = new("f-link", "");

    /// <summary>「运行中 · 已阻止睡眠」（样稿 SETP.nosleep：有设备在运行，本机不会进入睡眠，屏幕可以关闭）。</summary>
    public const string SleepText = "运行中 · 已阻止睡眠";
    /// <summary>F3 的状态项（已阻止睡眠、重连中 …）放这里：在「已连接」与「远程控制」之间，各带一条竖线。</summary>
    public ObservableCollection<StatusItem> Extra { get; } = new();
    [ObservableProperty] private string _remoteText = "";
    [ObservableProperty] private string _recordText = "";
    [ObservableProperty] private string _alarmText = "";
    [ObservableProperty] private string _alarmRed = "";
    [ObservableProperty] private string _alarmTail = "";
    [ObservableProperty] private string? _selText;
    [ObservableProperty] private string? _noteText;
    /// <summary>v0.8.1：收起了的设备活动（每台一项，可以点）。</summary>
    public ObservableCollection<ActStatusVm> Acts { get; } = new();
    [ObservableProperty] private bool _hasActs;
    [ObservableProperty] private string _sampleText = "采样 1 s";
    [ObservableProperty] private string _clockText = "";

    internal void Refresh()
    {
        var f = _shell.Fleet;
        var ds = f.Devices;
        int on = ds.Count(d => !d.IsOffline);
        var lost = ds.FirstOrDefault(d => d.IsOffline && d.EverOnline && d.ConnStatus != ConnStatus.Disconnected);
        Conn = on == ds.Count || lost == null
            ? new StatusItem("f-link", "已连接 " + on + "/" + ds.Count + " 台")
            : new StatusItem("f-warn-f", "已连接 " + on + "/" + ds.Count + " 台 · " + lost.Alias + "重连中", Alert: true);
        // 融合版 F8：只在真的阻止着系统睡眠时写「运行中 · 已阻止睡眠」（设置开 + 有设备在运行 + 系统接受了申请，见 ShellVm.Sleep）
        bool sleepBlock = _shell.Sleep.Held;
        bool has = Extra.Any(x => x.Text == SleepText);
        if (sleepBlock && !has) Extra.Add(new StatusItem("f-pc", SleepText, Tip: "有设备在运行，本机不会进入睡眠；屏幕可以关闭 · 全部结束后恢复系统设置"));
        else if (!sleepBlock && has) Extra.Remove(Extra.First(x => x.Text == SleepText));
        RemoteText = "远程控制 " + ds.Count(d => !d.IsOffline && d.Lease == LeaseState.Remote) + " 台";
        RecordText = "记录 " + ds.Count(d => d.SessionId != null) + "/" + ds.Count + " 台";
        int err = ds.Sum(d => d.ErrAlarms), warn = ds.Sum(d => d.WarnAlarms), ua = MenubarVm.UnackedOf(ds);
        AlarmText = "报警 " + err + " · 提醒 " + warn + " · ";
        AlarmRed = ua > 0 ? "未确认 " + ua : "";
        AlarmTail = ua > 0 ? "" : "已全部确认";
        var nav = _shell.Nav;
        if (nav.DeviceK > 0 && f.Device(nav.DeviceK) is { } d && d.Reactor(nav.Channel) is { } r)
            SelText = d.Alias + " · RV" + r.N + " · " + (r.Act?.Word ?? Pages.Home.RunRowText.Word(r.Own, r.Cause, r.OwnWord));
        else SelText = null;
        // v0.8.1：收起了的活动各一项（点它重新展开浮动面板）；窄屏缩成「1号机 泄漏测试 16 %」
        bool narrow = !_shell.Layout.IsWide;
        var acts = _shell.Activity.Deck.Visible(f.Now).Where(x => x.Folded && f.Device(x.K) != null)
            .Select(x => new ActStatusVm(x.K, ActivityText.Status(f.Device(x.K)!.Alias, x.Activity, narrow), x.Activity.Waiting,
                x.Activity.Kind == ActKind.Leak ? "f-timer" : "f-beaker", ActivityText.Title(f.Device(x.K)!.Alias, x.Activity) + " · 点击打开浮动面板",
                _shell.Activity)).ToList();
        if (!acts.SequenceEqual(Acts)) { Acts.Clear(); foreach (var a in acts) Acts.Add(a); }
        HasActs = Acts.Count > 0;
        // 窄屏又有收起的活动时，演示数据不写「演示数据 · 」（状态栏放不下；窗口标题里照样写着演示数据）
        NoteText = f.IsDemo ? (narrow && HasActs ? "" : "演示数据 · ") + ModelProfiles.SampleNote : f.HasSampleModels ? ModelProfiles.SampleNote : null;
        var now = f.Now;
        ClockText = Fmt.Date(now) + " " + Fmt.Time(now);
    }
}
