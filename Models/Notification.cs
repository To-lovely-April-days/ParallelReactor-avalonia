using Avalonia.Media;

namespace ParallelReactor.Models;

/// <summary>
/// 顶栏通知铃铛下拉列表中的单条通知。
/// 图标、配色随类型（报警 / 建议）计算好，View 直接绑定即可。
/// </summary>
public class Notification
{
    public string Kind { get; init; } = "advice"; // "alarm" | "advice"
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";

    /// <summary>右侧角标文字：报警 / 建议</summary>
    public string Tag { get; init; } = "";

    /// <summary>图标矢量路径（24×24 viewBox）</summary>
    public Geometry Icon { get; init; } = Geometry.Parse("M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z");

    /// <summary>图标圆底背景色</summary>
    public IBrush IconBackground { get; init; } = Brushes.Transparent;

    /// <summary>图标描边色</summary>
    public IBrush IconForeground { get; init; } = Brushes.White;

    // ===== 报警图标：circle-alert（Lucide）=====
    private static readonly Geometry AlarmIcon =
        Geometry.Parse("M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20z M12 8v4 M12 16h.01");

    // ===== 建议图标：lightbulb 灯泡（Lucide）=====
    private static readonly Geometry AdviceIcon =
        Geometry.Parse("M15.09 14c.18-.98.65-1.74 1.41-2.5A4.65 4.65 0 0 0 18 8 6 6 0 0 0 6 8" +
                       "c0 1 .23 2.23 1.5 3.5A4.61 4.61 0 0 1 8.91 14 M9 18h6 M10 22h4");

    public static Notification Alarm(string title, string message) => new()
    {
        Kind = "alarm",
        Title = title,
        Message = message,
        Tag = "报警",
        Icon = AlarmIcon,
        IconBackground = new SolidColorBrush(Color.Parse("#33e0394c")),
        IconForeground = new SolidColorBrush(Color.Parse("#e0394c"))
    };

    public static Notification Advice(string title, string message) => new()
    {
        Kind = "advice",
        Title = title,
        Message = message,
        Tag = "建议",
        Icon = AdviceIcon,
        IconBackground = new SolidColorBrush(Color.Parse("#33f0a830")),
        IconForeground = new SolidColorBrush(Color.Parse("#f0a830"))
    };
}
