using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 页面（融合版 F2；样稿 main-fusion.html 的 PAGE：main / prog / curve / alarm / test / rec / set）。菜单栏只列前六个，
/// 「设置」从深蓝条右端进；「组件陈列」是设计 / 自检用的页面（不在菜单里）。
/// </summary>
public enum AppPage { Home, Program, Curves, Alarms, Perf, Records, Settings, Gallery }

/// <summary>
/// 页面看哪些设备（样稿 PAGES[p].scope）：
/// <list type="bullet">
/// <item>Main —— 主页：设备下拉第一项「总览 · 全部设备」（不选设备 = 总览卡片），设备面板第一行「总览」。</item>
/// <item>All —— 报警 / 记录：第一项「全部设备」，设备面板第一行「全部设备」。</item>
/// <item>One —— 程序 / 曲线 / 性能测试 / 设置：一次看一台；进来时没选设备就选第一台。</item>
/// </list>
/// </summary>
public enum PageScope { Main, All, One }

/// <summary>一个页面的名字、地址里的写法与范围。</summary>
/// <param name="Key">样稿的写法（page=main / prog / curve / alarm / test / rec / set），截图计划与演示路由用它。</param>
public sealed record AppPageInfo(AppPage Page, string Key, string Title, PageScope Scope, bool InMenu);

/// <summary>页面表（顺序 = 菜单栏的顺序）。</summary>
public static class AppPages
{
    public static readonly IReadOnlyList<AppPageInfo> All = new[]
    {
        new AppPageInfo(AppPage.Home, "main", "主页", PageScope.Main, true),
        new AppPageInfo(AppPage.Program, "prog", "程序", PageScope.One, true),
        new AppPageInfo(AppPage.Curves, "curve", "曲线", PageScope.One, true),
        new AppPageInfo(AppPage.Alarms, "alarm", "报警", PageScope.All, true),
        new AppPageInfo(AppPage.Perf, "test", "性能测试", PageScope.One, true),
        new AppPageInfo(AppPage.Records, "rec", "记录", PageScope.All, true),
        new AppPageInfo(AppPage.Settings, "set", "设置", PageScope.One, false),
        new AppPageInfo(AppPage.Gallery, "gallery", "组件陈列", PageScope.Main, false),
    };

    /// <summary>菜单栏上的六个页面。</summary>
    public static readonly IReadOnlyList<AppPageInfo> Menu = All.Where(p => p.InMenu).ToArray();

    public static AppPageInfo Info(AppPage p) => All.First(i => i.Page == p);

    private static readonly Dictionary<string, AppPage> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = AppPage.Home, ["overview"] = AppPage.Home, ["program"] = AppPage.Program, ["curves"] = AppPage.Curves,
        ["alarms"] = AppPage.Alarms, ["perf"] = AppPage.Perf, ["records"] = AppPage.Records, ["settings"] = AppPage.Settings,
    };

    /// <summary>按样稿的写法（main / prog …）、英文名（home / program …）或中文标题找页面；不认识返回 null。</summary>
    public static AppPage? Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim().TrimStart('#', '/');
        foreach (var i in All) if (string.Equals(i.Key, k, StringComparison.OrdinalIgnoreCase) || i.Title == k) return i.Page;
        return Alias.TryGetValue(k, out var p) ? p : null;
    }
}
