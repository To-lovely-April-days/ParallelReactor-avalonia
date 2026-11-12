using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 状态点（样稿 ui.keyDot / .pr-dot）：9 px 圆点 + 内侧 1 px 描边，占 13 px 槽位（列表里「RV1」「RV4」左对齐）。
/// 运行类浅绿、报警红、其它灰；已结束画 13 px ✓（t-3，描边 2.2）；离线是 1.5 px 空心圈；未安装不画（仍占位）。
/// 颜色来自 StateStyles（Palette）。<see cref="Diameter"/> = 8 时是徽标里的小点（.pr-badge &gt; i）。
/// </summary>
public sealed class KeyDot : Avalonia.Controls.Control
{
    public static readonly StyledProperty<StateKey> KeyProperty = AvaloniaProperty.Register<KeyDot, StateKey>(nameof(Key));
    public static readonly StyledProperty<double> DiameterProperty = AvaloniaProperty.Register<KeyDot, double>(nameof(Diameter), 9);
    public static readonly StyledProperty<double> SlotProperty = AvaloniaProperty.Register<KeyDot, double>(nameof(Slot), 13);

    static KeyDot()
    {
        AffectsRender<KeyDot>(KeyProperty, DiameterProperty);
        AffectsMeasure<KeyDot>(SlotProperty);
    }

    public StateKey Key { get => GetValue(KeyProperty); set => SetValue(KeyProperty, value); }
    /// <summary>圆点直径（9；徽标里 8）。</summary>
    public double Diameter { get => GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }
    /// <summary>占位边长（13：点和 ✓ 一样宽）。</summary>
    public double Slot { get => GetValue(SlotProperty); set => SetValue(SlotProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Slot, Slot);

    // ✓ 的几何（资源 Icon.check）：第一次画时从应用资源取一次，之后复用（R10：渲染路径里不查资源）
    private static Geometry? _check;
    private static bool _checkResolved;

    private static Geometry? Check
    {
        get
        {
            if (!_checkResolved && Application.Current is { } app)
            {
                _checkResolved = true;
                _check = app.TryFindResource("Icon.check", out var g) ? g as Geometry : null;
            }
            return _check;
        }
    }

    public override void Render(DrawingContext context)
    {
        var st = StateStyles.Get(Key);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (st.Kind == StateKind.Uninstalled) return;
        if (st.DotCheck)
        {
            if (Check is { } geo)
            {
                var s = Slot / 24.0;
                var ox = (Bounds.Width - Slot) / 2;
                var oy = (Bounds.Height - Slot) / 2;
                using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy)))
                    context.DrawGeometry(null, Palette.Pen(Palette.T3Color, 2.2 / s, cap: PenLineCap.Round), geo);
            }
            return;
        }
        var r = Diameter / 2;
        if (st.DotHollow)
        {
            var w = st.DotLineWidth;
            context.DrawEllipse(null, Palette.Pen(st.DotLine!.Color, w), c, r - w / 2, r - w / 2);
            return;
        }
        if (st.Dot != null) context.DrawEllipse(st.Dot, null, c, r, r);
        // 内侧 1 px 描边（box-shadow: inset 0 0 0 1px）
        if (st.DotLine != null && st.DotLineWidth > 0)
            context.DrawEllipse(null, Palette.Pen(st.DotLine.Color, st.DotLineWidth), c, r - st.DotLineWidth / 2, r - st.DotLineWidth / 2);
    }
}
