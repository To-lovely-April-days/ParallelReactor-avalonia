using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Services;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Pages.Programs;

/// <summary>
/// 条件矩阵（融合版 F6，样稿 mxRender / mxChart / mxAct / mxChange）：列 = 通道（表头勾选「参加」、状态：运行中 · 几点结束 / 空闲 / 已停用；16 路分两组 RV1–8 / RV9–16），
/// 行 = 样品 / 催化剂、溶液体积（必填）、每一步的参数 —— 每路可以不同的（升温目标、升压目标、反应时长）每格一个输入，带「统一」「等差」两个工具；
/// 共用的（斜纹底）只填一次，写明为什么共用；粗体 = 改过模板值，琥珀 = 超出范围。表尾 = 每路预计用时（含升压排队）。
/// 右边：检查（按值分组的问题）· 各路预计温度 · 最短 / 最长 · 升压顺序说明；下发：会话名、下发后开始记录、预运行检查、下发并运行（每路一个程序）。
/// 状态（<see cref="MatrixPlan"/>）按「设备 + 模板」建一次；样品名和方案存在本机（ExperimentStore）。
/// </summary>
public sealed partial class MatrixVm : ObservableObject
{
    private readonly ProgramPageVm _page;
    private DeviceVm? _d;
    private ProgramSchema? _schema;
    private string _builtKey = "", _rowsKey = "";
    private bool _demoSeeded;

    public MatrixVm(ProgramPageVm page)
    {
        _page = page;
        CheckCommand = new RelayCommand(page.OpenCheck);
        RunCommand = new AsyncRelayCommand(SendAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        VolSameCommand = new RelayCommand(VolSame);
    }

    public MatrixPlan? Plan { get; private set; }
    public bool HasPlan => Plan != null;

    /// <summary>像新打开（截图）：下次刷新按模板重建（演示的 1号机 · H1 再填一次演示值），回到 RV1–8。</summary>
    public void Reset()
    {
        _builtKey = "";
        _rowsKey = "";
        _demoSeeded = false;
        Plan = null;
        Bank = 0;
    }

    // ———————————————————— 表 ————————————————————

    [ObservableProperty] private string _templateSub = "";
    /// <summary>16 路设备：0 = RV1–8，1 = RV9–16（样稿 MX.bank）。</summary>
    public int Bank
    {
        get => _bank;
        set
        {
            if (_bank == value) return;
            _bank = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BankKey));
            _rowsKey = "";
            Update();
        }
    }
    private int _bank;
    /// <summary>分段控件的值（'0' / '1'）。</summary>
    public string BankKey { get => Bank.ToString(CultureInfo.InvariantCulture); set => Bank = value == "1" ? 1 : 0; }
    [ObservableProperty] private bool _isMany;
    [ObservableProperty] private string _incText = "0";
    public ObservableCollection<MxColVm> Columns { get; } = new();
    public ObservableCollection<MxRow> Rows { get; } = new();
    public ObservableCollection<MxFootCell> Foot { get; } = new();
    /// <summary>可见的列数（行面板按它分列宽）。</summary>
    [ObservableProperty] private int _colCount = 8;

    // ———————————————————— 检查与预计 ————————————————————

    public ObservableCollection<MxIssueVm> Issues { get; } = new();
    [ObservableProperty] private bool _allOk;
    [ObservableProperty] private MxChartData? _chart;
    [ObservableProperty] private string _shortest = "";
    [ObservableProperty] private string _longest = "";
    [ObservableProperty] private bool _hasEst;
    [ObservableProperty] private string? _queueNote;
    [ObservableProperty] private string _sideSub = "";

    // ———————————————————— 下发 ————————————————————

    [ObservableProperty] private string _sessionName = "";
    partial void OnSessionNameChanged(string value) { if (Plan != null && value.Trim().Length > 0) Plan.Name = value.Trim(); }
    [ObservableProperty] private bool _record = true;
    partial void OnRecordChanged(bool value) { if (Plan != null) Plan.Record = value; }
    [ObservableProperty] private int _incCount, _idleCount, _busyCount;
    [ObservableProperty] private string? _runReason;
    [ObservableProperty] private string _runTip = "下发并运行";
    public ICommand CheckCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand VolSameCommand { get; }

    /// <summary>当前设备的各路（矩阵用的形状）。</summary>
    internal IReadOnlyList<MxChannel> Channels(DeviceVm d)
    {
        var now = _page.Shell.Fleet.Now;
        bool estop = d.Source.Estop.Active;
        return d.Reactors.Where(r => r.Installed).Select(r =>
        {
            bool busy = r.IsRunning || estop;
            DateTime? eta = busy && r.Program is { Status: ProgramStatus.Running, Progress: { TotalRemainMin: > 0 } pr } ? ProgramPanelText.EtaAt(now, pr.TotalRemainMin) : null;
            return new MxChannel(r.N, r.Own == StateKey.Idle, busy, eta, r.SolutionMl);
        }).ToArray();
    }

    internal MxContext Context(DeviceVm d, ProgramSchema schema) =>
        new(d.Alias, d.Model.Label, d.Model.Key, d.Actions.GuardText(GuardKind.Control).Length == 0, Channels(d), schema, ProgramPageVm.VolMaxOf(d));

    // ———————————————————— 刷新 ————————————————————

    private void Update() { if (_d != null && _schema != null) Refresh(_d, _schema); }

    public void Refresh(DeviceVm d, ProgramSchema schema)
    {
        _d = d;
        _schema = schema;
        var tpl = _page.Sel;
        if (tpl is null) { Plan = null; return; }
        var chs = Channels(d);
        string key = d.Source.Id + "/" + tpl.Id;
        if (key != _builtKey || Plan is null)
        {
            if (!_builtKey.StartsWith(d.Source.Id.ToString(), StringComparison.Ordinal)) { _bank = 0; OnPropertyChanged(nameof(Bank)); OnPropertyChanged(nameof(BankKey)); }
            _builtKey = key;
            _rowsKey = "";
            Plan = _page.Experiments.Plan(d.Source.Id, tpl, d.K, chs) ?? Build(d, tpl, chs);
            SetSessionUi();
        }
        var m = Plan;
        IsMany = chs.Count > 8;
        if (!IsMany && _bank != 0) { _bank = 0; OnPropertyChanged(nameof(Bank)); OnPropertyChanged(nameof(BankKey)); }
        var cols = IsMany ? chs.Skip(Bank * 8).Take(8).ToArray() : chs.ToArray();
        TemplateSub = "模板「" + tpl.Name + "」 · " + m.Steps.Count + " 步";
        var ctx = Context(d, schema);
        string rk = key + "|" + Bank + "|" + string.Join(",", cols.Select(c => c.N)) + "|" + string.Join(",", m.Steps.Select(s => s.Type)) + "|" + schema.GetHashCode() + "|" + PressureDisplay.Version;
        if (rk != _rowsKey) { _rowsKey = rk; BuildRows(m, cols, schema); }
        var inc = m.Included(chs);
        var plans = inc.Select((n, j) => (n, p: m.PlanFor(n, j))).ToDictionary(x => x.n, x => x.p);
        SyncColumns(d, cols);
        foreach (var r in Rows) r.Apply(m, ctx, d);
        for (int i = 0; i < Foot.Count; i++)
        {
            var f = Foot[i];
            f.Text = plans.TryGetValue(f.N, out var pl) ? ProgramPlanner.Hm(pl.Total) : Fmt.Dash;
            f.Xc = !m.Include.Contains(f.N);
        }
        IncText = inc.Count.ToString(CultureInfo.InvariantCulture);
        // 检查与预计
        var iss = m.Issues(ctx);
        SyncIssues(iss);
        AllOk = iss.Count == 0;
        SideSub = inc.Count + " 路";
        Chart = new MxChartData(inc.Select(n => new MxSeries(n, RunRowText.ChColor(n), n > 8, plans[n])).ToArray());
        var ends = plans.OrderBy(x => x.Value.Total).ThenBy(x => x.Key).ToList();
        HasEst = ends.Count > 0;
        Shortest = HasEst ? "RV" + ends[0].Key + " · " + ProgramPlanner.Hm(ends[0].Value.Total) : "";
        Longest = HasEst ? "RV" + ends[^1].Key + " · " + ProgramPlanner.Hm(ends[^1].Value.Total) : "";
        QueueNote = m.QueueNote(inc);
        // 下发
        IncCount = inc.Count;
        IdleCount = chs.Count(c => m.Include.Contains(c.N) && !c.Busy);
        BusyCount = IncCount - IdleCount;
        RunReason = iss.Count > 0 ? iss[0].Msg : null;
        RunTip = "按每路条件生成 " + inc.Count + " 个程序并下发到" + d.Alias;
    }

    private MatrixPlan Build(DeviceVm d, PcProgram tpl, IReadOnlyList<MxChannel> chs)
    {
        bool demo = d.Source.IsDemo;
        var m = MatrixPlan.Build(tpl, d.K, chs, demo ? DemoPrograms.MatrixName : tpl.Name + " · " + _page.Shell.Fleet.Now.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
        // 样稿 mxBuild 的演示：1号机 · H1 第一次打开时填演示值（RV4 压力故意填错）
        if (demo && !_demoSeeded && tpl.Id == "H1" && d.K == 1)
        {
            _demoSeeded = true;
            DemoPrograms.SeedMatrix(m, chs.Select(c => c.N).ToArray());
        }
        return m;
    }

    private void SetSessionUi()
    {
        if (Plan is null) return;
        SessionName = Plan.Name;
        Record = Plan.Record;
    }

    private void BuildRows(MatrixPlan m, IReadOnlyList<MxChannel> cols, ProgramSchema schema)
    {
        Rows.Clear();
        Columns.Clear();
        Foot.Clear();
        ColCount = cols.Count;
        foreach (var c in cols) Columns.Add(new MxColVm(this, c.N));
        Rows.Add(new MxSampleRow(this, cols.Select(c => c.N)));
        Rows.Add(new MxVolRow(this, cols.Select(c => c.N), VolSameCommand));
        for (int i = 0; i < m.Steps.Count; i++)
        {
            var s = m.Steps[i];
            Rows.Add(new MxHeadRow((i + 1).ToString("00", CultureInfo.InvariantCulture), schema.Of(s.Type)?.Label ?? s.Type.Zh(), s.Name));
            foreach (var k in MatrixPlan.RowKeys(s.Type))
            {
                var f = schema.Field(s.Type, k);
                if (f is null) continue;
                if (MatrixPlan.IsPer(s.Type, k)) Rows.Add(new MxPerRow(this, i, f, cols.Select(c => c.N)));
                else Rows.Add(new MxSharedRow(this, i, s.Type, f));
            }
        }
        foreach (var c in cols) Foot.Add(new MxFootCell(c.N));
    }

    private void SyncColumns(DeviceVm d, IReadOnlyList<MxChannel> cols)
    {
        var m = Plan!;
        foreach (var col in Columns)
        {
            var c = cols.FirstOrDefault(x => x.N == col.N);
            if (c is null) continue;
            col.Off = c.Off;
            col.Busy = c.Busy && !c.Off;
            col.SetIncluded(m.Include.Contains(c.N));
            col.Status = c.Off ? "已停用" : c.Busy ? "运行中" : "空闲";
            col.Time = !c.Off && c.Busy && !_page.IsNarrow && c.EtaAt is { } e ? " · " + Fmt.Hm(e) : "";
            col.Tip = c.Off ? col.Rv + " 已停用 · 触屏启用后才能参加" : c.Busy && c.EtaAt is { } e2 ? col.Rv + " 正在运行，约 " + Fmt.Hm(e2) + " 结束" : "勾选 = 这一路参加本次实验";
        }
    }

    private void SyncIssues(IReadOnlyList<MxIssue> iss)
    {
        while (Issues.Count > iss.Count) Issues.RemoveAt(Issues.Count - 1);
        for (int i = 0; i < iss.Count; i++)
        {
            if (i >= Issues.Count) Issues.Add(new MxIssueVm());
            Issues[i].IsNo = iss[i].Level == MxLevel.No;
            Issues[i].Msg = iss[i].Msg;
        }
    }

    // ———————————————————— 编辑 ————————————————————

    private void Changed() => _page.Refresh(_page.Shell.Fleet);

    internal void SetInclude(int n, bool on)
    {
        if (Plan is null || _d is null) return;
        var c = Channels(_d).FirstOrDefault(x => x.N == n);
        if (c is null || c.Off) return;
        if (on) Plan.Include.Add(n); else Plan.Include.Remove(n);
        Changed();
    }

    internal void SetSample(int n, string text)
    {
        if (Plan is null) return;
        Plan.Samples[n] = text.Trim();
    }

    /// <summary>溶液体积（0.1 mL）。</summary>
    public void SetVolume(int n, double v)
    {
        if (Plan is null) return;
        Plan.Vol[n] = double.IsFinite(v) ? Math.Round(v, 1, MidpointRounding.AwayFromZero) : double.NaN;
        Changed();
    }

    internal void SetPer(int i, string k, int n, double v)
    {
        if (Plan is null) return;
        Plan.SetPer(i, k, n, v);
        Changed();
    }

    internal void SetShared(int i, string k, double? num, string? code)
    {
        if (Plan is null || i >= Plan.Steps.Count) return;
        var s = Plan.Steps[i];
        if (code != null) s.Sel[k] = code; else s.Num[k] = num ?? double.NaN;
        Changed();
    }

    internal void Same(int i, FieldSpec f, bool ramp)
    {
        if (Plan is null || _d is null) return;
        var inc = Plan.Included(Channels(_d));
        if (inc.Count < 2) { _page.Shell.Toast("至少要有 2 路参加", ToastKind.Info); return; }
        var msg = ramp ? Plan.Ramp(i, f.Key, inc, f) : Plan.Same(i, f.Key, inc, f);
        if (msg != null) _page.Shell.Toast(msg, ToastKind.Info);
        Changed();
    }

    private void VolSame()
    {
        if (Plan is null || _d is null) return;
        var msg = Plan.VolSame(Plan.Included(Channels(_d)));
        if (msg != null) _page.Shell.Toast(msg, ToastKind.Info);
        Changed();
    }

    /// <summary>按模板重建（样稿 mx-reset）：丢掉修改（不再填演示值）。</summary>
    public void Rebuild(bool toast)
    {
        if (_d is null || _page.Sel is not { } tpl) return;
        _demoSeeded = true;
        Plan = MatrixPlan.Build(tpl, _d.K, Channels(_d), Plan?.Name ?? tpl.Name);
        _rowsKey = "";
        SetSessionUi();
        if (toast) _page.Shell.Toast("已按「" + tpl.Name + "」重建矩阵", ToastKind.Info);
        Changed();
    }

    /// <summary>保存方案（本机 experiments.json）。</summary>
    public void SavePlan()
    {
        if (Plan is null || _d is null) return;
        var err = _page.Experiments.SavePlan(_d.Source.Id, Plan, _page.Shell.Fleet.Now);
        _page.Shell.Toast(err ?? "已保存实验方案「" + Plan.Name + "」", err is null ? ToastKind.Ok : ToastKind.Err);
    }

    public async Task ExportCsvAsync()
    {
        if (Plan is null || _d is null || _schema is null) return;
        var csv = MatrixCsv.Export(Plan, Channels(_d).Select(c => c.N).ToArray(), _schema, _d.Model.Label);
        if (_page.SaveFile is null) { _page.Shell.Toast("导出 CSV：这里没有文件对话框", ToastKind.Info); return; }
        var path = await _page.SaveFile("导出条件矩阵", Plan.Name + ".csv", "csv").ConfigureAwait(true);
        if (path is null) return;
        try
        {
            await System.IO.File.WriteAllTextAsync(path, csv, new System.Text.UTF8Encoding(true)).ConfigureAwait(true);
            _page.Shell.Toast("已导出「" + Plan.Name + "」条件矩阵 → " + path, ToastKind.Ok);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _page.Shell.Toast("导出失败：" + ex.Message, ToastKind.Err); }
    }

    public async Task ImportCsvAsync()
    {
        if (Plan is null || _d is null) return;
        if (_page.OpenFile is null) { _page.Shell.Toast("导入 CSV：这里没有文件对话框", ToastKind.Info); return; }
        var path = await _page.OpenFile("导入条件矩阵", "csv").ConfigureAwait(true);
        if (path is null) return;
        try
        {
            var text = await System.IO.File.ReadAllTextAsync(path).ConfigureAwait(true);
            ImportCsv(text);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _page.Shell.Toast("导入失败：" + ex.Message, ToastKind.Err); }
    }

    /// <summary>导入 CSV 的正文（测试直接调）。</summary>
    public void ImportCsv(string text)
    {
        if (Plan is null || _d is null) return;
        var chs = Channels(_d);
        try
        {
            var msg = MatrixCsv.Import(Plan, text, chs.Select(c => c.N).ToArray(), chs.Where(c => c.Off).Select(c => c.N).ToArray());
            _page.Shell.Toast(msg, ToastKind.Ok);
        }
        catch (FormatException ex) { _page.Shell.Toast("导入失败：" + ex.Message, ToastKind.Err); }
        Changed();
    }

    // ———————————————————— 下发 ————————————————————

    /// <summary>
    /// 下发并运行（样稿 mx-run）：每一路一个程序（这一路的参数）+ 溶液体积；之后按「下发后开始记录会话」开始会话（名字 = 会话名称；已经在记录时不另开）；
    /// 方案和这次下发记进本机的实验方案文件（样品名在那里）。
    /// </summary>
    public async Task SendAsync()
    {
        Update();
        if (Plan is not { } m || _d is not { } d) return;
        if (RunReason is { } why) { _page.Shell.Toast(why, ToastKind.Warn); return; }
        var inc = m.Included(Channels(d));
        var items = inc.Select(n => new SendItem(n, m.ProgramFor(n), m.Vol.TryGetValue(n, out var v) ? v : double.NaN)).ToArray();
        var r = await ProgramSender.SendAsync(d, items).ConfigureAwait(true);
        string session = "";
        if (r.Started.Count > 0 && m.Record)
        {
            if (d.Actions.Why(DeviceAction.SessionStart).Length == 0)
            {
                var o = await d.Actions.RunAsync(DeviceAction.SessionStart, new ActionPayload(Text: m.Name), quiet: true).ConfigureAwait(true);
                session = o.Ok ? " · 会话已开始记录" : " · 会话没有开始：" + o.Message;
            }
            else if (d.Source.Session is { } cur) session = " · 会话 " + cur.Id + " 记录中";
        }
        _page.Experiments.AddRun(d.Source.Id, d.Alias, m, inc, r.Started, _page.Shell.Fleet.Now);
        if (r.Failed.Count == 0) _page.Shell.Toast("已按每路条件下发 " + r.Started.Count + " 个程序到 " + d.Alias + session, ToastKind.Ok);
        else
        {
            var (text, kind) = ProgramSender.Summary(d, m.Template.Name, r);
            _page.Shell.Toast(text + session, kind);
        }
        Changed();
    }
}

// ———————————————————— 行与格 ————————————————————

/// <summary>表头的一列（样稿 th）：参加的勾、识别色竖条、RVn、状态小字（运行中 · 15:19 / 空闲 / 已停用）。</summary>
public sealed partial class MxColVm : ObservableObject
{
    private readonly MatrixVm _owner;
    private bool _sync;

    public MxColVm(MatrixVm owner, int n)
    {
        _owner = owner;
        N = n;
        Rv = "RV" + n.ToString(CultureInfo.InvariantCulture);
        ChBrush = RunRowText.ChBrush(n);
        Dashed = n > 8;
    }

    public int N { get; }
    public string Rv { get; }
    public IBrush ChBrush { get; }
    /// <summary>RV9–16：识别色同 RV1–8，曲线上用虚线（表头不区分）。</summary>
    public bool Dashed { get; }
    [ObservableProperty] private bool _included;
    [ObservableProperty] private bool _off;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    /// <summary>「 · 15:19」（窄屏不写）。</summary>
    [ObservableProperty] private string _time = "";
    [ObservableProperty] private string _tip = "";

    partial void OnIncludedChanged(bool value) { if (!_sync) _owner.SetInclude(N, value); }

    internal void SetIncluded(bool v) { _sync = true; Included = v; _sync = false; }
}

/// <summary>矩阵的一行（基类）。</summary>
public abstract partial class MxRow : ObservableObject
{
    public virtual void Apply(MatrixPlan m, MxContext ctx, DeviceVm d) { }
}

/// <summary>一格输入（样稿 .mxi）：文字、可编辑（这一路参加）、超范围（琥珀）、改过模板值（粗体）、悬停说明。</summary>
public sealed partial class MxCellVm : ObservableObject
{
    private readonly Action<MxCellVm, string> _commit;
    private bool _sync;

    public MxCellVm(int n, Action<MxCellVm, string> commit) { N = n; _commit = commit; }

    public int N { get; }
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _bad;
    [ObservableProperty] private bool _chg;
    [ObservableProperty] private string? _tip;

    partial void OnTextChanged(string value) { if (!_sync) _commit(this, value); }

    internal void Set(string text)
    {
        if (Text == text) return;
        _sync = true;
        Text = text;
        _sync = false;
    }

    internal static double Parse(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
}

/// <summary>样品 / 催化剂（每路一个名字，只存在本机）。</summary>
public sealed class MxSampleRow : MxRow
{
    public MxSampleRow(MatrixVm owner, IEnumerable<int> ns) =>
        Cells = ns.Select(n => new MxCellVm(n, (c, t) => owner.SetSample(c.N, t))).ToArray();

    public IReadOnlyList<MxCellVm> Cells { get; }

    public override void Apply(MatrixPlan m, MxContext ctx, DeviceVm d)
    {
        foreach (var c in Cells)
        {
            c.Set(m.Samples.TryGetValue(c.N, out var s) ? s : "");
            c.Enabled = m.Include.Contains(c.N);
            c.Tip = c.Text.Length > 0 ? c.Text : null;
        }
    }
}

/// <summary>溶液体积（必填，1 – 上限 mL，0.1 mL）：悬停写气相体积怎么算。</summary>
public sealed class MxVolRow : MxRow
{
    public MxVolRow(MatrixVm owner, IEnumerable<int> ns, ICommand same)
    {
        Cells = ns.Select(n => new MxCellVm(n, (c, t) => owner.SetVolume(c.N, MxCellVm.Parse(t)))).ToArray();
        SameCommand = same;
    }

    public IReadOnlyList<MxCellVm> Cells { get; }
    public ICommand SameCommand { get; }

    public override void Apply(MatrixPlan m, MxContext ctx, DeviceVm d)
    {
        var vi = d.Source.Plant.Volume;
        foreach (var c in Cells)
        {
            double v = m.Vol.TryGetValue(c.N, out var x) ? x : double.NaN;
            bool inc = m.Include.Contains(c.N);
            string bad = inc ? MatrixPlan.VolBad(v, ctx.VolMax) : "";
            c.Set(double.IsFinite(v) ? JsNumber.ToFixed(v, 1) : "");
            c.Enabled = inc;
            c.Bad = bad.Length > 0;
            double? gas = inc && bad.Length == 0 ? vi?.GasVolume(c.N, v) : null;
            c.Tip = bad.Length > 0 ? "RV" + c.N + "：" + bad
                : gas is double g ? "RV" + c.N + " 气相体积约 " + JsNumber.ToFixed(g, 1) + " mL" + (vi!.Calibrated(c.N)
                    ? " = 有效容积 " + JsNumber.ToFixed(vi.EffectiveMl[c.N - 1]!.Value, 1) + " − 溶液 " + JsNumber.ToFixed(v, 1)
                    : " = 釜 " + JsNumber.ToString(vi.VesselMl) + " − 溶液 " + JsNumber.ToFixed(v, 1) + " + 管路 " + JsNumber.ToString(vi.LineMl)) + " · 用于计算吸气量"
                : inc ? "用于计算吸气量（设备按压降计算）" : null;
        }
    }
}

/// <summary>步骤的标题行（样稿 tr.sh）：序号、类型、名字。</summary>
public sealed class MxHeadRow : MxRow
{
    public MxHeadRow(string no, string type, string name) { No = no; TypeLabel = type; Name = name; }
    public string No { get; }
    public string TypeLabel { get; }
    public string Name { get; }
}

/// <summary>每路可以不同的参数（升温目标、升压目标、反应时长）：每格一个输入 +「统一」「等差」。</summary>
public sealed class MxPerRow : MxRow
{
    public MxPerRow(MatrixVm owner, int i, FieldSpec f, IEnumerable<int> ns)
    {
        I = i;
        Spec = f;
        // 输入是界面单位（压力按设置的单位），存内部单位（压力 MPa）
        Cells = ns.Select(n => new MxCellVm(n, (c, t) => owner.SetPer(I, Spec.Key, c.N, Spec.FromShown(MxCellVm.Parse(t))))).ToArray();
        SameCommand = new RelayCommand(() => owner.Same(I, Spec, false));
        RampCommand = new RelayCommand(() => owner.Same(I, Spec, true));
    }

    public int I { get; }
    public FieldSpec Spec { get; }
    public string Label => Spec.Label;
    public string Unit => Spec.ShowUnit;
    public IReadOnlyList<MxCellVm> Cells { get; }
    public ICommand SameCommand { get; }
    public ICommand RampCommand { get; }

    public override void Apply(MatrixPlan m, MxContext ctx, DeviceVm d)
    {
        var t = m.Steps[I].Type;
        foreach (var c in Cells)
        {
            double v = m.PerValue(I, Spec.Key, c.N);
            bool inc = m.Include.Contains(c.N);
            string bad = inc ? MatrixPlan.CellBad(ctx, t, Spec.Key, v) : "";
            c.Set(double.IsFinite(v) ? Spec.Num(v) : "");
            c.Enabled = inc;
            c.Bad = bad.Length > 0;
            c.Chg = inc && m.Changed(I, Spec.Key, c.N);
            c.Tip = bad.Length > 0 ? "RV" + c.N + "：" + bad : null;
        }
    }
}

/// <summary>共用参数（斜纹底，只填一次）：下拉或数值 + 为什么共用（硬件共用 = 链接图标，其它 = 说明图标）。</summary>
public sealed partial class MxSharedRow : MxRow
{
    private readonly MatrixVm _owner;
    private bool _sync;

    public MxSharedRow(MatrixVm owner, int i, StepType t, FieldSpec f)
    {
        _owner = owner;
        I = i;
        Spec = f;
        Why = MatrixPlan.Why(t, f.Key);
        WhyIcon = MatrixPlan.IsHardwareShared(t, f.Key) ? "link" : "info";
    }

    public int I { get; }
    public FieldSpec Spec { get; }
    public string Label => Spec.Label;
    public string Unit => Spec.ShowUnit;
    public bool IsSelect => Spec.IsSelect;
    public bool IsNum => !Spec.IsSelect;
    public IReadOnlyList<FieldOption> Options => Spec.Options;
    public string Why { get; }
    public string WhyIcon { get; }
    [ObservableProperty] private FieldOption? _selected;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _bad;
    [ObservableProperty] private string? _tip;

    partial void OnSelectedChanged(FieldOption? value) { if (!_sync && value != null) _owner.SetShared(I, Spec.Key, null, value.Code); }
    partial void OnTextChanged(string value) { if (!_sync) _owner.SetShared(I, Spec.Key, Spec.FromShown(Math.Round(MxCellVm.Parse(value), Spec.ShowDp)), null); }

    public override void Apply(MatrixPlan m, MxContext ctx, DeviceVm d)
    {
        var s = m.Steps[I];
        _sync = true;
        if (IsSelect)
        {
            var code = s.S(Spec.Key, Spec.Options.Count > 0 ? Spec.Options[0].Code : "");
            var opt = Spec.Options.FirstOrDefault(o => o.Code == code);
            if (!ReferenceEquals(opt, Selected)) Selected = opt;
        }
        else
        {
            double v = s.N(Spec.Key);
            var t = double.IsFinite(v) ? Spec.Num(v) : "";
            if (Text != t) Text = t;
            string bad = MatrixPlan.CellBad(ctx, s.Type, Spec.Key, v);
            Bad = bad.Length > 0;
            Tip = Bad ? bad : null;
        }
        _sync = false;
    }
}

/// <summary>表尾一格：这一路的预计用时（含升压排队）。</summary>
public sealed partial class MxFootCell : ObservableObject
{
    public MxFootCell(int n) => N = n;
    public int N { get; }
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _xc;
}

/// <summary>检查与预计里的一条（No = 琥珀 ⚠，Wait = 灰 ⏱）。</summary>
public sealed partial class MxIssueVm : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsWait))] private bool _isNo;
    [ObservableProperty] private string _msg = "";
    public bool IsWait => !IsNo;
}

/// <summary>各路预计温度图的一条线。</summary>
public sealed record MxSeries(int N, Color Color, bool Dashed, MxRoutePlan Plan);

/// <summary>各路预计温度图的数据（样稿 mxChart）。</summary>
public sealed record MxChartData(IReadOnlyList<MxSeries> Series);
