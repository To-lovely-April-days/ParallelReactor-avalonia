using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ParallelReactor.Control.Controls.Home;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Home;

namespace ParallelReactor.Control.Views.Pages.Home;

/// <summary>
/// 气路图（见 GasView.axaml）。样稿的容器查询在这里按面板体的尺寸算：<see cref="GasLayout"/> 给出网格的列宽 / 行高 / 内边距与各种偏移，
/// 再把类（g / c / w / mir / n4 / tiny）设在 Root 上，字号、釜图大小这些交给样式。
/// </summary>
public partial class GasView : UserControl
{
    private GasLayout? _last;

    public GasView()
    {
        InitializeComponent();
        Root.SizeChanged += (_, _) => Apply();
        CardsTop.SizeChanged += (_, _) => FitPipes();
        CardsLow.SizeChanged += (_, _) => FitPipes();
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
        var L = GasLayout.For(size.Width, size.Height, vm.TileCount);
        if (_last == L) { FitPipes(); return; }
        _last = L;
        Root.Classes.Set("g", L.Big);
        Root.Classes.Set("c", L.Compact);
        Root.Classes.Set("w", L.Narrow);
        Root.Classes.Set("mir", L.Mir);
        Root.Classes.Set("n4", L.N4);
        Root.Classes.Set("tiny", L.Tiny);

        Gn.Margin = L.Pad;
        Gn.MaxWidth = L.Big ? GasLayout.MaxW - L.Pad.Left - L.Pad.Right : double.PositiveInfinity;
        Gn.VerticalAlignment = L.Big ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        Gn.ColumnDefinitions = new ColumnDefinitions { new(L.Gl, GridUnitType.Pixel), new(1, GridUnitType.Star), new(L.Gr, GridUnitType.Pixel) };
        var rows = new RowDefinitions();
        if (L.Mir)
        {
            rows.Add(new RowDefinition(GridLength.Auto));
            rows.Add(Pipes(L));
            rows.Add(new RowDefinition(9, GridUnitType.Pixel));
            rows.Add(Pipes(L));
            rows.Add(new RowDefinition(GridLength.Auto));
        }
        else
        {
            rows.Add(new RowDefinition(GridLength.Auto));
            rows.Add(Pipes(L));
            rows.Add(new RowDefinition(L.BusH, GridUnitType.Pixel));
        }
        Gn.RowDefinitions = rows;

        // 左：气源。16 路跨上下两排竖管、竖直居中（汇流管从第一路到最后一路）；否则贴底（底 8），汇流管伸进总管 2
        Grid.SetRow(Gin, 1);
        Grid.SetRowSpan(Gin, L.Mir ? 3 : 1);
        Ins.VerticalAlignment = L.Mir ? VerticalAlignment.Center : VerticalAlignment.Bottom;
        InsRows.Margin = new Thickness(0, 0, L.Co, L.Mir ? 0 : L.InsPadB);
        InsLn.Margin = new Thickness(0, L.Rh / 2, L.Co - 1.5, L.Mir ? L.Rh / 2 : -2);
        if (InsRows.ItemsPanelRoot is StackPanel sp) sp.Spacing = L.Compact ? 1 : 3;
        // 右：排空。16 路跨上排釜卡 + 竖管
        Grid.SetRow(Vent, L.Mir ? 0 : 1);
        Grid.SetRowSpan(Vent, L.Mir ? 2 : 1);
        VentLn.Margin = new Thickness(0, L.Compact ? 20 : 22, 0, L.Mir ? -4 : -2);
        // 总管：从汇流管（左 gl − co − 2）到排空竖管中间（右 gr / 2 − 2）
        Hdr.Height = L.Compact ? 4 : 5;
        Hdr.Margin = new Thickness(L.Gl - L.Co - 2, L.Mir ? 2 : 0, L.Gr / 2 - 2, 0);
        HdrLabel.Margin = new Thickness(L.Gl, 8, L.Gr, 0);
        FitPipes();
    }

    private static RowDefinition Pipes(GasLayout L) =>
        L.Big ? new RowDefinition(L.PipeMax, GridUnitType.Pixel) : new RowDefinition(1, GridUnitType.Star);

    /// <summary>
    /// 放大时竖管行 = min(上限, 面板体剩下的高度)（样稿 minmax(0, 220px) + align-content:center）：网格竖直居中时星号行会缩到内容高，所以这里按釜卡的实际高度算成像素。
    /// </summary>
    private void FitPipes()
    {
        if (_last is not { Big: true } L || Gn.RowDefinitions.Count < 3) return;
        double avail = Root.Bounds.Height - L.Pad.Top - L.Pad.Bottom;
        double cards = CardsTop.Bounds.Height + (L.Mir ? CardsLow.Bounds.Height : 0);
        if (cards <= 0) return;
        double h = GasLayout.PipeRow(avail, cards, L);
        var len = new GridLength(h, GridUnitType.Pixel);
        if (Gn.RowDefinitions[1].Height != len) Gn.RowDefinitions[1].Height = len;
        if (L.Mir && Gn.RowDefinitions[3].Height != len) Gn.RowDefinitions[3].Height = len;
    }

    private void OnTile(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MonTileVm t } && DataContext is HomePageVm vm) vm.OpenTile(t);
    }

    private void OnValve(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HomePageVm vm) vm.ValveCommand.Execute(null);
        e.Handled = true;
    }
}

/// <summary>
/// 气路图的版面（样稿 .gn 的变量与容器查询，纯数据可测）：
/// gl / gr = 左气源列 / 右排空列（136 / 74；面板宽 ≤ 820：96 / 46；面板高 ≤ 260：88 / 40）、co = 汇流管离气源列右边（14，紧凑 8）、
/// rh = 气源一行高（28，紧凑 19）；内边距 8 12 2（紧凑 6 6 0）；总管行 26（放大 30、紧凑 10）。放大 = 面板高 ≥ 460（竖管限高 220 / 16 路 110、整体最宽 1500 居中）。
/// tiny = 釜卡内容宽 ≤ 70（样稿 .rv 的 @container (max-width:70px)）。
/// </summary>
public sealed record GasLayout(bool Big, bool Compact, bool Narrow, bool Mir, bool N4, bool Tiny,
    double Gl, double Gr, double Co, double Rh, double BusH, double InsPadB, Thickness Pad)
{
    public const double MaxW = 1500, BigH = 460, CompactH = 260, NarrowW = 820;

    /// <summary>放大时竖管行的上限：220（16 路上下各 110）。</summary>
    public double PipeMax => Mir ? 110 : 220;

    /// <summary>放大时竖管行的高：上限与（可用高 − 釜卡 − 总管行）之间取小（16 路两行平分）。</summary>
    public static double PipeRow(double avail, double cards, GasLayout l)
    {
        double rest = avail - cards - (l.Mir ? 9 : l.BusH);
        return System.Math.Clamp(l.Mir ? rest / 2 : rest, 0, l.PipeMax);
    }

    public static GasLayout For(double w, double h, int channels)
    {
        bool big = h >= BigH, cmp = h <= CompactH, nar = w <= NarrowW, mir = channels > 8, n4 = channels <= 4;
        double gl = nar ? 96 : cmp ? 88 : 136, gr = nar ? 46 : cmp ? 40 : 74;
        double co = cmp ? 8 : 14, rh = cmp ? 19 : 28;
        var pad = cmp ? new Thickness(6, 6, 6, 0) : new Thickness(12, 8, 12, 2);
        double gw = System.Math.Min(w, big ? MaxW : w) - pad.Left - pad.Right;
        int cols = n4 ? 4 : 8;
        double cardContent = (gw - gl - gr) / cols - (cmp ? 2 : 6) - 2 - (cmp ? 2 : 4);
        return new GasLayout(big, cmp, nar, mir, n4, cardContent <= 70, gl, gr, co, rh, big ? 30 : cmp ? 10 : 26, cmp ? 3 : 8, pad);
    }
}
