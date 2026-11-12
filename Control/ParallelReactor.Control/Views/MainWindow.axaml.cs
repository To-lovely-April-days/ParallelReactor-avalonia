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
/// Esc 依次（样稿 keydown，一次只做一步）：打开着的菜单 / 弹出框（Views/Shell/PopupMenus 在隧道阶段先关掉它、焦点回到打开它的按钮，到此为止）→
/// 关对话框 → 还原放大的面板 → 收起资源管理器浮层。文件 › 退出 = 关窗（走正常的关闭流程）。
/// </summary>
public partial class MainWindow : Window
{
    private ShellVm? _shell;

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        PopupMenus.Attach(this);
        // 在 PopupMenus 之后登记：打开着的菜单先接方向键（从外面 ↓ / ↑ 进菜单）
        AddHandler(KeyDownEvent, OnArrowWithoutFocus, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// 没有焦点时方向键什么也不做（样稿：焦点在 body 上，方向键只滚动页面）。Avalonia 11.1 这时会把焦点挪到窗口里的第一个控件
    /// （菜单「文件」，带焦点环），接着 Enter 就打开了它。
    /// </summary>
    private void OnArrowWithoutFocus(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right)) return;
        var focused = FocusManager?.GetFocusedElement();
        if (focused is null || ReferenceEquals(focused, this)) e.Handled = true;
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
        PopupMenus.Watch(_shell);
        if (_shell != null)
        {
            Title = _shell.WindowTitle;
            _shell.ExitRequested += OnExit;
            _shell.Layout.Update(ClientSize);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_shell != null) PopupMenus.Unwatch(_shell);
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
