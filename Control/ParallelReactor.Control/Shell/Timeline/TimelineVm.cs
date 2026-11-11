using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Shell;

/// <summary>时间轴刻度（相对位置 0–1 + 文字）。</summary>
public sealed record TimeTick(double X, string Text);

/// <summary>
/// 时间轴上的一段（样稿 shell.js tlBar 的一个 .pr-tl-seg）：起止时刻、状态（已完成 / 当前 / 未到 / 中止）、当前段的进度填充、
/// 名称（+ 当前段的标记「当前 / 暂停 / 等待进气 / 离线」）、悬停提示「第 1 步 升温 · 09:23–09:52」。不可变：每次刷新换新的。
/// </summary>
public sealed record TlSeg(int I, string Name, string Full, SegmentState State, bool Wait, bool Run, bool Held, DateTime T0, DateTime T1, string Tip);

/// <summary>
/// 时间轴（PORT-PLAN §2.1「时间轴」、§5 M2「TimelineVm：组 / 行 / 段 / 时间刻度」；样稿 shell.js renderTimeline / tlRow / tlBar / tlFollow）。
/// 标题栏：收起 / 展开 · 时间轴 · 运行 k/n（只数已连接的设备；另注断线台数）· 刻度（「现在」在左 45 %，跨度 1 / 2 / 4 / 8 h）· 放大 / 缩小 · 跨度。
/// 正文：按设备分组（<see cref="Groups"/>，身份 = 位置 K）：组头（展开箭头 · 设备图标 · 别名 · 型号（放得下才显示）| 运行 k/n · 急停 / 报警 / 提醒 · 控制权 |
/// 全部停止 · 打开）+ 每个已安装的反应釜一行（状态点 · RV · 状态字 · 程序名 | 程序条 | 运行 · 停止 · 打开，悬停或选中时出现）+ 合并的「RV5–RV8 未安装」。
/// 默认 ≤ 8 路的设备展开、16 路收起；导航 / 改选中对象时跟随（<see cref="Follow"/>：展开那一台、组头滚到顶部、选中行滚到可见处）。
/// 行、段都按身份就地更新（R10）；时间窗口 = [现在 − 45 % 跨度, 现在 + 55 % 跨度]，轨道宽度按外框布局算（标签列 300 / 248 / 220，操作列 128）。
/// </summary>
public sealed partial class TimelineVm : ObservableObject
{
    public static readonly int[] Spans = { 60, 120, 240, 480 };
    public const double ActsW = 128;

    private readonly ShellVm _shell;
    private readonly Dictionary<int, bool> _open = new();
    private int? _followK;

    public TimelineVm(ShellVm shell)
    {
        _shell = shell;
        ToggleCommand = new RelayCommand(shell.ToggleTimeline);
        ZoomCommand = new RelayCommand<string>(s =>
        {
            int i = Array.IndexOf(Spans, SpanMin) + (s == "1" ? 1 : -1);
            SpanMin = Spans[Math.Clamp(i, 0, Spans.Length - 1)];
            Refresh();
        });
    }

    public ICommand ToggleCommand { get; }
    /// <summary>参数 "-1" = 放大（缩短跨度），"1" = 缩小。</summary>
    public ICommand ZoomCommand { get; }

    // —— 标题栏 ——
    [ObservableProperty] private int _spanMin = 120;
    [ObservableProperty] private string _runText = "";
    [ObservableProperty] private string _runTip = "";
    [ObservableProperty] private string? _offText;
    [ObservableProperty] private string _offTip = "";
    [ObservableProperty] private string _toggleIcon = "down";
    [ObservableProperty] private string _toggleTip = "收起时间轴";
    [ObservableProperty] private string? _zoomInReason;
    [ObservableProperty] private string? _zoomOutReason;
    [ObservableProperty] private string _spanText = "2 h";
    [ObservableProperty] private IReadOnlyList<TimeTick> _ticks = Array.Empty<TimeTick>();
    [ObservableProperty] private double _nowX = 0.45;
    [ObservableProperty] private string _nowText = "";

    // —— 正文 ——
    public ObservableCollection<TlGroupVm> Groups { get; } = new();
    /// <summary>标签列宽（资源管理器停靠时 300，与它的右边线对齐；否则 248，窄于 1600 时 220）。</summary>
    [ObservableProperty] private double _labelWidth = 300;
    /// <summary>窄于 1600（样稿 .fw-s）：组头不画设备图标、行不写程序名。</summary>
    [ObservableProperty] private bool _narrow;
    /// <summary>时间轴只剩标题栏（收起或专注模式）。</summary>
    [ObservableProperty] private bool _folded;
    /// <summary>窗口起点、终点、现在。</summary>
    public DateTime X0 { get; private set; }
    public DateTime X1 { get; private set; }
    public DateTime Now { get; private set; }
    /// <summary>网格竖线的位置（0–1；刻度离两端不足半个字宽时不画）。</summary>
    public IReadOnlyList<double> Lines { get; private set; } = Array.Empty<double>();
    /// <summary>窗口或网格变了（轨道重画）。</summary>
    public event EventHandler? WindowChanged;

    /// <summary>跟随请求（导航 / 改选中对象）：视图在排版后滚动。</summary>
    public event EventHandler<TlFollow>? FollowRequested;

    /// <summary>重置布局时跨度回到 2 h。</summary>
    internal void ResetSpan() => SpanMin = 120;

    /// <summary>
    /// 展开状态回到默认（≤ 8 路展开、16 路收起）并刷新组：视图 › 重置布局（样稿 resetLayout 的 tlOpen = {}，跟随状态留着）。
    /// <paramref name="fresh"/>：新开的页面（截图，ShellVm.ResetView）—— 跟随状态也清掉（样稿 tlFollowK = null），下一次跟随算换了设备。
    /// </summary>
    internal void ResetOpen(bool fresh = false)
    {
        _open.Clear();
        if (fresh) _followK = null;
        Refresh();
    }

    /// <summary>这台设备的组展开着（默认 ≤ 8 路展开，样稿 tlGroupOpen）。</summary>
    public bool IsOpen(DeviceVm d) => _open.TryGetValue(d.K, out var o) ? o : d.ChannelCount <= 8;

    /// <summary>组头（标签或展开箭头）：展开 / 收起。</summary>
    public void ToggleGroup(int k)
    {
        if (_shell.Fleet.Device(k) is not { } d) return;
        _open[k] = !IsOpen(d);
        Refresh();
    }

    /// <summary>
    /// 跟随打开的设备（样稿 tlFollow）：页签的对象（没有时用选中的设备）那一台的组展开（含 16 路），换了设备时把它的组头滚到顶部，
    /// 再让页签 / 选中的反应釜行可见（组头吸顶，不藏在组头下面）；检查器选中的是别的设备的反应釜时，那一行也展开、滚到可见处。
    /// 只在导航 / 改选中对象时做，用户手动滚动保留到下一次导航。时间轴收起时不做。
    /// </summary>
    public void Follow(bool changedDevice)
    {
        var r = _shell.Route;
        var sel = _shell.Selection;
        int k = r.Page is PageKind.Device or PageKind.Reactor && _shell.Fleet.Device(r.K) != null ? r.K : sel.K;
        var d = _shell.Fleet.Device(k);
        if (d == null || _shell.Layout.TimelineFolded) return;
        bool newDev = changedDevice || _followK != d.K;
        bool changed = false;
        if (newDev && !IsOpen(d)) { _open[d.K] = true; changed = true; }
        int? n = r.Page == PageKind.Reactor && r.K == d.K ? r.N : sel.K == d.K ? sel.N : null;
        (int K, int N)? other = null;
        if (sel.N is int sn && sel.K != d.K && _shell.Fleet.Device(sel.K) is { } sd)
        {
            if (!IsOpen(sd)) { _open[sd.K] = true; changed = true; }
            other = (sd.K, sn);
        }
        if (changed) Refresh();
        _followK = d.K;
        FollowRequested?.Invoke(this, new TlFollow(d.K, n, newDev, other));
    }

    internal void Refresh()
    {
        var fleet = _shell.Fleet;
        var ds = fleet.Devices;
        var L = _shell.Layout;

        // 标题栏：合计只算已连接的设备（断线设备的状态是旧的），另注断线台数
        var live = ds.Where(d => d.ConnStatus == ConnStatus.Online).ToList();
        int run = live.Sum(d => d.Running), inst = live.Sum(d => d.Installed), off = ds.Count - live.Count;
        RunText = "运行 " + run + "/" + inst;
        var offNote = off > 0 ? " · 另有 " + off + " 台断线（状态停在断线时刻，不计入）" : "";
        RunTip = "已连接设备：运行 " + run + "/" + inst + offNote;
        OffText = off > 0 ? "· 断线 " + off : null;
        OffTip = off > 0 ? "另有 " + off + " 台断线（状态停在断线时刻，不计入）" : "";
        Folded = L.TimelineFolded;
        ToggleIcon = Folded ? "up" : "down";
        ToggleTip = Folded ? "展开时间轴" : "收起时间轴";
        int zi = Array.IndexOf(Spans, SpanMin);
        ZoomInReason = zi <= 0 ? "已是最短 1 h" : null;
        ZoomOutReason = zi >= Spans.Length - 1 ? "已是最长 8 h" : null;
        SpanText = SpanMin / 60 + " h";
        LabelWidth = L.TimelineLabelWidth;
        Narrow = L.IsNarrow;

        // 时间窗口与刻度：步长 10 / 15 / 30 / 60 min；轨道宽 = 时间轴宽 − 标签列 − 操作列 128；
        // 刻度离两端不足半个字宽（22 px）时不画（不被刻度栏裁掉），离「现在」太近只留竖线
        double tlW = L.FrameWidth - ShellLayout.SideW - L.InspectorColumn.Value;
        double trackPx = Math.Max(200, tlW - L.TimelineLabelWidth - ActsW);
        var now = fleet.Now;
        double span = SpanMin;
        var x0 = now.AddMinutes(-span * 0.45);
        var x1 = x0.AddMinutes(span);
        int stepMin = SpanMin <= 60 ? 10 : SpanMin <= 120 ? 15 : SpanMin <= 240 ? 30 : 60;
        double nowX = 0.45, gap = 64 / trackPx;
        var ticks = new List<TimeTick>();
        var lines = new List<double>();
        var day = x0.Date;
        double first = Math.Ceiling((x0 - day).TotalMinutes / stepMin - 1e-9) * stepMin;
        for (var t = day.AddMinutes(first); t <= x1; t = t.AddMinutes(stepMin))
        {
            double x = (t - x0).TotalMinutes / span, xp = x * trackPx;
            if (xp < 22 || xp > trackPx - 22) continue;
            lines.Add(x);
            if (Math.Abs(x - nowX) < gap) continue;
            ticks.Add(new TimeTick(x, Fmt.Hm(t)));
        }
        if (!ticks.SequenceEqual(Ticks)) Ticks = ticks;
        NowX = nowX;
        NowText = "现在 " + Fmt.Hm(now);
        bool windowChanged = X0 != x0 || X1 != x1 || !lines.SequenceEqual(Lines);
        X0 = x0; X1 = x1; Now = now;
        if (windowChanged) { Lines = lines; WindowChanged?.Invoke(this, EventArgs.Empty); }

        SyncGroups(ds);
    }

    private void SyncGroups(IReadOnlyList<DeviceVm> ds)
    {
        for (int i = Groups.Count - 1; i >= 0; i--) if (ds.All(d => !ReferenceEquals(d, Groups[i].D))) Groups.RemoveAt(i);
        for (int i = 0; i < ds.Count; i++)
        {
            var d = ds[i];
            int at = -1;
            for (int j = i; j < Groups.Count; j++) if (ReferenceEquals(Groups[j].D, d)) { at = j; break; }
            TlGroupVm g;
            if (at < 0) { g = new TlGroupVm(this, _shell, d); Groups.Insert(i, g); }
            else { g = Groups[at]; if (at != i) Groups.Move(at, i); }
            g.Refresh(IsOpen(d));
        }
    }
}

/// <summary>跟随请求：设备 K 的组（换了设备时组头滚到顶部）、要可见的行 N（可为 null）；另一台设备上选中的行（可为 null）。</summary>
public sealed record TlFollow(int K, int? N, bool ScrollGroupToTop, (int K, int N)? Other);

/// <summary>
/// 时间轴的一组（一台设备，样稿 .pr-tl-group）。组头中性（无设备色条）：运行 k/n（600 t-2）· 急停中 / 报警 n（红点 + 中性字 600）· 提醒 n · 控制权；
/// 收起时「 · 8 路已折叠」；断线时整句写连接（wifi-off 红 / 从未连上 link 灰）+「数据停在 …」（+ 控制权宽限）。行按 RV 号就地同步。
/// </summary>
public sealed partial class TlGroupVm : ObservableObject
{
    private readonly ShellVm _shell;

    public TlGroupVm(TimelineVm tl, ShellVm shell, DeviceVm d)
    {
        Tl = tl;
        _shell = shell;
        D = d;
        ToggleCommand = new RelayCommand(() => tl.ToggleGroup(D.K));
        OpenCommand = new RelayCommand(() => shell.Navigate(Route.Device(D.K)));
        StopAll = new InspAct("", "stop", () => _ = D.Actions.RunAsync(DeviceAction.StopAll));
    }

    public TimelineVm Tl { get; }
    public DeviceVm D { get; }
    public int K => D.K;
    public ICommand ToggleCommand { get; }
    public ICommand OpenCommand { get; }
    public InspAct StopAll { get; }

    /// <summary>反应釜行与合并的未安装行（展开时）。</summary>
    public ObservableCollection<object> Rows { get; } = new();

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _twistIcon = "down";
    [ObservableProperty] private string _twistTip = "收起";
    [ObservableProperty] private string _labelTip = "";
    [ObservableProperty] private bool _isOff;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private string _offText = "";
    [ObservableProperty] private string _offIcon = "wifioff";
    [ObservableProperty] private string _runText = "";
    [ObservableProperty] private string? _estopText;
    [ObservableProperty] private string? _errText;
    [ObservableProperty] private string? _warnText;
    /// <summary>控制权文字（宽限时「控制权宽限 18 s」）+ 收起时「 · 8 路已折叠」。</summary>
    [ObservableProperty] private string _leaseText = "";
    [ObservableProperty] private string _openTip = "";
    [ObservableProperty] private string _modelLabel = "";

    internal void Refresh(bool open)
    {
        var d = D;
        IsOpen = open;
        TwistIcon = open ? "down" : "right";
        TwistTip = open ? "收起" : "展开";
        LabelTip = d.Alias + " · " + d.ModelLabel + " · 点击" + (open ? "收起" : "展开");
        ModelLabel = d.ModelLabel;
        IsOff = d.IsOffline;
        IsNew = d.IsFirst;
        var lease = d.Source.Lease;
        OffText = d.ConnText + " · " + d.DataText + (lease.State == LeaseStatus.Lost ? " · 控制权宽限 " + lease.GraceS + " s" : "");
        OffIcon = d.IsFirst ? "link" : "wifioff";
        RunText = "运行 " + d.Running + "/" + d.Installed;
        EstopText = d.EstopActive ? d.EstopText : null;
        ErrText = d.ErrAlarms > 0 ? "报警 " + d.ErrAlarms : null;
        WarnText = d.WarnAlarms > 0 ? "提醒 " + d.WarnAlarms : null;
        var lt = lease.State == LeaseStatus.Lost ? "控制权宽限 " + lease.GraceS + " s" : d.LeaseText;
        LeaseText = lt + (open ? "" : " · " + d.Installed + " 路已折叠");
        OpenTip = "打开 " + d.Alias;
        StopAll.Tip = "全部停止 " + d.Alias;
        StopAll.Reason = InspCtx.Nz(d.Actions.Why(DeviceAction.StopAll));
        SyncRows(open);
    }

    private void SyncRows(bool open)
    {
        var want = new List<object>();
        if (open)
        {
            var rs = D.Reactors;
            for (int j = 0; j < rs.Count; j++)
            {
                var r = rs[j];
                if (r.Installed)
                {
                    var row = Rows.OfType<TlRowVm>().FirstOrDefault(x => ReferenceEquals(x.R, r)) ?? new TlRowVm(Tl, _shell, D, r);
                    want.Add(row);
                    continue;
                }
                int e = j;
                while (e + 1 < rs.Count && !rs[e + 1].Installed) e++;
                int from = r.N, to = rs[e].N;
                var un = Rows.OfType<TlUnRowVm>().FirstOrDefault(x => x.From == from && x.To == to) ?? new TlUnRowVm(Tl, from, to);
                un.Offline = D.IsOffline;
                want.Add(un);
                j = e;
            }
        }
        var keep = new HashSet<object>(want);
        for (int i = Rows.Count - 1; i >= 0; i--) if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            var o = want[i];
            if (i < Rows.Count && ReferenceEquals(Rows[i], o)) continue;
            int at = Rows.IndexOf(o);
            if (at >= 0) Rows.Move(at, i); else Rows.Insert(i, o);
        }
        foreach (var row in Rows.OfType<TlRowVm>()) row.Refresh();
    }
}

/// <summary>合并的未安装行（样稿「RV5–RV8 未安装」，14 px t-3，不可点）。</summary>
public sealed partial class TlUnRowVm : ObservableObject
{
    public TlUnRowVm(TimelineVm tl, int from, int to)
    {
        Tl = tl;
        From = from;
        To = to;
        Text = (to > from ? "RV" + from + "–RV" + to : "RV" + from) + " 未安装";
    }

    public TimelineVm Tl { get; }
    public int From { get; }
    public int To { get; }
    public string Text { get; }

    /// <summary>
    /// 设备断线：这一行也同反应釜行 bg-panel、悬停不变（样稿 .pr-tl-group.is-off .pr-tl-row 也套在 .is-un 上；
    /// 复核：以前只有反应釜行带 off，这一行仍是 bg-head、悬停变 bg-hover）。行按 RV 范围留着，组刷新时就地改。
    /// </summary>
    [ObservableProperty] private bool _offline;
}

/// <summary>
/// 时间轴的一行（一个反应釜，样稿 tlRow，36 px）：标签（状态点 · RV 16/600 · 状态字 · 程序名，单击选中、双击打开、右键菜单）|
/// 程序条（<see cref="Segments"/>：每步一段，已完成浅绿、当前段按进度填充（运行中呼吸）、暂停 / 中止灰；段宽放得下才写名字；
/// 程序在窗口右边之外结束时行尾「11:54 ›」）| 行操作（运行或启动、停止、打开；悬停或选中时出现）。
/// </summary>
public sealed partial class TlRowVm : ObservableObject
{
    private readonly ShellVm _shell;
    private DeviceAction _runAction = DeviceAction.Start;

    public TlRowVm(TimelineVm tl, ShellVm shell, DeviceVm d, ReactorVm r)
    {
        Tl = tl;
        _shell = shell;
        D = d;
        R = r;
        SelectCommand = new RelayCommand(() => shell.Select(D.K, R.N));
        OpenCommand = new RelayCommand(() => shell.Navigate(Route.Reactor(D.K, R.N)));
        Run = new InspAct("", "play", () => _ = D.Actions.RunAsync(_runAction, ActionPayload.Ch(R.N)));
        Stop = new InspAct("", "stop", () => _ = D.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(R.N)));
    }

    public TimelineVm Tl { get; }
    public DeviceVm D { get; }
    public ReactorVm R { get; }
    public ICommand SelectCommand { get; }
    public ICommand OpenCommand { get; }
    public InspAct Run { get; }
    public InspAct Stop { get; }

    [ObservableProperty] private bool _isSelected;
    /// <summary>反应釜在运行（「运行」按钮画成按下的样子，样稿 iconBtn active）。</summary>
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string? _progName;
    [ObservableProperty] private string _openTip = "";
    /// <summary>这一行的程序段（每次刷新换新的不可变列表；没有程序为空）。</summary>
    [ObservableProperty] private IReadOnlyList<TlSeg> _segments = Array.Empty<TlSeg>();
    /// <summary>程序在窗口右边之外结束、仍在运行 / 暂停时行尾的「11:54 ›」（null = 不画）。</summary>
    [ObservableProperty] private string? _moreText;
    [ObservableProperty] private string? _moreTip;
    /// <summary>当前段的填充到「现在」为止（运行 / 暂停的程序）。</summary>
    [ObservableProperty] private bool _offline;

    internal void Refresh()
    {
        var d = D;
        var r = R;
        IsSelected = _shell.Selection.IsReactor(d.K, r.N);
        IsRunning = r.IsRunning;
        var prog = d.Source.Program(r.N);
        ProgName = prog?.Name;
        var name = d.Alias + " · " + r.Rv;
        _runAction = prog is { Status: ProgramStatus.Loaded or ProgramStatus.Aborted or ProgramStatus.Done } ? DeviceAction.ProgRun : DeviceAction.Start;
        Run.Tip = (_runAction == DeviceAction.ProgRun ? "运行程序 " : "启动 ") + name;
        Run.Reason = InspCtx.Nz(d.Actions.Why(_runAction, ActionPayload.Ch(r.N)));
        Stop.Tip = "停止 " + name;
        Stop.Reason = InspCtx.Nz(d.Actions.Why(DeviceAction.Stop, ActionPayload.Ch(r.N)));
        OpenTip = "打开 " + name;
        Offline = d.IsOffline;

        // 程序条（样稿 tlBar）：每步一段；当前段的标记：暂停 / 等待进气 / 离线 / 当前
        var segs = d.Segments(r.N);
        if (prog == null || segs.Count == 0)
        {
            if (Segments.Count > 0) Segments = Array.Empty<TlSeg>();
            MoreText = null;
            MoreTip = null;
            return;
        }
        var pr = r.Program?.Progress;
        bool active = prog.Status is ProgramStatus.Running or ProgramStatus.Held;
        var list = new List<TlSeg>(segs.Count);
        foreach (var s in segs)
        {
            bool cur = s.State == SegmentState.Cur;
            string tag = !cur ? "" : prog.Status == ProgramStatus.Held ? "暂停" : pr is { WaitGas: true } ? pr.WaitWord : pr is { Offline: true } ? "离线" : "当前";
            bool run = cur && prog.Status == ProgramStatus.Running && !(pr is { Offline: true } or { WaitGas: true });
            // 等待进气：演示 = 样稿（去触屏开阀）；真机 = 排队等共用气路「等待气路（RV3 占用中）」（§3.5 / R6）
            var tip = "第 " + (s.I + 1) + " 步 " + s.Name + " · " + Fmt.Hm(s.T0) + "–" + Fmt.Hm(s.T1)
                + (!s.WaitGas ? "" : d.Source.IsDemo ? " · 等待进气（需在触屏开阀）" : " · " + StateMap.WaitGasText(d.GasHolder));
            list.Add(new TlSeg(s.I, s.Name, s.Name + (tag.Length > 0 ? " · " + tag : ""), s.State, s.WaitGas, run, cur && prog.Status == ProgramStatus.Held, s.T0, s.T1, tip));
        }
        if (!list.SequenceEqual(Segments)) Segments = list;
        var end = segs[^1].T1;
        bool more = end > Tl.X1 && active;
        MoreText = more ? Fmt.Hm(end) : null;
        MoreTip = more ? "预计 " + Fmt.Hm(end) + " 结束" : null;
    }
}
