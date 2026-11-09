using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 时间轴一行的轨道（样稿 .pr-tl-track + tlBar，css/shell.css「时间轴」）：自绘，一行一个控件（R10：24 行 × 每秒刷新不建子控件）。
/// 画的顺序同样稿的层叠：网格竖线（t-2 7 %）与「现在」线（1.5 px，t-3 55 %）在下面，程序段盖在上面（段名不会被划掉；当前段填充的末端标出「现在」），
/// 行尾的「11:54 ›」在最上面。段：上 7、高 22、1 px line-2 边、白底；已完成 g-2 → g-3；当前段填充 g-2 → g-4 + 末端 1 px g-dot-line（运行中呼吸）；
/// 选中行的当前段 a-tab 边；报警暂停灰色停住（line 边、bg-press 填充）；等待进气白 / bg-sunken 斜纹；中止 bg-sunken / line-soft 斜纹 + line 边。
/// 段宽放得下才写名字（15 px，当前段 t-1 600，其余 t-2）：先「反应 · 当前」，放不下写「反应」，再放不下不写（不出现「充…」）。
/// 断线设备（样稿 .is-off：grayscale + 75 % 不透明）：绿色换成色板里最接近的灰（bg-head / bg-panel / bg-sunken / n-dot）。
/// 自绘的文字经 <see cref="IAuditText"/> 报给截图审计。
/// </summary>
public sealed class TimelineTrack : Avalonia.Controls.Control, IAuditText
{
    public static readonly StyledProperty<TlRowVm?> RowProperty = AvaloniaProperty.Register<TimelineTrack, TlRowVm?>(nameof(Row));
    /// <summary>不是反应釜行（合并的未安装行）：只画网格线和「现在」线。</summary>
    public static readonly StyledProperty<TimelineVm?> TimelineProperty = AvaloniaProperty.Register<TimelineTrack, TimelineVm?>(nameof(Timeline));

    public TlRowVm? Row { get => GetValue(RowProperty); set => SetValue(RowProperty, value); }
    public TimelineVm? Timeline { get => GetValue(TimelineProperty); set => SetValue(TimelineProperty, value); }

    public const double SegTop = 7, SegH = 22, MoreW = 58, LabelPad = 8, LabelFs = 15, MoreFs = 13;

    private static readonly IBrush VLineBrush = new ImmutableSolidColorBrush(Palette.WithAlpha(Palette.T2Color, 0.07));
    private static readonly IBrush NowBrush = new ImmutableSolidColorBrush(Palette.WithAlpha(Palette.T3Color, 0.55));
    private static readonly IBrush MoreBg = new ImmutableSolidColorBrush(Palette.WithAlpha(Palette.BgCardColor, 0.92));

    private TimelineVm? _tl;
    private TlRowVm? _row;
    private bool _pulsing, _attached;
    private readonly List<(Rect Box, string Tip)> _hits = new();
    private readonly List<AuditTextRun> _texts = new();

    public IEnumerable<AuditTextRun> AuditTexts => _texts;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowProperty || change.Property == TimelineProperty) Hook();
        else if (change.Property == Frozen.IsFrozenProperty || change.Property == IsVisibleProperty) UpdatePulse();
    }

    private void Hook()
    {
        if (_row != null) _row.PropertyChanged -= OnRow;
        if (_tl != null) _tl.WindowChanged -= OnWindow;
        _row = _attached ? Row : null;
        _tl = _attached ? Row?.Tl ?? Timeline : null;
        if (_row != null) _row.PropertyChanged += OnRow;
        if (_tl != null) _tl.WindowChanged += OnWindow;
        UpdatePulse();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Hook();
        ClearLayouts();
    }

    private void OnRow(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TlRowVm.Segments) or nameof(TlRowVm.MoreText) or nameof(TlRowVm.IsSelected) or nameof(TlRowVm.Offline))
        {
            UpdatePulse();
            InvalidateVisual();
        }
    }

    private void OnWindow(object? sender, EventArgs e) => InvalidateVisual();

    private void UpdatePulse()
    {
        bool run = false;
        if (_row != null) foreach (var s in _row.Segments) if (s.Run) { run = true; break; }
        bool on = run && _attached && IsVisible && !Frozen.GetIsFrozen(this);
        if (on == _pulsing) return;
        _pulsing = on;
        PulseClock.Set(this, on);
        InvalidateVisual();
    }

    private Typeface Face(FontWeight w) => new(TextElement.GetFontFamily(this), FontStyle.Normal, w);

    // 段名 / 行尾时刻的排版缓存（R10）：同一段文字只排一次（判断放不放得下、画都用它）。呼吸动画每 100 ms 重画一次，
    // 不缓存时每段每帧要新建 3 个 TextLayout。键含字体，换字体（PR_CONTROL_UI_FONT、系统字体审计）自然失效；超过 64 个就整个清掉。
    private readonly Dictionary<(string Text, double Fs, FontWeight W, IBrush Fg, FontFamily Family), TextLayout> _layouts = new();

    private TextLayout Layout(string text, double fs, FontWeight w, IBrush fg)
    {
        var key = (text, fs, w, fg, TextElement.GetFontFamily(this));
        if (_layouts.TryGetValue(key, out var tl)) return tl;
        if (_layouts.Count >= 64) ClearLayouts();
        tl = new TextLayout(text, Face(w), fs, fg);
        _layouts[key] = tl;
        return tl;
    }

    private void ClearLayouts()
    {
        foreach (var l in _layouts.Values) l.Dispose();
        _layouts.Clear();
    }

    /// <summary>样稿 fits：段的可用宽度 ≥ max(40, 文字宽 + 18)（左右内边距 8 + 边框 1）。</summary>
    private bool Fits(string text, FontWeight w, double px) =>
        px >= 40 && px >= Math.Max(40, Layout(text, LabelFs, w, Palette.T2Brush).WidthIncludingTrailingWhitespace + 18);

    public override void Render(DrawingContext context)
    {
        _hits.Clear();
        _texts.Clear();
        var tl = _tl;
        double W = Bounds.Width, H = Bounds.Height;
        if (tl == null || W <= 0 || H <= 0) return;
        double span = (tl.X1 - tl.X0).TotalMilliseconds;
        if (span <= 0) return;
        double Pct(DateTime t) => (t - tl.X0).TotalMilliseconds / span;

        using var clip = context.PushClip(new Rect(Bounds.Size));
        // 网格竖线与「现在」线（在程序段下面）
        foreach (var x in tl.Lines) context.FillRectangle(VLineBrush, new Rect(Math.Round(x * W), 0, 1, H));
        double nowX = Pct(tl.Now) * W;
        context.FillRectangle(NowBrush, new Rect(nowX - 0.75, 0, 1.5, H));

        var row = _row;
        if (row == null || row.Segments.Count == 0) return;
        bool off = row.Offline, sel = row.IsSelected;
        double moreW = row.MoreText != null ? MoreW : 0;
        foreach (var s in row.Segments)
        {
            var a = s.T0 > tl.X0 ? s.T0 : tl.X0;
            var b = s.T1 < tl.X1 ? s.T1 : tl.X1;
            if (b <= a) continue;
            double l = Pct(a), wd = Pct(b) - l;
            double x0 = Math.Round(l * W), x1 = Math.Round((l + wd) * W);
            if (x1 - x0 < 1) x1 = x0 + 1;
            var box = new Rect(x0, SegTop, x1 - x0, SegH);
            var inner = box.Deflate(1);
            DrawSegBg(context, s, inner, off);
            // 当前段的进度填充（到「现在」为止）
            if (s.State == SegmentState.Cur && tl.Now > s.T0)
            {
                var now = tl.Now < b ? tl.Now : b;
                double f = Math.Clamp((now - a).TotalMilliseconds / (b - a).TotalMilliseconds, 0, 1);
                double fw = Math.Round(inner.Width * f);
                if (fw >= 1)
                {
                    var fr = new Rect(inner.X, inner.Y, fw, inner.Height);
                    if (s.Held)
                    {
                        context.FillRectangle(Palette.BgPressBrush, fr);
                        context.FillRectangle(Palette.LineBrush, new Rect(fr.Right - 1, fr.Y, 1, fr.Height));
                    }
                    else
                    {
                        context.FillRectangle(off ? OffRunFill : ProgressPr.RunFill, fr);
                        context.FillRectangle(off ? Palette.NDotBrush : Palette.GDotLineBrush, new Rect(fr.Right - 1, fr.Y, 1, fr.Height));
                        if (_pulsing && s.Run) context.FillRectangle(PulseClock.Overlay, fr);
                    }
                }
            }
            var border = s.Held || s.State == SegmentState.Abort ? Palette.LineColor
                : sel && s.State == SegmentState.Cur ? Palette.ATabColor
                : off ? Palette.LineSoftColor : Palette.Line2Color;
            context.DrawRectangle(null, Palette.Pen(border, 1), box.Deflate(0.5));

            // 段名：放得下才写（先带标记，再只写名字）
            double px = Math.Min(wd * W, W - moreW - l * W);
            bool cur = s.State == SegmentState.Cur;
            var weight = cur ? FontWeight.SemiBold : FontWeight.Normal;
            string? text = Fits(s.Full, weight, px) ? s.Full : Fits(s.Name, weight, px) ? s.Name : null;
            if (text != null)
            {
                var fg = cur ? Palette.T1Brush : Palette.T2Brush;
                var layout = Layout(text, LabelFs, weight, fg);
                var tr = new Rect(inner.X + LabelPad, inner.Y + (inner.Height - layout.Height) / 2, layout.WidthIncludingTrailingWhitespace, layout.Height);
                // 断线设备：段（连字）75 % 不透明（样稿 .pr-tl-group.is-off .pr-tl-seg { opacity: .75 }）
                using (context.PushClip(inner))
                using (context.PushOpacity(off ? 0.75 : 1))
                    layout.Draw(context, tr.TopLeft);
                _texts.Add(new AuditTextRun(text, LabelFs, tr, ((ISolidColorBrush)fg).Color));
            }
            _hits.Add((box, s.Tip));
        }

        // 行尾「11:54 ›」：程序在窗口右边之外结束（运行中 / 暂停）
        if (row.MoreText is { } more)
        {
            var layout = Layout(more, MoreFs, FontWeight.Normal, Palette.T3Brush);
            double tw = layout.WidthIncludingTrailingWhitespace, bw = 6 + tw + 2 + 13 + 4;
            var box = new Rect(Math.Round(W - 4 - bw), 9, Math.Round(bw), 18);
            context.DrawRectangle(MoreBg, null, box, 4, 4);
            var tr = new Rect(box.X + 6, box.Y + (box.Height - layout.Height) / 2, tw, layout.Height);
            layout.Draw(context, tr.TopLeft);
            _texts.Add(new AuditTextRun(more, MoreFs, tr, Palette.T3Color));
            if (Chevron is { } geo)
            {
                double s = 13 / 24.0, ix = box.X + 6 + tw + 2, iy = box.Y + (box.Height - 13) / 2;
                using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ix, iy)))
                    context.DrawGeometry(null, Palette.Pen(Palette.T3Color, 1.6 / s, cap: PenLineCap.Round), geo);
            }
            _hits.Add((box, row.MoreTip ?? more));
        }
    }

    /// <summary>断线设备的「当前段填充」：绿色渐变换成灰（bg-panel → bg-sunken）。</summary>
    private static readonly IBrush OffRunFill = Gradient(Palette.BgPanelColor, Palette.BgSunkenColor);
    private static readonly IBrush OffDoneFill = Gradient(Palette.BgHeadColor, Palette.BgPanelColor);

    private static IBrush Gradient(Color a, Color b) => new ImmutableLinearGradientBrush(
        new[] { new ImmutableGradientStop(0, a), new ImmutableGradientStop(1, b) },
        startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));

    private static void DrawSegBg(DrawingContext context, TlSeg s, Rect inner, bool off)
    {
        if (s.State == SegmentState.Done) { context.FillRectangle(off ? OffDoneFill : ProgressPr.DoneFill, inner); return; }
        if (s.State == SegmentState.Abort) { Hatch(context, inner, Palette.BgSunkenColor, Palette.LineSoftColor); return; }
        if (s.Wait) { Hatch(context, inner, Palette.BgCardColor, Palette.BgSunkenColor); return; }
        context.FillRectangle(Palette.BgCardBrush, inner);
    }

    /// <summary>135° 斜纹（样稿 repeating-linear-gradient(135deg, a 0 5px, b 5px 10px)：条纹「/」方向，宽 5、周期 10）。</summary>
    private static void Hatch(DrawingContext context, Rect r, Color a, Color b)
    {
        context.FillRectangle(a == Palette.BgCardColor ? Palette.BgCardBrush : Palette.BgSunkenBrush, r);
        using (context.PushClip(r))
        {
            var pen = Palette.Pen(b, 5);
            double step = 10 * Math.Sqrt(2);
            for (double x = r.X - r.Height; x < r.Right + r.Height; x += step)
                context.DrawLine(pen, new Point(x, r.Bottom), new Point(x + r.Height, r.Y));
        }
    }

    // 「›」的几何（资源 Icon.right）：第一次画时取一次
    private static Geometry? _chevron;
    private static bool _chevronResolved;

    private static Geometry? Chevron
    {
        get
        {
            if (!_chevronResolved && Application.Current is { } app)
            {
                _chevronResolved = true;
                _chevron = app.TryFindResource("Icon.right", out var g) ? g as Geometry : null;
            }
            return _chevron;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        string? tip = null;
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Box.Contains(p)) { tip = _hits[i].Tip; break; }
        if (!Equals(ToolTip.GetTip(this), tip)) ToolTip.SetTip(this, tip);
    }
}
