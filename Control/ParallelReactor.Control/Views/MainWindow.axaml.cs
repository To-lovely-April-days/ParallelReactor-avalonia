using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Shell;

namespace ParallelReactor.Control.Views;

/// <summary>
/// 主窗口：新外壳（ShellVm）。窗口尺寸变化时交给 ShellLayout（外框各区域只看窗口尺寸）；
/// Esc 依次：关对话框 → 还原放大的面板 → 收起资源管理器浮层（菜单 / 弹出框由 Avalonia 自己先关）。文件 › 退出 = 关窗（走正常的关闭流程）。
/// </summary>
public partial class MainWindow : Window
{
    private ShellVm? _shell;

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
        if (_shell != null) _shell.ExitRequested -= OnExit;
        _shell = DataContext as ShellVm;
        Host.Content = DataContext;
        if (_shell != null)
        {
            Title = _shell.WindowTitle;
            _shell.ExitRequested += OnExit;
            _shell.Layout.Update(ClientSize);
        }
    }

    private void OnExit(object? sender, EventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not ShellVm vm) return;
        if (vm.Dialogs.IsOpen) { vm.Dialogs.Close(); e.Handled = true; return; }
        if (this.GetVisualDescendants().OfType<PageHost>().FirstOrDefault()?.Escape() == true) { e.Handled = true; return; }
        e.Handled = vm.Escape();
    }
}
