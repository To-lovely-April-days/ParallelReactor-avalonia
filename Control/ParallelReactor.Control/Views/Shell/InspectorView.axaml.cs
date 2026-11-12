using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 检查器（样稿 renderInspector）：标题栏 + 可滚动的正文（Shell/Inspector 的三种正文视图模型，按 DataTemplate 显示）。
/// 换了对象（InspectorVm.BodyKey 变）时滚动回到顶部（样稿 data-key 换了整块重画）；「设置」链接请求聚焦共用转速时
/// 照样稿 inspFocus 把那个分区滚进来、聚焦并全选数值、分区提亮 1.2 s（<see cref="OnFocus"/>）。
/// </summary>
public partial class InspectorView : UserControl
{
    /// <summary>「设置」跳过来的分区的外圈（样稿 .pr-insp-sec.is-flash：box-shadow 0 0 0 8px a-tint；底色 a-tint 在样式里）。</summary>
    public static readonly BoxShadows FlashRing = new(new BoxShadow { Spread = 8, Color = Palette.ATintColor });

    /// <summary>
    /// 分区平时的外圈：0 px、透明，看不见（透明色什么都不画）。Avalonia 的阴影过渡只在两头都有阴影时插值 ——
    /// 从「没有阴影」开始时前 400 ms 一直没有、到最后一刻才整圈出现，熄灭时整圈留到最后一刻（复核）；
    /// 有了这一圈，外圈同样稿（CSS 把 none 当成 0 0 0 0 transparent）从 0 长到 8 px、颜色跟底色一起变。
    /// </summary>
    public static readonly BoxShadows NoRing = new(new BoxShadow { Color = Brushes.Transparent.Color });

    /// <summary>提亮多久（样稿 setTimeout 1200）。</summary>
    public static readonly TimeSpan FlashFor = TimeSpan.FromMilliseconds(1200);

    private InspectorVm? _vm;
    private Border? _flashed;
    private IDisposable? _flashOff;

    public InspectorView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) { _vm.PropertyChanged -= OnVm; _vm.FocusRequested -= OnFocus; }
        _vm = (DataContext as ShellVm)?.Inspector;
        if (_vm != null) { _vm.PropertyChanged += OnVm; _vm.FocusRequested += OnFocus; }
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectorVm.BodyKey)) Scroll.Offset = default;
    }

    /// <summary>
    /// 「设置」（共用转速；HT-PR4 每釜独立搅拌时是反应釜检查器的「转速」）：选中对象之后（等新正文排好版）照样稿 inspFocus ——
    /// 分区不在视野里时滚到分区顶部在正文顶部下面 8 px；数值框没有原因（不是只读）时聚焦并全选（inp.select()），带原因时不聚焦；
    /// 分区提亮 1.2 s（类 flash：a-tint 底 + 8 px a-tint 外圈，进出 400 ms 过渡）。数值框（Controls/Pr/NumBox）里面的 TextBox 从这里找。
    /// </summary>
    private void OnFocus(object? sender, string name)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = Body.GetVisualDescendants().OfType<NumBox>().FirstOrDefault(n => n.Name is "SharedRpm" or "ReactorRpm" && n.IsEffectivelyVisible);
            if (box == null) return;
            var sec = box.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("sec"));
            ScrollIntoView((Visual?)sec ?? box);
            var input = box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (input != null && !input.IsReadOnly)
            {
                input.Focus();
                input.SelectAll();
            }
            if (sec != null) Flash(sec);
        }, DispatcherPriority.Background);
    }

    /// <summary>样稿：top &lt; scrollTop 或 bottom &gt; clientHeight 时 scrollTop = max(0, top − 8)；在视野里的不动。</summary>
    private void ScrollIntoView(Visual target)
    {
        UpdateLayout();
        if (target.TranslatePoint(default, Scroll) is not { } at) return;
        double top = at.Y, bottom = at.Y + target.Bounds.Height;
        if (top < 0 || bottom > Scroll.Viewport.Height)
            Scroll.Offset = new Vector(Scroll.Offset.X, Math.Max(0, Scroll.Offset.Y + top - 8));
    }

    /// <summary>
    /// 分区提亮 1.2 s（再点一次重新计时；上一个还亮着的先灭）。进出的过渡（样稿 transition: background-color 400ms ease, box-shadow 400ms ease）
    /// 只在不冻结时给：写成分区的样式会盖掉全局的 .frozen（离控件近的样式优先），截图 / 冻结时就有过渡了。
    /// </summary>
    private void Flash(Border sec)
    {
        _flashOff?.Dispose();
        if (_flashed != null && !ReferenceEquals(_flashed, sec)) _flashed.Classes.Remove("flash");
        _flashed = sec;
        sec.Transitions = FlashTransitions(sec);
        if (!sec.Classes.Contains("flash")) sec.Classes.Add("flash");
        _flashOff = DispatcherTimer.RunOnce(() =>
        {
            sec.Transitions = FlashTransitions(sec);
            sec.Classes.Remove("flash");
            if (ReferenceEquals(_flashed, sec)) _flashed = null;
        }, FlashFor);
    }

    /// <summary>提亮的过渡（400 ms，CSS ease = 三次贝塞尔 0.25, 0.1, 0.25, 1；底色与外圈一起，外圈从 <see cref="NoRing"/> 起）；冻结时没有。每个分区一份（Transitions 不能共用）。</summary>
    private static Transitions? FlashTransitions(Border sec)
    {
        if (Frozen.GetIsFrozen(sec)) return null;
        var ease = new SplineEasing(0.25, 0.1, 0.25, 1);
        var d = TimeSpan.FromMilliseconds(400);
        return new Transitions
        {
            new BrushTransition { Property = Border.BackgroundProperty, Duration = d, Easing = ease },
            new BoxShadowsTransition { Property = Border.BoxShadowProperty, Duration = d, Easing = ease },
        };
    }
}
