using System.Collections.Generic;

namespace ParallelReactor.Control.Shell;

/// <summary>外框里几处共用的文字（样稿 ui.alarmText 等），逐字同样稿。</summary>
public static class ShellText
{
    /// <summary>「报警 1 · 提醒 1」/「提醒 1」/ none（样稿 ui.alarmText：只有错误报警叫「报警」，提醒级别叫「提醒」；急停那条不数）。</summary>
    public static string AlarmText(IFleetDevice d, string none = "") => AlarmText(d.ErrAlarms, d.WarnAlarms, none);

    public static string AlarmText(int err, int warn, string none = "")
    {
        var parts = new List<string>(2);
        if (err > 0) parts.Add("报警 " + err);
        if (warn > 0) parts.Add("提醒 " + warn);
        return parts.Count == 0 ? none : string.Join(" · ", parts);
    }

    /// <summary>「N 台」（资源管理器标题栏）。</summary>
    public static string Units(int n) => n + " 台";
}
