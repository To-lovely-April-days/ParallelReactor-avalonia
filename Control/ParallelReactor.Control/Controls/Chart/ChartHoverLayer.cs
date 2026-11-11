using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Chart;

/// <summary>
/// 折线图的悬停层（样稿 lineChart 的 .pr-chart-cross + .pr-chart-tip；<see cref="TimeSeriesChart"/> 的视觉子元素，铺满曲线、不接指针）：
/// <list type="bullet">
/// <item>十字线：竖虚线（--t-4、1 px、4 3），从绘图区顶到底，对齐到点数最多的那条曲线上最近的采样点的时刻；</item>
/// <item>每条覆盖这个时刻的曲线一个圆点（半径 4、--bg-card 填充、2 px 系列色描边）；</item>
/// <item>提示框（.pr-chart-tip：--toast 底、圆角 6、shadow-pop、内边距 10 / 14、至少 180 宽）：时间（13 px、白 72 %）+ 每条线一行（行高 25.2：10 × 3 色块 · 名称 14 白 86 % ·
///   右对齐读数 14/600 白 + 单位 13 白 66 %）；右边放不下时放到十字线左边，纵向跟着指针（上方 30），夹在绘图区与图高之内。</item>
/// </list>
/// 同一个采样点（指针在两个采样之间移动）只挪提示的纵向位置，不重新排字；换了采样点才按 Core 的 <see cref="ChartLayout.Hover"/> 重新取读数、排字（R10）。
/// </summary>
public sealed class ChartHoverLayer : Avalonia.Controls.Control, IAuditText
{
    /// <summary>提示框的尺寸（样稿 base.css .pr-chart-tip）。</summary>
    public const double TipPadX = 14, TipPadY = 10, TipMinWidth = 180, HeadLine = 13 * 1.45, HeadGap = 6, RowLine = 14 * 1.8, Swatch = 10, Gap = 8, ValuePad = 16;

    private static readonly FontFeatureCollection Tnum = new() { FontFeature.Parse("+tnum") };

    private readonly TimeSeriesChart _owner;
    private ChartHover? _hover;
    private int _version = -1;
    private double _tt = double.NaN;
    private double _pointerY;
    private Rect _tip;

    private TextLayout? _head;
    private readonly List<Row> _rows = new();

    private readonly record struct Row(string Color, double DotX, double DotY, TextLayout Name, TextLayout Value, TextLayout? Unit);

    public ChartHoverLayer(TimeSeriesChart owner)
    {
        _owner = owner;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>当前的悬停读数（没有为 null）。</summary>
    public ChartHover? Current => _hover;

    /// <summary>提示框的矩形（控件坐标；没有悬停时为空）。</summary>
    public Rect TipRect => _hover is null ? default : _tip;

    /// <summary>指针在 <paramref name="p"/>（曲线的坐标）：在绘图区里、且有曲线覆盖这个时刻时显示，否则收起。</summary>
    internal void Update(ChartGeometry? g, Point p, int version)
    {
        if (g is null || !g.InPlot(p.X, p.Y) || g.Series.Count == 0) { Hide(); return; }
        // 先只找对齐的时刻（不分配）：同一个采样点、同一版数据时只挪提示
        double t = g.TimeAt(p.X), gap = g.TipGapMs;
        int refS = -1;
        for (int i = 0; i < g.Series.Count; i++)
        {
            var pts = g.Series[i].Points;
            if (pts.Count == 0 || t < pts[0].T - gap || t > pts[pts.Count - 1].T + gap) continue;
            if (refS < 0 || pts.Count > g.Series[refS].Points.Count) refS = i;
        }
        if (refS < 0) { Hide(); return; }
        int ti = ChartLayout.NearestIndex(g.Series[refS].Points, t);
        double tt = ti >= 0 ? g.Series[refS].Points[ti].T : t;
        _pointerY = p.Y;
        if (_hover != null && version == _version && tt.Equals(_tt))
        {
            Position(g);
            InvalidateVisual();
            return;
        }
        var h = ChartLayout.Hover(g, p.X, p.Y);
        if (h is null) { Hide(); return; }
        _hover = h;
        _version = version;
        _tt = tt;
        BuildTexts(h);
        Position(g);
        InvalidateVisual();
    }

    internal void Hide()
    {
        if (_hover is null) return;
        _hover = null;
        _tt = double.NaN;
        DisposeTexts();
        InvalidateVisual();
    }

    private void BuildTexts(ChartHover h)
    {
        DisposeTexts();
        var family = TextElement.GetFontFamily(_owner);
        var regular = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
        var bold = new Typeface(family, FontStyle.Normal, FontWeight.SemiBold);
        _head = new TextLayout(h.TimeText, regular, Tnum, ChartStyle.TickFont, ChartPaint.Brush(Palette.TInvColor, 0.72));
        foreach (var r in h.Rows)
        {
            bool unit = !string.IsNullOrEmpty(r.Unit);
            var name = new TextLayout(r.Name, regular, ChartStyle.LabelFont, ChartPaint.Brush(Palette.TInvColor, 0.86));
            // 读数与单位在同一个 <b> 里：「120.0 <small>℃</small>」（空格是 14/600）
            var value = new TextLayout(unit ? r.Value + " " : r.Value, bold, Tnum, ChartStyle.LabelFont, ChartPaint.Brush(Palette.TInvColor));
            var u = unit ? new TextLayout(r.Unit!, regular, ChartStyle.TickFont, ChartPaint.Brush(Palette.TInvColor, 0.66)) : null;
            _rows.Add(new Row(r.Color, ChartScene.F1(r.DotX), ChartScene.F1(r.DotY), name, value, u));
        }
    }

    /// <summary>提示框的宽高与位置（样稿 onMove：右边放不下就放到十字线左边；纵向 = 指针上方 30，夹在 [padT, 图高 − 框高 − 4]）。</summary>
    private void Position(ChartGeometry g)
    {
        if (_hover is null || _head is null) return;
        double content = _head.WidthIncludingTrailingWhitespace;
        foreach (var r in _rows)
            content = Math.Max(content, Swatch + Gap + r.Name.WidthIncludingTrailingWhitespace + Gap + ValuePad
                + r.Value.WidthIncludingTrailingWhitespace + (r.Unit?.WidthIncludingTrailingWhitespace ?? 0));
        double w = Math.Max(TipMinWidth, Math.Ceiling(content + 2 * TipPadX));
        double h = 2 * TipPadY + HeadLine + HeadGap + _rows.Count * RowLine;
        double left = ChartLayout.TipLeft(g, _hover.CrossX, w);
        double top = ChartLayout.TipTop(g, _pointerY, h);
        _tip = new Rect(left, top, w, h);
    }

    private void DisposeTexts()
    {
        _head?.Dispose();
        _head = null;
        foreach (var r in _rows)
        {
            r.Name.Dispose();
            r.Value.Dispose();
            r.Unit?.Dispose();
        }
        _rows.Clear();
    }

    private static double Baseline(TextLayout tl) => tl.TextLines.Count > 0 ? tl.TextLines[0].Baseline : tl.Height * 0.8;

    public override void Render(DrawingContext ctx)
    {
        var h = _hover;
        var g = _owner.Geometry;
        if (h is null || g is null || _head is null) return;
        // 十字线 + 圆点
        double cx = ChartScene.F1(h.CrossX);
        ctx.DrawLine(ChartPaint.Pen(ChartStyle.CrossInk, ChartStyle.CrossWidth, ChartStyle.CrossDash), new Point(cx, g.PadT), new Point(cx, g.PadT + g.Ph));
        foreach (var r in _rows)
            ctx.DrawEllipse(Palette.BgCardBrush, ChartPaint.Pen(r.Color, ChartStyle.HoverDotStroke), new Point(r.DotX, r.DotY), ChartStyle.HoverDotR, ChartStyle.HoverDotR);
        // 提示框
        var tip = _tip;
        ctx.DrawRectangle(Palette.ToastBrush, null, new RoundedRect(tip, 6), Palette.ShadowPop);
        double x0 = tip.X + TipPadX, right = tip.Right - TipPadX, y = tip.Y + TipPadY;
        _head.Draw(ctx, new Point(x0, y + (HeadLine - _head.Height) / 2));
        y += HeadLine + HeadGap;
        foreach (var r in _rows)
        {
            double cy = y + RowLine / 2;
            ctx.DrawRectangle(ChartPaint.Brush(r.Color), null, new RoundedRect(new Rect(x0, cy - 1.5, Swatch, 3), 2));
            r.Name.Draw(ctx, new Point(x0 + Swatch + Gap, cy - r.Name.Height / 2));
            // 读数右对齐；单位与读数同一条基线
            double vb = cy - r.Value.Height / 2 + Baseline(r.Value);
            double uw = r.Unit?.WidthIncludingTrailingWhitespace ?? 0;
            double vx = right - uw - r.Value.WidthIncludingTrailingWhitespace;
            r.Value.Draw(ctx, new Point(vx, vb - Baseline(r.Value)));
            r.Unit?.Draw(ctx, new Point(right - uw, vb - Baseline(r.Unit)));
            y += RowLine;
        }
    }

    /// <summary>截图审计：显示着的提示框里的文字（时间、名称、读数、单位）。</summary>
    public IEnumerable<AuditTextRun> AuditTexts
    {
        get
        {
            if (_hover is null || _head is null) yield break;
            var tip = _tip;
            yield return new AuditTextRun(_hover.TimeText, ChartStyle.TickFont,
                new Rect(tip.X + TipPadX, tip.Y + TipPadY, _head.WidthIncludingTrailingWhitespace, HeadLine), Palette.TInvColor);
            double y = tip.Y + TipPadY + HeadLine + HeadGap;
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                var row = _hover.Rows[i];
                yield return new AuditTextRun(row.Name, ChartStyle.LabelFont, new Rect(tip.X + TipPadX + Swatch + Gap, y, r.Name.WidthIncludingTrailingWhitespace, RowLine), Palette.TInvColor);
                double uw = r.Unit?.WidthIncludingTrailingWhitespace ?? 0;
                yield return new AuditTextRun(row.Value, ChartStyle.LabelFont, new Rect(tip.Right - TipPadX - uw - r.Value.WidthIncludingTrailingWhitespace, y, r.Value.WidthIncludingTrailingWhitespace, RowLine), Palette.TInvColor);
                if (r.Unit != null) yield return new AuditTextRun(row.Unit!, ChartStyle.TickFont, new Rect(tip.Right - TipPadX - uw, y, uw, RowLine), Palette.TInvColor);
                y += RowLine;
            }
        }
    }
}
