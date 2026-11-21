using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Heat;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Shell;

/// <summary>检查器曲线摘要里的一行：「1　升到 80 °C　20 min」。</summary>
public sealed record CurveStepItem(string I, string Text, string Min);

/// <summary>曲线进度条的一段：宽度按用时、走完 / 当前（当前段带完成比例）/ 没到。</summary>
public sealed record CurveBarItem(double Weight, bool Done, bool Current, double F);

/// <summary>运行中那一块的文字（检查器和曲线窗口共用）。</summary>
public sealed record CurveRunText(string Title, string Step, string Left, string Sub, IReadOnlyList<CurveBarItem> Bars, int Seg)
{
    /// <summary><paramref name="tempText"/> = 实测温度的文字；<paramref name="now"/> = 设备的现在。</summary>
    public static CurveRunText Of(CurveReading c, double? spC, string tempText, DateTime now)
    {
        double start = c.StartC ?? (c.Segs.Count > 0 ? c.Segs[0].TempC : 25);
        var rows = HeatCurve.Rows(c.Segs, start);
        var at = HeatCurve.At(rows, c.ElapsedMin);
        if (at == null) return new CurveRunText("", "", "", "", Array.Empty<CurveBarItem>(), 0);
        bool done = c.Complete || c.ElapsedMin >= c.TotalMin - 1e-6;
        var bars = rows.Select(r => new CurveBarItem(Math.Max(r.Minutes, 0.1), done || r.I < at.Row.I, !done && r.I == at.Row.I, at.F)).ToArray();
        string last = HeatCurve.N(rows[^1].TempC);
        string sp = (spC ?? at.SpC).ToString("0.0", CultureInfo.InvariantCulture);
        if (done)
            return new CurveRunText("已走完", "保持 " + last + " °C", "", Nb("给定 " + sp + " °C · 实测 " + tempText + " °C · 直到停止曲线或停止加热"), bars, rows.Count);
        return new CurveRunText("第 " + at.Row.I + "/" + rows.Count + " 段", HeatCurve.StepText(at.Row), "本段剩 " + HeatCurve.Dur(Math.Ceiling(at.LeftMin)),
            Nb("给定 " + sp + " °C · 实测 " + tempText + " °C · 预计 " + Fmt.Hm(now.AddMinutes(c.TotalMin - c.ElapsedMin)) + " 走完，之后保持 " + last + " °C"), bars, at.Row.I);
    }

    /// <summary>数值和单位不拆行（「150 °C」不会断成两行）。</summary>
    public static string Nb(string s) => s.Replace(" °C", "\u00a0°C", StringComparison.Ordinal);
}

/// <summary>
/// 检查器「温度控制」的曲线部分（设计稿 curve-design 方向 B，2026-11-21）：升温方式 定值 / 曲线；曲线时放摘要（共几段、多久、最高、预计几点走完、各段一行）
/// + 「编辑曲线」「启动曲线」；运行中放第几段、本段剩多久、分段进度条、给定 / 实测 + 「查看曲线」「停止曲线」。
/// </summary>
public sealed partial class ChannelCtlVm
{
    private readonly SegOption _optFixed = new() { Value = "fixed", Label = "定值" };
    private readonly SegOption _optCurve = new() { Value = "curve", Label = "曲线" };
    private string _modeKey = "fixed";

    public IReadOnlyList<SegOption> ModeOptions => _modeOptions ??= new[] { _optFixed, _optCurve };
    private IReadOnlyList<SegOption>? _modeOptions;

    /// <summary>升温方式（分段控件，双向）：点了就发 HeatMode；显示的始终是设备上的方式。</summary>
    public string ModeKey
    {
        get => _modeKey;
        set
        {
            if (value is not ("fixed" or "curve") || value == _modeKey) return;
            Act(DeviceAction.HeatMode, new ActionPayload(N: _n, Id: value));
            OnPropertyChanged();   // 先显示回设备上的方式，设备改了以后下一帧再变
        }
    }

    [ObservableProperty] private bool _isFixed = true;
    [ObservableProperty] private bool _isCurve;
    /// <summary>曲线方式、没在走：摘要 + 编辑 / 启动。</summary>
    [ObservableProperty] private bool _curveIdle;
    [ObservableProperty] private bool _curveRunning;
    [ObservableProperty] private string _curveHead = "";
    [ObservableProperty] private string _curveMax = "";
    [ObservableProperty] private string _curveSub = "";
    [ObservableProperty] private IReadOnlyList<CurveStepItem> _curveSteps = Array.Empty<CurveStepItem>();
    [ObservableProperty] private CurveRunText? _curveRun;
    [ObservableProperty] private string? _curveStartReason;
    [ObservableProperty] private string? _curveEditReason;
    [ObservableProperty] private string? _curveStopReason;

    public ICommand CurveEditCommand => _curveEdit ??= new RelayCommand(OpenCurve);
    public ICommand CurveStartCommand => _curveStart ??= new RelayCommand(StartCurve);
    public ICommand CurveStopCommand => _curveStop ??= new RelayCommand(() => { if (D is { } d) _shell.ShowDialog(Dialogs.StopConfirmDialogVm.CurveStop(_shell, d, _n)); });
    private ICommand? _curveEdit, _curveStart, _curveStop;

    private void OpenCurve() => _shell.OpenDialog("curve", _k.ToString(CultureInfo.InvariantCulture) + ":" + _n.ToString(CultureInfo.InvariantCulture));

    private void StartCurve()
    {
        if (D is not { } d || R is not { } r) return;
        _ = CurveOps.ApplyAsync(_shell, d, new[] { r.N }, CurveOps.SegsFor(d, r), start: true);
    }

    private void RefreshCurve(DeviceVm d, ReactorVm r)
    {
        var c = r.Curve ?? CurveReading.None;
        string key = c.CurveMode ? "curve" : "fixed";
        if (key != _modeKey) { _modeKey = key; OnPropertyChanged(nameof(ModeKey)); }
        _optFixed.Reason = c.CurveMode ? Nz(d.Actions.Why(DeviceAction.HeatMode, new ActionPayload(N: r.N, Id: "fixed"))) : null;
        _optCurve.Reason = c.CurveMode ? null : Nz(d.Actions.Why(DeviceAction.HeatMode, new ActionPayload(N: r.N, Id: "curve")));
        IsCurve = c.CurveMode;
        IsFixed = !c.CurveMode;
        CurveRunning = c.CurveMode && c.Running;
        CurveIdle = c.CurveMode && !c.Running;
        if (!c.CurveMode) return;

        var now = CurveOps.NowOf(_shell, d);
        if (c.Running)
        {
            CurveRun = CurveRunText.Of(c, r.SpC, r.TempText, now);
            CurveStopReason = Nz(d.Actions.Why(DeviceAction.CurveStop, ActionPayload.Ch(r.N)));
            return;
        }
        var segs = CurveOps.SegsFor(d, r);
        double start = r.TempC ?? 25;
        var rows = HeatCurve.Rows(segs, start);
        CurveHead = HeatCurve.Head(segs);
        CurveMax = " · 最高 " + HeatCurve.N(HeatCurve.Max(segs)) + " °C";
        CurveSub = CurveRunText.Nb("从当前 " + start.ToString("0.0", CultureInfo.InvariantCulture) + " °C 开始 · 预计 " + Fmt.Hm(now.AddMinutes(HeatCurve.Total(segs)))
            + " 走完，之后保持 " + HeatCurve.N(segs[^1].TempC) + " °C");
        var steps = rows.Select(x => new CurveStepItem(x.I.ToString(CultureInfo.InvariantCulture), HeatCurve.StepText(x), HeatCurve.N(x.Minutes) + " min")).ToArray();
        if (!steps.SequenceEqual(CurveSteps)) CurveSteps = steps;
        CurveStartReason = Nz(CurveOps.Why(d, r, segs, start: true));
        CurveEditReason = null;
    }
}
