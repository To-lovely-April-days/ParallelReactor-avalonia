#if DEBUG
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 图标核对表（PR_CONTROL_DEMO=1 + PR_CONTROL_ICON_SHEET=1）：76 个 pc-v3 图标按 8 列排开，每格 150×96，
/// 同一图标画 18 / 24 / 32 px（描边都应是 1.6 px），格子下方写名字。样稿用 snap 注入同样排布的 SVG 拍参照，左右对照检查 gen-icons.py 的换算。
/// 下方另有一行旧用法（Geometry + viewBox 单位描边）的 9 个旧键（旧视图还在用的），以及填充用法（停止方块、录制点）。
/// </summary>
public static class IconSheet
{
    public const int Cols = 8, CellW = 150, CellH = 96;
    public static readonly double[] Sizes = { 18, 24, 32 };
    public static readonly double[] Xs = { 12, 42, 78 };

    public static Avalonia.Controls.Control Build()
    {
        var c = new Canvas { Width = Cols * CellW, Background = Palette.BgCardBrush, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        int i = 0;
        foreach (var name in IconSet.Names)
        {
            double x = i % Cols * CellW, y = i / Cols * CellH;
            for (int k = 0; k < Sizes.Length; k++)
                Add(c, new IconView { Icon = name, Size = Sizes[k], Foreground = Palette.T2Brush }, x + Xs[k], y + 12);
            Add(c, new TextBlock { Text = name, FontSize = 13, Foreground = Palette.T3Brush }, x + 12, y + 62);
            i++;
        }
        double y0 = (i + Cols - 1) / Cols * CellH + 16;
        // 填充用法：停止类按钮的深灰方块（Fill + 1 px 描边，15 px）、录制点、logo（22 px，描边 1.8）
        Add(c, new IconView { Icon = "stop", Size = 15, Fill = true, StrokeWidth = 1, Foreground = Palette.T2Brush }, 12, y0);
        Add(c, new IconView { Icon = "rec", Size = 24, Foreground = Palette.RBrush }, 42, y0);
        Add(c, new IconView { Icon = "logo", Size = 22, StrokeWidth = 1.8, Foreground = Palette.ADBrush }, 78, y0);
        // 描边宽度核对：minus 在 18 / 36 / 72 px 下都应是 1.6 px 粗
        Add(c, new IconView { Icon = "minus", Size = 18, Foreground = Palette.T1Brush }, 150, y0);
        Add(c, new IconView { Icon = "minus", Size = 36, Foreground = Palette.T1Brush }, 180, y0);
        Add(c, new IconView { Icon = "minus", Size = 72, Foreground = Palette.T1Brush }, 230, y0);
        // 旧用法（M7 删除）：Geometry + Size + viewBox 单位描边
        int j = 0;
        foreach (var key in new[] { "IcoGauge", "IcoClock", "IcoList", "IcoClose", "IcoExpand", "IcoMove", "IcoSearch", "IcoZoomOut", "IcoTree" })
        {
            var iv = new IconView { Size = 16, Foreground = Palette.T3Brush };
            if (Avalonia.Application.Current!.TryFindResource(key, out var g) && g is Geometry geo) iv.Geometry = geo;
            Add(c, iv, 340 + j++ * 28, y0 + 4);
        }
        c.Height = y0 + 90;
        return c;
    }

    private static void Add(Canvas c, Avalonia.Controls.Control child, double x, double y)
    {
        Canvas.SetLeft(child, x); Canvas.SetTop(child, y);
        c.Children.Add(child);
    }
}
#endif
