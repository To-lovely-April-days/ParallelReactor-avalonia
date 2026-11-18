using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>通道面板的显示方式（样稿 MONV：运行表 / 气路图 / 卡片 / 棒图；后三种 F4 做）。</summary>
public enum MonView { Run, Gas, Vessel, Bar }

/// <summary>
/// 主页看板（融合版 F3，样稿 renderBoard 的 main 分支）：通道面板（运行表）· 曲线面板（8 路叠加）· 共用资源面板；三块都能折叠成竖条（样稿 PCOL）。
/// </summary>
public sealed partial class HomePageVm
{
    private int _rowsFor;
    private NetBannerVm? _net;
    private OverlayVm? _curve;
    private ResPaneVm? _res;

    /// <summary>断线横条。</summary>
    public NetBannerVm Net => _net ??= new NetBannerVm(_shell);
    /// <summary>叠加曲线。</summary>
    public OverlayVm Curve => _curve ??= new OverlayVm(_shell);
    /// <summary>共用资源。</summary>
    public ResPaneVm Res => _res ??= new ResPaneVm(_shell, () => MonView = MonView.Gas);

    /// <summary>窄屏（&lt; 1600）：运行表格子左右 10、程序步骤名宽 100；曲线说明不写。</summary>
    [ObservableProperty] private bool _isNarrow;

    // —— 三块面板的折叠（样稿 PCOL：收起成 30 的竖条） ——
    [ObservableProperty] private PaneMode _monMode = PaneMode.Docked;
    [ObservableProperty] private PaneMode _curveMode = PaneMode.Docked;
    [ObservableProperty] private PaneMode _resMode = PaneMode.Docked;

    public ICommand ToggleMonCommand => _tMon ??= new RelayCommand(() => MonMode = Flip(MonMode));
    public ICommand ToggleCurveCommand => _tCurve ??= new RelayCommand(() => CurveMode = Flip(CurveMode));
    public ICommand ToggleResCommand => _tRes ??= new RelayCommand(() => ResMode = Flip(ResMode));
    private ICommand? _tMon, _tCurve, _tRes;

    private static PaneMode Flip(PaneMode m) => m == PaneMode.Collapsed ? PaneMode.Docked : PaneMode.Collapsed;

    /// <summary>看板回到默认（重置布局 / 截图前）：三块都展开、显示方式回到运行表。</summary>
    public void ResetBoard()
    {
        MonMode = CurveMode = ResMode = PaneMode.Docked;
        MonView = MonView.Run;
        Curve.ModeKey = "G";
    }

    /// <summary>切回运行表（报警弹窗的「定位」）。</summary>
    public void ShowRunTable() => MonView = MonView.Run;

    /// <summary>显示方式的分段值（run / gas / vessel / bar）。</summary>
    public string MonKey
    {
        get => MonView switch { MonView.Gas => "gas", MonView.Vessel => "vessel", MonView.Bar => "bar", _ => "run" };
        set => MonView = value switch { "gas" => MonView.Gas, "vessel" => MonView.Vessel, "bar" => MonView.Bar, _ => MonView.Run };
    }

    /// <summary>运行表的行（选中设备的全部已安装通道，按 RV 号；换设备时重建，否则就地更新）。</summary>
    public ObservableCollection<RunRowVm> Rows { get; } = new();

    /// <summary>16 路设备：紧凑行（样稿 .rtab.c16：32 高）。</summary>
    [ObservableProperty] private bool _isC16;
    /// <summary>这台设备断线（运行表读数变灰，样稿 .rtab.stale）。</summary>
    [ObservableProperty] private bool _isStale;
    /// <summary>通道面板灰带：「8 路 · 运行 」「6」「 · 」「报警 1」「 · 程序「…」」（别名另一段）。</summary>
    [ObservableProperty] private string _ctxA = "";
    [ObservableProperty] private string _ctxRun = "";
    [ObservableProperty] private string _ctxErr = "";
    [ObservableProperty] private string _ctxSep = "";
    [ObservableProperty] private string _ctxProg = "";
    [ObservableProperty] private MonView _monView = MonView.Run;
    [ObservableProperty] private string _stopDevLabel = "停止";
    /// <summary>不是运行表时的占位说明（气路图 / 卡片 / 棒图在 F4）。</summary>
    [ObservableProperty] private string _monPlaceholder = "";

    public bool IsRunView => MonView == MonView.Run;
    partial void OnMonViewChanged(MonView value)
    {
        OnPropertyChanged(nameof(IsRunView));
        OnPropertyChanged(nameof(MonKey));
        MonPlaceholder = value switch
        {
            MonView.Gas => "气路图（阀门、管路、维护模式下手动开关）在 F4 做",
            MonView.Vessel => "反应釜卡片在 F4 做",
            MonView.Bar => "仪表棒图在 F4 做",
            _ => "",
        };
    }


    /// <summary>「停止1号机」：打开全部停止对话框，预选这台。</summary>
    public ICommand StopDeviceCommand => _stopDev ??= new RelayCommand(() => { if (Device is { } d) _shell.OpenDialog("stopall", d.K.ToString(CultureInfo.InvariantCulture)); });
    private ICommand? _stopDev;

    /// <summary>点运行表的一行：选中这一路并打开检查器（样稿 data-ch 的点击：SEL + CTL_OPEN + 展开检查器）。</summary>
    public void OpenRow(RunRowVm row)
    {
        if (Device is { } d) _shell.OpenChannel(d.K, row.N);
    }

    private void RefreshBoard(DeviceVm d)
    {
        var now = d.Source.IsDemo ? _shell.Fleet.Now : d.Source.History.Now;
        if (_rowsFor != d.K || Rows.Count != d.Reactors.Count(r => r.Installed))
        {
            Rows.Clear();
            foreach (var r in d.Reactors) if (r.Installed) Rows.Add(new RunRowVm(r.N));
            _rowsFor = d.K;
        }
        IsC16 = d.ChannelCount > 8;
        IsStale = d.IsOffline && d.EverOnline;
        int sel = _shell.Nav.ChannelOf(d.K);
        var plim = d.Source.Plant.OverPressureMPa;
        foreach (var row in Rows)
            if (d.Reactor(row.N) is { } r) row.Apply(r, now, plim, row.N == sel, IsStale);

        int err = d.Reactors.Count(r => r.Installed && r.Own == StateKey.Alarm);
        CtxA = " · " + d.Installed.ToString(CultureInfo.InvariantCulture) + " 路 · 运行 ";
        CtxRun = d.Running.ToString(CultureInfo.InvariantCulture);
        CtxErr = err > 0 ? "报警 " + err.ToString(CultureInfo.InvariantCulture) : "";
        CtxSep = err > 0 ? " · " : "";
        var prog = d.Reactors.Where(r => r.Program is { IsActive: true }).GroupBy(r => r.Program!.Name).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
                   ?? d.Reactors.Select(r => r.Program?.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        CtxProg = prog is { Length: > 0 } ? " · 程序「" + prog + "」" : "";
        StopDevLabel = "停止" + d.Alias;
        IsNarrow = !_shell.Layout.IsWide;
        Net.Apply(d);
        Curve.Refresh(d);
        Res.IsShort = _shell.Layout.IsShort;
        Res.Refresh(d);
    }
}
