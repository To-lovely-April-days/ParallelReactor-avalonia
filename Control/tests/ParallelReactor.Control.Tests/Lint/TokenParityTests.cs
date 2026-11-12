using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ParallelReactor.Control.Tests.Lint;

/// <summary>
/// 令牌一致性（PORT-PLAN §4.7 第 2 条）：期望表抄自样稿 RESTYLE-BRIEF §2（= css/base.css :root = js/core.js PR.PALETTE / PR.PALETTE_MISC），
/// 67 个颜色令牌 + 4 个杂项（toast、overlay、shadow-card、shadow-dlg；另有 shadow-pop 只用于阴影）。核对：
///  · Tokens.axaml 的每个 Color 都在表里、键名按 §4.1 的规则（kebab → PascalCase + Color / Brush）、值相同；表里每个令牌都有 Color 和 Brush；
///  · 每个 Brush 引用自己的 Color（StaticResource）；
///  · Tokens.axaml 里其它颜色（阴影、杂项、手写的渐变 / 透明度变体）的 RGB 都在表里（允许 #aarrggbb 的透明度变体）；
///  · Theme/Palette.cs 的常量与 Tokens.axaml 完全一致（含杂项与三个阴影）。
/// </summary>
public class TokenParityTests
{
    /// <summary>RESTYLE-BRIEF §2 的 67 个颜色令牌（顺序同 PR.PALETTE）。</summary>
    public static readonly (string Css, string Hex)[] Palette =
    {
        // 底色
        ("--bg-app", "#ececee"), ("--bg-panel", "#f7f7f8"), ("--bg-head", "#fafafb"), ("--bg-card", "#ffffff"),
        ("--bg-side", "#f4f4f5"), ("--bg-sunken", "#f1f1f3"), ("--bg-hover", "#ececf1"), ("--bg-press", "#e7e7eb"),
        // 线
        ("--line-strong", "#aaaab0"), ("--line", "#c9c9cd"), ("--line-2", "#d5d5d8"), ("--line-soft", "#dedee1"), ("--line-faint", "#ededf0"), ("--line-menu", "#2e2e31"),
        // 文字
        ("--t-1", "#2f2f35"), ("--t-2", "#3f3f45"), ("--t-3", "#5f5f66"), ("--t-4", "#75757c"), ("--t-dis", "#a5a5ab"), ("--t-inv", "#ffffff"),
        // 强调色
        ("--a", "#765cc5"), ("--a-d", "#6550b1"), ("--a-ink", "#554677"), ("--a-soft", "#e5e0f5"), ("--a-tint", "#f1eefa"), ("--a-line", "#a999da"), ("--a-tab", "#8a78cd"),
        ("--side-1", "#8269d0"), ("--side-2", "#6e57ba"), ("--side-line", "#6753a8"), ("--side-ink", "#eeeaff"),
        // 运行
        ("--g-1", "#f7fff1"), ("--g-2", "#efffe4"), ("--g-3", "#e5ffd0"), ("--g-4", "#d9fbb9"), ("--g-line", "#dcf8c7"), ("--g-edge", "#d9f9bf"),
        ("--g-dot", "#b7e99c"), ("--g-dot-line", "#a0d782"), ("--g-ink", "#4a7a31"),
        // 报警
        ("--r", "#c4505d"), ("--r-ink", "#b0404d"), ("--r-bg", "#fbeff1"), ("--r-line", "#ebc0c6"), ("--r-btn", "#c43f4c"), ("--r-btn-d", "#ad3441"),
        // 中性状态
        ("--n-dot", "#c4c4ca"), ("--n-dot-line", "#b0b0b6"),
        // 曲线
        ("--ch-bg", "#fafafa"), ("--ch-grid", "#e3e3e6"), ("--ch-axis", "#aaaeb1"), ("--ch-t", "#6f797b"), ("--ch-t-sp", "#aab1b3"), ("--ch-p", "#7aa088"), ("--ch-p-sp", "#aac5b2"),
        ("--ch-s1", "#4f5559"), ("--ch-s2", "#7d8789"), ("--ch-s3", "#a9b0b2"), ("--ch-mark", "#8a78cd"), ("--ch-limit", "#c4505d"),
        // 釜体
        ("--v-line", "#8d8d94"), ("--v-detail", "#a5a5ab"), ("--v-liq", "#ededf0"), ("--v-liq-run", "#e6f7da"), ("--v-liq-run-line", "#b9dca3"),
        ("--v-liq-alarm", "#f8e4e7"), ("--v-line-alarm", "#c98b94"),
    };

    /// <summary>
    /// §2 Misc（PR.PALETTE_MISC + shadow-pop）：（令牌, RGB, alpha）。toast / overlay 是颜色资源（#aarrggbb），三个阴影是 BoxShadows。
    /// </summary>
    public static readonly (string Css, string Rgb, double Alpha)[] Misc =
    {
        ("--toast", "#2a2a30", 0.92),
        ("--overlay", "#1e1e26", 0.25),
        ("--shadow-card", "#282832", 0.06),
        ("--shadow-dlg", "#000000", 0.25),
        ("--shadow-pop", "#000000", 0.16),   // 样稿 base.css 的 --shadow-pop（与 shadow-dlg 同为黑色，只许出现在阴影里）
    };

    /// <summary>§4.1 命名规则：去掉「--」，kebab 的每段首字母大写后拼起来（bg-app → BgApp、a-d → AD、t-1 → T1、ch-t-sp → ChTSp）。</summary>
    public static string KeyOf(string css) => string.Concat(css.TrimStart('-').Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    private static readonly Regex ColorRes = new(@"<Color\s+x:Key=""(\w+)"">\s*(#[0-9A-Fa-f]{6,8})\s*</Color>", RegexOptions.Compiled);
    private static readonly Regex BrushRes = new(@"<SolidColorBrush\s+x:Key=""(\w+)""\s+Color=""\{StaticResource\s+(\w+)\}""\s*/>", RegexOptions.Compiled);
    private static readonly Regex AnyHex = new(@"#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\b", RegexOptions.Compiled);
    private static readonly Regex Shadow = new(@"<BoxShadows\s+x:Key=""(\w+)"">([^<]+)</BoxShadows>", RegexOptions.Compiled);

    private static string Tokens => AppSource.Read("Styles/Tokens.axaml");
    private static string PaletteCs => AppSource.Read("Theme/Palette.cs");

    private static string Rgb(string hex) => hex.Length == 9 ? "#" + hex[3..].ToLowerInvariant() : hex.ToLowerInvariant();

    [Fact]
    public void Table_Has67ColourTokens_And4Misc()
    {
        Assert.Equal(67, Palette.Length);
        Assert.Equal(67, Palette.Select(p => p.Css).Distinct().Count());
        Assert.Equal(4, Misc.Count(m => m.Css != "--shadow-pop"));
        Assert.Equal("BgApp", KeyOf("--bg-app"));
        Assert.Equal("AD", KeyOf("--a-d"));
        Assert.Equal("T1", KeyOf("--t-1"));
        Assert.Equal("ChTSp", KeyOf("--ch-t-sp"));
        Assert.Equal("VLiqRunLine", KeyOf("--v-liq-run-line"));
    }

    [Fact]
    public void TokensAxaml_ColoursMatchTable_AndFollowNamingRule()
    {
        var expected = Palette.ToDictionary(p => KeyOf(p.Css) + "Color", p => p.Hex);
        expected["ToastColor"] = "#eb2a2a30";    // rgba(42,42,48,.92) → α = round(.92 × 255) = 235
        expected["OverlayColor"] = "#401e1e26";  // rgba(30,30,38,.25) → α = 64
        var found = ColorRes.Matches(Tokens).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToLowerInvariant());
        var unknown = found.Keys.Where(k => !expected.ContainsKey(k)).ToList();
        Assert.True(unknown.Count == 0, "Tokens.axaml 里有表外 / 不合命名规则的 Color 键：" + string.Join(", ", unknown));
        var missing = expected.Keys.Where(k => !found.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Tokens.axaml 缺少令牌：" + string.Join(", ", missing));
        var wrong = expected.Where(e => found[e.Key] != e.Value).Select(e => $"{e.Key} = {found[e.Key]}（期望 {e.Value}）").ToList();
        Assert.True(wrong.Count == 0, "Tokens.axaml 的值与 RESTYLE-BRIEF §2 不同：\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void TokensAxaml_EveryBrushReferencesItsColour()
    {
        var brushes = BrushRes.Matches(Tokens).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var bad = brushes.Where(b => b.Key != "FocusBrush" && (!b.Key.EndsWith("Brush", StringComparison.Ordinal) || b.Key[..^5] + "Color" != b.Value))
            .Select(b => $"{b.Key} → {b.Value}").ToList();
        Assert.True(bad.Count == 0, "Brush 没有引用同名的 Color：" + string.Join(", ", bad));
        foreach (var (css, _) in Palette)
            Assert.True(brushes.ContainsKey(KeyOf(css) + "Brush"), $"缺少 {KeyOf(css)}Brush");
        Assert.True(brushes.ContainsKey("ToastBrush") && brushes.ContainsKey("OverlayBrush"), "缺少 ToastBrush / OverlayBrush");
        Assert.Matches(@"<SolidColorBrush\s+x:Key=""FocusBrush""\s+Color=""\{StaticResource\s+ADColor\}""", Tokens);   // 焦点环 = a-d
    }

    [Fact]
    public void TokensAxaml_EveryOtherColourIsATokenOrAlphaVariant()
    {
        var rgbs = Palette.Select(p => p.Hex).Concat(Misc.Select(m => m.Rgb)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bad = AnyHex.Matches(Tokens).Select(m => m.Value).Where(h => !rgbs.Contains(Rgb(h))).Distinct().ToList();
        Assert.True(bad.Count == 0, "Tokens.axaml 里有色板外的颜色：" + string.Join(", ", bad));
    }

    [Fact]
    public void TokensAxaml_ShadowsMatchMisc()
    {
        var shadows = Shadow.Matches(Tokens).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim().ToLowerInvariant());
        Assert.Equal("0 1 2 0 #0f282832", shadows["ShadowCard"]);   // 0 1px 2px rgba(40,40,50,.06) → α 15
        Assert.Equal("0 8 28 0 #29000000", shadows["ShadowPop"]);   // 0 8px 28px rgba(0,0,0,.16) → α 41
        Assert.Equal("0 10 45 0 #40000000", shadows["ShadowDlg"]);  // 0 10px 45px rgba(0,0,0,.25) → α 64
        foreach (var (css, rgb, alpha) in Misc.Where(m => m.Css.StartsWith("--shadow", StringComparison.Ordinal)))
        {
            var v = shadows[KeyOf(css)];
            var hex = AnyHex.Match(v).Value;
            Assert.Equal(rgb, Rgb(hex));
            Assert.Equal((int)Math.Round(alpha * 255), int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber));
        }
    }

    [Fact]
    public void PaletteCs_EqualsTokensAxaml()
    {
        var axaml = ColorRes.Matches(Tokens).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToLowerInvariant());
        var cs = new Regex(@"public static readonly Color (\w+) = (Rgb|Argb)\(0x([0-9a-fA-F]+)\);").Matches(PaletteCs)
            .ToDictionary(m => m.Groups[1].Value, m => "#" + m.Groups[3].Value.ToLowerInvariant());
        Assert.Equal(axaml.Keys.OrderBy(k => k), cs.Keys.OrderBy(k => k));
        foreach (var (k, v) in axaml) Assert.True(cs[k] == v, $"Palette.cs 的 {k} = {cs[k]}，Tokens.axaml = {v}");
        // 画刷：每个 Color 一个同名 Brush
        foreach (var k in axaml.Keys)
            Assert.Matches($@"public static readonly IImmutableSolidColorBrush {k[..^5]}Brush = new ImmutableSolidColorBrush\({k}\);", PaletteCs);
        // 阴影字符串与 Tokens.axaml 相同
        var shadows = Shadow.Matches(Tokens).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim().ToLowerInvariant());
        var csShadows = new Regex(@"public static readonly BoxShadows (\w+) = BoxShadows\.Parse\(""([^""]+)""\)").Matches(PaletteCs)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim().ToLowerInvariant());
        Assert.Equal(shadows.OrderBy(k => k.Key), csShadows.OrderBy(k => k.Key));
        // 令牌表（审计用）与期望表同序同值
        var tokens = new Regex(@"new\(""(--[\w-]+)"", ""(\w+)"", ""[^""]+"", (\w+)\)").Matches(PaletteCs).Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)).ToList();
        Assert.Equal(Palette.Select(p => p.Css), tokens.Select(t => t.Item1));
        Assert.All(tokens, t => Assert.Equal(KeyOf(t.Item1), t.Item2));
        Assert.All(tokens, t => Assert.Equal(t.Item2 + "Color", t.Item3));
    }
}
