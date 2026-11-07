using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.ViewModels;

namespace ParallelReactor.Control.Controls;

/// <summary>
/// mock 的 <c>.tracks</c>：8 条 25.5px 轨道（底线 #eeeeef）、每 25% 一条竖网格 #e6e6e8、每行一个 run-bar
/// （高 17，边 1px #d2d2d5，白底；左段 idle 白底居中 book 图标，右段 complete 渐变 #f2ffe9→#e5ffd0 左边 1px #e1f3d2；
/// running 用 #efffe4→#d9fbb9；完成 #eaffd8；等气路左段紫灰渐变 +「等气路」；未安装整体 .5 透明）。
/// </summary>
public sealed class TimelineTracks : Avalonia.Controls.Control
{
    public static readonly StyledProperty<IList<TimelineRowVm>?> RowsProperty = AvaloniaProperty.Register<TimelineTracks, IList<TimelineRowVm>?>(nameof(Rows));
    public static readonly StyledProperty<Geometry?> BookIconProperty = AvaloniaProperty.Register<TimelineTracks, Geometry?>(nameof(BookIcon));

    public const double TrackHeight = 25.5;
    private static readonly IBrush TrackLine = new SolidColorBrush(Color.Parse("#eeeeef"));
    private static readonly IBrush Grid = new SolidColorBrush(Color.Parse("#e6e6e8"));
    private static readonly IBrush BarLine = new SolidColorBrush(Color.Parse("#d2d2d5"));
    private static readonly IBrush RunLeft = new SolidColorBrush(Color.Parse("#e1f3d2"));
    private static readonly IBrush Done = new SolidColorBrush(Color.Parse("#eaffd8"));
    private static readonly IBrush Status = new SolidColorBrush(Color.Parse("#77777d"));
    private static readonly IBrush Purple = new SolidColorBrush(Color.Parse("#6550b1"));
    private static readonly IBrush RunGradient = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#f2ffe9"), 0), new GradientStop(Color.Parse("#e5ffd0"), 1) } };
    private static readonly IBrush RunningGradient = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#efffe4"), 0), new GradientStop(Color.Parse("#d9fbb9"), 1) } };
    private static readonly IBrush WaitingGradient = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#f1eefb"), 0), new GradientStop(Color.Parse("#e5e0f5"), 1) } };
    private static readonly Typeface Face = new(new FontFamily("Segoe UI, Arial, PingFang SC, Microsoft YaHei"));

    static TimelineTracks() { AffectsRender<TimelineTracks>(RowsProperty, BookIconProperty); }

    public IList<TimelineRowVm>? Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public Geometry? BookIcon { get => GetValue(BookIconProperty); set => SetValue(BookIconProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty)
        {
            if (change.OldValue is IList<TimelineRowVm> old) foreach (var r in old) r.PropertyChanged -= OnRowChanged;
            if (change.NewValue is IList<TimelineRowVm> nw) foreach (var r in nw) r.PropertyChanged += OnRowChanged;
        }
    }

    private void OnRowChanged(object? s, PropertyChangedEventArgs e) => InvalidateVisual();

    /// <summary>运行条最小像素宽。</summary>
    public const double MinBarPx = 60;

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width; var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var grid = new Pen(Grid, 1);
        for (int i = 1; i <= 4; i++) { double x = Math.Round(w * i / 4.0) - 0.5; ctx.DrawLine(grid, new Point(x, 0), new Point(x, h)); }
        var rows = Rows;
        var line = new Pen(TrackLine, 1);
        var barPen = new Pen(BarLine, 1);
        var leftPen = new Pen(RunLeft, 1);
        for (int i = 0; i < 8; i++)
        {
            double y0 = i * TrackHeight;
            ctx.DrawLine(line, new Point(0, y0 + TrackHeight - 0.5), new Point(w, y0 + TrackHeight - 0.5));
            if (rows == null || i >= rows.Count) continue;
            var r = rows[i];
            if (!r.HasBar) continue;
            double avail = w - 9;
            double x0 = 9 + avail * r.Left, x1 = 9 + avail * r.Right;
            if (x1 - x0 < MinBarPx) x0 = Math.Max(9, x1 - MinBarPx);   // 刚启动的程序也要看得见
            if (x1 - x0 < 6) continue;
            double by = y0 + (TrackHeight - 17) / 2;
            var bar = new Rect(Math.Round(x0) + 0.5, Math.Round(by) + 0.5, Math.Round(x1 - x0) - 1, 16);
            using (r.Disabled ? ctx.PushOpacity(0.5) : default)
            {
                ctx.FillRectangle(Brushes.White, bar);
                // 左段 idle（book 图标）/ 右段 complete
                double idleW = bar.Width * (1 - r.Complete);
                var idle = new Rect(bar.X, bar.Y, idleW, bar.Height);
                var comp = new Rect(bar.X + idleW, bar.Y, bar.Width - idleW, bar.Height);
                if (r.WaitingGas) ctx.FillRectangle(WaitingGradient, idle);
                if (comp.Width > 0.5)
                {
                    ctx.FillRectangle(r.Done ? Done : r.Running ? RunningGradient : RunGradient, comp);
                    ctx.DrawLine(leftPen, new Point(comp.X + 0.5, comp.Y), new Point(comp.X + 0.5, comp.Bottom));
                }
                ctx.DrawRectangle(null, barPen, bar);
                var book = BookIcon;
                if (book != null && !r.Disabled && idleW > 14)
                {
                    double s = 8 / 24.0;
                    var cx = idle.X + idle.Width / 2 - 4; var cy = bar.Y + (bar.Height - 8) / 2;
                    using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(cx, cy)))
                        ctx.DrawGeometry(null, new Pen(Status, 1.45, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), book);
                    if (r.WaitingGas && idleW > 50)
                    {
                        var ft = new FormattedText("等气路", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 7, Purple);
                        ctx.DrawText(ft, new Point(cx + 12, bar.Y + (bar.Height - ft.Height) / 2));
                    }
                }
                if (r.Label.Length > 0 && comp.Width > 40)
                {
                    var ft = new FormattedText(r.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 7, Status) { MaxTextWidth = Math.Max(10, comp.Width - 8), Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
                    ctx.DrawText(ft, new Point(comp.X + 5, bar.Y + (bar.Height - ft.Height) / 2));
                }
            }
        }
    }
}

/// <summary>mock 的 <c>.timeline-head</c> 时间标签：四个时刻分别居中在 20% / 48% / 71% / 90%（7px #99999e，top 6）。</summary>
public sealed class TimelineHead : Avalonia.Controls.Control
{
    public static readonly StyledProperty<string> T1Property = AvaloniaProperty.Register<TimelineHead, string>(nameof(T1), "");
    public static readonly StyledProperty<string> T2Property = AvaloniaProperty.Register<TimelineHead, string>(nameof(T2), "");
    public static readonly StyledProperty<string> T3Property = AvaloniaProperty.Register<TimelineHead, string>(nameof(T3), "");
    public static readonly StyledProperty<string> T4Property = AvaloniaProperty.Register<TimelineHead, string>(nameof(T4), "");
    private static readonly IBrush Label = new SolidColorBrush(Color.Parse("#99999e"));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI, Arial, PingFang SC"));
    static TimelineHead() { AffectsRender<TimelineHead>(T1Property, T2Property, T3Property, T4Property); }
    public string T1 { get => GetValue(T1Property); set => SetValue(T1Property, value); }
    public string T2 { get => GetValue(T2Property); set => SetValue(T2Property, value); }
    public string T3 { get => GetValue(T3Property); set => SetValue(T3Property, value); }
    public string T4 { get => GetValue(T4Property); set => SetValue(T4Property, value); }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var items = new[] { (T1, .20), (T2, .48), (T3, .71), (T4, .90) };
        foreach (var (t, f) in items)
        {
            if (string.IsNullOrEmpty(t)) continue;
            var ft = new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 7, Label);
            ctx.DrawText(ft, new Point(w * f - ft.Width / 2, 6));
        }
    }
}
