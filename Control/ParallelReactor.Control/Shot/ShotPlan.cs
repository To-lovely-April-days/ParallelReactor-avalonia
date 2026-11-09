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
/// M1 只有 gallery；后续里程碑按矩阵追加（overview、device1、reactor11 …）。
/// </summary>
public static class ShotPlan
{
    /// <summary>一张截图。Route 与样稿的 hash 路由同名；Query 是路由参数（a=1&amp;b=2）。</summary>
    public sealed record Spec(string Name, string Route, string Query, IReadOnlyList<PixelSize> Sizes, string? Ref = null)
    {
        /// <summary>参照图文件名（默认 final-&lt;name&gt;-&lt;w&gt;.png，与样稿 shots/ 同名）。</summary>
        public string RefFile(int width) => Ref ?? $"final-{Name}-{width}.png";

        public IReadOnlyDictionary<string, string> Params => ParseQuery(Query);
    }

    public static readonly IReadOnlyList<Spec> All = new[]
    {
        // 陈列页：页面放在新外壳工作区的位置（frame=workspace），与样稿整页截图（含外框）左右对照；
        // 样稿只有 1920 的参照图，1600 / 1366（工作区 936 / 1002）只做审计，高度够放下整页
        new Spec("gallery", "gallery", "frame=workspace", new[] { new PixelSize(1920, 5100), new PixelSize(1600, 3900), new PixelSize(1366, 3900) }),
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
