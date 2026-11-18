using Avalonia.Controls;
using Avalonia.Interactivity;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Home;

namespace ParallelReactor.Control.Views.Pages.Home;

/// <summary>仪表棒图（见 BarView.axaml）。样稿的容器查询按面板体的尺寸算（面板高 ≤ 300：short；面板宽 ≤ 700：nar），类设在 Root 上。</summary>
public partial class BarView : UserControl
{
    public BarView()
    {
        InitializeComponent();
        Root.SizeChanged += (_, _) => Apply();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is HomePageVm vm)
            vm.PropertyChanged += (_, a) => { if (a.PropertyName is nameof(HomePageVm.TileCount)) Apply(); };
        Apply();
    }

    /// <summary>样稿 @container pb (max-height:300px) / (max-width:700px)。</summary>
    public const double ShortH = 300, NarrowW = 700;

    private void Apply()
    {
        var vm = DataContext as HomePageVm;
        var size = Root.Bounds.Size;
        if (vm == null || size.Width <= 0 || size.Height <= 0) return;
        bool n16 = vm.TileCount > 8;
        Root.Classes.Set("n16", n16);
        Root.Classes.Set("n4", vm.TileCount <= 4);
        Root.Classes.Set("short", size.Height <= ShortH);
        Root.Classes.Set("nar", size.Width <= NarrowW);
        var m = n16 ? new Avalonia.Thickness(8, 6, 8, 10) : new Avalonia.Thickness(8, 6, 8, 10);
        if (size.Height <= ShortH) m = new Avalonia.Thickness(8, 4, 8, 6);
        Bars.Margin = m;
    }

    private void OnTile(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MonTileVm t } && DataContext is HomePageVm vm) vm.OpenTile(t);
    }
}
