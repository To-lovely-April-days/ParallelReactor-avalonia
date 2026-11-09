using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ParallelReactor.Control.Tests.Lint;

/// <summary>
/// 封闭色板的源码检查（PORT-PLAN §4.7 第 1 条）：App 的 .axaml / .cs 里不许自己造颜色 ——
/// 颜色字面量（#rgb / #rrggbb / #aarrggbb）、Color.Parse / TryParse / FromRgb / FromArgb / FromUInt32 / FromHsl / FromHsv、
/// new Color(…) / Color x = new(…)、new (Immutable)SolidColorBrush(数字)、HslColor / HsvColor、KnownColor(s)、(Solid)Brush.Parse、Colors.xxx、
/// Brushes.xxx（Transparent 除外），以及 XAML 里写颜色名（Background="Red"、Color="DodgerBlue"、&lt;Color&gt;White&lt;/Color&gt;，Transparent 除外）。
/// 颜色只能来自 Styles/Tokens.axaml 与 Theme/Palette.cs（两者由 tools/gen-tokens.py 从样稿生成，见 TokenParityTests）。
/// 允许名单只列 M1 时仍在用的旧界面文件（M7 换掉旧界面后清空）和故意违规的自检文件；名单里的文件必须存在，而且必须真的还有违规
/// （没有违规了就从名单里删掉，名单只会变短）。
/// </summary>
public class PaletteLintTests
{
    /// <summary>色板本身：只有这两处可以写颜色值。</summary>
    public static readonly string[] TokenFiles = { "Styles/Tokens.axaml", "Theme/Palette.cs" };

    /// <summary>
    /// 旧界面（pc-control.html 的 1:1 移植，M2–M6 期间仍在用，M7 删除或改写）。前 6 个是 PORT-PLAN §4.7 列出的（Property.axaml 已经没有颜色值，M1 复核时删掉），
    /// 后面是 M1 核对时发现同样带旧色值的旧文件（时间轴、对话框、外壳样式、全部曲线的 8 色、性能测试与属性查看器的灰色图标、主视图的白字）。
    /// </summary>
    public static readonly string[] LegacyAllowList =
    {
        "Styles/LegacyTokens.axaml",
        "Styles/Perf.axaml",
        "Styles/Plan.axaml",
        "Styles/Panels.axaml",
        "Controls/ProcessChart.cs",
        "Controls/FillBar.cs",
        // M1 核对补上的旧文件
        "Controls/TimelineTracks.cs",
        "Styles/Dialogs.axaml",
        "Styles/Shell.axaml",
        "ViewModels/MainViewModel.cs",
        "Views/PerfProgressView.axaml",
        "Views/PropertyViewerView.axaml",
        "Views/MainView.axaml",   // Foreground="White"（XAML 颜色名规则，M1 复核补上）
    };

    /// <summary>故意违规的自检文件（审计自检要种下色板外的颜色，证明 ShotAudit 能报出来）。不是产品代码，不随 M7 清空。</summary>
    public static readonly string[] DeliberateAllowList = { "Shot/AuditSelfTest.cs" };

    /// <summary>Avalonia KnownColor / CSS 的颜色名（XAML 里写颜色名同样是色板外的颜色；Transparent 除外）。</summary>
    public static readonly HashSet<string> KnownColorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AliceBlue", "AntiqueWhite", "Aqua", "Aquamarine", "Azure", "Beige", "Bisque", "Black", "BlanchedAlmond", "Blue", "BlueViolet", "Brown",
        "BurlyWood", "CadetBlue", "Chartreuse", "Chocolate", "Coral", "CornflowerBlue", "Cornsilk", "Crimson", "Cyan", "DarkBlue", "DarkCyan",
        "DarkGoldenrod", "DarkGray", "DarkGreen", "DarkGrey", "DarkKhaki", "DarkMagenta", "DarkOliveGreen", "DarkOrange", "DarkOrchid", "DarkRed",
        "DarkSalmon", "DarkSeaGreen", "DarkSlateBlue", "DarkSlateGray", "DarkSlateGrey", "DarkTurquoise", "DarkViolet", "DeepPink", "DeepSkyBlue",
        "DimGray", "DimGrey", "DodgerBlue", "Firebrick", "FloralWhite", "ForestGreen", "Fuchsia", "Gainsboro", "GhostWhite", "Gold", "Goldenrod",
        "Gray", "Grey", "Green", "GreenYellow", "Honeydew", "HotPink", "IndianRed", "Indigo", "Ivory", "Khaki", "Lavender", "LavenderBlush",
        "LawnGreen", "LemonChiffon", "LightBlue", "LightCoral", "LightCyan", "LightGoldenrodYellow", "LightGray", "LightGreen", "LightGrey",
        "LightPink", "LightSalmon", "LightSeaGreen", "LightSkyBlue", "LightSlateGray", "LightSlateGrey", "LightSteelBlue", "LightYellow", "Lime",
        "LimeGreen", "Linen", "Magenta", "Maroon", "MediumAquamarine", "MediumBlue", "MediumOrchid", "MediumPurple", "MediumSeaGreen",
        "MediumSlateBlue", "MediumSpringGreen", "MediumTurquoise", "MediumVioletRed", "MidnightBlue", "MintCream", "MistyRose", "Moccasin",
        "NavajoWhite", "Navy", "OldLace", "Olive", "OliveDrab", "Orange", "OrangeRed", "Orchid", "PaleGoldenrod", "PaleGreen", "PaleTurquoise",
        "PaleVioletRed", "PapayaWhip", "PeachPuff", "Peru", "Pink", "Plum", "PowderBlue", "Purple", "RebeccaPurple", "Red", "RosyBrown", "RoyalBlue",
        "SaddleBrown", "Salmon", "SandyBrown", "SeaGreen", "SeaShell", "Sienna", "Silver", "SkyBlue", "SlateBlue", "SlateGray", "SlateGrey", "Snow",
        "SpringGreen", "SteelBlue", "Tan", "Teal", "Thistle", "Tomato", "Turquoise", "Violet", "Wheat", "White", "WhiteSmoke", "Yellow", "YellowGreen",
    };

    private static readonly (string What, Regex Re)[] Rules =
    {
        ("颜色字面量 #rgb / #rrggbb / #aarrggbb", new Regex(@"#[0-9A-Fa-f]{3,8}\b", RegexOptions.Compiled)),
        ("Color.Parse / TryParse / FromRgb / FromArgb / FromUInt32 / FromHsl / FromHsv",
            new Regex(@"\bColor\.(Parse|TryParse|FromRgb|FromArgb|FromUInt32|FromHsl|FromHsla|FromHsv|FromHsva)\s*\(", RegexOptions.Compiled)),
        ("new Color(…) / Color x = new(…)", new Regex(@"\bnew\s+Color\s*\(|\bColor\??\s+\w+\s*(?:=|=>)\s*new\s*\(", RegexOptions.Compiled)),
        ("new (Immutable)SolidColorBrush(数字)", new Regex(@"\bnew\s+(?:Immutable)?SolidColorBrush\s*\(\s*(?:0[xX][0-9A-Fa-f]+|\d+)[uUlL]*\s*[,)]", RegexOptions.Compiled)),
        ("HslColor / HsvColor", new Regex(@"\bnew\s+(?:HslColor|HsvColor)\s*\(|\b(?:HslColor|HsvColor)\.Parse\s*\(", RegexOptions.Compiled)),
        ("KnownColor(s)", new Regex(@"\bKnownColors?\b", RegexOptions.Compiled)),
        ("(Solid)Brush.Parse", new Regex(@"\b(?:SolidColor)?Brush\.Parse\s*\(", RegexOptions.Compiled)),
        ("Colors.xxx", new Regex(@"\bColors\.[A-Z]\w*", RegexOptions.Compiled)),
        ("Brushes.xxx（Transparent 除外）", new Regex(@"\bBrushes\.(?!Transparent\b)[A-Z]\w*", RegexOptions.Compiled)),
    };

    // XAML 颜色名：颜色类属性（…Brush / …Color / Background / Foreground / Fill / Stroke）的特性值、Setter 的 Value、<Color> / <SolidColorBrush> 的内容
    private const string ColourProp = @"(?:\w+\.)?\w*(?:Brush|Color|Background|Foreground|Fill|Stroke)";
    private static readonly Regex XamlAttr = new(@"(?<![\w.:])(" + ColourProp + @")=""([A-Za-z]+)""", RegexOptions.Compiled);
    private static readonly Regex XamlSetter = new(@"Property=""(" + ColourProp + @")""\s+Value=""([A-Za-z]+)""", RegexOptions.Compiled);
    private static readonly Regex XamlContent = new(@"<(?:Color|SolidColorBrush)\b[^>]*>\s*([A-Za-z]+)\s*</", RegexOptions.Compiled);

    private static bool IsNamedColour(string v) => KnownColorNames.Contains(v) && !v.Equals("Transparent", StringComparison.OrdinalIgnoreCase);

    /// <summary>一个文件里的违规：（行号, 规则, 行内容）。axaml = true 时另查 XAML 颜色名。</summary>
    public static IEnumerable<(int Line, string Rule, string Text)> Violations(string text, bool axaml = false)
    {
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            foreach (var (what, re) in Rules)
                if (re.IsMatch(l))
                    yield return (i + 1, what, l.Trim());
            if (!axaml) continue;
            bool named = XamlAttr.Matches(l).Any(m => !m.Groups[1].Value.EndsWith("x:Key", StringComparison.Ordinal) && IsNamedColour(m.Groups[2].Value))
                         || XamlSetter.Matches(l).Any(m => IsNamedColour(m.Groups[2].Value))
                         || XamlContent.Matches(l).Any(m => IsNamedColour(m.Groups[1].Value));
            if (named) yield return (i + 1, "XAML 颜色名（Red / White / DodgerBlue …）", l.Trim());
        }
    }

    private static bool IsAxaml(string rel) => rel.EndsWith(".axaml", StringComparison.Ordinal);

    [Fact]
    public void NoColourLiterals_OutsideTokensAndAllowLists()
    {
        var bad = new List<string>();
        int scanned = 0;
        foreach (var (rel, full) in AppSource.Sources())
        {
            if (TokenFiles.Contains(rel) || LegacyAllowList.Contains(rel) || DeliberateAllowList.Contains(rel)) continue;
            scanned++;
            foreach (var (line, rule, t) in Violations(File.ReadAllText(full), IsAxaml(rel)))
                bad.Add($"{rel}:{line} [{rule}] {(t.Length > 120 ? t[..120] + "…" : t)}");
        }
        Assert.True(scanned > 50, $"只扫到 {scanned} 个文件（目录 {AppSource.Dir} 不对？）");
        Assert.True(bad.Count == 0, "色板外的颜色写法（只许用 Tokens.axaml / Palette.cs 的令牌）：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void AllowListEntries_Exist()
    {
        foreach (var rel in LegacyAllowList.Concat(DeliberateAllowList).Concat(TokenFiles))
            Assert.True(File.Exists(Path.Combine(AppSource.Dir, rel)), $"允许名单里的 {rel} 不存在了：从名单里删掉");
    }

    /// <summary>名单只会变短：名单里的文件已经没有违规了，就必须从名单里删掉（否则以后新加的违规会被悄悄放过）。</summary>
    [Fact]
    public void AllowListEntries_StillNeeded()
    {
        var stale = LegacyAllowList.Concat(DeliberateAllowList)
            .Where(rel => File.Exists(Path.Combine(AppSource.Dir, rel)) && !Violations(AppSource.Read(rel), IsAxaml(rel)).Any())
            .ToList();
        Assert.True(stale.Count == 0, "这些文件已经没有色板外的颜色了，从允许名单里删掉：" + string.Join(", ", stale));
    }

    [Theory]
    [InlineData("<Setter Property=\"Background\" Value=\"#ececee\" />", true)]
    [InlineData("<SolidColorBrush Color=\"#FF765CC5\" />", true)]
    [InlineData("var c = Color.Parse(\"#abc\");", true)]
    [InlineData("Color.TryParse(s, out var c);", true)]
    [InlineData("var c = Color.FromRgb(1, 2, 3);", true)]
    [InlineData("var c = Color.FromHsl(new HslColor(1, 0.5, 0.5, 0.5));", true)]
    [InlineData("var c = new Color(255, 255, 0, 0);", true)]
    [InlineData("private static readonly Color OffRed = new(255, 255, 0, 0);", true)]
    [InlineData("var b = new SolidColorBrush(0xFF00FF00);", true)]
    [InlineData("var b = new ImmutableSolidColorBrush(4278255360u);", true)]
    [InlineData("var c = KnownColors.Red;", true)]
    [InlineData("var b = SolidColorBrush.Parse(\"Red\");", true)]
    [InlineData("var b = Brush.Parse(\"Red\");", true)]
    [InlineData("Foreground = Brushes.Red;", true)]
    [InlineData("Background = Colors.White;", true)]
    [InlineData("Background = Brushes.Transparent;", false)]
    [InlineData("var b = new SolidColorBrush(Palette.T1Color, 0.4);", false)]
    [InlineData("a[i] = new ImmutableSolidColorBrush(Palette.WithAlpha(Palette.T1Color, 0.05));", false)]
    [InlineData("#if DEBUG", false)]
    [InlineData("<Border Name=\"PART_Body\" />  <!-- Border#PART_Body -->", false)]
    [InlineData("$\"#{c.R:x2}{c.G:x2}{c.B:x2}\"", false)]
    [InlineData("组件陈列页（MOCK #/gallery）", false)]
    public void Rules_FlagWhatTheyShould(string line, bool flagged) => Assert.Equal(flagged, Violations(line).Any());

    [Theory]
    [InlineData("<Border Background=\"Red\" />", true)]
    [InlineData("<TextBlock Foreground=\"White\" Text=\"x\" />", true)]
    [InlineData("<SolidColorBrush Color=\"DodgerBlue\" />", true)]
    [InlineData("<GradientStop Offset=\"0\" Color=\"red\" />", true)]
    [InlineData("<Setter Property=\"Background\" Value=\"Black\" />", true)]
    [InlineData("<Setter Property=\"TextBlock.Foreground\" Value=\"Gray\" />", true)]
    [InlineData("<Border BorderBrush=\"Navy\" BorderThickness=\"1\" />", true)]
    [InlineData("<Path Fill=\"Orange\" Stroke=\"{StaticResource T1Brush}\" />", true)]
    [InlineData("<Color x:Key=\"Accent\">Purple</Color>", true)]
    [InlineData("<Border Background=\"Transparent\" />", false)]
    [InlineData("<Setter Property=\"Background\" Value=\"Transparent\" />", false)]
    [InlineData("<SolidColorBrush x:Key=\"Purple\" Color=\"{StaticResource AColor}\" />", false)]
    [InlineData("<TextBlock Text=\"Red\" />", false)]
    [InlineData("<Button Content=\"White\" Classes=\"Green\" />", false)]
    [InlineData("<c:IconView Icon=\"tag\" Foreground=\"{StaticResource T3Brush}\" />", false)]
    public void XamlNamedColours_AreFlagged(string line, bool flagged) => Assert.Equal(flagged, Violations(line, axaml: true).Any());
}
