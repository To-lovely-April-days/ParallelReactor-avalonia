using Avalonia;
using System;

namespace ParallelReactor;

internal static class Program
{
    // Linux 上请确保已安装字体与 libice/libsm 等依赖；详见 README。
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Linux/X11 下让弹层（通知下拉、ComboBox、ToolTip 等）在窗口内绘制，
            // 避免独立子窗口在嵌入式设备上"先黑框再出内容"的闪烁。
            .With(new X11PlatformOptions { OverlayPopups = true })
            .WithInterFont()
            .LogToTrace();
}
