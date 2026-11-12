using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.ViewModels;
using ParallelReactor.PerfTest.Model;
using DeviceActions = ParallelReactor.Control.Fleet.DeviceActions;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 旧样式的页面（程序编辑、性能测试，D5：M2 起挂进新外壳，M7 用新组件重做）在旧视图里用到的几个按钮：面板「展开」「关闭」（旧界面是覆盖层副本；
/// 新外壳里「展开」= 这一页只显示这块面板，「关闭」同旧界面只提示「（示意）」）。旧视图经 LegacyHost 的 DataContext 找到它（不再找 MainViewModel）。
/// </summary>
public interface ILegacyPage
{
    void ToggleExpand(string panel);
    void NotImplemented(string what);
}

/// <summary>
/// 程序页（旧样式，PORT-PLAN §5 M2「LegacyProgramPageVm：包住 PlanEditorVm，接管原 MainViewModel 的 ToggleEdit、Upload、RunProgram、Export」）。
/// 草稿按 RV 号存在本机（ProgramDraftStore，键 0 = 新建程序），反应釜用旧界面那一排 RV 选择（新建程序、RV1–RV8）；
/// 下发 / 运行作用于实时设备（M2 只有一台；演示数据里提示「未连接设备」）。下发需要控制权，执行前照样查守卫（同新界面的 DeviceActions）。
/// </summary>
public sealed partial class LegacyProgramPageVm : PageVm, ILegacyPage
{
    private readonly ShellVm _shell;
    private readonly ProgramDraftStore _drafts = new();

    public LegacyProgramPageVm(ShellVm shell) : base(Route.Placeholder("program"))
    {
        _shell = shell;
        Plan.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlanEditorVm.IsEditing)) OnPropertyChanged(nameof(EditButtonText)); };
        Rvs = Enumerable.Range(0, 9).Select(i => new LegacyRvVm(i, i == 0 ? "新建程序" : "RV" + i, SelectRv)).ToList();
        SelectRv(1);
    }

    public PlanEditorVm Plan { get; } = new();

    /// <summary>旧界面那一排：新建程序、RV1–RV8（选中的写进程序编辑器）。</summary>
    public IReadOnlyList<LegacyRvVm> Rvs { get; }

    [ObservableProperty] private int _currentRv = 1;
    [ObservableProperty] private string _expandedPanel = "";

    public string EditButtonText => Plan.IsEditing ? "保存程序" : "编辑程序";
    public bool IsPlanExpanded => ExpandedPanel == "plan";
    /// <summary>程序编辑器占几列（展开时铺满，右边的工具列隐藏）。</summary>
    public int PlanSpan => IsPlanExpanded ? 2 : 1;
    partial void OnExpandedPanelChanged(string value) { OnPropertyChanged(nameof(IsPlanExpanded)); OnPropertyChanged(nameof(PlanSpan)); }

    private int StirRpm => _shell.Fleet.LiveConnection?.Telemetry.Latest is { Stir: { On: true } s } ? s.RpmSp : 0;

    public void SelectRv(int rv)
    {
        CurrentRv = rv;
        foreach (var r in Rvs) r.IsActive = r.Rv == rv;
        var name = rv == 0 ? "新建程序" : "RV" + rv;
        Plan.RootName = rv == 0 ? "新建程序" : "系统视图";
        Plan.SetContext(rv, name, _drafts.Get(rv), StirRpm, CurrentStep(rv));
    }

    private int CurrentStep(int rv)
    {
        var ch = _shell.Fleet.LiveConnection?.Telemetry.Latest?.Channels.FirstOrDefault(c => c.Id == rv);
        return ch?.Program is { Running: true } p ? p.StepIndex : -1;
    }

    /// <summary>实时数据到了：当前步、全局转速、硬件自检文字（同旧 MainViewModel.OnTelemetry）。</summary>
    public override void Refresh(IFleetView fleet)
    {
        var f = _shell.Fleet.LiveConnection?.Telemetry.Latest;
        if (f == null || CurrentRv == 0) return;
        var ch = f.Channels.FirstOrDefault(c => c.Id == CurrentRv);
        if (ch == null) return;
        bool running = ch.Program?.Running ?? false;
        Plan.UpdateLive(StirRpm, running ? ch.Program!.StepIndex : -1, running,
            ch.Installed && ch.State != ChannelStates.Alarm && f.Comm.Temp && f.Comm.Pressure ? "= 通过" : "= 未通过",
            (f.Estop.Active ? "急停已触发" : "急停未触发") + " · " + (ch.PressurePsi is double p && p < 200 ? "压力在安全范围" : ch.PressurePsi is null ? "压力无读数" : "压力偏高"));
    }

    [RelayCommand]
    public void ToggleEdit()
    {
        if (!Plan.IsEditing) { Plan.BeginEdit(); Toast("点击设定值即可编辑（搅拌转速为全局共用）"); return; }
        var err = Plan.Save();
        if (err != null) { Toast(err); return; }
        _drafts.Save();
        Toast("程序已保存到本机");
    }

    /// <summary>下发到实时设备的当前 RV（需要控制权；执行前查守卫）。</summary>
    [RelayCommand]
    public async Task UploadProgramAsync()
    {
        var d = _shell.Fleet.LiveDevice;
        if (d?.Source is not LiveDeviceSource live || live.Connection == null) { Toast("未连接设备"); return; }
        int rv = CurrentRv == 0 ? (_shell.SelectedRv ?? 1) : CurrentRv;
        if (Plan.IsEditing) { var err = Plan.Save(); if (err != null) { Toast(err); return; } }
        var doc = Plan.Program ?? _drafts.Get(rv);
        var why = d.Actions.Why(DeviceAction.ProgLoad, ActionPayload.Ch(rv));
        if (why.Length > 0) { _shell.Toast(why, ToastKind.Warn); return; }
        try
        {
            var o = await live.UploadProgramAsync(rv, doc, CancellationToken.None).ConfigureAwait(true);
            if (CurrentRv == 0) _drafts.Set(rv, doc);
            _shell.Toast(o.Message, ToastKind.Ok);
            if (DeviceActions.WarningText(o.Warnings) is { } w) _shell.Toast(d.Alias + " · " + w, ToastKind.Warn);
        }
        catch (Exception ex) { _shell.Toast(d.Alias + " · " + DeviceActions.Describe(ex), ToastKind.Err); }
        _shell.Fleet.RequestRefresh();
    }

    /// <summary>运行当前 RV 上的程序（需要控制权；走 DeviceActions：守卫、提示、警告）。</summary>
    [RelayCommand]
    public async Task RunProgramAsync()
    {
        var d = _shell.Fleet.LiveDevice;
        if (d == null) { Toast("未连接设备"); return; }
        int rv = CurrentRv == 0 ? (_shell.SelectedRv ?? 1) : CurrentRv;
        await d.Actions.RunAsync(DeviceAction.ProgRun, ActionPayload.Ch(rv)).ConfigureAwait(true);
    }

    /// <summary>中止当前 RV 上的程序（停止类：只要连接）。</summary>
    [RelayCommand]
    public async Task AbortProgramAsync()
    {
        var d = _shell.Fleet.LiveDevice;
        if (d == null) { Toast("未连接设备"); return; }
        int rv = CurrentRv == 0 ? (_shell.SelectedRv ?? 1) : CurrentRv;
        await d.Actions.RunAsync(DeviceAction.ProgAbort, ActionPayload.Ch(rv)).ConfigureAwait(true);
    }

    /// <summary>导出当前 RV 的程序（JSON）和本机收到的遥测（CSV），同旧界面；目录 PR_CONTROL_DATA，否则 文稿/ParallelReactor/export。</summary>
    [RelayCommand]
    public async Task ExportAsync()
    {
        var c = _shell.Fleet.LiveConnection;
        int rv = CurrentRv;
        try
        {
            var dir = Environment.GetEnvironmentVariable("PR_CONTROL_DATA") is { Length: > 0 } env ? env
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ParallelReactor", "export");
            System.IO.Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var name = rv == 0 ? "新建程序" : "RV" + rv;
            var prog = _drafts.Get(rv);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir, $"{name}-program-{stamp}.json"), prog.ToJson()).ConfigureAwait(true);
            if (c != null && rv > 0)
            {
                var frames = c.Telemetry.Snapshot();
                var sb = new System.Text.StringBuilder("ts,seq,rv,state,tempC,tempSpC,heatOutputPct,pressurePsi,valveOpen,stirOn,rpmSp,programState,stepIndex\n");
                foreach (var f in frames)
                {
                    var ch = f.Channels.FirstOrDefault(x => x.Id == rv);
                    if (ch == null) continue;
                    sb.Append(f.Ts.ToString("o", CultureInfo.InvariantCulture)).Append(',').Append(f.Seq).Append(',').Append(ch.Id).Append(',').Append(ch.State).Append(',')
                      .Append(ch.TempC?.ToString(CultureInfo.InvariantCulture)).Append(',').Append(ch.TempSpC.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(ch.HeatOutputPct?.ToString(CultureInfo.InvariantCulture)).Append(',').Append(ch.PressurePsi?.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(ch.ValveOpen ? 1 : 0).Append(',').Append(f.Stir.On ? 1 : 0).Append(',').Append(f.Stir.RpmSp).Append(',')
                      .Append(ch.Program?.State).Append(',').Append(ch.Program?.StepIndex).Append('\n');
                }
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir, $"{name}-telemetry-{stamp}.csv"), sb.ToString()).ConfigureAwait(true);
            }
            Toast($"已导出 {name} 程序与数据 → {dir}");
        }
        catch (Exception ex) { Toast(DeviceActions.Describe(ex)); }
    }

    [RelayCommand]
    public void ZoomPlan(string dir) => Plan.Zoom = dir == "reset" ? 1 : Math.Clamp(Plan.Zoom + (dir == "in" ? .1 : -.1), .8, 1.4);

    [RelayCommand]
    public void ToggleExpand(string panel) => ExpandedPanel = ExpandedPanel == panel ? "" : panel;

    [RelayCommand]
    public void NotImplemented(string what) => Toast(what + "（示意）");

    /// <summary>截图脚本 / 外部写入草稿。</summary>
    public void SetDraft(int rv, ProgramDoc doc)
    {
        _drafts.Set(rv, doc);
        if (CurrentRv == rv) SelectRv(rv);
    }

    private void Toast(string text) => _shell.Toast(text, ToastKind.Info);
}

/// <summary>程序页那一排 RV 选择里的一项（新建程序、RV1–RV8）。</summary>
public sealed partial class LegacyRvVm : ObservableObject
{
    public LegacyRvVm(int rv, string title, Action<int> select)
    {
        Rv = rv;
        Title = title;
        SelectCommand = new RelayCommand(() => select(rv));
    }

    public int Rv { get; }
    public string Title { get; }
    public ICommand SelectCommand { get; }
    [ObservableProperty] private bool _isActive;
}

/// <summary>
/// 性能测试页（旧样式，PORT-PLAN §5 M2「PerfPageVm」；M7 换新组件并加设备选择）：承载 PerfViewModel（清单 / 进度 / 报告 / 实时曲线 + 操作员提示桥）。
/// 实现 <see cref="IPerfHost"/>：连接 = 实时设备的连接（M2 只有一台），客户端名、是否在看这一页、toast 都来自新外壳。
/// 遥测：外框每次合并刷新时把最新一帧交给 PerfViewModel（旧界面是每帧调用）。
/// </summary>
public sealed partial class PerfPageVm : PageVm, IPerfHost, ILegacyPage
{
    private readonly ShellVm _shell;
    private TelemetrySnapshot? _lastFrame;
    private DeviceInfoDto? _lastInfo;

    public PerfPageVm(ShellVm shell) : base(Route.Placeholder("perf"))
    {
        _shell = shell;
        Perf = new PerfViewModel(this, LoadPlanSafe());
    }

    public PerfViewModel Perf { get; }

    /// <summary>测试在跑（页签关掉时页面留着，引擎还在用它）。</summary>
    public bool IsBusy => Perf.IsRunning;

    [ObservableProperty] private string _expandedPanel = "";
    public bool IsProgressExpanded => ExpandedPanel == "perfprogress";
    public bool IsGraphExpanded => ExpandedPanel == "perfgraph";
    public bool ShowChecklist => ExpandedPanel.Length == 0;
    public bool ShowProgress => ExpandedPanel.Length == 0 || IsProgressExpanded;
    public bool ShowGraph => ExpandedPanel.Length == 0 || IsGraphExpanded;

    partial void OnExpandedPanelChanged(string value)
    {
        OnPropertyChanged(nameof(IsProgressExpanded));
        OnPropertyChanged(nameof(IsGraphExpanded));
        OnPropertyChanged(nameof(ShowChecklist));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ShowGraph));
    }

    // ———— IPerfHost ————
    public IReactorConnection? Connection => _shell.Fleet.LiveConnection;
    public string ClientName => _shell.Fleet.ClientName;
    public bool IsVisible => ReferenceEquals(_shell.Page, this);
    public void Toast(string text) => _shell.Toast(text, ToastKind.Info);

    /// <summary>方案加载失败（外部 JSON 坏了）时退回内置方案并提示（同旧 MainViewModel.LoadPlanSafe）。</summary>
    private TestPlan LoadPlanSafe()
    {
        try { return PerfViewModel.LoadDefaultPlan(); }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Toast("性能测试方案加载失败，已用内置方案：" + ex.Message));
            return PlanLoader.LoadEmbedded(PlanLoader.BaselinePlan);
        }
    }

    public override void Refresh(IFleetView fleet)
    {
        var c = Connection;
        if (c == null) return;
        if (c.Device is { } info && !ReferenceEquals(info, _lastInfo))
        {
            _lastInfo = info;
            Perf.ApplyInstalled(info.InstalledChannels.ToList());
        }
        var f = c.Telemetry.Latest;
        if (f != null && !ReferenceEquals(f, _lastFrame))
        {
            _lastFrame = f;
            Perf.OnTelemetry(c, f);
        }
    }

    [RelayCommand]
    public void ToggleExpand(string panel) => ExpandedPanel = ExpandedPanel == panel ? "" : panel;

    [RelayCommand]
    public void NotImplemented(string what) => Toast(what + "（示意）");

    public override void Dispose() { }
}
