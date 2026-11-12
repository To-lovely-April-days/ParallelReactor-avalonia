#if DEBUG
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 左右对照图 cmp-&lt;name&gt;-&lt;w&gt;.png（PORT-PLAN M1）：左 = 样稿参照 PNG（高度不同时等比缩放到与本机截图同高），右 = 本机截图；
/// 顶部一条 32 px 的标签条，中间 8 px 间隔。用 RenderTargetBitmap 拼，1920×5100 也可以。
/// Skia 与 WebKit 的字形度量不同：对照图只给人看，不做像素门槛（§5.9）。
/// </summary>
public static class ShotCompare
{
    private const int Strip = 32, Gap = 8;

    public static PixelSize Write(string refPath, string appPath, string outPath, string leftLabel, string rightLabel)
    {
        using var left = new Bitmap(refPath);
        using var right = new Bitmap(appPath);
        int h = right.PixelSize.Height;
        double k = left.PixelSize.Height == h ? 1 : (double)h / left.PixelSize.Height;
        int lw = (int)Math.Round(left.PixelSize.Width * k), rw = right.PixelSize.Width;
        var size = new PixelSize(lw + Gap + rw, Strip + h);

        using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var dc = rtb.CreateDrawingContext())
        {
            dc.FillRectangle(Palette.LineStrongBrush, new Rect(0, 0, size.Width, size.Height));
            dc.FillRectangle(Palette.BgHeadBrush, new Rect(0, 0, size.Width, Strip));
            dc.DrawLine(Palette.Pen(Palette.Line2Color), new Point(0, Strip - 0.5), new Point(size.Width, Strip - 0.5));
            dc.DrawImage(left, new Rect(0, 0, left.PixelSize.Width, left.PixelSize.Height), new Rect(0, Strip, lw, h));
            dc.DrawImage(right, new Rect(0, 0, rw, h), new Rect(lw + Gap, Strip, rw, h));
            var face = new Typeface(UiFonts.Create());
            Label(dc, leftLabel + (k == 1 ? "" : $"（缩放 {k.ToString("0.###", CultureInfo.InvariantCulture)}）"), face, 12);
            Label(dc, rightLabel, face, lw + Gap + 12);
        }
        rtb.Save(outPath);
        return size;
    }

    private static void Label(DrawingContext dc, string text, Typeface face, double x)
    {
        var ft = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, face, 14, Palette.T2Brush);
        dc.DrawText(ft, new Point(x, (Strip - ft.Height) / 2));
    }
}
#endif
