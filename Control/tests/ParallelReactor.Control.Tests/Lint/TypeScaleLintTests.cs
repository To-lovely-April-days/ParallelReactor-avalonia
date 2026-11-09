using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ParallelReactor.Control.Tests.Lint;

/// <summary>
/// 字号下限的源码检查（PORT-PLAN §4.7 第 3 条；RESTYLE-BRIEF §3：任何文字 ≥ 13 px，中文 ≥ 14 px —— 中文的下限由运行时 ShotAudit 查）。
/// .axaml 里 FontSize 的字面量（属性、Setter、x:Double 的 Fs* 令牌）必须 ≥ 13，或者引用 Fs* 资源 / 绑定；
/// .cs 里写死的字号（FontSize = n、FontSizeProperty, n、fontSize: n、emSize: n、["…FontSize"] = n）&lt; 13 即失败；
/// 自绘文字（M4 / M5 的曲线、釜体会用）：new FormattedText(…) 的 emSize、new TextLayout(…) / new GenericTextRunProperties(…) 的字号参数是 &lt; 13 的字面量即失败
/// （跨行的调用也查；运行时由 ShotAudit 查 IAuditText 报告的自绘文字）。
/// 允许名单只列旧界面（字号 6–11 的 1:1 旧稿，M7 删除）；名单里的文件必须还有违规（没有了就删掉，名单只会变短）。
/// </summary>
public class TypeScaleLintTests
{
    public const double Min = 13;

    /// <summary>旧界面（M2–M6 期间仍在用，M7 删除或改写）：旧稿的 6–11 px 字号都在这些文件里。</summary>
    public static readonly string[] LegacyAllowList =
    {
        "Styles/LegacyTokens.axaml",   // 旧令牌里的 Fs6–Fs11
        "Styles/Dialogs.axaml",
        "Styles/Panels.axaml",
        "Styles/Perf.axaml",
        "Styles/Plan.axaml",
        "Styles/Property.axaml",
        "Styles/Shell.axaml",
        "Styles/Timeline.axaml",
        "Views/MainView.axaml",
        "Views/MainWindow.axaml",
        "Views/OverviewView.axaml",
        // 自绘的 7 px 刻度 / 标签（FormattedText 字面量规则，M1 复核补上）
        "Controls/ProcessChart.cs",
        "Controls/TimelineTracks.cs",
    };

    private const string Num = @"(\d+(?:\.\d+)?)";
    /// <summary>C# 数字字面量（可带 d / f / m 后缀），后面不能再接标识符或小数点。</summary>
    private const string CsNum = Num + @"[dDfFmM]?(?![\w.])";

    private static readonly Regex[] AxamlRules =
    {
        new(@"\bFontSize=""" + Num + @"""", RegexOptions.Compiled),                                  // FontSize="9"、TextElement.FontSize="9"
        new(@"Property=""(?:\w+\.)?FontSize""\s+Value=""" + Num + @"""", RegexOptions.Compiled),       // <Setter Property="FontSize" Value="8" />
        new(@"<x:Double\s+x:Key=""Fs\w*"">\s*" + Num + @"\s*</x:Double>", RegexOptions.Compiled),     // 字号令牌本身
    };

    private static readonly Regex[] CsRules =
    {
        new(@"\bFontSize\s*=\s*" + CsNum, RegexOptions.Compiled),                // FontSize = 9 / FontSize = 9d
        new(@"FontSizeProperty\s*,\s*" + CsNum, RegexOptions.Compiled),          // SetValue(TextBlock.FontSizeProperty, 9)
        new(@"\b(?:fontSize|emSize|fontRenderingEmSize)\s*:\s*" + CsNum, RegexOptions.Compiled),   // 命名参数 fontSize: 9 / emSize: 9
        new(@"\[""\w*FontSize""\]\s*=\s*" + CsNum, RegexOptions.Compiled),       // ["ControlContentThemeFontSize"] = 9
    };

    /// <summary>一个文件里小于下限的字号：（行号, 字号, 行内容）。.cs 另查自绘文字的构造调用（<see cref="TooSmallDrawn"/>）。</summary>
    public static IEnumerable<(int Line, double Size, string Text)> TooSmall(string text, bool axaml)
    {
        var lines = text.Split('\n');
        var rules = axaml ? AxamlRules : CsRules;
        for (int i = 0; i < lines.Length; i++)
            foreach (var re in rules)
                foreach (Match m in re.Matches(lines[i]))
                {
                    var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (v < Min) yield return (i + 1, v, lines[i].Trim());
                }
        if (!axaml)
            foreach (var hit in TooSmallDrawn(text)) yield return hit;
    }

    private static readonly Regex DrawnCtor = new(@"\bnew\s+(FormattedText|TextLayout|GenericTextRunProperties)\s*\(", RegexOptions.Compiled);
    private static readonly Regex Literal = new(@"^(\d+(?:\.\d+)?)[dDfFmM]?$", RegexOptions.Compiled);

    /// <summary>
    /// 自绘文字的字号参数：FormattedText(text, culture, flow, typeface, <b>emSize</b>, brush)、
    /// TextLayout(text, typeface, <b>fontSize</b>, …) 或 TextLayout(text, typeface, fontFeatures, <b>fontSize</b>, …)、
    /// GenericTextRunProperties(typeface, <b>size</b>) 或 (typeface, fontFeatures, <b>size</b>)。只看字面量（变量由运行时审计查）。
    /// </summary>
    public static IEnumerable<(int Line, double Size, string Text)> TooSmallDrawn(string text)
    {
        foreach (Match m in DrawnCtor.Matches(text))
        {
            var args = TopLevelArgs(text, m.Index + m.Length);
            int[] at = m.Groups[1].Value switch { "FormattedText" => new[] { 4 }, "TextLayout" => new[] { 2, 3 }, _ => new[] { 1, 2 } };
            foreach (var i in at)
            {
                if (i >= args.Count) continue;
                var lm = Literal.Match(args[i]);
                if (!lm.Success) continue;
                var v = double.Parse(lm.Groups[1].Value, CultureInfo.InvariantCulture);
                if (v < Min)
                {
                    var line = 1 + text.Take(m.Index).Count(c => c == '\n');
                    var end = text.IndexOf('\n', m.Index);
                    yield return (line, v, text[m.Index..(end < 0 ? text.Length : end)].Trim());
                }
                break;
            }
        }
    }

    /// <summary>从左括号之后取顶层参数（跳过嵌套括号与字符串），到配对的右括号为止。</summary>
    private static List<string> TopLevelArgs(string s, int start)
    {
        var args = new List<string>();
        int depth = 0, from = start;
        for (int i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' || c == '\'')
            {
                for (i++; i < s.Length && s[i] != c; i++) if (s[i] == '\\') i++;
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    args.Add(s[from..i].Trim());
                    return args;
                }
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                args.Add(s[from..i].Trim());
                from = i + 1;
            }
        }
        return args;
    }

    [Fact]
    public void NoFontSizeBelow13_OutsideLegacyAllowList()
    {
        var bad = new List<string>();
        int scanned = 0;
        foreach (var (rel, full) in AppSource.Sources())
        {
            if (LegacyAllowList.Contains(rel)) continue;
            scanned++;
            foreach (var (line, size, t) in TooSmall(File.ReadAllText(full), rel.EndsWith(".axaml", StringComparison.Ordinal)))
                bad.Add($"{rel}:{line} 字号 {size.ToString(CultureInfo.InvariantCulture)} < 13：{(t.Length > 120 ? t[..120] + "…" : t)}");
        }
        Assert.True(scanned > 50, $"只扫到 {scanned} 个文件（目录 {AppSource.Dir} 不对？）");
        Assert.True(bad.Count == 0, "字号小于 13 px（任何文字 ≥ 13，中文 ≥ 14；请用 Fs* 令牌）：\n" + string.Join("\n", bad));
    }

    /// <summary>字号资源（新令牌 Fs* 与旧令牌 Fs6–Fs20）的值：引用小于 13 的旧字号资源同样算违规。</summary>
    private static Dictionary<string, double> FontSizeResources()
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var f in new[] { "Styles/Tokens.axaml", "Styles/LegacyTokens.axaml" })
            foreach (Match m in new Regex(@"<x:Double\s+x:Key=""(Fs\w*)"">\s*" + Num + @"\s*</x:Double>").Matches(AppSource.Read(f)))
                d[m.Groups[1].Value] = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return d;
    }

    [Fact]
    public void NoSmallFontSizeResourceReferences_OutsideLegacyAllowList()
    {
        var res = FontSizeResources();
        var re = new Regex(@"(?:FontSize=""|Property=""(?:\w+\.)?FontSize""\s+Value="")\{(?:StaticResource|DynamicResource)\s+(\w+)\}");
        var bad = new List<string>();
        foreach (var (rel, full) in AppSource.Sources().Where(f => f.Rel.EndsWith(".axaml", StringComparison.Ordinal)))
        {
            if (LegacyAllowList.Contains(rel)) continue;
            var lines = File.ReadAllLines(full);
            for (int i = 0; i < lines.Length; i++)
                foreach (Match m in re.Matches(lines[i]))
                    if (res.TryGetValue(m.Groups[1].Value, out var v) && v < Min)
                        bad.Add($"{rel}:{i + 1} 引用 {m.Groups[1].Value} = {v.ToString(CultureInfo.InvariantCulture)}");
        }
        Assert.True(bad.Count == 0, "引用了小于 13 px 的字号资源（旧令牌）：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void AllowListEntries_Exist()
    {
        foreach (var rel in LegacyAllowList)
            Assert.True(File.Exists(Path.Combine(AppSource.Dir, rel)), $"允许名单里的 {rel} 不存在了：从名单里删掉");
    }

    /// <summary>名单只会变短：名单里的文件已经没有小字号（字面量或引用 Fs6–Fs11）了，就必须从名单里删掉。</summary>
    [Fact]
    public void AllowListEntries_StillNeeded()
    {
        var res = FontSizeResources();
        var refRe = new Regex(@"\{(?:StaticResource|DynamicResource)\s+(Fs\w*)\}");
        var stale = LegacyAllowList.Where(rel =>
        {
            if (!File.Exists(Path.Combine(AppSource.Dir, rel))) return false;
            var text = AppSource.Read(rel);
            bool axaml = rel.EndsWith(".axaml", StringComparison.Ordinal);
            bool smallRef = axaml && refRe.Matches(text).Any(m => res.TryGetValue(m.Groups[1].Value, out var v) && v < Min);
            return !TooSmall(text, axaml).Any() && !smallRef;
        }).ToList();
        Assert.True(stale.Count == 0, "这些文件已经没有小于 13 px 的字号了，从允许名单里删掉：" + string.Join(", ", stale));
    }

    [Fact]
    public void FontSizeTokens_AreTheBriefScale()
    {
        var tokens = new Regex(@"<x:Double\s+x:Key=""(Fs\w*)"">\s*" + Num + @"\s*</x:Double>").Matches(AppSource.Read("Styles/Tokens.axaml"))
            .ToDictionary(m => m.Groups[1].Value, m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        var expected = new Dictionary<string, double>
        {
            ["FsNum"] = 13, ["Fs2"] = 14, ["FsBody"] = 15, ["FsTitle"] = 16, ["FsHead"] = 18,
            ["FsReadS"] = 20, ["FsRead24"] = 24, ["FsRead"] = 26, ["FsRead28"] = 28, ["FsRead30"] = 30,
        };
        Assert.Equal(expected.OrderBy(k => k.Key), tokens.OrderBy(k => k.Key));
    }

    [Theory]
    [InlineData("<TextBlock FontSize=\"9\" />", true, true)]
    [InlineData("<Setter Property=\"FontSize\" Value=\"8\" />", true, true)]
    [InlineData("<Setter Property=\"TextBlock.FontSize\" Value=\"12.5\" />", true, true)]
    [InlineData("<x:Double x:Key=\"FsTiny\">11</x:Double>", true, true)]
    [InlineData("<TextBlock FontSize=\"{StaticResource FsBody}\" />", true, false)]
    [InlineData("<TextBlock FontSize=\"13\" />", true, false)]
    [InlineData("new TextBlock { FontSize = 11, Text = \"x\" }", false, true)]
    [InlineData("tb.SetValue(TextBlock.FontSizeProperty, 12d);", false, true)]
    [InlineData("[\"ControlContentThemeFontSize\"] = 10d,", false, true)]
    [InlineData("new TextBlock { FontSize = 15 }", false, false)]
    [InlineData("FontSize = size ?? 15", false, false)]
    [InlineData("var px = run.Properties.FontRenderingEmSize * scale;", false, false)]
    [InlineData("var ft = new FormattedText(\"12\", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 9, brush);", false, true)]
    [InlineData("using var l = new TextLayout(text, face, 10, null);", false, true)]
    [InlineData("using var l = new TextLayout(text, face, features, 11.5, null);", false, true)]
    [InlineData("var p = new GenericTextRunProperties(face, 12);", false, true)]
    [InlineData("var ft = new FormattedText(s, c, f, face, emSize: 9, brush);", false, true)]
    [InlineData("var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 13, brush);", false, false)]
    [InlineData("using var l = new TextLayout(Fmt(v, 2), face, size, null);", false, false)]
    [InlineData("using var l = new TextLayout(text, new Typeface(f, FontStyle.Normal, FontWeight.Bold), FontFeatures, 15, null);", false, false)]
    public void Rules_FlagWhatTheyShould(string line, bool axaml, bool flagged) => Assert.Equal(flagged, TooSmall(line, axaml).Any());

    [Fact]
    public void DrawnText_MultiLineCallsAreChecked()
    {
        var src = "var ft = new FormattedText(\n    label,\n    CultureInfo.InvariantCulture,\n    FlowDirection.LeftToRight,\n    new Typeface(f),\n    10,\n    brush);";
        var hit = Assert.Single(TooSmallDrawn(src));
        Assert.Equal(1, hit.Line);
        Assert.Equal(10, hit.Size);
    }
}
