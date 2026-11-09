#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 截图计划（PORT-PLAN §5.9）：截图名 → 演示路由 + 参数 + 尺寸 + 参照图。每个尺寸一个进程（PR_CONTROL_SHOT_SIZE），
/// 该尺寸下跑 Sizes 含这个尺寸的条目；PR_CONTROL_SHOT_NAMES（或 PR_CONTROL_SHOTS）给了名字时只跑这些、并且不看尺寸。
/// 输出 &lt;name&gt;-&lt;w&gt;.png、&lt;name&gt;-&lt;w&gt;.audit.txt、cmp-&lt;name&gt;-&lt;w&gt;.png（参照 = 参照目录下的 final-&lt;name&gt;-&lt;w&gt;.png）。
/// 路由 = 样稿的 hash 路由（overview、device/1、reactor/1/1、placeholder/curves、gallery），参数同样稿 README「常用查询参数」（explorer、tl、sel）。
/// M2：外壳（菜单、页签、图标栏、资源管理器 / 检查器 / 时间轴的框架、状态栏、全局停止格）+ 页面骨架；M4 / M5 起工作区与参照图逐张对上。
/// </summary>
public static class ShotPlan
{
    /// <summary>
    /// 一张截图。Route 与样稿的 hash 路由同名；Query 是路由参数（a=1&amp;b=2）。
    /// From = 先按这个窗口尺寸排一遍、再改成截图尺寸（截窗口尺寸变化之后的样子：检查器 300 ↔ 330、资源管理器停靠 ↔ 收起）。
    /// Setup = 截图前对演示数据做的事（"nodevices"：移除全部设备 —— 空的资源管理器 / 检查器 / 时间轴）。
    /// </summary>
    public sealed record Spec(string Name, string Route, string Query, IReadOnlyList<PixelSize> Sizes, string? Ref = null, PixelSize? From = null, string? Setup = null)
    {
        /// <summary>参照图文件名（默认 final-&lt;name&gt;-&lt;w&gt;.png，与样稿 shots/ 同名）。</summary>
        public string RefFile(int width) => Ref ?? $"final-{Name}-{width}.png";

        public IReadOnlyDictionary<string, string> Params => ParseQuery(Query);
    }

    /// <summary>外壳的三个标准尺寸（PORT-PLAN §5.9）。</summary>
    private static readonly PixelSize[] Screens = { new(1920, 1080), new(1600, 900), new(1366, 768) };

    public static readonly IReadOnlyList<Spec> All = new[]
    {
        // 陈列页：新外壳里的「组件」页签（与样稿整页截图同样含外框）；样稿只有 1920 的参照图，
        // 1600 / 1366（工作区 936 / 1002）只做审计，窗口高度让工作区（高 − 菜单 34 − 页签 44 − 时间轴 220 − 状态栏 30）放得下整页、不出滚动条
        new Spec("gallery", "gallery", "", new[] { new PixelSize(1920, 5100), new PixelSize(1600, 4200), new PixelSize(1366, 4200) }),
        // M2 外壳（工作区是骨架；M4 总览 / 设备页、M5 反应釜页补上内容）
        new Spec("overview", "overview", "", Screens),
        new Spec("device1", "device/1", "", Screens),
        new Spec("reactor11", "reactor/1/1", "", Screens),
        // 演示路由的状态（样稿 README 的地址参数，M2 起由 Core 的 DemoFleet 给出）：4 台（4号机断线重连中）、选中 3号机 · RV11（16 路设备展开）。
        // 外框（资源管理器、页签、状态栏）与参照图对上；工作区是骨架（M4 / M5）
        new Spec("overview4dev", "overview", "devs=4&scroll=end", new[] { new PixelSize(1920, 1080) }),
        new Spec("reactor311", "reactor/3/11", "sel=3/11", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        // 反应釜页 · 独立搅拌的型号（2号机 HT-PR4）：检查器「搅拌（本釜）」转速框；工作区是骨架（M5）
        new Spec("reactor21", "reactor/2/1", "", new[] { new PixelSize(1920, 1080) }),
        // 外框的状态集（PORT-PLAN §5.9「状态集」；参照图用 snap 现拍样稿同一个地址，文件名 final-st-*-<w>.png，见 doc §9）：
        // 急停（检查器红框、资源管理器 / 时间轴组头「急停中」）、申请控制权中、断线（4 种区域的写法）、宽限、资源管理器收起、时间轴收起、未安装的位置、4 台
        new Spec("st-estop3", "overview", "estop=3", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("st-estop1", "overview", "estop=1", new[] { new PixelSize(1920, 1080) }),
        new Spec("st-lease2req", "device/2", "lease=2:requested", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("st-lost1", "overview", "lease=1:lost", new[] { new PixelSize(1920, 1080) }),
        new Spec("st-off1", "overview", "offline=1", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("st-off3", "overview", "offline=3", new[] { new PixelSize(1920, 1080) }),
        new Spec("st-x0", "overview", "explorer=0", new[] { new PixelSize(1920, 1080) }),
        new Spec("st-tl0", "overview", "tl=0", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("st-un16", "overview", "sel=1/6", new[] { new PixelSize(1920, 1080) }),
        new Spec("st-devs4", "overview", "devs=4", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        // 长名字（stress=1：「高压加氢测试一号」等）：页签栏刚好放得下 / 放不下的边界 —— 页签的量宽必须同 WebKit（pr:PrText），
        // 1920 下 7 个页签全显示、不能多收一个进「›」（R1）；三个尺寸都对照
        new Spec("st-stress1", "overview", "stress=1", Screens),
        // 窄屏 + explorer=1：资源管理器是打开的浮层（导航之后再套用外框参数，不会被导航收起）
        new Spec("st-x1", "overview", "explorer=1", new[] { new PixelSize(1366, 768) }),
        // 第二阶段视图的占位页（样稿 pages/placeholder.js）：样稿没有 final 参照图，只审计（参照可用 snap 现拍 /placeholder/curves）。
        // 先按 1600 × 900 排一遍再改成截图尺寸：居中的说明文字在窗口变宽 / 变窄之后仍在自己的框里（审计 text drawn outside its box）
        new Spec("curves", "placeholder/curves", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }, From: new PixelSize(1600, 900)),
        // 空状态（移除全部设备）：资源管理器「还没有设备」、检查器「没有选中对象」、时间轴运行 0/0；同样先按 1600 × 900 排再改尺寸
        // （检查器 300 → 330 / 资源管理器停靠 → 收起之后，居中的空状态文字不能偏出自己的框）。样稿没有这个地址参数，只审计
        new Spec("st-empty", "overview", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }, From: new PixelSize(1600, 900), Setup: "nodevices"),
        // 窗口变大 / 变小之后的总览：应当和直接按这个尺寸打开的一模一样（收进「›」的页签、时间轴组头让出去的型号再显示出来时名字不能是空的 ——
        // Controls/TextRelayout；居中文字不能按旧宽度偏出框）。参照同 overview
        new Spec("overview-grow", "overview", "", new[] { new PixelSize(1920, 1080) }, Ref: "final-overview-1920.png", From: new PixelSize(1366, 768)),
        new Spec("overview-shrink", "overview", "", new[] { new PixelSize(1366, 768) }, Ref: "final-overview-1366.png", From: new PixelSize(1920, 1080)),
        // 旧样式的页面（D5：程序编辑 / 下发 / 导出、性能测试，M7 重做）：没有参照图；外框照常审计，旧样式的子树（LegacyHost，类 audit-skip）不审计
        new Spec("program", "placeholder/program", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
        new Spec("perf", "placeholder/perf", "", new[] { new PixelSize(1920, 1080), new PixelSize(1366, 768) }),
    };

    public static Spec? Find(string name) => All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>这个尺寸下要跑的截图。names 非空时只跑这些名字（任意尺寸）；未知名字抛异常。</summary>
    public static IReadOnlyList<Spec> Select(PixelSize size, IReadOnlyCollection<string>? names)
    {
        if (names is { Count: > 0 })
            return names.Select(n => Find(n) ?? throw new ArgumentException($"截图计划里没有「{n}」（已有：{string.Join(", ", All.Select(s => s.Name))}）")).ToList();
        return All.Where(s => s.Sizes.Contains(size)).ToList();
    }

    /// <summary>PR_CONTROL_SHOT_NAMES / PR_CONTROL_SHOTS：逗号分隔的截图名。</summary>
    public static IReadOnlyCollection<string> NamesFromEnv()
    {
        var s = Environment.GetEnvironmentVariable("PR_CONTROL_SHOT_NAMES");
        if (string.IsNullOrWhiteSpace(s)) s = Environment.GetEnvironmentVariable("PR_CONTROL_SHOTS");
        return string.IsNullOrWhiteSpace(s) ? Array.Empty<string>() : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static IReadOnlyDictionary<string, string> ParseQuery(string? q)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(q)) return d;
        foreach (var part in q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            if (i < 0) d[Uri.UnescapeDataString(part)] = "1";
            else d[Uri.UnescapeDataString(part[..i])] = Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return d;
    }
}
#endif
