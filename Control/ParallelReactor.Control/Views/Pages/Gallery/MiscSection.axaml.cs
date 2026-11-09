using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace ParallelReactor.Control.Views.Pages.Gallery;

public partial class MiscSection : UserControl
{
    /// <summary>状态列在宽度充裕时的宽度（样稿 1920 的自动表格布局量出来的 134）。</summary>
    private const double StatusColumnWide = 134;

    public MiscSection()
    {
        InitializeComponent();
        TableHost.SizeChanged += (_, e) => FitStatusColumn(e.NewSize.Width);
    }

    /// <summary>
    /// 模拟样稿的自动表格布局：表格宽度放得下「别名 + 型号全文 + 134 的状态列」时状态列保持 134（与 1920 参照图一致）；
    /// 放不下时状态列只占内容宽度，多出来的空间先给型号列，型号仍放不下才省略（M1 复核：1600 / 1366 时型号被截成「HT-PR…」，旁边却有空白）。
    /// 型号的自然宽度用 TextLayout 量（不在布局回调里调 Measure）。只改列的 MinWidth，表格自身的宽度不变，不会来回振荡。
    /// </summary>
    private void FitStatusColumn(double width)
    {
        var grids = TableHost.GetLogicalDescendants().OfType<Grid>().Where(g => g.ColumnDefinitions.Count == 3).ToList();
        if (grids.Count == 0 || width <= 0) return;
        double alias = 0, model = 0;
        foreach (var g in grids)
            foreach (var c in g.Children.OfType<Avalonia.Controls.Control>())
            {
                var col = Grid.GetColumn(c);
                if (col == 0) alias = Math.Max(alias, Math.Max(g.ColumnDefinitions[0].MinWidth, c.DesiredSize.Width));
                else if (col == 1 && c is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                {
                    using var layout = new TextLayout(tb.Text, new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontFeatures, tb.FontSize, null);
                    model = Math.Max(model, layout.WidthIncludingTrailingWhitespace + tb.Margin.Left + tb.Margin.Right);
                }
            }
        var min = width >= alias + model + StatusColumnWide ? StatusColumnWide : 0;
        foreach (var g in grids)
            if (g.ColumnDefinitions[2].MinWidth != min) g.ColumnDefinitions[2].MinWidth = min;
    }
}
