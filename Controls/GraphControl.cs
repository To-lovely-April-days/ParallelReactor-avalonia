using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Controls;

/// <summary>
/// 多通道实时曲线绘制控件。从 GraphViewModel 读取历史数据，
/// 按当前变量(温度/压力/气体/全部)与时间范围绘制网格、坐标轴与多条曲线。
/// 「全部」视图下每通道温度/压力/气体各一条曲线（实线/虚线/点线区分），左侧三条独立 Y 轴。
/// 点击曲线任意位置：吸附最近采样点，画十字线并弹出该通道该时刻 温度/压力/气体 详情 tip。
/// </summary>
public class GraphControl : Control
{
    public GraphViewModel? Vm { get; set; }

    private static readonly Typeface Face = new("Inter, Noto Sans SC");
    private static readonly Color Mute = Color.Parse("#55555f");
    private static readonly Color Body = Color.Parse("#3a3a44");

    // 「全部」视图的三个变量：温度=实线 / 压力=虚线 / 气体=点线（曲线颜色仍是通道色，此处为轴与 tip 的变量配色）
    private static readonly string[] AllMetrics = { "T", "P", "gas" };
    private static readonly Color[] AllColors =
    {
        Color.Parse("#c97818"),   // 温度：琥珀
        Color.Parse("#2d5ca0"),   // 压力：蓝
        Color.Parse("#5fae14"),   // 气体：绿
    };

    // —— 点击选点：命中测试用上一帧渲染缓存的布局；选中后跨帧保留，数据刷新时 tip 原位重绘 ——
    private sealed class HitSeries
    {
        public int Id;
        public int Metric;
        public Color Col;
        public List<(GSample s, double v)> Pts = new();
    }
    private List<HitSeries>? _hit;
    private (int id, int m, GSample s)? _sel;
    private double _pl, _pr, _pt, _pb, _tMin, _tMax = 1, _span = 1, _W, _H;
    private readonly double[] _yLo = new double[3], _yHi = new double[3];
    private readonly bool[] _has = new bool[3];

    public override void Render(DrawingContext ctx)
    {
        var vm = Vm;
        var b = Bounds;
        if (vm == null || b.Width < 12 || b.Height < 12) return;

        double W = b.Width, H = b.Height;
        _W = W; _H = H;

        // 背景（浅色主题：极淡灰绘图区）
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(6, 0, 0, 0)),
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
            _hit = null; _sel = null;
            DrawText(ctx, "暂无数据 · 启用通道后开始记录实测曲线", W / 2, H / 2 - 8, Mute, 13, TextAlignment.Center);
            return;
        }

        double rangeSec = vm.RangeSeconds;
        _tMin = rangeSec >= double.MaxValue ? gMin : Math.Max(gMin, tMax - rangeSec);
        _tMax = tMax;
        _span = tMax - _tMin <= 0 ? 1 : tMax - _tMin;
        Array.Clear(_has, 0, _has.Length);

        if (vm.Metric == "all") RenderAll(ctx, vm, on);
        else RenderSingle(ctx, vm, on);

        DrawSelection(ctx, vm);
    }

    // ============ 单变量视图 ============

    /// <summary>单变量视图：所有启用通道叠加，共用左侧一条 Y 轴。</summary>
    private void RenderSingle(DrawingContext ctx, GraphViewModel vm, List<GraphChannel> on)
    {
        _pl = 52; _pr = 16; _pt = 16; _pb = 30;
        int mi = vm.Metric == "T" ? 0 : vm.Metric == "P" ? 1 : 2;
        double W = _W, H = _H;

        // 收集（按时间范围过滤后的）各通道序列。NaN = 该时刻读数无效（模块离线 / 未实测），保留以便断开曲线
        var series = new List<HitSeries>();
        foreach (var ch in on)
        {
            var h = vm.History[ch.Reactor.Id];
            var sr = new HitSeries { Id = ch.Reactor.Id, Metric = mi, Col = Color.Parse(ch.ColorHex) };
            foreach (var s in h)
                if (s.Ts >= _tMin) sr.Pts.Add((s, vm.Val(s)));
            if (sr.Pts.Count(p => !double.IsNaN(p.v)) >= 2) series.Add(sr);
        }
        _hit = series.Count > 0 ? series : null;
        if (_hit == null)
        {
            _sel = null;
            DrawText(ctx, "暂无有效读数 · 模块离线或该量尚未实测", W / 2, H / 2 - 8, Mute, 13, TextAlignment.Center);
            return;
        }

        // y 范围（自适应 + 留白）
        double yMin = double.MaxValue, yMax = double.MinValue;
        foreach (var s in series)
            foreach (var p in s.Pts)
            {
                if (double.IsNaN(p.v)) continue;
                yMin = Math.Min(yMin, p.v); yMax = Math.Max(yMax, p.v);
            }
        if (yMax - yMin < 1) yMax = yMin + 1;
        double pad = (yMax - yMin) * 0.12;
        _yLo[mi] = yMin - pad; _yHi[mi] = yMax + pad; _has[mi] = true;
        double yLo = _yLo[mi], yHi = _yHi[mi];

        double pl = _pl;
        double X(double t) => pl + (W - pl - _pr) * (t - _tMin) / _span;
        double Y(double v) => H - _pb - (H - _pt - _pb) * (v - yLo) / (yHi - yLo <= 0 ? 1 : yHi - yLo);

        // —— y 网格 + 刻度 ——
        var gpenY = new Pen(new SolidColorBrush(Color.FromArgb(16, 0, 0, 0)), 1);
        string fmt = "0." + new string('0', vm.Decimals);
        for (int i = 0; i <= 5; i++)
        {
            double y = _pt + (H - _pt - _pb) * i / 5;
            double v = yHi - (yHi - yLo) * i / 5;
            ctx.DrawLine(gpenY, new Point(pl, y), new Point(W - _pr, y));
            DrawText(ctx, v.ToString(fmt, CultureInfo.InvariantCulture), pl - 8, y - 7, Mute, 10.5, TextAlignment.Right);
        }

        DrawXGrid(ctx, vm, W, H);

        // y 轴标题
        DrawText(ctx, $"{vm.YTitle} ({vm.YUnit})", pl - 44, _pt - 14, Body, 10.5, TextAlignment.Left);

        // —— 曲线（先画半透明描边作辉光，再画实线）。遇到无效点断开，分成多段 ——
        foreach (var s in series)
        {
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                bool open = false;
                foreach (var p in s.Pts)
                {
                    if (double.IsNaN(p.v))
                    {
                        if (open) { gc.EndFigure(false); open = false; }
                        continue;
                    }
                    var q = new Point(X(p.s.Ts), Y(p.v));
                    if (!open) { gc.BeginFigure(q, false); open = true; }
                    else gc.LineTo(q);
                }
                if (open) gc.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(s.Col, 0.22), 4)
            { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, geo);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(s.Col), 1.8)
            { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round }, geo);

            // 末端圆点只画在最新一个点上，且该点有效时才画（最新读数无效时不标「当前值」）
            var last = s.Pts[^1];
            if (!double.IsNaN(last.v))
                ctx.DrawEllipse(new SolidColorBrush(s.Col), null, new Point(X(last.s.Ts), Y(last.v)), 2.8, 2.8);
        }
    }

    // ============ 三变量同屏视图 ============

    /// <summary>「全部」视图：每通道 温度/压力/气体 三条曲线同屏，左侧三条独立 Y 轴（各变量独立自适应刻度）。</summary>
    private void RenderAll(DrawingContext ctx, GraphViewModel vm, List<GraphChannel> on)
    {
        _pl = 148; _pr = 16; _pt = 26; _pb = 30;
        double pl = _pl, W = _W, H = _H;

        // 收集序列：通道 × 变量。NaN = 该时刻读数无效（模块离线 / 未实测），曲线在此断开
        var series = new List<HitSeries>();
        foreach (var ch in on)
        {
            var h = vm.History[ch.Reactor.Id];
            for (int m = 0; m < 3; m++)
            {
                var sr = new HitSeries { Id = ch.Reactor.Id, Metric = m, Col = Color.Parse(ch.ColorHex) };
                foreach (var s in h)
                    if (s.Ts >= _tMin) sr.Pts.Add((s, vm.Val(s, AllMetrics[m])));
                if (sr.Pts.Count(p => !double.IsNaN(p.v)) >= 2) series.Add(sr);
            }
        }
        _hit = series.Count > 0 ? series : null;
        if (_hit == null)
        {
            _sel = null;
            DrawText(ctx, "暂无有效读数 · 模块离线或该量尚未实测", W / 2, H / 2 - 8, Mute, 13, TextAlignment.Center);
            return;
        }

        // 每变量独立 y 范围（自适应 + 留白）；某变量整体无数据时不画它的刻度列
        for (int m = 0; m < 3; m++)
        {
            double mn = double.MaxValue, mx = double.MinValue;
            foreach (var s in series)
            {
                if (s.Metric != m) continue;
                foreach (var p in s.Pts)
                {
                    if (double.IsNaN(p.v)) continue;
                    mn = Math.Min(mn, p.v); mx = Math.Max(mx, p.v);
                }
            }
            if (mn == double.MaxValue) continue;
            _has[m] = true;
            double minSpan = m == 0 ? 1.0 : 0.01;
            if (mx - mn < minSpan) { double mid = (mx + mn) / 2; mn = mid - minSpan / 2; mx = mid + minSpan / 2; }
            double pad = (mx - mn) * 0.12;
            _yLo[m] = mn - pad; _yHi[m] = mx + pad;
        }

        // —— y 网格 + 三条刻度列（由远到近：温度 / 压力 / 气体；列顶只标单位，刻度按变量配色）——
        var gpenY = new Pen(new SolidColorBrush(Color.FromArgb(16, 0, 0, 0)), 1);
        double[] labelX = { pl - 104, pl - 56, pl - 8 };
        for (int m = 0; m < 3; m++)
            if (_has[m])
                DrawText(ctx, GraphViewModel.MetricUnit(AllMetrics[m]), labelX[m], _pt - 15,
                    AllColors[m], 10.5, TextAlignment.Right, FontWeight.SemiBold);
        for (int i = 0; i <= 5; i++)
        {
            double y = _pt + (H - _pt - _pb) * i / 5;
            ctx.DrawLine(gpenY, new Point(pl, y), new Point(W - _pr, y));
            for (int m = 0; m < 3; m++)
            {
                if (!_has[m]) continue;
                double v = _yHi[m] - (_yHi[m] - _yLo[m]) * i / 5;
                string fmt = "0." + new string('0', GraphViewModel.MetricDecimals(AllMetrics[m]));
                DrawText(ctx, v.ToString(fmt, CultureInfo.InvariantCulture), labelX[m], y - 7, AllColors[m], 10.5, TextAlignment.Right);
            }
        }

        // —— 线型图例（绘图区上方居中，中性色：只说明线型含义，不代表曲线颜色）——
        var legendLabels = AllMetrics
            .Select(m => $"{GraphViewModel.MetricName(m)} {GraphViewModel.MetricUnit(m)}")
            .ToArray();
        double legendWidth = legendLabels.Sum(lab => 23 + TextWidth(lab, 10.5) + 20) - 20;
        double kx = pl + Math.Max(4, ((W - _pr - pl) - legendWidth) / 2);
        for (int m = 0; m < legendLabels.Length; m++)
        {
            string lab = legendLabels[m];
            ctx.DrawLine(MetricPen(m, Body), new Point(kx, _pt - 11), new Point(kx + 18, _pt - 11));
            DrawText(ctx, lab, kx + 23, _pt - 17, Body, 10.5, TextAlignment.Left);
            kx += 23 + TextWidth(lab, 10.5) + 20;
        }

        DrawXGrid(ctx, vm, W, H);

        // —— 曲线：温度=实线 / 压力=虚线 / 气体=点线；颜色 = 通道色。遇到无效点断开，分成多段 ——
        foreach (var s in series)
        {
            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                bool open = false;
                foreach (var p in s.Pts)
                {
                    if (double.IsNaN(p.v))
                    {
                        if (open) { gc.EndFigure(false); open = false; }
                        continue;
                    }
                    var q = new Point(XOf(p.s.Ts), YOf(s.Metric, p.v));
                    if (!open) { gc.BeginFigure(q, false); open = true; }
                    else gc.LineTo(q);
                }
                if (open) gc.EndFigure(false);
            }
            ctx.DrawGeometry(null, MetricPen(s.Metric, s.Col), geo);
        }
    }

    /// <summary>x 网格 + 刻度（墙钟时间 HH:mm:ss，不含日期）。</summary>
    private void DrawXGrid(DrawingContext ctx, GraphViewModel vm, double W, double H)
    {
        var gpenX = new Pen(new SolidColorBrush(Color.FromArgb(11, 0, 0, 0)), 1);
        for (int i = 0; i <= 6; i++)
        {
            double x = _pl + (W - _pl - _pr) * i / 6;
            double tv = _tMin + _span * i / 6;
            ctx.DrawLine(gpenX, new Point(x, _pt), new Point(x, H - _pb));
            DrawText(ctx, vm.WallTime(tv).ToString("HH:mm:ss"), x, H - _pb + 6, Mute, 10.5, TextAlignment.Center);
        }
    }

    // ============ 点击选点：十字线 + 详情 tip ============

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var vm = Vm;
        if (vm == null || _hit == null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);

        // 点到绘图区外（刻度区 / 边距）：清除选点
        if (p.X < _pl || p.X > _W - _pr || p.Y < _pt || p.Y > _H - _pb)
        {
            if (_sel != null) { _sel = null; InvalidateVisual(); }
            return;
        }

        double clickT = _tMin + (p.X - _pl) / Math.Max(1, _W - _pl - _pr) * _span;

        // 每条曲线取时间上最近的有效点，再按垂直距离挑出用户点的那条
        HitSeries? bestSr = null;
        GSample bestS = default;
        double bestDy = double.MaxValue;
        foreach (var sr in _hit)
        {
            GSample bs = default;
            double bd = double.MaxValue;
            bool found = false;
            foreach (var (s, v) in sr.Pts)
            {
                if (double.IsNaN(v)) continue;
                double d = Math.Abs(s.Ts - clickT);
                if (d < bd) { bd = d; bs = s; found = true; }
            }
            if (!found) continue;
            double dy = Math.Abs(YOf(sr.Metric, vm.Val(bs, AllMetrics[sr.Metric])) - p.Y);
            if (dy < bestDy) { bestDy = dy; bestSr = sr; bestS = bs; }
        }

        // 离所有曲线都太远 = 点空白处：清除选点；否则吸附到最近采样点
        _sel = bestSr == null || bestDy > 56 ? null : (bestSr.Id, bestSr.Metric, bestS);
        InvalidateVisual();
    }

    /// <summary>选中点 overlay：竖直十字线 + 三变量对应圆点 + 详情卡片。</summary>
    private void DrawSelection(DrawingContext ctx, GraphViewModel vm)
    {
        if (_sel is not { } sel || _hit == null) return;
        var sr = _hit.FirstOrDefault(h => h.Id == sel.id && h.Metric == sel.m);
        if (sr == null || !_has[sel.m]) { _sel = null; return; }   // 通道被关闭/停用或变量轴消失：清除

        double x = XOf(sel.s.Ts);
        if (x < _pl - 0.5 || x > _W - _pr + 0.5) { _sel = null; return; }   // 时间范围变化后点已出界

        // 十字竖线
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1)
        { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) },
            new Point(x, _pt), new Point(x, _H - _pb));

        // 三变量在同一时刻的对应点：选中变量实心大点，其余空心小点（读数无效则跳过）
        double yMain = _pt + 40;
        for (int m = 0; m < 3; m++)
        {
            if (!_has[m]) continue;
            double v = vm.Val(sel.s, AllMetrics[m]);
            if (double.IsNaN(v)) continue;
            double y = YOf(m, v);
            if (m == sel.m)
            {
                yMain = y;
                ctx.DrawEllipse(Brushes.White, null, new Point(x, y), 5.5, 5.5);
                ctx.DrawEllipse(new SolidColorBrush(sr.Col), null, new Point(x, y), 4, 4);
            }
            else ctx.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(sr.Col), 1.4), new Point(x, y), 3, 3);
        }

        DrawTip(ctx, vm, sel, sr.Col, x, yMain);
    }

    /// <summary>详情卡片（深色专业风）：深底 + 通道色圆点（带光晕）+ RV 编号 + 时钟时间 + 温度/压力/气体三行读数（无效显示「—」）。</summary>
    private void DrawTip(DrawingContext ctx, GraphViewModel vm, (int id, int m, GSample s) sel,
        Color chCol, double x, double y)
    {
        var bg = Color.Parse("#1d2333");
        var labelCol = Color.Parse("#aab2c5");
        var timeCol = Color.Parse("#9aa3b8");
        var unitCol = Color.Parse("#8b93a8");
        var white = Color.Parse("#ffffff");

        string time = vm.WallTime(sel.s.Ts).ToString("HH:mm:ss");
        string title = $"RV{sel.id}";

        var rows = new (string name, string val, string unit, Color col)[3];
        for (int m = 0; m < 3; m++)
        {
            double v = vm.Val(sel.s, AllMetrics[m]);
            bool ok = !double.IsNaN(v);
            rows[m] = (GraphViewModel.MetricName(AllMetrics[m]),
                ok ? v.ToString("0." + new string('0', GraphViewModel.MetricDecimals(AllMetrics[m])), CultureInfo.InvariantCulture) : "—",
                ok ? " " + GraphViewModel.MetricUnit(AllMetrics[m]) : "",
                AllColors[m]);
        }

        const double padH = 12, padV = 10, headerH = 16, rowH = 19;
        double wHead = 12 + TextWidth(title, 12.5, FontWeight.SemiBold) + 14 + TextWidth(time, 10.5);
        double wRow = 0;
        foreach (var r in rows)
            wRow = Math.Max(wRow, 15 + TextWidth(r.name, 10.5) + 12
                + TextWidth(r.val, 12, FontWeight.SemiBold) + TextWidth(r.unit, 10.5));
        double boxW = Math.Ceiling(Math.Max(wHead, wRow) + padH * 2);
        double boxH = padV * 2 + headerH + 3 + 3 * rowH;

        // 位置：默认在点的右侧，贴边时翻转/收拢到控件内
        double tx = x + 14;
        if (tx + boxW > _W - 6) tx = x - 14 - boxW;
        tx = Math.Max(6, tx);
        double ty = Math.Max(6, Math.Min(_H - 6 - boxH, y - boxH / 2));

        // 阴影 + 深底卡片 + 微亮描边
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(70, 16, 24, 40)), null,
            new RoundedRect(new Rect(tx + 2, ty + 5, boxW, boxH), 10));
        ctx.DrawRectangle(new SolidColorBrush(bg),
            new Pen(new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)), 1),
            new RoundedRect(new Rect(tx, ty, boxW, boxH), 10));

        double cx = tx + padH;
        double cy = ty + padV;
        // 标题行：通道色圆点（带光晕）+ RV 编号 + 时间
        ctx.DrawEllipse(new SolidColorBrush(chCol, 0.35), null, new Point(cx + 3.5, cy + 6), 6, 6);
        ctx.DrawEllipse(new SolidColorBrush(chCol), null, new Point(cx + 3.5, cy + 6), 3.5, 3.5);
        DrawText(ctx, title, cx + 11, cy, white, 12.5, TextAlignment.Left, FontWeight.SemiBold);
        DrawText(ctx, time, tx + boxW - padH, cy + 1.5, timeCol, 10.5, TextAlignment.Right);
        cy += headerH + 3;
        // 温度 / 压力 / 气体 三行：变量色小方块 + 灰蓝标签 + 右对齐白色读数（单位更暗）
        foreach (var (name, val, unit, col) in rows)
        {
            ctx.DrawRectangle(new SolidColorBrush(col), null, new RoundedRect(new Rect(cx, cy + 4.5, 8, 8), 2.5));
            DrawText(ctx, name, cx + 15, cy + 2.5, labelCol, 10.5, TextAlignment.Left, FontWeight.Medium);
            double uw = unit.Length > 0 ? TextWidth(unit, 10.5) : 0;
            if (uw > 0)
                DrawText(ctx, unit, tx + boxW - padH, cy + 1.5, unitCol, 10.5, TextAlignment.Right);
            DrawText(ctx, val, tx + boxW - padH - uw, cy, white, 12, TextAlignment.Right, FontWeight.SemiBold);
            cy += rowH;
        }
    }

    // ============ 工具 ============

    private double XOf(double t) => _pl + (_W - _pl - _pr) * (t - _tMin) / _span;
    private double YOf(int m, double v)
        => _H - _pb - (_H - _pt - _pb) * (v - _yLo[m]) / (_yHi[m] - _yLo[m] <= 0 ? 1 : _yHi[m] - _yLo[m]);

    /// <summary>变量线型：温度=实线、压力=虚线、气体=点线（颜色由调用方给：曲线用通道色）。</summary>
    private static Pen MetricPen(int m, Color col)
    {
        var pen = new Pen(new SolidColorBrush(col), m == 0 ? 2.0 : 1.7)
        { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round };
        if (m == 1) pen.DashStyle = new DashStyle(new double[] { 5, 3 }, 0);
        if (m == 2) pen.DashStyle = new DashStyle(new double[] { 1.6, 2.6 }, 0);
        return pen;
    }

    private static double TextWidth(string text, double size, FontWeight weight = FontWeight.Normal)
        => new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            WeightedFace(weight), size, Brushes.Black).Width;

    private static Typeface WeightedFace(FontWeight weight)
        => weight == FontWeight.Normal ? Face : new Typeface(Face.FontFamily, FontStyle.Normal, weight);

    private static void DrawText(DrawingContext ctx, string text, double x, double y, Color color,
        double size, TextAlignment align, FontWeight weight = FontWeight.Normal)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            WeightedFace(weight), size, new SolidColorBrush(color));
        double dx = align switch
        {
            TextAlignment.Center => -ft.Width / 2,
            TextAlignment.Right => -ft.Width,
            _ => 0,
        };
        ctx.DrawText(ft, new Point(x + dx, y));
    }
}
