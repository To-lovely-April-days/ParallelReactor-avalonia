using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>程序界面（PROGRAM Tab）：每通道独立的步骤程序编辑。</summary>
public partial class ProgramViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly Dictionary<int, ObservableCollection<ProgramStep>> _byRv = new();

    public ObservableCollection<ProgramChannel> Channels { get; } = new();
    public List<PaletteItem> Palette { get; }

    [ObservableProperty] private ProgramChannel? _currentChannel;
    [ObservableProperty] private ObservableCollection<ProgramStep> _steps = new();
    [ObservableProperty] private ProgramStep? _selectedStep;
    public ObservableCollection<object> InspectorRows { get; } = new();

    [ObservableProperty] private string _progTitle = "RV1 程序";
    [ObservableProperty] private string _progSub = "自定义程序";
    [ObservableProperty] private string _stepCountText = "0";
    [ObservableProperty] private string _durText = "—";
    [ObservableProperty] private string _emptyHint = "";

    public bool HasSelection => SelectedStep != null;
    public bool IsEmpty => Steps.Count == 0;
    public string SelInfo => SelectedStep == null
        ? "" : $"{SelectedStep.Ordinal:00} / {Steps.Count:00}";

    // 通道真正在运行（升温/反应/升压/超压）或程序执行中，程序锁定为只读；仅「已启用」不锁定
    public bool IsLocked => CurrentChannel?.Reactor.IsWorking == true || IsRunningProgram;
    public bool CanEdit => !IsLocked;
    public string LockHint => IsRunningProgram ? "程序执行中 · 已锁定为只读" : "该通道正在运行 · 程序已锁定为只读";

    private void OnReactorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Reactor.State) or nameof(Reactor.IsWorking)) RaiseLock();
    }

    private void RaiseLock()
    {
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(LockHint));
    }

    public ProgramViewModel(MainViewModel main)
    {
        _main = main;
        Palette = new[]
        {
            StepType.Purge, StepType.Heat, StepType.Press, StepType.Stir,
            StepType.Wait, StepType.React, StepType.End
        }.Select(t => new PaletteItem(t)).ToList();

        // 每通道开机为空程序：不预置示例步骤（程序 / 模板的保存与载入随配方一起实现）
        foreach (var rv in _main.Reactors)
        {
            _byRv[rv.Id] = new ObservableCollection<ProgramStep>();
            Channels.Add(new ProgramChannel(rv));
        }

        var first = Channels.FirstOrDefault(c => !c.Reactor.IsIdle) ?? Channels.First();
        SelectChannel(first);
    }

    // ============ 通道选择 ============
    [RelayCommand]
    private void SelectChannel(ProgramChannel ch)
    {
        if (ch.Reactor.IsIdle) return;
        if (CurrentChannel != null) CurrentChannel.Reactor.PropertyChanged -= OnReactorChanged;
        foreach (var c in Channels) c.IsCurrent = c == ch;
        CurrentChannel = ch;
        ch.Reactor.PropertyChanged += OnReactorChanged;
        RaiseLock();
        Steps = _byRv[ch.Reactor.Id];
        SelectedStep = null;
        RenumberAndRefresh();
    }

    // ============ 步骤库：点击添加到末尾（拖拽下一轮）============
    [RelayCommand]
    private void AddStep(PaletteItem item)
    {
        if (CurrentChannel == null || !CanEdit) return;
        var step = ProgramStep.New(item.Type);
        if (item.Type == StepType.Stir) step.With("rpm", (double)_main.StirRpm);
        int at = SelectedStep != null ? Steps.IndexOf(SelectedStep) + 1 : Steps.Count;
        Steps.Insert(at, step);
        SelectedStep = step;
        MarkModified();
        RenumberAndRefresh();
    }

    [RelayCommand]
    private void SelectStep(ProgramStep step)
    {
        SelectedStep = step;
    }

    // ============ 屏幕键盘：检查器数值字段 ============
    [RelayCommand]
    private void EditField(StepField f)
    {
        if (!CanEdit || f is not { IsNum: true }) return;
        _main.Keyboard.OpenNumeric(f.Label, f.Number, f.Unit ?? "", f.Min, f.Max, f.SetNumber);
    }

    // ============ 全局搅拌（程序里的「搅拌」步骤改为全局单步）============
    private StirRow? _stirRow;
    public string GlobalStirText => $"{_main.StirRpm} rpm";

    public void EditGlobalStir()
        => _main.Keyboard.OpenNumeric("搅拌转速（全局共用）", _main.StirRpm, "rpm", 0, _main.Settings.RpmMax, v =>
        {
            _main.StirRpm = (int)v;
            _stirRow?.Refresh();
            // 同步当前步骤数据，使时间线中的步骤详情一致
            if (SelectedStep?.Type == StepType.Stir) SelectedStep.SetNum("rpm", v);
        });

    // ============ 屏幕键盘：曲线段数值（温度 / 斜率 / 保温）============
    [RelayCommand]
    private void EditSegTemp(HeatSeg s)
    { if (s != null && CanEdit) _main.Keyboard.OpenNumeric("目标温度", s.Temp, "°C", 0, 300, v => s.Temp = v); }

    [RelayCommand]
    private void EditSegRate(HeatSeg s)
    { if (s != null && CanEdit) _main.Keyboard.OpenNumeric("升温斜率", s.Rate, "°C/min", 0, 50, v => s.Rate = v); }

    [RelayCommand]
    private void EditSegHold(HeatSeg s)
    { if (s != null && CanEdit) _main.Keyboard.OpenNumeric("保温时长", s.Hold, "min", 0, 600, v => s.Hold = v); }

    [RelayCommand]
    private void MoveUp(ProgramStep step)
    {
        if (!CanEdit) return;
        int i = Steps.IndexOf(step);
        if (i <= 0) return;
        Steps.Move(i, i - 1);
        SelectedStep = step;
        MarkModified();
        RenumberAndRefresh();
    }

    [RelayCommand]
    private void MoveDown(ProgramStep step)
    {
        if (!CanEdit) return;
        int i = Steps.IndexOf(step);
        if (i < 0 || i >= Steps.Count - 1) return;
        Steps.Move(i, i + 1);
        SelectedStep = step;
        MarkModified();
        RenumberAndRefresh();
    }

    [RelayCommand]
    private void DeleteStep(ProgramStep step)
    {
        if (!CanEdit) return;
        int i = Steps.IndexOf(step);
        if (i < 0) return;
        Steps.RemoveAt(i);
        if (SelectedStep == step) SelectedStep = null;
        MarkModified();
        RenumberAndRefresh();
    }

    // ============ 底部按钮 ============
    [RelayCommand] private void ApplyOthers() => OpenApply();
    [RelayCommand] private void SaveTemplate() => _main.Toast("warn", "保存为模板尚未实现（随配方功能一起提供）");

    // ============ 程序执行（引擎见 ProgramViewModel.Run.cs：7 种步骤全部可执行）============
    [ObservableProperty] private bool _isRunningProgram;
    [ObservableProperty] private string _runStatus = "";
    [ObservableProperty] private string _runMonitorDetail = "等待开始";
    [ObservableProperty] private string _runMonitorPhase = "准备运行";
    [ObservableProperty] private string _runProgressText = "0 / 0";
    [ObservableProperty] private int _runCurrentStepIndex = -1;
    [ObservableProperty] private bool _isRunMonitorOpen;
    [ObservableProperty] private bool _isRunConfirmOpen;
    [ObservableProperty] private string _runConfirmTitle = "";
    [ObservableProperty] private string _runConfirmSub = "";
    [ObservableProperty] private string _runConfirmText = "";
    public bool CanRun => !IsRunningProgram;
    public bool RunMonitorCanClose => !IsRunningProgram;
    public ObservableCollection<ProgramRunStep> RunSteps { get; } = new();
    public GraphViewModel MonitorGraph => _main.Graph;
    private ObservableCollection<ProgramStep>? _runSteps;
    private int _runRv;

    partial void OnIsRunningProgramChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(RunMonitorCanClose));
        RaiseLock();
    }

    partial void OnRunStatusChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) RunMonitorDetail = value;
    }

    [RelayCommand]
    private void RunProgram()
    {
        if (CurrentChannel == null) { Blocked("当前未选中通道（先点上方通道条里的 RV）"); return; }
        if (IsRunningProgram) return;
        if (IsEmpty) { Blocked("空程序无法下发 · 请先从左侧步骤库添加步骤"); return; }
        if (CurrentChannel.Reactor.IsWorking) { Blocked("该通道正在运行，不能下发程序"); return; }
        if (_main.Estopped) { Blocked("急停状态下不可下发程序 · 请先复位"); return; }

        bool gasSteps = Steps.Any(s => s.Type is StepType.Purge or StepType.Press or StepType.End);
        if (gasSteps && _main.GasBlockReason(needPressure: false) is { } why) { Blocked(why); return; }

        int rv = CurrentChannel.Reactor.Id;
        RunConfirmTitle = $"下发并运行 RV{rv} 程序";
        RunConfirmSub = gasSteps ? "含气路步骤 · 执行中气路独占" : "不含气路步骤";
        RunConfirmText =
            $"共 {Steps.Count} 步，预计约 {DurText}。\n" +
            "执行期间釜状态随步骤变化（升温/升压/等待/搅拌/反应/降温），可随时「中止」。" +
            (gasSteps ? "\n含气路步骤：执行期间气路由程序独占，泄漏测试 / 手动阀控 / 其他程序的气路步骤将被拒绝。" : "");
        _runSteps = Steps;
        _runRv = rv;
        IsRunConfirmOpen = true;
    }

    /// <summary>下发被守卫拦下：toast 一闪而过容易漏看，同步写一条事件记录（报警/事件页可查）。</summary>
    private void Blocked(string why)
    {
        _main.Toast("err", $"下发被拒绝 · {why}");
        _main.Alarm.Log("warn", "系统", "程序下发被拒绝", why);
    }

    [RelayCommand] private void CancelRun() => IsRunConfirmOpen = false;

    [RelayCommand]
    private void ConfirmRun()
    {
        IsRunConfirmOpen = false;
        if (_runSteps == null) return;
        if (_main.Estopped) { _main.Toast("err", "急停状态下不可下发程序 · 请先复位"); return; }   // 确认期间状态可能变了
        PrepareRunMonitor(_runSteps);
        _ = ExecuteAsync(_runRv, _runSteps.ToList());
    }

    private void PrepareRunMonitor(IEnumerable<ProgramStep> steps)
    {
        RunSteps.Clear();
        int index = 1;
        foreach (var step in steps) RunSteps.Add(new ProgramRunStep(index++, step));
        RunCurrentStepIndex = -1;
        RunProgressText = $"0 / {RunSteps.Count}";
        RunMonitorPhase = "准备执行";
        RunMonitorDetail = $"RV{_runRv} 程序正在准备启动";
        IsRunMonitorOpen = true;
        // 进入监控时优先展示最容易判断运行状态的温度曲线；用户仍可在右侧切换变量。
        MonitorGraph.Metric = "T";
        MonitorGraph.Range = "15";
    }

    [RelayCommand]
    private void CloseRunMonitor()
    {
        if (IsRunningProgram) return;
        IsRunMonitorOpen = false;
    }

    internal void MarkRunStepStarted(int index)
    {
        if (index < 0 || index >= RunSteps.Count) return;
        RunCurrentStepIndex = index;
        RunSteps[index].SetState(ProgramRunState.Running, "正在执行");
        RunProgressText = $"{index} / {RunSteps.Count}";
        RunMonitorPhase = $"执行第 {index + 1} 步 · {RunSteps[index].Name}";
    }

    internal void MarkRunStepCompleted(int index)
    {
        if (index < 0 || index >= RunSteps.Count) return;
        RunSteps[index].SetState(ProgramRunState.Completed, "已完成");
        RunProgressText = $"{index + 1} / {RunSteps.Count}";
    }

    internal void MarkRunStepFailed(int index, string message)
    {
        if (index < 0 || index >= RunSteps.Count) return;
        RunSteps[index].SetState(ProgramRunState.Failed, message);
        RunMonitorPhase = $"第 {index + 1} 步执行失败";
    }

    internal void MarkRunStepAborted(int index)
    {
        if (index < 0 || index >= RunSteps.Count) return;
        RunSteps[index].SetState(ProgramRunState.Aborted, "已中止");
        RunMonitorPhase = "程序已手动中止";
    }

    /// <summary>中止当前程序（引擎在 Run.cs：取消令牌 + 中止气路，序列 finally 全关阀）。</summary>
    [RelayCommand] private void AbortRun() => AbortProgram();

    // ============ 升温模式切换（HeatModeRow 调用）============
    public void SetHeatMode(ProgramStep step, string mode)
    {
        if (!CanEdit) return;
        step.SetStr("mode", mode);
        if (mode == "curve") step.EnsureSegments();
        MarkModified();
        BuildInspector();
        RenumberAndRefresh();
    }

    // ============ 拖拽（供 code-behind 调用）============
    public void DropPaletteAt(StepType t, int index)
    {
        var step = ProgramStep.New(t);
        if (t == StepType.Stir) step.With("rpm", (double)_main.StirRpm);
        index = Math.Clamp(index, 0, Steps.Count);
        Steps.Insert(index, step);
        SelectedStep = step;
        MarkModified();
        RenumberAndRefresh();
    }

    public void ReorderTo(ProgramStep step, int index)
    {
        int from = Steps.IndexOf(step);
        if (from < 0) return;
        if (index > from) index--;
        Steps.RemoveAt(from);
        index = Math.Clamp(index, 0, Steps.Count);
        Steps.Insert(index, step);
        SelectedStep = step;
        MarkModified();
        RenumberAndRefresh();
    }

    // ============ 内部刷新 ============
    partial void OnSelectedStepChanged(ProgramStep? oldValue, ProgramStep? newValue)
    {
        if (oldValue != null) oldValue.Changed -= OnStepEdited;
        if (newValue != null) newValue.Changed += OnStepEdited;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelInfo));
        BuildInspector();
    }

    private void OnStepEdited()
    {
        MarkModified();
        RefreshMeta();
    }

    private void MarkModified()
    {
        if (CurrentChannel != null) CurrentChannel.IsModified = true;
    }

    private void RenumberAndRefresh()
    {
        for (int i = 0; i < Steps.Count; i++) Steps[i].Ordinal = i + 1;
        OnPropertyChanged(nameof(IsEmpty));
        RefreshMeta();
    }

    private void RefreshMeta()
    {
        int rv = CurrentChannel?.Reactor.Id ?? 0;
        ProgTitle = $"RV{rv} 程序";
        ProgSub = "自定义程序";
        StepCountText = Steps.Count.ToString();
        if (Steps.Count == 0) { DurText = "—"; }
        else
        {
            int total = (int)Steps.Sum(s => s.DurationMin);
            DurText = $"{total / 60}h {total % 60}m";
        }
        EmptyHint = $"RV{rv} 当前 {Steps.Count} 步";
        OnPropertyChanged(nameof(SelInfo));
    }

    private void BuildInspector()
    {
        InspectorRows.Clear();
        var s = SelectedStep;
        if (s == null) { RefreshMeta(); return; }

        if (s.Type == StepType.Heat)
        {
            bool fixedMode = s.Str("mode", "fixed") != "curve";
            InspectorRows.Add(new HeatModeRow(this, s));
            if (fixedMode)
            {
                foreach (var f in s.Def.Fields) InspectorRows.Add(new StepField(s, f));
            }
            else
            {
                s.EnsureSegments();
                InspectorRows.Add(new CurveRow(this, s));
            }
        }
        else if (s.Type == StepType.Stir)
        {
            // 搅拌转速是全机共用设置；搅拌时长仍属于当前步骤，必须在检查器中单独显示。
            _stirRow = new StirRow(this);
            InspectorRows.Add(_stirRow);
            foreach (var f in s.Def.Fields.Where(f => f.Key == "dur"))
                InspectorRows.Add(new StepField(s, f));
        }
        else
        {
            foreach (var f in s.Def.Fields) InspectorRows.Add(new StepField(s, f));
        }
        InspectorRows.Add(new HelpRow(s.Def.Help));
    }

    // ============ 16 段曲线编辑器弹窗 ============
    private const double StartT = 25;
    private ProgramStep? _curveStep;

    [ObservableProperty] private bool _isCurveEditorOpen;
    [ObservableProperty] private string _curveSub = "";
    [ObservableProperty] private string _curveTotalLabel = "总时长 ≈ 0 min";
    [ObservableProperty] private string _curveScope = "all";
    [ObservableProperty] private Geometry? _curveLineGeo;
    [ObservableProperty] private Geometry? _curveAreaGeo;
    [ObservableProperty] private Geometry? _curveGridGeo;
    public ObservableCollection<HeatSeg> CurveSegs { get; } = new();
    public bool CanAddSeg => CurveSegs.Count < 16;
    public bool CurveScopeIsAll => CurveScope == "all";
    public bool CurveScopeIsCur => CurveScope == "cur";

    public void OpenCurveEditor(ProgramStep step)
    {
        _curveStep = step;
        step.EnsureSegments();
        foreach (var s in CurveSegs) s.PropertyChanged -= OnCurveSegChanged;
        CurveSegs.Clear();
        foreach (var seg in step.Segments)
        {
            var c = seg.Clone();
            c.PropertyChanged += OnCurveSegChanged;
            CurveSegs.Add(c);
        }
        CurveScope = "all";
        RecomputeCurve();
        IsCurveEditorOpen = true;
    }

    private void OnCurveSegChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HeatSeg.No)) return;
        RecomputeCurve();
    }

    partial void OnCurveScopeChanged(string value)
    {
        OnPropertyChanged(nameof(CurveScopeIsAll));
        OnPropertyChanged(nameof(CurveScopeIsCur));
        UpdateCurveSub();
    }

    [RelayCommand] private void CurveScopeAll() => CurveScope = "all";
    [RelayCommand] private void CurveScopeCur() => CurveScope = "cur";

    [RelayCommand]
    private void AddSeg()
    {
        if (CurveSegs.Count >= 16) { _main.Toast("warn", "已达到最多 16 段"); return; }
        var last = CurveSegs.Count > 0 ? CurveSegs[^1] : null;
        var seg = new HeatSeg { Temp = last?.Temp ?? 50, Rate = 0, Hold = 30 };
        seg.PropertyChanged += OnCurveSegChanged;
        CurveSegs.Add(seg);
        RecomputeCurve();
    }

    [RelayCommand]
    private void DelSeg(HeatSeg seg)
    {
        if (CurveSegs.Count <= 1) { _main.Toast("warn", "至少保留 1 段"); return; }
        seg.PropertyChanged -= OnCurveSegChanged;
        CurveSegs.Remove(seg);
        RecomputeCurve();
    }

    [RelayCommand] private void CancelCurve() => IsCurveEditorOpen = false;

    [RelayCommand]
    private void ApplyCurve()
    {
        if (_curveStep == null) { IsCurveEditorOpen = false; return; }
        void Copy(ProgramStep dst)
        {
            dst.Segments.Clear();
            foreach (var s in CurveSegs) dst.Segments.Add(s.Clone());
            dst.SetStr("mode", "curve");
            dst.RaiseChanged();
        }
        Copy(_curveStep);
        int idx = Steps.IndexOf(_curveStep);
        if (CurveScope == "all" && idx >= 0)
        {
            foreach (var ch in Channels)
            {
                if (ch.Reactor.IsIdle || ch == CurrentChannel) continue;
                var list = _byRv[ch.Reactor.Id];
                if (idx < list.Count && list[idx].Type == StepType.Heat)
                {
                    Copy(list[idx]);
                    ch.IsModified = true;
                }
            }
            _main.Toast("ok", $"已应用 {CurveSegs.Count} 段曲线到全部运行中通道");
        }
        else _main.Toast("ok", $"已应用 {CurveSegs.Count} 段曲线到 RV{CurrentChannel?.Reactor.Id}");

        MarkModified();
        IsCurveEditorOpen = false;
        BuildInspector();
        RenumberAndRefresh();
    }

    private void UpdateCurveSub()
    {
        string ch = CurveScope == "all" ? "全部运行中" : $"RV{CurrentChannel?.Reactor.Id}";
        CurveSub = $"应用通道：{ch} · 起始温度 {StartT:0} °C · 共 {CurveSegs.Count} 段";
    }

    private void RecomputeCurve()
    {
        for (int i = 0; i < CurveSegs.Count; i++) CurveSegs[i].No = i + 1;
        OnPropertyChanged(nameof(CanAddSeg));
        UpdateCurveSub();

        const double W = 800, H = 240, padL = 42, padR = 14, padT = 14, padB = 24;
        double t = 0, curT = StartT;
        var pts = new List<(double t, double v)> { (0, curT) };
        foreach (var seg in CurveSegs)
        {
            double ramp = seg.Rate > 0 ? Math.Abs(seg.Temp - curT) / seg.Rate : 0;
            if (ramp > 0) { t += ramp; pts.Add((t, seg.Temp)); }
            if (seg.Hold > 0) { t += seg.Hold; pts.Add((t, seg.Temp)); }
            curT = seg.Temp;
        }
        double totalT = Math.Max(t, 1);
        double maxV = pts.Max(p => p.v);
        double maxT = Math.Max(200, Math.Ceiling((maxV + 20) / 50) * 50);
        double X(double v) => padL + (W - padL - padR) * (v / totalT);
        double Y(double v) => padT + (H - padT - padB) * (1 - v / maxT);

        var grid = new System.Text.StringBuilder();
        for (double v = 0; v <= maxT; v += 50)
            grid.Append($"M{Fmt(X(0))},{Fmt(Y(v))} L{Fmt(X(totalT))},{Fmt(Y(v))} ");
        CurveGridGeo = Geometry.Parse(grid.ToString());

        var line = new System.Text.StringBuilder();
        for (int i = 0; i < pts.Count; i++)
            line.Append((i == 0 ? "M" : "L") + Fmt(X(pts[i].t)) + "," + Fmt(Y(pts[i].v)) + " ");
        CurveLineGeo = Geometry.Parse(line.ToString());
        CurveAreaGeo = Geometry.Parse(line.ToString() + $"L{Fmt(X(totalT))},{Fmt(Y(0))} L{Fmt(X(0))},{Fmt(Y(0))} Z");

        CurveTotalLabel = $"总时长 ≈ {Math.Round(totalT)} min";
    }

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // ============ 应用到其他通道弹窗 ============
    [ObservableProperty] private bool _isApplyOpen;
    [ObservableProperty] private string _applyTitle = "";
    [ObservableProperty] private string _applySub = "";
    public ObservableCollection<ApplyRow> ApplyRows { get; } = new();

    private void OpenApply()
    {
        if (CurrentChannel == null) return;
        int rv = CurrentChannel.Reactor.Id;
        ApplyTitle = $"应用 RV{rv} 程序到其他通道";
        ApplySub = $"RV{rv} 的 {Steps.Count} 步程序 · 勾选目标通道";
        ApplyRows.Clear();
        foreach (var ch in Channels)
        {
            bool cur = ch == CurrentChannel;
            bool picked = !ch.Reactor.IsIdle && !cur;
            ApplyRows.Add(new ApplyRow(ch, cur, _byRv[ch.Reactor.Id].Count, picked));
        }
        IsApplyOpen = true;
    }

    [RelayCommand] private void ApplyQuickActive() => SetApplyQuick("active");
    [RelayCommand] private void ApplyQuickAll() => SetApplyQuick("all");
    [RelayCommand] private void ApplyQuickNone() => SetApplyQuick("none");
    private void SetApplyQuick(string m)
    {
        foreach (var r in ApplyRows)
        {
            if (!r.CanPick) { r.IsPicked = false; continue; }
            r.IsPicked = m switch
            {
                "all" => true,
                "active" => !r.Channel.Reactor.IsDone,
                _ => false
            };
        }
    }

    [RelayCommand] private void CancelApply() => IsApplyOpen = false;

    [RelayCommand]
    private void ApplyDo()
    {
        var targets = ApplyRows.Where(r => r.CanPick && r.IsPicked).ToList();
        if (targets.Count == 0) { _main.Toast("warn", "请先选择目标通道"); return; }
        var src = Steps;
        foreach (var r in targets)
        {
            var list = _byRv[r.Channel.Reactor.Id];
            list.Clear();
            foreach (var s in src) list.Add(s.Clone());
            for (int i = 0; i < list.Count; i++) list[i].Ordinal = i + 1;
            r.Channel.IsModified = false;
        }
        _main.Toast("ok", $"已将 RV{CurrentChannel?.Reactor.Id} 的 {src.Count} 步程序应用到 {targets.Count} 个通道");
        IsApplyOpen = false;
    }

}

// ============ 辅助视图模型 ============

/// <summary>步骤库的一个卡片。</summary>
public class PaletteItem
{
    public StepType Type { get; }
    public string Name { get; }
    public Geometry Icon { get; }
    public IBrush IconBrush { get; }
    public PaletteItem(StepType t)
    {
        Type = t;
        var d = StepDef.Registry[t];
        Name = d.Name; Icon = d.Icon; IconBrush = d.Brush;
    }
}

public enum ProgramRunState { Pending, Running, Completed, Failed, Aborted }

/// <summary>运行监控左侧的一步程序状态。</summary>
public partial class ProgramRunStep : ObservableObject
{
    public int Number { get; }
    public ProgramStep Step { get; }
    public string Name => Step.Name;
    public string Detail => Step.Detail;
    public string IndexText => Number.ToString("00");

    [ObservableProperty] private ProgramRunState _state = ProgramRunState.Pending;
    [ObservableProperty] private string _message = "待执行";

    public bool IsCurrent => State == ProgramRunState.Running;
    public bool IsCompleted => State == ProgramRunState.Completed;
    public bool IsFailed => State == ProgramRunState.Failed;
    public bool IsAborted => State == ProgramRunState.Aborted;

    public string StatusText => State switch
    {
        ProgramRunState.Running => "执行中",
        ProgramRunState.Completed => "已完成",
        ProgramRunState.Failed => "失败",
        ProgramRunState.Aborted => "已中止",
        _ => "待执行"
    };

    public string StatusGlyph => State switch
    {
        ProgramRunState.Running => "…",
        ProgramRunState.Completed => "✓",
        ProgramRunState.Failed => "!",
        ProgramRunState.Aborted => "×",
        _ => "·"
    };

    public IBrush StateBrush => State switch
    {
        ProgramRunState.Running => new SolidColorBrush(Color.Parse("#C11B2E")),
        ProgramRunState.Completed => new SolidColorBrush(Color.Parse("#4d9a12")),
        ProgramRunState.Failed => new SolidColorBrush(Color.Parse("#d5394d")),
        ProgramRunState.Aborted => new SolidColorBrush(Color.Parse("#b5750a")),
        _ => new SolidColorBrush(Color.Parse("#8a8b94"))
    };

    public IBrush StateBackground => State switch
    {
        ProgramRunState.Running => new SolidColorBrush(Color.Parse("#16C11B2E")),
        ProgramRunState.Completed => new SolidColorBrush(Color.Parse("#1684CC16")),
        ProgramRunState.Failed => new SolidColorBrush(Color.Parse("#16E0394C")),
        ProgramRunState.Aborted => new SolidColorBrush(Color.Parse("#16F0A830")),
        _ => new SolidColorBrush(Color.Parse("#0D000000"))
    };

    public ProgramRunStep(int number, ProgramStep step)
    {
        Number = number;
        Step = step;
    }

    public void SetState(ProgramRunState state, string message)
    {
        State = state;
        Message = message;
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsAborted));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(StateBackground));
    }
}

/// <summary>通道选择条的一个通道。</summary>
public partial class ProgramChannel : ObservableObject
{
    public Reactor Reactor { get; }
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isModified;
    public ProgramChannel(Reactor r)
    {
        Reactor = r;
        // 通道状态在主界面变化（启用 / 停用 / 启停）时，同步刷新选择条小字
        r.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Reactor.State)) OnPropertyChanged(nameof(SmallText));
        };
    }
    public string Title => $"RV{Reactor.Id}";
    public string SmallText => Reactor.IsIdle ? "停用" : Reactor.StateZh;
}

/// <summary>检查器：升温方式（定值 / 16 段曲线）切换行。</summary>
public partial class HeatModeRow : ObservableObject
{
    private readonly ProgramViewModel _vm;
    private readonly ProgramStep _step;
    public HeatModeRow(ProgramViewModel vm, ProgramStep step) { _vm = vm; _step = step; }
    public bool IsFixed => _step.Str("mode", "fixed") != "curve";
    public bool IsCurve => !IsFixed;

    [RelayCommand] private void Fixed() => _vm.SetHeatMode(_step, "fixed");
    [RelayCommand] private void Curve() => _vm.SetHeatMode(_step, "curve");
}

/// <summary>检查器：全局搅拌转速（共用一台搅拌器，所有反应釜同一转速）。</summary>
public partial class StirRow : ObservableObject
{
    private readonly ProgramViewModel _vm;
    public StirRow(ProgramViewModel vm) { _vm = vm; }
    public string RpmText => _vm.GlobalStirText;
    public void Refresh() => OnPropertyChanged(nameof(RpmText));
    [RelayCommand] private void Edit() => _vm.EditGlobalStir();
}

/// <summary>检查器：曲线预览卡片。</summary>
public partial class CurveRow : ObservableObject
{
    private readonly ProgramViewModel _vm;
    private readonly ProgramStep _step;
    public CurveRow(ProgramViewModel vm, ProgramStep step)
    {
        _vm = vm; _step = step;
        Points = BuildPoints(step.Segments);
    }

    public string SegCountText => $"{_step.Segments.Count} / 16";
    public string EndTempText => $"{StepDef.N(_step.Segments[^1].Temp)} °C";
    public List<Point> Points { get; }

    [RelayCommand] private void Edit() => _vm.OpenCurveEditor(_step);

    private static List<Point> BuildPoints(List<HeatSeg> segs)
    {
        const double W = 248, H = 78, padL = 22, padR = 6, padT = 6, padB = 12, startT = 25;
        double t = 0, curT = startT;
        var raw = new List<(double t, double v)> { (0, curT) };
        foreach (var seg in segs)
        {
            double ramp = seg.Rate > 0 ? Math.Abs(seg.Temp - curT) / seg.Rate : 0;
            if (ramp > 0) { t += ramp; raw.Add((t, seg.Temp)); }
            if (seg.Hold > 0) { t += seg.Hold; raw.Add((t, seg.Temp)); }
            curT = seg.Temp;
        }
        double totalT = Math.Max(t, 1);
        double maxV = raw.Count > 0 ? raw.Max(p => p.v) : 200;
        double maxT = Math.Max(200, Math.Ceiling((maxV + 20) / 50) * 50);
        double X(double v) => padL + (W - padL - padR) * (v / totalT);
        double Y(double v) => padT + (H - padT - padB) * (1 - v / maxT);
        return raw.Select(p => new Point(X(p.t), Y(p.v))).ToList();
    }
}

/// <summary>检查器：底部帮助文字。</summary>
public class HelpRow
{
    public string Text { get; }
    public HelpRow(string text) { Text = text; }
}

/// <summary>「应用到其他通道」弹窗的一行。</summary>
public partial class ApplyRow : ObservableObject
{
    public ProgramChannel Channel { get; }
    public bool IsCurrent { get; }
    public bool CanPick { get; }
    [ObservableProperty] private bool _isPicked;

    public ApplyRow(ProgramChannel ch, bool cur, int stepN, bool picked)
    {
        Channel = ch;
        IsCurrent = cur;
        CanPick = !ch.Reactor.IsIdle && !cur;
        _isPicked = picked && CanPick;
        StepsText = $"{stepN} 步" + (ch.IsModified ? " · 已独立修改" : "");
    }

    public string Name => $"RV{Channel.Reactor.Id}";
    public string StateText => Channel.Reactor.IsIdle ? "已停用" : Channel.Reactor.StateZh;
    public string StepsText { get; }

    // 自定义勾选指示（主题红）
    partial void OnIsPickedChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckBg));
        OnPropertyChanged(nameof(CheckBorder));
    }
    private static IBrush PB(string hex) => new SolidColorBrush(Color.Parse(hex));
    public IBrush CheckBg => IsPicked ? PB("#e0394c") : PB("#00000000");
    public IBrush CheckBorder => IsPicked ? PB("#e0394c") : PB("#3D000000");

    [RelayCommand]
    private void Toggle() { if (CanPick) IsPicked = !IsPicked; }
}
