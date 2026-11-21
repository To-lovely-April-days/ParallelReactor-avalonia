using ParallelReactor.Control.Core.Format;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Alarms;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Curves;

/// <summary>
/// 曲线页（融合版 F5，样稿 PAGES.curve）：一台设备的多通道趋势。
/// <list type="bullet">
/// <item>左块「通道」：全选 / 运行中 / 清空 · 已选 n；每路一行（勾选、识别色短线、RVn、当前温度 · 压力）；停用的不能勾。颜色固定跟通道走（RV9–16 同色虚线）。</item>
/// <item>右上「趋势」：时间范围（5 min / 15 min / 1 h / 全程 / 自定义…）· 温度 / 压力 / 气体消耗 · 设定值 · 实时 / 已暂停；
///   勾选的量各一张图上下排列、共用时间轴（不做双纵轴）；会话标记（旗 + 文字 + 竖虚线）；压力图画报警上限；十字光标一次读出所有选中通道。</item>
/// <item>右下「光标读数 · 区间统计」：分析（平均反应速率 / 气体消耗 RSD / 温度最大偏差）+ 表（光标处读数、区间最低 / 最高 / 平均、区间最高压力、吸气量与区间增加）。</item>
/// <item>工具条：暂停滚动 / 实时跟随 · 回到最新｜打标记（写进会话，要控制权）｜导出 CSV · 复制图片（还没做，只提示）。</item>
/// </list>
/// 数据 = 本机历史缓存（10 s 一格，最多 240 min）；「全程」超过 240 min 时从 240 min 前开始，并写明。
/// </summary>
public sealed partial class CurvePageVm : PageVm
{
    private readonly ShellVm _shell;
    private readonly RibbonTool _followTool, _latest, _mark, _export, _copy;
    private readonly IReadOnlyList<RibbonGroup> _tools;
    private readonly Dictionary<int, HashSet<int>> _sel = new();
    private readonly Dictionary<(int N, HistoryField F), ChartPoint[]> _bufs = new();
    private int _chFor;
    private DateTime? _pausedAt;
    private DateTime _a, _b;
    private DeviceVm? _dev;
    private bool _resetting;

    public static readonly HistoryField[] AllFields = { HistoryField.TempC, HistoryField.PressureMPa, HistoryField.Mmol };

    public CurvePageVm(ShellVm shell) : base(AppPage.Curves)
    {
        _shell = shell;
        _followTool = new RibbonTool("pause", "暂停滚动", ToggleFollow, "暂停自动滚动，便于查看");
        _latest = new RibbonTool("reset", "回到最新", Latest, "回到最新数据并恢复实时跟随");
        _mark = new RibbonTool("flag", "打标记", Mark, "在当前时刻打标记（写入本次运行记录）");
        _export = new RibbonTool("download", "导出 CSV", ExportCsv, "导出选中通道 · 当前时间范围 · CSV");
        _copy = new RibbonTool("copy", "复制图片", () => _shell.Toast("复制图片还没做（Avalonia 跨平台剪贴板图片）· 先用「导出 CSV」", ToastKind.Info), "把当前曲线复制为图片");
        _tools = new[]
        {
            new RibbonGroup("时间", new[] { _followTool, _latest }),
            new RibbonGroup("标记", new[] { _mark }),
            new RibbonGroup("导出", new[] { _export, _copy }),
        };
        AllCommand = new RelayCommand(() => SetSel(s => { foreach (var c in Channels) if (c.IsEnabled) s.Add(c.N); }));
        RunningCommand = new RelayCommand(() => SetSel(s => { s.Clear(); foreach (var n in DefaultSel(_dev)) s.Add(n); }));
        NoneCommand = new RelayCommand(() => SetSel(s => s.Clear()));
        PickRowCommand = new RelayCommand<CurveRowVm>(r => { if (r != null && _dev != null) _shell.SelectChannel(_dev.K, r.N); });
    }

    public override IReadOnlyList<RibbonGroup> Tools => _tools;

    public ICommand AllCommand { get; }
    public ICommand RunningCommand { get; }
    public ICommand NoneCommand { get; }
    public ICommand PickRowCommand { get; }

    /// <summary>导出目录（同报警页）。</summary>
    public static Func<string> ExportDir { get => AlarmPageVm.ExportDir; set => AlarmPageVm.ExportDir = value; }

    // ———————————————— 状态 ————————————————

    /// <summary>时间范围：m5 / m15 / h1 / run（全程）/ cus（自定义：只提示，回到原来的）。</summary>
    [ObservableProperty] private string _rangeKey = "run";
    [ObservableProperty] private bool _showT = true;
    [ObservableProperty] private bool _showP = true;
    [ObservableProperty] private bool _showG;
    [ObservableProperty] private bool _showSp;
    [ObservableProperty] private bool _follow = true;
    [ObservableProperty] private bool _isShort;
    private string _prevRange = "run";

    partial void OnRangeKeyChanged(string value)
    {
        if (value == "cus")
        {
            _shell.Toast("自定义时间段：日期选择还没做 · 本机只保留最近 240 min（更早的数据在「记录」页下载）", ToastKind.Info);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RangeKey = _prevRange);
            return;
        }
        _prevRange = value;
        Cursor = null;
        Rebuild();
    }

    partial void OnShowTChanged(bool value) => Rebuild();
    partial void OnShowPChanged(bool value) => Rebuild();
    partial void OnShowGChanged(bool value) => Rebuild();
    partial void OnShowSpChanged(bool value) => Rebuild();

    public CurveRange Range => RangeKey switch { "m5" => CurveRange.M5, "m15" => CurveRange.M15, "h1" => CurveRange.H1, _ => CurveRange.Run };

    // ———————————————— 通道列表 ————————————————

    public ObservableCollection<CurveChVm> Channels { get; } = new();
    [ObservableProperty] private string _chSub = "";
    [ObservableProperty] private string _selText = "";
    [ObservableProperty] private string _note = "";

    // ———————————————— 趋势 ————————————————

    [ObservableProperty] private string _trendSub = "";
    [ObservableProperty] private CurvePanelData? _tData;
    [ObservableProperty] private CurvePanelData? _pData;
    [ObservableProperty] private CurvePanelData? _gData;
    [ObservableProperty] private bool _noVars;
    /// <summary>光标（ms，null = 不在图上）。所有图同一条竖线。</summary>
    [ObservableProperty] private double? _cursor;
    [ObservableProperty] private CurveTipData? _tip;
    [ObservableProperty] private bool _isClipped;

    // ———————————————— 读数表 ————————————————

    [ObservableProperty] private string _readoutSub = "";
    [ObservableProperty] private string _rateText = "—";
    [ObservableProperty] private string _rsdText = "—";
    [ObservableProperty] private string _devText = "—";
    [ObservableProperty] private string _countText = "";
    public ObservableCollection<CurveRowVm> Rows { get; } = new();
    [ObservableProperty] private bool _noRows;

    /// <summary>当前设备选中的通道（按号排）。</summary>
    public IReadOnlyList<int> Selected => _dev != null ? Sel(_dev).OrderBy(n => n).ToList() : Array.Empty<int>();

    // ———————————————— 刷新 ————————————————

    public override void Refresh(IFleetView fleet)
    {
        IsShort = _shell.Layout.IsShort;
        _dev = _shell.Nav.DeviceK > 0 ? _shell.Fleet.Device(_shell.Nav.DeviceK) : null;
        Rebuild();
    }

    /// <summary>样稿 cvSel 的默认：在跑或报警的通道（最多 8 路）。</summary>
    private static IEnumerable<int> DefaultSel(DeviceVm? d) =>
        d == null ? Array.Empty<int>() : d.Reactors.Where(r => r.Installed && !r.TempNa && (r.IsRunning || r.IsAlarm)).Select(r => r.N).Take(8);

    private HashSet<int> Sel(DeviceVm d)
    {
        if (!_sel.TryGetValue(d.K, out var s)) _sel[d.K] = s = DefaultSel(d).ToHashSet();
        return s;
    }

    private void SetSel(Action<HashSet<int>> f)
    {
        if (_dev == null) return;
        f(Sel(_dev));
        Rebuild();
    }

    internal void Toggle(int n, bool on)
    {
        if (_dev == null || _resetting) return;
        var s = Sel(_dev);
        if (on ? s.Add(n) : s.Remove(n)) Rebuild();
    }

    private void Rebuild()
    {
        if (_resetting) return;
        var d = _dev;
        if (d == null) return;
        var src = d.Source;
        var now = src.History.Now;
        var sel = Sel(d);
        // 通道列表（换设备 / 路数变了才重建）
        if (_chFor != d.K || Channels.Count != d.Reactors.Count)
        {
            Channels.Clear();
            foreach (var r in d.Reactors) Channels.Add(new CurveChVm(this, r.N));
            _chFor = d.K;
        }
        foreach (var c in Channels)
        {
            var r = d.Reactor(c.N)!;
            bool enabled = r.Installed && !r.TempNa;
            if (!enabled) sel.Remove(c.N);
            c.Apply(r, enabled, sel.Contains(c.N));
        }
        ChSub = d.Alias + " · " + d.ChannelCount.ToString(CultureInfo.InvariantCulture) + " 路";
        SelText = "已选 " + sel.Count.ToString(CultureInfo.InvariantCulture);

        // 时间窗
        var end = Follow ? now : _pausedAt ?? now;
        DateTime? runStart = null;
        foreach (var r in d.Reactors)
            if (src.History.Channel(r.N) is { Count: > 0 } h)
            {
                var t0 = h[0].T.ToLocalTime();
                if (runStart == null || t0 < runStart) runStart = t0;
            }
        var (a, b, clipped) = CurveStats.Window(Range, end, runStart);
        _a = a;
        _b = b;
        IsClipped = clipped;
        bool atNow = Follow && Range == CurveRange.Run;
        TrendSub = d.Alias + " · " + a.ToString("HH:mm", CultureInfo.InvariantCulture) + " – " + (Follow ? "现在" : b.ToString("HH:mm", CultureInfo.InvariantCulture))
                   + (clipped ? " · 本机只保留最近 240 min" : "");
        Note = "颜色固定跟通道走" + (d.ChannelCount > 8 ? "；RV9–16 用同色虚线" : "") + (clipped ? "；更早的数据在「记录」页下载" : "");

        // 三张图
        var chans = sel.OrderBy(n => n).ToList();
        var marks = src.History.Marks.Where(m => m.At.ToLocalTime() >= a && m.At.ToLocalTime() <= b)
            .Select(m => new CurveMark(ChartTime.Ms(m.At), m.Text, IsAlarmMark(m.Text))).ToList();
        var vars = new List<HistoryField>();
        if (ShowT) vars.Add(HistoryField.TempC);
        if (ShowP) vars.Add(HistoryField.PressureMPa);
        if (ShowG) vars.Add(HistoryField.Mmol);
        NoVars = vars.Count == 0;
        CurvePanelData? Make(HistoryField f) => vars.Contains(f) ? Panel(d, f, chans, a, b, marks, first: vars[0] == f, last: vars[^1] == f) : null;
        TData = Make(HistoryField.TempC);
        PData = Make(HistoryField.PressureMPa);
        GData = Make(HistoryField.Mmol);
        _ = atNow;

        RefreshTable();
        RefreshTools();
    }

    private static bool IsAlarmMark(string text) => text.Contains("超压", StringComparison.Ordinal) || text.Contains("超温", StringComparison.Ordinal) || text.Contains("报警", StringComparison.Ordinal);

    private CurvePanelData Panel(DeviceVm d, HistoryField f, IReadOnlyList<int> chans, DateTime a, DateTime b, IReadOnlyList<CurveMark> marks, bool first, bool last)
    {
        // 压力按设置的单位画：点、上限、设定值都乘同一个倍数（MPa 时 1，与改单位之前相同）
        double k = f == HistoryField.PressureMPa ? PressureUnits.PerMpa(PressureDisplay.Primary) : 1;
        var lines = new List<CurveLine>(chans.Count);
        var all = new List<double>();
        foreach (var n in chans)
        {
            var h = d.Source.History.Channel(n);
            if (h == null) continue;
            _bufs.TryGetValue((n, f), out var buf);
            buf ??= Array.Empty<ChartPoint>();
            int cnt = CurveStats.CopyRange(h, f, a, b, ref buf);
            _bufs[(n, f)] = buf;
            var pts = new ChartPoint[cnt];
            Array.Copy(buf, pts, cnt);
            if (k != 1) for (int i = 0; i < cnt; i++) pts[i] = new ChartPoint(pts[i].T, pts[i].V * k);
            for (int i = 0; i < cnt; i++) if (double.IsFinite(pts[i].V)) all.Add(pts[i].V);
            lines.Add(new CurveLine(n, RunRowText.ChColor(n), n > 8, pts, cnt));
        }
        var plant = d.Source.Plant;
        double? limit = f == HistoryField.PressureMPa ? plant.OverPressureMPa * k : null;
        var sps = new List<(Color, double)>();
        if (ShowSp)
            foreach (var n in chans)
            {
                var r = d.Reactor(n);
                double? v = f == HistoryField.TempC ? r?.SpC : f == HistoryField.PressureMPa ? (r?.PTargetMPa is > 0 ? r.PTargetMPa : null) : null;
                if (v is double sv) { sps.Add((RunRowText.ChColor(n), sv * k)); all.Add(sv * k); }
            }
        var axis = CurveStats.Axis(f, all, limit, k);
        var (title, unit, dec) = f switch
        {
            HistoryField.TempC => ("温度", "°C", 1),
            HistoryField.PressureMPa => ("压力", PressureDisplay.Unit, PressureDisplay.Decimals(2)),
            _ => ("气体消耗", "mmol", 1),
        };
        // 纵轴刻度的小数位：压力 MPa 1 位（同样稿）、bar / psi 的间隔是整数时不写小数；温度 / 吸气量整数
        int axisDec = f != HistoryField.PressureMPa ? 0 : k == 1 || axis.Step < 1 ? 1 : 0;
        return new CurvePanelData(f, title, unit, dec, lines, ChartTime.Ms(a), ChartTime.Ms(b), a, axis, CurveStats.TickMinutes((b - a).TotalMinutes),
            limit, marks, first, last, chans.Count > 0 && chans.Count <= 4, sps, chans.Count == 0 ? "左侧勾选通道" : null, axisDec);
    }

    // ———————————————— 光标与读数表 ————————————————

    /// <summary>光标移到 <paramref name="ms"/>（null = 离开图）：按 10 s 一格取整；所有图同一条竖线，提示框与读数表跟着变。</summary>
    public void SetCursor(double? ms)
    {
        if (ms is double t)
        {
            double a = ChartTime.Ms(_a), b = ChartTime.Ms(_b);
            t = Math.Clamp(Math.Round(t / 10000) * 10000, a, b);
            if (Cursor == t) return;
            Cursor = t;
        }
        else
        {
            if (Cursor == null) return;
            Cursor = null;
        }
        RefreshTable();
    }

    private void RefreshTable()
    {
        var d = _dev;
        if (d == null) return;
        var sel = Sel(d).OrderBy(n => n).ToList();
        var at = Cursor is double c ? ChartTime.Local(c) : _b;
        ReadoutSub = (Cursor == null ? "最新 " : "光标 ") + at.ToString("HH:mm", CultureInfo.InvariantCulture) + " · 区间 "
                     + _a.ToString("HH:mm", CultureInfo.InvariantCulture) + " – " + _b.ToString("HH:mm", CultureInfo.InvariantCulture);
        var plant = d.Source.Plant;
        int picked = _shell.Nav.ChannelOf(d.K);

        // 读数表：就地更新
        for (int i = Rows.Count - 1; i >= 0; i--) if (!sel.Contains(Rows[i].N)) Rows.RemoveAt(i);
        for (int i = 0; i < sel.Count; i++)
        {
            int n = sel[i];
            int idx = -1;
            for (int j = 0; j < Rows.Count; j++) if (Rows[j].N == n) { idx = j; break; }
            CurveRowVm row;
            if (idx < 0) { row = new CurveRowVm(n, PickRowCommand); Rows.Insert(i, row); }
            else { row = Rows[idx]; if (idx != i) Rows.Move(idx, i); }
            var r = d.Reactor(n)!;
            var h = d.Source.History.Channel(n);
            var sT = CurveStats.Stat(h, HistoryField.TempC, _a, _b);
            var sP = CurveStats.Stat(h, HistoryField.PressureMPa, _a, _b);
            var sG = CurveStats.Stat(h, HistoryField.Mmol, _a, _b);
            double? tAt = CurveStats.At(h, HistoryField.TempC, at, _a, _b);
            double? pAt = CurveStats.At(h, HistoryField.PressureMPa, at, _a, _b);
            double? gAt = CurveStats.At(h, HistoryField.Mmol, at, _a, _b);
            row.Apply(RunRowText.Word(r.Own, r.Cause, r.OwnWord), tAt, sT, pAt, sP, plant.OverPressureMPa, gAt, sG, (_b - _a).TotalMinutes, n == picked);
        }
        NoRows = sel.Count == 0;

        // 分析（样稿 cvAna：按已选的各路当前值）
        var live = sel.Select(n => d.Reactor(n)!).Where(r => !r.TempNa).ToList();
        var rates = live.Select(r => r.GasRate ?? 0).ToList();
        int rateN = rates.Count(x => x != 0);
        RateText = (rates.Sum() / Math.Max(1, rateN)).ToString("0.00", CultureInfo.InvariantCulture);
        var rsd = CurveStats.Rsd(live.Select(r => r.GasMmol ?? 0).Where(g => g > 0));
        RsdText = rsd is double rv ? rv.ToString("0.0", CultureInfo.InvariantCulture) : "—";
        var dev = live.Where(r => r.Own == StateKey.React && r.SpC != null && r.TempC != null).Select(r => Math.Abs(r.TempC!.Value - r.SpC!.Value)).ToList();
        DevText = dev.Count > 0 ? dev.Max().ToString("0.0", CultureInfo.InvariantCulture) : "—";
        CountText = "按已选 " + sel.Count.ToString(CultureInfo.InvariantCulture) + " 路";

        // 提示框（光标在图上时）
        if (Cursor is double ct)
        {
            var rows = new List<CurveTipRow>();
            var units = new List<string>();
            var fields = new List<HistoryField>();
            if (ShowT) { fields.Add(HistoryField.TempC); units.Add("°C"); }
            if (ShowP) { fields.Add(HistoryField.PressureMPa); units.Add(PressureDisplay.Unit); }
            if (ShowG) { fields.Add(HistoryField.Mmol); units.Add("mmol"); }
            foreach (var n in sel.Take(8))
            {
                var h = d.Source.History.Channel(n);
                var vals = fields.Select(f => CurveStats.At(h, f, at, _a, _b) is double v ? (f == HistoryField.PressureMPa ? PressureDisplay.Fixed(v, 2) : v.ToString("0.0", CultureInfo.InvariantCulture)) : "—").ToArray();
                rows.Add(new CurveTipRow(n, RunRowText.ChColor(n), n > 8, vals));
            }
            Tip = new CurveTipData(ct, at.ToString(at.Second == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture), rows, units, sel.Count > 8 ? "另 " + (sel.Count - 8).ToString(CultureInfo.InvariantCulture) + " 路见下方表格" : null,
                ChartTime.Ms(_a), ChartTime.Ms(_b), sel.Count > 0 && sel.Count <= 4);
        }
        else Tip = null;
    }

    private void RefreshTools()
    {
        _followTool.Icon = Follow ? "pause" : "play";
        _followTool.Label = Follow ? "暂停滚动" : "实时跟随";
        _followTool.Tip = Follow ? "暂停自动滚动，便于查看" : "恢复跟随最新数据";
        var d = _dev;
        _mark.Reason = d == null ? "先选一台设备" : Nz(d.Actions.Why(DeviceAction.SessionMark));
        _export.Reason = d == null ? "先选一台设备" : Sel(d).Count == 0 ? "先在左侧勾选通道" : null;
    }

    // ———————————————— 动作 ————————————————

    private void ToggleFollow()
    {
        Follow = !Follow;
        _pausedAt = Follow ? null : _dev?.Source.History.Now;
        _shell.Toast(Follow ? "已恢复实时跟随" : "已暂停滚动", ToastKind.Info);
        Rebuild();
    }

    private void Latest()
    {
        Follow = true;
        _pausedAt = null;
        Cursor = null;
        Rebuild();
        _shell.Toast("已回到最新", ToastKind.Info);
    }

    private void Mark()
    {
        if (_dev is not { } d) return;
        int n = d.Source.History.Marks.Count + 1;
        _ = d.Actions.RunAsync(DeviceAction.SessionMark, new ActionPayload(Text: "标记 " + n.ToString(CultureInfo.InvariantCulture)));
    }

    private void ExportCsv()
    {
        if (_dev is not { } d) return;
        var sel = Sel(d).OrderBy(n => n).ToList();
        var fields = new List<HistoryField>();
        if (ShowT) fields.Add(HistoryField.TempC);
        if (ShowP) fields.Add(HistoryField.PressureMPa);
        if (ShowG) fields.Add(HistoryField.Mmol);
        if (fields.Count == 0) fields.AddRange(AllFields);
        var csv = CurveStats.Csv(sel.Select(n => new CurveStats.CsvChannel(n, d.Source.History.Channel(n))).ToList(), fields, _a, _b);
        try
        {
            var dir = ExportDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "curves-" + d.Alias + "-" + _a.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "-" + _b.ToString("HHmm", CultureInfo.InvariantCulture) + ".csv");
            File.WriteAllText(path, csv, new UTF8Encoding(true));
            _shell.Toast("已导出 " + d.Alias + " " + sel.Count.ToString(CultureInfo.InvariantCulture) + " 路 · " + _a.ToString("HH:mm", CultureInfo.InvariantCulture) + " – "
                         + _b.ToString("HH:mm", CultureInfo.InvariantCulture) + " → " + path, ToastKind.Ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.Toast("导出失败：" + ex.Message, ToastKind.Err);
        }
    }

    /// <summary>从报警页「查看曲线」进来：勾上这一路、时间范围 1 h、实时跟随（样稿 al-cv）。</summary>
    public void Focus(int k, int n)
    {
        if (_shell.Fleet.Device(k) is not { } d) return;
        _dev = d;
        Sel(d).Add(n);
        Follow = true;
        _pausedAt = null;
        RangeKey = "h1";
        Rebuild();
    }

    // ———————————————— 地址参数 / 还原 ————————————————

    /// <summary>
    /// 截图参数：cvr=m5|m15|h1|run、cvv=TPG（勾选的量，字母任意组合）、cvsp=1（设定值）、cvsel=1,2,3（勾选的通道）、cvpause=1、
    /// cvcur=HH:mm（光标停在这个时刻，截图用）。
    /// </summary>
    public override void ApplyParams(Func<string, string?> param)
    {
        _resetting = true;
        if (param("cvr") is { Length: > 0 } r) RangeKey = _prevRange = r;
        if (param("cvv") is { } v) { ShowT = v.Contains('T'); ShowP = v.Contains('P'); ShowG = v.Contains('G'); }
        if (param("cvsp") is { } sp) ShowSp = sp == "1";
        _resetting = false;
        Refresh(_shell.Fleet);
        if (_dev is { } d && param("cvsel") is { } cs)
        {
            var s = Sel(d);
            s.Clear();
            foreach (var part in cs.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) s.Add(n);
        }
        if (param("cvpause") == "1") { Follow = false; _pausedAt = _dev?.Source.History.Now; }
        Rebuild();
        if (param("cvcur") is { Length: > 0 } cur && TimeSpan.TryParse(cur, CultureInfo.InvariantCulture, out var tod))
            SetCursor(ChartTime.Ms(_b.Date + tod));
    }

    public override void ResetView()
    {
        _resetting = true;
        _sel.Clear();
        RangeKey = _prevRange = "run";
        ShowT = ShowP = true;
        ShowG = ShowSp = false;
        Follow = true;
        _pausedAt = null;
        Cursor = null;
        _resetting = false;
    }

    private static string? Nz(string s) => s.Length == 0 ? null : s;
}

/// <summary>
/// 通道列表的一行（样稿 .cvli）：勾选（停用的不能勾）、识别色短线（22 × 2.4，RV9–16 虚线）、RVn 14/600、当前「145.2°C · 2.35MPa」13 t-2（单位 t-3）；
/// 没勾的名字和读数变浅（t-dis）。
/// </summary>
public sealed partial class CurveChVm : ObservableObject
{
    private readonly CurvePageVm _page;
    private bool _applying;

    public CurveChVm(CurvePageVm page, int n)
    {
        _page = page;
        N = n;
        Label = "RV" + n.ToString(CultureInfo.InvariantCulture);
        Color = RunRowText.ChColor(n);
        Dashed = n > 8;
    }

    public int N { get; }
    public string Label { get; }
    public Color Color { get; }
    public bool Dashed { get; }

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private string _temp = "—";
    [ObservableProperty] private string _press = "—";
    [ObservableProperty] private string _tip = "";

    partial void OnIsOnChanged(bool value)
    {
        if (!_applying) _page.Toggle(N, value);
    }

    internal void Apply(ReactorVm r, bool enabled, bool on)
    {
        _applying = true;
        IsEnabled = enabled;
        IsOn = on;
        _applying = false;
        Temp = r.TempC is double t && !r.TempNa ? t.ToString("0.0", CultureInfo.InvariantCulture) : "—";
        Press = r.PressureMPa is double p && !r.PressNa ? PressureDisplay.Fixed(p, 2) : "—";
        Tip = Label + " · " + RunRowText.Word(r.Own, r.Cause, r.OwnWord) + (enabled ? "" : " · 没有读数，不能勾选");
    }
}

/// <summary>
/// 读数表的一行（样稿 #cvTbl tr.rw）：通道（识别色短线 + RVn 600）· 状态（t-3）· 温度（光标处，600）· 区间 最低 / 最高 · 平均 · 压力（光标处，600）·
/// 区间最高（超过报警上限红色 600）· 气体 mmol（光标处）· 区间增加。选中（检查器那一路）= a-tint + 左侧 3 px 品牌蓝。
/// </summary>
public sealed partial class CurveRowVm : ObservableObject
{
    public CurveRowVm(int n, ICommand pick)
    {
        N = n;
        Rv = "RV" + n.ToString(CultureInfo.InvariantCulture);
        Color = RunRowText.ChColor(n);
        Dashed = n > 8;
        PickCommand = pick;
    }

    public int N { get; }
    public string Rv { get; }
    public Color Color { get; }
    public bool Dashed { get; }
    public ICommand PickCommand { get; }

    [ObservableProperty] private string _word = "";
    [ObservableProperty] private string _t = "—";
    [ObservableProperty] private string _tRange = "—";
    [ObservableProperty] private string _tMean = "—";
    [ObservableProperty] private string _p = "—";
    [ObservableProperty] private string _pMax = "—";
    [ObservableProperty] private bool _pHot;
    [ObservableProperty] private string _g = "—";
    [ObservableProperty] private string _gRise = "—";
    /// <summary>区间增加的悬停：「平均 0.20 mmol/min（90 min）」（区间的斜率）。</summary>
    [ObservableProperty] private string _gTip = "";
    [ObservableProperty] private bool _isSelected;

    internal void Apply(string word, double? t, CurveStat? sT, double? p, CurveStat? sP, double? limit, double? g, CurveStat? sG, double minutes, bool selected)
    {
        static string F(double? v, string fmt) => v is double x ? x.ToString(fmt, CultureInfo.InvariantCulture) : "—";
        Word = word;
        T = F(t, "0.0");
        TRange = sT is { } st ? st.Min.ToString("0.0", CultureInfo.InvariantCulture) + " / " + st.Max.ToString("0.0", CultureInfo.InvariantCulture) : "—";
        TMean = sT is { } st2 ? st2.Mean.ToString("0.0", CultureInfo.InvariantCulture) : "—";
        // 压力按设置的单位（MPa 2 位、bar 2 位、psi 1 位）
        P = PressureDisplay.Fixed(p, 2);
        PMax = sP is { } sp ? PressureDisplay.Fixed(sp.Max, 2) : "—";
        PHot = sP is { } sp2 && limit is double l && sp2.Max > l;
        G = F(g, "0.0");
        GRise = sG is { } sg ? sg.Rise.ToString("0.0", CultureInfo.InvariantCulture) : "0.0";
        GTip = sG is { } sg2 && minutes > 0 ? "区间平均速率 " + (sg2.Rise / minutes).ToString("0.00", CultureInfo.InvariantCulture) + " mmol/min（" + Math.Round(minutes).ToString(CultureInfo.InvariantCulture) + " min）" : "";
        IsSelected = selected;
    }
}

/// <summary>一条曲线：RV 号、识别色、虚线（RV9–16）、点（时刻 ms + 值，NaN = 断开）。</summary>
public sealed record CurveLine(int N, Color Color, bool Dashed, ChartPoint[] Points, int Count);

/// <summary>会话标记：时刻（ms）、文字、报警类（红色）。</summary>
public sealed record CurveMark(double T, string Text, bool Err);

/// <summary>
/// 一张图的输入（每次刷新一个新实例）：量、标题 / 单位 / 小数位、各条线、时间窗（ms + 起点的本地时刻）、纵轴、横轴刻度间隔（min）、报警上限（压力）、
/// 标记（第一张图写文字）、最后一张写时间、≤ 4 路时末端写 RVn、设定值（识别色点线）、空的说明。
/// </summary>
public sealed record CurvePanelData(HistoryField Field, string Title, string Unit, int Decimals, IReadOnlyList<CurveLine> Lines, double T0, double T1, DateTime Start,
    CurveAxis Axis, int TickMinutes, double? Limit, IReadOnlyList<CurveMark> Marks, bool First, bool Last, bool EndLabels,
    IReadOnlyList<(Color Color, double V)> SpLines, string? Empty, int AxisDec = 0);

/// <summary>提示框的一行：RV 号、颜色、虚线、各量的读数（与 <see cref="CurveTipData.Units"/> 对应）。</summary>
public sealed record CurveTipRow(int N, Color Color, bool Dashed, IReadOnlyList<string> Values);

/// <summary>十字光标的提示框（样稿 .cvtip）：光标时刻（ms）、标题时刻、各路读数、单位、「另 n 路见下方表格」、时间窗（定位用）、右边距是否 52（≤ 4 路有末端标签）。</summary>
public sealed record CurveTipData(double T, string Head, IReadOnlyList<CurveTipRow> Rows, IReadOnlyList<string> Units, string? More, double T0, double T1, bool WideRight);
