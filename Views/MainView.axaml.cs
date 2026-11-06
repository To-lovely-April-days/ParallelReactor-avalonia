using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ParallelReactor.Controls;
using ParallelReactor.Services;
using ParallelReactor.ViewModels;
using System.Linq;

namespace ParallelReactor.Views;

/// <summary>
/// 主界面内容（单视图）。既可被 <see cref="MainWindow"/> 托管跑在 X11/桌面，
/// 也可被 DRM 单视图直出（无窗口、无 X）——由 App 按生命周期选择。
/// 窗口专属逻辑（铺满屏幕）在 <see cref="MainWindow"/>；本类只管内容与交互。
/// Tick / 时钟 / 开机预热的定时器由 <see cref="ControlLoop"/> 持有（C1）：View 卸载后控制循环照常运行。
/// </summary>
public partial class MainView : UserControl, IWarmupHost
{
    private MainViewModel? _vm;
    private SchematicControl? _schematic;
    private bool _closing;
    private bool _started;           // Loaded 可能多次触发，只初始化一次
    private bool _toastDetached;     // 卸载时退订了 Toast（VM 改为暂存），重新挂载时再订阅
    // 开机遮罩还在时到达的 Toast（含 VM 暂存的开机提示）：遮罩盖住 ToastHost，2.6s 寿命会在遮罩下耗尽，
    // 所以先留着，HideSplash 时再显示。最多 20 条，保留最早的。
    private bool _splashUp = true;
    private readonly System.Collections.Generic.List<(string kind, string msg)> _splashToasts = new();

    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
        _schematic = this.FindControl<SchematicControl>("Schematic");
        ApplyTextRenderingMode();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 文本渲染模式：可用环境变量 PR_TEXT_MODE 切换以对比清晰度/粗细（改完重启即可，无需重编译）。
    ///   alias      = 不抗锯齿（最细最锐，可能有锯齿）
    ///   antialias  = 灰度抗锯齿（默认，较平滑）
    ///   subpixel   = 次像素（类 ClearType，最平滑但可能偏粗/有彩边）
    /// </summary>
    private void ApplyTextRenderingMode()
    {
        var canvas = this.FindControl<Panel>("DesignCanvas");
        if (canvas == null) return;
        var mode = (Environment.GetEnvironmentVariable("PR_TEXT_MODE") ?? "antialias").Trim().ToLowerInvariant() switch
        {
            "alias" => Avalonia.Media.TextRenderingMode.Alias,
            "subpixel" => Avalonia.Media.TextRenderingMode.SubpixelAntialias,
            _ => Avalonia.Media.TextRenderingMode.Antialias,
        };
        Avalonia.Media.RenderOptions.SetTextRenderingMode(canvas, mode);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.SchematicInvalidated -= RefreshSchematic;
            if (!_toastDetached) _vm.ToastRequested -= ShowToast;
            _vm.TabSwitched -= OnTabSwitched;
        }
        _vm = DataContext as MainViewModel;
        if (_vm == null || _schematic == null) return;

        _schematic.Reactors = _vm.Reactors;
        _schematic.GasInlets = _vm.GasInlets;
        _schematic.VentOn = _vm.VentOn;
        _schematic.OnReactorClick = id => _vm.OpenDrawer(id);
        _schematic.OnValveClick = id => _vm.ValveClicked(id);
        _schematic.OnGasClick = i => _vm.GasClicked(i);
        _schematic.OnVentClick = () => _vm.VentClicked();
        _schematic.ManualValve = _vm.ManualValve;
        _schematic.OnManualClick = () => _vm.ToggleManualCommand.Execute(null);

        _vm.SchematicInvalidated += RefreshSchematic;
        _toastDetached = false;
        _vm.ToastRequested += ShowToast;
        _vm.TabSwitched += OnTabSwitched;
    }

    private void RefreshSchematic()
    {
        if (_schematic != null && _vm != null)
        {
            _schematic.VentOn = _vm.VentOn;
            _schematic.ManualValve = _vm.ManualValve;
            _schematic.InvalidateVisual();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_toastDetached && _vm != null)
        {
            _toastDetached = false;
            _vm.ToastRequested += ShowToast;   // 卸载期间 VM 暂存的提示在这里补发
        }
        if (_started) return;   // 挂载后只初始化一次（Loaded 可能重复触发）
        _started = true;

        // 实时数据 tick / 时钟由控制循环驱动（App 启动时已开始）。
        // 已移除持续动画：气路图改为事件驱动，仅在数据/状态变化时（Tick 调 RefreshSchematic）重绘一次，
        // 弱板上空闲 CPU 接近 0、点击即时响应。

        // 启动预热：开机时把各页各渲染一次（JIT + 布局预热），消除首次进菜单卡顿（序列见 ControlLoop）
        if (_vm?.Loop is { } loop) loop.StartWarmup(this);
        else HideSplash();
    }

    bool IWarmupHost.IsClosing => _closing;
    void IWarmupHost.HideSplash() => HideSplash();

    private void HideSplash()
    {
        var splash = this.FindControl<Border>("Splash");
        if (splash != null) splash.IsVisible = false;
        _splashUp = false;
        foreach (var (k, m) in _splashToasts) ShowToast(k, m);   // 遮罩期间的提示（开机提示、首几拍 Tick）现在才看得见
        _splashToasts.Clear();
    }

    // 标记关闭（预热据此提前结束）并退订 Toast（之后的提示由 VM 暂存）；不停定时器——控制循环与 View 无关（C1）
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _closing = true;
        if (_vm != null && !_toastDetached)
        {
            _vm.ToastRequested -= ShowToast;
            _toastDetached = true;
        }
    }

    // 点击遮罩关闭抽屉
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
        => _vm?.Drawer.CloseCommand.Execute(null);

    // ============ 通知铃铛：点击展开/收起下拉列表（根层浮层，随 Viewbox 缩放）============
    private void OnBellClick(object? sender, RoutedEventArgs e)
    {
        var overlay = this.FindControl<Panel>("NotifOverlay");
        if (overlay != null) overlay.IsVisible = !overlay.IsVisible;
    }

    // 点击空白处关闭下拉
    private void OnNotifDismiss(object? sender, PointerPressedEventArgs e)
    {
        var overlay = this.FindControl<Panel>("NotifOverlay");
        if (overlay != null) overlay.IsVisible = false;
    }

    // 点击「查看历史 →」后关闭下拉
    private void OnNotifLinkClick(object? sender, RoutedEventArgs e)
    {
        var overlay = this.FindControl<Panel>("NotifOverlay");
        if (overlay != null) overlay.IsVisible = false;
    }

    // 导航药丸高亮
    private void OnTabSwitched(string id)
    {
        var pills = this.FindControl<Panel>("Pills");
        if (pills == null) return;
        foreach (var child in pills.Children.OfType<Button>())
        {
            bool on = (child.CommandParameter as string) == id;
            if (on) child.Classes.Add("active");
            else child.Classes.Remove("active");
        }
    }

    // ============ Toast ============
    private void ShowToast(string kind, string msg)
    {
        if (_splashUp)
        {
            if (_splashToasts.Count < 20) _splashToasts.Add((kind, msg));
            return;
        }
        var host = this.FindControl<StackPanel>("ToastHost");
        if (host == null) return;

        Color border = kind switch
        {
            "ok" => Color.Parse("#5fae14"),
            "warn" => Color.Parse("#c9820f"),
            _ => Color.Parse("#e0394c")
        };

        var toast = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F2FFFFFF")),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(16, 11),
            Child = new TextBlock
            {
                Text = msg,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.Parse("#17171c")),
                FontWeight = FontWeight.Medium
            }
        };
        host.Children.Add(toast);

        // 2.6s 后直接移除（无淡入淡出动画）
        var life = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        life.Tick += (_, _) => { life.Stop(); host.Children.Remove(toast); };
        life.Start();
    }
}
