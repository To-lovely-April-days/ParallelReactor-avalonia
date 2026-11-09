#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using ParallelReactor.Control.Behaviors;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 截图后的运行时审计（PORT-PLAN §4.7 第 4 条；对应 MOCK core.js 的 PR.debug.audit / overlaps / palette）。遍历窗口的可视树，报告：
///  · [audit] 文字 &lt; 13 px、含中文（含「」、。等中文标点）&lt; 14 px（字号 × 外框缩放，取每段文字的实际字号）；输入框里的文字同样查（control font）；
///  · [audit] missing glyph：画成方块（.notdef）的字（字体缺字，或连接符引起的回退缺陷，见 Theme/UiText）；
///  · [audit] font weight：某段文字实际用的字体字重与要求的差一档以上；或几段中文要求了常规 + 粗体、画出来只有一种
///    （Avalonia 11.1 回退沿用上一段字重的缺陷，见 Theme/UiFonts、Behaviors/CjkRuns.cs）；
///  · [audit] 单行文字被省略（TextLine.HasCollapsed）但自身或 4 层内祖先没有 ToolTip；状态字（类 stword）被省略或溢出一律报；
///    读数（类 num 或 FitText 里的文字）被省略一律报（number truncated：「11…」看起来像另一个数，带提示也不行）；
///  · [audit] lines dropped：多行文字没排完又没有省略号（MaxLines、或框的高度不够，后面的行悄悄丢了）；clipped vertically：文字比自己的框高；
///  · [audit] 不省略的单行文字比自己的框宽（溢出，会压到旁边）；输入框的文字比输入框宽（input text clipped）；
///  · [audit] text drawn outside its box：行的对齐偏移（居中 / 右对齐）加行宽超出自己的框 —— TextLayout 是按另一个宽度排的，整段偏出去被裁掉；
///  · [audit] clipped：文字被 ClipToBounds 的祖先横向 / 纵向裁掉（同 MOCK 的 clipped；滚动区里的不算）；
///  · [audit] outside：文字横向超出所在的框（最近的有底色 / 边框的 Border 或有底色的 Panel）—— 布局没收住、压到框外面；
///    BarFitPanel 有意藏起来的剩余时间不算（同 MOCK 的 .pr-fitopt）；
///  · [audit] step bar squeezed：步骤条窄于每段 6 px 的最小宽度，或被祖先裁掉一截（同 MOCK）；
///  · [audit] reason on plain ToggleButton：普通 ToggleButton / CheckBox 带 Pr.Reason（UI 自动化 / HotKey 仍能翻动它，见 Behaviors/Reason.cs）；
///  · [overlap] 同层元素重叠：文字 × 文字、文字 × 控件、控件 × 控件（嵌套的不算；上下相碰、重叠高度 &lt; 较小者 30 % 的不算），
///    文字 × 图形（IconView：任何超过 2 × 2 px 的重叠都算，同 MOCK 的 figure）；
///  · [palette] Background / Foreground（含 Run 自己的前景 / 底色）/ BorderBrush / BoxShadow / Fill / Stroke 的颜色与最近的令牌任一通道差 &gt; 6
///    （忽略透明度；阴影色只许在阴影里）；
///  · 自绘文字的控件（<see cref="IAuditText"/>）报告的文字：字号、颜色、重叠同上（drawn text）；
///  · [audit] 横向溢出 / 横向滚动条（ScrollViewer 的 Extent.Width &gt; Viewport.Width，或横向 ScrollBar 可见；输入框自己的滚动区按 input text clipped 报）。
/// 约定：类 audit-skip 的子树不查（同 data-audit-skip）；类 audit-ctl 的元素按「控件」参与重叠检查；类 audit-hscroll-ok 的 ScrollViewer 允许横向滚动；
/// 类 audit-layer 的子树是浮在上面的一层（窄屏的浮层资源管理器）：重叠只在同一层里比（浮层盖住下面是设计如此，同 MOCK 不同层叠上下文不比）。
/// 干净时返回空列表（&lt;name&gt;-&lt;w&gt;.audit.txt 为空文件）。
/// </summary>
public static class ShotAudit
{
    /// <summary>任何文字 ≥ 13 px，中文 ≥ 14 px（RESTYLE-BRIEF §3）。</summary>
    public const double MinText = 13, MinCjk = 14;

    /// <summary>MOCK core.js 的 CJK 正则 [㐀-鿿！-～]，再加中文标点 U+3000–303F（「」『』、。〔〕…）、假名、兼容表意文字。</summary>
    private static readonly Regex Cjk = new("[　-ヿ㐀-鿿豈-﫿！-～]", RegexOptions.Compiled);

    /// <summary>上一次 Run 查了多少东西（写进 shot.log，证明审计确实跑过）。</summary>
    public static string LastStats { get; private set; } = "";

    private sealed class Ctx
    {
        public required Visual Root;
        public int Visuals, Colors;
        public readonly List<string> Issues = new();
        public readonly List<(Visual El, Rect Rc, string Text)> Texts = new();
        public readonly List<(Visual El, Rect Rc)> Controls = new();
        /// <summary>参与控件 × 控件比较的「叶子」控件（按钮、输入、徽标、进度条…；不含面板类容器）。</summary>
        public readonly List<(Visual El, Rect Rc)> Leaves = new();
        /// <summary>图形（IconView）。</summary>
        public readonly List<(Visual El, Rect Rc)> Figures = new();
        /// <summary>色板外的颜色（键 = 「#rrggbb」）。</summary>
        public readonly Dictionary<string, (int N, string Where, string Prop, string Near, int D)> Bad = new();
    }

    public static IReadOnlyList<string> Run(Visual root)
    {
        var ctx = new Ctx { Root = root };
        Walk(ctx, root, 1.0);
        Overlaps(ctx);
        foreach (var kv in ctx.Bad.OrderByDescending(k => k.Value.N))
            ctx.Issues.Add($"[palette] {kv.Key} ({kv.Value.N}×) on {kv.Value.Where} · {kv.Value.Prop}（最近 {kv.Value.Near} Δ{kv.Value.D}）");
        LastStats = $"visuals={ctx.Visuals} text-lines={ctx.Texts.Count} controls={ctx.Controls.Count} leaves={ctx.Leaves.Count} figures={ctx.Figures.Count} colours={ctx.Colors}";
        return ctx.Issues;
    }

    // ---------------- 遍历 ----------------

    private static void Walk(Ctx ctx, Visual v, double opacity)
    {
        if (!v.IsVisible) return;
        opacity *= v.Opacity;
        if (opacity < 0.01) return;
        if (v is StyledElement se && se.Classes.Contains("audit-skip")) return;
        ctx.Visuals++;

        CheckColors(ctx, v);
        if (v is TextBlock tb) CheckText(ctx, tb);
        else if (v is TextPresenter tp) CheckPresenter(ctx, tp);
        if (v is IAuditText drawn) CheckDrawn(ctx, v, drawn);
        if (v.Bounds.Width > 0.5 && v.Bounds.Height > 0.5)
        {
            if (IsControl(v) && ToRoot(ctx, v, new Rect(v.Bounds.Size)) is { } rc)
            {
                ctx.Controls.Add((v, rc));
                if (IsLeaf(v)) ctx.Leaves.Add((v, rc));
            }
            if (v is IconView && ToRoot(ctx, v, new Rect(v.Bounds.Size)) is { } fr) ctx.Figures.Add((v, fr));
        }
        if (v is StepBar sb) CheckStepBar(ctx, sb);
        if (v is ToggleButton tg && tg is not IReasonGuarded && Pr.IsLocked(tg))
            ctx.Issues.Add($"[audit] reason on plain ToggleButton (UI 自动化 / HotKey 仍能翻动它，改用 pr:PrToggle / pr:PrCheckBox / pr:Chip): {Describe(tg)}");
        if (v is ScrollViewer sv) CheckScroll(ctx, sv);
        if (v is ScrollBar { Orientation: Orientation.Horizontal } bar && bar.IsEffectivelyVisible && bar.Bounds.Width > 0 && !HScrollOk(bar) && !InTextBox(bar))
            ctx.Issues.Add($"[audit] horizontal scrollbar visible: {Describe(bar)}");

        foreach (var child in v.GetVisualChildren()) Walk(ctx, child, opacity);
    }

    /// <summary>参与重叠检查的「控件」：按钮类、输入类、进度条、Pr 控件、标了 audit-ctl 的元素。</summary>
    private static bool IsControl(Visual v) =>
        v is Button or TextBox or ComboBox or Slider or ProgressBar or NumericUpDown
        || (v is StyledElement s && s.Classes.Contains("audit-ctl"))
        || (v.GetType().Namespace ?? "").StartsWith("ParallelReactor.Control.Controls.Pr", StringComparison.Ordinal);

    /// <summary>控件 × 控件比较只用「叶子」控件（同 MOCK 的 .pr-btn / .pr-badge / input / .pr-stepbar …）：面板、行、卡片这些容器不算。</summary>
    private static bool IsLeaf(Visual v) =>
        v is Button or TextBox or ComboBox or Slider or ProgressBar or NumericUpDown
            or Badge or StateBadge or DevTag or NumBox or ProgressPr or StepBar or KeyDot or Ring
        || (v is StyledElement s && s.Classes.Contains("audit-ctl"));

    private static bool HScrollOk(Visual v) => v.GetSelfAndVisualAncestors().OfType<StyledElement>().Any(a => a.Classes.Contains("audit-hscroll-ok"));

    /// <summary>输入框自己模板里的滚动区（文字比框宽按 input text clipped 报，不按横向滚动报）。</summary>
    private static bool InTextBox(Visual v) => v is StyledElement s && s.TemplatedParent is TextBox
        || v.GetVisualAncestors().OfType<StyledElement>().Take(4).Any(a => a.TemplatedParent is TextBox && a is ScrollViewer);

    // ---------------- 文字 ----------------

    private static string TextOf(TextBlock tb) => (tb.Text ?? tb.Inlines?.Text ?? "").Replace('\n', ' ').Trim();

    private static string Snip(string s, int n = 40) => s.Length <= n ? s : s[..n] + "…";

    private static double ScaleOf(Ctx ctx, Visual v)
    {
        var m = v.TransformToVisual(ctx.Root);
        return m == null ? 1 : Math.Sqrt(Math.Abs(m.Value.M11 * m.Value.M22 - m.Value.M12 * m.Value.M21));
    }

    /// <summary>字号下限（每个元素只报第一条）：px 已乘外框缩放。</summary>
    private static bool CheckSize(Ctx ctx, double px, string text, string what, Visual v, string all)
    {
        if (text.Trim().Length == 0) return false;
        if (px < MinText - 0.05) { ctx.Issues.Add($"[audit] {what}font {Px(px)}px < 13px: {Describe(v)} \"{Snip(all)}\""); return true; }
        if (px < MinCjk - 0.05 && Cjk.IsMatch(text)) { ctx.Issues.Add($"[audit] CJK {what}font {Px(px)}px < 14px: {Describe(v)} \"{Snip(all)}\""); return true; }
        return false;
    }

    private static void CheckText(Ctx ctx, TextBlock tb)
    {
        var text = TextOf(tb);
        if (text.Length == 0 || tb.Bounds.Width <= 0 || tb.Bounds.Height <= 0) return;
        if (tb.TransformToVisual(ctx.Root) == null) return;
        var scale = ScaleOf(ctx, tb);
        TextLayout layout;
        try { layout = tb.TextLayout; } catch { return; }

        // 字号与字重：逐段取实际字号 / 实际字体（Inline 可以有自己的字号、字重），每个元素各只报第一条
        bool sizeReported = false, weightReported = false, glyphReported = false;
        foreach (var run in layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>())
        {
            var t = run.Text.ToString();
            if (!sizeReported) sizeReported = CheckSize(ctx, run.Properties.FontRenderingEmSize * scale, t, "", tb, text);
            if (!glyphReported) glyphReported = CheckGlyphs(ctx, run, tb, text);
            if (!weightReported && t.Trim().Length > 0 && WeightMismatch(run) is { } wm)
            {
                ctx.Issues.Add($"[audit] font weight {wm}: {Describe(tb)} \"{Snip(t.Trim(), 20)}\" in \"{Snip(text, 30)}\"");
                weightReported = true;
            }
        }
        // Run 自己的前景 / 底色（Inline 可以单独设颜色）
        if (tb.Inlines is { Count: > 0 } inlines)
        {
            foreach (var inline in Flatten(inlines))
            {
                Check(ctx, tb, inline.Foreground, "Run.Foreground");
                Check(ctx, tb, inline.Background, "Run.Background");
            }
            // 字重回退缺陷：几段中文要求了不同字重（常规 / 粗体），画出来却只有一种 —— 后一段沿用了前一段回退到的字体（连字重一起），
            // 排出来的段连「要求的字重」都被改成了前一段的，所以只能拿 Run 自己的字重来比
            if (!weightReported)
            {
                var want = Flatten(inlines).OfType<Run>().Where(r => !string.IsNullOrEmpty(r.Text) && Cjk.IsMatch(r.Text!))
                    .Select(r => Bold((int)r.FontWeight)).Distinct().ToList();
                if (want.Count > 1)
                {
                    var got = layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>().Where(r => Cjk.IsMatch(r.Text.ToString()))
                        .Select(r => Bold(Actual(r))).Distinct().ToList();
                    if (got.Count < want.Count)
                        ctx.Issues.Add($"[audit] font weight 中文段要求常规 + 粗体，画出来只有{(got.FirstOrDefault() ? "粗体" : "常规")}（11.1 回退缺陷：粗体段和它后面的一段用 UiFontCjk，见 Pr.CjkRuns）: {Describe(tb)} \"{Snip(text, 30)}\"");
                }
            }
        }

        bool stword = tb.Classes.Contains("stword");
        bool number = tb.Classes.Contains("num") || IsInFitText(tb);
        bool collapsed = layout.TextLines.Any(l => l.HasCollapsed);
        var avail = tb.Bounds.Width - tb.Padding.Left - tb.Padding.Right;
        var availH = tb.Bounds.Height - tb.Padding.Top - tb.Padding.Bottom;
        var widest = layout.TextLines.Count == 0 ? 0 : layout.TextLines.Max(l => l.Width);
        bool overflow = !collapsed && widest > avail + 1;
        // 画到框外：行的起点（居中 / 右对齐的偏移）加行宽超出了框 —— TextLayout 是按另一个宽度排的（居中的自动宽度文字，容器变宽以后
        // 测量宽度 ≠ 排版宽度，见 pr:Empty / PlaceholderPage），文字整段偏出去、被自己的框裁掉
        var firstStart = layout.TextLines.Count == 0 ? 0 : layout.TextLines.Min(l => l.Start);
        var lastRight = layout.TextLines.Count == 0 ? 0 : layout.TextLines.Max(l => l.Start + l.Width);
        bool shifted = !collapsed && !overflow && (lastRight > avail + 1 || firstStart < -1);
        int dropped = collapsed ? 0 : DroppedChars(tb, layout);
        if (stword && (collapsed || overflow))
            ctx.Issues.Add($"[audit] state word truncated {Px(collapsed ? FullWidth(tb, text) : widest)}>{Px(avail)}px: {Describe(tb)} \"{Snip(text, 20)}\"");
        else if (number && collapsed)
            ctx.Issues.Add($"[audit] number truncated (读数被截断，带提示也不行): {Describe(tb)} \"{Snip(text, 20)}\"");
        else if (collapsed && !HasTip(tb))
            ctx.Issues.Add($"[audit] trimmed without tooltip: {Describe(tb)} \"{Snip(text, 50)}\"");
        else if (overflow)
            ctx.Issues.Add($"[audit] overflow {Px(widest)}>{Px(avail)}px: {Describe(tb)} \"{Snip(text, 50)}\"");
        else if (shifted)
            ctx.Issues.Add($"[audit] text drawn outside its box（行从 {Px(firstStart)} 到 {Px(lastRight)} px，框只有 {Px(avail)} px：TextLayout 按别的宽度排，居中 / 右对齐偏到框外）: {Describe(tb)} \"{Snip(text, 40)}\"");
        else if (dropped > 0)
            ctx.Issues.Add($"[audit] lines dropped ({dropped} 字没排出来，MaxLines={tb.MaxLines} 或框太矮、又没有省略号): {Describe(tb)} \"{Snip(text, 40)}\"");
        else if (!collapsed && layout.Height > availH + 1.5)
            ctx.Issues.Add($"[audit] clipped vertically {Px(layout.Height - availH)}px by own box: {Describe(tb)} \"{Snip(text, 40)}\"");
        else CheckContainment(ctx, tb, layout, text);

        // 重叠检查用的文字框：每行文字实际占的矩形（对齐偏移 Start、不含行尾空白）
        double y = tb.Padding.Top;
        foreach (var line in layout.TextLines)
        {
            if (line.Width > 0.5 && ToRoot(ctx, tb, new Rect(tb.Padding.Left + line.Start, y, line.Width, line.Height)) is { } rc)
                ctx.Texts.Add((tb, rc, text));
            y += line.Height;
        }
    }

    private static IEnumerable<Inline> Flatten(IEnumerable<Inline> inlines)
    {
        foreach (var i in inlines)
        {
            yield return i;
            if (i is Span span)
                foreach (var c in Flatten(span.Inlines)) yield return c;
        }
    }

    private static bool IsInFitText(Visual v)
    {
        int i = 0;
        for (var a = v.GetVisualParent(); a != null && i < 3; a = a.GetVisualParent(), i++)
            if (a is FitText) return true;
        return false;
    }

    /// <summary>
    /// 没排出来的字数：最后一行的结束位置 &lt; 文字长度（不算末尾空白）= 后面的行被 MaxLines 或框的高度悄悄丢掉了（没有省略号）。
    /// 带 InlineUIContainer 的不查（文字长度对不上）。
    /// </summary>
    private static int DroppedChars(TextBlock tb, TextLayout layout)
    {
        if (layout.TextLines.Count == 0) return 0;
        string? src;
        if (tb.Inlines is { Count: > 0 } inl)
        {
            if (Flatten(inl).Any(i => i is InlineUIContainer)) return 0;
            src = inl.Text;
        }
        else src = tb.Text;
        if (string.IsNullOrEmpty(src)) return 0;
        var len = src.TrimEnd().Length;
        var last = layout.TextLines[^1];
        var covered = last.FirstTextSourceIndex + last.Length;
        return Math.Max(0, len - covered);
    }

    /// <summary>
    /// 画成方块的字：字形 0（.notdef）且有宽度（不可见的零宽控制字符缺字形时宽度为 0，不算）。
    /// 字体缺字、或 Avalonia 11.1 的回退缺陷（连接符后面的中文留在西文字体里，见 Theme/UiText）都会这样。
    /// </summary>
    private static bool CheckGlyphs(Ctx ctx, ShapedTextRun run, Visual v, string all)
    {
        if (!run.ShapedBuffer.Any(g => g.GlyphIndex == 0 && g.GlyphAdvance > 0.5)) return false;
        ctx.Issues.Add($"[audit] missing glyph（画成方块，字体 {run.ShapedBuffer.GlyphTypeface.FamilyName}）: {Describe(v)} \"{Snip(all)}\"");
        return true;
    }

    /// <summary>实际字体的字重（合成加粗按粗体算）。</summary>
    private static int Actual(ShapedTextRun run)
    {
        var gt = run.ShapedBuffer.GlyphTypeface;
        var got = (int)gt.Weight;
        if ((gt.FontSimulations & FontSimulations.Bold) != 0) got = Math.Max(got, (int)FontWeight.Bold);
        return got;
    }

    /// <summary>粗体档（≥ 600；Medium 500 算常规档）。</summary>
    private static bool Bold(int weight) => weight >= 600;

    /// <summary>这一段要求的字重与实际字体的字重差一档以上（字体缺这个字重、又没合成）：返回说明，没问题返回 null。</summary>
    private static string? WeightMismatch(ShapedTextRun run)
    {
        var want = (int)run.Properties.Typeface.Weight;
        var got = Actual(run);
        bool bad = (want >= 600 && got < 550) || (want <= 500 && got >= 600);
        return bad ? $"{want}→{got} ({run.ShapedBuffer.GlyphTypeface.FamilyName})" : null;
    }

    /// <summary>输入框里的文字（TextPresenter）：字号下限、文字比输入框宽。占位提示是模板里的 TextBlock，按普通文字查。</summary>
    private static void CheckPresenter(Ctx ctx, TextPresenter tp)
    {
        var text = tp.Text ?? "";
        if (text.Trim().Length == 0 || tp.Bounds.Width <= 0 || tp.TransformToVisual(ctx.Root) == null) return;
        CheckSize(ctx, tp.FontSize * ScaleOf(ctx, tp), text, "control ", tp, text);
        TextLayout layout;
        try { layout = tp.TextLayout; } catch { return; }
        foreach (var run in layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>())
            if (CheckGlyphs(ctx, run, tp, text)) break;
        if (tp.TextWrapping != TextWrapping.NoWrap) return;
        var sv = tp.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        var visible = sv != null && sv.Viewport.Width > 0 ? sv.Viewport.Width : tp.Bounds.Width;
        var w = layout.WidthIncludingTrailingWhitespace;
        if (w > visible + 1)
            ctx.Issues.Add($"[audit] input text clipped {Px(w)}>{Px(visible)}px: {Describe(tp)} \"{Snip(text, 30)}\"");
    }

    /// <summary>自绘文字（IAuditText）：字号、颜色，并加入重叠检查。</summary>
    private static void CheckDrawn(Ctx ctx, Visual v, IAuditText drawn)
    {
        var scale = ScaleOf(ctx, v);
        bool reported = false;
        foreach (var t in drawn.AuditTexts)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            if (!reported) reported = CheckSize(ctx, t.FontSize * scale, t.Text, "drawn text ", v, t.Text);
            Check(ctx, v, t.Foreground, "drawn text");
            if (t.Bounds.Width > 0.5 && ToRoot(ctx, v, t.Bounds) is { } rc) ctx.Texts.Add((v, rc, t.Text));
        }
    }

    /// <summary>步骤条：窄于每段 6 px 的最小宽度，或被祖先（滚动区以外）裁掉一截（同 MOCK 的 step bar squeezed）。</summary>
    private static void CheckStepBar(Ctx ctx, StepBar sb)
    {
        int n = sb.Segments.Count;
        if (n == 0 || sb.Bounds.Width <= 0) return;
        var min = sb.MinBarWidthSelf;
        var segW = n > 1 ? (sb.Bounds.Width - (n - 1) * StepBar.Gap) / n : sb.Bounds.Width;
        if (sb.Bounds.Width + 0.5 < min)
        {
            ctx.Issues.Add($"[audit] step bar squeezed {Px(sb.Bounds.Width)}px（{n} 段，最窄 {Px(Math.Max(0, segW))} px，至少 {Px(min)}）: {Describe(sb)}");
            return;
        }
        var m = sb.TransformToVisual(ctx.Root);
        if (m == null) return;
        var full = new Rect(sb.Bounds.Size).TransformToAABB(m.Value);
        var r = full;
        for (var a = sb.GetVisualParent(); a != null && a != ctx.Root; a = a.GetVisualParent())
        {
            if (a is ScrollContentPresenter or ScrollViewer) break;
            if (!a.ClipToBounds) continue;
            var am = a.TransformToVisual(ctx.Root);
            if (am != null) r = r.Intersect(new Rect(a.Bounds.Size).TransformToAABB(am.Value));
        }
        if (r.Width < full.Width - 1)
            ctx.Issues.Add($"[audit] step bar squeezed: 被裁掉 {Px(full.Width - Math.Max(0, r.Width))}px（{n} 段）: {Describe(sb)}");
    }

    /// <summary>
    /// 文字有没有被裁掉（ClipToBounds 的祖先，滚动区除外）或超出所在的框（最近的有底色 / 边框的 Border、有底色的 Panel）。
    /// 用所有行的并集（元素坐标 → 根坐标，不裁剪）；容差 1.5 px（同 MOCK）。BarFitPanel 藏起来的文字跳过。
    /// </summary>
    private static void CheckContainment(Ctx ctx, TextBlock tb, TextLayout layout, string text)
    {
        if (layout.TextLines.Count == 0) return;
        for (Visual? a = tb.GetVisualParent(), prev = tb; a != null; prev = a, a = a.GetVisualParent())
            if (a is BarFitPanel { IsBarFit: true } fit && fit.Children.Count > 1 && (fit.Children[1] == prev || fit.Children[1].IsVisualAncestorOf(tb)))
                return;
        var m = tb.TransformToVisual(ctx.Root);
        if (m == null) return;
        double y = tb.Padding.Top, l = double.MaxValue, r = double.MinValue;
        foreach (var line in layout.TextLines)
        {
            if (line.Width > 0.5) { l = Math.Min(l, tb.Padding.Left + line.Start); r = Math.Max(r, tb.Padding.Left + line.Start + line.Width); }
            y += line.Height;
        }
        if (l >= r) return;
        var box = new Rect(l, tb.Padding.Top, r - l, Math.Max(1, y - tb.Padding.Top)).TransformToAABB(m.Value);
        const double tol = 1.5;
        bool outsideChecked = false;
        for (var a = tb.GetVisualParent(); a != null && a != ctx.Root; a = a.GetVisualParent())
        {
            if (a is ScrollContentPresenter or ScrollViewer) return;   // 滚动区：滚出视口是正常的
            var am = a.TransformToVisual(ctx.Root);
            if (am == null) return;
            var ar = new Rect(a.Bounds.Size).TransformToAABB(am.Value);
            if (a.ClipToBounds)
            {
                var dx = Math.Max(box.Right - ar.Right, ar.Left - box.Left);
                var dy = Math.Max(box.Bottom - ar.Bottom, ar.Top - box.Top);
                if (dx > tol) { ctx.Issues.Add($"[audit] clipped {Px(dx)}px by {Describe(a)}: {Describe(tb)} \"{Snip(text, 40)}\""); return; }
                if (dy > tol) { ctx.Issues.Add($"[audit] clipped vertically {Px(dy)}px by {Describe(a)}: {Describe(tb)} \"{Snip(text, 40)}\""); return; }
            }
            if (!outsideChecked && IsSurface(a))
            {
                outsideChecked = true;
                var dx = Math.Max(box.Right - ar.Right, ar.Left - box.Left);
                if (dx > tol) { ctx.Issues.Add($"[audit] outside {Px(dx)}px of {Describe(a)}: {Describe(tb)} \"{Snip(text, 40)}\""); return; }
            }
        }
    }

    /// <summary>「框」：画了底色或边框的 Border，或有底色的 Panel（透明底不算）。</summary>
    private static bool IsSurface(Visual v) => v switch
    {
        Border b => Visible(b.Background) || (!IsZero(b.BorderThickness) && Visible(b.BorderBrush)),
        Panel p => Visible(p.Background),
        _ => false,
    };

    private static bool Visible(IBrush? b) => b switch
    {
        ISolidColorBrush s => s.Opacity > 0.004 && s.Color.A > 1,
        null => false,
        _ => true,
    };

    /// <summary>不省略时整段文字的宽度（报告用）。</summary>
    private static double FullWidth(TextBlock tb, string text)
    {
        using var l = new TextLayout(text, new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontFeatures, tb.FontSize, null);
        return l.Width;
    }

    private static bool HasTip(Visual v)
    {
        int i = 0;
        for (Visual? a = v; a != null && i < 5; a = a.GetVisualParent(), i++)
            if (a is Avalonia.Controls.Control c && ToolTip.GetTip(c) is { } tip && !(tip is string s && string.IsNullOrWhiteSpace(s))) return true;
        return false;
    }

    private static string Px(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    // ---------------- 重叠 ----------------

    /// <summary>元素内的矩形 → 根坐标，再裁到祖先的裁剪区（ScrollViewer 视口等）；被裁掉 40 % 以上高度的不参与比较（同 MOCK clipKeep）。</summary>
    private static Rect? ToRoot(Ctx ctx, Visual v, Rect local)
    {
        var m = v.TransformToVisual(ctx.Root);
        if (m == null) return null;
        var r = local.TransformToAABB(m.Value);
        var h0 = r.Height;
        for (var a = v.GetVisualParent(); a != null && a != ctx.Root; a = a.GetVisualParent())
        {
            // 裁剪：ClipToBounds 的框，或 Clip 几何（例如时间轴吸顶组头下面被裁掉的行，Views/Shell/StickyHeadPanel）
            if (!a.ClipToBounds && a.Clip == null) continue;
            var am = a.TransformToVisual(ctx.Root);
            if (am == null) continue;
            if (a.ClipToBounds) r = r.Intersect(new Rect(a.Bounds.Size).TransformToAABB(am.Value));
            if (a.Clip is { } clip) r = r.Intersect(clip.Bounds.TransformToAABB(am.Value));
        }
        if (r.Width <= 0 || r.Height <= 0 || (h0 > 0 && r.Height < h0 * 0.6)) return null;
        return r;
    }

    private static double Inter(Rect a, Rect b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (h < 0.3 * Math.Min(a.Height, b.Height)) return 0;   // 上下两行只是相碰
        return w > 1 && h > 1 ? w * h : 0;
    }

    /// <summary>图形的覆盖：任何超过 2 × 2 px 的重叠（同 MOCK figure × text）。</summary>
    private static double Cover(Rect a, Rect b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return w > 2 && h > 2 ? w * h : 0;
    }

    private static bool Nested(Visual a, Visual b) => a == b || a.IsVisualAncestorOf(b) || b.IsVisualAncestorOf(a);

    /// <summary>元素所在的浮层（最近的类 audit-layer 的祖先，含自己）；不在浮层里为 null。</summary>
    private static Visual? LayerOf(Dictionary<Visual, Visual?> cache, Visual v)
    {
        if (cache.TryGetValue(v, out var l)) return l;
        l = v.GetSelfAndVisualAncestors().OfType<StyledElement>().FirstOrDefault(a => a.Classes.Contains("audit-layer")) as Visual;
        cache[v] = l;
        return l;
    }

    private static void Overlaps(Ctx ctx)
    {
        var seen = new HashSet<(Visual, Visual)>();
        var layers = new Dictionary<Visual, Visual?>();
        bool Apart(Visual a, Visual b) => !ReferenceEquals(LayerOf(layers, a), LayerOf(layers, b));
        for (int i = 0; i < ctx.Texts.Count; i++)
        {
            var A = ctx.Texts[i];
            for (int j = i + 1; j < ctx.Texts.Count; j++)
            {
                var B = ctx.Texts[j];
                if (Nested(A.El, B.El) || Apart(A.El, B.El) || !seen.Add((A.El, B.El))) continue;
                if (Inter(A.Rc, B.Rc) > 3) ctx.Issues.Add($"[overlap] text × text: \"{Snip(A.Text, 18)}\" × \"{Snip(B.Text, 18)}\" ({Describe(A.El)})");
                else seen.Remove((A.El, B.El));
            }
            foreach (var B in ctx.Controls)
            {
                if (Nested(A.El, B.El) || Apart(A.El, B.El) || !seen.Add((A.El, B.El))) continue;
                if (Inter(A.Rc, B.Rc) > 3) ctx.Issues.Add($"[overlap] text × control: \"{Snip(A.Text, 18)}\" × {Describe(B.El)}");
                else seen.Remove((A.El, B.El));
            }
            foreach (var F in ctx.Figures)
            {
                if (Nested(A.El, F.El) || Apart(A.El, F.El) || !seen.Add((A.El, F.El))) continue;
                if (Cover(F.Rc.Deflate(1), A.Rc) > 0) ctx.Issues.Add($"[overlap] text × figure: \"{Snip(A.Text, 18)}\" × {Describe(F.El)}（{(F.El as IconView)?.Icon}）");
                else seen.Remove((A.El, F.El));
            }
        }
        for (int i = 0; i < ctx.Leaves.Count; i++)
            for (int j = i + 1; j < ctx.Leaves.Count; j++)
            {
                var A = ctx.Leaves[i];
                var B = ctx.Leaves[j];
                if (Nested(A.El, B.El) || Apart(A.El, B.El)) continue;
                if (Inter(A.Rc, B.Rc) > 3) ctx.Issues.Add($"[overlap] control × control: {Describe(A.El)} × {Describe(B.El)}");
            }
    }

    // ---------------- 横向滚动 ----------------

    private static void CheckScroll(Ctx ctx, ScrollViewer sv)
    {
        if (HScrollOk(sv) || sv.Viewport.Width <= 0 || sv.TemplatedParent is TextBox) return;
        if (sv.Extent.Width > sv.Viewport.Width + 1)
            ctx.Issues.Add($"[audit] horizontal overflow {Px(sv.Extent.Width)}>{Px(sv.Viewport.Width)}px: {Describe(sv)}");
    }

    // ---------------- 色板 ----------------

    private static void CheckColors(Ctx ctx, Visual v)
    {
        switch (v)
        {
            case Border b:
                Check(ctx, v, b.Background, "Background");
                if (!IsZero(b.BorderThickness)) Check(ctx, v, b.BorderBrush, "BorderBrush");
                foreach (var sh in b.BoxShadow) Check(ctx, v, sh.Color, "BoxShadow", shadow: true);
                break;
            case Panel p:
                Check(ctx, v, p.Background, "Background");
                break;
            case TextBlock tb:
                Check(ctx, v, tb.Background, "Background");
                if (TextOf(tb).Length > 0) Check(ctx, v, tb.Foreground, "Foreground");
                break;
            case ContentPresenter cp:
                Check(ctx, v, cp.Background, "Background");
                if (!IsZero(cp.BorderThickness)) Check(ctx, v, cp.BorderBrush, "BorderBrush");
                foreach (var sh in cp.BoxShadow) Check(ctx, v, sh.Color, "BoxShadow", shadow: true);
                break;
            case TextPresenter tp:
                if (!string.IsNullOrEmpty(tp.Text)) Check(ctx, v, tp.Foreground, "Foreground");
                break;
            case TemplatedControl tc:
                Check(ctx, v, tc.Background, "Background");
                if (!IsZero(tc.BorderThickness)) Check(ctx, v, tc.BorderBrush, "BorderBrush");
                break;
            case Shape s:
                Check(ctx, v, s.Fill, "Fill");
                if (s.StrokeThickness > 0) Check(ctx, v, s.Stroke, "Stroke");
                break;
            case IconView iv:
                Check(ctx, v, iv.Foreground, "Foreground");
                break;
        }
    }

    private static bool IsZero(Thickness t) => t.Left == 0 && t.Top == 0 && t.Right == 0 && t.Bottom == 0;

    private static void Check(Ctx ctx, Visual v, IBrush? brush, string prop)
    {
        switch (brush)
        {
            case ISolidColorBrush s when s.Opacity > 0.004:
                Check(ctx, v, s.Color, prop);
                break;
            case IGradientBrush g when g.Opacity > 0.004:
                foreach (var stop in g.GradientStops) Check(ctx, v, stop.Color, prop + " gradient");
                break;
        }
    }

    private static void Check(Ctx ctx, Visual v, Color c, string prop, bool shadow = false)
    {
        if (c.A <= 1) return;   // 完全透明
        ctx.Colors++;
        var (tok, d) = Palette.Nearest(c, shadow);
        if (d <= Palette.MaxDelta) return;
        var key = $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        ctx.Bad[key] = ctx.Bad.TryGetValue(key, out var e) ? e with { N = e.N + 1 } : (1, Describe(v), prop, tok.Css, d);
    }

    // ---------------- 描述 ----------------

    /// <summary>「父类型.类 &gt; 类型#名字.类1.类2」（同 MOCK describe）。</summary>
    public static string Describe(Visual v)
    {
        static string One(Visual x)
        {
            var s = x.GetType().Name;
            if (x is StyledElement se)
            {
                if (!string.IsNullOrEmpty(se.Name)) s += "#" + se.Name;
                var cls = se.Classes.Where(c => !c.StartsWith(':')).Take(3).ToList();
                if (cls.Count > 0) s += "." + string.Join(".", cls);
            }
            return s;
        }
        // 宿主：最近的、带样式类或非模板名字（不是 PART_*）的祖先，或者本工程的控件 / 视图
        var host = v.GetVisualAncestors().OfType<StyledElement>().FirstOrDefault(a =>
            a.Classes.Any(c => !c.StartsWith(':'))
            || (!string.IsNullOrEmpty(a.Name) && !a.Name.StartsWith("PART_", StringComparison.Ordinal))
            || (a.GetType().Namespace ?? "").StartsWith("ParallelReactor", StringComparison.Ordinal));
        return host is Visual hv ? One(hv) + " > " + One(v) : One(v);
    }
}
#endif
