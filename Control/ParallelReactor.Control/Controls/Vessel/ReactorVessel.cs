using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Core.Vessel;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Vessel;

/// <summary>
/// 反应釜线描（样稿 ui.reactorSVG + ui.fitReactors，RESTYLE-BRIEF §2「Vessel drawing」：去色）。自绘，viewBox 150 × 230 等比放进控件的框里（xMidYMid meet）。
/// <list type="bullet">
/// <item>轮廓 = 状态的 line 色（报警 v-line-alarm、离线 v-detail、未安装 line，其余 v-line），细节（进气、电机盒、出料口、测温探头、压力表、搅拌轴、桨）= v-detail；</item>
/// <item>液体：未运行 v-liq、运行中 v-liq-run、报警 v-liq-alarm（液面线同轮廓），离线灰斜纹（bg-sunken + line-soft，6 单位一格、45°），未安装只画虚线轮廓；
///   液位 = 体积 / 釜容积（<see cref="Level"/>，夹在 0.04 … 1）；</item>
/// <item>夹套 = 加热器：加热中实线 + 单色火焰（t-2）+ 「HT」（t-1 600），降温中长虚线 7 / 3 + 雪花，其它短虚线 4 / 4（细节色）、写「OFF」；</item>
/// <item>反应中三个气泡；运行中且搅拌在转时桨来回缩放（只在完整线描里动）；大号（<see cref="VesselSize.Large"/>）超压时压力表画红圈 + 脉冲；</item>
/// <item>角标 HT / OFF / TT / PT：按控件的实际尺寸换算成 13.5 px（大号 14 px），太小时依次不写（<see cref="VesselLabels"/>：k &lt; 0.85 不写 OFF，
///   k &lt; 0.72 什么都不写，字号永远 ≥ 13 px）。自绘文字经 <see cref="IAuditText"/> 报给截图审计；</item>
/// <item>线宽：card / compact 1.5 px（细节 1.25、液面 1）不随尺寸变化；large 照样稿随 k 缩放（1.5 × k）。简化釜体（<see cref="Simple"/>，
///   宽 &lt; 60 px 时用）只画釜身、夹套、液体、搅拌轴和桨。</item>
/// </list>
/// 状态来自 Core 的 <see cref="StateKey"/>（ReactorVm.Key：未安装 / 离线 / 自己的状态）+ 标志（加热、冷却、搅拌）+ 液位，颜色全部取自 StateStyles / Palette。
/// 动画共用 <see cref="VesselClock"/>（R10），冻结（Theme/Frozen）时不动。M5 反应釜页的引线标注用 <see cref="AnchorPoint"/> / <see cref="CalloutPoint"/>。
/// 测量：给定的框里最大的 150 : 230 的矩形（同 Image 的 Uniform）；页面一般给定宽高（样稿 .overview-vessel 36 × 56 等），图在框里居中。
/// </summary>
public sealed class ReactorVessel : Avalonia.Controls.Control, IAuditText
{
    public static readonly StyledProperty<StateKey> KeyProperty =
        AvaloniaProperty.Register<ReactorVessel, StateKey>(nameof(Key), StateKey.Idle);
    public static readonly StyledProperty<bool> HeatingProperty = AvaloniaProperty.Register<ReactorVessel, bool>(nameof(Heating));
    public static readonly StyledProperty<bool> CoolingProperty = AvaloniaProperty.Register<ReactorVessel, bool>(nameof(Cooling));
    public static readonly StyledProperty<bool> StirringProperty = AvaloniaProperty.Register<ReactorVessel, bool>(nameof(Stirring));
    public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<ReactorVessel, double>(nameof(Level), double.NaN);
    public static readonly StyledProperty<VesselSize> SizeProperty = AvaloniaProperty.Register<ReactorVessel, VesselSize>(nameof(Size));
    public static readonly StyledProperty<bool> SimpleProperty = AvaloniaProperty.Register<ReactorVessel, bool>(nameof(Simple));
    public static readonly StyledProperty<bool> ShowLabelsProperty = AvaloniaProperty.Register<ReactorVessel, bool>(nameof(ShowLabels), true);
    public static readonly StyledProperty<bool> ShowTagLabelsProperty = AvaloniaProperty.Register<ReactorVessel, bool>(nameof(ShowTagLabels), true);

    static ReactorVessel()
    {
        AffectsRender<ReactorVessel>(KeyProperty, HeatingProperty, CoolingProperty, StirringProperty, LevelProperty,
            SizeProperty, SimpleProperty, ShowLabelsProperty, ShowTagLabelsProperty);
    }

    public ReactorVessel() => _look = VesselLook.Of(StateKey.Idle, false, false, false, double.NaN);

    /// <summary>界面上的状态（ReactorVm.Key）：未安装 = 只画虚线轮廓；离线 = 灰、斜纹液体、不画加热 / 冷却 / 气泡；其余按状态上色。</summary>
    public StateKey Key { get => GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    /// <summary>加热中（ReactorVm.Heating）：实线夹套 + 火焰 + HT。断线 / 未安装时不画。</summary>
    public bool Heating { get => GetValue(HeatingProperty); set => SetValue(HeatingProperty, value); }
    /// <summary>冷却水降温中（演示数据才有；ReactorVm.Cooling）：长虚线夹套 + 雪花。</summary>
    public bool Cooling { get => GetValue(CoolingProperty); set => SetValue(CoolingProperty, value); }
    /// <summary>搅拌在转（有效转速 &gt; 0；ReactorVm.Stirring）。只在运行类状态下桨才动。</summary>
    public bool Stirring { get => GetValue(StirringProperty); set => SetValue(StirringProperty, value); }
    /// <summary>液位 = 体积 / 釜容积（ReactorVm.VesselLevel，<see cref="VesselAnchors.Level(double?, Core.Models.ModelProfile?)"/>）；NaN = 默认 10 mL / 50 mL。</summary>
    public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    /// <summary>尺寸档：card / compact（线宽固定、角标 13.5 px）或 large（M5 反应釜页：线宽随缩放、角标 14 px、超压红圈）。</summary>
    public VesselSize Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>简化釜体（样稿 simple：宽 &lt; 60 px 的小卡片）：不画电机盒、压力表、测温探头、出料口、角标、火焰、气泡，桨不动。</summary>
    public bool Simple { get => GetValue(SimpleProperty); set => SetValue(SimpleProperty, value); }
    /// <summary>写角标（HT / OFF / TT / PT）的总开关；即使为 true，太小时也按 <see cref="VesselLabels"/> 的规则不写。</summary>
    public bool ShowLabels { get => GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }
    /// <summary>写 TT / PT（反应釜页的标注框已经写了：false，同样稿 reactor.css 的 .reactor-vbox .rv-tt / .rv-pt）。</summary>
    public bool ShowTagLabels { get => GetValue(ShowTagLabelsProperty); set => SetValue(ShowTagLabelsProperty, value); }

    private VesselLook _look;

    /// <summary>此刻的外观（颜色、液面、夹套、搅拌、气泡）。</summary>
    public VesselLook Look => _look;

    /// <summary>viewBox → 控件的换算（k 与居中偏移）。</summary>
    public VesselFit Fit => VesselGeometry.FitIn(Bounds.Size);

    /// <summary>液面的 y（viewBox 单位）。</summary>
    public double LevelY => _look.LevelY;

    /// <summary>此刻写哪些角标（按当前尺寸）。</summary>
    public VesselLabels Labels => VesselLabels.Of(_look, Fit.K, Simple, ShowLabels, ShowTagLabels);

    /// <summary>引线锚点（ui.REACTOR_ANCHORS）在控件里的坐标（设计 px，相对控件左上角）。</summary>
    public Point AnchorPoint(VesselAnchor a) => Fit.ToBox(VesselAnchors.Of(a, _look.LevelY)).ToPoint();

    /// <summary>反应釜页标注框的锚点（SV / HT / TT / M / PT / LV）在控件里的坐标。</summary>
    public Point CalloutPoint(VesselCallout c) => Fit.ToBox(VesselAnchors.Of(c, _look.LevelY)).ToPoint();

    // ---------------- 状态 → 外观；动画订阅 ----------------

    private bool _attached, _animating;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == KeyProperty || p == HeatingProperty || p == CoolingProperty || p == StirringProperty || p == LevelProperty)
        {
            _look = VesselLook.Of(Key, Heating, Cooling, Stirring, Level);
            UpdateAnim();
        }
        else if (p == SizeProperty || p == SimpleProperty || p == IsVisibleProperty || p == Frozen.IsFrozenProperty)
            UpdateAnim();
        else if (p == TextElement.FontFamilyProperty)
            ClearLayouts();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateAnim();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        UpdateAnim();
        ClearLayouts();
    }

    /// <summary>有东西要动（桨、气泡、超压脉冲）。</summary>
    private bool WantsMotion => (!Simple && (_look.Spin || _look.Bubbles)) || (Size == VesselSize.Large && _look.Alarm);

    private void UpdateAnim()
    {
        bool on = _attached && IsVisible && !Frozen.GetIsFrozen(this) && WantsMotion;
        if (on == _animating) return;
        _animating = on;
        VesselClock.Set(this, on);
        InvalidateVisual();
    }

    // ---------------- 测量 ----------------

    /// <summary>框里最大的 150 : 230 的矩形；两边都不限时按 viewBox 原尺寸（k = 1）。</summary>
    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
    {
        double w = availableSize.Width, h = availableSize.Height;
        bool fw = !double.IsInfinity(w), fh = !double.IsInfinity(h);
        double k = fw && fh ? Math.Min(w / VesselAnchors.ViewW, h / VesselAnchors.ViewH)
            : fw ? w / VesselAnchors.ViewW : fh ? h / VesselAnchors.ViewH : 1;
        return new Avalonia.Size(VesselAnchors.ViewW * k, VesselAnchors.ViewH * k);
    }

    // ---------------- 绘制 ----------------

    public override void Render(DrawingContext context)
    {
        var fit = Fit;
        double k = fit.K;
        if (!(k > 1e-3)) return;
        var L = _look;
        bool large = Size == VesselSize.Large, simple = Simple;
        // non-scaling 的线（card / compact）：在 viewBox 变换下画，线宽 ÷ k；large 的线照样稿随 k 缩放
        double u = large ? 1 : 1 / k;
        double w1 = 1.5, w2 = large ? 1.5 : 1.25;
        double t = _animating ? VesselClock.Now : double.NaN;

        using (context.PushTransform(fit.ToMatrix()))
        {
            if (L.Uninstalled)
            {
                // 未安装：只画虚线轮廓（釜盖 + 釜身，line 色，4 / 4，圆线帽）
                context.DrawGeometry(null, VesselPaint.Pen(L.Line, w2, "4 4", PenLineCap.Round, PenLineJoin.Round, u), VesselPaint.Lid);
                context.DrawGeometry(null, VesselPaint.Pen(L.Line, w1, "4 4", PenLineCap.Round, PenLineJoin.Round, u), VesselPaint.Body);
                return;
            }
            DrawBody(context, L, k, u, w1, w2, large, simple, t);
        }
        var runs = LabelRuns();
        for (int i = 0; i < runs.Count; i++) runs[i].Layout.Draw(context, runs[i].Origin);
    }

    private void DrawBody(DrawingContext ctx, VesselLook L, double k, double u, double w1, double w2, bool large, bool simple, double t)
    {
        const PenLineCap Round = PenLineCap.Round, Flat = PenLineCap.Flat;
        const PenLineJoin RJ = PenLineJoin.Round, Miter = PenLineJoin.Miter;

        // 1. 细节（g.rv-detail：细节色，圆线帽 / 圆拐角）+ 夹套
        var detail = VesselPaint.Pen(L.Detail, w2, null, Round, RJ, u);
        if (!simple)
        {
            ctx.DrawGeometry(null, detail, VesselPaint.Inlet);
            ctx.DrawGeometry(VesselPaint.Brush(Palette.BgSideColor), detail, VesselPaint.Motor);
            ctx.DrawGeometry(null, detail, VesselPaint.MotorLines);
            ctx.DrawGeometry(null, detail, VesselPaint.Outlet);
            ctx.DrawGeometry(null, detail, VesselPaint.Probe);
            ctx.DrawGeometry(null, detail, VesselPaint.Gauge);
        }
        var jacket = L.Jacket switch
        {
            JacketStyle.Cooling => VesselPaint.Pen(L.Line, w1, "7 3", Round, RJ, u),
            JacketStyle.Heating => VesselPaint.Pen(L.Line, w1, null, Round, RJ, u),
            _ => VesselPaint.Pen(L.Detail, w1, "4 4", Round, RJ, u),
        };
        ctx.DrawGeometry(null, jacket, VesselPaint.Jacket);

        // 2. 液体 + 3. 液面线（card / compact 1 px，large 1.2）
        var (liquid, rim) = VesselPaint.Level(L.LevelY);
        if (L.Hatch) DrawHatch(ctx, liquid, L.LevelY);
        else if (L.Liquid is { } lc) ctx.DrawGeometry(VesselPaint.Brush(lc), null, liquid);
        ctx.DrawGeometry(null, VesselPaint.Pen(L.LiquidLine, large ? 1.2 : 1, null, Flat, Miter, u), rim);

        // 4. 轮廓（g.rv-outline）：釜盖填 bg-sunken，釜身 1.5
        ctx.DrawGeometry(VesselPaint.Brush(Palette.BgSunkenColor), VesselPaint.Pen(L.Line, w2, null, Round, RJ, u), VesselPaint.Lid);
        ctx.DrawGeometry(null, VesselPaint.Pen(L.Line, w1, null, Round, RJ, u), VesselPaint.Body);

        // 5. 气泡（反应中；bg-card 填充、液面线色 1 单位描边 —— 样稿的气泡没有 non-scaling-stroke，线宽随 k）
        if (L.Bubbles && !simple) DrawBubbles(ctx, L, t);

        // 6. 搅拌轴 7. 桨（bg-card 填充；转的时候 scaleX 1 → .25 → 1，绕桨的中心）
        ctx.DrawGeometry(null, VesselPaint.Pen(L.Detail, w2, null, Flat, Miter, u), simple ? VesselPaint.ShaftSimple : VesselPaint.Shaft);
        double sx = !double.IsNaN(t) && L.Spin && !simple ? VesselMotion.PaddleScale(t) : 1;
        var pr = VesselPaint.Paddle;
        double pw = pr.Width * sx;
        ctx.DrawRectangle(VesselPaint.Brush(Palette.BgCardColor), VesselPaint.Pen(L.Detail, w2, null, Flat, Miter, u),
            new Rect(pr.Center.X - pw / 2, pr.Y, pw, pr.Height), VesselPaint.PaddleRadius * sx, VesselPaint.PaddleRadius);

        // 8. 降温：雪花（轮廓色 1.5，圆线帽）
        if (L.Cooling) ctx.DrawGeometry(null, VesselPaint.Pen(L.Line, 1.5, null, Round, Miter, u), VesselPaint.Snow);

        // 9. 超压红圈：只在大号釜体的压力表上（卡片上的小釜体只用浅红液体）
        if (large && L.Alarm)
        {
            ctx.DrawGeometry(null, VesselPaint.Pen(Palette.RColor, 2.4), VesselPaint.PtRing);
            var (s, op) = double.IsNaN(t) ? VesselMotion.PulseStill : VesselMotion.Pulse(t);
            if (op > 0.01)
                using (ctx.PushOpacity(op))
                using (ctx.PushTransform(Matrix.CreateTranslation(-132, -76) * Matrix.CreateScale(s, s) * Matrix.CreateTranslation(132, 76)))
                    ctx.DrawGeometry(null, VesselPaint.Pen(Palette.RColor, 2), VesselPaint.PtRing);
        }

        // 10. 火焰（加热中；图标 flame 18 × 18 放在 (11, 88)，t-2，描边恒 1.6 px）
        if (!simple && VesselLabels.Of(L, k, simple, ShowLabels, ShowTagLabels).Flame) DrawFlame(ctx, k);
    }

    /// <summary>
    /// 离线液体的斜纹（ui.js #pr-hatch：6 × 6 的 userSpaceOnUse 图案转 45°，bg-sunken 底 + line-soft 竖线 2.4 宽 —— 图案格裁掉一半，看得见的条纹宽 1.2）。
    /// 条纹中心在 x + y = √2 · (6n + 0.6) 上，方向「/」；单位是 viewBox（随 k 缩放，同样稿）。
    /// </summary>
    private static void DrawHatch(DrawingContext ctx, Geometry liquid, double levelY)
    {
        ctx.DrawGeometry(VesselPaint.Brush(Palette.BgSunkenColor), null, liquid);
        using (ctx.PushGeometryClip(liquid))
        {
            var pen = VesselPaint.Pen(Palette.LineSoftColor, 1.2);
            double step = 6 * Math.Sqrt(2), off = 0.6 * Math.Sqrt(2);
            double y0 = levelY - 4, y1 = 207;
            int n0 = (int)Math.Floor((40 + y0 - off) / step) - 1, n1 = (int)Math.Ceiling((110 + y1 - off) / step) + 1;
            for (int n = n0; n <= n1; n++)
            {
                double c = n * step + off;
                ctx.DrawLine(pen, new Point(c - y1, y1), new Point(c - y0, y0));
            }
        }
    }

    private static void DrawBubbles(DrawingContext ctx, VesselLook L, double t)
    {
        var fill = VesselPaint.Brush(Palette.BgCardColor);
        var pen = VesselPaint.Pen(L.LiquidLine, 1);
        for (int i = 0; i < VesselPaint.Bubbles.Length; i++)
        {
            var (cx, dy, r) = VesselPaint.Bubbles[i];
            var (my, op) = double.IsNaN(t) ? (0d, 1d) : VesselMotion.Bubble(t, i);
            if (op <= 0.01) continue;
            var c = new Point(cx, L.LevelY + dy + my);
            if (op >= 0.999) ctx.DrawEllipse(fill, pen, c, r, r);
            else
                using (ctx.PushOpacity(op))
                    ctx.DrawEllipse(fill, pen, c, r, r);
        }
    }

    private static Geometry? _flame;

    private void DrawFlame(DrawingContext ctx, double k)
    {
        if (_flame == null && this.TryFindResource("Icon.flame", out var g) && g is Geometry geo) _flame = geo;
        if (_flame == null) return;
        double s = VesselPaint.FlameSize / 24;
        using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(VesselPaint.FlameX, VesselPaint.FlameY)))
            ctx.DrawGeometry(null, VesselPaint.Pen(Palette.T2Color, 1.6, null, PenLineCap.Flat, PenLineJoin.Miter, 1 / (s * k)), _flame);
    }

    // ---------------- 角标（控件坐标里排版，字号固定 13.5 / 14 px） ----------------

    private readonly record struct LabelRun(TextLayout Layout, Point Origin, AuditTextRun Audit);

    private readonly List<LabelRun> _runs = new();
    private (Avalonia.Size Box, VesselLook Look, VesselSize Size, bool Simple, bool Show, bool Tags, FontFamily Family)? _runsKey;
    private readonly Dictionary<(string Text, double Px, FontWeight Weight, FontFamily Family), TextLayout> _layouts = new();

    /// <summary>
    /// 这一帧要写的角标：HT / OFF 右端对齐 (71, 62)、dominant-baseline hanging（WebKit：基线在 y + 0.8 × ascent）；
    /// TT 起点 (4, 171)、PT 起点 (124, 97)，字母基线。字号固定（不随 k），颜色 t-3，HT 加热中 t-1 600。
    /// </summary>
    private IReadOnlyList<LabelRun> LabelRuns()
    {
        var family = TextElement.GetFontFamily(this);
        var key = (Bounds.Size, _look, Size, Simple, ShowLabels, ShowTagLabels, family);
        if (_runsKey == key) return _runs;
        _runsKey = key;
        _runs.Clear();
        var fit = VesselGeometry.FitIn(Bounds.Size);
        var lb = VesselLabels.Of(_look, fit.K, Simple, ShowLabels, ShowTagLabels);
        if (!lb.AnyText) return _runs;
        double px = VesselLabels.Px(Size);
        if (lb.Ht || lb.Off)
        {
            bool on = lb.Ht;
            var text = on ? "HT" : "OFF";
            var tl = Layout(text, px, on ? FontWeight.SemiBold : FontWeight.Normal, family);
            var a = fit.ToBox(new Point(71, 62));
            double asc = Ascent(tl);
            Add(text, tl, new Point(a.X - tl.WidthIncludingTrailingWhitespace, a.Y + 0.8 * asc - asc), on ? Palette.T1Color : Palette.T3Color);
        }
        if (lb.Tt) AddStart("TT", fit.ToBox(new Point(4, 171)));
        if (lb.Pt) AddStart("PT", fit.ToBox(new Point(124, 97)));
        return _runs;

        void AddStart(string text, Point baseline)
        {
            var tl = Layout(text, px, FontWeight.Normal, family);
            Add(text, tl, new Point(baseline.X, baseline.Y - Ascent(tl)), Palette.T3Color);
        }

        void Add(string text, TextLayout tl, Point origin, Color fg) =>
            _runs.Add(new LabelRun(tl, origin, new AuditTextRun(text, px,
                new Rect(origin, new Avalonia.Size(tl.WidthIncludingTrailingWhitespace, tl.Height)), fg)));
    }

    /// <summary>首行基线到行顶的距离（默认行高时 = 字体的 ascent）。</summary>
    private static double Ascent(TextLayout tl) => tl.TextLines.Count > 0 ? tl.TextLines[0].Baseline : tl.Height * 0.8;

    private TextLayout Layout(string text, double px, FontWeight weight, FontFamily family)
    {
        var key = (text, px, weight, family);
        if (_layouts.TryGetValue(key, out var tl)) return tl;
        var fg = weight == FontWeight.SemiBold ? Palette.T1Brush : Palette.T3Brush;
        tl = new TextLayout(text, new Typeface(family, FontStyle.Normal, weight), px, fg);
        _layouts[key] = tl;
        return tl;
    }

    private void ClearLayouts()
    {
        foreach (var l in _layouts.Values) l.Dispose();
        _layouts.Clear();
        _runs.Clear();
        _runsKey = null;
    }

    /// <summary>截图审计：此刻写着的角标（字号、颜色、在控件里的矩形）。</summary>
    public IEnumerable<AuditTextRun> AuditTexts
    {
        get
        {
            foreach (var r in LabelRuns()) yield return r.Audit;
        }
    }
}
