using Avalonia.Controls;
using Avalonia.Interactivity;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Home;

namespace ParallelReactor.Control.Views.Pages.Home;

/// <summary>
/// 反应釜卡片（见 VesselView.axaml）。样稿的容器查询按面板体 / 卡片的尺寸算（<see cref="VesselLayout"/>），类设在 Root 上。
/// </summary>
public partial class VesselView : UserControl
{
    private VesselLayout? _last;

    public VesselView()
    {
        InitializeComponent();
        Root.SizeChanged += (_, _) => Apply();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is HomePageVm vm)
            vm.PropertyChanged += (_, a) => { if (a.PropertyName is nameof(HomePageVm.TileCount) or nameof(HomePageVm.MonCols)) Apply(); };
        Apply();
    }

    private void Apply()
    {
        var vm = DataContext as HomePageVm;
        var size = Root.Bounds.Size;
        if (vm == null || size.Width <= 0 || size.Height <= 0) return;
        var L = VesselLayout.For(size.Width, size.Height, vm.TileCount);
        if (_last == L) return;
        _last = L;
        Root.Classes.Set("n16", L.N16);
        Root.Classes.Set("n4", L.N4);
        Root.Classes.Set("cmp", L.Compact);
        Root.Classes.Set("wide", L.Wide);
        Root.Classes.Set("c112", L.CardW <= 112);
        Root.Classes.Set("c84", L.CardW <= 84);
        Cards.Margin = L.N16 ? new Avalonia.Thickness(9, 8, 9, 4) : new Avalonia.Thickness(8, 8, 8, 2);
    }

    private void OnTile(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MonTileVm t } && DataContext is HomePageVm vm) vm.OpenTile(t);
    }
}

/// <summary>
/// 卡片视图的版面（样稿 .vgrid / .vcard 的容器查询，纯数据可测）：列数（4 路 4，其余 8）、卡片内容宽（面板宽 − 内边距 24 − 间距）、
/// 紧凑（16 路一律；面板高 ≤ 300 全部）、宽卡（卡宽 ≥ 190 且不紧凑：图在左）。
/// </summary>
public sealed record VesselLayout(bool N16, bool N4, bool Compact, bool Wide, double CardW)
{
    public const double ShortH = 300, WideW = 190;

    public static VesselLayout For(double w, double h, int channels)
    {
        bool n16 = channels > 8, n4 = channels <= 4;
        int cols = n4 ? 4 : 8;
        double gap = n16 ? 6 : 8;
        double card = (w - 24 - gap * (cols - 1)) / cols - 2;
        bool cmp = n16 || h <= ShortH;
        return new VesselLayout(n16, n4, cmp, !cmp && card >= WideW, card);
    }
}
