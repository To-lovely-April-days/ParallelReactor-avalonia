using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ParallelReactor.Control.Tests.Lint;

/// <summary>
/// 令牌一致性（融合版 F1）：来源 = Control/tools/fusion-tokens.json（从样稿 research/main-fusion.html 抽出的最终值，tools/gen-tokens.py 据此生成）。核对：
///  · JSON 本身：令牌名 kebab、值小写 #rrggbb、不重名；别名只指向色板里的令牌；
///  · 样稿在的时候：每个颜色确实出现在样稿里；出处写了「:root --x」的等于 --x 在 10 层 :root 层叠之后的最终值（令牌不能凭空造，也不能取到被覆盖的旧层）；
///  · Tokens.axaml 的每个 Color 都在 JSON 里、键名按规则（kebab → PascalCase + Color / Brush）、值相同；每个令牌都有 Color 和 Brush；
///  · 每个 Brush 引用自己的 Color（焦点画刷 → focus 令牌；语义画刷 → JSON 里的目标）；
///  · Tokens.axaml 里其它颜色（阴影、杂项、透明度变体）的 RGB 都在 JSON 里；阴影与 JSON 的 CSS 写法换算后相同；
///  · Theme/Palette.cs 的常量、画刷、别名、阴影、令牌表与 Tokens.axaml / JSON 完全一致；
///  · 没有紫色（用户决定：ALZK 品牌蓝 + 灰白）：色板与全部源码里的颜色字面量都不许落在紫色色相（只有 8 路曲线的识别色按样稿保留）。
/// </summary>
public class TokenParityTests
{
    public sealed record Colour(string Name, string Hex, string Group, string Src);
    public sealed record Alias(string Name, string To, string Group);
    public sealed record Shadow(string Name, string Key, string Css);

    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(File.ReadAllText(JsonPath)));

    /// <summary>tools/fusion-tokens.json。</summary>
    public static string JsonPath => Path.Combine(AppSource.Repo, "Control", "tools", "fusion-tokens.json");

    public static IReadOnlyList<Colour> Colours => Doc.Value.RootElement.GetProperty("colors").EnumerateArray()
        .Select(e => new Colour(e.GetProperty("name").GetString()!, e.GetProperty("hex").GetString()!, e.GetProperty("group").GetString()!,
            e.TryGetProperty("src", out var s) ? s.GetString()! : "")).ToList();

    public static IReadOnlyList<Alias> Aliases => Doc.Value.RootElement.GetProperty("aliases").EnumerateArray()
        .Select(e => new Alias(e.GetProperty("name").GetString()!, e.GetProperty("to").GetString()!, e.GetProperty("group").GetString()!)).ToList();

    public static IReadOnlyList<Shadow> Shadows => Doc.Value.RootElement.GetProperty("shadows").EnumerateArray()
        .Select(e => new Shadow(e.GetProperty("name").GetString()!, e.GetProperty("key").GetString()!, e.GetProperty("css").GetString()!)).ToList();

    /// <summary>杂项：（名字, rgb, alpha）。</summary>
    public static IReadOnlyList<(string Name, (int R, int G, int B) Rgb, double Alpha)> Misc => Doc.Value.RootElement.GetProperty("misc").EnumerateArray()
        .Select(e => { var (rgb, a) = ParseRgba(e.GetProperty("rgba").GetString()!); return (e.GetProperty("name").GetString()!, rgb, a); }).ToList();

    public static string FocusToken => Doc.Value.RootElement.GetProperty("focus").GetProperty("brush").GetString()!;
    public static double FocusRingAlpha => Doc.Value.RootElement.GetProperty("focus").GetProperty("ringAlpha").GetDouble();

    /// <summary>命名规则：去掉「--」，kebab 的每段首字母大写后拼起来（bg-app → BgApp、a-d → AD、t-1 → T1、ch-t-sp → ChTSp）。</summary>
    public static string KeyOf(string css) => string.Concat(css.TrimStart('-').Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static readonly Regex ColorRes = new(@"<Color\s+x:Key=""(\w+)"">\s*(#[0-9A-Fa-f]{6,8})\s*</Color>", RegexOptions.Compiled);
    private static readonly Regex BrushRes = new(@"<SolidColorBrush\s+x:Key=""(\w+)""\s+Color=""\{StaticResource\s+(\w+)\}""(?:\s+Opacity=""[\d.]+"")?\s*/>", RegexOptions.Compiled);
    private static readonly Regex AnyHex = new(@"#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\b", RegexOptions.Compiled);
    private static readonly Regex ShadowRes = new(@"<BoxShadows\s+x:Key=""(\w+)"">([^<]+)</BoxShadows>", RegexOptions.Compiled);

    private static string Tokens => AppSource.Read("Styles/Tokens.axaml");
    private static string PaletteCs => AppSource.Read("Theme/Palette.cs");

    private static string Rgb(string hex) => hex.Length == 9 ? "#" + hex[3..].ToLowerInvariant() : hex.ToLowerInvariant();
    private static string Hex((int R, int G, int B) c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    public static ((int R, int G, int B) Rgb, double Alpha) ParseRgba(string v)
    {
        var m = Regex.Match(v.Trim(), @"^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+)\s*)?\)$");
        Assert.True(m.Success, "无法解析 " + v);
        return ((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)),
            m.Groups[4].Success ? double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 1);
    }

    private static string Argb((int R, int G, int B) c, double a) => $"#{(int)Math.Floor(a * 255 + 0.5):x2}{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>CSS 阴影（可多层）→ Avalonia BoxShadows 文本（同 gen-tokens.py）。</summary>
    public static string ShadowText(string css, out List<(int R, int G, int B)> rgbs)
    {
        var parts = new List<string>();
        rgbs = new List<(int, int, int)>();
        foreach (var one in Regex.Split(css, @",(?![^(]*\))").Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            var m = Regex.Match(one, @"^(-?[\d.]+)(?:px)?\s+(-?[\d.]+)(?:px)?\s+([\d.]+)(?:px)?(?:\s+(-?[\d.]+)(?:px)?)?\s+(rgba?\([^)]*\))$");
            Assert.True(m.Success, "无法解析阴影 " + one);
            var (rgb, a) = ParseRgba(m.Groups[5].Value);
            rgbs.Add(rgb);
            parts.Add($"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value} {(m.Groups[4].Success ? m.Groups[4].Value : "0")} {Argb(rgb, a)}");
        }
        return string.Join(", ", parts);
    }

    [Fact]
    public void Json_IsWellFormed()
    {
        var cs = Colours;
        Assert.True(cs.Count >= 60, $"只有 {cs.Count} 个颜色令牌？");
        var dup = cs.GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dup.Count == 0, "颜色令牌重名：" + string.Join(", ", dup));
        Assert.All(cs, c => Assert.Matches(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*$", c.Name));
        Assert.All(cs, c => Assert.Matches(@"^#[0-9a-f]{6}$", c.Hex));
        Assert.Equal(cs.Count, cs.Select(c => KeyOf(c.Name)).Distinct().Count());
        var names = cs.Select(c => c.Name).ToHashSet();
        Assert.All(Aliases, a => Assert.True(names.Contains(a.To), $"别名 {a.Name} 指向不存在的令牌 {a.To}"));
        Assert.All(Aliases, a => Assert.DoesNotContain(KeyOf(a.Name), cs.Select(c => KeyOf(c.Name))));
        Assert.Contains(FocusToken, names);
        Assert.Equal("BgApp", KeyOf("--bg-app"));
        Assert.Equal("AD", KeyOf("--a-d"));
        Assert.Equal("T1", KeyOf("--t-1"));
        Assert.Equal("ChTSp", KeyOf("--ch-t-sp"));
        Assert.Equal("VLiqRunLine", KeyOf("--v-liq-run-line"));
        // pc-v3 的键名凡是意思还在的都保留（现有视图照常编译）：抽查几组
        foreach (var keep in new[] { "bg-app", "bg-card", "line", "line-2", "t-1", "t-3", "a", "a-d", "a-soft", "a-tint", "side-1", "g-dot", "r", "r-ink", "n-dot", "ch-t", "ch-p", "v-line" })
            Assert.Contains(keep, names);
        // 融合版的品牌蓝
        Assert.Equal("#204797", cs.Single(c => c.Name == "a").Hex);
    }

    /// <summary>样稿 10 层 :root 层叠之后的最终值（后写的覆盖先写的）。</summary>
    public static Dictionary<string, string> MockRoot(string html)
    {
        var css = string.Join("\n", Regex.Matches(html, @"<style[^>]*>(.*?)</style>", RegexOptions.Singleline).Select(m => m.Groups[1].Value));
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var final = new Dictionary<string, string>();
        foreach (Match b in Regex.Matches(css, @":root\s*\{([^}]*)\}"))
            foreach (Match v in Regex.Matches(b.Groups[1].Value, @"--([a-z0-9-]+)\s*:\s*([^;]+);?"))
                final[v.Groups[1].Value] = v.Groups[2].Value.Trim();
        return final;
    }

    private static string? NormHex(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (Regex.IsMatch(v, "^#[0-9a-f]{3}$")) return "#" + string.Concat(v[1..].Select(c => new string(c, 2)));
        return Regex.IsMatch(v, "^#[0-9a-f]{6}$") ? v : null;
    }

    [Fact]
    public void Json_ColoursComeFromTheMock()
    {
        var path = AppSource.FusionMock;
        if (path == null) return;   // 样稿不在（只有仓库的检出）：跳过交叉核对
        var html = File.ReadAllText(path);
        var hexes = Regex.Matches(html, @"#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})\b").Select(m => NormHex(m.Value)).Where(h => h != null).ToHashSet();
        var rgbs = Regex.Matches(html, @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)")
            .Select(m => Hex((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)))).ToHashSet();
        var root = MockRoot(html);
        Assert.Equal("#eceef1", root["bg"]);           // 最后一层（细节打磨）覆盖了前面九层
        Assert.Equal("#204797", root["brand"]);
        var bad = new List<string>();
        foreach (var c in Colours)
        {
            if (!hexes.Contains(c.Hex) && !rgbs.Contains(c.Hex)) bad.Add($"--{c.Name} {c.Hex}：样稿里没有这个颜色");
            foreach (Match m in Regex.Matches(c.Src, @":root\s+--([a-z0-9-]+)"))
            {
                var v = root.TryGetValue(m.Groups[1].Value, out var x) ? x : "（没有）";
                for (int i = 0; i < 8 && Regex.Match(v, @"^var\(--([a-z0-9-]+)\)$") is { Success: true } vm; i++) v = root.GetValueOrDefault(vm.Groups[1].Value, "");
                if (NormHex(v) != c.Hex) bad.Add($"--{c.Name} {c.Hex}：出处 :root --{m.Groups[1].Value} 层叠之后是 {v}");
            }
        }
        foreach (var (name, rgb, _) in Misc)
            if (!hexes.Contains(Hex(rgb)) && !rgbs.Contains(Hex(rgb))) bad.Add($"misc {name} {Hex(rgb)}：样稿里没有");
        foreach (var s in Shadows)
        {
            ShadowText(s.Css, out var srgbs);
            bad.AddRange(srgbs.Where(r => !hexes.Contains(Hex(r)) && !rgbs.Contains(Hex(r))).Select(r => $"shadow {s.Name} {Hex(r)}：样稿里没有"));
        }
        Assert.True(bad.Count == 0, "令牌与样稿对不上：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void TokensAxaml_ColoursMatchJson_AndFollowNamingRule()
    {
        var expected = Colours.ToDictionary(c => KeyOf(c.Name) + "Color", c => c.Hex);
        foreach (var (name, rgb, a) in Misc) expected[KeyOf(name) + "Color"] = Argb(rgb, a);
        var found = ColorRes.Matches(Tokens).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToLowerInvariant());
        var unknown = found.Keys.Where(k => !expected.ContainsKey(k)).ToList();
        Assert.True(unknown.Count == 0, "Tokens.axaml 里有 JSON 外 / 不合命名规则的 Color 键：" + string.Join(", ", unknown));
        var missing = expected.Keys.Where(k => !found.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Tokens.axaml 缺少令牌（重新跑 tools/gen-tokens.py）：" + string.Join(", ", missing));
        var wrong = expected.Where(e => found[e.Key] != e.Value).Select(e => $"{e.Key} = {found[e.Key]}（期望 {e.Value}）").ToList();
        Assert.True(wrong.Count == 0, "Tokens.axaml 的值与 fusion-tokens.json 不同：\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void TokensAxaml_EveryBrushReferencesItsColour()
    {
        var brushes = BrushRes.Matches(Tokens).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var aliases = Aliases.ToDictionary(a => KeyOf(a.Name) + "Brush", a => KeyOf(a.To) + "Color");
        var focus = KeyOf(FocusToken) + "Color";
        var bad = new List<string>();
        foreach (var (k, v) in brushes)
        {
            var want = k is "FocusBrush" or "FocusRingBrush" ? focus : aliases.TryGetValue(k, out var t) ? t : k.EndsWith("Brush", StringComparison.Ordinal) ? k[..^5] + "Color" : "?";
            if (want != v) bad.Add($"{k} → {v}（应为 {want}）");
        }
        Assert.True(bad.Count == 0, "Brush 没有引用应有的 Color：" + string.Join(", ", bad));
        foreach (var c in Colours) Assert.True(brushes.ContainsKey(KeyOf(c.Name) + "Brush"), $"缺少 {KeyOf(c.Name)}Brush");
        foreach (var a in Aliases) Assert.True(brushes.ContainsKey(KeyOf(a.Name) + "Brush"), $"缺少语义画刷 {KeyOf(a.Name)}Brush");
        Assert.True(brushes.ContainsKey("ToastBrush") && brushes.ContainsKey("OverlayBrush"), "缺少 ToastBrush / OverlayBrush");
        Assert.Matches(@"<SolidColorBrush\s+x:Key=""FocusRingBrush""\s+Color=""\{StaticResource\s+AColor\}""\s+Opacity=""0\.12""", Tokens);
    }

    [Fact]
    public void TokensAxaml_EveryOtherColourIsATokenOrAlphaVariant()
    {
        var rgbs = Colours.Select(c => c.Hex).Concat(Misc.Select(m => Hex(m.Rgb)))
            .Concat(Shadows.SelectMany(s => { ShadowText(s.Css, out var r); return r.Select(Hex); }))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bad = AnyHex.Matches(Tokens).Select(m => m.Value).Where(h => !rgbs.Contains(Rgb(h))).Distinct().ToList();
        Assert.True(bad.Count == 0, "Tokens.axaml 里有色板外的颜色：" + string.Join(", ", bad));
    }

    [Fact]
    public void TokensAxaml_ShadowsMatchJson()
    {
        var shadows = ShadowRes.Matches(Tokens).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim().ToLowerInvariant());
        foreach (var s in Shadows)
            Assert.Equal(ShadowText(s.Css, out _), shadows[s.Key]);
        Assert.Equal("0 1 2 0 #0a101828", shadows["ShadowCard"]);   // 面板 0 1px 2px rgba(16,24,40,.04) → α 10
        Assert.Equal("0 0 0 3 #1f204797", shadows["FocusRingShadow"]);   // 输入框聚焦 0 0 0 3px rgba(32,71,151,.12) → α 31
    }

    [Fact]
    public void PaletteCs_EqualsTokensAxaml()
    {
        var axaml = ColorRes.Matches(Tokens).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToLowerInvariant());
        var cs = new Regex(@"public static readonly Color (\w+) = (Rgb|Argb)\(0x([0-9a-fA-F]+)\);").Matches(PaletteCs)
            .ToDictionary(m => m.Groups[1].Value, m => "#" + m.Groups[3].Value.ToLowerInvariant());
        Assert.Equal(axaml.Keys.OrderBy(k => k), cs.Keys.OrderBy(k => k));
        foreach (var (k, v) in axaml) Assert.True(cs[k] == v, $"Palette.cs 的 {k} = {cs[k]}，Tokens.axaml = {v}");
        foreach (var k in axaml.Keys)
            Assert.Matches($@"public static readonly IImmutableSolidColorBrush {k[..^5]}Brush = new ImmutableSolidColorBrush\({k}\);", PaletteCs);
        // 语义画刷：Color 与 Brush 都是色板的别名
        foreach (var a in Aliases)
        {
            Assert.Matches($@"public static readonly Color {KeyOf(a.Name)}Color = {KeyOf(a.To)}Color;", PaletteCs);
            Assert.Matches($@"public static readonly IImmutableSolidColorBrush {KeyOf(a.Name)}Brush = {KeyOf(a.To)}Brush;", PaletteCs);
        }
        // 阴影字符串与 Tokens.axaml 相同
        var shadows = ShadowRes.Matches(Tokens).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim().ToLowerInvariant());
        var csShadows = new Regex(@"public static readonly BoxShadows (\w+) = BoxShadows\.Parse\(""([^""]+)""\)").Matches(PaletteCs)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim().ToLowerInvariant());
        Assert.Equal(shadows.OrderBy(k => k.Key), csShadows.OrderBy(k => k.Key));
        // 令牌表（审计 / 陈列页用）与 JSON 同序同值
        var tokens = new Regex(@"new\(""(--[\w-]+)"", ""(\w+)"", ""([^""]+)"", (\w+)\)").Matches(PaletteCs).Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value)).ToList();
        Assert.Equal(Colours.Select(c => "--" + c.Name), tokens.Select(t => t.Item1));
        Assert.Equal(Colours.Select(c => c.Group), tokens.Select(t => t.Item3));
        Assert.All(tokens, t => Assert.Equal(KeyOf(t.Item1), t.Item2));
        Assert.All(tokens, t => Assert.Equal(t.Item2 + "Color", t.Item4));
    }

    /// <summary>紫色色相（HSV 色相 245°–330°、饱和度 ≥ 0.06（浅紫底也算）、不太暗）：pc-v3 的强调色 #765cc5 在 255°。</summary>
    public static bool IsPurple(string hex)
    {
        hex = Rgb(hex);
        double r = Convert.ToInt32(hex[1..3], 16) / 255.0, g = Convert.ToInt32(hex[3..5], 16) / 255.0, b = Convert.ToInt32(hex[5..7], 16) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (max < 0.15 || d / max < 0.06) return false;
        double h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return h >= 245 && h <= 330;
    }

    /// <summary>曲线识别色也不例外：2026-11-18 用户确认 RV7 不用紫色，改为深青 #264653，8 路识别色都要过「无紫色」检查。</summary>
    private static readonly HashSet<string> SeriesExempt = new();

    [Fact]
    public void NoPurple_InPalette()
    {
        Assert.True(IsPurple("#765cc5"));    // pc-v3 的 a
        Assert.True(IsPurple("#e5e0f5"));    // pc-v3 的 a-soft
        Assert.False(IsPurple("#204797"));   // ALZK 品牌蓝
        Assert.False(IsPurple("#c9d4ea"));
        var bad = Colours.Where(c => !SeriesExempt.Contains(c.Name) && IsPurple(c.Hex)).Select(c => $"--{c.Name} {c.Hex}").ToList();
        Assert.True(bad.Count == 0, "色板里有紫色：" + string.Join(", ", bad));
    }

    /// <summary>源码里（含旧界面允许名单里的文件）也不许再写紫色字面量（注释除外）。</summary>
    [Fact]
    public void NoPurple_InSources()
    {
        var bad = new List<string>();
        var series = Colours.Where(c => SeriesExempt.Contains(c.Name)).Select(c => c.Hex).ToHashSet();
        foreach (var (rel, full) in AppSource.Sources())
        {
            var lines = File.ReadAllLines(full);
            for (int i = 0; i < lines.Length; i++)
            {
                var code = Regex.Replace(lines[i], @"<!--.*?-->|//.*$", "");
                foreach (Match m in AnyHex.Matches(code))
                    if (IsPurple(m.Value) && !series.Contains(Rgb(m.Value))) bad.Add($"{rel}:{i + 1} {m.Value}");
            }
        }
        Assert.True(bad.Count == 0, "源码里还有紫色：\n" + string.Join("\n", bad));
    }
}
