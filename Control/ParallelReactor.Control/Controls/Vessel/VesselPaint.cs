using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Vessel;

/// <summary>
/// 釜体线描的几何与画笔（样稿 ui.reactorSVG 的各个 SVG 元素，viewBox 150 × 230 单位）。静态部分只解析一次（静态字段，第一次用到釜体时）；
/// 随液面变的液体 / 液面线按液面 y 缓存（液面只有加料、取样时才变）。画笔按（颜色, 线宽, 虚线, 线帽, 拐角）缓存，颜色只取色板。
/// 「不随缩放的线宽」（样稿 vector-effect: non-scaling-stroke）的做法：整幅图在 viewBox → 控件的等比变换下画，画笔线宽写成 px ÷ k
/// （等比缩放下与先变换几何、再按 px 描边完全相同；虚线的长度在 Avalonia 里是线宽的倍数，所以 px 的虚线照样是 px）。
/// </summary>
internal static class VesselPaint
{
    private static Geometry P(string d) => Geometry.Parse(d);

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>SVG &lt;rect rx&gt; 的等价路径（SVG 2：从 (x + rx, y) 起顺时针 —— 虚线的起点与样稿一致）。</summary>
    public static string RoundRect(double x, double y, double w, double h, double r) =>
        $"M{F(x + r)} {F(y)}H{F(x + w - r)}A{F(r)} {F(r)} 0 0 1 {F(x + w)} {F(y + r)}V{F(y + h - r)}A{F(r)} {F(r)} 0 0 1 {F(x + w - r)} {F(y + h)}" +
        $"H{F(x + r)}A{F(r)} {F(r)} 0 0 1 {F(x)} {F(y + h - r)}V{F(y + r)}A{F(r)} {F(r)} 0 0 1 {F(x + r)} {F(y)}Z";

    /// <summary>SVG &lt;circle&gt; 的等价路径（从 (cx + r, cy) 起顺时针）。</summary>
    public static string Circle(double cx, double cy, double r) =>
        $"M{F(cx + r)} {F(cy)}A{F(r)} {F(r)} 0 0 1 {F(cx - r)} {F(cy)}A{F(r)} {F(r)} 0 0 1 {F(cx + r)} {F(cy)}Z";

    // ---------------- 静态几何（样稿 reactorSVG 的元素，顺序同样稿） ----------------

    /// <summary>进气：竖管 + 漏斗三角 + 两道法兰（细节）。</summary>
    public static readonly Geometry Inlet = P("M74 8v38M63 8h22l-11 16ZM68 39h12M67 34h14");
    /// <summary>电机盒（细节，填 bg-side）。</summary>
    public static readonly Geometry Motor = P(RoundRect(87, 25, 20, 23, 1));
    /// <summary>电机盒里的三道线、电机底座、釜盖上的两颗螺栓（细节）。</summary>
    public static readonly Geometry MotorLines = P("M91 30h12M91 35h12M91 40h12M82 48h30M42 43v8M116 43v8");
    /// <summary>底部出料口（细节）。</summary>
    public static readonly Geometry Outlet = P("M75 207v12M68 207h14v12H68ZM75 219v7");
    /// <summary>测温探头「C⊏」与压力表的引管（细节）。</summary>
    public static readonly Geometry Probe = P("M29 139H17v12h12M17 135v20M10 141a5 5 0 1 0 0 6M111 83h9v-7h7");
    /// <summary>压力表（细节）。</summary>
    public static readonly Geometry Gauge = P(Circle(132, 76, 5));
    /// <summary>夹套 = 加热器。</summary>
    public static readonly Geometry Jacket = P("M33 65v111c0 22 15 37 42 37s42-15 42-37V65");
    /// <summary>釜盖（轮廓，填 bg-sunken；未安装时只画虚线）。</summary>
    public static readonly Geometry Lid = P(RoundRect(29, 49, 100, 10, 1));
    /// <summary>釜身（轮廓，线宽 1.5）。</summary>
    public static readonly Geometry Body = P("M39 60v118c0 17 11 29 36 29s36-12 36-29V60");
    /// <summary>搅拌轴：完整线描从釜盖下沿（60）起，简化釜体从釜盖上沿（49）起。</summary>
    public static readonly Geometry Shaft = P("M75 60v116");
    public static readonly Geometry ShaftSimple = P("M75 49v116");
    /// <summary>降温的雪花（两个 path 合成一个：轮廓色，1.5，圆线帽）。</summary>
    public static readonly Geometry Snow = P("M16 186v16M9.1 190l13.8 8M9.1 198l13.8-8M12 182l4 3 4-3M12 206l4-3 4 3");
    /// <summary>超压红圈（只在大号釜体的压力表上）。</summary>
    public static readonly Geometry PtRing = P(Circle(132, 76, 9));

    /// <summary>搅拌桨（rx 4 的圆角矩形；动画时只改宽度，用 DrawRectangle 画）。</summary>
    public static readonly Rect Paddle = new(58, 176, 34, 9);
    public const double PaddleRadius = 4;

    /// <summary>三个气泡：(cx, 液面 y 之下的偏移, r)。</summary>
    public static readonly (double Cx, double Dy, double R)[] Bubbles = { (55, 30, 2.4), (93, 24, 2), (68, 40, 1.7) };

    /// <summary>火焰：图标 flame（24 网格）放在 viewBox (11, 88)、18 × 18；描边 1.6 px（样稿的图标 symbol 一律 non-scaling-stroke）。</summary>
    public const double FlameX = 11, FlameY = 88, FlameSize = 18;

    // ---------------- 随液面变的几何 ----------------

    private static readonly Dictionary<double, (Geometry Liquid, Geometry Rim)> ByLevel = new();

    /// <summary>液体（样稿 liqPath）与液面线（rimPath），按液面 y 缓存（超过 64 个整个清掉）。</summary>
    public static (Geometry Liquid, Geometry Rim) Level(double y)
    {
        if (ByLevel.TryGetValue(y, out var g)) return g;
        if (ByLevel.Count >= 64) ByLevel.Clear();
        var ys = F(y);
        var y9 = F(y + 9);
        g = (P($"M40 {ys}Q75 {y9} 110 {ys}V178c0 15-10 25-35 25s-35-10-35-25Z"), P($"M40 {ys}Q75 {y9} 110 {ys}"));
        ByLevel[y] = g;
        return g;
    }

    // ---------------- 画笔与画刷 ----------------

    private static readonly ConcurrentDictionary<(uint Color, double Width, double Thickness, string Dash, PenLineCap Cap, PenLineJoin Join), ImmutablePen> Pens = new();
    private static readonly ConcurrentDictionary<uint, IImmutableBrush> BrushCache = new();

    /// <summary>令牌色的画刷（颜色只来自 Palette）。</summary>
    public static IImmutableBrush Brush(Color c) => BrushCache.GetOrAdd(c.ToUInt32(), _ => new ImmutableSolidColorBrush(c));

    /// <summary>
    /// 画笔。<paramref name="width"/> 与 <paramref name="dash"/>（CSS 写法「4 4」）是同一种单位（样稿里写的数：non-scaling 时是 px，否则是 viewBox 单位）；
    /// 实际线宽 = width × <paramref name="unit"/>（当前坐标系的单位：在 viewBox 变换下画 non-scaling 的线时 unit = 1 / k）。
    /// 虚线换成 Avalonia 的「线宽的倍数」（dash ÷ width，与 unit 无关）。拐角默认 miter（SVG 默认，miter-limit 4），线帽默认平头（SVG butt）。
    /// 线宽随 k 变（窗口缩放时）—— 超过 256 支整个清掉。
    /// </summary>
    public static ImmutablePen Pen(Color color, double width, string? dash = null, PenLineCap cap = PenLineCap.Flat,
        PenLineJoin join = PenLineJoin.Miter, double unit = 1)
    {
        double thickness = width * unit;
        var key = (color.ToUInt32(), Math.Round(width, 6), Math.Round(thickness, 6), dash ?? "", cap, join);
        if (Pens.TryGetValue(key, out var pen)) return pen;
        if (Pens.Count >= 256) Pens.Clear();
        ImmutableDashStyle? ds = null;
        if (!string.IsNullOrEmpty(dash) && width > 0)
            ds = new ImmutableDashStyle(dash.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, CultureInfo.InvariantCulture) / width), 0);
        pen = new ImmutablePen(Brush(color), thickness, ds, cap, join, 4);
        Pens[key] = pen;
        return pen;
    }
}
