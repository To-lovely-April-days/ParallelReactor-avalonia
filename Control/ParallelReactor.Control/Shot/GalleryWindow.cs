#if DEBUG
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views.Pages;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 组件陈列窗口（仅 Debug，M1）：直接承载 Views/Pages/GalleryPage，不经过旧的 MainView；只在演示进程（PR_CONTROL_DEMO=1）里创建。
/// Fluent 资源覆盖（强调色 / 内容字体 / 字号）与 .frozen 样式在 M1 只在这个进程里生效（旧界面在别的进程，不受影响），M2 起在正式外壳里全局生效。
/// </summary>
public sealed class GalleryWindow : Window
{
    private readonly ScrollViewer _scroll;
    private static bool _fluentApplied;

    public GalleryPage Page { get; }

    public GalleryWindow()
    {
        Title = "组件陈列 · 演示数据";
        // Fluent 覆盖挂在 Application 上才能传到 Fluent 自己的派生画刷（AccentButtonBackground 等由主题在应用级解析；
        // 只挂在窗口上时 SystemAccentColor 变了、派生画刷仍是系统蓝 0078D7）。演示进程里只有这个窗口（旧界面不在同一进程），所以仍只影响陈列页。
        if (!_fluentApplied) { Application.Current!.Resources.MergedDictionaries.Add(FluentOverrides.Create()); _fluentApplied = true; }
        Styles.Add(new StyleInclude(new Uri("avares://ParallelReactor.Control/")) { Source = new Uri("avares://ParallelReactor.Control/Styles/Frozen.axaml") });
        if (Application.Current!.TryFindResource("UiFont", out var f) && f is FontFamily ff) FontFamily = ff;
        FontSize = 15;
        Foreground = Palette.T2Brush;
        Background = Palette.BgAppBrush;
        UseLayoutRounding = true;
        Page = new GalleryPage();
        _scroll = new ScrollViewer { Content = Page };
        Content = _scroll;
    }

    /// <summary>
    /// frame=workspace：页面放在新外壳工作区的位置（菜单 34 + 页签 44 之下；图标栏 64 + 资源管理器 300（宽 ≥ 1600 停靠）之右；
    /// 检查器 330（宽 &lt; 1920 时 300）之左），与样稿 final-gallery-1920.png 的工作区对齐，便于左右对照；外框本身在 M2 做，这里留空。
    /// frame=full（默认）：铺满窗口。
    /// </summary>
    public void SetFrame(string? frame, double width)
    {
        double R(string key) => Application.Current!.TryFindResource(key, out var v) && v is double d ? d : 0;
        _scroll.Margin = frame == "workspace"
            ? new Thickness(R("SideW") + (width >= 1600 ? R("ExplorerW") : 0), R("MenuH") + R("TabsH"), width >= 1920 ? R("InspW") : R("InspWNarrow"), 0)
            : default;
    }
}
#endif
