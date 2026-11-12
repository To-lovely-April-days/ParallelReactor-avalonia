#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 字体探针（PORT-PLAN §4.3 / M1）：PR_CONTROL_DEMO=1 + PR_CONTROL_FONT_PROBE=1（无头）。
/// 用每个候选字体栈排「1号机 · RV1 反应中 119.8 ℃ 0.349 MPa 0123456789」，记录每段文字实际落到的字体、0–9 的前进宽度（有无 +tnum），
/// 写 &lt;PR_CONTROL_SHOT&gt;/font-probe.txt，并截样张 font-probe-&lt;w&gt;.png。Windows 实机上同样能跑（M8 核对 Segoe UI / 雅黑）。
/// </summary>
public static class FontProbe
{
    public const string Sample = "1号机 · RV1 反应中 119.8 ℃ 0.349 MPa 0123456789";

    /// <summary>候选栈：当前系统的选择（UiFonts）+ macOS / Windows 候选 + 旧界面的栈。</summary>
    public static IReadOnlyList<string> Stacks() => new[]
    {
        UiFonts.Stack(),
        UiFonts.MacSystemStack,
        "Helvetica Neue, PingFang SC, fonts:Inter#Inter",
        "SF Pro Text, PingFang SC, fonts:Inter#Inter",
        "SF Pro, PingFang SC, fonts:Inter#Inter",
        ".SF NS, PingFang SC, fonts:Inter#Inter",
        "PingFang SC, fonts:Inter#Inter",
        "fonts:Inter#Inter, PingFang SC",
        UiFonts.WindowsStack,
        "Segoe UI, Arial, PingFang SC, Microsoft YaHei, Noto Sans CJK SC, Inter",
    }.Distinct().ToArray();

    private static readonly FontFeatureCollection Tnum = new() { FontFeature.Parse("+tnum") };

    public static string Report()
    {
        var sb = new StringBuilder();
        var fm = FontManager.Current;
        sb.AppendLine($"# font probe · {Environment.OSVersion} · Avalonia {typeof(FontManager).Assembly.GetName().Version}");
        sb.AppendLine($"default family: {fm.DefaultFontFamily.Name}");
        sb.AppendLine($"UiFonts.Stack(): {UiFonts.Stack()}");
        var sys = fm.SystemFonts.Select(f => f.Name).ToList();
        sb.AppendLine($"system families: {sys.Count}");
        foreach (var key in new[] { "SF", "Helvetica", "PingFang", "Segoe", "YaHei", "Inter", "Arial", "AppleSystem" })
            sb.AppendLine($"  ~{key}: " + string.Join(" | ", sys.Where(n => n.Contains(key, StringComparison.OrdinalIgnoreCase)).Take(12)));

        // 单个族名能否解析（不回退）
        sb.AppendLine();
        sb.AppendLine("## single family resolution (FontManager.TryGetGlyphTypeface)");
        foreach (var name in new[] { ".AppleSystemUIFont", "Helvetica Neue", "SF Pro Text", "SF Pro", ".SF NS", ".SF NS Text", "PingFang SC", "Segoe UI", "Microsoft YaHei UI", "Microsoft YaHei", "Arial", "fonts:Inter#Inter", "Inter" })
        {
            var ok = fm.TryGetGlyphTypeface(new Typeface(new FontFamily(name)), out var gt);
            sb.AppendLine($"  {name,-22} → {(ok ? gt!.FamilyName + " w=" + gt.Weight : "(not found)")}");
        }

        // 显式行高时基线放在哪里（CSS 是上下各加一半行距）
        sb.AppendLine();
        sb.AppendLine("## line height (UiFont, 15 px / 26 px)：TextLine.Height / Baseline");
        foreach (var (size, lh) in new[] { (15.0, double.NaN), (15.0, 22.0), (15.0, 30.0), (26.0, double.NaN), (26.0, 37.7) })
        {
            using var l = new TextLayout(Sample, new Typeface(UiFonts.Create()), null, size, Palette.T1Brush, lineHeight: lh);
            var line = l.TextLines[0];
            sb.AppendLine($"  size={size} lineHeight={(double.IsNaN(lh) ? "NaN" : lh.ToString(CultureInfo.InvariantCulture))} → height={line.Height:0.00} baseline={line.Baseline:0.00} extent={line.Extent:0.00}");
        }

        foreach (var stack in Stacks())
        {
            sb.AppendLine();
            sb.AppendLine($"## stack: {stack}");
            foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold })
            {
                var tf = new Typeface(new FontFamily(stack), FontStyle.Normal, weight);
                foreach (var feat in new[] { (FontFeatureCollection?)null, Tnum })
                {
                    using var layout = new TextLayout(Sample, tf, feat, 15, Palette.T1Brush);
                    var runs = layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>().ToList();
                    var tag = $"{(weight == FontWeight.Normal ? "400" : "600")}{(feat == null ? "" : " +tnum")}";
                    sb.AppendLine($"  [{tag}] width={layout.WidthIncludingTrailingWhitespace:0.00} height={layout.Height:0.00}");
                    foreach (var r in runs)
                    {
                        var gt = r.ShapedBuffer.GlyphTypeface;
                        sb.AppendLine($"    run \"{r.Text}\" → {gt.FamilyName} (w={gt.Weight}, sim={gt.FontSimulations}) requested={r.Properties.Typeface.FontFamily.Name}");
                    }
                    // 0–9 的前进宽度：取 Sample 尾部「0123456789」的字形
                    var adv = DigitAdvances(runs, Sample.LastIndexOf("0123456789", StringComparison.Ordinal));
                    if (adv.Count == 10)
                        sb.AppendLine($"    digits 0-9 adv: {string.Join(" ", adv.Select(a => a.ToString("0.00", CultureInfo.InvariantCulture)))} → {(adv.Max() - adv.Min() < 0.01 ? "TABULAR" : "proportional (Δ" + (adv.Max() - adv.Min()).ToString("0.00", CultureInfo.InvariantCulture) + ")")}");
                    else sb.AppendLine($"    digits: {adv.Count} glyph advances found");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>从字形缓冲里取出文本位置 start 起 10 个字符（0–9）的前进宽度。</summary>
    private static List<double> DigitAdvances(List<ShapedTextRun> runs, int start)
    {
        var res = new List<double>();
        int offset = 0;
        foreach (var r in runs)
        {
            var buf = r.ShapedBuffer;
            for (int i = 0; i < buf.Length; i++)
            {
                var g = buf[i];
                int cluster = g.GlyphCluster;   // 字形簇是整段文本里的字符位置
                if (cluster >= start && cluster < start + 10) res.Add(g.GlyphAdvance);
            }
            offset += r.Length;
        }
        return res;
    }

    /// <summary>样张：每个栈一行，15 px 400 / 15 px 600 / 26 px 600 +tnum，另加三行等宽数字对齐检查。</summary>
    public static Avalonia.Controls.Control Sheet()
    {
        var root = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        foreach (var stack in Stacks())
        {
            var ff = new FontFamily(stack);
            var box = new StackPanel { Spacing = 2 };
            box.Children.Add(new TextBlock { Text = stack, FontFamily = new FontFamily("fonts:Inter#Inter"), FontSize = 13, Foreground = Palette.T3Brush });
            box.Children.Add(new TextBlock { Text = Sample, FontFamily = ff, FontSize = 15, Foreground = Palette.T1Brush });
            box.Children.Add(new TextBlock { Text = Sample, FontFamily = ff, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Palette.T1Brush });
            box.Children.Add(new TextBlock { Text = Sample, FontFamily = ff, FontSize = 26, FontWeight = FontWeight.SemiBold, FontFeatures = Tnum, Foreground = Palette.T1Brush });
            var digits = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
            foreach (var feat in new[] { (FontFeatureCollection?)null, Tnum })
            {
                var col = new StackPanel();
                foreach (var s in new[] { "119.8 ℃", "111.1 ℃", "0.349 MPa", "888.8 ℃" })
                    col.Children.Add(new TextBlock { Text = s, FontFamily = ff, FontSize = 20, FontWeight = FontWeight.SemiBold, FontFeatures = feat, Foreground = Palette.T2Brush });
                digits.Children.Add(col);
            }
            box.Children.Add(digits);
            root.Children.Add(new Border { Child = box, BorderBrush = Palette.Line2Brush, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 10) });
        }
        return new ScrollViewer { Content = root, Background = Palette.BgCardBrush };
    }

    public static void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "font-probe.txt"), Report());
    }
}
#endif
