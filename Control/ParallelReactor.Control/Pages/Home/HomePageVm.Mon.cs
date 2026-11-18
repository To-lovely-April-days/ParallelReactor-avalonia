using ParallelReactor.Control.Core.Format;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 主页通道面板的另外三种显示方式（融合版 F4，样稿 MONV = gas / vessel / bar）：气路图、反应釜卡片、仪表棒图。三种视图共用 <see cref="Tiles"/>（每路一个，就地更新）；
/// 灰带换成「运行 6/8 ｜ 搅拌 ｜ 维护模式 … 显示方式 · 停止」（样稿 monTool 的非运行表分支）；矮屏（&lt; 820）时曲线和共用资源那一行收起（样稿 .gcols.mv-x）。
/// </summary>
public sealed partial class HomePageVm
{
    private int _tilesFor;
    private GasNetVm? _gas;

    /// <summary>每路一个（按 RV 号，已安装的通道）。</summary>
    public ObservableCollection<MonTileVm> Tiles { get; } = new();
    /// <summary>气路图上排（16 路：RV1–8；其余：全部）。</summary>
    public ObservableCollection<MonTileVm> TilesTop { get; } = new();
    /// <summary>气路图下排（16 路：RV9–16；其余：空）。</summary>
    public ObservableCollection<MonTileVm> TilesLow { get; } = new();
    /// <summary>气路（气源、总管、排空）。</summary>
    public GasNetVm Gas => _gas ??= new GasNetVm();

    /// <summary>不是运行表（样稿 .gcols.mv-x：通道一行高一些；矮屏收起第二行）。</summary>
    public bool IsMvX => MonView != MonView.Run;
    public bool IsGasView => MonView == MonView.Gas;
    public bool IsVesselView => MonView == MonView.Vessel;
    public bool IsBarView => MonView == MonView.Bar;
    /// <summary>矮屏且不是运行表：曲线和共用资源那一行不显示（样稿 @media max-height 820 .gcols.mv-x > .grow.r2{display:none}）。</summary>
    public bool HideRow2 => IsMvX && IsShort;

    /// <summary>通道数（4 / 8 / 16）：视图按它排列（样稿 .n4 / .n8 / .n16）。</summary>
    [ObservableProperty] private int _tileCount;
    /// <summary>气路图 / 卡片的列数（4 路 4 列，其余 8 列）；棒图 16 路排一行（16 列）。</summary>
    [ObservableProperty] private int _monCols = 8;
    [ObservableProperty] private int _barCols = 8;
    /// <summary>16 路（气路图上下两排、卡片 / 棒图紧凑）。</summary>
    [ObservableProperty] private bool _isN16;
    [ObservableProperty] private bool _isN4;
    /// <summary>灰带「运行 6/8」的两段。</summary>
    [ObservableProperty] private string _runCount = "";
    [ObservableProperty] private string _runOf = "";
    /// <summary>棒图 16 路时灰带写单位「°C / MPa」（每根条下不写单位）。</summary>
    [ObservableProperty] private bool _showBarUnits;

    /// <summary>点阀门：只显示状态（电脑端没有维护模式的接口）。</summary>
    public ICommand ValveCommand => _valve ??= new RelayCommand(() => _shell.Toast(MonText.ValveToast, ToastKind.Info));
    private ICommand? _valve;

    /// <summary>点一路（釜卡 / 棒）：选中这一路并打开检查器（同运行表的行，样稿 data-ch）。</summary>
    public void OpenTile(MonTileVm t)
    {
        if (Device is { } d) _shell.OpenChannel(d.K, t.N);
    }

    partial void OnIsShortChanged(bool value) => OnPropertyChanged(nameof(HideRow2));

    private void OnMonViewChangedF4()
    {
        OnPropertyChanged(nameof(IsMvX));
        OnPropertyChanged(nameof(IsGasView));
        OnPropertyChanged(nameof(IsVesselView));
        OnPropertyChanged(nameof(IsBarView));
        OnPropertyChanged(nameof(HideRow2));
    }

    private void RefreshMon(DeviceVm d)
    {
        int installed = d.Reactors.Count(r => r.Installed);
        if (_tilesFor != d.K || Tiles.Count != installed)
        {
            Tiles.Clear();
            TilesTop.Clear();
            TilesLow.Clear();
            foreach (var r in d.Reactors) if (r.Installed) Tiles.Add(new MonTileVm(r.N));
            _tilesFor = d.K;
            bool mir = d.ChannelCount > 8;
            foreach (var t in Tiles) (mir && t.N > 8 ? TilesLow : TilesTop).Add(t);
        }
        TileCount = d.ChannelCount;
        IsN16 = d.ChannelCount > 8;
        IsN4 = d.ChannelCount <= 4;
        MonCols = IsN4 ? 4 : 8;
        BarCols = IsN16 ? 16 : IsN4 ? 4 : 8;
        ShowBarUnits = IsN16 && MonView == MonView.Bar;
        RunCount = d.Running.ToString(CultureInfo.InvariantCulture);
        RunOf = "/" + d.Installed.ToString(CultureInfo.InvariantCulture);

        Gas.Refresh(d);
        var lim = d.Model.Limits;
        var plim = d.Source.Plant.OverPressureMPa;
        int sel = _shell.Nav.ChannelOf(d.K);
        bool stale = d.IsOffline && d.EverOnline;
        int? devSteps = d.Reactors.Where(r => r.Program?.Record is { Steps.Count: > 0 }).GroupBy(r => r.Program!.Record!.Steps.Count)
            .OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();
        foreach (var t in Tiles)
        {
            if (d.Reactor(t.N) is not { } r) continue;
            var (mark, tip) = MarkOf(d, t.N);
            t.Apply(r, lim.TempMaxC, lim.PressureMaxMPa, plim, Gas.SrcOn, d.EstopActive, t.N == sel, stale, mark, tip, devSteps);
            t.BarTUnit = IsN16 ? "" : " °C";
            t.BarPUnit = IsN16 ? "" : " " + PressureDisplay.Unit;
        }
    }

    /// <summary>这一路的报警标记（样稿 chAl + ind）：有报警级的取报警，否则提醒；悬停「报警 · 压力超上限 · 未确认」。</summary>
    private static (TileMark, string?) MarkOf(DeviceVm d, int n)
    {
        AlarmVm? hit = null;
        foreach (var a in d.Alarms)
        {
            if (a.Ch != n) continue;
            if (hit == null || (a.IsErr && !hit.IsErr)) hit = a;
        }
        if (hit == null) return (TileMark.None, null);
        return (hit.IsErr ? TileMark.Err : TileMark.Warn, (hit.IsErr ? "报警" : "提醒") + " · " + hit.Text + " · " + (hit.Acked ? "已确认" : "未确认"));
    }
}
