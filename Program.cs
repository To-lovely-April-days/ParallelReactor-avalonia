using Avalonia;
using System;

namespace ParallelReactor;

internal static class Program
{
    // Linux 上请确保已安装字体与 libice/libsm 等依赖；详见 README。
    [STAThread]
    public static void Main(string[] args)
    {
        // 强制 UI 高 DPI 渲染缩放：让文字/矢量按设备像素栅格化，显示锐利（而非把 720p 画面放大）。
        // 默认 1.5（适配 1080p 面板 + 1280×720 设计）；可用 PR_UI_SCALE 覆盖，已手动设置则尊重。
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR")))
            Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR",
                Environment.GetEnvironmentVariable("PR_UI_SCALE") ?? "1.5");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Linux/X11 下让弹层（通知下拉、ComboBox、ToolTip 等）在窗口内绘制，
            // 避免独立子窗口在嵌入式设备上"先黑框再出内容"的闪烁。
            // RenderingMode 优先 EGL（RK3568 Mali GPU 走 EGL，启用硬件加速大幅减少卡顿），
            // 不可用时依次回退 GLX、软件渲染。
            .With(new X11PlatformOptions
            {
                OverlayPopups = true,
                RenderingMode = new[] { X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software },
            })
            .WithInterFont()
            .LogToTrace();
}
