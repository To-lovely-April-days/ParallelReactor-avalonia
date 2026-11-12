using System;
using System.Collections.Generic;
using System.Globalization;

namespace ParallelReactor.Control.Shell;

/// <summary>页面种类（样稿 hash 路由的 page）。</summary>
public enum PageKind { Overview, Device, Reactor, Placeholder, Gallery }

/// <summary>
/// 路由（样稿 #/overview、#/device/1、#/reactor/1/3、#/placeholder/curves、#/gallery）：页签的 Id 就是路由的路径。
/// <see cref="K"/> 是设备位置（1号机 = 1），<see cref="N"/> 是 RV 号，<see cref="Key"/> 是占位页的名字。
/// </summary>
public sealed record Route(PageKind Page, int K = 0, int N = 0, string? Key = null)
{
    public static readonly Route Overview = new(PageKind.Overview);
    public static readonly Route Gallery = new(PageKind.Gallery);

    public static Route Device(int k) => new(PageKind.Device, k);
    public static Route Reactor(int k, int n) => new(PageKind.Reactor, k, n);
    public static Route Placeholder(string key) => new(PageKind.Placeholder, Key: key);

    /// <summary>路径（同样稿 PR.route.id()）：overview、device/1、reactor/1/3、placeholder/curves、gallery。</summary>
    public string Id => Page switch
    {
        PageKind.Device => "device/" + K.ToString(CultureInfo.InvariantCulture),
        PageKind.Reactor => "reactor/" + K.ToString(CultureInfo.InvariantCulture) + "/" + N.ToString(CultureInfo.InvariantCulture),
        PageKind.Placeholder => "placeholder/" + Key,
        PageKind.Gallery => "gallery",
        _ => "overview",
    };

    public override string ToString() => Id;

    /// <summary>解析路径（可以带开头的「#/」或「/」）；不认识返回 null。占位页也接受中文名（同样稿 placeholder.js 的 ALIAS）。</summary>
    public static Route? Parse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var parts = path.Trim().TrimStart('#').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : 0;
        switch (parts[0])
        {
            case "overview": return Overview;
            case "gallery": return Gallery;
            case "device" when parts.Length >= 2 && Int(parts[1]) > 0: return Device(Int(parts[1]));
            case "reactor" when parts.Length >= 3 && Int(parts[1]) > 0 && Int(parts[2]) > 0: return Reactor(Int(parts[1]), Int(parts[2]));
            case "placeholder" when parts.Length >= 2:
                var key = Placeholders.KeyOf(Uri.UnescapeDataString(parts[1]));
                return key == null ? null : Placeholder(key);
            default: return null;
        }
    }
}

/// <summary>第二阶段视图的占位（样稿 pages/placeholder.js 的 PH，文字逐字照抄）。</summary>
public static class Placeholders
{
    public sealed record Info(string Key, string Title, string Icon, string Text);

    /// <summary>顺序同样稿（新页签弹出框「视图」一栏）：新建程序、全部曲线、性能测试、记录、报警、设置。</summary>
    public static readonly IReadOnlyList<Info> All = new[]
    {
        new Info("program", "新建程序", "program", "第二阶段设计：用步骤卡片画布编辑程序（升温、充压、反应、等待、吹扫、结束），按所选设备的型号校验量程、搅拌方式与气路名称，再下发到一台或多台设备的多个反应釜，并管理程序库。"),
        new Info("curves", "全部曲线", "curves", "第二阶段设计：跨设备的温度、压力、转速曲线叠加对比，按设备分组（灰色线 + 不同虚线样式区分），可按会话、标记和时间范围筛选，光标读数并导出 CSV。"),
        new Info("perf", "性能测试", "perf", "第二阶段设计：选择设备、反应釜和测试项（功能与安全、搅拌、压力、温度、组合、长时），运行时显示进度并录入外部表计读数，结束后生成报告与原始数据。"),
        new Info("records", "记录", "records", "第二阶段设计：按设备浏览会话、标记和事件日志，查看并导出设备触屏端保存的数据文件，可按设备、反应釜和时间筛选。"),
        new Info("alarms", "报警", "bell", "第二阶段设计：所有已连接设备的当前报警与历史报警（按设备分组），确认报警不需要控制权，报警阈值只读显示、修改需在触屏。"),
        new Info("settings", "设置", "settings", "第二阶段设计：本机客户端名、数据目录、压力单位（MPa / psi）、设备连接（地址、端口、令牌、自动连接），设备参数只读显示、修改需在触屏。"),
    };

    /// <summary>占位页的提示行（样稿 .placeholder-hint）。</summary>
    public const string Hint = "本阶段先确认工作台结构与总览、设备、反应釜三个视图";

    private static readonly Dictionary<string, string> Alias = new()
    {
        ["新建程序"] = "program", ["程序"] = "program", ["全部曲线"] = "curves", ["曲线"] = "curves", ["性能测试"] = "perf", ["测试"] = "perf",
        ["记录"] = "records", ["报警"] = "alarms", ["设置"] = "settings",
    };

    public static Info? Get(string? key) => key == null ? null : Array.Find((Info[])All, i => i.Key == key);

    public static string? KeyOf(string name) => Get(name) != null ? name : Alias.TryGetValue(name, out var k) ? k : null;
}
