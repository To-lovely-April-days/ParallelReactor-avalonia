#if DEBUG
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 图标核对表（PR_CONTROL_DEMO=1 + PR_CONTROL_ICON_SHEET=1）：全部统一 Phosphor Icons 按 8 列排开，每格 150×96，
/// 同一图标画 14 / 18 / 28 px，格子下方写名字。
/// </summary>
public static class IconSheet
{
    public const int Cols = 8, CellW = 150, CellH = 96;
    public static readonly double[] Sizes = { 14, 18, 28 };
    public static readonly double[] Xs = { 12, 40, 74 };

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
        // 彩色彩样：急停、录制、成功、警告、报警、品牌大图标
        Add(c, new IconView { Icon = "estop", Size = 20, Foreground = Palette.RBrush }, 12, y0);
        Add(c, new IconView { Icon = "rec", Size = 20, Foreground = Palette.RBrush }, 44, y0);
        Add(c, new IconView { Icon = "ok-f", Size = 20, Foreground = Palette.GDotBrush }, 76, y0);
        Add(c, new IconView { Icon = "warn-f", Size = 20, Foreground = Palette.AmbBrush }, 108, y0);
        Add(c, new IconView { Icon = "err-f", Size = 20, Foreground = Palette.RBtnBrush }, 140, y0);
        Add(c, new IconView { Icon = "bell-lg", Size = 26, Foreground = Palette.GbgIconBrush }, 176, y0 - 3);
        Add(c, new IconView { Icon = "pc-lg", Size = 26, Foreground = Palette.GbgIconBrush }, 216, y0 - 3);
        Add(c, new IconView { Icon = "logo", Size = 26, Foreground = Palette.ADBrush }, 256, y0 - 3);
        c.Height = y0 + 60;
        return c;
    }

    private static void Add(Canvas c, Avalonia.Controls.Control child, double x, double y)
    {
        Canvas.SetLeft(child, x); Canvas.SetTop(child, y);
        c.Children.Add(child);
    }
}
#endif
