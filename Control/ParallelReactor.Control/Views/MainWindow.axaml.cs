using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views;

/// <summary>
/// 主窗口（融合版 F2）：外框（ShellVm → ShellView）。标题栏（F9，用户：「用我们自定义的」）按 <see cref="WindowChrome.Kind"/>：
/// 自绘（Windows）= 去掉系统标题栏，深蓝条当标题栏，窗口按钮自绘（StripView），可调大小的边框照旧；最大化时内容按 OffScreenMargin 缩进
/// （不然最大化的窗口四边各有几个像素在屏幕外）；macOS = 内容伸到顶、保留系统红黄绿；系统标题栏（Linux）= 不改。窗口尺寸变化时交给 DockLayout（外框只看窗口尺寸）。
/// 键盘：Esc 一次一步（对话框 → 报警列表 → 浮出的面板，ShellVm.Escape）；Ctrl+1…6 换到菜单栏的第 1…6 个页面；F11 全屏。
/// 退出（ShellVm.Exit）= 关窗（走正常的关闭流程）。
/// </summary>
public partial class MainWindow : Window
{
    private ShellVm? _shell;
    private WindowState _beforeFull = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        ApplyChrome(WindowChrome.Kind);
    }

    private void ApplyChrome(ChromeKind kind)
    {
        if (kind == ChromeKind.Native) return;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = kind == ChromeKind.Custom ? Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome
            : Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
        // 标题栏高度 = 深蓝条 30（macOS 红黄绿按这个高度竖向居中）
        ExtendClientAreaTitleBarHeightHint = 30;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) _shell?.Layout.Update(ClientSize);
        if (change.Property == OffScreenMarginProperty || change.Property == WindowStateProperty)
            Padding = WindowChrome.Kind == ChromeKind.Custom && WindowState == WindowState.Maximized ? OffScreenMargin : default;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell != null) { _shell.ExitRequested -= OnExit; _shell.PropertyChanged -= OnShell; }
        _shell = DataContext as ShellVm;
        Host.Content = DataContext;
        if (_shell != null)
        {
            Title = _shell.WindowTitle;
            _shell.ExitRequested += OnExit;
            _shell.PropertyChanged += OnShell;
            _shell.Layout.Update(ClientSize);
        }
    }

    private void OnShell(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellVm.IsFullScreen) || _shell == null) return;
        if (_shell.IsFullScreen) { _beforeFull = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState; WindowState = WindowState.FullScreen; }
        else WindowState = _beforeFull;
    }

    private void OnExit(object? sender, EventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ShellVm vm || e.Handled) return;
        if (e.Key == Key.Escape) { e.Handled = vm.Escape(); return; }
        if (e.Key == Key.F11) { vm.ToggleFullScreen(); e.Handled = true; return; }
        // Ctrl+1…6（macOS 上 ⌘ 也行）
        if (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta && e.Key >= Key.D1 && e.Key <= Key.D6 && !vm.Dialogs.IsOpen)
        {
            vm.Go(AppPages.Menu[e.Key - Key.D1].Page);
            e.Handled = true;
        }
    }
}
