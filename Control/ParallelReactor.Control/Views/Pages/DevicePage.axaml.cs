using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Views.Pages;

/// <summary>
/// 设备页视图（样稿 js/pages/device.js 的交互部分；版面与文字在 Pages/Device 的视图模型里）：
/// <list type="bullet">
/// <item>尺寸：根元素的尺寸（= 工作区，父元素给的）交给 <see cref="DevicePageVm.SetViewport"/>（Core 的 DeviceLayout 据此排版，R4）。</item>
/// <item>面板的放大 / 关闭（PrPanel 的 □ / ×，PageHost 原地放大 / 隐藏）告诉视图模型（<see cref="DevicePageVm.SetPanel"/>）：卡片区、下排、「报警与事件」是否可见跟着变。</item>
/// <item>卡片：单击 = 选中反应釜（检查器跟随），双击 / Enter = 打开反应釜页，空格 = 选中，方向键在卡片间移动（上下按列数跳行），右键 = 反应釜菜单（样稿 onKey / data-ctx）；
///   按住左键时卡片是按下的样子（.device-card:active：类 pressed）。</item>
/// <item>窄工作区的页签（PTabs）与事件筛选（SegControl）的选项是视图模型里的实例（OptionsSource 绑定：红点 / 数字 / 计数就地更新，
///   模板什么时候实例化都带着选项）；换页签后内容回到顶部。</item>
/// <item>宽度类变了（跨过 1100）：下排面板的放大撤掉（<see cref="DevicePageVm.PanelsUnmaximized"/>，同样稿换了一组面板）。</item>
/// </list>
/// </summary>
public partial class DevicePage : UserControl
{
    private DevicePageVm? _vm;
    /// <summary>按住左键的那张卡片（类 pressed）。</summary>
    private Panel? _pressed;

    static DevicePage()
    {
        PrPanel.IsMaximizedProperty.Changed.AddClassHandler<PrPanel>((p, _) => Notify(p));
        PrPanel.IsClosedProperty.Changed.AddClassHandler<PrPanel>((p, _) => Notify(p));
    }

    public DevicePage()
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
    }

    /// <summary>面板的放大 / 关闭变了：告诉这一页的视图模型（键 = 面板的 Tag）。</summary>
    private static void Notify(PrPanel p)
    {
        if (!p.Classes.Contains("dvpanel") || p.FindAncestorOfType<DevicePage>() is not { DataContext: DevicePageVm vm }) return;
        vm.SetPanel(KeyOf(p.Tag as string), p.IsMaximized, p.IsClosed);
    }

    private static DevicePanelKey KeyOf(string? tag) => tag switch
    {
        "rv" => DevicePanelKey.Rv,
        "res" => DevicePanelKey.Res,
        "info" => DevicePanelKey.Info,
        "log" => DevicePanelKey.Log,
        "tabs" => DevicePanelKey.Tabs,
        _ => DevicePanelKey.None,
    };

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.TabPicked -= OnTabPicked;
            _vm.PanelsUnmaximized -= OnPanelsUnmaximized;
        }
        _vm = DataContext as DevicePageVm;
        if (_vm == null) return;
        _vm.TabPicked += OnTabPicked;
        _vm.PanelsUnmaximized += OnPanelsUnmaximized;
        // 这个视图上的面板状态从头来（换了页面实例）
        _vm.ResetPanels();
        foreach (var p in this.GetVisualDescendants().OfType<PrPanel>()) Notify(p);
        if (Bounds.Width > 0) _vm.SetViewport(Bounds.Width, Bounds.Height);
    }

    /// <summary>换了页签：内容回到顶部（同一个面板，样稿 onClick 的 body.scrollTop = 0）。</summary>
    private void OnTabPicked(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var sv in TabsPanel.GetVisualDescendants().OfType<ScrollViewer>()) sv.Offset = default;
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// 宽度类变了：「设备信息」「报警与事件」（宽）/ 页签面板（窄）的放大撤掉 —— 那块面板跟着宽度类藏起来了，另一组面板不能还被放大藏着（样稿换了一组面板，
    /// is-max 跟着旧面板一起没了）。PageHost 收到 IsMaximized = false 就还原被藏起来的面板。
    /// </summary>
    private void OnPanelsUnmaximized(object? sender, EventArgs e)
    {
        foreach (var p in this.GetVisualDescendants().OfType<PrPanel>())
            if (p.IsMaximized && p.Classes.Contains("dvpanel") && KeyOf(p.Tag as string) is DevicePanelKey.Info or DevicePanelKey.Log or DevicePanelKey.Tabs)
                p.IsMaximized = false;
    }

    // ———————————————————— 卡片：单击 / 双击 / 右键 / 键盘 ————————————————————

    /// <summary>事件来源所在的卡片（在按钮里的不算：按钮自己处理）。</summary>
    private static (DeviceCardVm? Card, Panel? Tile) Hit(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
        {
            if (v is Button) return (null, null);
            if (v is Panel { DataContext: DeviceCardVm c } p && p.Classes.Contains("dvtile")) return (c, p);
        }
        return (null, null);
    }

    /// <summary>按下左键：那张卡片换成按下的样子（样稿 .device-card:active），松开 / 失去捕获时还原。</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        SetPressed(Hit(e.Source).Tile);
    }

    private void SetPressed(Panel? tile)
    {
        if (ReferenceEquals(tile, _pressed)) return;
        _pressed?.Classes.Remove("pressed");
        _pressed = tile;
        tile?.Classes.Add("pressed");
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        var (card, _) = Hit(e.Source);
        if (card == null) return;
        card.SelectCommand.Execute(null);
        e.Handled = true;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        var (card, _) = Hit(e.Source);
        if (card == null) return;
        card.OpenCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>右键（样稿 data-ctx）：先选中，再弹出反应釜菜单（同资源管理器 / 总览）。</summary>
    private void OnContext(object? sender, ContextRequestedEventArgs e)
    {
        var (card, tile) = Hit(e.Source);
        if (card == null || tile == null) return;
        card.SelectCommand.Execute(null);
        ContextMenus.Open(tile, card.ContextItems(), e);
        e.Handled = true;
    }

    /// <summary>卡片键盘（样稿 onKey）：Enter 打开反应釜页 · 空格选中 · 方向键在卡片间移动（上下按列数跳行）。</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Source is not Panel { DataContext: DeviceCardVm card } tile || !tile.Classes.Contains("dvtile")) return;
        switch (e.Key)
        {
            case Key.Enter: card.OpenCommand.Execute(null); e.Handled = true; return;
            case Key.Space: card.SelectCommand.Execute(null); e.Handled = true; return;
        }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down) || _vm == null) return;
        e.Handled = true;
        var cards = Cards.GetVisualDescendants().OfType<Panel>().Where(p => p.Classes.Contains("dvtile")).ToList();
        int i = cards.IndexOf(tile);
        int cols = _vm.Layout?.Grid.Cols is int c && c > 0 ? c : cards.Count;
        int j = e.Key switch { Key.Right => i + 1, Key.Left => i - 1, Key.Down => i + cols, _ => i - cols };
        if (i >= 0 && j >= 0 && j < cards.Count)
        {
            cards[j].Focus(NavigationMethod.Directional);
            cards[j].BringIntoView();
        }
    }
}
