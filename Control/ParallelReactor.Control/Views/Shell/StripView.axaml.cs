using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 深蓝条（见 StripView.axaml）。F9 起它也是窗口标题栏（<see cref="WindowChrome"/>）：
/// 自绘时右端的最小化 / 最大化（还原）/ 关闭操作所在的窗口；不是系统标题栏时空白处按住拖动窗口、双击最大化 / 还原。
/// 「关闭」走窗口的正常关闭流程（App 里 Closing 照样先问要不要退出）。
/// </summary>
public partial class StripView : UserControl
{
    private Window? _window;

    public StripView()
    {
        InitializeComponent();
        MinButton.Click += (_, _) => { if (_window != null) _window.WindowState = WindowState.Minimized; };
        MaxButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => _window?.Close();
        Bar.PointerPressed += OnBarPressed;
        ApplyChrome(WindowChrome.Kind);
    }

    /// <summary>按外框画法摆深蓝条：自绘 = 右端三个窗口按钮、右边不留白（样稿 padding 0 0 0 8）；macOS = 左边让出红黄绿；系统标题栏 = 右边留 8。</summary>
    internal void ApplyChrome(ChromeKind kind)
    {
        bool custom = kind == ChromeKind.Custom;
        WinButtons.IsVisible = custom;
        Bar.Padding = new Thickness(8, 0, custom ? 0 : 8, 0);
        MacInset.IsVisible = kind == ChromeKind.MacInset;
        // 红黄绿从窗口左边 ~70 DIP 处结束；深蓝条左边已经有 8，间距 10 —— 让出的宽度扣掉这两段
        MacInset.Width = kind == ChromeKind.MacInset ? WindowChrome.MacInsetWidth - 8 - 10 : 0;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window != null) _window.PropertyChanged += OnWindowProperty;
        SyncMaxGlyph();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window != null) _window.PropertyChanged -= OnWindowProperty;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowProperty(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) SyncMaxGlyph();
    }

    /// <summary>最大化之后按钮换成「还原」（两个叠着的方框，Windows 的习惯）。</summary>
    private void SyncMaxGlyph()
    {
        bool max = _window?.WindowState == WindowState.Maximized;
        MaxGlyph.IsVisible = !max;
        RestoreGlyph.IsVisible = max;
        ToolTip.SetTip(MaxButton, max ? "还原" : "最大化");
        Avalonia.Automation.AutomationProperties.SetName(MaxButton, max ? "还原" : "最大化");
    }

    private void ToggleMaximize()
    {
        if (_window == null || _window.WindowState == WindowState.FullScreen) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>空白处（按钮会先吃掉按下）：双击最大化 / 还原，按住拖动窗口。系统标题栏时不管（系统自己有）。</summary>
    private void OnBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowChrome.Kind == ChromeKind.Native || _window == null || e.Handled) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { ToggleMaximize(); e.Handled = true; return; }
        if (_window.WindowState == WindowState.FullScreen) return;
        _window.BeginMoveDrag(e);
        e.Handled = true;
    }
}
