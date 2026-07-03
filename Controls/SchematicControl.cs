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
    private const double Scale = 0.82, RTop = 52;
    private static readonly double RBot = RTop + 222 * Scale;     // ≈ 222
    private const double HdrY = 470, CheckY = 360, ValY = 406;
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
    private static readonly Color OnDark = Color.Parse("#0a0a0e");
    private static readonly Color Body = Color.Parse("#3a3a44");
    private static readonly Color GreenBright = Color.Parse("#5fae14");
    private static readonly Color AccentBright = Color.Parse("#e0394c");
    private static readonly Color Amber = Color.Parse("#f0a830");
    private static readonly Color Ink = Color.Parse("#514b41");
    private static readonly Color InkErr = Color.Parse("#c1352a");
    private static readonly Color InkLight = Color.Parse("#9d978d");
    private static readonly Color HeaderLine = Color.FromArgb(150, 120, 128, 145);

    private static Color StColor(Reactor c) => c.State switch
    {
        ReactorState.React => GreenBright,
        ReactorState.Alarm => AccentBright,
        ReactorState.Heating or ReactorState.Pressing => Amber,
        ReactorState.Done => Color.Parse("#7d8694"),
        _ => Color.Parse("#66666f")
    };

    private static readonly Typeface SansFace = new("Inter, Noto Sans SC");
    private static readonly Typeface MonoFace = new("JetBrains Mono, monospace");

    // —— 资源缓存：避免每帧 new 画笔/字体，削减 GC 与 UI 线程重绘尖峰（弱 GPU 上很关键）——
    private static readonly Dictionary<uint, SolidColorBrush> _brushCache = new();
    private static readonly Dictionary<(string, FontWeight), Typeface> _tfCache = new();

    /// <summary>按颜色取缓存的纯色画笔（只读复用，不可对外修改）。</summary>
    private static SolidColorBrush Brush(Color c)
    {
        uint k = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
        if (!_brushCache.TryGetValue(k, out var b)) { b = new SolidColorBrush(c); _brushCache[k] = b; }
        return b;
    }

    /// <summary>按字族+字重取缓存的字体（避免每次 FormattedText 重新解析字族）。</summary>
    private static Typeface Tf(FontFamily fam, FontWeight w = FontWeight.Normal)
    {
        var key = (fam.Name, w);
        if (!_tfCache.TryGetValue(key, out var t)) { t = new Typeface(fam, FontStyle.Normal, w); _tfCache[key] = t; }
        return t;
    }

    private static readonly Typeface InterFace = new("Inter");


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
        var glow = new Pen(new SolidColorBrush(Color.FromArgb(18, 0, 0, 0)), 9);
        ctx.DrawLine(glow, new Point(90, HdrY), new Point(1380, HdrY));
        var main = new Pen(new SolidColorBrush(HeaderLine), 4) { LineCap = PenLineCap.Round };
        ctx.DrawLine(main, new Point(90, HdrY), new Point(1380, HdrY));

        DrawText(ctx, "共用气路总管 · GAS HEADER HDR-01", 735, HdrY + 10,
            Color.Parse("#45454f"), 12, SansFace, TextAlignment.Center);
    }

    // ============ 单个反应釜 ============
    private void DrawReactor(DrawingContext ctx, Reactor c, int i)
    {
        double cx = Xs[i];
        bool flow = c.Valve && (c.State == ReactorState.React || c.State == ReactorState.Pressing);
        Color col = StColor(c);
        var lc = flow ? col : Color.FromArgb(64, 0, 0, 0);
        double lw = flow ? 3 : 2.2;

        // RV 标号 + 状态点（整组水平居中于釜中心 cx，与釜体/读数同一竖直中线）
        var rvFt = new FormattedText($"RV{c.Id}", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Tf(SansFace.FontFamily, FontWeight.Bold), 17, Brush(OnDark));
        const double dotR = 5, gap = 8;
        double groupW = dotR * 2 + gap + rvFt.Width;
        double gx = cx - groupW / 2;
        const double gcy = 15;
        ctx.DrawEllipse(new SolidColorBrush(col), null, new Point(gx + dotR, gcy), dotR, dotR);
        ctx.DrawText(rvFt, new Point(gx + dotR * 2 + gap, gcy - rvFt.Height / 2));

        // 气体消耗放釜顶（RV 标号下方），把卡片空间让给温度/压力大字
        if (c.State != ReactorState.Idle || Services.HardwareOptions.AnyReal)
            DrawText(ctx, $"{c.Gas:0.00} mmol", cx, 32, Body, 14, SansFace, TextAlignment.Center, FontWeight.SemiBold);

        // 釜体（平移 + 缩放到内部坐标系）
        using (ctx.PushTransform(
            Matrix.CreateScale(Scale, Scale) *
            Matrix.CreateTranslation(cx - 50 * Scale, RTop)))
        {
            DrawReactorInner(ctx, c);
        }

        // 釜下方读数：白底圆角"数据卡"把 温度/压力/气体/状态 框成一组（报警时整卡描红），
        // 数值+单位按整体居中、单位小一号底对齐，比裸文字堆叠整齐醒目。
        bool alarm = c.State == ReactorState.Alarm;
        var cardRect = new Rect(cx - 66, RBot + 4, 132, 102);
        var cardPen = new Pen(new SolidColorBrush(
            alarm ? Color.FromArgb(150, 224, 57, 76) : Color.FromArgb(34, 0, 0, 0)), alarm ? 1.8 : 1.2);
        ctx.DrawRectangle(Brush(Color.Parse("#ffffff")), cardPen, new RoundedRect(cardRect, 12));
        double cardT = RBot + 4;

        if (c.State != ReactorState.Idle || Services.HardwareOptions.AnyReal)
        {
            var tCol = c.State == ReactorState.React ? GreenBright : alarm ? AccentBright : OnDark;
            DrawValueUnit(ctx, $"{c.T:0.0}", "°C", cx, cardT + 6, tCol, 29, FontWeight.Light, 13.5);

            var pCol = alarm ? AccentBright : OnDark;
            DrawValueUnit(ctx, Models.Units.PValue(c.P), Models.Units.PLabel, cx, cardT + 48,
                pCol, 22, FontWeight.SemiBold, 12.5);

            // 搅拌为全局共用，转速统一显示在主界面「搅拌总控」卡片，这里只显示状态
            DrawText(ctx, c.StateZh, cx, cardT + 84, col, 12.5, SansFace,
                TextAlignment.Center, FontWeight.SemiBold);
        }
        else
        {
            DrawText(ctx, "通道停用", cx, cardT + 43, Color.Parse("#63636e"),
                13.5, SansFace, TextAlignment.Center);
        }

        // 釜 → CV → SV → 总管 的竖直管路（从卡片底沿接出）
        double dropTop = RBot + 106;
        var linePen = PipePen(lc, lw, flow);
        ctx.DrawLine(linePen, new Point(cx, dropTop), new Point(cx, CheckY - 12));
        DrawCheckValve(ctx, cx, CheckY);
        DrawText(ctx, $"CV{c.Id}", cx + 20, CheckY + 3 - 5.5, Color.Parse("#45454f"), 11, SansFace);
        ctx.DrawLine(linePen, new Point(cx, CheckY + 12), new Point(cx, ValY - 18));
        DrawCtrlValve(ctx, cx, ValY, c.Valve);
        DrawText(ctx, $"SV{c.Id}", cx + 23, ValY + 3.5 - 5.5, Color.Parse("#45454f"), 11, SansFace);
        ctx.DrawLine(linePen, new Point(cx, ValY + 18), new Point(cx, HdrY));

        // 命中区（SV 热区 72×72 设计单位，便于手指点按；相邻釜间距 150 不会重叠）
        _hits.Add(new Hit(new Rect(cx - 72, 0, 144, RBot + 108), "rv", c.Id));
        _hits.Add(new Hit(new Rect(cx - 36, ValY - 36, 72, 72), "sv", c.Id));
    }

    // 釜内部结构（内部坐标系：中心 x=50，高约 0–222）。
    // 样式按用户提供的 SVG：夹套圆底釜 + 顶置电机/加料漏斗 + 雪糕棍式横置平桨 + 向桨心微沉的单曲线液面。
    private void DrawReactorInner(DrawingContext ctx, Reactor c)
    {
        bool isOff = c.State == ReactorState.Idle;
        bool isErr = c.State == ReactorState.Alarm;
        bool isHeat = c.State == ReactorState.Heating;
        Color ink = isOff ? InkLight : (isErr ? InkErr : Ink);
        var inkBrush = Brush(ink);
        var inkPen = new Pen(inkBrush, 1.2);

        // 1. 加料漏斗（盖左上）：倒三角 + 竖管 + 盖上接口
        var funnel = new PolylineGeometry(new[] { new Point(20, 6), new Point(33, 6), new Point(26.5, 17) }, true);
        ctx.DrawGeometry(Brushes.White, inkPen, funnel);
        ctx.DrawLine(new Pen(inkBrush, 1.3), new Point(26.5, 17), new Point(26.5, 40));
        ctx.DrawRectangle(Brush(Color.Parse("#E9EDF0")), inkPen, new Rect(19.5, 40, 14, 4));
        ctx.DrawRectangle(Brushes.White, inkPen, new Rect(22.5, 44, 8, 8));

        // 2. 搅拌电机（盖中央）：减速箱 + 机身 + 散热线
        var ventPen = new Pen(Brush(Color.Parse("#6f7780")), 0.9);
        ctx.DrawRectangle(Brush(Color.Parse("#F2F4F6")), inkPen, new RoundedRect(new Rect(43, 14, 14, 11), 1.5));
        ctx.DrawLine(ventPen, new Point(45.5, 18), new Point(54.5, 18));
        ctx.DrawLine(ventPen, new Point(45.5, 21.5), new Point(54.5, 21.5));
        ctx.DrawRectangle(Brush(Color.Parse("#F2F4F6")), inkPen, new Rect(40, 25, 20, 21));
        ctx.DrawLine(ventPen, new Point(43, 32), new Point(57, 32));
        ctx.DrawLine(ventPen, new Point(43, 39), new Point(57, 39));

        // 3. 釜盖 + 两侧盖耳
        ctx.DrawRectangle(Brush(Color.Parse("#E9EDF0")), inkPen, new Rect(6, 41, 6, 5));
        ctx.DrawRectangle(Brush(Color.Parse("#E9EDF0")), inkPen, new Rect(88, 41, 6, 5));
        ctx.DrawRectangle(Brush(Color.Parse("#E9EDF0")), new Pen(inkBrush, 1.4),
            new RoundedRect(new Rect(2, 46, 96, 12), 2));

        // 4. 夹套外壁（直壁 + 圆底）：加热时暖色、停用虚线
        var jacket = new PathGeometry();
        using (var gc = jacket.Open())
        {
            gc.BeginFigure(new Point(6, 58), true);
            gc.LineTo(new Point(6, 182));
            gc.CubicBezierTo(new Point(6, 206), new Point(94, 206), new Point(94, 182));
            gc.LineTo(new Point(94, 58));
            gc.EndFigure(true);
        }
        IBrush jacketFill = isOff ? Brush(Color.Parse("#F6F7F8"))
            : isHeat ? Brush(Color.Parse("#F6E6C8")) : Brush(Color.Parse("#E9EDF0"));
        var jacketPen = isOff
            ? new Pen(inkBrush, 1.3) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) }
            : new Pen(inkBrush, 1.4);
        ctx.DrawGeometry(jacketFill, jacketPen, jacket);

        // 5. 内胆（白）
        var inner = new PathGeometry();
        using (var gc = inner.Open())
        {
            gc.BeginFigure(new Point(10.5, 58), true);
            gc.LineTo(new Point(10.5, 178));
            gc.CubicBezierTo(new Point(10.5, 198), new Point(89.5, 198), new Point(89.5, 178));
            gc.LineTo(new Point(89.5, 58));
            gc.EndFigure(true);
        }
        ctx.DrawGeometry(Brushes.White, new Pen(inkBrush, 1.1), inner);

        // 加热指示标签（加热=琥珀，停用=OFF）
        DrawText(ctx, isOff ? "OFF" : "HT", 13, 63,
            isHeat ? Color.Parse("#c97818") : Color.Parse("#6f7780"), 7.5, InterFace);

        // 6. 液体：液面为向桨心微沉的单曲线，液位随溶液体积
        double liqH = Math.Max(36, Math.Min(96, 30 + c.Vol * 10));
        double surfY = 178 - liqH;
        var (liqBody, liqRim) = StateLiquidColors(c.State);
        if (liqBody != Colors.Transparent)
        {
            var liquid = new PathGeometry();
            using (var gc = liquid.Open())
            {
                gc.BeginFigure(new Point(10.5, surfY), true);
                gc.QuadraticBezierTo(new Point(50, surfY + 8), new Point(89.5, surfY));
                gc.LineTo(new Point(89.5, 178));
                gc.CubicBezierTo(new Point(89.5, 198), new Point(10.5, 198), new Point(10.5, 178));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(Brush(liqBody), null, liquid);
            // 液面描边
            var surf = new PathGeometry();
            using (var gc = surf.Open())
            {
                gc.BeginFigure(new Point(10.5, surfY), false);
                gc.QuadraticBezierTo(new Point(50, surfY + 8), new Point(89.5, surfY));
                gc.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(Brush(liqRim), 1.6), surf);
        }

        // 7. 气泡（反应中：白圈 + 液色描边，自桨叶升向液面）
        if (c.State == ReactorState.React)
        {
            var bubblePen = new Pen(Brush(liqRim), 0.8);
            for (int k = 0; k < 3; k++)
            {
                double bx = 36 + k * 6, dur = 1.4 + k * 0.3;
                double tt = ((Phase / dur) + k * 0.3) % 1.0;
                double by = 164 - (164 - (surfY + 10)) * tt;
                double r = 1.8 + k * 0.4;
                double op = Math.Sin(tt * Math.PI);
                using (ctx.PushOpacity(Math.Max(0, op)))
                    ctx.DrawEllipse(Brushes.White, bubblePen, new Point(bx, by), r, r);
            }
        }

        // 8. 搅拌轴 + 雪糕棍式横置平桨（phase 水平缩放模拟旋转）
        ctx.DrawRectangle(Brushes.White, new Pen(inkBrush, 1.1), new Rect(47.5, 58, 5, 108));
        bool showStir = !(isOff || c.IsDone || c.Rpm == 0);
        double halfW = 19;
        if (showStir)
        {
            double t = (Phase / 0.9) % 1.0;
            double scaleX = 1 + (-0.9 - 1) * (1 - Math.Cos(t * Math.PI * 2)) / 2;
            halfW = 19 * Math.Abs(scaleX);
        }
        halfW = Math.Max(3, halfW);
        ctx.DrawRectangle(Brushes.White, new Pen(inkBrush, 1.2),
            new RoundedRect(new Rect(50 - halfW, 166, halfW * 2, 9), 4.5));

        // 9. 热电偶 TT（左壁，探入液位区）
        ctx.DrawRectangle(Brushes.White, inkPen, new Rect(-1, 148, 8, 8));
        ctx.DrawRectangle(Brushes.White, new Pen(inkBrush, 1), new Rect(-5, 146, 4, 12));
        ctx.DrawEllipse(Brushes.White, inkPen, new Point(-10, 152), 4.5, 4.5);
        DrawText(ctx, "TT", -14, 160, InkLight, 7, MonoFace);

        // 10. 压力变送器 PT（右壁）+ 报警脉冲圈
        ctx.DrawLine(new Pen(inkBrush, isErr ? 1.8 : 1.2), new Point(94, 72), new Point(101, 72));
        ctx.DrawEllipse(Brushes.White, new Pen(inkBrush, isErr ? 1.6 : 1.2), new Point(105, 72), 4.5, 4.5);
        DrawText(ctx, "PT", 100, 80, ink, 7, MonoFace, TextAlignment.Left, FontWeight.SemiBold);
        if (isErr)
        {
            double pr = 7 + 7 * (Math.Sin(Phase * Math.PI) * 0.5 + 0.5);
            double pop = 0.7 * (1 - (Math.Sin(Phase * Math.PI) * 0.5 + 0.5));
            using (ctx.PushOpacity(Math.Max(0, pop)))
                ctx.DrawEllipse(null, new Pen(Brush(InkErr), 1.4), new Point(105, 72), pr, pr);
        }

        // 11. 底部出料阀短管 + 出气线
        ctx.DrawRectangle(Brushes.White, new Pen(inkBrush, 1.2), new Rect(44, 200, 12, 13));
        var botPen = isOff
            ? new Pen(inkBrush, 1.4) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) }
            : new Pen(inkBrush, 1.6);
        ctx.DrawLine(botPen, new Point(50, 213), new Point(50, 222));
    }

    /// <summary>液体配色（新样式为纯色 + 深色液面描边）：body=液体、rim=液面曲线/气泡描边。</summary>
    private static (Color body, Color rim) StateLiquidColors(ReactorState st) => st switch
    {
        ReactorState.React => (Color.Parse("#6FBA79"), Color.Parse("#3E8A4C")),
        ReactorState.Pressing => (Color.Parse("#9fc0e8"), Color.Parse("#2d5ca0")),
        ReactorState.Heating => (Color.Parse("#f3d896"), Color.Parse("#c97818")),
        ReactorState.Alarm => (Color.Parse("#f0a8a0"), Color.Parse("#c1352a")),
        ReactorState.Done => (Color.Parse("#c3c8cf"), Color.Parse("#6b7079")),
        // 停用：中性水灰（真机上开机即停用，物料也应可见）
        _ => (Color.Parse("#d3dade"), Color.Parse("#9aa4ad"))
    };

    // 控制阀（方框 + 双三角，开启时绿色）
    private void DrawCtrlValve(DrawingContext ctx, double cx, double cy, bool open)
    {
        var fill = new SolidColorBrush(open ? Color.FromArgb(56, 132, 204, 22) : Color.FromArgb(22, 0, 0, 0));
        var stroke = new SolidColorBrush(open ? GreenBright : Color.FromArgb(96, 0, 0, 0));
        var pen = new Pen(stroke, 1.8);
        ctx.DrawRectangle(fill, pen, new RoundedRect(new Rect(cx - 18, cy - 18, 36, 36), 8));
        var g = new PathGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(new Point(cx - 10, cy - 10), false);
            gc.LineTo(new Point(cx, cy));
            gc.LineTo(new Point(cx - 10, cy + 10));
            gc.EndFigure(false);
            gc.BeginFigure(new Point(cx + 10, cy - 10), false);
            gc.LineTo(new Point(cx, cy));
            gc.LineTo(new Point(cx + 10, cy + 10));
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    // 单向阀（红色圆 + 三角）
    private void DrawCheckValve(DrawingContext ctx, double cx, double cy)
    {
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(26, 224, 57, 76)),
            new Pen(new SolidColorBrush(Color.FromArgb(107, 224, 57, 76)), 1.5),
            new Point(cx, cy), 12, 12);
        var tri = new PolylineGeometry(new[]
        {
            new Point(cx - 4.8, cy - 5.2), new Point(cx + 5.5, cy), new Point(cx - 4.8, cy + 5.2)
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
            var lc = g.On ? GreenBright : Color.FromArgb(64, 0, 0, 0);
            var pen = PipePen(lc, g.On ? 3 : 2.2, g.On);
            ctx.DrawLine(pen, new Point(x, HdrY), new Point(x, HdrY + 38));
            DrawCtrlValve(ctx, x, HdrY + 56, g.On);
            DrawText(ctx, g.Label, x, HdrY + 86 - 12, Body, 12, SansFace, TextAlignment.Center, FontWeight.SemiBold);
            // 进气阀间距 50，热区取 44 宽避免相邻重叠
            _hits.Add(new Hit(new Rect(x - 22, HdrY + 32, 44, 48), "gas", i));
        }
    }

    // ============ 排空阀 ============
    private void DrawVent(DrawingContext ctx)
    {
        var lc = VentOn ? AccentBright : Color.FromArgb(64, 0, 0, 0);
        var pen = PipePen(lc, VentOn ? 3 : 2.2, VentOn);
        ctx.DrawLine(pen, new Point(VentX, HdrY), new Point(VentX, HdrY + 38));
        // 排空阀沿用控制阀外形，但开启时是红色——用一个变体
        DrawVentValve(ctx, VentX, HdrY + 56, VentOn);
        DrawText(ctx, "排空 / 泄压", VentX, HdrY + 86 - 12, Body, 12, SansFace, TextAlignment.Center, FontWeight.SemiBold);
        _hits.Add(new Hit(new Rect(VentX - 36, HdrY + 20, 72, 72), "vent", 0));
    }

    private void DrawVentValve(DrawingContext ctx, double cx, double cy, bool open)
    {
        var fill = new SolidColorBrush(open ? Color.FromArgb(56, 224, 57, 76) : Color.FromArgb(22, 0, 0, 0));
        var stroke = new SolidColorBrush(open ? AccentBright : Color.FromArgb(96, 0, 0, 0));
        var pen = new Pen(stroke, 1.8);
        ctx.DrawRectangle(fill, pen, new RoundedRect(new Rect(cx - 18, cy - 18, 36, 36), 8));
        var g = new PathGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(new Point(cx - 10, cy - 10), false);
            gc.LineTo(new Point(cx, cy));
            gc.LineTo(new Point(cx - 10, cy + 10));
            gc.EndFigure(false);
            gc.BeginFigure(new Point(cx + 10, cy - 10), false);
            gc.LineTo(new Point(cx, cy));
            gc.LineTo(new Point(cx + 10, cy + 10));
            gc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    // ============ 文本辅助 ============

    /// <summary>「数值 + 单位」组合绘制：整体水平居中于 cx，单位小一号、与数值底部对齐。</summary>
    private void DrawValueUnit(DrawingContext ctx, string val, string unit, double cx, double top,
        Color valColor, double valSize, FontWeight valWeight, double unitSize)
    {
        var vf = new FormattedText(val, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Tf(SansFace.FontFamily, valWeight), valSize, Brush(valColor));
        var uf = new FormattedText(unit, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Tf(SansFace.FontFamily, FontWeight.Normal), unitSize, Brush(Body));
        const double gap = 5;
        double x = cx - (vf.Width + gap + uf.Width) / 2;
        ctx.DrawText(vf, new Point(x, top));
        ctx.DrawText(uf, new Point(x + vf.Width + gap, top + vf.Height - uf.Height - 3));
    }

    private void DrawText(DrawingContext ctx, string text, double x, double y, Color color,
        double size, Typeface face, TextAlignment align = TextAlignment.Left,
        FontWeight weight = FontWeight.Normal)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Tf(face.FontFamily, weight), size, Brush(color));
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
