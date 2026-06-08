using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Controls;

/// <summary>
/// 多通道实时曲线绘制控件。从 GraphViewModel 读取历史数据，
/// 按当前变量(温度/压力/气体)与时间范围绘制网格、坐标轴与多条曲线。
/// </summary>
public class GraphControl : Control
{
    public GraphViewModel? Vm { get; set; }

    private static readonly Typeface Face = new("Inter, Noto Sans SC");
    private static readonly Color Mute = Color.Parse("#66666f");
    private static readonly Color Body = Color.Parse("#a4a4b0");

    public override void Render(DrawingContext ctx)
    {
        var vm = Vm;
        var b = Bounds;
        if (vm == null || b.Width < 12 || b.Height < 12) return;

        double W = b.Width, H = b.Height, pl = 52, pr = 16, pt = 16, pb = 30;

        // 背景
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(110, 12, 12, 15)),
            new Rect(0, 0, W, H), 10);

        var on = vm.Channels.Where(c => c.IsOn && !c.Reactor.IsIdle).ToList();
        double tMax = double.MinValue, gMin = double.MaxValue;
        foreach (var ch in on)
        {
            var h = vm.History[ch.Reactor.Id];
            if (h.Count == 0) continue;
            tMax = Math.Max(tMax, h[^1].Ts);
            gMin = Math.Min(gMin, h[0].Ts);
        }
        if (on.Count == 0 || tMax == double.MinValue)
        {
            DrawText(ctx, "暂无数据 · 请在右侧选择要显示的通道", W / 2, H / 2 - 8, Mute, 13, TextAlignment.Center);
            return;
        }

        double rangeSec = vm.RangeSeconds;
        double tMin = rangeSec >= double.MaxValue ? gMin : Math.Max(gMin, tMax - rangeSec);

        // 收集（按时间范围过滤后的）各通道序列
        var series = new List<(Color col, List<(double t, double v)> pts)>();
        foreach (var ch in on)
        {
            var h = vm.History[ch.Reactor.Id];
            var pts = new List<(double, double)>();
            foreach (var s in h)
                if (s.Ts >= tMin) pts.Add((s.Ts, vm.Val(s)));
            if (pts.Count >= 2) series.Add((Color.Parse(ch.ColorHex), pts));
        }
        if (series.Count == 0)
        {
            DrawText(ctx, "暂无数据 · 请在右侧选择要显示的通道", W / 2, H / 2 - 8, Mute, 13, TextAlignment.Center);
            return;
        }

        // y 范围（自适应 + 留白）
        double yMin = double.MaxValue, yMax = double.MinValue;
        foreach (var s in series)
            foreach (var p in s.pts) { yMin = Math.Min(yMin, p.v); yMax = Math.Max(yMax, p.v); }
        if (yMax - yMin < 1) yMax = yMin + 1;
        double pad = (yMax - yMin) * 0.12;
        double yLo = yMin - pad, yHi = yMax + pad;

        double span = tMax - tMin <= 0 ? 1 : tMax - tMin;
        double X(double t) => pl + (W - pl - pr) * (t - tMin) / span;
        double Y(double v) => H - pb - (H - pt - pb) * (v - yLo) / (yHi - yLo <= 0 ? 1 : yHi - yLo);

        // —— y 网格 + 刻度 ——
        var gpenY = new Pen(new SolidColorBrush(Color.FromArgb(14, 255, 255, 255)), 1);
        string fmt = "0." + new string('0', vm.Decimals);
        for (int i = 0; i <= 5; i++)
        {
            double y = pt + (H - pt - pb) * i / 5;
            double v = yHi - (yHi - yLo) * i / 5;
            ctx.DrawLine(gpenY, new Point(pl, y), new Point(W - pr, y));
            DrawText(ctx, v.ToString(fmt, CultureInfo.InvariantCulture), pl - 8, y - 7, Mute, 9.5, TextAlignment.Right);
        }

        // —— x 网格 + 刻度（相对“现在”的时间）——
        var gpenX = new Pen(new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)), 1);
        for (int i = 0; i <= 6; i++)
        {
            double x = pl + (W - pl - pr) * i / 6;
            double tv = tMin + span * i / 6;
            ctx.DrawLine(gpenX, new Point(x, pt), new Point(x, H - pb));
            double ago = tMax - tv;
            string lab = ago < 1 ? "现在" : $"-{(int)(ago / 60)}:{(int)(ago % 60):00}";
            DrawText(ctx, lab, x, H - pb + 6, Mute, 9.5, TextAlignment.Center);
        }

        // y 轴标题
        DrawText(ctx, $"{vm.YTitle} ({vm.YUnit})", pl - 44, pt - 14, Body, 9.5, TextAlignment.Left);

        // —— 曲线（先画半透明描边作辉光，再画实线）——
        foreach (var s in series)
        {
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                gc.BeginFigure(new Point(X(s.pts[0].t), Y(s.pts[0].v)), false);
                for (int i = 1; i < s.pts.Count; i++)
                    gc.LineTo(new Point(X(s.pts[i].t), Y(s.pts[i].v)));
                gc.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(s.col, 0.22), 4)
            { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, geo);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(s.col), 1.8)
            { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, geo);

            var last = s.pts[^1];
            ctx.DrawEllipse(new SolidColorBrush(s.col), null, new Point(X(last.t), Y(last.v)), 2.8, 2.8);
        }
    }

    private static void DrawText(DrawingContext ctx, string text, double x, double y, Color color,
        double size, TextAlignment align)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Face, size, new SolidColorBrush(color));
        double dx = align switch
        {
            TextAlignment.Center => -ft.Width / 2,
            TextAlignment.Right => -ft.Width,
            _ => 0,
        };
        ctx.DrawText(ft, new Point(x + dx, y));
    }
}
