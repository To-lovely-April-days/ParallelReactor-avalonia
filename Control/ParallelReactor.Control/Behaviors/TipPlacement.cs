using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.VisualTree;

namespace ParallelReactor.Control.Behaviors;

/// <summary>
/// 悬停提示的位置（M6，样稿 shell.js showTip）：控件正下方居中、间距 6；下面放不下（提示底 &gt; 范围底 − 4）翻到上面、间距照样 6；
/// 左右贴边时滑回来、离边 4 px。放在控件下面的类型见 <see cref="TipBelowTypes"/>（Reason.cs 的静态构造函数把它们的 ToolTip.Placement 默认值改成 Bottom、不偏移），
/// 自绘的轨道 / 曲线照旧跟着指针，这里不管。
/// <para>做法：提示打开之后（ToolTip 自己的 IsOpen 处理器先跑：弹出层已经建好、按 Bottom 放过一次）再按上面的规则定上下，用 ±6 的偏移量重新放一次，
/// 并关掉弹出层自己的上下翻转 —— Avalonia 11.1 翻转时偏移量不跟着变号（Bottom + 6 翻到上面会压住控件 6 px）。</para>
/// <para>间距放在弹出层<b>外面</b>（偏移量），不做成提示透明的外边距：桌面上提示是单独的弹出窗口，窗口里透明的地方照样接住指针
/// （做不到样稿 pointer-events: none）。间距在窗口里时，指针从控件往下一移就进了提示窗口：ToolTipService 在指针停在提示上时不关它，
/// 下一行就点不到。间距在外面时，指针先经过窗口里的别的东西，提示照常关掉。</para>
/// <para>范围：窗口里的弹出层（无头、截图）= 窗口；单独的弹出窗口（桌面）= 控件所在屏幕的工作区，同 Avalonia 自己的 ManagedPopupPositioner（样稿是外框）。
/// 左右离边 4 px 是窗口里的提示透明的外边距（<see cref="TipMargin"/>，滑回来时离边 4 px；提示不接收指针）；桌面上不加（离屏幕边 4 px 没有意义，
/// 透明的边还会接住指针、露出一截被窗口边裁掉的阴影）。窗口里的弹出层和它模板里的几层不裁剪，阴影（ShadowPop）整个画出来，同样稿；
/// 桌面上阴影在提示窗口的边上被裁掉（同 M6 之前）。桌面上的弹出窗口没有人工核对过（见文档 §8）。</para>
/// </summary>
public static partial class Pr
{
    /// <summary>窗口里放在控件下面的提示左右透明的外边距（滑回窗口里时离边 <see cref="TipInset"/>）。跟着指针的提示、桌面上的提示窗口不加。</summary>
    public static readonly Thickness TipMargin = new(TipInset, 0);

    private static void OnTipOpenChanged(Avalonia.Controls.Control c, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) PlaceTip(c);
    }

    /// <summary>把刚打开的提示放到控件下面或上面（见类注释）。不是默认的 Bottom、不偏移的（自己设了位置的），不动。</summary>
    private static void PlaceTip(Avalonia.Controls.Control c)
    {
        if (ToolTip.GetPlacement(c) != PlacementMode.Bottom || ToolTip.GetVerticalOffset(c) != 0 || ToolTip.GetHorizontalOffset(c) != 0) return;
        if (c.GetValue(ToolTipDiagnostics.ToolTipProperty) is not ToolTip tip || ((IPopupHostProvider)tip).PopupHost is not { } host) return;
        if (TopLevel.GetTopLevel(c) is not { } top) return;
        bool ownWindow = host is PopupRoot;
        var margin = ownWindow ? default : TipMargin;
        if (tip.Margin != margin) tip.Margin = margin;
        if (!ownWindow) Unclip(host, tip);
        // 单独的弹出窗口在 Show 里已经排过版；窗口里的弹出层要到下一次排版 —— 先量一次（最宽 400，量出来的高同排版时）
        if (!tip.IsMeasureValid || tip.DesiredSize.Height <= 0) tip.Measure(Size.Infinity);
        bool below = FitsBelow(c, top, host, tip.DesiredSize.Height);
        host.ConfigurePosition(c, below ? PlacementMode.Bottom : PlacementMode.Top, new Point(0, below ? TipGap : -TipGap),
            PopupAnchor.None, PopupGravity.None, PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY);
    }

    /// <summary>
    /// 窗口里的弹出层（无头、截图）：弹出层和它模板里的几层（LayoutTransformControl …）默认裁剪，正好是提示那么大 —— 提示的阴影整个被裁掉。
    /// 都不裁（只改这一次的弹出层，每次打开都是新的）；模板要到第一次排版才建，建好时（TemplateApplied）再改一遍。
    /// </summary>
    private static void Unclip(IPopupHost host, ToolTip tip)
    {
        if (host is not Visual hv) return;
        void Apply()
        {
            hv.ClipToBounds = false;
            foreach (var v in hv.GetVisualDescendants().ToList())
                if (v != tip && !tip.IsVisualAncestorOf(v)) v.ClipToBounds = false;
        }
        host.TemplateApplied += (_, _) => Apply();
        Apply();
    }

    /// <summary>放得下控件下面吗（<see cref="TipFits"/>）：窗口里的弹出层比窗口，单独的弹出窗口比控件所在屏幕的工作区。</summary>
    private static bool FitsBelow(Avalonia.Controls.Control c, TopLevel top, IPopupHost host, double tipHeight)
    {
        if (host is PopupRoot)
        {
            var p = c.PointToScreen(new Point(0, c.Bounds.Height));
            var screens = (top as WindowBase)?.Screens;
            var s = screens?.ScreenFromPoint(p) ?? screens?.Primary;
            if (s == null) return true;
            var area = s.WorkingArea.Width == 0 && s.WorkingArea.Height == 0 ? s.Bounds : s.WorkingArea;
            double scale = top.RenderScaling > 0 ? top.RenderScaling : 1;
            return TipFits(p.Y / scale, tipHeight, area.Bottom / scale);
        }
        // 窗口里的弹出层铺满窗口；第一次打开时弹出层（OverlayLayer）才建、还没排版（大小是 0），所以直接比窗口
        if (c.TranslatePoint(new Point(0, c.Bounds.Height), top) is not { } bottom) return true;
        return TipFits(bottom.Y, tipHeight, top.ClientSize.Height);
    }

    /// <summary>样稿 showTip：y = 控件底 + 6；y + 提示高 &gt; 范围底 − 4 时翻到上面（同一个坐标系，与设备无关的单位）。</summary>
    public static bool TipFits(double controlBottom, double tipHeight, double areaBottom) =>
        controlBottom + TipGap + tipHeight <= areaBottom - TipInset;
}
