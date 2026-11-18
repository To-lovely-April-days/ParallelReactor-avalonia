using ParallelReactor.Control.Core.Format;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Curves;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Alarms;

/// <summary>
/// 报警页（融合版 F5，样稿 PAGES.alarm）：全部设备的报警与事件。
/// <list type="bullet">
/// <item>左块「报警与事件 · 1号机 / 全部设备」：筛选（今天 / 近 7 天 / 自定义…｜全部 / 报警 / 提醒 / 事件｜全部 / 未确认 / 活动 / 已解除｜搜索）、
///   四个指标（活动报警 / 未确认 / 提醒 / 今天的事件）、表（时间 · 级别 ·（全部设备时）设备 · 来源 · 内容 · 值 / 限值 · 状态 · 确认）。</item>
/// <item>右块「详情」：报警名、发生 / 状态 / 确认 / 值 / 限值 / 自动动作、处理建议（样稿 AL_TIP）、报警前后 30 min 的小趋势（本机历史缓存）、
///   相关事件、确认 · 定位到通道 · 查看曲线 · 停止 RVn（先弹停止通道的确认框）。</item>
/// <item>工具条：确认所选 · 全部确认（n）｜导出 CSV（当前筛选的行）。确认不需要控制权。</item>
/// </list>
/// 数据 = 各台设备的报警簿（活动 + 已解除）+ 事件簿（Core 的 <see cref="AlarmLog"/> 合成、筛选）。设备 = 路由的设备（0 = 全部设备）。
/// </summary>
public sealed partial class AlarmPageVm : PageVm
{
    private readonly ShellVm _shell;
    private readonly RibbonTool _ackSel, _ackAll, _export;
    private readonly IReadOnlyList<RibbonGroup> _tools;
    private IReadOnlyList<AlarmLogRow> _all = Array.Empty<AlarmLogRow>();
    private string? _sel;
    private string _prevRange = "day";
    private bool _resetting;

    public AlarmPageVm(ShellVm shell) : base(AppPage.Alarms)
    {
        _shell = shell;
        _ackSel = new RibbonTool("f-check", "确认所选", AckSelected, "确认选中的报警 · 不需要控制权");
        _ackAll = new RibbonTool("f-check", "全部确认", AckAll, "确认全部设备的未确认报警与提醒 · 不需要控制权");
        _export = new RibbonTool("f-down", "导出 CSV", ExportCsv, "导出当前筛选的报警与事件（CSV）");
        _tools = new[] { new RibbonGroup("确认", new[] { _ackSel, _ackAll }), new RibbonGroup("导出", new[] { _export }) };
        AckOneCommand = new RelayCommand<AlarmRowVm>(r => { if (r != null) Ack(r.Row); });
        AckCommand = new RelayCommand(() => { if (Current is { } r) Ack(r); });
        LocateCommand = new RelayCommand(Locate);
        CurveCommand = new RelayCommand(ToCurve);
        StopCommand = new RelayCommand(StopCh);
        PickCommand = new RelayCommand<string>(Pick);
    }

    public override IReadOnlyList<RibbonGroup> Tools => _tools;
    public override bool ShowsAlarmLog => true;

    public ICommand AckOneCommand { get; }
    public ICommand AckCommand { get; }
    public ICommand LocateCommand { get; }
    public ICommand CurveCommand { get; }
    public ICommand StopCommand { get; }
    /// <summary>选中一行（表格行、相关事件；参数 = 行的身份）。</summary>
    public ICommand PickCommand { get; }

    /// <summary>导出目录（测试换成临时目录）：数据目录（<see cref="Services.DataDirs.Root"/>）/export。</summary>
    public static Func<string> ExportDir { get; set; } = () => Path.Combine(Services.DataDirs.Root, "export");

    // ———————————————— 筛选 ————————————————

    /// <summary>时间段：day 今天 / d7 近 7 天 / cus 自定义（只提示，回到原来的选择）。</summary>
    [ObservableProperty] private string _rangeKey = "day";
    /// <summary>级别：all / hi 报警 / lo 提醒 / ev 事件。</summary>
    [ObservableProperty] private string _levelKey = "all";
    /// <summary>状态：all / ua 未确认 / act 活动 / clr 已解除。</summary>
    [ObservableProperty] private string _stateKey = "all";
    [ObservableProperty] private string _query = "";

    partial void OnRangeKeyChanged(string value)
    {
        if (value == "cus")
        {
            _shell.Toast("自定义时间段：日期选择还没做 · 本机保留的报警与事件都在「近 7 天」里", ToastKind.Info);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RangeKey = _prevRange);
            return;
        }
        _prevRange = value;
        Rebuild();
    }

    partial void OnLevelKeyChanged(string value) => Rebuild();
    partial void OnStateKeyChanged(string value) => Rebuild();
    partial void OnQueryChanged(string value) => Rebuild();

    public AlarmLogFilter Filter => new(
        RangeKey == "d7" ? AlarmLogRange.Week : AlarmLogRange.Day,
        LevelKey switch { "hi" => AlarmLogLevelFilter.Hi, "lo" => AlarmLogLevelFilter.Lo, "ev" => AlarmLogLevelFilter.Ev, _ => AlarmLogLevelFilter.All },
        StateKey switch { "ua" => AlarmLogStateFilter.Unacked, "act" => AlarmLogStateFilter.Active, "clr" => AlarmLogStateFilter.Cleared, _ => AlarmLogStateFilter.All },
        Query);

    // ———————————————— 列表 ————————————————

    /// <summary>灰带副标题：「1号机」/「全部设备」。</summary>
    [ObservableProperty] private string _scopeText = "";
    /// <summary>全部设备（表格多一列「设备」）。</summary>
    [ObservableProperty] private bool _isAllDevices;
    [ObservableProperty] private bool _isNarrow;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private int _kpiActive;
    [ObservableProperty] private int _kpiUnacked;
    [ObservableProperty] private int _kpiWarn;
    [ObservableProperty] private int _kpiToday;
    [ObservableProperty] private bool _kpiActiveErr;

    public ObservableCollection<AlarmRowVm> Rows { get; } = new();

    // ———————————————— 详情 ————————————————

    [ObservableProperty] private bool _hasDetail;
    [ObservableProperty] private string _detailSub = "";
    [ObservableProperty] private AlarmLogLevel _detailLevel;
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailMeta = "";
    public ObservableCollection<AlarmFieldVm> DetailFields { get; } = new();
    [ObservableProperty] private string? _detailTip;
    [ObservableProperty] private AlarmSparkData? _spark;
    public ObservableCollection<AlarmRelVm> Related { get; } = new();
    [ObservableProperty] private bool _hasRelated;
    [ObservableProperty] private bool _canAck;
    [ObservableProperty] private string? _ackReason;
    [ObservableProperty] private bool _hasCh;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private string _stopLabel = "停止";
    [ObservableProperty] private string? _stopReason;

    /// <summary>选中的一行（样稿 ALP.sel）。</summary>
    public AlarmLogRow? Current { get; private set; }

    // ———————————————— 刷新 ————————————————

    public override void Refresh(IFleetView fleet)
    {
        IsNarrow = !_shell.Layout.IsWide;
        _all = AlarmLog.Build(Sources(), HomeTexts.AlarmTips.Keys);
        Rebuild();
    }

    /// <summary>各台设备的数据（报警簿全部条目 + 事件簿 + 读不出数值时的当前读数）。</summary>
    private IEnumerable<AlarmLogSource> Sources()
    {
        foreach (var d in _shell.Fleet.Devices)
        {
            var src = d.Source;
            yield return new AlarmLogSource(d.K, d.Alias, d.ModelLabel, src.AlarmBook.All, src.EventBook.Items, a => ReadingOf(d, a));
        }
    }

    /// <summary>
    /// 报警文字里没有读数时（演示、HMI 的「升温偏慢」等）：还在报警的反应釜报警用当前读数 —— 压力类 = 压力 + 压力报警上限，超温 = 温度 + 超温上限，
    /// 其余 = 温度 + 设定。已解除的不写（当前读数不是报警时的读数）。
    /// </summary>
    public static AlarmReading? ReadingOf(DeviceVm d, AlarmEntry a)
    {
        if (!a.Active || a.Ch is not int n || d.Reactor(n) is not { } r) return null;
        var plant = d.Source.Plant;
        if (a.Code == AlarmCodes.PressureHigh || a.Text.Contains("压", StringComparison.Ordinal))
            return r.PressureMPa is double p ? new AlarmReading(PressureDisplay.FixedU(p, 2),
                plant.OverPressureMPa is double l ? "上限 " + PressureDisplay.FixedU(l, 2) : null) : null;
        if (r.TempC is not double t) return null;
        string val = t.ToString("0.0", CultureInfo.InvariantCulture) + " °C";
        if (a.Code == AlarmCodes.TempHigh)
            return new AlarmReading(val, plant.OverTempC is double lt ? "上限 " + lt.ToString("0", CultureInfo.InvariantCulture) + " °C" : null);
        return new AlarmReading(val, r.SpC is double sp ? "设定 " + sp.ToString("0.0", CultureInfo.InvariantCulture) + " °C" : null);
    }

    private void Rebuild()
    {
        if (_resetting) return;
        var now = _shell.Fleet.Now;
        int k = _shell.Nav.DeviceK;
        var dev = k > 0 ? _shell.Fleet.Device(k) : null;
        IsAllDevices = k == 0;
        ScopeText = dev?.Alias ?? "全部设备";
        var kp = AlarmLog.Kpis(_all, k, now);
        (KpiActive, KpiUnacked, KpiWarn, KpiToday) = (kp.ActiveHi, kp.Unacked, kp.ActiveLo, kp.Today);
        KpiActiveErr = kp.ActiveHi > 0;

        var rows = AlarmLog.Filter(_all, k, Filter, now);
        // 选中：保持；没选过 / 不在筛选结果里 → 最新的未确认报警（样稿默认 A0 = 1号机 RV4 超压），没有就第一行
        var cur = rows.FirstOrDefault(r => r.Key == _sel)
                  ?? rows.FirstOrDefault(r => r.Unacked && r.Level == AlarmLogLevel.Hi && r.State == AlarmLogState.Active)
                  ?? rows.FirstOrDefault(r => r.Unacked)
                  ?? rows.FirstOrDefault();
        _sel = cur?.Key;
        SyncRows(rows, now);
        IsEmpty = rows.Count == 0;
        ShowDetail(cur, now);
        RefreshTools();
    }

    private void SyncRows(IReadOnlyList<AlarmLogRow> rows, DateTime now)
    {
        // 就地更新：同一身份的行留着（滚动位置、悬停不丢），多的删、新的插
        var want = rows.Select(r => r.Key).ToHashSet();
        for (int i = Rows.Count - 1; i >= 0; i--) if (!want.Contains(Rows[i].Key)) Rows.RemoveAt(i);
        for (int i = 0; i < rows.Count; i++)
        {
            int at = -1;
            for (int j = i; j < Rows.Count; j++) if (Rows[j].Key == rows[i].Key) { at = j; break; }
            AlarmRowVm vm;
            if (at < 0) { vm = new AlarmRowVm(rows[i].Key, PickCommand, AckOneCommand); Rows.Insert(i, vm); }
            else { vm = Rows[at]; if (at != i) Rows.Move(at, i); }
            vm.Apply(rows[i], now, IsAllDevices, rows[i].Key == _sel, AckReasonOf(rows[i]));
        }
    }

    private string? AckReasonOf(AlarmLogRow r)
    {
        if (!r.Unacked || r.AlarmId is null || _shell.Fleet.Device(r.K) is not { } d) return null;
        if (r.State == AlarmLogState.Cleared) return "报警已解除 · 本机只能确认当前的报警";
        return Nz(d.Actions.Why(DeviceAction.AlarmAck, new ActionPayload(Id: r.AlarmId)));
    }

    private void ShowDetail(AlarmLogRow? x, DateTime now)
    {
        Current = x;
        HasDetail = x != null;
        DetailFields.Clear();
        Related.Clear();
        if (x == null)
        {
            DetailSub = "";
            Spark = null;
            HasRelated = false;
            CanAck = HasCh = CanStop = false;
            return;
        }
        var d = _shell.Fleet.Device(x.K);
        DetailSub = x.Device + " · " + x.Src;
        DetailLevel = x.Level;
        DetailTitle = x.Title;
        DetailMeta = x.LevelWord + " · " + x.Device + " " + x.Model + " · " + x.Src;
        DetailFields.Add(new AlarmFieldVm("发生", AlarmLog.WhenText(x.At, now)));
        if (x.IsAlarm)
        {
            DetailFields.Add(x.State == AlarmLogState.Active ? new AlarmFieldVm("状态", "活动中", Bold: true, Red: true)
                : new AlarmFieldVm("状态", x.State == AlarmLogState.Logged ? "已记录（报警簿里没有这一条）" : AlarmLog.StateText(x).Replace("已解除 ", "已解除 · ", StringComparison.Ordinal)));
            if (x.State != AlarmLogState.Logged) DetailFields.Add(new AlarmFieldVm("确认", AlarmLog.AckText(x)));
        }
        if (x.Val != null) DetailFields.Add(new AlarmFieldVm("值 / 限值", x.Val, Bold: true, Tail: x.Lim is { Length: > 0 } l ? " · " + l : ""));
        if (x.Act.Length > 0) DetailFields.Add(new AlarmFieldVm(x.IsAlarm ? "自动动作" : "说明", x.Act));
        DetailTip = x.IsAlarm && HomeTexts.AlarmTips.TryGetValue(x.Title, out var tip) ? tip : null;
        Spark = x.IsAlarm && x.N is int && d != null ? SparkOf(d, x) : null;

        foreach (var y in _all.Where(y => y.Key != x.Key && y.K == x.K && (x.N is not int n || y.N == n)).Take(4))
            Related.Add(new AlarmRelVm(y.Key, y.Level, y.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture), y.Src + " · " + y.Title, PickCommand));
        HasRelated = Related.Count > 0;

        CanAck = x.Unacked;
        AckReason = AckReasonOf(x);
        HasCh = x.N is int;
        StopLabel = x.N is int sn ? "停止 RV" + sn.ToString(CultureInfo.InvariantCulture) : "停止";
        CanStop = x.N is int && x.State == AlarmLogState.Active;
        StopReason = x.N is int stn && d != null ? Nz(d.Actions.Why(DeviceAction.Stop, ActionPayload.Ch(stn))) : null;
    }

    /// <summary>
    /// 报警前后的小趋势（样稿 alSpark）：报警时刻前 15 min 到后 15 min（共 30 min；碰到现在往前挪、碰到缓存的开头往后挪），压力类画压力、其余画温度；
    /// 上限 = 设备的报警上限（超温 / 超压）。本机历史缓存里没有那段时间（比连上更早、超过 240 min）时不画。
    /// </summary>
    public static AlarmSparkData? SparkOf(DeviceVm d, AlarmLogRow x)
    {
        if (x.N is not int n || d.Source.History.Channel(n) is not { Count: > 1 } h) return null;
        var now = d.Source.History.Now;
        var first = h[0].T.ToLocalTime();
        if (x.At < first || x.At > now) return null;
        var a = x.At.AddMinutes(-15);
        var b = a.AddMinutes(30);
        if (b > now) { b = now; a = b.AddMinutes(-30); }
        if (a < first) { a = first; b = a.AddMinutes(30); if (b > now) b = now; }
        bool isP = x.IsPressure;
        var buf = Array.Empty<ChartPoint>();
        int cnt = CurveStats.CopyRange(h, isP ? HistoryField.PressureMPa : HistoryField.TempC, a, b, ref buf);
        if (cnt < 2) return null;
        var plant = d.Source.Plant;
        double? lim = isP ? PressureDisplay.ToUnit(plant.OverPressureMPa) : plant.OverTempC;
        // 压力按设置的单位画（点、上限一起换算；刻度 MPa / bar 1 位小数、psi 整数）
        if (isP && PressureDisplay.Primary != PressureUnit.MPa)
            for (int i = 0; i < cnt; i++) buf[i] = new ChartPoint(buf[i].T, PressureDisplay.ToUnit(buf[i].V));
        string title = "RV" + n.ToString(CultureInfo.InvariantCulture) + (isP ? " " + PressureDisplay.LabelParen("压力") : " 温度 (°C)") + " · 报警前后";
        return new AlarmSparkData(buf, cnt, ChartTime.Ms(a), ChartTime.Ms(b), lim, ChartTime.Ms(x.At), isP, title,
            new[] { a.ToString("HH:mm", CultureInfo.InvariantCulture), a.AddTicks((b - a).Ticks / 2).ToString("HH:mm", CultureInfo.InvariantCulture), b.ToString("HH:mm", CultureInfo.InvariantCulture) },
            RunRowText.ChColor(n), isP && PressureDisplay.Primary != PressureUnit.Psi ? 1 : 0);
    }

    private void RefreshTools()
    {
        var x = Current;
        _ackSel.Reason = x == null ? "先选一条报警" : !x.IsAlarm ? "事件不需要确认" : x.State == AlarmLogState.Logged ? "报警簿里没有这一条" : x.Acked ? "已确认" : AckReasonOf(x);
        int ua = _shell.Fleet.Devices.Sum(d => d.UnackedAlarms);
        _ackAll.Label = ua > 0 ? "全部确认（" + ua.ToString(CultureInfo.InvariantCulture) + "）" : "全部确认";
        _ackAll.Reason = ua > 0 ? null : "没有未确认的报警";
        _export.Reason = null;
    }

    // ———————————————— 动作 ————————————————

    private void Pick(string? key)
    {
        if (key == null || key == _sel) return;
        _sel = key;
        Rebuild();
    }

    /// <summary>选中某一行（测试 / 地址参数）。</summary>
    public void Select(string key) => Pick(key);

    private void Ack(AlarmLogRow r)
    {
        if (r.AlarmId is null || _shell.Fleet.Device(r.K) is not { } d) return;
        if (AckReasonOf(r) is { } why) { _shell.Toast(why, ToastKind.Info); return; }
        var vm = d.Alarms.FirstOrDefault(a => a.Id == r.AlarmId);
        if (vm != null && vm.AckCommand.CanExecute(null)) vm.AckCommand.Execute(null);
        else _ = d.Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: r.AlarmId));
    }

    private void AckSelected()
    {
        if (Current is { } r) Ack(r);
    }

    private void AckAll()
    {
        foreach (var d in _shell.Fleet.Devices)
            foreach (var a in d.Alarms.Where(a => !a.Acked).ToList())
                if (a.AckReason == null && a.AckCommand.CanExecute(null)) a.AckCommand.Execute(null);
    }

    /// <summary>定位到通道：主页选中这一路、打开检查器、回到运行表（同报警弹窗的「定位」）。</summary>
    private void Locate()
    {
        if (Current is not { N: int n } x) return;
        _shell.Go(AppPage.Home);
        _shell.OpenChannel(x.K, n);
        _shell.OpenPage<HomePageVm>()?.ShowRunTable();
    }

    /// <summary>查看曲线：曲线页 · 这台设备 · 勾上这一路 · 时间范围 1 h（样稿 al-cv）。</summary>
    private void ToCurve()
    {
        if (Current is not { N: int n } x) return;
        _shell.Go(AppPage.Curves);
        _shell.SelectChannel(x.K, n);
        _shell.OpenPage<CurvePageVm>()?.Focus(x.K, n);
    }

    /// <summary>停止 RVn：先弹停止通道的确认框（2026-11-18 用户：停止一律先确认）。</summary>
    private void StopCh()
    {
        if (Current is not { N: int n } x || _shell.Fleet.Device(x.K) is null) return;
        _shell.OpenDialog("stopch", x.K.ToString(CultureInfo.InvariantCulture) + ":" + n.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>导出当前筛选的行（UTF-8 带 BOM，Excel 直接打开不乱码）。</summary>
    private void ExportCsv()
    {
        var now = _shell.Fleet.Now;
        var rows = AlarmLog.Filter(_all, _shell.Nav.DeviceK, Filter, now);
        try
        {
            var dir = ExportDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "alarms-" + (_shell.Nav.DeviceK > 0 ? ScopeText + "-" : "") + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
            File.WriteAllText(path, AlarmLog.Csv(rows), new UTF8Encoding(true));
            _shell.Toast("已导出 " + rows.Count.ToString(CultureInfo.InvariantCulture) + " 条报警与事件 → " + path, ToastKind.Ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.Toast("导出失败：" + ex.Message, ToastKind.Err);
        }
    }

    // ———————————————— 地址参数 / 还原 ————————————————

    /// <summary>截图参数：alsel=k:报警 Id / ek:事件文字的开头（选中）、alr=day|d7、alv=all|hi|lo|ev、als=all|ua|act|clr、alq=搜索。</summary>
    public override void ApplyParams(Func<string, string?> param)
    {
        _resetting = true;
        if (param("alr") is { Length: > 0 } r) RangeKey = r;
        if (param("alv") is { Length: > 0 } v) LevelKey = v;
        if (param("als") is { Length: > 0 } s) StateKey = s;
        if (param("alq") is { } q) Query = q;
        _resetting = false;
        Refresh(_shell.Fleet);
        if (param("alsel") is { Length: > 0 } sel && Rows.FirstOrDefault(x => x.Key.EndsWith(":" + sel, StringComparison.Ordinal) || x.Title.StartsWith(sel, StringComparison.Ordinal)) is { } row)
            Pick(row.Key);
    }

    public override void ResetView()
    {
        _resetting = true;
        RangeKey = _prevRange = "day";
        LevelKey = "all";
        StateKey = "all";
        Query = "";
        _sel = null;
        _resetting = false;
    }

    private static string? Nz(string s) => s.Length == 0 ? null : s;
}

/// <summary>详情表的一行（样稿 .alfr：标签 13 t-2 · 值 14 t-1；活动中红色粗体；值 / 限值「<b>3.55 MPa</b> · 上限 3.50 MPa」）。</summary>
public sealed record AlarmFieldVm(string Label, string Text, bool Bold = false, bool Red = false, string Tail = "");

/// <summary>相关事件的一行（样稿 .alrel：图标 · 时刻 · 来源 · 标题；点 = 选中）。</summary>
public sealed record AlarmRelVm(string Key, AlarmLogLevel Level, string Time, string Text, ICommand PickCommand);

/// <summary>报警前后的小趋势（样稿 alSpark）：点、时间窗、上限、报警时刻、是不是压力、标题、横轴三个时刻、线色（通道识别色）、纵轴刻度的小数位。
/// 压力的点和上限已经是设置的压力单位。</summary>
public sealed record AlarmSparkData(ChartPoint[] Points, int Count, double T0, double T1, double? Limit, double AlarmT, bool IsPressure, string Title,
    IReadOnlyList<string> Ticks, Avalonia.Media.Color Color, int Dec = 0);
