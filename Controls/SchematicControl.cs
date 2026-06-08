using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using ParallelReactor.Models;

namespace ParallelReactor.Controls;

/// <summary>
/// 反应器与气路系统示意图。
/// 完整复刻 HTML build() 的坐标系（viewBox 0 0 1440 484）与绘制逻辑：
/// 8 个反应釜横向排列 + 共用气路总管 + 3 进气阀 + 1 排空阀。
/// 通过 RenderTransform 等比缩放铺满容器，命中检测在原始坐标系内完成。
/// </summary>
public class SchematicControl : Control
{
    // —— 设计坐标系 ——
    private const double VbW = 1440, VbH = 560;
    // 实际图形内容的包围盒（设计坐标系）。按它来缩放铺满，去掉 viewBox 四周空白，
    // 使反应釜/阀门/文字整体更大、更充分利用面板空间。
    private const double CtX = 80, CtY = 2, CtW = 1312, CtH = 556;
    private static readonly double[] Xs = { 250, 400, 550, 700, 850, 1000, 1150, 1300 };
    private const double Scale = 0.82, RTop = 40;
    private static readonly double RBot = RTop + 222 * Scale;     // ≈ 222
    private const double HdrY = 470, CheckY = 320, ValY = 374;
    private static readonly double[] GasX = { 110, 160, 210 };
    private const double VentX = 1340;

    // —— 数据源 ——
    public IReadOnlyList<Reactor>? Reactors { get; set; }
    public IReadOnlyList<GasInlet>? GasInlets { get; set; }
    public bool VentOn { get; set; }

    // —— 命中回调 ——
    public Action<int>? OnReactorClick;   // 传 RV id
    public Action<int>? OnValveClick;     // 传 RV id（控制阀 SV）
    public Action<int>? OnGasClick;       // 传 gas index
    public Action? OnVentClick;

    // 动画相位（由外部计时器推进，用于流动虚线偏移、桨叶旋转、气泡、脉冲）
    private double _phase;
    public double Phase
    {
        get => _phase;
        set { _phase = value; InvalidateVisual(); }
    }

    // 命中矩形（设计坐标系）
    private record struct Hit(Rect Bounds, string Kind, int Index);
    private readonly List<Hit> _hits = new();

    // ============ 颜色 ============
    private static readonly Color OnDark = Color.Parse("#f6f6f8");
    private static readonly Color Body = Color.Parse("#a4a4b0");
    private static readonly Color GreenBright = Color.Parse("#a3e635");
    private static readonly Color AccentBright = Color.Parse("#e0394c");
    private static readonly Color Amber = Color.Parse("#f0a830");
    private static readonly Color Ink = Color.Parse("#514b41");
    private static readonly Color InkErr = Color.Parse("#c1352a");
    private static readonly Color InkLight = Color.Parse("#9d978d");
    private static readonly Color HeaderLine = Color.FromArgb(128, 218, 226, 238);

    private static Color StColor(Reactor c) => c.State switch
    {
        ReactorState.React => GreenBright,
        ReactorState.Alarm => AccentBright,
        ReactorState.Heating or ReactorState.Pressing => Amber,
        ReactorState.Done => Color.Parse("#7d8694"),
        _ => Color.Parse("#66666f")
    };

    private static Typeface SansFace => new("Inter, Noto Sans SC");
    private static Typeface MonoFace => new("JetBrains Mono, monospace");

    /// <summary>
    /// 管段画笔。flow=true 时返回会流动的虚线（dasharray 7,9，dashoffset 随相位移动），
    /// 对应 HTML .flow{stroke-dasharray:7 9;animation:flow 1.1s linear infinite}。
    /// 注意 Avalonia 的 DashStyle 单位是「线宽倍数」，需除以 width 换算。
    /// </summary>
    private Pen PipePen(Color color, double width, bool flow)
    {
        if (!flow)
            return new Pen(new SolidColorBrush(color), width) { LineCap = PenLineCap.Round };

        double offset = -(Phase / 1.1 * 16.0) % 16.0;
        return new Pen(new SolidColorBrush(color), width)
        {
            LineCap = PenLineCap.Round,
            DashStyle = new DashStyle(new double[] { 7.0 / width, 9.0 / width }, offset / width)
        };
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) return;

        // 等比缩放，按内容包围盒铺满并居中（preserveAspectRatio: xMidYMid meet）
        double s = Math.Min(b.Width / CtW, b.Height / CtH);
        double ox = (b.Width - CtW * s) / 2 - CtX * s;
        double oy = (b.Height - CtH * s) / 2 - CtY * s;

        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
        {
            _hits.Clear();
            DrawHeader(ctx);
            if (Reactors != null)
                for (int i = 0; i < Reactors.Count && i < 8; i++)
                    DrawReactor(ctx, Reactors[i], i);
            DrawGasInlets(ctx);
            DrawVent(ctx);
        }

        // 记录变换，供命中检测反推
        _lastScale = s; _lastOx = ox; _lastOy = oy;
    }

    private double _lastScale = 1, _lastOx, _lastOy;

    // ============ 总管 ============
    private void DrawHeader(DrawingContext ctx)
    {
        // 外发光底
        var glow = new Pen(new SolidColorBrush(Color.FromArgb(58, 255, 255, 255)), 9);
        ctx.DrawLine(glow, new Point(90, HdrY), new Point(1380, HdrY));
        var main = new Pen(new SolidColorBrush(HeaderLine), 4) { LineCap = PenLineCap.Round };
        ctx.DrawLine(main, new Point(90, HdrY), new Point(1380, HdrY));

        DrawText(ctx, "共用气路总管 · GAS HEADER HDR-01", 735, HdrY + 10,
            Color.Parse("#5c5c66"), 10.5, SansFace, TextAlignment.Center);
    }

    // ============ 单个反应釜 ============
    private void DrawReactor(DrawingContext ctx, Reactor c, int i)
    {
        double cx = Xs[i];
        bool flow = c.Valve && (c.State == ReactorState.React || c.State == ReactorState.Pressing);
        Color col = StColor(c);
        var lc = flow ? col : Color.FromArgb(51, 255, 255, 255);
        double lw = flow ? 3 : 2.2;

        // RV 标号 + 状态点（整组水平居中于釜中心 cx，与釜体/读数同一竖直中线）
        var rvFt = new FormattedText($"RV{c.Id}", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(SansFace.FontFamily, FontStyle.Normal, FontWeight.Bold), 14.5,
            new SolidColorBrush(OnDark));
        const double dotR = 4, gap = 8;
        double groupW = dotR * 2 + gap + rvFt.Width;
        double gx = cx - groupW / 2;
        const double gcy = 18;
        ctx.DrawEllipse(new SolidColorBrush(col), null, new Point(gx + dotR, gcy), dotR, dotR);
        ctx.DrawText(rvFt, new Point(gx + dotR * 2 + gap, gcy - rvFt.Height / 2));

        // 釜体（平移 + 缩放到内部坐标系）
        using (ctx.PushTransform(
            Matrix.CreateScale(Scale, Scale) *
            Matrix.CreateTranslation(cx - 50 * Scale, RTop)))
        {
            DrawReactorInner(ctx, c);
        }

        // 釜下方读数
        if (c.State != ReactorState.Idle)
        {
            var tCol = c.State == ReactorState.React ? GreenBright
                : c.State == ReactorState.Alarm ? AccentBright : OnDark;
            DrawText(ctx, $"{c.T:0.0}", cx, RBot + 20 - 16, tCol, 21, SansFace,
                TextAlignment.Center, FontWeight.Light);
            DrawText(ctx, "°C", cx + 26, RBot + 20 - 8, Body, 10.5, SansFace, TextAlignment.Left);

            var pCol = c.State == ReactorState.Alarm ? AccentBright : Color.Parse("#dfe2e8");
            DrawText(ctx, $"{c.P:0.0} psi · {c.Gas:0.00} mmol", cx, RBot + 37 - 9,
                pCol, 11, SansFace, TextAlignment.Center, FontWeight.SemiBold);

            // 搅拌为全局共用，转速统一显示在主界面「搅拌总控」卡片，这里只显示状态
            DrawText(ctx, c.StateZh, cx, RBot + 52 - 8,
                col, 10, SansFace, TextAlignment.Center, FontWeight.SemiBold);
        }
        else
        {
            DrawText(ctx, "通道停用", cx, RBot + 28 - 10, Color.Parse("#66666f"),
                12.5, SansFace, TextAlignment.Center);
        }

        // 釜 → CV → SV → 总管 的竖直管路
        double dropTop = RBot + 62;
        var linePen = PipePen(lc, lw, flow);
        ctx.DrawLine(linePen, new Point(cx, dropTop), new Point(cx, CheckY - 8.5));
        DrawCheckValve(ctx, cx, CheckY);
        DrawText(ctx, $"CV{c.Id}", cx + 14, CheckY + 3 - 4.5, Color.Parse("#5c5c66"), 9, SansFace);
        ctx.DrawLine(linePen, new Point(cx, CheckY + 8.5), new Point(cx, ValY - 11));
        DrawCtrlValve(ctx, cx, ValY, c.Valve);
        DrawText(ctx, $"SV{c.Id}", cx + 15, ValY + 3.5 - 4.5, Color.Parse("#5c5c66"), 9, SansFace);
        ctx.DrawLine(linePen, new Point(cx, ValY + 11), new Point(cx, HdrY));

        // 命中区
        _hits.Add(new Hit(new Rect(cx - 72, 0, 144, RBot + 58), "rv", c.Id));
        _hits.Add(new Hit(new Rect(cx - 13, ValY - 13, 26, 26), "sv", c.Id));
    }

    // 釜内部结构（内部坐标系：中心 x=50，高约 0–222）
    private void DrawReactorInner(DrawingContext ctx, Reactor c)
    {
        bool isOff = c.State == ReactorState.Idle;
        bool isErr = c.State == ReactorState.Alarm;
        Color ink = isOff ? InkLight : (isErr ? InkErr : Ink);
        var inkBrush = new SolidColorBrush(ink);
        var inkPen = new Pen(inkBrush, 1.2);
        var inkPenThin = new Pen(inkBrush, 1.5) { LineCap = PenLineCap.Round };

        // 1. 顶部注射口
        ctx.DrawLine(inkPenThin, new Point(50, 6), new Point(50, 22));
        var tri = new PolylineGeometry(new[] { new Point(45, 4), new Point(55, 4), new Point(50, 12) }, true);
        ctx.DrawGeometry(inkBrush, null, tri);
        ctx.DrawRectangle(Brushes.White, inkPen, new RoundedRect(new Rect(37, 22, 26, 5), 1));
        DrawText(ctx, "INJ", 73, 12 - 4, InkLight, 8, MonoFace);

        // 2. 单向阀
        ctx.DrawLine(inkPenThin, new Point(50, 27), new Point(50, 34));
        ctx.DrawGeometry(null, inkPen,
            new PolylineGeometry(new[] { new Point(43, 30), new Point(57, 30), new Point(50, 38) }, true));

        // 3. 釜头
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#f0ead9")), inkPen,
            new RoundedRect(new Rect(17, 38, 66, 14), 2));
        var dash = new Pen(new SolidColorBrush(Color.Parse("#c4bfb4")), 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
        ctx.DrawLine(dash, new Point(17, 48), new Point(83, 48));

        // 4. 加热块
        IBrush heaterFill = isOff
            ? new SolidColorBrush(Color.Parse("#fafaf7"))
            : new SolidColorBrush(Color.Parse("#f0ead9"));   // 斜纹用纯色近似
        var heaterPen = isOff
            ? new Pen(inkBrush, 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) }
            : new Pen(inkBrush, 1.2);
        ctx.DrawRectangle(heaterFill, heaterPen, new RoundedRect(new Rect(17, 52, 66, 130), 3));
        // 斜纹（仅非停用时叠加）
        if (!isOff) DrawHatch(ctx, new Rect(17, 52, 66, 130), Color.Parse("#dcd1b1"));
        DrawText(ctx, isOff ? "OFF" : "HTR", 24, 64 - 8, Color.Parse("#65605a"), 8,
            new Typeface("Inter"));

        // 5. 玻璃内衬
        var glassFill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#fafbfc"), 0),
                new GradientStop(Color.Parse("#eef0f3"), 0.8)
            }
        };
        ctx.DrawRectangle(glassFill, new Pen(inkBrush, 1), new RoundedRect(new Rect(35, 58, 30, 118), 6));

        // 6. 液体
        if (!isOff)
        {
            double liqH = Math.Max(30, Math.Min(78, 26 + c.Vol * 10));
            double liqTop = 176 - liqH;
            var liqBrush = StateLiquidBrush(c.State);
            using (ctx.PushOpacity(0.88))
                ctx.DrawRectangle(liqBrush, null, new RoundedRect(new Rect(37, liqTop, 26, liqH), 4));
        }

        // 7. 桨叶（搅拌时旋转 — 用 phase 控制水平缩放模拟旋转）
        //    放大桨片、加大摆动幅度并加快周期，确保在嵌入式 Linux 上也清晰可见
        bool showStir = !(isOff || c.IsDone || c.Rpm == 0);
        using (ctx.PushTransform(Matrix.CreateTranslation(50, 166)))
        {
            var bladeBrush = new SolidColorBrush(Color.Parse("#2f2a23"));
            // 搅拌杆（更粗）
            ctx.DrawLine(new Pen(bladeBrush, 2.2), new Point(0, -52), new Point(0, 4));
            double rx = 14;
            if (showStir)
            {
                // 周期 0.9s，scaleX 在 1 ↔ -0.9 间振荡（边缘几乎成竖线 = 旋转到侧面）
                double t = (Phase / 0.9) % 1.0;
                double scaleX = 1 + (-0.9 - 1) * (1 - Math.Cos(t * Math.PI * 2)) / 2;
                rx = 14 * scaleX;
            }
            double absRx = Math.Max(1.6, Math.Abs(rx));
            // 桨片画成圆角矩形（比细椭圆更醒目）
            ctx.DrawRectangle(bladeBrush, null,
                new RoundedRect(new Rect(-absRx, -2.5, absRx * 2, 5), 2.5));
        }

        // 8. 气泡（反应中）
        if (c.State == ReactorState.React)
        {
            double liqH = Math.Max(30, Math.Min(78, 26 + c.Vol * 10));
            double liqTop = 176 - liqH;
            for (int k = 0; k < 4; k++)
            {
                double bx = 43 + k * 5, dur = 1.4 + k * 0.3, top = liqTop + 8;
                double tt = ((Phase / dur) + k * 0.25) % 1.0;
                double by = 168 - (168 - top) * tt;
                double op = Math.Sin(tt * Math.PI) * 0.75;
                using (ctx.PushOpacity(Math.Max(0, op)))
                    ctx.DrawEllipse(Brushes.White, null, new Point(bx, by), 2.3, 2.3);
            }
        }

        // 9. 热电偶 TT
        ctx.DrawLine(inkPen, new Point(17, 160), new Point(9, 160));
        ctx.DrawEllipse(Brushes.White, inkPen, new Point(6, 160), 3.5, 3.5);
        DrawText(ctx, "TT", 1, 172 - 3.5, InkLight, 7, MonoFace);

        // 10. 压力变送器 PT
        double ptW = isErr ? 1.8 : 1.2, ptCircle = isErr ? 1.6 : 1.2;
        ctx.DrawLine(new Pen(inkBrush, ptW), new Point(83, 72), new Point(91, 72));
        ctx.DrawEllipse(Brushes.White, new Pen(inkBrush, ptCircle), new Point(95, 72), 5, 5);
        DrawText(ctx, "PT", 91, 75 - 3.5, ink, 7, MonoFace, TextAlignment.Left, FontWeight.SemiBold);
        if (isErr)
        {
            // 脉冲圈
            double pr = 7 + 7 * (Math.Sin(Phase * Math.PI) * 0.5 + 0.5);
            double pop = 0.7 * (1 - (Math.Sin(Phase * Math.PI) * 0.5 + 0.5));
            using (ctx.PushOpacity(Math.Max(0, pop)))
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(InkErr), 1.4), new Point(95, 72), pr, pr);
        }

        // 11. 底部出气短管
        var botPen = isOff
            ? new Pen(inkBrush, 1.4) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) }
            : new Pen(inkBrush, 1.6);
        ctx.DrawLine(botPen, new Point(50, 182), new Point(50, 222));
    }

    private static IBrush StateLiquidBrush(ReactorState st)
    {
        (Color a, Color b) = st switch
        {
            ReactorState.React => (Color.Parse("#9ee8b8"), Color.Parse("#2c8b56")),
            ReactorState.Pressing => (Color.Parse("#a8c4e4"), Color.Parse("#2d5ca0")),
            ReactorState.Heating => (Color.Parse("#f3d896"), Color.Parse("#c97818")),
            ReactorState.Alarm => (Color.Parse("#f0a8a0"), Color.Parse("#c1352a")),
            ReactorState.Done => (Color.Parse("#b8bcc4"), Color.Parse("#6b7079")),
            _ => (Colors.Transparent, Colors.Transparent)
        };
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) }
        };
    }

    // 斜纹填充（45°）
    private void DrawHatch(DrawingContext ctx, Rect r, Color line)
    {
        var pen = new Pen(new SolidColorBrush(line), 1);
        using (ctx.PushClip(r))
        {
            for (double x = r.X - r.Height; x < r.Right; x += 6)
            {
                ctx.DrawLine(pen, new Point(x, r.Bottom), new Point(x + r.Height, r.Top));
            }
        }
    }

    // 控制阀（方框 + 双三角，开启时绿色）
    private void DrawCtrlValve(DrawingContext ctx, double cx, double cy, bool open)
    {
        var fill = new SolidColorBrush(open ? Color.FromArgb(56, 132, 204, 22) : Color.FromArgb(13, 255, 255, 255));
        var stroke = new SolidColorBrush(open ? GreenBright : Color.FromArgb(77, 255, 255, 255));
        var pen = new Pen(stroke, 1.3);
        ctx.DrawRectangle(fill, pen, new RoundedRect(new Rect(cx - 11, cy - 11, 22, 22), 6));
        var g = new PathGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(new Point(cx - 6, cy - 6), false);
            gc.LineTo(new Point(cx, cy));
            gc.LineTo(new Point(cx - 6, cy + 6));
            gc.EndFigure(false);
            gc.BeginFigure(new Point(cx + 6, cy - 6), false);
            gc.LineTo(new Point(cx, cy));
            gc.LineTo(new Point(cx + 6, cy + 6));
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    // 单向阀（红色圆 + 三角）
    private void DrawCheckValve(DrawingContext ctx, double cx, double cy)
    {
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(26, 224, 57, 76)),
            new Pen(new SolidColorBrush(Color.FromArgb(107, 224, 57, 76)), 1.2),
            new Point(cx, cy), 8.5, 8.5);
        var tri = new PolylineGeometry(new[]
        {
            new Point(cx - 3.3, cy - 3.6), new Point(cx + 3.8, cy), new Point(cx - 3.3, cy + 3.6)
        }, true);
        ctx.DrawGeometry(new SolidColorBrush(Color.FromArgb(153, 224, 57, 76)), null, tri);
    }

    // ============ 进气阀 ============
    private void DrawGasInlets(DrawingContext ctx)
    {
        if (GasInlets == null) return;
        for (int i = 0; i < GasInlets.Count && i < 3; i++)
        {
            var g = GasInlets[i];
            double x = GasX[i];
            var lc = g.On ? GreenBright : Color.FromArgb(51, 255, 255, 255);
            var pen = PipePen(lc, g.On ? 3 : 2.2, g.On);
            ctx.DrawLine(pen, new Point(x, HdrY), new Point(x, HdrY + 44));
            DrawCtrlValve(ctx, x, HdrY + 56, g.On);
            DrawText(ctx, g.Label, x, HdrY + 82 - 10, Body, 10, SansFace, TextAlignment.Center, FontWeight.SemiBold);
            _hits.Add(new Hit(new Rect(x - 13, HdrY + 43, 26, 26), "gas", i));
        }
    }

    // ============ 排空阀 ============
    private void DrawVent(DrawingContext ctx)
    {
        var lc = VentOn ? AccentBright : Color.FromArgb(51, 255, 255, 255);
        var pen = PipePen(lc, VentOn ? 3 : 2.2, VentOn);
        ctx.DrawLine(pen, new Point(VentX, HdrY), new Point(VentX, HdrY + 44));
        // 排空阀沿用控制阀外形，但开启时是红色——用一个变体
        DrawVentValve(ctx, VentX, HdrY + 56, VentOn);
        DrawText(ctx, "排空 / 泄压", VentX, HdrY + 82 - 10, Body, 10, SansFace, TextAlignment.Center, FontWeight.SemiBold);
        _hits.Add(new Hit(new Rect(VentX - 13, HdrY + 43, 26, 26), "vent", 0));
    }

    private void DrawVentValve(DrawingContext ctx, double cx, double cy, bool open)
    {
        var fill = new SolidColorBrush(open ? Color.FromArgb(56, 224, 57, 76) : Color.FromArgb(13, 255, 255, 255));
        var stroke = new SolidColorBrush(open ? AccentBright : Color.FromArgb(77, 255, 255, 255));
        var pen = new Pen(stroke, 1.3);
        ctx.DrawRectangle(fill, pen, new RoundedRect(new Rect(cx - 11, cy - 11, 22, 22), 6));
        var g = new PathGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(new Point(cx - 6, cy - 6), false);
            gc.LineTo(new Point(cx, cy));
            gc.LineTo(new Point(cx - 6, cy + 6));
            gc.EndFigure(false);
            gc.BeginFigure(new Point(cx + 6, cy - 6), false);
            gc.LineTo(new Point(cx, cy));
            gc.LineTo(new Point(cx + 6, cy + 6));
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    // ============ 文本辅助 ============
    private void DrawText(DrawingContext ctx, string text, double x, double y, Color color,
        double size, Typeface face, TextAlignment align = TextAlignment.Left,
        FontWeight weight = FontWeight.Normal)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(face.FontFamily, FontStyle.Normal, weight), size, new SolidColorBrush(color));
        double dx = align switch
        {
            TextAlignment.Center => -ft.Width / 2,
            TextAlignment.Right => -ft.Width,
            _ => 0
        };
        ctx.DrawText(ft, new Point(x + dx, y));
    }

    // ============ 命中检测 ============
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        // 反推到设计坐标系
        double dx = (p.X - _lastOx) / _lastScale;
        double dy = (p.Y - _lastOy) / _lastScale;
        var pt = new Point(dx, dy);

        // 优先级：阀门/进气/排空 > 釜体（与 HTML 一致，小命中区在前）
        foreach (var h in _hits)
        {
            if (h.Kind == "rv") continue;
            if (h.Bounds.Contains(pt))
            {
                Dispatch(h);
                return;
            }
        }
        foreach (var h in _hits)
        {
            if (h.Kind != "rv") continue;
            if (h.Bounds.Contains(pt))
            {
                OnReactorClick?.Invoke(h.Index);
                return;
            }
        }
    }

    private void Dispatch(Hit h)
    {
        switch (h.Kind)
        {
            case "sv": OnValveClick?.Invoke(h.Index); break;
            case "gas": OnGasClick?.Invoke(h.Index); break;
            case "vent": OnVentClick?.Invoke(); break;
        }
    }
}
