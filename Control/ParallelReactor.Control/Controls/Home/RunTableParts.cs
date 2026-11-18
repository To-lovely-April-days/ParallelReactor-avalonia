using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Home;

/// <summary>
/// 运行表的列宽（样稿 .rtab：自动表格布局）：通道 76、状态 104、程序 34 %（最小 250 / 窄屏 210；其余列放不下时让出来），其余六列（本步剩余、预计结束、温度、压力、吸气、速率）
/// 先给内容的最小宽（表头字、读数 + 设定）+ 左右内边距，多出来的按样稿 1920 宽量到的比例分。单元格左右内边距 14（窄屏 10）。
/// </summary>
public sealed record RunColumns(double[] X, double[] W, double Pad)
{
    /// <summary>六列内容的最小宽：「本步剩余」「预计结束」表头、温度（偏差标签 + 读数 + 设定）、压力（读数 + 设定）、「吸气 mmol」、「mmol/min」（矮屏不写单位小字时 34）。</summary>
    private static readonly double[] Min = { 54, 54, 170, 106, 68, 64 };
    /// <summary>多出来的宽度怎么分（样稿 1920 宽 1532 px 的表上量到的各列宽减去最小宽）。</summary>
    private static readonly double[] Extra = { 18, 18, 47, 25, 20, 19 };

    public static RunColumns For(double width, bool narrow, bool shortRows = false)
    {
        double pad = narrow ? 10 : 14;
        var w = new double[9];
        w[0] = 76;
        w[1] = 104;
        var mins = new double[6];
        double minSum = 0;
        for (int i = 0; i < 6; i++)
        {
            mins[i] = (i == 5 && shortRows ? 34 : Min[i]) + 2 * pad;
            minSum += mins[i];
        }
        double pgMin = narrow ? 210 : 250;
        w[2] = Math.Max(pgMin, Math.Min(Math.Round(width * 0.34), width - w[0] - w[1] - minSum));
        double rest = Math.Max(0, width - w[0] - w[1] - w[2]), extra = Math.Max(0, rest - minSum), esum = 0, acc = 0;
        foreach (var v in Extra) esum += v;
        for (int i = 0; i < 6; i++)
        {
            double v = i == 5 ? Math.Max(0, rest - acc) : Math.Round(mins[i] + extra * Extra[i] / esum);
            w[3 + i] = v;
            acc += v;
        }
        var x = new double[9];
        for (int i = 1; i < 9; i++) x[i] = x[i - 1] + w[i - 1];
        return new RunColumns(x, w, pad);
    }
}

/// <summary>
/// 运行表的一行（或表头）：9 个子元素按 <see cref="ColumnsProperty"/>（继承属性，表格在尺寸变化时设在列表上）摆进各列，左右留单元格内边距，竖直方向撑满。
/// </summary>
public sealed class RunRowPanel : Panel
{
    public static readonly AttachedProperty<RunColumns?> ColumnsProperty =
        AvaloniaProperty.RegisterAttached<RunRowPanel, Avalonia.Controls.Control, RunColumns?>("Columns", inherits: true);

    public static RunColumns? GetColumns(Avalonia.Controls.Control c) => c.GetValue(ColumnsProperty);
    public static void SetColumns(Avalonia.Controls.Control c, RunColumns? v) => c.SetValue(ColumnsProperty, v);

    static RunRowPanel()
    {
        AffectsMeasure<RunRowPanel>(ColumnsProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cols = GetColumns(this);
        double h = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            double cw = cols != null && i < 9 ? Math.Max(0, cols.W[i] - 2 * cols.Pad) : double.PositiveInfinity;
            Children[i].Measure(new Size(cw, availableSize.Height));
            h = Math.Max(h, Children[i].DesiredSize.Height);
        }
        double w = cols != null ? cols.X[8] + cols.W[8] : 0;
        return new Size(double.IsInfinity(availableSize.Width) ? w : Math.Min(w, availableSize.Width), h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = GetColumns(this);
        for (int i = 0; i < Children.Count; i++)
        {
            if (cols == null || i >= 9) { Children[i].Arrange(default); continue; }
            Children[i].Arrange(new Rect(cols.X[i] + cols.Pad, 0, Math.Max(0, cols.W[i] - 2 * cols.Pad), finalSize.Height));
        }
        return finalSize;
    }
}

/// <summary>
/// 步骤分段条（样稿 .rtab .pgb：高 6、段间 3、圆角 3）：已完成 a-done、当前 a-track 轨道 + 品牌蓝进度（报警暂停：r-track + 红）、未到 bg-press；
/// 段宽按步骤计划分钟数。悬停提示 = 鼠标所在那一段（「第 3 步 恒温反应 · 约 45 min」）。
/// </summary>
public sealed class StepSegBar : Avalonia.Controls.Control
{
    public static readonly StyledProperty<IReadOnlyList<StepSeg>?> SegsProperty =
        AvaloniaProperty.Register<StepSegBar, IReadOnlyList<StepSeg>?>(nameof(Segs));

    public IReadOnlyList<StepSeg>? Segs { get => GetValue(SegsProperty); set => SetValue(SegsProperty, value); }

    private const double H = 6, Gap = 3, R = 3;

    static StepSegBar()
    {
        AffectsRender<StepSegBar>(SegsProperty);
        HeightProperty.OverrideDefaultValue<StepSegBar>(H);
    }

    public StepSegBar()
    {
        PointerMoved += (_, e) => ToolTip.SetTip(this, TipAt(e.GetPosition(this).X));
    }

    private string? TipAt(double x)
    {
        var s = Segs;
        if (s == null || s.Count == 0) return null;
        foreach (var (seg, x0, w) in Layout(s, Bounds.Width)) if (x >= x0 - Gap / 2 && x <= x0 + w + Gap / 2) return seg.Tip;
        return null;
    }

    private static IEnumerable<(StepSeg Seg, double X, double W)> Layout(IReadOnlyList<StepSeg> s, double width)
    {
        double total = 0;
        foreach (var g in s) total += g.Weight;
        double avail = Math.Max(0, width - Gap * (s.Count - 1)), x = 0;
        foreach (var g in s)
        {
            double w = total > 0 ? avail * g.Weight / total : 0;
            yield return (g, x, w);
            x += w + Gap;
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var s = Segs;
        if (s == null || s.Count == 0) return;
        double y = Math.Round((Bounds.Height - H) / 2);
        foreach (var (g, x, w) in Layout(s, Bounds.Width))
        {
            if (w <= 0) continue;
            var track = g.State switch
            {
                SegmentState.Done => Palette.RunStepDoneBrush,
                SegmentState.Cur => g.Err ? Palette.RTrackBrush : Palette.RunStepTrackBrush,
                _ => Palette.RunStepNextBrush,
            };
            ctx.DrawRectangle(track, null, new RoundedRect(new Rect(x, y, w, H), R));
            if (g.State == SegmentState.Cur && g.Fill > 0)
                ctx.DrawRectangle(g.Err ? Palette.RBrush : Palette.RunStepFillBrush, null, new RoundedRect(new Rect(x, y, Math.Max(H, w * g.Fill), H), R));
        }
    }
}
