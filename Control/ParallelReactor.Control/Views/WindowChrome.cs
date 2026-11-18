using System;

namespace ParallelReactor.Control.Views;

/// <summary>窗口外框的画法（用户 2026-11-18：「标题栏还是改一下 用我们自定义的」）。</summary>
public enum ChromeKind
{
    /// <summary>系统标题栏 + 窗口内深蓝条（F2 的做法；Linux：X11 上扩展客户区支持不完整，保留系统标题栏）。</summary>
    Native,
    /// <summary>自绘：去掉系统标题栏，深蓝条就是标题栏，右端画最小化 / 最大化 / 关闭（样稿 .tb-win，Windows）。</summary>
    Custom,
    /// <summary>macOS：深蓝条伸到窗口顶，左上角留给系统的红黄绿三个按钮（不自绘窗口按钮）。</summary>
    MacInset,
}

/// <summary>
/// 当前用哪种外框：PR_CONTROL_CHROME=custom|mac|native 可覆盖；默认 Windows 自绘、macOS 留红黄绿、其它系统用系统标题栏。
/// 无头截图（Debug）按样稿用自绘（Program 里设）。
/// </summary>
public static class WindowChrome
{
    public static ChromeKind Kind { get; set; } = Detect();

    /// <summary>macOS 红黄绿三个按钮占的宽度（深蓝条左边让出来，DIP）。</summary>
    public const double MacInsetWidth = 72;

    public static ChromeKind? FromEnv() => (Environment.GetEnvironmentVariable("PR_CONTROL_CHROME") ?? "").Trim().ToLowerInvariant() switch
    {
        "custom" => ChromeKind.Custom,
        "mac" => ChromeKind.MacInset,
        "native" => ChromeKind.Native,
        _ => null,
    };

    private static ChromeKind Detect() =>
        FromEnv() ?? (OperatingSystem.IsWindows() ? ChromeKind.Custom : OperatingSystem.IsMacOS() ? ChromeKind.MacInset : ChromeKind.Native);
}
