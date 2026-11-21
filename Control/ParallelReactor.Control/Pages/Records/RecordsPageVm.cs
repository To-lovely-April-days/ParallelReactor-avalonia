using ParallelReactor.Control.Core.Format;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Controls.Records;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Records;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Records;

/// <summary>
/// 记录页（融合版 F7，样稿 PAGES.rec）：三块面板 ——
/// <list type="bullet">
/// <item><b>运行记录</b>（左，跨两行）：全部设备（或设备下拉选的那一台）的会话，按日期分组（今天 / 昨天 / 更早），筛选「全部 / 近 7 天」、搜索（名字 / 会话号 / 程序）。</item>
/// <item><b>8 路对比</b>（右上）：会话名 + 状态 + 说明 · 查看曲线 · 用这组参数再做一次；KPI（平均吸气 / SD / RSD / 最高 / 最低 / 报警；保压类：判定 / 测试压力 / 判据 / 最大压降 / 时长）；
///   叠加曲线（吸气量 / 温度 / 压力，平均线，选中一路加粗 + 面积，悬停读数）+ 结果表（统计行）。没运行的通道不计入、写出名字。</item>
/// <item><b>标记与事件 / 数据文件</b>（右下）页签。</item>
/// </list>
/// 数据见 <see cref="RecordsStore"/>；结果在 PC 上按数据算（Core 的 <see cref="RecCompute"/>）。地址参数（截图 / 演示）：rc=会话号、rcch=n、rcm=G|T|P、rct=mk|fl、rcq=搜索。
/// 「再做一次」的交接见 <see cref="RedoRequest"/>。
/// </summary>
public sealed partial class RecordsPageVm : PageVm
{
    private readonly ShellVm _shell;
    private readonly RibbonTool _new, _end, _mark, _down, _report, _folder;
    private readonly IReadOnlyList<RibbonGroup> _tools;
    private IReadOnlyList<RecSession> _all = Array.Empty<RecSession>();
    private RecSession? _sel;
    private RecSeriesSet? _set;
    private RecCompare? _cmp;
    private string _listKey = "";

    public RecordsPageVm(ShellVm shell) : base(AppPage.Records)
    {
        _shell = shell;
        Store = new RecordsStore(shell.Fleet);
        Store.Changed += () => { if (ReferenceEquals(_shell.Page, this)) Refresh(_shell.Fleet); };
        _new = new RibbonTool("rec", "新建会话", NewSession, "开始新的记录会话");
        _end = new RibbonTool("stop", "结束会话", EndSession, "结束所选会话");
        _mark = new RibbonTool("flag", "打标记", Mark, "在所选会话里打标记");
        _down = new RibbonTool("download", "下载数据", () => _ = DownloadAsync(), "把这次会话的运行记录（CSV）存到本机数据目录");
        _report = new RibbonTool("report", "生成报告", () => _ = ReportAsync(), "导出 8 路对比结果（CSV：每路结果 + 统计）");
        _folder = new RibbonTool("folder", "打开数据目录", OpenFolder, "打开本机数据目录（下载的数据、导出的报告）");
        _tools = new[] { new RibbonGroup("会话", new[] { _new, _end, _mark }), new RibbonGroup("导出", new[] { _down, _report, _folder }) };
        PickModeCommand = new RelayCommand<string>(m => { if (m != null) ModeKey = m; });
        CurvesCommand = new RelayCommand(ViewCurves);
        RedoCommand = new RelayCommand(Redo);
        TabCommand = new RelayCommand<string>(t => { if (t != null) Tab = t; });
    }

    public RecordsStore Store { get; }

    public override IReadOnlyList<RibbonGroup> Tools => _tools;

    /// <summary>重的页面：看不见时不跟着每秒刷新（切回来时外壳先刷新一次）。</summary>
    public override bool RefreshWhenHidden => false;

    // ———————————————————— 列表 ————————————————————

    public ObservableCollection<RecListItemVm> Items { get; } = new();
    [ObservableProperty] private string _listSub = "";
    [ObservableProperty] private string _query = "";
    /// <summary>筛选：all = 全部，7d = 近 7 天。</summary>
    [ObservableProperty] private string _range = "all";
    [ObservableProperty] private bool _listEmpty;
    [ObservableProperty] private string _listStatus = "";

    partial void OnQueryChanged(string value) => Refresh(_shell.Fleet);

    partial void OnRangeChanged(string value)
    {
        if (value == "custom")
        {
            _shell.Toast("自定义时间段：选择起止日期（后续版本）", ToastKind.Info);
            Range = "all";
            return;
        }
        Refresh(_shell.Fleet);
    }

    /// <summary>选中的会话号。</summary>
    [ObservableProperty] private string? _selId;

    private void Pick(string id)
    {
        if (id == SelId) return;
        SelId = id;
        Ch = null;
        Refresh(_shell.Fleet);
    }

    // ———————————————————— 对比面板 ————————————————————

    [ObservableProperty] private bool _hasSel;
    [ObservableProperty] private string _cmpTitle = "8 路对比";
    [ObservableProperty] private string _cmpId = "";
    [ObservableProperty] private string _sesName = "";
    [ObservableProperty] private string _sesTag = "";
    [ObservableProperty] private BadgeTone _sesTone;
    [ObservableProperty] private bool _sesDot;
    [ObservableProperty] private string _sesMeta = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private bool _hasNote;
    [ObservableProperty] private string? _curvesReason;
    [ObservableProperty] private string _curvesTip = "在曲线页看这台设备的完整曲线";
    [ObservableProperty] private string _redoLabel = "用这组参数再做一次";
    [ObservableProperty] private string? _redoReason;
    [ObservableProperty] private string _redoTip = "";
    [ObservableProperty] private bool _isHold;
    [ObservableProperty] private int _kpiCount = 6;
    public ObservableCollection<RecKpiVm> Kpis { get; } = new();

    /// <summary>曲线模式：G 吸气量 / T 温度 / P 压力（样稿 RC.m）。</summary>
    [ObservableProperty] private string _modeKey = "G";
    [ObservableProperty] private RecChartData? _chart;
    [ObservableProperty] private string _holdChartTitle = "各路保压曲线 · 平衡后（第 10–40 min 判读）";
    [ObservableProperty] private bool _showMean = true;
    /// <summary>数据还没准备好（下载中 / 失败 / 设备未连接）的说明；空 = 有数据。</summary>
    [ObservableProperty] private string _seriesStatus = "";
    [ObservableProperty] private bool _hasSeriesStatus;

    partial void OnModeKeyChanged(string value) => Recompute();

    /// <summary>选中的通道（样稿 RC.ch；null = 不选）。</summary>
    [ObservableProperty] private int? _ch;

    private bool _quietCh;

    /// <summary>改选中的通道但不重算（随后的刷新会算）。</summary>
    private void SetChQuiet(int? n)
    {
        _quietCh = true;
        try { Ch = n; } finally { _quietCh = false; }
    }

    partial void OnChChanged(int? value)
    {
        if (_quietCh) return;
        foreach (var r in Rows) r.IsOn = r.N == value;
        Recompute(chartOnly: true);
    }

    public ObservableCollection<RecRowVm> Rows { get; } = new();
    /// <summary>结果表的列宽（表头和行共用）。</summary>
    public RecColumns Cols { get; } = new();
    /// <summary>左列（运行记录）宽：380，窄屏 300（样稿 REC-V2c）。</summary>
    [ObservableProperty] private Avalonia.Controls.GridLength _listW = new(380);
    /// <summary>下方面板高：236，矮屏 168。</summary>
    [ObservableProperty] private Avalonia.Controls.GridLength _bottomH = new(236);
    /// <summary>曲线 : 结果表 = 1 : 1，窄屏 0.86 : 1.14。</summary>
    [ObservableProperty] private Avalonia.Controls.GridLength _chartW = new(1, Avalonia.Controls.GridUnitType.Star);
    [ObservableProperty] private Avalonia.Controls.GridLength _tableW = new(1, Avalonia.Controls.GridUnitType.Star);
    [ObservableProperty] private Avalonia.Controls.GridLength _cmpGap = new(14);
    [ObservableProperty] private string _footMean = "";
    [ObservableProperty] private string _footLine = "";
    [ObservableProperty] private string _footSkip = "";

    public ICommand PickModeCommand { get; }
    public ICommand CurvesCommand { get; }
    public ICommand RedoCommand { get; }

    // ———————————————————— 下方页签 ————————————————————

    /// <summary>mk = 标记与事件，fl = 数据文件。</summary>
    [ObservableProperty] private string _tab = "mk";
    [ObservableProperty] private bool _isMarks = true;
    [ObservableProperty] private string _eventCount = "0";
    [ObservableProperty] private string _fileCount = "0";
    [ObservableProperty] private bool _eventsEmpty;
    public ObservableCollection<RecEventVm> Events { get; } = new();
    public ObservableCollection<RecFileVm> Files { get; } = new();
    public ICommand TabCommand { get; }

    partial void OnTabChanged(string value) => IsMarks = value != "fl";

    // ———————————————————— 外框 ————————————————————

    [ObservableProperty] private bool _isNarrow;
    [ObservableProperty] private bool _isShort;

    /// <summary>上一次「再做一次」的请求（目标页面还没接住时，程序页 / 性能测试页可以从这里取）。</summary>
    public RedoRequest? LastRedo { get; private set; }

    /// <summary>「再做一次」发出请求时（程序页 / 主会话可以订阅，见 <see cref="RedoRequest"/>）。</summary>
    public event EventHandler<RedoRequest>? RedoRequested;

    private DeviceVm? Dev(int k) => _shell.Fleet.Device(k);

    /// <summary>
    /// 保压类的判据（融合版 F8）：这台设备设置里的泄漏率阈值 psi/h（同性能测试页的泄漏测试，<see cref="Services.LeakTestInfo"/>）；
    /// 设备不在 / 不知道时 HMI 的出厂值 2 psi/h。
    /// </summary>
    public double HoldLimitOf(int k) => Dev(k)?.Source.Plant.Leak?.RatePsiH is double r && r > 0 ? r : RecCompute.HoldLimitPsiH;

    /// <summary>当前会话的对比结果（测试用）。</summary>
    public RecCompare? Compare => _cmp;

    /// <summary>当前选中的会话（测试用）。</summary>
    public RecSession? Selected => _sel;

    // ======================================================================

    /// <summary>地址参数（截图 / 演示路由；只在给了 page= 时调）：rc、rcch、rcm、rct、rcq。没给的回到默认。</summary>
    public override void ApplyParams(Func<string, string?> x)
    {
        Query = x("rcq") ?? "";
        Range = "all";
        SelId = x("rc");
        SetChQuiet(int.TryParse(x("rcch"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null);
        ModeKey = x("rcm") is "T" or "P" ? x("rcm")! : "G";
        Tab = x("rct") == "fl" ? "fl" : "mk";
        _listKey = "";
    }

    public override void Refresh(IFleetView fleet)
    {
        IsNarrow = !_shell.Layout.IsWide;
        IsShort = _shell.Layout.IsShort;
        ListW = new Avalonia.Controls.GridLength(IsNarrow ? 300 : 380);
        BottomH = new Avalonia.Controls.GridLength(IsShort ? 168 : 236);
        ChartW = new Avalonia.Controls.GridLength(IsNarrow ? .86 : 1, Avalonia.Controls.GridUnitType.Star);
        TableW = new Avalonia.Controls.GridLength(IsNarrow ? 1.14 : 1, Avalonia.Controls.GridUnitType.Star);
        CmpGap = new Avalonia.Controls.GridLength(IsNarrow ? 10 : 14);
        var now = _shell.Fleet.Now;
        _all = Store.Sessions();
        int k = _shell.Nav.DeviceK;
        var q = Query.Trim();
        var list = _all.Where(s => (k == 0 || s.K == k)
                && (q.Length == 0 || (s.Name + s.Id + s.Program).Contains(q, StringComparison.OrdinalIgnoreCase))
                && (Range != "7d" || (now.Date - s.Start.Date).TotalDays < 7))
            .ToList();
        var alias = k > 0 ? Dev(k)?.Alias ?? "" : "全部设备";
        ListSub = alias + " · " + list.Count.ToString(CultureInfo.InvariantCulture) + " 条";
        ListEmpty = list.Count == 0;
        ListStatus = string.Join(" · ", _shell.Fleet.Devices.Where(d => k == 0 || d.K == k).Select(d => Store.DeviceStatus(d.K) is { } st ? d.Alias + " " + st : null).Where(x => x != null));
        if (ListStatus.Length == 0 && ListEmpty) ListStatus = q.Length > 0 ? "没有找到「" + q + "」" : "没有记录";

        _sel = list.FirstOrDefault(s => s.Id == SelId) ?? list.FirstOrDefault();
        if (_sel?.Id != SelId) { SelId = _sel?.Id; SetChQuiet(null); }
        SyncList(list, now);
        RefreshTools();
        RefreshSelection(now);
    }

    private void SyncList(IReadOnlyList<RecSession> list, DateTime now)
    {
        string key = string.Join("|", list.Select(s => s.K.ToString(CultureInfo.InvariantCulture) + ":" + s.Id + ":" + RecTexts.DayGroup(s.Start, now)));
        if (key != _listKey)
        {
            Items.Clear();
            string day = "";
            foreach (var s in list)
            {
                var g = RecTexts.DayGroup(s.Start, now);
                if (g != day) { Items.Add(new RecListItemVm(g)); day = g; }
                Items.Add(new RecListItemVm(s.Id, Pick));
            }
            _listKey = key;
        }
        int i = 0;
        foreach (var it in Items)
        {
            if (it.IsGroup) continue;
            var s = list[i++];
            it.Apply(s, Dev(s.K)?.Alias ?? "—", now, !IsNarrow);
            it.IsOn = s.Id == SelId;
        }
    }

    private void RefreshSelection(DateTime now)
    {
        var s = _sel;
        HasSel = s != null;
        if (s == null)
        {
            _set = null; _cmp = null; Chart = null; Rows.Clear(); Kpis.Clear(); Events.Clear(); Files.Clear();
            return;
        }
        var d = Dev(s.K);
        IsHold = s.Hold != RecHold.None;
        Cols.Set(IsNarrow, IsHold);
        CmpTitle = IsHold ? "各路压降对比" : "8 路对比";
        CmpId = s.Id;
        SesName = s.Name;
        var (tag, tone) = RecTexts.Tag(s);
        SesTag = tag;
        SesTone = tone switch { RecTagTone.Run => BadgeTone.Ok, RecTagTone.Warn => BadgeTone.Warn, _ => BadgeTone.None };
        SesDot = tone == RecTagTone.Run;
        var meta = new List<string> { d?.Alias ?? "—", RecTexts.ProgramText(s) };
        if (s.Operator is { Length: > 0 } op && op != "—") meta.Add(op);
        meta.Add(RecTexts.Day(s.Start) + " " + RecTexts.Hm(s.Start) + " 起");
        meta.Add(RecTexts.Dur(s.Duration(now)));
        SesMeta = string.Join(" · ", meta.Where(m => m != "—"));
        Note = s.Note ?? s.Warn ?? "";
        HasNote = Note.Length > 0;
        CurvesReason = s.Active ? null : "历史会话的完整曲线：下载数据后查看（第二阶段）";
        RefreshRedo(s, d);

        _set = Store.Series(s, out var status);
        SeriesStatus = _set == null ? status ?? "" : "";
        HasSeriesStatus = SeriesStatus.Length > 0;
        Recompute();

        var evs = Store.Events(s);
        EventCount = evs.Count.ToString(CultureInfo.InvariantCulture);
        EventsEmpty = evs.Count == 0;
        var evVms = evs.Select(e => new RecEventVm(e.At.ToString("HH:mm", CultureInfo.InvariantCulture), e.Src, e.Text, e.Kind)).ToList();
        if (!Events.SequenceEqual(evVms)) { Events.Clear(); foreach (var e in evVms) Events.Add(e); }
        var files = Store.Files(s);
        FileCount = files.Count.ToString(CultureInfo.InvariantCulture);
        if (Files.Count != files.Count || !Files.Select(f => f.File + f.Size).SequenceEqual(files.Select(f => f.File + f.Size)))
        {
            Files.Clear();
            foreach (var f in files)
            {
                var ff = f;
                string? why = f.Type == null ? "演示数据：没有文件可下载" : null;
                Files.Add(new RecFileVm(f.File, f.What, f.Size, new RelayCommand(() => _ = DownloadFileAsync(s, ff)), why));
            }
        }
    }

    private void RefreshRedo(RecSession s, DeviceVm? d)
    {
        if (s.Hold != RecHold.None)
        {
            RedoLabel = "再做一次泄漏测试";
            RedoReason = d == null ? "设备不在了" : null;
            RedoTip = "打开泄漏测试 · " + (d?.Alias ?? "") + " · " + s.Channels.Count.ToString(CultureInfo.InvariantCulture) + " 路";
            return;
        }
        RedoLabel = "用这组参数再做一次";
        var prog = d == null ? null : FindProgram(d, s.Program);
        RedoReason = d == null ? "设备不在了" : !s.HasProgram ? "这次会话没有记下程序名" : prog == null ? "程序库里找不到这个程序" : null;
        RedoTip = "载入「" + s.Program + "」并预选这 " + s.Channels.Count.ToString(CultureInfo.InvariantCulture) + " 路，装好样品后下发";
    }

    /// <summary>按名字在设备的程序库里找程序（每路的程序库合起来）。</summary>
    private static Core.Programs.ProgramDef? FindProgram(DeviceVm d, string name) =>
        d.Reactors.Where(r => r.Installed).SelectMany(r => d.Source.Library(r.N)).FirstOrDefault(p => p.Name == name);

    /// <summary>重新算对比、KPI、结果表和曲线（数据、模式、选中的通道变了）。</summary>
    private void Recompute(bool chartOnly = false)
    {
        var s = _sel;
        if (s == null) return;
        if (_set == null) { _cmp = null; Chart = null; if (!chartOnly) { Rows.Clear(); FillKpis(s, null); FootMean = FootLine = FootSkip = ""; } return; }
        if (!chartOnly || _cmp == null) _cmp = RecCompute.Compare(s, _set, HoldLimitOf(s.K));
        var c = _cmp;
        if (!chartOnly)
        {
            FillKpis(s, c);
            FillRows(c);
        }
        BuildChart(s, c);
    }

    private void FillKpis(RecSession s, RecCompare? c)
    {
        bool hold = s.Hold != RecHold.None;
        int count = hold ? 5 : 6;
        KpiCount = count;
        while (Kpis.Count > count) Kpis.RemoveAt(Kpis.Count - 1);
        while (Kpis.Count < count) Kpis.Add(new RecKpiVm());
        var inv = CultureInfo.InvariantCulture;
        if (hold)
        {
            var rows = c?.Rows ?? Array.Empty<RecRow>();
            int ok = rows.Count(r => r.Ok && r.Used);
            Kpis[0].Set("判定", c == null ? "—" : ok.ToString(inv) + "/" + rows.Count.ToString(inv), "路通过", c == null ? RecKpiTone.None : ok == rows.Count ? RecKpiTone.Ok : RecKpiTone.Err);
            // 测试压力按设置的压力单位（MPa / bar 2 位、psi 1 位）；说明里「约 50 psi」在单位已经是 psi 时不写；压降 / 判据照旧 psi/h（HMI 的泄漏率单位）
            Kpis[1].Set("测试压力", c?.TestMPa is double mp ? PressureDisplay.Fixed(mp, 2) : "—", PressureDisplay.Unit,
                sub: c?.TestMPa is double mp2 ? PressureDisplay.Primary == PressureUnit.Psi ? "惰性气体" : RecTexts.HoldGasLine(mp2) : "");
            Kpis[2].Set("判据", "≤ " + (c?.HoldLimit ?? HoldLimitOf(s.K)).ToString("0.0##", inv), "psi/h", sub: "第 10–40 min 压降");
            var worst = rows.Where(r => r.Used).OrderByDescending(r => r.Drop).FirstOrDefault();
            Kpis[3].Set("最大压降", worst == null ? "—" : RecTexts.F2(worst.Drop), "psi/h", sub: worst == null ? "" : "RV" + worst.N.ToString(inv));
            Kpis[4].Set("时长", RecTexts.Dur(s.Duration(_shell.Fleet.Now)));
            return;
        }
        var st = c?.Stats ?? RecStats.Empty;
        var used = c?.Rows.Where(r => r.Used).ToList() ?? new List<RecRow>();
        var best = used.OrderByDescending(r => r.G).FirstOrDefault();
        var low = used.OrderBy(r => r.G).FirstOrDefault();
        bool live = c?.Live == true;
        Kpis[0].Set("平均吸气", st.N > 0 ? RecTexts.F1(st.Mean) : "—", "mmol", sub: c == null ? "" : RecTexts.MeanSub(st.N, live));
        Kpis[1].Set("标准差 SD", st.N > 1 ? RecTexts.F1(st.Sd) : "—", "mmol");
        bool warn = st.Rsd > RecCompute.RsdWarnPct;
        Kpis[2].Set("RSD", st.N > 1 ? RecTexts.F1(st.Rsd) : "—", "%", st.N > 1 ? warn ? RecKpiTone.Warn : RecKpiTone.Ok : RecKpiTone.None,
            st.N > 1 ? warn ? "大于 10 %，各路差异明显" : "各路一致性好" : "");
        Kpis[3].Set("最高", best != null ? RecTexts.F1(best.G) : "—", "mmol", sub: best != null ? "RV" + best.N.ToString(inv) : "");
        Kpis[4].Set("最低", low != null ? RecTexts.F1(low.G) : "—", "mmol", sub: low != null ? "RV" + low.N.ToString(inv) + (low.Live ? " · 仍在反应" : "") : "");
        Kpis[5].Set("报警", s.Alarms.ToString(inv), "", s.Alarms > 0 ? RecKpiTone.Err : RecKpiTone.None);
    }

    private void FillRows(RecCompare c)
    {
        if (Rows.Count != c.Rows.Count || !Rows.Select(r => r.N).SequenceEqual(c.Rows.Select(r => r.N)))
        {
            Rows.Clear();
            foreach (var r in c.Rows) Rows.Add(new RecRowVm(r.N, PickCh, Cols));
        }
        double maxAbs = Math.Max(1, c.Rows.Where(r => r.Used).Select(r => Math.Abs(RecCompute.Rel(r.G, c.Stats.Mean))).DefaultIfEmpty(0).Max());
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].Apply(c.Rows[i], c.Stats, maxAbs, c.IsHold);
            Rows[i].IsOn = Rows[i].N == Ch;
        }
        var st = c.Stats;
        FootMean = st.N > 0 ? RecTexts.F1(st.Mean) : "—";
        FootLine = RecTexts.StatsLine(st);
        FootSkip = IsNarrow ? "" : RecTexts.SkipLine(c.Skipped);
    }

    private void PickCh(int n) => Ch = Ch == n ? null : n;

    private void BuildChart(RecSession s, RecCompare c)
    {
        string mode = c.IsHold ? "P" : ModeKey;
        // 压力按设置的单位画（MPa 时倍数 1，与改单位之前相同）；悬停的小数位：MPa / bar 2 位（保压类 3 位）、psi 少一位
        double k = mode == "P" ? PressureUnits.PerMpa(PressureDisplay.Primary) : 1;
        Func<RecPoint, double> pick = mode switch { "T" => p => p.T, "P" => p => p.P * k, _ => p => p.G };
        int pDec = c.IsHold ? 3 : 2;
        var (title, unit, dec) = mode switch
        {
            "T" => ("温度", "°C", 1),
            "P" => ("压力", PressureDisplay.Unit, PressureDisplay.Primary == PressureUnit.Psi ? pDec - 1 : pDec),
            _ => ("吸气量", "mmol", 1),
        };
        var lines = c.Rows.Where(r => r.Used && r.Points.Count > 0).Select(r =>
        {
            var m = new double[r.Points.Count];
            var v = new double[r.Points.Count];
            for (int i = 0; i < m.Length; i++) { m[i] = r.Points[i].M; v[i] = pick(r.Points[i]); }
            return new RecChartLine(r.N, RunRowText.ChColor(r.N), r.N > 8, m, v);
        }).ToList();
        var mean = mode == "G" && !c.IsHold ? RecCompute.MeanLine(c.Rows, pick) : Array.Empty<(double, double)>();
        ShowMean = !c.IsHold;
        Chart = new RecChartData(title, unit, dec, c.IsHold ? RecCompute.HoldChartFromMin : 0, c.Span, s.Start, lines, mean, Ch, c.IsHold, k);
    }

    // ———————————————————— 工具条 ————————————————————

    private static string? Nz(string s) => string.IsNullOrEmpty(s) ? null : s;

    private void RefreshTools()
    {
        var d = _shell.Nav.DeviceK > 0 ? Dev(_shell.Nav.DeviceK) : null;
        var s = _sel;
        _new.Reason = d == null ? "先在左侧设备列表选一台设备"
            : d.SessionId != null ? d.Alias + "已有进行中的会话" : Nz(d.Actions.Why(DeviceAction.SessionStart));
        var sd = s == null ? null : Dev(s.K);
        _end.Reason = s == null ? "先选一条记录" : !s.Active ? "所选会话已结束"
            : s.By != _shell.Fleet.ClientName ? "只有创建者（" + s.By + "）或设备触屏能结束"
            : sd == null ? "设备不在了" : Nz(sd.Actions.Why(DeviceAction.SessionEnd));
        _mark.Reason = s == null ? "先选一条记录" : !s.Active ? "所选会话已结束" : sd == null ? "设备不在了" : Nz(sd.Actions.Why(DeviceAction.SessionMark));
        _down.Reason = s == null ? "先选一条记录" : null;
        _report.Reason = s == null ? "先选一条记录" : null;
    }

    private void NewSession()
    {
        if (_shell.Nav.DeviceK > 0 && Dev(_shell.Nav.DeviceK) is { } d) _ = d.Actions.RunAsync(DeviceAction.SessionStart);
    }

    private void EndSession()
    {
        if (_sel is { Active: true } s && Dev(s.K) is { } d) _ = d.Actions.RunAsync(DeviceAction.SessionEnd);
    }

    private void Mark()
    {
        if (_sel is { Active: true } s && Dev(s.K) is { } d)
            _ = d.Actions.RunAsync(DeviceAction.SessionMark, new ActionPayload(Text: "标记 " + (s.Marks + 1).ToString(CultureInfo.InvariantCulture)));
    }

    private async Task DownloadAsync()
    {
        if (_sel is not { } s) return;
        try
        {
            var path = await Store.ExportDataAsync(s).ConfigureAwait(true);
            _shell.Toast("已保存 " + s.Id + " 的数据：" + path, ToastKind.Ok);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _shell.Toast("下载数据失败：" + Fleet.DeviceActions.Describe(ex), ToastKind.Err);
        }
    }

    private async Task ReportAsync()
    {
        if (_sel is not { } s || _cmp is not { } c) { _shell.Toast("数据还没准备好", ToastKind.Info); return; }
        try
        {
            var dir = Path.Combine(Store.CacheRoot, "reports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, s.Id + "-对比.csv");
            await File.WriteAllTextAsync(path, RecExport.ResultsCsv(s, Dev(s.K)?.Alias ?? "—", c), new System.Text.UTF8Encoding(true)).ConfigureAwait(true);
            _shell.Toast("已生成 " + s.Id + " 的对比结果：" + path, ToastKind.Ok);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _shell.Toast("生成报告失败：" + ex.Message, ToastKind.Err);
        }
    }

    private async Task DownloadFileAsync(RecSession s, RecFile f)
    {
        try
        {
            var path = await Store.DownloadFileAsync(s, f).ConfigureAwait(true);
            _shell.Toast("已下载 " + f.File + "：" + path, ToastKind.Ok);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _shell.Toast("下载失败：" + Fleet.DeviceActions.Describe(ex), ToastKind.Err);
        }
    }

    /// <summary>打开本机数据目录（没有就先建）：Windows explorer、macOS open、Linux xdg-open。截图 / 测试（无界面）时只提示路径。</summary>
    private void OpenFolder()
    {
        var dir = Store.CacheRoot;
        try
        {
            Directory.CreateDirectory(dir);
            if (Environment.GetEnvironmentVariable("PR_CONTROL_HEADLESS") != "1")
            {
                var exe = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
                Process.Start(new ProcessStartInfo(exe) { ArgumentList = { dir }, UseShellExecute = false });
            }
            _shell.Toast("数据目录：" + dir, ToastKind.Info);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _shell.Toast("打不开数据目录：" + ex.Message, ToastKind.Err);
        }
    }

    // ———————————————————— 查看曲线 · 再做一次 ————————————————————

    private void ViewCurves()
    {
        if (_sel is not { Active: true } s) return;
        _shell.Go(AppPage.Curves);
        _shell.SelectDevice(s.K);
    }

    /// <summary>
    /// 「再做一次」：组一个 <see cref="RedoRequest"/>（预选 = 那次的通道里此刻空闲的），换到目标页面并选设备，交给实现了 <see cref="IRedoTarget"/> 的页面；
    /// 没接住时提示怎么手动做。
    /// </summary>
    public RedoRequest? BuildRedo()
    {
        if (_sel is not { } s || Dev(s.K) is not { } d) return null;
        bool hold = s.Hold != RecHold.None;
        var prog = hold ? null : FindProgram(d, s.Program);
        if (!hold && prog == null) return null;
        var idle = new List<int>();
        var busy = new List<int>();
        foreach (int n in s.Channels)
        {
            var r = d.Reactor(n);
            bool free = r != null && r.Installed && !r.TempNa && !RunRowText.Running(r.Own) && r.Own != StateKey.Alarm && r.Own != StateKey.Stopped;
            (free ? idle : busy).Add(n);
        }
        return new RedoRequest(hold ? RedoKind.LeakTest : RedoKind.Program, s.K, s.Id, prog?.Name, prog?.Id, idle, busy);
    }

    private void Redo()
    {
        var req = BuildRedo();
        if (req == null) return;
        LastRedo = req;
        RedoRequested?.Invoke(this, req);
        _shell.Go(req.Page);
        _shell.SelectDevice(req.DeviceK);
        var alias = Dev(req.DeviceK)?.Alias ?? "";
        string? toast = null;
        bool ok = _shell.Page is IRedoTarget t && t.AcceptRedo(req, out toast);
        string chs = req.Channels.Count == 0 ? "没有空闲的通道" : string.Join("、", req.Channels.Select(n => "RV" + n.ToString(CultureInfo.InvariantCulture)));
        if (req.Kind == RedoKind.LeakTest)
            _shell.Toast(toast ?? (ok ? "已打开泄漏测试 · " + alias + " · " + req.Channels.Count.ToString(CultureInfo.InvariantCulture) + " 路"
                : "请在「泄漏测试」里对 " + alias + " 的 " + chs + " 开始测试"), ToastKind.Info);
        else
            _shell.Toast(toast ?? (ok ? "已载入「" + req.ProgramName + "」· 预选 " + req.Channels.Count.ToString(CultureInfo.InvariantCulture) + " 路（正在运行的通道不能选）· 装好样品后点「下发并运行」"
                : "请在程序页载入「" + req.ProgramName + "」，选 " + chs + "（正在运行的通道不能选）"), ToastKind.Info);
    }
}
