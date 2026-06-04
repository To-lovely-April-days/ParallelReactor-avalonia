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
            .WithInterFont()
            .LogToTrace();
}
