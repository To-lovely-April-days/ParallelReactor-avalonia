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
using ParallelReactor.ViewModels;
using System.Linq;

namespace ParallelReactor.Views;

/// <summary>
/// 主界面内容（单视图）。既可被 <see cref="MainWindow"/> 托管跑在 X11/桌面，
/// 也可被 DRM 单视图直出（无窗口、无 X）——由 App 按生命周期选择。
/// 窗口专属逻辑（铺满屏幕）在 <see cref="MainWindow"/>；本类只管内容与交互。
/// </summary>
public partial class MainView : UserControl
{
    private MainViewModel? _vm;
    private SchematicControl? _schematic;
    private DispatcherTimer? _tickTimer;
    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _warmTimer;
    private int _warmIdx;
    private int _warmSetIdx = -1;   // 设置子页预热进度：-1=未开始
    private static readonly string[] WarmTabs = { "program", "graph", "data", "alarm", "setting", "home" };
    private bool _closing;
    private bool _started;           // Loaded 可能多次触发，只初始化一次

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
            _vm.ToastRequested -= ShowToast;
            _vm.TabSwitched -= OnTabSwitched;
        }
        _vm = DataContext as MainViewModel;
        if (_vm == null || _schematic == null) return;

        _schematic.Reactors = _vm.Reactors;
        _schematic.GasInlets = _vm.GasInlets;
        _schematic.VentOn = _vm.VentOn;
        _schematic.OnReactorClick = id => _vm.OpenDrawer(id);
        _schematic.OnValveClick = id => _vm.TryValve(id);
        _schematic.OnGasClick = i => _vm.ToggleGas(i);
        _schematic.OnVentClick = () => _vm.ToggleVent();

        _vm.SchematicInvalidated += RefreshSchematic;
        _vm.ToastRequested += ShowToast;
        _vm.TabSwitched += OnTabSwitched;
    }

    private void RefreshSchematic()
    {
        if (_schematic != null && _vm != null)
        {
            _schematic.VentOn = _vm.VentOn;
            _schematic.InvalidateVisual();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_started) return;   // 挂载后只初始化一次（Loaded 可能重复触发）
        _started = true;

        // 实时数据 tick（1.2s，对应 HTML setInterval 节奏）
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _tickTimer.Tick += (_, _) => _vm?.Tick();
        _tickTimer.Start();

        // 时钟（1s）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => _vm?.ClockTick();
        _clockTimer.Start();

        // 已移除持续动画：气路图改为事件驱动，仅在数据/状态变化时（Tick 调 RefreshSchematic）重绘一次，
        // 弱板上空闲 CPU 接近 0、点击即时响应。

        StartWarmup();
    }

    // ============ 启动预热：开机时把各页各渲染一次（JIT + 布局预热），消除首次进菜单卡顿 ============
    private void StartWarmup()
    {
        if (_vm == null) { HideSplash(); return; }
        _warmIdx = 0;
        _warmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _warmTimer.Tick += WarmStep;
        _warmTimer.Start();
    }

    private void WarmStep(object? sender, EventArgs e)
    {
        if (_closing || _vm == null) { FinishWarm(); return; }

        // 阶段一：逐个切 tab，让每页首次布局/渲染发生（被遮罩盖住，用户看不到）
        if (_warmIdx < WarmTabs.Length)
        {
            _vm.SwitchTab(WarmTabs[_warmIdx]);
            _warmIdx++;
            return;
        }

        // 阶段二：停在设置页，逐个切设置子页预热——把「首次点各设置子页」的 JIT/实例化开销
        // 全部前置到开机遮罩期间，消除运行时点设置左菜单的一次性卡顿。
        var pages = _vm.Setting.Pages;
        if (_warmSetIdx < 0)
        {
            _vm.SwitchTab("setting");   // 设置页可见，子页内容才会真正 realize
            _warmSetIdx = 0;
            return;
        }
        if (_warmSetIdx < pages.Count)
        {
            _vm.Setting.Selected = pages[_warmSetIdx];
            _warmSetIdx++;
            return;
        }

        FinishWarm();
    }

    private void FinishWarm()
    {
        _warmTimer?.Stop();
        _warmTimer = null;
        if (_vm != null)
        {
            if (_vm.Setting.Pages.Count > 0) _vm.Setting.Selected = _vm.Setting.Pages[0];   // 复位到首个设置子页
            _vm.SwitchTab("home");   // 预热结束停在首页
        }
        HideSplash();
    }

    private void HideSplash()
    {
        var splash = this.FindControl<Border>("Splash");
        if (splash != null) splash.IsVisible = false;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _closing = true;
        _tickTimer?.Stop();
        _clockTimer?.Stop();
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
