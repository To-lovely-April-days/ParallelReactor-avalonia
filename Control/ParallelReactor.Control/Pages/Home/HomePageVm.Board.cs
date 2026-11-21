using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 主页看板：通道面板（气路图）· 曲线面板（叠加曲线）· 共用资源面板；三块都能折叠成竖条。
/// </summary>
public sealed partial class HomePageVm
{
    private NetBannerVm? _net;
    private OverlayVm? _curve;
    private ResPaneVm? _res;

    /// <summary>断线横条。</summary>
    public NetBannerVm Net => _net ??= new NetBannerVm(_shell);
    /// <summary>叠加曲线。</summary>
    public OverlayVm Curve => _curve ??= new OverlayVm(_shell);
    /// <summary>共用资源。</summary>
    public ResPaneVm Res => _res ??= new ResPaneVm(_shell);

    /// <summary>窄屏（&lt; 1600）：曲线说明不写、断线横条折两行。</summary>
    [ObservableProperty] private bool _isNarrow;

    // —— 三块面板的折叠（收起成 30 的竖条） ——
    [ObservableProperty] private PaneMode _monMode = PaneMode.Docked;
    [ObservableProperty] private PaneMode _curveMode = PaneMode.Docked;
    [ObservableProperty] private PaneMode _resMode = PaneMode.Docked;

    public ICommand ToggleMonCommand => _tMon ??= new RelayCommand(() => MonMode = Flip(MonMode));
    public ICommand ToggleCurveCommand => _tCurve ??= new RelayCommand(() => CurveMode = Flip(CurveMode));
    public ICommand ToggleResCommand => _tRes ??= new RelayCommand(() => ResMode = Flip(ResMode));
    private ICommand? _tMon, _tCurve, _tRes;

    private static PaneMode Flip(PaneMode m) => m == PaneMode.Collapsed ? PaneMode.Docked : PaneMode.Collapsed;

    /// <summary>看板回到默认（重置布局 / 截图前）：三块都展开。</summary>
    public void ResetBoard()
    {
        MonMode = CurveMode = ResMode = PaneMode.Docked;
        Curve.ModeKey = "G";
    }

    [ObservableProperty] private string _stopDevLabel = "停止";

    /// <summary>「停止1号机」：打开全部停止对话框，预选这台。</summary>
    public ICommand StopDeviceCommand => _stopDev ??= new RelayCommand(() => { if (Device is { } d) _shell.OpenDialog("stopall", d.K.ToString(CultureInfo.InvariantCulture)); });
    private ICommand? _stopDev;

    private void RefreshBoard(DeviceVm d)
    {
        StopDevLabel = "停止" + d.Alias;
        IsNarrow = !_shell.Layout.IsWide;
        Net.Apply(d);
        Curve.Refresh(d);
        Res.IsShort = _shell.Layout.IsShort;
        Res.Refresh(d);
        RefreshMon(d);
    }
}
