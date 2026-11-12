using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 外框缩放（PORT-PLAN §2.1、§4.8；风险 R3）：窗口窄于 1366 或矮于 768 时，按 1366×768 以上的尺寸排版再等比缩小
/// （scale = min(1, W/1366, H/768)，排版尺寸 = 窗口 / scale，正好铺满窗口）；够大时 1:1，什么都不做。
/// 公式在 <see cref="ShellLayout.FrameFor"/>（外框各区域的宽度类也按它算，两边一致）。
/// 做法同 LayoutTransformControl（按变换前的尺寸排版、渲染时缩放），但不缓存子元素的旧尺寸：窗口每次变化都按新尺寸重新排版。
/// 渲染变换会参与命中测试和 TransformToVisual，所以点击、菜单 / 提示的定位都对；弹出层本身不缩放。
/// 细线：Window 开着 UseLayoutRounding；缩小时 1 px 线可能落在半像素上（M8 在 Windows 125 % / 150 % 实机上检查）。
/// </summary>
public sealed class FrameScaler : Decorator
{
    public static readonly DirectProperty<FrameScaler, double> ScaleProperty =
        AvaloniaProperty.RegisterDirect<FrameScaler, double>(nameof(Scale), o => o.Scale);

    private double _scale = 1;

    /// <summary>当前缩放（1 = 不缩放）。</summary>
    public double Scale { get => _scale; private set => SetAndRaise(ScaleProperty, ref _scale, value); }

    static FrameScaler()
    {
        ClipToBoundsProperty.OverrideDefaultValue<FrameScaler>(true);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (frame, k) = ShellLayout.FrameFor(availableSize);
        if (Child == null) return default;
        Child.Measure(frame);
        return double.IsInfinity(availableSize.Width) || double.IsInfinity(availableSize.Height)
            ? new Size(Child.DesiredSize.Width * k, Child.DesiredSize.Height * k)
            : availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (frame, k) = ShellLayout.FrameFor(finalSize);
        Scale = k;
        if (Child == null) return finalSize;
        if (k >= 1)
        {
            if (Child.RenderTransform != null) Child.RenderTransform = null;
        }
        else if (Child.RenderTransform is not ScaleTransform st || st.ScaleX != k)
        {
            Child.RenderTransformOrigin = RelativePoint.TopLeft;
            Child.RenderTransform = new ScaleTransform(k, k);
        }
        Child.Arrange(new Rect(frame));
        return finalSize;
    }
}
