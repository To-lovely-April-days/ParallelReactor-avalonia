using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ParallelReactor.Control.Tests.Lint;

/// <summary>
/// 图标集（PORT-PLAN §5 M1「IconSetTests」）：Styles/Icons.axaml 与 Controls/IconSet.g.cs 由 tools/gen-icons.py 从样稿 js/ui.js 的 ICONS 生成。
/// 期望名单抄自 ui.js 的 ICON_NAMES（76 个，同序）；样稿目录在仓库旁边时再直接读 ui.js 交叉核对一次。
/// 另查：每个图标有非空几何、填充子图形（rec 的录制点）存在、旧界面的 23 个 Ico* 键仍在（M7 删除）。
/// </summary>
public class IconSetTests
{
    /// <summary>ui.js ICON_NAMES（= Object.keys(ICONS)）的顺序。</summary>
    public static readonly string[] Expected =
    {
        "logo", "devices", "overview", "program", "curves", "records", "bell", "perf", "settings", "collapse",
        "expand", "down", "up", "right", "left", "x", "max", "restore", "move", "plus",
        "minus", "play", "stop", "pause", "skip", "power", "flame", "snow", "fan", "gauge",
        "thermo", "lock", "unlock", "key", "link", "plug", "unplug", "refresh", "warn", "alert",
        "estop", "flag", "rec", "edit", "download", "upload", "send", "open", "eye", "eyeoff",
        "search", "zoomin", "zoomout", "more", "tree", "monitor", "info", "trash", "check", "clock",
        "wifioff", "cpu", "droplet", "valve", "wind", "flask", "pin", "pulse", "user", "list",
        "layout", "tag", "hand", "target", "fit", "machine",
    };

    private static string IconsAxaml => AppSource.Read("Styles/Icons.axaml");

    private static readonly Regex Geo = new(@"<StreamGeometry\s+x:Key=""([\w.]+)"">([^<]*)</StreamGeometry>", RegexOptions.Compiled);

    [Fact]
    public void Expected_Has76UniqueNames()
    {
        Assert.Equal(76, Expected.Length);
        Assert.Equal(76, Expected.Distinct().Count());
    }

    [Fact]
    public void IconsAxaml_HasEveryIcon_WithGeometry()
    {
        var geos = Geo.Matches(IconsAxaml).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
        var icons = geos.Keys.Where(k => k.StartsWith("Icon.", StringComparison.Ordinal) && !k.EndsWith(".Fill", StringComparison.Ordinal)).Select(k => k[5..]).ToList();
        Assert.Equal(Expected.OrderBy(n => n, StringComparer.Ordinal), icons.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var n in Expected)
            Assert.Matches(@"^M[\d.\- ]", geos["Icon." + n]);   // 绝对坐标、以 M 开头（gen-icons.py 全部换算成绝对命令）
        Assert.True(geos.ContainsKey("Icon.rec.Fill"), "缺少录制点的填充子图形 Icon.rec.Fill");
    }

    [Fact]
    public void IconSetGenerated_MatchesUiJsOrder_AndFills()
    {
        var src = AppSource.Read("Controls/IconSet.g.cs");
        var block = Regex.Match(src, @"Names = new\[\]\s*\{(.*?)\};", RegexOptions.Singleline).Groups[1].Value;
        var names = Regex.Matches(block, @"""(\w+)""").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(Expected, names);
        Assert.Contains("[\"logo\"] = (0, 0, 24, 28)", src);          // logo 的 viewBox 24×28
        Assert.Matches(@"WithFill = new\[\] \{ ""rec"" \}", src);
    }

    [Fact]
    public void LegacyIcoKeys_StillPresent()
    {
        var legacy = Geo.Matches(IconsAxaml).Select(m => m.Groups[1].Value).Where(k => k.StartsWith("Ico", StringComparison.Ordinal) && !k.StartsWith("Icon.", StringComparison.Ordinal)).ToList();
        Assert.Equal(23, legacy.Count);
        // 旧视图引用的键都还在（M7 随旧视图一起删）
        foreach (var (rel, full) in AppSource.Sources().Where(f => f.Rel.EndsWith(".axaml", StringComparison.Ordinal)))
            foreach (Match m in Regex.Matches(File.ReadAllText(full), @"\{(?:StaticResource|DynamicResource) (Ico[A-Z]\w*)\}"))
                Assert.True(legacy.Contains(m.Groups[1].Value), $"{rel} 引用的 {m.Groups[1].Value} 不在 Icons.axaml 里");
    }

    [Fact]
    public void MockUiJs_IconNames_MatchExpected_WhenMockIsPresent()
    {
        var mock = AppSource.MockDir;
        if (mock == null) return;   // 样稿不在这台机器上：只用上面的内嵌名单
        var js = File.ReadAllText(Path.Combine(mock, "js", "ui.js"));
        var start = js.IndexOf("const ICONS = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "ui.js 里找不到 const ICONS");
        var end = js.IndexOf("\n  };", start, StringComparison.Ordinal);
        var body = js[start..end];
        // 一行可以有好几个键（down / up / right / left 写在同一行）：行首或逗号后的「名字: '」或「名字: [」
        var keys = Regex.Matches(body, @"(?:^|,)\s*(\w+):\s*['\[]", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(Expected, keys);
    }
}
