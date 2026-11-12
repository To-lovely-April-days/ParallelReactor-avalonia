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
    /// <summary>宽度充裕时的别名列 / 状态列宽度（样稿 1920 的自动表格布局量出来的 77 / 134）。</summary>
    private const double AliasColumnWide = 77, StatusColumnWide = 134;

    public MiscSection()
    {
        InitializeComponent();
        Grid3.SizeChanged += (_, e) => FitTable(CellWidth(e.NewSize.Width));
    }

    /// <summary>三列网格里一个格子的宽度（同 GalleryGrid：(宽 − 2 × 列间距) / 3）。</summary>
    private double CellWidth(double gridWidth) => Math.Max(0, (gridWidth - 2 * Grid3.ColumnGap) / 3);

    /// <summary>
    /// 模拟样稿的自动表格布局（M1 复核 + M2：1600 时型号曾被截成「HT-P…」）：
    ///  · 格子放得下「77 的别名列 + 型号全文 + 134 的状态列」时：77 / 剩余 / 134（与 1920 参照图一致）；
    ///  · 放不下时：三列都按内容宽（型号列 Auto + 共享宽度组，各行对齐），型号从不省略；三列合起来仍比格子宽时，
    ///    表格的最小宽度 = 内容宽度，伸进右边的列间距里（同样稿 1600：WebKit 的表格同样比格子宽）。
    /// 内容宽度用已经量好的 DesiredSize（别名、状态）和 TextLayout（型号的自然宽度）；只看格子的宽度（网格自己的宽度），表格变宽不会反过来影响判断。
    /// </summary>
    private void FitTable(double cell)
    {
        var grids = TableHost.GetLogicalDescendants().OfType<Grid>().Where(g => g.ColumnDefinitions.Count == 3).ToList();
        if (grids.Count == 0 || cell <= 0) return;
        double alias = 0, model = 0, status = 0;
        foreach (var g in grids)
            foreach (var c in g.Children.OfType<Avalonia.Controls.Control>())
            {
                var col = Grid.GetColumn(c);
                var margin = c.Margin.Left + c.Margin.Right;
                if (col == 0 && c is not TextBlock) alias = Math.Max(alias, c.DesiredSize.Width);
                else if (col == 1 && c is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                {
                    using var layout = new TextLayout(tb.Text, new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontFeatures, tb.FontSize, null);
                    model = Math.Max(model, Math.Ceiling(layout.OverhangLeading + layout.WidthIncludingTrailingWhitespace + layout.OverhangTrailing) + margin);
                }
                else if (col == 2 && c is not TextBlock) status = Math.Max(status, c.DesiredSize.Width);
            }
        bool wide = cell >= AliasColumnWide + model + StatusColumnWide;
        foreach (var g in grids)
        {
            var cd = g.ColumnDefinitions;
            cd[0].MinWidth = wide ? AliasColumnWide : 0;
            cd[2].MinWidth = wide ? StatusColumnWide : 0;
            var w = wide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            if (cd[1].Width != w) cd[1].Width = w;
            cd[1].SharedSizeGroup = wide ? null : "c1";
        }
        var need = alias + model + status;
        TableHost.MinWidth = !wide && need > cell ? need : 0;
    }
}
