using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Heat;

/// <summary>曲线升温的分段进度条（设计稿 .cvbar）：每段宽度按用时，段间距 3、高 6、圆角 3；走完 = 品牌蓝，当前段蓝到完成比例，其余轨道色。</summary>
public sealed class CurveBar : Avalonia.Controls.Control
{
    public static readonly StyledProperty<IReadOnlyList<CurveBarItem>?> ItemsProperty =
        AvaloniaProperty.Register<CurveBar, IReadOnlyList<CurveBarItem>?>(nameof(Items));

    public IReadOnlyList<CurveBarItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }

    static CurveBar() => AffectsRender<CurveBar>(ItemsProperty);

    protected override Size MeasureOverride(Size availableSize) => new(double.IsFinite(availableSize.Width) ? availableSize.Width : 200, 6);

    public override void Render(DrawingContext ctx)
    {
        var items = Items;
        if (items == null || items.Count == 0 || Bounds.Width < 10) return;
        const double gap = 3, h = 6, min = 6;
        double sum = 0;
        foreach (var it in items) sum += it.Weight;
        double free = Bounds.Width - gap * (items.Count - 1) - min * items.Count;
        double x = 0;
        foreach (var it in items)
        {
            double w = min + Math.Max(0, free) * (sum > 0 ? it.Weight / sum : 1.0 / items.Count);
            ctx.DrawRectangle(it.Done ? Palette.ABrush : Palette.ATrackBrush, null, new RoundedRect(new Rect(x, 0, w, h), 3));
            if (it.Current && it.F > 0)
                using (ctx.PushClip(new RoundedRect(new Rect(x, 0, w, h), 3)))
                    ctx.DrawRectangle(Palette.ABrush, null, new Rect(x, 0, w * Math.Clamp(it.F, 0, 1), h));
            x += w + gap;
        }
    }
}
