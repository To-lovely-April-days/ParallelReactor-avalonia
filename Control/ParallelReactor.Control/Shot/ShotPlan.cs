#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 截图计划（融合版）：截图名 → 页面 + 参数 + 尺寸 + 参照图。每个尺寸一个进程（PR_CONTROL_SHOT_SIZE），该尺寸下跑 Sizes 含这个尺寸的条目；
/// PR_CONTROL_SHOT_NAMES（或 PR_CONTROL_SHOTS）给了名字时只跑这些、并且不看尺寸。
/// 输出 &lt;name&gt;-&lt;w&gt;.png、&lt;name&gt;-&lt;w&gt;.audit.txt、cmp-&lt;name&gt;-&lt;w&gt;.png（参照 = 参照目录下的 final-&lt;name&gt;-&lt;w&gt;.png，tools/snap-fusion-refs.sh 按样稿的地址参数现拍）。
/// 页面 = 样稿的 page（main / prog / curve / alarm / test / rec / set / gallery），参数同样稿：dev=k（不给 = 总览）、ch=n、po / pc（展开 / 收起面板：dev、insp）、
/// alm=1（报警列表）、dialog=name，以及演示数据参数（devs、lease、offline、alarm、estop …）。
/// </summary>
public static partial class ShotPlan
{
    /// <summary>
    /// 一张截图。Page 是样稿的 page 写法；Query 是其余参数（a=1&amp;b=2）。
    /// From = 先按这个窗口尺寸排一遍、再改成截图尺寸（截窗口尺寸变化之后的样子）。
    /// </summary>
    public sealed record Spec(string Name, string Route, string Query, IReadOnlyList<PixelSize> Sizes, string? Ref = null, PixelSize? From = null, string? Setup = null)
    {
        /// <summary>参照图文件名（默认 final-&lt;name&gt;-&lt;w&gt;.png）。</summary>
        public string RefFile(int width) => Ref?.Replace("{w}", width.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? $"final-{Name}-{width}.png";

        public IReadOnlyDictionary<string, string> Params => ParseQuery(Query);

        /// <summary>交给 ShellVm.ApplyDemoParams 的整串参数：page=… + Query。</summary>
        public string FullQuery => "page=" + Route + (string.IsNullOrEmpty(Query) ? "" : "&" + Query);
    }

    private static readonly PixelSize W1920 = new(1920, 1080), W1366 = new(1366, 768);
    private static readonly PixelSize[] Both = { W1920, W1366 };

    /// <summary>
    /// 融合版的截图。F2：外框的各个状态 —— 主页（1号机 / 总览）、六个页面的框架（占位）、设备面板收起成竖条 / 窄屏浮出、检查器展开（宽屏停靠 / 窄屏浮出）、
    /// 报警列表、运行胶囊的两个对话框（全部停止 / 急停，对话框本身 F3 重做）。名字与参照图同名的会生成对照图。
    /// </summary>
    private static readonly Spec[] Fusion = new[]
    {
        // 组件陈列页：窗口高度让整页放得下、不出滚动条；没有参照图，只审计
        new Spec("gallery", "gallery", "apop=0", new[] { new PixelSize(1920, 6400), new PixelSize(1600, 7600), new PixelSize(1366, 7600) }),
        // apop=0：报警弹窗不弹（同样稿参照图的地址参数；只有 home-apop 弹）
        new Spec("home", "main", "dev=1&apop=0", Both),
        new Spec("home-overview", "main", "apop=0", Both),
        new Spec("page-prog", "prog", "dev=1&apop=0", Both),
        new Spec("page-curve", "curve", "dev=1&apop=0", Both),
        new Spec("page-alarm", "alarm", "dev=1&apop=0", Both),
        new Spec("page-test", "test", "dev=1&apop=0", Both),
        new Spec("page-rec", "rec", "dev=1&apop=0", Both),
        new Spec("page-set", "set", "dev=1&apop=0", Both),
        new Spec("home-alarms", "main", "dev=1&alm=1&apop=0", Both),
        // 外框状态（样稿没有同名参照图）：宽屏把设备面板收起成竖条；窄屏把设备面板浮出；检查器展开（宽屏停靠 340 / 窄屏浮出 400）
        new Spec("frame-rail", "main", "dev=1&pc=dev&apop=0", new[] { W1920 }),
        new Spec("frame-float", "main", "dev=1&po=dev&apop=0", new[] { W1366 }),
        new Spec("frame-insp", "main", "dev=1&po=insp&apop=0", Both),
        // F3 主页：报警弹窗 · 叠加曲线温度 / 压力 · 选中报警通道 · 4 路 / 16 路设备 · 断线 / 恢复横条 · 三个停止对话框 · 检查器（参照图同名）
        new Spec("home-apop", "main", "dev=1", Both),
        new Spec("home-ovt", "main", "dev=1&apop=0&ovm=T", Both),
        new Spec("home-ovp", "main", "dev=1&apop=0&ovm=P", Both),
        new Spec("home-ch4", "main", "dev=1&apop=0&ch=4", Both),
        new Spec("home-dev2", "main", "dev=2&apop=0", Both),
        new Spec("home-dev3", "main", "dev=3&apop=0", Both),
        new Spec("home-netlost", "main", "dev=1&apop=0&net=lost", Both),
        new Spec("home-netback", "main", "dev=1&apop=0&net=back", Both),
        new Spec("home-stopall", "main", "dev=1&apop=0&dialog=stopall", Both),
        new Spec("home-estop", "main", "dev=1&apop=0&dialog=estop", Both),
        new Spec("home-stopch", "main", "dev=1&apop=0&dialog=stopch:1:1", Both),
        // 检查器展开（宽屏停靠 / 窄屏浮出）：样稿 frame-insp 的参照（RV1）；选中报警通道 RV4 的检查器（没有参照图）
        new Spec("home-insp4", "main", "dev=1&apop=0&ch=4&po=insp", Both, Ref: "final-frame-insp-{w}.png"),
    };

    /// <summary>默认计划（shots.sh 不给名字时按尺寸跑这些；PR_CONTROL_SHOT_PLAN=1 打印的也是这些）。</summary>
    public static readonly IReadOnlyList<Spec> All = Fusion;

    /// <summary>按名字找。</summary>
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
