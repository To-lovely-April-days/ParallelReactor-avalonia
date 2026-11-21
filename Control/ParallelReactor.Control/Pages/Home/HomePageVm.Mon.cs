using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Home;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 主页通道面板的气路图数据（<see cref="Tiles"/> / <see cref="TilesTop"/> / <see cref="TilesLow"/> + <see cref="Gas"/>）。
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

    /// <summary>通道数（4 / 8 / 16）：气路图按它排列。</summary>
    [ObservableProperty] private int _tileCount;
    /// <summary>气路图列数（4 路 4 列，其余 8 列）。</summary>
    [ObservableProperty] private int _monCols = 8;
    /// <summary>16 路（气路图上下两排）。</summary>
    [ObservableProperty] private bool _isN16;
    [ObservableProperty] private bool _isN4;
    /// <summary>灰带「运行 6/8」的两段。</summary>
    [ObservableProperty] private string _runCount = "";
    [ObservableProperty] private string _runOf = "";

    /// <summary>点气路图上的阀门（HMI v0.7：直接开关，见 <see cref="ValveCtl"/>）：第 n 路 SVn / 一路气源 / 排空。</summary>
    public void ToggleSv(int n, bool isOpen) { if (Device is { } d) ValveCtl.ToggleSv(_shell, d, n, isOpen); }
    public void ToggleGas(string key, bool isOpen) { if (Device is { } d) ValveCtl.ToggleGas(_shell, d, key, isOpen); }
    public void ToggleVent() { if (Device is { Model.Vent: { } v } d) ValveCtl.ToggleGas(_shell, d, v.Key, d.Source.GasOpen(v.Key)); }

    /// <summary>点一路釜卡：选中这一路并打开检查器。</summary>
    public void OpenTile(MonTileVm t)
    {
        if (Device is { } d) _shell.OpenChannel(d.K, t.N);
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
            t.Apply(r, lim.TempMaxC, lim.PressureMaxMPa, plim, Gas.SrcOn, d.EstopActive, t.N == sel, stale, mark, tip, devSteps, ValveCtl.SvReason(d, r.N, r.Valve));
        }
    }

    /// <summary>这一路的报警标记：有报警级的取报警，否则提醒；悬停「报警 · 压力超上限 · 未确认」。</summary>
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
