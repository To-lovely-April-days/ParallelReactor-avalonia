using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.ViewModels;

public enum PlanMode { Plan, Curves, Overview }

/// <summary>
/// 主窗口 VM：标签 / 侧栏 / 程序编辑器 / 属性查看器 / 曲线 / 反应釜列表 / 时间轴 / 检查器 / 连接与控制权 / Toast。
/// 性能测试视图 = <see cref="Perf"/>（PerfViewModel：清单 / 进度 / 报告 + IOperatorPrompt），与 IsPerfView 联动。
/// 所有 API 调用在线程池上执行，回调由 Client 经 UiContext 投递到 UI 线程。
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ConnectionService _conns;
    private readonly ProgramDraftStore _drafts = new();
    private readonly DateTime?[] _programEndedAt = new DateTime?[8];
    private readonly bool[] _wasRunning = new bool[8];
    private DateTime? _firstFrameAt;
    private CancellationTokenSource? _toastCts;
    private readonly DispatcherTimer _tick;
    private bool _autoReacquired;
    private int _requestRemaining;

    public ControlConfig Config { get; }
    public IReactorConnection? Connection => _conns.Current;

    public ObservableCollection<TabVm> Tabs { get; } = new();
    public ChannelVm[] Channels { get; } = Enumerable.Range(1, 8).Select(i => new ChannelVm(i)).ToArray();
    public ObservableCollection<PropertyCardVm> Properties { get; } = new();
    public ObservableCollection<TimelineRowVm> TimelineRows { get; } = new();
    public ObservableCollection<OverviewTileVm> OverviewTiles { get; } = new();
    public PlanEditorVm Plan { get; } = new();
    public ChartVm Chart { get; }
    public ChartVm AllChart { get; }
    public ConnectDialogVm ConnectDialog { get; } = new();
    public ControlDialogVm ControlDialog { get; } = new();
    public PerfViewModel Perf { get; }

    [ObservableProperty] private TabVm? _selectedTab;
    [ObservableProperty] private int _currentRv = 1;
    [ObservableProperty] private ChannelVm _currentChannel;
    [ObservableProperty] private bool _isPerfView;
    [ObservableProperty] private PlanMode _planMode = PlanMode.Plan;

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionText = "未连接";
    [ObservableProperty] private string _deviceText = "";
    [ObservableProperty] private bool _canControl;
    [ObservableProperty] private string _controlBadge = "● 未连接";
    [ObservableProperty] private string _controlBadgeClass = "idle"; // idle | remote | lost | requesting
    [ObservableProperty] private bool _estopActive;
    [ObservableProperty] private string _estopText = "";
    [ObservableProperty] private bool _estopConfirmOpen;
    [ObservableProperty] private string? _openMenu;
    [ObservableProperty] private string _expandedPanel = "";
    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private bool _toastVisible;

    [ObservableProperty] private string _heatTempInput = "80";
    [ObservableProperty] private string _spInput = "80";
    [ObservableProperty] private string _rpmInput = "300";
    [ObservableProperty] private string _stirText = "0 rpm";

    [ObservableProperty] private string _t1 = "";
    [ObservableProperty] private string _t2 = "";
    [ObservableProperty] private string _t3 = "";
    [ObservableProperty] private string _t4 = "";

    public bool IsMainView => !IsPerfView;
    public bool IsPlanMode => PlanMode == PlanMode.Plan && !IsPerfView;
    public bool IsCurvesMode => PlanMode == PlanMode.Curves && !IsPerfView;
    public bool IsOverviewMode => PlanMode == PlanMode.Overview && !IsPerfView;
    public bool IsExpanded => ExpandedPanel.Length > 0;
    public bool IsPlanExpanded => ExpandedPanel == "plan";
    public bool IsGraphExpanded => ExpandedPanel == "graph";
    public bool IsPropertyExpanded => ExpandedPanel == "property";
    public bool IsCurvesExpanded => ExpandedPanel == "curves";
    public bool IsPerfProgressExpanded => ExpandedPanel == "perfprogress";
    public bool IsPerfGraphExpanded => ExpandedPanel == "perfgraph";
    public string EditButtonText => Plan.IsEditing ? "保存程序" : "编辑程序";
    public string CurrentReactorName => "RV" + CurrentRv;
    public string ChartSubtitle => "温度 · 压力 · 全局转速";
    public bool MenuFileOpen => OpenMenu == "file";
    public bool MenuViewOpen => OpenMenu == "view";
    public bool MenuHelpOpen => OpenMenu == "help";
    public bool MenuEditOpen => OpenMenu == "edit";
    public bool MenuWindowOpen => OpenMenu == "window";
    public bool CanStopClass => IsConnected;

    public MainViewModel(ConnectionService conns, ControlConfig config)
    {
        _conns = conns; Config = config;
        _currentChannel = Channels[0];
        for (int i = 1; i <= 8; i++) Tabs.Add(new TabVm("RV" + i, TabKind.Reactor, i));
        Tabs.Add(new TabVm("全部曲线", TabKind.Curves));
        Tabs.Add(new TabVm("总览", TabKind.Overview));
        Tabs.Add(new TabVm("新建程序", TabKind.NewProgram));
        Tabs.Add(new TabVm("性能测试", TabKind.Perf));
        foreach (var ch in Channels) { TimelineRows.Add(new TimelineRowVm(ch)); OverviewTiles.Add(new OverviewTileVm(ch)); }

        Chart = new ChartVm("曲线", ChartSubtitle, new[]
        {
            new ChartSeries("温度", ProcessChart.TempColor),
            new ChartSeries("压力", ProcessChart.OilColor, right: true),
            new ChartSeries("转速 ÷ 2", ProcessChart.SpeedColor, right: true),
        });
        var palette = new[] { "#91999a", "#9ab4a1", "#7f8d92", "#a99bc9", "#b5a687", "#8aa3b8", "#c39a9f", "#9fb08f" };
        AllChart = new ChartVm("全部曲线", "RV1–RV8 温度 · 全局转速", Enumerable.Range(1, 8).Select(i => new ChartSeries("RV" + i, Color.Parse(palette[i - 1]))).Append(new ChartSeries("转速 ÷ 2", ProcessChart.SpeedColor, right: true)).ToArray())
        { RightLabel = "转速 (rpm ÷ 2)" };

        Perf = new PerfViewModel(this, LoadPlanSafe());

        ConnectDialog.Host = config.Host; ConnectDialog.Port = config.Port.ToString(CultureInfo.InvariantCulture);
        ConnectDialog.ClientName = config.ClientName; ConnectDialog.Token = config.Token ?? ""; ConnectDialog.RememberToken = config.RememberToken;

        Plan.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlanEditorVm.IsEditing)) OnPropertyChanged(nameof(EditButtonText)); };
        BuildProperties();
        SelectTab(Tabs[0]);
        UpdateTimelineLabels(DateTime.UtcNow);
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnSecond());
        _tick.Start();
        _conns.CurrentChanged += OnConnectionChanged;
    }

    // ───────────────────────── 标签 / 视图 ─────────────────────────

    [RelayCommand]
    public void SelectTab(TabVm tab)
    {
        foreach (var t in Tabs) t.IsActive = t == tab;
        SelectedTab = tab;
        OpenMenu = null;
        switch (tab.Kind)
        {
            case TabKind.Perf:
                IsPerfView = true;
                break;
            case TabKind.Reactor:
                IsPerfView = false; PlanMode = PlanMode.Plan; CurrentRv = tab.Rv;
                if (tab.Rv > 4 && !Channels[tab.Rv - 1].Installed) Toast($"RV{tab.Rv} 未安装：仅显示空程序");
                break;
            case TabKind.Curves: IsPerfView = false; PlanMode = PlanMode.Curves; break;
            case TabKind.Overview: IsPerfView = false; PlanMode = PlanMode.Overview; break;
            case TabKind.NewProgram:
                IsPerfView = false; PlanMode = PlanMode.Plan;
                Plan.SetContext(0, CurrentReactorName, _drafts.Get(0), StirRpm, -1);
                Plan.RootName = "新建程序";
                break;
        }
        if (tab.Kind != TabKind.NewProgram) RebuildPlanForCurrent();
        RefreshNotifications();
    }

    [RelayCommand]
    public void ShowView(string name)
    {
        OpenMenu = null;
        if (name == "perf") { SelectTab(Tabs.First(t => t.Kind == TabKind.Perf)); return; }
        SelectTab(Tabs.First(t => t.Kind == TabKind.Reactor && t.Rv == CurrentRv));
    }

    public void SelectReactor(int rv) => SelectTab(Tabs.First(t => t.Kind == TabKind.Reactor && t.Rv == rv));
    public void SelectKind(TabKind kind) => SelectTab(Tabs.First(t => t.Kind == kind));

    partial void OnIsPerfViewChanged(bool value) => RefreshNotifications();
    partial void OnPlanModeChanged(PlanMode value) => RefreshNotifications();
    partial void OnExpandedPanelChanged(string value)
    { OnPropertyChanged(nameof(IsExpanded)); OnPropertyChanged(nameof(IsPlanExpanded)); OnPropertyChanged(nameof(IsGraphExpanded)); OnPropertyChanged(nameof(IsPropertyExpanded)); OnPropertyChanged(nameof(IsCurvesExpanded)); OnPropertyChanged(nameof(IsPerfProgressExpanded)); OnPropertyChanged(nameof(IsPerfGraphExpanded)); }

    /// <summary>方案加载失败（外部 JSON 坏了）时退回内置方案并提示。</summary>
    private ParallelReactor.PerfTest.Model.TestPlan LoadPlanSafe()
    {
        try { return PerfViewModel.LoadDefaultPlan(); }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => Toast("性能测试方案加载失败，已用内置方案：" + ex.Message));
            return ParallelReactor.PerfTest.Model.PlanLoader.LoadEmbedded(ParallelReactor.PerfTest.Model.PlanLoader.BaselinePlan);
        }
    }
    partial void OnOpenMenuChanged(string? value)
    { OnPropertyChanged(nameof(MenuFileOpen)); OnPropertyChanged(nameof(MenuViewOpen)); OnPropertyChanged(nameof(MenuHelpOpen)); OnPropertyChanged(nameof(MenuEditOpen)); OnPropertyChanged(nameof(MenuWindowOpen)); }
    partial void OnCurrentRvChanged(int value) { CurrentChannel = Channels[value - 1]; OnPropertyChanged(nameof(CurrentReactorName)); }
    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(CanStopClass));

    private void RefreshNotifications()
    {
        OnPropertyChanged(nameof(IsMainView)); OnPropertyChanged(nameof(IsPlanMode)); OnPropertyChanged(nameof(IsCurvesMode)); OnPropertyChanged(nameof(IsOverviewMode));
    }

    [RelayCommand] public void ToggleMenu(string name) => OpenMenu = OpenMenu == name ? null : name;
    [RelayCommand] public void CloseMenus() => OpenMenu = null;

    [RelayCommand]
    public void ToggleExpand(string panel) => ExpandedPanel = ExpandedPanel == panel ? "" : panel;
    [RelayCommand] public void CloseExpanded() => ExpandedPanel = "";

    [RelayCommand]
    public void Escape()
    {
        OpenMenu = null;
        if (ExpandedPanel.Length > 0) { ExpandedPanel = ""; return; }
        if (Plan.IsEditing) { Plan.CancelEdit(); Toast("已取消编辑"); return; }
        if (EstopConfirmOpen) { EstopConfirmOpen = false; return; }
        if (ConnectDialog.IsOpen) ConnectDialog.IsOpen = false;
        if (ControlDialog.IsOpen) ControlDialog.IsOpen = false;
    }

    [RelayCommand] public void ZoomPlan(string dir) => Plan.Zoom = dir == "reset" ? 1 : Math.Clamp(Plan.Zoom + (dir == "in" ? .1 : -.1), .8, 1.4);

    [RelayCommand]
    public void HideProperty(PropertyCardVm card) => card.IsHidden = true;
    [RelayCommand]
    public void RestoreProperties() { foreach (var p in Properties) p.IsHidden = false; Toast("已恢复全部属性"); }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var c = Connection;
        if (c == null) { Toast("未连接设备"); return; }
        try
        {
            var snap = await c.Rest.SnapshotAsync(CancellationToken.None);
            OnTelemetry(snap);
            Toast("已刷新设备状态");
        }
        catch (Exception ex) { ToastError(ex); }
    }

    // ───────────────────────── 程序编辑 / 下发 ─────────────────────────

    private int StirRpm { get; set; }
    private bool _stirOn;

    private void RebuildPlanForCurrent()
    {
        if (SelectedTab?.Kind == TabKind.NewProgram) return;
        var ch = CurrentChannel;
        Plan.RootName = "系统视图";
        Plan.SetContext(CurrentRv, CurrentReactorName, _drafts.Get(CurrentRv), StirRpm, ch.ProgramRunning ? ch.StepIndex : -1);
        Chart.Subtitle = ChartSubtitle;
        RefreshProperties();
        RebuildChart();
    }

    /// <summary>截图脚本 / 外部写入草稿。</summary>
    public void SetDraft(int rv, ProgramDoc doc) { _drafts.Set(rv, doc); if (CurrentRv == rv && SelectedTab?.Kind == TabKind.Reactor) RebuildPlanForCurrent(); }

    [RelayCommand]
    public void ToggleEdit()
    {
        if (!Plan.IsEditing) { Plan.BeginEdit(); Toast("点击设定值即可编辑（搅拌转速为全局共用）"); return; }
        var err = Plan.Save();
        if (err != null) { Toast(err); return; }
        _drafts.Save();
        Toast("程序已保存到本机");
    }

    [RelayCommand]
    public async Task UploadProgramAsync()
    {
        var c = Connection;
        if (c == null) { Toast("未连接设备"); return; }
        if (Plan.IsEditing) { var err = Plan.Save(); if (err != null) { Toast(err); return; } }
        var doc = Plan.Program ?? _drafts.Get(CurrentRv);
        try
        {
            await c.Control.ReplaceProgramAsync(CurrentRv, doc, CancellationToken.None);
            if (Plan.Rv == 0) _drafts.Set(CurrentRv, doc);
            Toast($"程序「{doc.Name}」已下发至 RV{CurrentRv}");
        }
        catch (Exception ex) { ToastError(ex); }
    }

    [RelayCommand]
    public async Task RunProgramAsync() => await RunProgramOnAsync(CurrentChannel);

    public async Task RunProgramOnAsync(ChannelVm ch)
    {
        var c = Connection;
        if (c == null) { Toast("未连接设备"); return; }
        try
        {
            var r = await c.Control.RunProgramAsync(ch.Id, null, IdempotencyKeys.New($"pc-run-rv{ch.Id}"), CancellationToken.None);
            Toast($"RV{ch.Id}：程序已启动（{r.Result?.Steps ?? 0} 步）");
        }
        catch (Exception ex) { ToastError(ex); }
    }

    [RelayCommand]
    public async Task AbortProgramAsync() => await AbortProgramOnAsync(CurrentChannel);

    public async Task AbortProgramOnAsync(ChannelVm ch)
    {
        var c = Connection;
        if (c == null) { Toast("未连接设备"); return; }
        try { await c.Rest.AbortProgramAsync(ch.Id, CancellationToken.None); Toast($"RV{ch.Id}：程序已中止"); }
        catch (Exception ex) { ToastError(ex); }
    }

    [RelayCommand]
    public async Task ExportAsync()
    {
        var c = Connection;
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetEnvironmentVariable("PR_CONTROL_DATA") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ParallelReactor", "export"));
            System.IO.Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var prog = _drafts.Get(CurrentRv);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir, $"RV{CurrentRv}-program-{stamp}.json"), prog.ToJson());
            if (c != null)
            {
                var frames = c.Telemetry.Snapshot();
                var sb = new System.Text.StringBuilder("ts,seq,rv,state,tempC,tempSpC,heatOutputPct,pressurePsi,valveOpen,stirOn,rpmSp,programState,stepIndex\n");
                foreach (var f in frames)
                {
                    var ch = f.Channels[CurrentRv - 1];
                    sb.Append(f.Ts.ToString("o")).Append(',').Append(f.Seq).Append(',').Append(ch.Id).Append(',').Append(ch.State).Append(',')
                      .Append(ch.TempC?.ToString(CultureInfo.InvariantCulture)).Append(',').Append(ch.TempSpC.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(ch.HeatOutputPct?.ToString(CultureInfo.InvariantCulture)).Append(',').Append(ch.PressurePsi?.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(ch.ValveOpen ? 1 : 0).Append(',').Append(f.Stir.On ? 1 : 0).Append(',').Append(f.Stir.RpmSp).Append(',')
                      .Append(ch.Program?.State).Append(',').Append(ch.Program?.StepIndex).Append('\n');
                }
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(dir, $"RV{CurrentRv}-telemetry-{stamp}.csv"), sb.ToString());
            }
            Toast($"已导出 RV{CurrentRv} 程序与数据 → {dir}");
        }
        catch (Exception ex) { ToastError(ex); }
    }

    // ───────────────────────── 通道操作（检查器 / 操作行） ─────────────────────────

    [RelayCommand] public Task EnableRvAsync() => Controlled(c => c.Control.EnableChannelAsync(CurrentRv, CancellationToken.None), $"RV{CurrentRv}：已启用");
    [RelayCommand] public Task DisableRvAsync() => StopClass(c => c.Rest.DisableChannelAsync(CurrentRv, CancellationToken.None), $"RV{CurrentRv}：已停用");
    [RelayCommand]
    public Task StartHeatAsync()
    {
        if (!double.TryParse(HeatTempInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) { Toast("加热温度不是有效数字"); return Task.CompletedTask; }
        return Controlled(c => c.Control.StartHeatAsync(CurrentRv, t, CancellationToken.None), $"RV{CurrentRv}：开始加热至 {t:0.#} ℃");
    }
    [RelayCommand] public Task StopHeatAsync() => StopClass(c => c.Rest.StopHeatAsync(CurrentRv, CancellationToken.None), $"RV{CurrentRv}：已停止加热");
    [RelayCommand]
    public Task SetSpAsync()
    {
        if (!double.TryParse(SpInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) { Toast("设定值不是有效数字"); return Task.CompletedTask; }
        return Controlled(c => c.Control.SetSetpointAsync(CurrentRv, t, CancellationToken.None), $"RV{CurrentRv}：SP → {t:0.#} ℃");
    }
    [RelayCommand]
    public Task StirOnAsync()
    {
        if (!int.TryParse(RpmInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rpm)) { Toast("转速不是有效整数"); return Task.CompletedTask; }
        return Controlled(c => c.Control.SetStirAsync(true, rpm, CancellationToken.None), $"搅拌：{rpm} rpm（全局）");
    }
    [RelayCommand] public Task StirOffAsync() => StopClass(c => c.Rest.StopStirAsync(CancellationToken.None), "搅拌已停止");
    [RelayCommand] public Task StopAllAsync() => StopClass(c => c.Rest.StopAllAsync(true, CancellationToken.None), "已全部停止（加热 · 程序 · 搅拌）");

    [RelayCommand]
    public async Task RowPlayAsync(ChannelVm ch)
    {
        if (!ch.Installed) { Toast($"RV{ch.Id} 未安装"); return; }
        if (ch.ProgramRunning) { await AbortProgramOnAsync(ch); return; }
        var c = Connection;
        if (c == null) { Toast("未连接设备"); return; }
        try
        {
            if (ch.State is ChannelStates.Ready or ChannelStates.Done or ChannelStates.Idle)
            {
                if (ch.State == ChannelStates.Idle) await c.Control.EnableChannelAsync(ch.Id, CancellationToken.None);
                await c.Control.RunProgramAsync(ch.Id, null, IdempotencyKeys.New($"pc-run-rv{ch.Id}"), CancellationToken.None);
                Toast($"RV{ch.Id}：已启动");
            }
            else { await c.Rest.StopChannelAsync(ch.Id, CancellationToken.None); Toast($"RV{ch.Id}：已停止"); }
        }
        catch (Exception ex) { ToastError(ex); }
    }
    [RelayCommand] public Task RowStopAsync(ChannelVm ch) => StopClass(c => c.Rest.StopChannelAsync(ch.Id, CancellationToken.None), $"RV{ch.Id}：已停止");
    [RelayCommand] public void RowSkip(ChannelVm ch) => Toast("接口未提供「下一步」命令");
    [RelayCommand] public void RowBook(ChannelVm ch) => SelectReactor(ch.Id);
    [RelayCommand]
    public async Task RowUploadAsync(ChannelVm ch)
    {
        var c = Connection;
        if (c == null) { Toast("未连接设备"); return; }
        var doc = _drafts.Get(ch.Id);
        try { await c.Control.ReplaceProgramAsync(ch.Id, doc, CancellationToken.None); Toast($"程序「{doc.Name}」已下发至 RV{ch.Id}"); }
        catch (Exception ex) { ToastError(ex); }
    }

    private async Task Controlled(Func<IReactorConnection, Task> op, string ok)
    {
        var c = Connection;
        if (c == null) { Toast("未连接设备"); return; }
        if (c.Lease.State != LeaseState.Holding) { Toast("尚未持有控制权 · 请先申请远程控制"); return; }
        try { await op(c); Toast(ok); } catch (Exception ex) { ToastError(ex); }
    }

    private async Task StopClass(Func<IReactorConnection, Task> op, string ok)
    {
        var c = Connection;
        if (c == null) { Toast("未连接设备"); return; }
        try { await op(c); Toast(ok); } catch (Exception ex) { ToastError(ex); }
    }

    // ───────────────────────── 连接 / 控制权 / 急停 ─────────────────────────

    [RelayCommand] public void OpenConnectDialog() { OpenMenu = null; ConnectDialog.IsOpen = true; ConnectDialog.HasError = false; ConnectDialog.Status = IsConnected ? ConnectionText : "未连接"; }
    [RelayCommand] public void CloseConnectDialog() => ConnectDialog.IsOpen = false;
    [RelayCommand] public void OpenControlDialog() { OpenMenu = null; if (!IsConnected) { OpenConnectDialog(); return; } ControlDialog.IsOpen = true; UpdateControlBadge(); }
    [RelayCommand] public void CloseControlDialog() => ControlDialog.IsOpen = false;

    [RelayCommand]
    public async Task ConnectAsync()
    {
        var d = ConnectDialog;
        if (!int.TryParse(d.Port, out var port) || port <= 0) { d.Status = "端口无效"; d.HasError = true; return; }
        if (string.IsNullOrWhiteSpace(d.Token)) { d.Status = "请输入访问令牌"; d.HasError = true; return; }
        d.Busy = true; d.HasError = false; d.Status = "连接中…";
        try
        {
            var url = $"http://{d.Host.Trim()}:{port}";
            var conn = await _conns.ConnectAsync(url, d.Token.Trim(), d.ClientName.Trim(), CancellationToken.None);
            Config.Host = d.Host.Trim(); Config.Port = port; Config.ClientName = d.ClientName.Trim(); Config.RememberToken = d.RememberToken; Config.Token = d.Token.Trim();
            Config.Save();
            d.Status = $"已连接 · {conn.Device?.Model} {conn.Device?.Serial}";
            d.IsOpen = false;
            Toast($"已连接 {url}");
        }
        catch (Exception ex) { d.Status = "连接失败：" + Describe(ex); d.HasError = true; }
        finally { d.Busy = false; }
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        OpenMenu = null;
        if (Perf.IsRunning) { Toast("断开前先中止性能测试：停止加热 / 搅拌…"); await Perf.AbortAndWaitAsync(TimeSpan.FromSeconds(8)); }
        await _conns.DisconnectAsync();
        Toast("已断开连接");
    }

    [RelayCommand]
    public async Task RequestControlAsync()
    {
        var c = Connection;
        if (c == null) { Toast("未连接设备"); return; }
        if (c.Lease.State == LeaseState.Holding) { Toast("已持有控制权"); return; }
        _requestRemaining = c.ControlState?.RequestTimeoutSec > 0 ? c.ControlState.RequestTimeoutSec : 60;
        ControlDialog.Requesting = true; ControlDialog.RemainingSec = _requestRemaining;
        UpdateControlBadge();
        try
        {
            var ok = await c.Lease.AcquireAsync(CancellationToken.None);
            Toast(ok ? "已取得远程控制权" : "申请被拒绝或超时");
        }
        catch (Exception ex) { ToastError(ex); }
        finally { ControlDialog.Requesting = false; UpdateControlBadge(); }
    }

    [RelayCommand]
    public async Task ReleaseControlAsync()
    {
        var c = Connection;
        if (c == null) return;
        try { await c.Lease.ReleaseAsync(CancellationToken.None); Toast("已释放控制权"); }
        catch (Exception ex) { ToastError(ex); }
        UpdateControlBadge();
    }

    [RelayCommand] public void AskEstop() { OpenMenu = null; if (!IsConnected) { Toast("未连接设备"); return; } EstopConfirmOpen = true; }
    [RelayCommand] public void CancelEstop() => EstopConfirmOpen = false;
    [RelayCommand]
    public async Task ConfirmEstopAsync()
    {
        EstopConfirmOpen = false;
        var c = Connection;
        if (c == null) return;
        try { await c.Rest.EstopAsync("PC 急停", CancellationToken.None); Toast("急停已发出"); }
        catch (Exception ex) { ToastError(ex); }
    }

    [RelayCommand] public void Exit() => (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    [RelayCommand] public void About() { OpenMenu = null; Toast("ParallelReactor 控制软件 · pc-1.0 · 接口 v0.5"); }
    [RelayCommand] public void NotImplemented(string what) { OpenMenu = null; Toast(what + "（示意）"); }

    private void OnConnectionChanged(IReactorConnection? c)
    {
        IsConnected = c != null;
        _firstFrameAt = null;
        if (c == null)
        {
            ConnectionText = "未连接"; DeviceText = ""; CanControl = false; ControlBadge = "● 未连接"; ControlBadgeClass = "idle";
            foreach (var ch in Channels) ch.HasProgram = false;
            return;
        }
        c.TelemetryReceived += OnTelemetry;
        c.LeaseTransitioned += OnLease;
        c.ControlChanged += OnControlMsg;
        c.ConnectionStateChanged += s => { ConnectionText = s switch { ConnectionState.Connected => "已连接", ConnectionState.Resyncing => "重同步中", ConnectionState.Reconnecting => "重连中…", ConnectionState.Connecting => "连接中…", _ => "WS 断开" } + $" · {Config.Host}:{Config.Port}"; };
        c.AlarmChanged += a => { if (a.Alarm.Level is "err" or "error" || a.Alarm.State == "active" && a.Alarm.Level == "warn") Toast($"报警 {a.Alarm.Rv}：{a.Alarm.Title}"); };
        c.Error += ex => { System.Diagnostics.Debug.WriteLine(ex); if (c.Lease.HeartbeatDegraded && c.Lease.State == LeaseState.Holding) { ControlBadge = $"● 远程控制中 · 心跳异常 · {Config.ClientName}"; ControlBadgeClass = "lost"; } };
        c.ServerRestarted += () => { Toast("设备 HMI 已重启 · 曲线缓冲已清空"); foreach (var ch in Channels) ch.HasProgram = false; };
        var dev = c.Device;
        DeviceText = dev == null ? "" : $"{dev.Model} · {dev.Serial} · HMI {dev.HmiVersion}" + (dev.Simulated ? " · 模拟" : "");
        ConnectionText = $"已连接 · {Config.Host}:{Config.Port}";
        if (dev != null) { for (int i = 0; i < 8; i++) Channels[i].Installed = dev.InstalledChannels.Contains(i + 1); Perf.ApplyInstalled(dev.InstalledChannels.ToList()); }
        var latest = c.Telemetry.Latest;
        if (latest != null) OnTelemetry(latest);
        UpdateControlBadge();
    }

    private void OnLease(LeaseTransition t)
    {
        UpdateControlBadge();
        if (t.To == LeaseState.Lost) Toast("控制权连接丢失 · 宽限期内自动恢复");
        else if (t.From == LeaseState.Lost && t.To == LeaseState.Holding) Toast("控制权已恢复");
        else if (t.To == LeaseState.None && t.Reason.Contains("takenBack")) Toast("触屏已收回控制权");
        else if (t.To == LeaseState.None && t.Reason.Contains("invalidated")) Toast("急停：控制权已失效");
        else if (t.To == LeaseState.None && t.From is LeaseState.Holding or LeaseState.Lost && t.Reason.StartsWith("state:")) Toast("重连后设备已回到本地模式 · 控制权失效" + (t.Reason == "state:local" ? "，正在重新申请" : ""));
        else if (t.To == LeaseState.None && t.From is LeaseState.Holding or LeaseState.Lost && t.Reason.StartsWith("heartbeat:")) Toast("心跳被拒 · 控制权失效（" + t.Reason[10..] + "）");
        else if (t.To == LeaseState.None && t.From is LeaseState.Holding or LeaseState.Lost && t.Reason.StartsWith("write:")) Toast("写命令被拒 · 控制权失效（" + t.Reason[6..] + "）");
        else if (t.To == LeaseState.None && t.Reason == "expired") Toast("控制权宽限期满 · 正在重新申请");
        else if (t.To == LeaseState.Holding && t.From == LeaseState.Requesting && t.Reason == "granted" && _autoReacquired) { Toast("控制权已重新取得"); _autoReacquired = false; }
        if (t.To == LeaseState.Requesting && t.From == LeaseState.None && !ControlDialog.Requesting) _autoReacquired = true;
    }

    private void OnControlMsg(ControlMsg m) => UpdateControlBadge();

    private void UpdateControlBadge()
    {
        var c = Connection;
        if (c == null) { ControlBadge = "● 未连接"; ControlBadgeClass = "idle"; CanControl = false; return; }
        var st = c.Lease.State;
        var cs = c.ControlState;
        CanControl = st == LeaseState.Holding;
        switch (st)
        {
            case LeaseState.Holding: ControlBadge = $"● 远程控制中 · {Config.ClientName}"; ControlBadgeClass = "remote"; break;
            case LeaseState.Requesting: ControlBadge = $"● 申请中 · 等待触屏确认 · 剩余 {_requestRemaining} s"; ControlBadgeClass = "requesting"; break;
            case LeaseState.Lost: ControlBadge = $"● 连接丢失 · 宽限 {c.Lease.LeaseGraceSec} s"; ControlBadgeClass = "lost"; break;
            default:
                ControlBadge = cs?.Mode == ControlModes.Remote ? $"● 远程（{cs.Holder}）" : cs?.Mode == ControlModes.RemoteLost ? "● 远程丢失" : "● 本地";
                ControlBadgeClass = "idle"; break;
        }
        ControlDialog.Holding = st == LeaseState.Holding;
        ControlDialog.Requesting = st == LeaseState.Requesting;
        ControlDialog.ModeText = cs == null ? "—" : cs.Mode switch { ControlModes.Local => "本地", ControlModes.Remote => "远程", ControlModes.Requested => "已申请", ControlModes.RemoteLost => "远程丢失", _ => cs.Mode };
        ControlDialog.HolderText = cs?.Holder ?? "—";
        ControlDialog.LeaseText = c.Lease.LeaseId == null ? "—" : $"{c.Lease.LeaseId[..Math.Min(8, c.Lease.LeaseId.Length)]}… · 心跳 {c.Lease.HeartbeatTimeoutSec} s · 宽限 {c.Lease.LeaseGraceSec} s";
        ControlDialog.StatusLine = st switch
        {
            LeaseState.Holding => $"远程控制中（{Config.ClientName}）",
            LeaseState.Requesting => $"申请中（等待触屏确认 · 剩余 {_requestRemaining} s）",
            LeaseState.Lost => $"连接丢失（宽限 {c.Lease.LeaseGraceSec} s）",
            _ => cs?.Mode == ControlModes.Remote ? $"远程（{cs.Holder}）" : "本地",
        };
    }

    private void OnSecond()
    {
        if (ControlDialog.Requesting && _requestRemaining > 0) { _requestRemaining--; ControlDialog.RemainingSec = _requestRemaining; UpdateControlBadge(); }
    }

    // ───────────────────────── 遥测 ─────────────────────────

    public void OnTelemetry(TelemetrySnapshot f)
    {
        _firstFrameAt ??= f.Ts;
        StirRpm = f.Stir.On ? f.Stir.RpmSp : 0; _stirOn = f.Stir.On;
        StirText = $"{(f.Stir.On ? f.Stir.RpmSp : 0)} rpm" + (f.Stir.Locked ? " · 程序占用" : "") + (f.Stir.Fault != null ? " · 故障" : "");
        for (int i = 0; i < 8 && i < f.Channels.Length; i++)
        {
            var ch = Channels[i];
            ch.Apply(f.Channels[i], f.Stir.BusVoltageV);
            if (_wasRunning[i] && !ch.ProgramRunning) _programEndedAt[i] = f.Ts;
            if (ch.ProgramRunning) _programEndedAt[i] = null;
            _wasRunning[i] = ch.ProgramRunning;
        }
        EstopActive = f.Estop.Active;
        EstopText = f.Estop.Active ? $"急停中 · 加热 {f.Estop.Heat} / 搅拌 {f.Estop.Stir} / 阀门 {f.Estop.Valves}" : "";
        var cur = CurrentChannel;
        Plan.UpdateLive(StirRpm, cur.ProgramRunning ? cur.StepIndex : -1, cur.ProgramRunning,
            cur.Installed && cur.State != ChannelStates.Alarm && f.Comm.Temp && f.Comm.Pressure ? "= 通过" : "= 未通过",
            (f.Estop.Active ? "急停已触发" : "急停未触发") + " · " + (cur.PressurePsi is double p && p < 200 ? "压力在安全范围" : cur.PressurePsi is null ? "压力无读数" : "压力偏高"));
        RefreshProperties(f);
        RebuildChart();
        UpdateTimeline(f.Ts);
        if (Connection is IReactorConnection pc) Perf.OnTelemetry(pc, f);
        if (!f.Comm.Temp || !f.Comm.Pressure || !f.Comm.Stir || !f.Comm.Io) ConnectionText = "已连接 · 模块离线：" + string.Join(" ", new[] { f.Comm.Temp ? null : "温度", f.Comm.Pressure ? null : "压力", f.Comm.Stir ? null : "搅拌", f.Comm.Io ? null : "IO" }.Where(s => s != null));
    }

    private void BuildProperties()
    {
        Properties.Add(new PropertyCardVm("pv", "/RV1/模拟输入", "反应器温度", "温度 PV", "—", "℃", true));
        Properties.Add(new PropertyCardVm("press", "/RV1/模拟输入", "压力传感器", "压力", "—", "psi · — MPa", true));
        Properties.Add(new PropertyCardVm("sp", "/RV1/设备/加热器", "加热器", "温度设定值 SP", "—", "℃", false));
        Properties.Add(new PropertyCardVm("stir", "/全局/搅拌电机", "搅拌电机（全局）", "转速 RV1–RV8 共用", "0", "rpm", true));
        Properties.Add(new PropertyCardVm("out", "/RV1/设备/加热器", "加热器", "加热输出", "—", "%", false));
        Properties.Add(new PropertyCardVm("valve", "/RV1/设备/阀门", "进气阀 SV / 排空阀", "— · 关 / 关", "关 / 关", "惰性气体 待机", false));
    }

    private void RefreshProperties(TelemetrySnapshot? f = null)
    {
        f ??= Connection?.Telemetry.Latest;
        var ch = CurrentChannel;
        var rv = CurrentReactorName;
        var pv = Properties[0]; pv.Path = $"/{rv}/模拟输入"; pv.Number = ch.TempText; pv.GreenEdge = ch.TempC != null && ch.TempOnline; pv.IsOffline = !ch.TempOnline;
        var pr = Properties[1]; pr.Path = $"/{rv}/模拟输入"; pr.Number = ch.PressureText; pr.Unit = ch.PressurePsi is double p ? $"psi · {p * 0.00689476:0.00} MPa" : "psi · — MPa"; pr.GreenEdge = ch.PressurePsi != null;
        var sp = Properties[2]; sp.Path = $"/{rv}/设备/加热器"; sp.Number = ch.SpText;
        sp.Secondary = "温度设定值 SP" + (ch.SpMode == "curve" ? " · 曲线" : "") + (ch.ApproachPhase is "settle" or "creep" or "hold" ? $" · {ch.ApproachText}" : "");
        var st = Properties[3];
        if (f != null) { st.Number = (f.Stir.On ? f.Stir.RpmSp : 0).ToString(CultureInfo.InvariantCulture); st.Unit = f.Stir.On ? "rpm" : "rpm · 停止"; st.IsFault = f.Stir.Fault != null || f.Stir.AlarmCode is > 0; st.Secondary = f.Stir.Locked ? "转速 RV1–RV8 共用 · 程序占用" : "转速 RV1–RV8 共用"; st.GreenEdge = f.Stir.Online; }
        var ho = Properties[4]; ho.Path = $"/{rv}/设备/加热器"; ho.Number = ch.HeatOutputPct is double h ? h.ToString("0", CultureInfo.InvariantCulture) : "—";
        var va = Properties[5]; va.Path = $"/{rv}/设备/阀门";
        if (f != null)
        {
            var open = f.Gas.Inlets.FirstOrDefault(i => i.Open);
            var gasName = open == null ? "—" : open.Id switch { "inert" => "惰性气体", "A" => "气体A", "B" => "气体B", _ => open.Id };
            va.Secondary = $"{gasName} · {(ch.ValveOpen ? "开" : "关")} / {(f.Gas.VentOpen ? "开" : "关")}";
            va.Number = $"{(ch.ValveOpen ? "开" : "关")} / {(f.Gas.VentOpen ? "开" : "关")}";
            va.Unit = f.Gas.Task != null ? $"{f.Gas.Owner ?? "手动"} · {f.Gas.Task}" : f.Gas.ManualValve ? "手动阀 已开" : "惰性气体 待机";
        }
        // 状态 / 程序 / 报警 的文字放到属性卡 1 的副标题
        pv.Secondary = "温度 PV · " + ch.StateText + (ch.HasProgram ? $" · 程序 {ChannelProgramStates.Zh(ch.ProgramState)} {ch.StepIndex}/{ch.StepCount}" : "");
    }

    private void RebuildChart()
    {
        var c = Connection;
        foreach (var s in Chart.Series) s.Points.Clear();
        foreach (var s in AllChart.Series) s.Points.Clear();
        if (c == null) { Chart.Revision++; AllChart.Revision++; return; }
        var frames = c.Telemetry.Snapshot();
        if (frames.Length == 0) return;
        var ch = CurrentChannel;
        var origin = ch.ProgramStartedAt ?? _firstFrameAt ?? frames[0].Ts;
        double maxX = 0, maxT = 0, maxP = 0;
        foreach (var f in frames)
        {
            if (f.Ts < origin) continue;
            double x = (f.Ts - origin).TotalMinutes;
            maxX = Math.Max(maxX, x);
            var cs = f.Channels[CurrentRv - 1];
            if (cs.TempC is double t) { Chart.Series[0].Points.Add(new Point(x, t)); maxT = Math.Max(maxT, t); }
            if (cs.PressurePsi is double p) { Chart.Series[1].Points.Add(new Point(x, p)); maxP = Math.Max(maxP, p); }
            Chart.Series[2].Points.Add(new Point(x, (f.Stir.On ? f.Stir.RpmSp : 0) / 2.0));
        }
        Chart.XMax = NiceXMax(maxX);
        Chart.LeftMax = maxT <= 100 ? 100 : maxT <= 200 ? 200 : 300;
        Chart.RightMax = Math.Max(500, Math.Ceiling(maxP / 500) * 500);
        Chart.Revision++;

        var o2 = _firstFrameAt ?? frames[0].Ts;
        double mx2 = 0, mt2 = 0;
        foreach (var f in frames)
        {
            double x = (f.Ts - o2).TotalMinutes; mx2 = Math.Max(mx2, x);
            for (int i = 0; i < 8 && i < f.Channels.Length; i++)
                if (f.Channels[i].TempC is double t) { AllChart.Series[i].Points.Add(new Point(x, t)); mt2 = Math.Max(mt2, t); }
            AllChart.Series[8].Points.Add(new Point(x, (f.Stir.On ? f.Stir.RpmSp : 0) / 2.0));
        }
        AllChart.XMax = NiceXMax(mx2);
        AllChart.LeftMax = mt2 <= 100 ? 100 : mt2 <= 200 ? 200 : 300;
        AllChart.Revision++;
    }

    /// <summary>X 轴范围：无数据 / 超过 70 min 时用 mock 的 88 min 格；短数据按 1 / 2 / 5 / 10 / 20 / 40 min 档位放大，曲线可见。</summary>
    internal static double NiceXMax(double spanMin)
        => spanMin <= 0 ? 88 : spanMin < 0.9 ? 1 : spanMin < 1.8 ? 2 : spanMin < 4.5 ? 5 : spanMin < 9 ? 10 : spanMin < 18 ? 20 : spanMin < 36 ? 40 : spanMin < 70 ? 88 : Math.Ceiling(spanMin * 1.1 / 10) * 10;

    private void UpdateTimeline(DateTime nowUtc)
    {
        var win = TimeSpan.FromMinutes(Math.Max(1, Config.TimelineMinutes));
        var start = nowUtc - win;
        UpdateTimelineLabels(nowUtc);
        for (int i = 0; i < 8; i++)
        {
            var r = TimelineRows[i]; var ch = Channels[i];
            r.Disabled = !ch.Installed;
            if (!ch.HasProgram || ch.ProgramStartedAt is not DateTime sa)
            { r.HasBar = false; r.Left = 0; r.Right = 0; r.Complete = 0; r.Running = false; r.Done = false; r.WaitingGas = false; r.Label = ""; r.PlayTitle = ch.Installed ? "启动 RV" + ch.Id : "未安装"; continue; }
            var end = ch.ProgramRunning ? nowUtc : (_programEndedAt[i] ?? nowUtc);
            var (l, rr) = BarSpan((sa - start) / win, (end - start) / win);
            r.HasBar = true; r.Left = l; r.Right = rr;
            r.Complete = ch.ProgramState == ChannelProgramStates.Completed ? 1 : ch.StepCount > 0 ? Math.Clamp((double)ch.StepIndex / ch.StepCount, 0, 1) : 0;
            r.Running = ch.ProgramRunning; r.Done = ch.ProgramState == ChannelProgramStates.Completed; r.WaitingGas = ch.WaitingGas;
            r.Label = ch.ProgramRunning ? $"步骤 {Math.Min(ch.StepIndex + 1, ch.StepCount)}/{ch.StepCount} · {ChannelProgramStates.Zh(ch.ProgramState)}" : ChannelProgramStates.Zh(ch.ProgramState);
            r.PlayTitle = (ch.ProgramRunning ? "停止 RV" : "启动 RV") + ch.Id;
        }
    }

    /// <summary>时间轴条的 [左, 右] 比例：按真实时间落位，右端不超 1；刚启动的程序保证至少 <see cref="MinBarFraction"/>（像素最小宽由 TimelineTracks 再兜底 60px）。</summary>
    internal const double MinBarFraction = 0.06;
    internal static (double Left, double Right) BarSpan(double startFrac, double endFrac)
    {
        double rr = Math.Clamp(endFrac, 0, 1);
        double l = Math.Clamp(startFrac, 0, 1);
        if (rr - l < MinBarFraction) l = Math.Max(0, rr - MinBarFraction);
        return (l, rr);
    }

    private void UpdateTimelineLabels(DateTime nowUtc)
    {
        var win = TimeSpan.FromMinutes(Math.Max(1, Config.TimelineMinutes));
        string L(double frac) => (nowUtc - win + win * frac).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        T1 = L(.20); T2 = L(.48); T3 = L(.71); T4 = L(.90);
    }

    // ───────────────────────── Toast ─────────────────────────

    public void Toast(string text)
    {
        ToastText = text; ToastVisible = true;
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        _ = Task.Delay(1800, cts.Token).ContinueWith(t => { if (!t.IsCanceled) Dispatcher.UIThread.Post(() => { if (_toastCts == cts) ToastVisible = false; }); }, TaskScheduler.Default);
    }

    private void ToastError(Exception ex) => Toast(Describe(ex));

    private static string Describe(Exception ex) => ex switch
    {
        ApiException a => a.Code switch
        {
            OpCodes.ModeLocal => "409 本地模式：触屏未开放远程控制，请先申请控制权",
            OpCodes.NotControlHolder => "409 非控制权持有者：请先申请远程控制",
            OpCodes.ControlLeaseExpired => "409 控制权已过期：请重新申请",
            OpCodes.HwUnconfirmed => "硬件未确认：命令已下发但设备未在时限内确认，请核对属性卡 / 快照后再操作，勿直接重发",
            _ => $"{a.Status} {a.Code}：{a.Message}",
        },
        System.Net.Http.HttpRequestException h => "网络错误：" + h.Message,
        TaskCanceledException => "请求超时",
        _ => ex.Message,
    };

    /// <summary>收尾：停计时器、中止性能测试、释放租约、断开。可在任意线程调用；整体不超过 <paramref name="timeout"/>。</summary>
    public async Task ShutdownAsync(TimeSpan? timeout = null)
    {
        if (Dispatcher.UIThread.CheckAccess()) _tick.Stop(); else Dispatcher.UIThread.Post(() => _tick.Stop());
        var work = Task.Run(async () =>
        {
            await Perf.AbortAndWaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await _conns.DisconnectAsync().ConfigureAwait(false);
        });
        var done = await Task.WhenAny(work, Task.Delay(timeout ?? TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        if (done == work) await work.ConfigureAwait(false);
    }
}
