using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ParallelReactor.Control.Tests.Lint;

/// <summary>
/// 图标集：Styles/Icons.axaml 与 Controls/IconSet.g.cs 同源，2026-11-21 起是 Phosphor Icons（MIT，256×256 网格；之前由样稿 js/ui.js 生成的 76 个已换掉）。
/// 期望名单 = 现在的 87 个统一图标名（同序）；其中 8 个大图标带 Duotone 浅底纹层（资源键 Icon.名字.Tone）。
/// 另查：每个图标有非空几何、旧界面的 Ico* 键不再有。
/// </summary>
public class IconSetTests
{
    /// <summary>IconSet.Names 的顺序。</summary>
    public static readonly string[] Expected =
    {
        "bell-lg", "pc-lg", "beaker-lg", "program-lg", "machine-lg", "key-lg", "check-lg", "logo", "play", "stop",
        "pause", "next", "rec", "estop", "ok-f", "warn-f", "err-f", "no-f", "info-f", "bell",
        "pc", "gear", "wrench", "help", "info", "alert", "warn", "check", "x", "plus",
        "add-c", "minus", "lock", "unlock", "key", "plug", "unplug", "link", "wifioff", "refresh",
        "reset", "down", "up", "right", "left", "arrow-up", "arrow-dn", "download", "upload", "save",
        "copy", "edit", "trash", "folder", "doc", "report", "task", "trend", "flag", "grid",
        "gas-flow", "locate", "search", "zoomin", "zoomout", "fit", "max", "restore", "move", "eye",
        "eyeoff", "clock", "timer", "beaker", "send", "tree", "list", "user", "flame", "snow",
        "droplet", "hand", "gauge", "power", "open", "devices", "valve",
    };

    /// <summary>带 Duotone 浅底纹层的图标（IconSet.WithTone）。</summary>
    public static readonly string[] Toned = { "bell-lg", "pc-lg", "beaker-lg", "program-lg", "machine-lg", "key-lg", "check-lg", "logo" };

    private static string IconsAxaml => AppSource.Read("Styles/Icons.axaml");

    private static readonly Regex Geo = new(@"<StreamGeometry\s+x:Key=""([\w.\-]+)"">([^<]*)</StreamGeometry>", RegexOptions.Compiled);

    [Fact]
    public void Expected_Has87UniqueNames()
    {
        Assert.Equal(87, Expected.Length);
        Assert.Equal(87, Expected.Distinct().Count());
        Assert.All(Toned, t => Assert.Contains(t, Expected));
    }

    [Fact]
    public void IconsAxaml_HasEveryIcon_WithGeometry()
    {
        var geos = Geo.Matches(IconsAxaml).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
        var icons = geos.Keys.Where(k => k.StartsWith("Icon.", StringComparison.Ordinal) && !k.EndsWith(".Tone", StringComparison.Ordinal)).Select(k => k[5..]).ToList();
        Assert.Equal(Expected.OrderBy(n => n, StringComparer.Ordinal), icons.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var n in Expected)
            Assert.Matches(@"^(F1 )?M[\d.\-, ]", geos["Icon." + n]);   // 以 M 开头（可带填充规则 F1）
        var toned = geos.Keys.Where(k => k.EndsWith(".Tone", StringComparison.Ordinal)).Select(k => k[5..^5]).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(Toned.OrderBy(n => n, StringComparer.Ordinal), toned);
    }

    [Fact]
    public void IconSetGenerated_MatchesExpectedOrder_AndTones()
    {
        var src = AppSource.Read("Controls/IconSet.g.cs");
        string[] List(string field) => Regex.Matches(Regex.Match(src, field + @" = new\[\]\s*\{(.*?)\};", RegexOptions.Singleline).Groups[1].Value, @"""([\w\-]+)""").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(Expected, List("Names"));
        Assert.Equal(Toned, List("WithTone"));
    }

    /// <summary>旧界面的 Ico* 键已经全部删掉：图标文件里没有，界面也不再引用。</summary>
    [Fact]
    public void LegacyIcoKeys_AreGone()
    {
        Assert.DoesNotContain(Geo.Matches(IconsAxaml).Select(m => m.Groups[1].Value), k => k.StartsWith("Ico", StringComparison.Ordinal) && !k.StartsWith("Icon.", StringComparison.Ordinal));
        foreach (var (rel, full) in AppSource.Sources().Where(f => f.Rel.EndsWith(".axaml", StringComparison.Ordinal)))
            Assert.False(Regex.IsMatch(File.ReadAllText(full), @"\{(?:StaticResource|DynamicResource) Ico[A-Z]\w*\}"), rel + " 还在引用旧的 Ico* 图标键");
    }

    /// <summary>界面里写的图标名（Icon="…" / b:Pr.Icon="…"）都在图标表里。</summary>
    [Fact]
    public void IconNamesUsedInViews_Exist()
    {
        var known = new HashSet<string>(Expected, StringComparer.Ordinal);
        foreach (var (rel, full) in AppSource.Sources().Where(f => f.Rel.EndsWith(".axaml", StringComparison.Ordinal)))
            foreach (Match m in Regex.Matches(File.ReadAllText(full), @"(?:\bIcon|Pr\.Icon)=""([a-z][\w\-]*)"""))
                Assert.True(known.Contains(m.Groups[1].Value), rel + " 用了图标表里没有的图标「" + m.Groups[1].Value + "」");
    }
}
