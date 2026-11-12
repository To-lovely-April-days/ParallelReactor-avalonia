using System;
using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Chart;

/// <summary>
/// 曲线的颜色、画刷、画笔：只按色板令牌名取（Core 的 ChartOptions 写的是「--ch-t」这样的令牌名，同样稿 PR.tok），全部缓存（R10：绘制时不分配）。
/// 虚线写样稿 SVG 的 px（「6 4」），这里换成 Avalonia DashStyle 的「线宽的倍数」再交给 <see cref="Palette.Pen"/>（同一套画笔缓存）。
/// </summary>
public static class ChartPaint
{
    private static readonly ConcurrentDictionary<string, Color> ColorCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(Color Color, double Alpha), IImmutableSolidColorBrush> BrushCache = new();
    private static readonly ConcurrentDictionary<(string Dash, double Width), string> DashCache = new();

    /// <summary>令牌名（「--ch-t」或「ch-t」）→ 颜色；不认识的令牌按 --t-3（不会出现：Core 只写色板里的令牌）。</summary>
    public static Color Of(string? token)
    {
        if (string.IsNullOrEmpty(token)) return Palette.T3Color;
        return ColorCache.GetOrAdd(token, t => Palette.Find(t)?.Color ?? Palette.T3Color);
    }

    /// <summary>令牌颜色的画刷（可带透明度：只改 alpha，RGB 仍是令牌，同 PR.tokA）。</summary>
    public static IImmutableSolidColorBrush Brush(string? token, double alpha = 1) => Brush(Of(token), alpha);

    public static IImmutableSolidColorBrush Brush(Color c, double alpha = 1) =>
        BrushCache.GetOrAdd((c, alpha), k => new ImmutableSolidColorBrush(k.Alpha >= 1 ? k.Color : Palette.WithAlpha(k.Color, k.Alpha)));

    /// <summary>
    /// 画笔：令牌色、线宽（px）、虚线（SVG 的 px 写法，null = 实线）、线帽。线的连接一律圆角（Palette.Pen；样稿 .pr-chart-line stroke-linejoin: round）。
    /// </summary>
    public static IPen Pen(string? token, double width, string? dashPx = null, PenLineCap cap = PenLineCap.Flat) =>
        Palette.Pen(Of(token), width, DashInWidths(dashPx, width), cap);

    /// <summary>「6 4」（px）在线宽 1.5 下 →「4 2.6667」（线宽的倍数，Avalonia DashStyle 的语义）；null / 空 → null。</summary>
    public static string? DashInWidths(string? dashPx, double width)
    {
        if (string.IsNullOrWhiteSpace(dashPx)) return null;
        return DashCache.GetOrAdd((dashPx, width), k =>
        {
            var a = Core.Chart.ChartStyle.DashInWidths(k.Dash, k.Width) ?? Array.Empty<double>();
            var parts = new string[a.Length];
            for (int i = 0; i < a.Length; i++) parts[i] = a[i].ToString("0.####", CultureInfo.InvariantCulture);
            return string.Join(" ", parts);
        });
    }
}
