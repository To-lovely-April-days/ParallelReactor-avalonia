using Avalonia;
using System;

namespace ParallelReactor;

internal static class Program
{
    // Linux 上请确保已安装字体与 libice/libsm 等依赖；详见 README。
    [STAThread]
    public static void Main(string[] args)
    {
        bool drm = (Environment.GetEnvironmentVariable("PR_DRM") ?? "") is "1" or "true";
        double scale = double.TryParse(Environment.GetEnvironmentVariable("PR_UI_SCALE"), out var s) ? s : 1.5;

        // 强制 UI 高 DPI 渲染缩放：让文字/矢量按设备像素栅格化，显示锐利（而非把 720p 画面放大）。
        // 默认 1.5（适配 1080p 面板 + 1280×720 设计）；可用 PR_UI_SCALE 覆盖，已手动设置则尊重。
        // 注意：DRM 走 StartLinuxDrm 的 scaling 参数，不再叠加全局缩放，避免双重放大。
        if (!drm && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR")))
            Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR",
                scale.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (drm)
        {
            // 专用面板：DRM/KMS 直出，无需 X/Wayland。省掉 Xorg 的 CPU+内存+合成开销。
            // 不走 UsePlatformDetect，直接由 StartLinuxDrm 配置 framebuffer/DRM 后端。
            // 显示卡：默认 /dev/dri/card0；有的板子显示控制器是 card1（card0 是 GPU 无 KMS，
            // 会报 drmModeGetResources failed）。用 PR_DRM_CARD 指定，如 /dev/dri/card1。
            var card = Environment.GetEnvironmentVariable("PR_DRM_CARD");
            card = string.IsNullOrWhiteSpace(card) ? null : card.Trim();
            BuildAvaloniaApp().StartLinuxDrm(args: args, card: card, scaling: scale);
        }
        else
            BuildAvaloniaApp()
                .UsePlatformDetect()
                // Linux/X11 下让弹层（通知下拉、ComboBox、ToolTip 等）在窗口内绘制，
                // 避免独立子窗口在嵌入式设备上"先黑框再出内容"的闪烁。
                // RenderingMode 优先 EGL（RK3568 Mali GPU 走 EGL，硬件加速），不可用时依次回退 GLX、软件。
                .With(new X11PlatformOptions
                {
                    OverlayPopups = true,
                    RenderingMode = new[] { X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software },
                })
                .StartWithClassicDesktopLifetime(args);
    }

    // 公共配置。X11 预览器/设计器会调用它——平台在各自启动分支里补齐。
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .WithInterFont()
            .LogToTrace();
}
