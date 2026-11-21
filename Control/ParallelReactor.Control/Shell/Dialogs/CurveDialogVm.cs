using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Heat;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>曲线窗口里的一段（可改）：目标温度、用时；右边自动写速率（「升 2.7 °C/min」/「保温」），升得太快 / 降温段带琥珀提示。</summary>
public sealed partial class CurveSegVm : ObservableObject
{
    private readonly Action _changed;

    public CurveSegVm(double temp, double minutes, Action changed, Action<CurveSegVm> remove)
    {
        _temp = temp;
        _minutes = minutes;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    [ObservableProperty] private string _index = "";
    [ObservableProperty] private double? _temp;
    [ObservableProperty] private double? _minutes;
    [ObservableProperty] private string _tempText = "";
    [ObservableProperty] private string _minText = "";
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private bool _isHold;
    [ObservableProperty] private string? _warn;
    [ObservableProperty] private bool _hasWarn;
    [ObservableProperty] private bool _isCur;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string? _removeReason;
    [ObservableProperty] private string _tempLabel = "";
    [ObservableProperty] private string _minLabel = "";

    public ICommand RemoveCommand { get; }

    public CurveSeg Seg => new(Temp ?? double.NaN, Minutes ?? double.NaN);

    partial void OnTempChanged(double? value) => _changed();
    partial void OnMinutesChanged(double? value) => _changed();
}

/// <summary>「同时用到」的一路：RV 号 + 一行小字（本路 / 当前温度 / 不能用的原因）；能用的可以点选。</summary>
public sealed partial class CurveChipVm : ObservableObject
{
    public CurveChipVm(int n, string rv, bool self, Action<CurveChipVm> toggle)
    {
        N = n;
        Rv = rv;
        IsSelf = self;
        _isOn = self;
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    public int N { get; }
    public string Rv { get; }
    public bool IsSelf { get; }
    public ICommand ToggleCommand { get; }

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string _sub = "";
    /// <summary>不能选的原因（null = 能选）。本路固定选中，也不能点。</summary>
    [ObservableProperty] private string? _reason;
    [ObservableProperty] private bool _isOff;
    [ObservableProperty] private string _tip = "";
}

/// <summary>预览图要画的东西（见 Controls/Heat/CurvePreview）。</summary>
public sealed record CurvePlot(IReadOnlyList<CurveRow> Rows, double StartC, double TempMaxC, double TotalMin, DateTime StartAt,
    bool Running, double ElapsedMin, double? SpC, double? PvC, string PvText);

/// <summary>
/// 曲线窗口（设计稿 curve-design 方向 B）：左边改段（目标温度 / 用时，自动算速率，最多 16 段），右边预览图（从当前温度画到走完），
/// 下面「同时用到」选几路用同一条曲线；底栏 取消 / 保存 /「保存并启动（3 路）」。曲线在走的时候只能看（段不能改 —— 触屏的规则），
/// 图上画出现在走到哪、实测温度，底栏换成 关闭 /「停止曲线」（停止先确认）。
/// </summary>
public sealed partial class CurveDialogVm : DialogVm
{
    private readonly ShellVm _shell;
    private readonly DeviceVm _d;
    private readonly int _n;
    private bool _closed, _building;
    private readonly FootButton _cancel, _save, _go, _close, _stop;
    private readonly FootBar _editBar, _runBar;

    public static CurveDialogVm? Create(ShellVm shell, string? arg)
    {
        var parts = (arg ?? "").Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) || shell.Fleet.Device(k) is not { } d) return null;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || d.Reactor(n) is not { Installed: true }) return null;
        var vm = new CurveDialogVm(shell, d, n);
        // 截图 / 地址参数：curve:k:n:6,8 预选几路
        if (parts.Length > 2)
            foreach (var s in parts[2].Split(','))
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && vm.Chips.FirstOrDefault(c => c.N == m) is { IsSelf: false, Reason: null } chip) chip.IsOn = true;
        vm.UpdateFoot();
        return vm;
    }

    public CurveDialogVm(ShellVm shell, DeviceVm d, int n)
    {
        _shell = shell;
        _d = d;
        _n = n;
        Name = "curve";
        Title = "RV" + n.ToString(CultureInfo.InvariantCulture) + " 曲线升温";
        Icon = null;
        Width = 980;
        Body = this;
        TempMax = d.Model.Limits.TempMaxC;
        AddCommand = new RelayCommand(AddSeg);
        SelectAllCommand = new RelayCommand(SelectAll);
        _cancel = new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this)));
        _save = new FootButton("保存", new AsyncRelayCommand(() => GoAsync(false)));
        _go = new FootButton("保存并启动", new AsyncRelayCommand(() => GoAsync(true)), FootKind.Primary, "play");
        _close = new FootButton("关闭", new RelayCommand(() => shell.Dialogs.Close(this)));
        _stop = new FootButton("停止曲线", new RelayCommand(() => shell.ShowDialog(StopConfirmDialogVm.CurveStop(shell, d, n))), FootKind.Secondary, "stop");
        _editBar = new FootBar(_cancel, _save, _go);
        _runBar = new FootBar(_close, _stop);
        Chips = d.Reactors.Where(x => x.Installed).Select(x => new CurveChipVm(x.N, x.Rv, x.N == n, Toggle)).ToArray();
        ChipColumns = Math.Min(8, Math.Max(1, Chips.Count));
        Build();
    }

    private ReactorVm? R => _d.Reactor(_n);

    public double TempMax { get; }
    public ObservableCollection<CurveSegVm> Rows { get; } = new();
    public IReadOnlyList<CurveChipVm> Chips { get; }
    public int ChipColumns { get; }
    public ICommand AddCommand { get; }
    public ICommand SelectAllCommand { get; }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isEdit = true;
    [ObservableProperty] private CurveRunText? _run;
    [ObservableProperty] private string? _addReason;
    [ObservableProperty] private string _addHint = "";
    [ObservableProperty] private IReadOnlyList<string> _warnings = Array.Empty<string>();
    [ObservableProperty] private bool _hasWarnings;
    [ObservableProperty] private string _sumText = "";
    [ObservableProperty] private string _afterText = "";
    [ObservableProperty] private CurvePlot? _plot;
    [ObservableProperty] private string _selText = "只用到本路";

    public override void OnClosed() => _closed = true;

    /// <summary>按设备现状重建（打开时、编辑 ↔ 运行切换时）。</summary>
    private void Build()
    {
        if (R is not { } r) return;
        var c = r.Curve ?? CurveReading.None;
        IsRunning = c.CurveMode && c.Running;
        IsEdit = !IsRunning;
        _building = true;
        Rows.Clear();
        foreach (var s in CurveOps.SegsFor(_d, r)) Rows.Add(new CurveSegVm(s.TempC, s.Minutes, Recalc, Remove));
        _building = false;
        Footer = IsRunning ? _runBar : _editBar;
        Recalc();
        Refresh();
    }

    private IReadOnlyList<CurveSeg> Segs => Rows.Select(x => x.Seg).ToArray();

    private void AddSeg()
    {
        if (Rows.Count >= HeatCurve.MaxSegs || !IsEdit) return;
        Rows.Add(new CurveSegVm(Rows.Count > 0 ? Rows[^1].Temp ?? 80 : 80, 30, Recalc, Remove));
        Recalc();
    }

    private void Remove(CurveSegVm row)
    {
        if (Rows.Count < 2 || !IsEdit) return;
        Rows.Remove(row);
        Recalc();
    }

    private void Toggle(CurveChipVm chip)
    {
        if (chip.IsSelf || chip.Reason != null || !IsEdit) return;
        chip.IsOn = !chip.IsOn;
        UpdateFoot();
    }

    private void SelectAll()
    {
        foreach (var c in Chips) if (!c.IsSelf && c.Reason == null) c.IsOn = true;
        UpdateFoot();
    }

    /// <summary>段变了 / 温度变了：重算每段的速率与提示、合计、预览图、底栏。</summary>
    private void Recalc()
    {
        if (_building || R is not { } r) return;
        var c = r.Curve ?? CurveReading.None;
        var segs = Segs;
        var now = CurveOps.NowOf(_shell, _d);
        double start = IsRunning ? c.StartC ?? (segs.Count > 0 ? segs[0].TempC : 25) : r.TempC ?? 25;
        // 没填的格子按上一段的温度 / 1 min 画，错误在底栏按钮上说
        var safe = new List<CurveSeg>(segs.Count);
        double prevT = start;
        foreach (var s in segs)
        {
            double t = double.IsFinite(s.TempC) ? s.TempC : prevT, m = double.IsFinite(s.Minutes) && s.Minutes > 0 ? s.Minutes : 1;
            safe.Add(new CurveSeg(t, m));
            prevT = t;
        }
        var rows = HeatCurve.Rows(safe, start);
        var at = IsRunning ? HeatCurve.At(rows, c.ElapsedMin) : null;
        bool done = IsRunning && (c.Complete || c.ElapsedMin >= c.TotalMin - 1e-6);
        var warns = new List<string>();
        for (int i = 0; i < Rows.Count; i++)
        {
            var vm = Rows[i];
            var row = rows[i];
            string w = HeatCurve.Warn(row);
            vm.Index = row.I.ToString(CultureInfo.InvariantCulture);
            vm.TempText = HeatCurve.N(row.TempC);
            vm.MinText = HeatCurve.N(row.Minutes);
            vm.RateText = HeatCurve.RateText(row);
            vm.IsHold = row.Kind == CurveKind.Hold;
            vm.Warn = w.Length > 0 ? w : null;
            vm.HasWarn = w.Length > 0;
            vm.IsCur = at != null && !done && row.I == at.Row.I;
            vm.IsDone = at != null && (done || row.I < at.Row.I);
            vm.RemoveReason = Rows.Count < 2 ? "至少要有 1 段" : null;
            vm.TempLabel = "第 " + row.I + " 段目标温度";
            vm.MinLabel = "第 " + row.I + " 段用时";
            if (w.Length > 0 && !warns.Contains(w)) warns.Add(w);
        }
        Warnings = warns;
        HasWarnings = warns.Count > 0 && IsEdit;
        AddReason = Rows.Count >= HeatCurve.MaxSegs ? "已到 16 段" : null;
        AddHint = Rows.Count >= HeatCurve.MaxSegs ? "已到 16 段" : "最多 16 段 · 目标和上一段相同就是保温";
        double total = HeatCurve.Total(safe);
        var startAt = IsRunning ? now.AddMinutes(-c.ElapsedMin) : now;
        SumText = HeatCurve.Head(safe) + " · 最高 " + HeatCurve.N(HeatCurve.Max(safe)) + " °C · 预计 " + Fmt.Hm(startAt.AddMinutes(total)) + " 走完";
        AfterText = "走完后保持 " + HeatCurve.N(safe.Count > 0 ? safe[^1].TempC : start) + " °C，直到停止加热";
        Plot = new CurvePlot(rows, start, TempMax, total, startAt, IsRunning, IsRunning ? Math.Min(c.ElapsedMin, total) : 0, r.SpC, r.TempC, r.TempText);
        Run = IsRunning ? CurveRunText.Of(c, r.SpC, r.TempText, now) : null;
        UpdateFoot();
    }

    private void UpdateFoot()
    {
        if (R is not { } r) return;
        var on = Chips.Where(c => c.IsOn && !c.IsSelf).ToArray();
        SelText = on.Length == 0 ? "只用到本路" : "本路 + " + string.Join("、", on.Select(c => c.Rv));
        FooterLeft = IsRunning ? null : SelText;
        _go.Label = on.Length == 0 ? "保存并启动" : "保存并启动（" + (on.Length + 1).ToString(CultureInfo.InvariantCulture) + " 路）";
        var segs = Segs;
        string err = HeatCurve.Error(segs, TempMax, _d.Source.Plant.OverTempC);
        _save.Reason = FootButton.Nz(err.Length > 0 ? err : CurveOps.Why(_d, r, segs, start: false));
        _go.Reason = FootButton.Nz(err.Length > 0 ? err : CurveOps.Why(_d, r, segs, start: true));
        _stop.Reason = FootButton.Nz(_d.Actions.Why(DeviceAction.CurveStop, ActionPayload.Ch(_n)));
    }

    public override void Refresh()
    {
        if (_closed) return;
        if (R is not { Installed: true } r) { _shell.Dialogs.Close(this); return; }
        var c = r.Curve ?? CurveReading.None;
        if ((c.CurveMode && c.Running) != IsRunning) { Build(); return; }
        Sub = _d.Alias + " · 当前 " + r.TempText + " °C · 上限 " + HeatCurve.N(TempMax) + " °C";
        var segs = Segs;
        foreach (var chip in Chips)
        {
            if (_d.Reactor(chip.N) is not { } x) continue;
            if (chip.IsSelf) { chip.Sub = "本路"; chip.Tip = "正在编辑的这一路"; continue; }
            string why = CurveOps.Why(_d, x, segs.Count > 0 && HeatCurve.Error(segs, TempMax).Length == 0 ? segs : HeatCurve.Default(TempMax), start: true);
            // 只读 / 离线这类整台设备的原因写在底栏按钮上，不逐路重复
            string devWhy = _d.Actions.Why(DeviceAction.CurveSet, new ActionPayload(N: _n, Segs: HeatCurve.Default(TempMax)));
            bool own = why.Length > 0 && why != devWhy.Replace("RV" + _n, "RV" + chip.N) && why.Contains("RV" + chip.N, StringComparison.Ordinal);
            chip.Reason = own ? why : null;
            chip.IsOff = own;
            chip.Sub = own ? Short(CurveOps.Strip(why, _d.Alias, chip.N)) : x.TempText + " °C";
            chip.Tip = own ? why : "点一下选中 / 取消";
            if (own && chip.IsOn) chip.IsOn = false;
        }
        Recalc();
    }

    /// <summary>「程序运行中」「已停用」「报警中」「曲线已在运行」「定值加热中」：取「 · 」前面的一截。</summary>
    private static string Short(string why)
    {
        int i = why.IndexOf(" · ", StringComparison.Ordinal);
        return i > 0 ? why[..i] : why;
    }

    private async Task GoAsync(bool start)
    {
        if (_closed || (start ? _go.Reason : _save.Reason) != null) return;
        var ns = new List<int> { _n };
        ns.AddRange(Chips.Where(c => c.IsOn && !c.IsSelf).Select(c => c.N));
        var segs = Segs;
        _shell.Dialogs.Close(this);
        await CurveOps.ApplyAsync(_shell, _d, ns, segs, start).ConfigureAwait(true);
    }
}
