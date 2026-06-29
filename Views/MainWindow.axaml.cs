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

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private SchematicControl? _schematic;
    private DispatcherTimer? _tickTimer;
    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _animTimer;
    private DispatcherTimer? _warmTimer;
    private int _warmIdx;
    private static readonly string[] WarmTabs = { "program", "graph", "data", "alarm", "setting", "home" };
    private bool _closing;
    private double _phase;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _schematic = this.FindControl<SchematicControl>("Schematic");
        ApplyTextRenderingMode();
        DataContextChanged += OnDataContextChanged;
        Opened += OnOpened;
        Closed += OnClosed;
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

    private void OnOpened(object? sender, EventArgs e)
    {
        // 强制铺满整屏。borderless 窗口在部分 Linux 窗口管理器上不认 WindowState=FullScreen，
        // 因此按屏幕实际尺寸手动铺满（无边框 + 占满屏幕 = 看板/全屏效果）。
        GoFullScreen();

        // 实时数据 tick（1.2s，对应 HTML setInterval 节奏）
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _tickTimer.Tick += (_, _) => _vm?.Tick();
        _tickTimer.Start();

        // 时钟（1s）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => _vm?.ClockTick();
        _clockTimer.Start();

        // 动画：50ms ≈ 20fps，Background 优先级让触摸/输入优先处理，避免在弱板上卡顿
        _animTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, OnAnimTick);
        _animTimer.Start();

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
        if (_closing || _vm == null || _warmIdx >= WarmTabs.Length)
        {
            _warmTimer?.Stop();
            _warmTimer = null;
            _vm?.SwitchTab("home");   // 预热结束停在首页
            HideSplash();
            return;
        }
        _vm.SwitchTab(WarmTabs[_warmIdx]);   // 每拍切一页，让其首次布局/渲染发生（被遮罩盖住，用户看不到）
        _warmIdx++;
    }

    private void HideSplash()
    {
        var splash = this.FindControl<Border>("Splash");
        if (splash != null) splash.IsVisible = false;
    }

    /// <summary>把无边框窗口铺满当前屏幕（按屏幕实际像素/缩放手动铺满）。无放大变换，靠高 DPI 渲染保证锐利。</summary>
    private void GoFullScreen()
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null)
        {
            WindowState = WindowState.FullScreen;
            return;
        }

        var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        Position = screen.Bounds.Position;            // 物理像素坐标，置于屏幕左上角
        Width = screen.Bounds.Width / scaling;        // 逻辑单位 = 物理像素 / 缩放
        Height = screen.Bounds.Height / scaling;

        // 诊断：打印真实分辨率/缩放（看终端或 journalctl）
        Console.WriteLine($"[Display] 屏幕物理像素={screen.Bounds.Width}x{screen.Bounds.Height} 系统缩放={scaling} " +
                          $"窗口逻辑尺寸={Width:0}x{Height:0} 渲染缩放={RenderScaling} ClientSize={ClientSize.Width:0}x{ClientSize.Height:0}");
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (_closing) return;
        // 只在首页显示气路图、且没有模态弹窗时推进动画；其它页/弹窗时完全不重绘，省出 UI 线程给触摸
        if (_vm == null || _vm.ActiveTab != "home" || _vm.Keyboard.IsOpen || _vm.IsExitConfirmOpen) return;
        _phase += 0.05; // 50ms 一帧，保持约 1.0/秒 的相位速度
        if (_schematic != null) _schematic.Phase = _phase;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _tickTimer?.Stop();
        _clockTimer?.Stop();
        _animTimer?.Stop();
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
            "ok" => Color.Parse("#a3e635"),
            "warn" => Color.Parse("#f0a830"),
            _ => Color.Parse("#e0394c")
        };

        var toast = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F21E1E23")),
            BorderBrush = new SolidColorBrush(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(16, 11),
            Opacity = 0,
            Transitions = new Avalonia.Animation.Transitions
            {
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(220)
                }
            },
            Child = new TextBlock
            {
                Text = msg,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.Parse("#f6f6f8")),
                FontWeight = FontWeight.Medium
            }
        };
        host.Children.Add(toast);

        // 淡入（下一帧触发过渡）
        Dispatcher.UIThread.Post(() => toast.Opacity = 1, DispatcherPriority.Background);
        // 2.6s 后淡出移除
        var life = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        life.Tick += (_, _) =>
        {
            life.Stop();
            toast.Opacity = 0;
            var rm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            rm.Tick += (_, _) => { rm.Stop(); host.Children.Remove(toast); };
            rm.Start();
        };
        life.Start();
    }
}
