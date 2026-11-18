using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views;

/// <summary>
/// 主窗口（融合版 F2）：系统标题栏 + 外框（ShellVm → ShellView）。窗口尺寸变化时交给 DockLayout（外框只看窗口尺寸）。
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
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) _shell?.Layout.Update(ClientSize);
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
