using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Metadata;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>步骤段的状态（样稿 ui.stepBar 的 is-done / is-cur / is-held / is-abort）。</summary>
public enum StepSegState { Pending, Done, Current, Held, Aborted }

/// <summary>
/// 步骤条的一段：步骤名（标签）、状态、当前段的进度（0..1，画出来至少 8 %）。属性可以随时改（进度每帧都变）：
/// 所在的步骤条就地更新（进度只重画，状态 / 步骤名只改那一段的标签），不重建标签。
/// </summary>
public sealed class StepSeg : INotifyPropertyChanged
{
    private string _label = "";
    private StepSegState _state;
    private double _fraction;

    public string Label { get => _label; set => Set(ref _label, value ?? ""); }
    public StepSegState State { get => _state; set => Set(ref _state, value); }
    public double Fraction { get => _fraction; set => Set(ref _fraction, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// 程序步骤条（样稿 ui.stepBar / .pr-stepbar）：每步一段，段间 3 px；轨道高 10、白底 1 px line-2 边框。
/// 已完成浅绿（g-2 → g-3）；当前段按进度填充（g-2 → g-4 + 末端 1 px g-dot-line，<see cref="IsRunning"/> 时缓慢呼吸）；
/// 报警暂停灰色停住（边框 line、填充 bg-press，红色只在状态字 / 卡片边框上）；中止灰斜纹；未到白。
/// <see cref="ShowLabels"/>：段下 6 px 写步骤名（14 px t-3，当前 / 暂停段 t-1 600，放不下省略并带提示）。
/// 每段至少 <see cref="MinSegment"/> = 6 px：放不下时步骤条保持最小宽度，由父元素决定让出空间（<see cref="BarFitPanel"/>：
/// 先去掉同一行的剩余时间文字），样稿刚修过「8 路窄卡上进度条被挤到 1 px」的问题。
/// </summary>
public sealed class StepBar : Avalonia.Controls.Control
{
    public const double MinSegment = 6, Gap = 3, TrackHeight = 10, LabelGap = 6;
    /// <summary>步骤名的行高：14 px × 1.45，同 WebKit 取整 20。</summary>
    public const double LabelLineHeight = 20;

    public static readonly StyledProperty<bool> ShowLabelsProperty = AvaloniaProperty.Register<StepBar, bool>(nameof(ShowLabels));
    public static readonly StyledProperty<bool> IsRunningProperty = AvaloniaProperty.Register<StepBar, bool>(nameof(IsRunning));
    public static readonly StyledProperty<bool> IsGrayProperty = AvaloniaProperty.Register<StepBar, bool>(nameof(IsGray));
    /// <summary>轨道高（默认 <see cref="TrackHeight"/> = 10；设备页窄紧凑卡 8，样稿 .device-cards.is-narrow .pr-stepbar-track）。</summary>
    public static readonly StyledProperty<double> TrackProperty = AvaloniaProperty.Register<StepBar, double>(nameof(Track), TrackHeight);

    /// <summary>
    /// 绑定用的段列表（视图模型自己持有 <see cref="StepSeg"/> 实例、每次刷新就地改它们的属性）：设置后 <see cref="Segments"/> 按位置同步成同一批实例 ——
    /// 实例没变的位置不动（进度只重画），段数变了才增删。null = 不同步（XAML 里直接写段时用）。
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<StepSeg>?> SegmentsSourceProperty =
        AvaloniaProperty.Register<StepBar, IReadOnlyList<StepSeg>?>(nameof(SegmentsSource));

    public IReadOnlyList<StepSeg>? SegmentsSource { get => GetValue(SegmentsSourceProperty); set => SetValue(SegmentsSourceProperty, value); }

    private void SyncSource()
    {
        var src = SegmentsSource;
        if (src is null) return;
        for (int i = 0; i < src.Count; i++)
        {
            if (i < Segments.Count) { if (!ReferenceEquals(Segments[i], src[i])) Segments[i] = src[i]; }
            else Segments.Add(src[i]);
        }
        while (Segments.Count > src.Count) Segments.RemoveAt(Segments.Count - 1);
    }

    static StepBar()
    {
        AffectsMeasure<StepBar>(ShowLabelsProperty, TrackProperty);
        AffectsRender<StepBar>(IsRunningProperty, IsGrayProperty);
    }

    public StepBar()
    {
        Segments.CollectionChanged += OnSegmentsChanged;
    }

    /// <summary>
    /// 各段（XAML 里直接写 &lt;pr:StepSeg …/&gt;）。增删改都是增量的：段的进度变了只重画，状态 / 步骤名变了只改那一段的标签，
    /// 插入 / 删除 / 替换只动对应的标签（R10：每秒刷新不重建）。
    /// </summary>
    [Content]
    public AvaloniaList<StepSeg> Segments { get; } = new();

    public bool ShowLabels { get => GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }
    public double Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    /// <summary>当前段在运行（呼吸）；离线 / 等待进气时为 false。</summary>
    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    /// <summary>
    /// 灰显（样稿断线设备的 .overview-dev.is-off .pr-stepbar { filter: grayscale(1) }）：浅绿换成色板里最接近的灰（按亮度换算，各通道差 ≤ 6）——
    /// 已完成 g-2 → g-3 ≈ bg-head → bg-panel；当前段 g-2 → g-4 ≈ bg-head → bg-sunken；末端 1 px g-dot-line ≈ n-dot。不呼吸。
    /// </summary>
    public bool IsGray { get => GetValue(IsGrayProperty); set => SetValue(IsGrayProperty, value); }

    private static readonly IBrush GrayDoneFill = ProgressPr.Gradient(Palette.BgHeadColor, Palette.BgPanelColor);
    private static readonly IBrush GrayRunFill = ProgressPr.Gradient(Palette.BgHeadColor, Palette.BgSunkenColor);

    /// <summary>n 段时步骤条的最小宽度（每段 6 px + 段间 3 px）。</summary>
    public static double MinBarWidth(int segments) => segments <= 1 ? MinSegment : segments * MinSegment + (segments - 1) * Gap;

    /// <summary>这条步骤条的最小宽度。</summary>
    public double MinBarWidthSelf => MinBarWidth(Segments.Count);

    private readonly List<TextBlock> _labels = new();
    // 已订阅 PropertyChanged 的段（与 Segments 同步；整体重置时按它退订）
    private readonly List<StepSeg> _watched = new();

    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems != null:
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    var seg = (StepSeg)e.NewItems[i]!;
                    Watch(seg, e.NewStartingIndex + i);
                    if (ShowLabels) InsertLabel(e.NewStartingIndex + i, seg);
                }
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                for (int i = 0; i < e.OldItems.Count; i++)
                {
                    Unwatch(e.OldStartingIndex);
                    if (ShowLabels) RemoveLabel(e.OldStartingIndex);
                }
                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems != null:
                for (int i = 0; i < e.NewItems.Count; i++)
                {
                    var idx = e.NewStartingIndex + i;
                    var seg = (StepSeg)e.NewItems[i]!;
                    Unwatch(idx);
                    Watch(seg, idx);
                    if (ShowLabels && idx < _labels.Count) ApplyLabel(_labels[idx], seg);
                }
                break;
            default:   // Move / Reset：少见，整体重来
                Rebuild();
                return;
        }
        UpdatePulse();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Watch(StepSeg seg, int index)
    {
        seg.PropertyChanged += OnSegChanged;
        _watched.Insert(Math.Clamp(index, 0, _watched.Count), seg);
    }

    private void Unwatch(int index)
    {
        if (index < 0 || index >= _watched.Count) return;
        _watched[index].PropertyChanged -= OnSegChanged;
        _watched.RemoveAt(index);
    }

    /// <summary>段的属性变了：进度只重画；状态 / 步骤名改那一段的标签（字重、颜色、文字），并重算呼吸。</summary>
    private void OnSegChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not StepSeg seg) return;
        if (e.PropertyName != nameof(StepSeg.Fraction))
        {
            if (ShowLabels)
            {
                var i = Segments.IndexOf(seg);
                if (i >= 0 && i < _labels.Count) ApplyLabel(_labels[i], seg);
            }
            if (e.PropertyName == nameof(StepSeg.State)) UpdatePulse();
        }
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowLabelsProperty) Rebuild();
        else if (change.Property == SegmentsSourceProperty) SyncSource();
        else if (change.Property == IsRunningProperty || change.Property == IsGrayProperty || change.Property == IsVisibleProperty || change.Property == Frozen.IsFrozenProperty) UpdatePulse();
    }

    /// <summary>整体重建（ShowLabels 切换、Move / Reset）：重新订阅所有段、重建全部标签。</summary>
    private void Rebuild()
    {
        foreach (var s in _watched) s.PropertyChanged -= OnSegChanged;
        _watched.Clear();
        for (int i = 0; i < Segments.Count; i++) Watch(Segments[i], i);
        while (_labels.Count > 0) RemoveLabel(_labels.Count - 1);
        if (ShowLabels) for (int i = 0; i < Segments.Count; i++) InsertLabel(i, Segments[i]);
        UpdatePulse();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void InsertLabel(int index, StepSeg seg)
    {
        var tb = new TextBlock
        {
            FontSize = 14,
            LineHeight = LabelLineHeight,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ApplyLabel(tb, seg);
        index = Math.Clamp(index, 0, _labels.Count);
        _labels.Insert(index, tb);
        LogicalChildren.Add(tb);
        VisualChildren.Add(tb);
    }

    private void RemoveLabel(int index)
    {
        if (index < 0 || index >= _labels.Count) return;
        var tb = _labels[index];
        _labels.RemoveAt(index);
        VisualChildren.Remove(tb);
        LogicalChildren.Remove(tb);
    }

    /// <summary>标签外观：当前 / 暂停段 t-1 600，其它 t-3 400；省略时提示全文。</summary>
    private static void ApplyLabel(TextBlock tb, StepSeg s)
    {
        var cur = s.State is StepSegState.Current or StepSegState.Held;
        if (tb.Text != s.Label) { tb.Text = s.Label; ToolTip.SetTip(tb, s.Label); }
        tb.FontWeight = cur ? FontWeight.SemiBold : FontWeight.Normal;
        tb.Foreground = cur ? Palette.T1Brush : Palette.T3Brush;
    }

    private double SegWidth(double width)
    {
        int n = Math.Max(1, Segments.Count);
        return Math.Max(MinSegment, (width - (n - 1) * Gap) / n);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double labelH = 0;
        var segW = double.IsInfinity(availableSize.Width) ? 60 : SegWidth(availableSize.Width);
        foreach (var l in _labels)
        {
            l.Measure(new Size(segW, double.PositiveInfinity));
            labelH = Math.Max(labelH, l.DesiredSize.Height);
        }
        var h = Track + (_labels.Count > 0 ? LabelGap + labelH : 0);
        return new Size(MinBarWidthSelf, h);
    }

    /// <summary>第 i 段的横向范围（左右边取整像素：段的 1 px 边框不发虚，同网页）。</summary>
    private (double X, double W) Seg(int i, double width)
    {
        var segW = SegWidth(width);
        var x0 = Math.Round(i * (segW + Gap));
        var x1 = Math.Round(i * (segW + Gap) + segW);
        return (x0, Math.Max(1, x1 - x0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < _labels.Count; i++)
        {
            var (x, w) = Seg(i, finalSize.Width);
            _labels[i].Arrange(new Rect(x, Track + LabelGap, w, _labels[i].DesiredSize.Height));
        }
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        if (Segments.Count == 0)
        {
            DrawTrack(context, new Rect(0, 0, Bounds.Width, Track), Palette.Line2Color);
            return;
        }
        for (int i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            var (x, w) = Seg(i, Bounds.Width);
            var r = new Rect(x, 0, w, Track);
            DrawTrack(context, r, s.State == StepSegState.Held ? Palette.LineColor : Palette.Line2Color);
            var inner = r.Deflate(1);
            switch (s.State)
            {
                case StepSegState.Done:
                    context.DrawRectangle(IsGray ? GrayDoneFill : ProgressPr.DoneFill, null, inner);
                    break;
                case StepSegState.Current:
                {
                    var f = Fill(inner, s.Fraction);
                    context.DrawRectangle(IsGray ? GrayRunFill : ProgressPr.RunFill, null, f);
                    context.DrawRectangle(IsGray ? Palette.NDotBrush : Palette.GDotLineBrush, null, new Rect(f.Right - 1, f.Y, 1, f.Height));
                    if (_pulsing && !IsGray) context.DrawRectangle(PulseClock.Overlay, null, f);
                    break;
                }
                case StepSegState.Held:
                {
                    var f = Fill(inner, s.Fraction);
                    context.DrawRectangle(Palette.BgPressBrush, null, f);
                    context.DrawRectangle(Palette.LineBrush, null, new Rect(f.Right - 1, f.Y, 1, f.Height));
                    break;
                }
                case StepSegState.Aborted:
                    Hatch(context, inner);
                    break;
            }
        }
    }

    /// <summary>当前 / 暂停段的填充：至少 8 %（样稿 Math.max(8, within × 100)）。</summary>
    private static Rect Fill(Rect inner, double fraction)
        => new(inner.X, inner.Y, Math.Max(1, Math.Round(inner.Width * Math.Clamp(Math.Max(0.08, fraction), 0, 1))), inner.Height);

    private static void DrawTrack(DrawingContext context, Rect r, Color border)
        => context.DrawRectangle(Palette.BgCardBrush, Palette.Pen(border, 1), r.Deflate(0.5));

    /// <summary>中止：135° 灰斜纹（bg-sunken 3 px / line-soft 3 px，周期 6 px）。</summary>
    private static void Hatch(DrawingContext context, Rect r)
    {
        context.DrawRectangle(Palette.BgSunkenBrush, null, r);
        using (context.PushClip(r))
        {
            var pen = Palette.Pen(Palette.LineSoftColor, 3);
            var step = 6 * Math.Sqrt(2);
            for (double x = r.X - r.Height; x < r.Right + r.Height; x += step)
                context.DrawLine(pen, new Point(x, r.Bottom), new Point(x + r.Height, r.Y));
        }
    }

    // ---------------- 呼吸（同 ProgressPr）：当前段运行中、已上树、未冻结 ----------------
    private bool _pulsing, _attached;

    private void UpdatePulse()
    {
        bool hasCur = false;
        foreach (var s in Segments) if (s.State == StepSegState.Current) hasCur = true;
        bool on = hasCur && IsRunning && !IsGray && IsVisible && _attached && !Frozen.GetIsFrozen(this);
        if (on == _pulsing) return;
        _pulsing = on;
        PulseClock.Set(this, on);
        InvalidateVisual();
    }

    // 不在树上时不订阅段的变化（段对象可能比步骤条活得久，不能被它们拖住）；回到树上时重新订阅并按段的现状刷新标签
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        foreach (var s in _watched) { s.PropertyChanged -= OnSegChanged; s.PropertyChanged += OnSegChanged; }
        for (int i = 0; i < _labels.Count && i < Segments.Count; i++) ApplyLabel(_labels[i], Segments[i]);
        UpdatePulse();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        foreach (var s in _watched) s.PropertyChanged -= OnSegChanged;
        UpdatePulse();
    }
}
