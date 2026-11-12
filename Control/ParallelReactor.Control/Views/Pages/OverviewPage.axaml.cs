using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Behaviors;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Views.Pages;

/// <summary>
/// 总览视图（样稿 js/pages/overview.js 的交互部分；版面与文字在 Pages/Overview 的视图模型里）：
/// <list type="bullet">
/// <item>尺寸：根元素的尺寸（= 工作区，父元素给的）交给 <see cref="OverviewPageVm.SetViewport"/>（Core 的 OverviewLayout 据此排版，R4）。</item>
/// <item>单击卡片 = 选中反应釜，双击 = 打开反应釜页签，右键 = 反应釜菜单；单击分区（头或空白）= 选中设备，双击 = 打开设备页，右键 = 设备菜单
///   （样稿 data-sel / data-open / data-ctx）；按钮自己处理自己的点击，但右键分区里的按钮（打开、«⋯»、放大、立即重连、报警小块）也是设备菜单
///   （样稿 closest('[data-ctx]')：按钮属于分区）。</item>
/// <item>«⋯»：设备菜单（打开时生成、打开着时刷新，同右键菜单 + 「设备信息」），打开时选中这台设备（样稿 openMenu）；「设备信息」锚在 «⋯» 下
///   （右对齐、下沿 + 2；开着时 «⋯» 是按下的样子，再点 «⋯» 一下关掉它并打开菜单）。键盘、点在外面、Esc 见 Views/Shell/PopupMenus。</item>
/// <item>键盘（样稿 onKey）：卡片可 Tab 到，Enter = 打开反应釜页签，空格 = 选中，方向键按位置在卡片之间移动（跨设备分区）；
///   分区头也可 Tab 到（焦点环画在头里面 2 px），空格 = 选中设备，Enter = 打开设备页。</item>
/// <item>按下（样稿 :active）：按住左键时卡片是按下的样子（.overview-card:active：类 pressed），分区头 bg-press（头里的按钮按下时也算，同 CSS 的 :active 链）。</item>
/// <item>放大分区（PrPanel 的 □，PageHost.InPlaceMax）：以滚动区为定位框 —— 滚动区回到顶部、不再滚动，还原后回到放大前的滚动位置（样稿 paint 的 savedScroll）。</item>
/// <item>滚动请求：报警条「定位」滚到那台设备的分区顶部；演示参数 scroll=end 滚到底，换了参数回到顶部。请求记在视图模型里（PendingScroll），
///   视图排好版、显示着的时候才滚（LayoutUpdated）—— 请求可能在视图建好之前、或总览藏着的时候发出。</item>
/// </list>
/// </summary>
public partial class OverviewPage : UserControl
{
    private OverviewPageVm? _vm;
    private double? _savedScroll;
    /// <summary>按住左键的那张卡片 / 那个分区（类 pressed）。</summary>
    private StyledElement? _pressed;

    static OverviewPage()
    {
        PrPanel.IsMaximizedProperty.Changed.AddClassHandler<PrPanel>((p, e) =>
        {
            if (!p.Classes.Contains("ovdev") || p.DataContext is not OverviewSectionVm s) return;
            s.Page.SetMax(s.K, e.GetNewValue<bool>());
        });
    }

    public OverviewPage()
    {
        InitializeComponent();
        AddHandler(Gestures.TappedEvent, OnTapped, RoutingStrategies.Bubble);
        AddHandler(Gestures.DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
        AddHandler(ContextRequestedEvent, OnContext, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => SetPressed(null), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => SetPressed(null), RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, e) => _vm?.SetViewport(e.NewSize.Width, e.NewSize.Height);
        LayoutUpdated += (_, _) => ApplyPendingScroll();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.ScrollRequested -= OnScrollRequested;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = DataContext as OverviewPageVm;
        if (_vm != null)
        {
            _vm.ScrollRequested += OnScrollRequested;
            _vm.PropertyChanged += OnVmChanged;
            if (Bounds.Width > 0) _vm.SetViewport(Bounds.Width, Bounds.Height);
            // 视图建好之前发出的请求（页面刚建时 Refresh 先跑）：等第一次排版完成再滚
            if (_vm.PendingScroll != null) Scroll.InvalidateMeasure();
        }
    }

    // ———————————————————— 单击 / 双击 / 右键 ————————————————————

    /// <summary>事件来源所在的卡片 / 分区。<paramref name="throughButtons"/> = false 时在按钮里的不算（单击 / 双击：按钮自己处理）；右键要算（按钮属于分区）。</summary>
    private static (OverviewCardVm? Card, OverviewSectionVm? Section, Visual? Anchor) Hit(object? source, bool throughButtons = false)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
        {
            if (v is Button && !throughButtons) return (null, null, null);
            if (v is Panel { DataContext: OverviewCardVm c } p && p.Classes.Contains("ovtile")) return (c, c.Section, p);
            if (v is PrPanel { DataContext: OverviewSectionVm s } pp && pp.Classes.Contains("ovdev")) return (null, s, pp);
        }
        return (null, null, null);
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        var (card, sec, _) = Hit(e.Source);
        if (card != null) { card.SelectCommand.Execute(null); e.Handled = true; }
        else if (sec != null) { sec.SelectCommand.Execute(null); e.Handled = true; }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var (card, sec, _) = Hit(e.Source);
        if (card != null) { card.OpenCommand.Execute(null); e.Handled = true; }
        else if (sec != null) { sec.OpenCommand.Execute(null); e.Handled = true; }
    }

    /// <summary>右键（样稿 data-ctx）：先选中，再弹出菜单 —— 卡片 = 反应釜菜单，分区（含分区里的按钮）= 设备菜单（同 «⋯»）。</summary>
    private void OnContext(object? sender, ContextRequestedEventArgs e)
    {
        var (card, sec, anchor) = Hit(e.Source, throughButtons: true);
        if (anchor is not Avalonia.Controls.Control target) return;
        if (card != null)
        {
            card.SelectCommand.Execute(null);
            ContextMenus.Open(target, card.ContextItems(), e);
            e.Handled = true;
        }
        else if (sec != null)
        {
            sec.SelectCommand.Execute(null);
            ContextMenus.Open(target, sec.ContextItems(), e);
            e.Handled = true;
        }
    }

    // ———————————————————— 按下 ————————————————————

    /// <summary>
    /// 按下左键：卡片（不在按钮里）换成按下的样子（.overview-card:active：bg-hover、line-strong 边、没有阴影）；在分区头里（含头里的按钮，
    /// 同 CSS 的 :active 链）分区头 bg-press。松开 / 失去捕获时还原。
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        SetPressed(PressTarget(e.Source));
    }

    /// <summary>按下的是哪张卡片（Panel.ovtile）或哪个分区的头（返回分区的 PrPanel）；都不是返回 null。</summary>
    private static StyledElement? PressTarget(object? source)
    {
        bool inButton = false;
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
        {
            if (v is Button) inButton = true;
            if (v is Panel p && p.Classes.Contains("ovtile")) return inButton ? null : p;
            if (v is Border { Name: "PART_Head" } head && head.TemplatedParent is PrPanel pp && pp.Classes.Contains("ovdev")) return pp;
            if (v is PrPanel) return null;
        }
        return null;
    }

    private void SetPressed(StyledElement? target)
    {
        if (ReferenceEquals(target, _pressed)) return;
        _pressed?.Classes.Remove("pressed");
        _pressed = target;
        target?.Classes.Add("pressed");
    }

    // ———————————————————— «⋯» 菜单 / 设备信息 ————————————————————

    private void OnMoreOpening(object? sender, EventArgs e)
    {
        if (sender is not DynamicMenuFlyout mf || mf.Target is not Button btn || btn.DataContext is not OverviewSectionVm s) return;
        s.SelectCommand.Execute(null);   // 样稿 openMenu：打开菜单时选中这台设备（检查器跟过去）
        mf.SetEntries(s.MenuItems(() => Dispatcher.UIThread.Post(() => ShowInfo(btn, s))));
    }

    /// <summary>最近一次打开的「设备信息」弹出框（截图核对 Shot/ShellChecks 用）。</summary>
    internal static Flyout? LastInfo { get; private set; }

    /// <summary>
    /// 设备信息（样稿 infoPop：锚在 «⋯» 下的弹出框，右对齐、下沿 + 2，宽 340）：设备图标 + 别名 16/600 + 型号 14 t-3 · 键值表 · 「打开设备页」。
    /// PrFlyout：开着时 «⋯» 是按下的样子；它不是 «⋯» 自己的 Flyout，点 «⋯» 一下就关掉它并打开菜单（样稿 same = false）。
    /// </summary>
    private static void ShowInfo(Button anchor, OverviewSectionVm s)
    {
        LastInfo?.Hide();
        var head = new BaselineStack { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new IconView { Icon = "machine", Size = 18, Foreground = Palette.T3Brush, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new PrText { Text = s.Alias, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Palette.T1Brush, LineHeight = 23 });
        head.Children.Add(new PrText { Text = s.Model, FontSize = 14, Foreground = Palette.T3Brush, LineHeight = 20 });
        var kv = new Kv { RowSpacing = 6, ColumnSpacing = 16 };
        foreach (var (k, v) in s.InfoRows())
        {
            kv.Children.Add(new TextBlock { Text = k });
            kv.Children.Add(new TextBlock { Text = v, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }
        var flyout = new PrFlyout { Placement = PlacementMode.BottomEdgeAlignedRight, VerticalOffset = 2 };
        var open = new Button { Content = "打开设备页", HorizontalAlignment = HorizontalAlignment.Right };
        open.Classes.Add("sm");
        Pr.SetIcon(open, "open");
        open.Click += (_, _) => { flyout.Hide(); s.OpenCommand.Execute(null); };
        var foot = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            BorderBrush = Palette.LineFaintBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = open,
        };
        var body = new StackPanel { Width = 340 - 32, Margin = new Thickness(16, 14, 16, 12) };
        body.Children.Add(head);
        body.Children.Add(kv);
        body.Children.Add(foot);
        flyout.Content = body;
        LastInfo = flyout;
        flyout.ShowAt(anchor);
    }

    // ———————————————————— 键盘 ————————————————————

    /// <summary>
    /// 卡片：Enter = 打开反应釜页签，空格 = 选中，方向键按位置移到下一张卡片（左右在一行里，上下跨行、跨设备分区；样稿 cardInDir）。
    /// 分区头（焦点在头上，不是头里的按钮）：Enter = 打开设备页，空格 = 选中设备（样稿 onKey）。
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Source is Border { Name: "PART_Head" } head && head.TemplatedParent is PrPanel { DataContext: OverviewSectionVm sec } panel && panel.Classes.Contains("ovdev"))
        {
            if (e.Key == Key.Enter) { sec.OpenCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Space) { sec.SelectCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (e.Source is not Panel { DataContext: OverviewCardVm card } tile || !tile.Classes.Contains("ovtile")) return;
        switch (e.Key)
        {
            case Key.Enter: card.OpenCommand.Execute(null); e.Handled = true; break;
            case Key.Space: card.SelectCommand.Execute(null); e.Handled = true; break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                e.Handled = true;
                if (CardInDir(tile, e.Key) is { } next) { next.Focus(NavigationMethod.Directional); next.BringIntoView(); }
                break;
        }
    }

    private Panel? CardInDir(Panel from, Key key)
    {
        var c0 = Rect(from);
        if (c0 is not { } r0) return null;
        double cx = r0.Center.X, cy = r0.Center.Y;
        Panel? best = null;
        double bestD = double.MaxValue;
        foreach (var p in this.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("ovtile") && p.IsEffectivelyVisible && !ReferenceEquals(p, from)))
        {
            if (Rect(p) is not { } r) continue;
            double dx = r.Center.X - cx, dy = r.Center.Y - cy, d;
            bool ok;
            if (key is Key.Left or Key.Right) { ok = Math.Abs(dy) < r0.Height / 2 && (key == Key.Right ? dx > 4 : dx < -4); d = Math.Abs(dx); }
            else { ok = key == Key.Down ? dy > 4 : dy < -4; d = Math.Abs(dy) * 4 + Math.Abs(dx); }
            if (ok && d < bestD) { best = p; bestD = d; }
        }
        return best;
    }

    private Rect? Rect(Visual v) => v.TransformToVisual(this) is { } m ? new Rect(v.Bounds.Size).TransformToAABB(m) : null;

    // ———————————————————— 滚动：定位 / 到底 / 放大时停在顶部 ————————————————————

    /// <summary>新的滚动请求（视图模型记在 PendingScroll 里）：排完版再滚（分区的高度、报警条可能刚变）—— 保证有一次排版，在它完成时处理。</summary>
    private void OnScrollRequested(object? sender, OverviewScrollRequest r) => Scroll.InvalidateMeasure();

    /// <summary>
    /// 每次排版完成：有还没处理的滚动请求、总览显示着、滚动区排好了，就滚过去并清掉请求。总览藏着时（别的页签在前面）请求留着，
    /// 回到总览、排好版之后再滚（M4 复核：overview4dev 在 reactor11 之后截图时没有滚到底）。
    /// </summary>
    private void ApplyPendingScroll()
    {
        if (_vm?.PendingScroll is not { } r || !IsEffectivelyVisible) return;
        var sc = Scroll;
        if (!sc.IsMeasureValid || !sc.IsArrangeValid || sc.Viewport.Height <= 0) return;
        _vm.TakeScroll();
        ScrollTo(r);
    }

    private void ScrollTo(OverviewScrollRequest r)
    {
        var sc = Scroll;
        if (r.ToTop) { sc.Offset = new Vector(sc.Offset.X, 0); return; }
        if (r.ToEnd) { sc.Offset = new Vector(sc.Offset.X, Math.Max(0, sc.Extent.Height - sc.Viewport.Height)); return; }
        if (_vm?.Sections.FirstOrDefault(s => s.K == r.K) is not { } sec) return;
        if (SectionList.ContainerFromItem(sec) is not Avalonia.Controls.Control c || c.TranslatePoint(default, SectionList) is not { } p) return;
        sc.Offset = new Vector(sc.Offset.X, Math.Min(p.Y, Math.Max(0, sc.Extent.Height - sc.Viewport.Height)));
    }

    /// <summary>放大 / 还原分区：放大时记下滚动位置、回到顶部并停止滚动；还原后回到原来的位置（样稿 savedScroll）。</summary>
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OverviewPageVm.MaxK) || _vm == null) return;
        var sc = Scroll;
        // 放大时别的分区不显示、放大的分区高 = 滚动区高度：内容正好一屏，滚动区自然不再滚动（不切换 VerticalScrollBarVisibility ——
        // 11.1 从 Disabled 换回 Auto 后滚动范围不再跟着内容变）
        if (_vm.MaxK != null)
        {
            _savedScroll ??= sc.Offset.Y;
            sc.Offset = new Vector(sc.Offset.X, 0);
        }
        else
        {
            if (_savedScroll is double y)
            {
                _savedScroll = null;
                // 别的分区刚显示回来：先排一次版（滚动范围变回原来的高度），再回到原来的位置（否则被当时的范围夹到 0）
                Dispatcher.UIThread.Post(() =>
                {
                    sc.UpdateLayout();
                    sc.Offset = new Vector(sc.Offset.X, Math.Min(y, Math.Max(0, sc.Extent.Height - sc.Viewport.Height)));
                }, DispatcherPriority.Background);
            }
        }
    }
}
