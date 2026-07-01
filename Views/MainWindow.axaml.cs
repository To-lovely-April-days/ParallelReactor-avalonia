using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ParallelReactor.Views;

/// <summary>
/// X11/桌面下的窗口外壳：无边框、开机铺满整屏，内容托管 <see cref="MainView"/>。
/// DRM 单视图直出时不用本类（App 直接把 MainView 作为 MainView 上屏）。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) => GoFullScreen();
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
}
