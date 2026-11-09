#if DEBUG
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ParallelReactor.Control.Behaviors;
using ParallelReactor.Control.Controls;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 审计自检（PR_CONTROL_DEMO=1 + PR_CONTROL_AUDIT_SELFTEST=1）：渲染一块故意做坏的视图，确认 ShotAudit 能报出每一类问题，
/// 也确认不该报的（带提示的省略、透明度变体、上下相碰、控件内的文字、图标按钮、放得下的步骤条…）不会误报。不进陈列页。
/// 注意：本文件故意用色板外的颜色和过小的字号（运行时由常量算出，不是字面量），只给自检用；PaletteLintTests 把它列在「故意违规」名单里。
/// </summary>
public static class AuditSelfTest
{
    // 故意的坏值：由常量算出（不是字面量，源码扫描 TypeScaleLintTests 不会把它们当成产品代码里的违规）
    private static readonly double Px11 = ShotAudit.MinText - 2;
    private static readonly double Px13 = ShotAudit.MinCjk - 1;
    private static readonly Color OffRed = new(255, 255, 0, 0);
    private static readonly Color OffGreen = new(255, 0, 255, 0);
    private static readonly Color OffBlue = new(255, 0, 0, 255);

    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>必须出现在审计结果里的片段（说明, 片段）。</summary>
    public static readonly (string What, string Needle)[] Expected =
    {
        ("11 px 文字", "font 11.0px < 13px"),
        ("13 px 中文", "CJK font 13.0px < 14px"),
        ("缩放 0.8 后的 14 px 中文（× 外框缩放）", "font 11.2px < 13px"),
        ("13 px 的中文标点「」", "「标点」『』"),
        ("输入框里的 11 px 字", "control font 11.0px < 13px"),
        ("输入框的文字比框宽", "[audit] input text clipped"),
        ("色板外的红色边框", "[palette] " + Hex(OffRed)),
        ("色板外的绿色文字", "[palette] " + Hex(OffGreen)),
        ("Run 自己的色板外前景", "[palette] " + Hex(OffBlue)),
        ("文字 × 文字重叠", "[overlap] text × text: \"重叠甲"),
        ("文字 × 控件重叠", "[overlap] text × control: \"压住按钮"),
        ("控件 × 控件重叠", "[overlap] control × control"),
        ("文字 × 图标重叠", "[overlap] text × figure: \"压住图标"),
        ("省略但没有提示", "trimmed without tooltip"),
        ("状态字被省略", "state word truncated"),
        ("读数被省略（带提示也报）", "[audit] number truncated"),
        ("文字溢出自己的框", "[audit] overflow"),
        ("MaxLines 悄悄丢掉后面的行", "MaxLines=1 或框太矮"),
        ("框太矮、后面的行被丢掉", "\"竖向溢出卡片"),
        ("横向滚动", "[audit] horizontal"),
        ("文字被裁掉", "[audit] clipped"),
        ("文字超出所在的框", "[audit] outside"),
        ("步骤条被挤扁", "[audit] step bar squeezed"),
        ("普通 ToggleButton 带原因", "[audit] reason on plain ToggleButton"),
        ("字重回退成常规（11.1 回退缺陷）", "[audit] font weight"),
        ("自绘文字 11 px", "drawn text font 11.0px"),
        ("画成方块的字（Inter + U+2060 的回退缺陷）", "[audit] missing glyph"),
    };

    /// <summary>不许出现的片段（误报）。</summary>
    public static readonly (string What, string Needle)[] MustNot =
    {
        ("带提示的省略", "有提示的省略"),
        ("t-1 的透明度变体", Hex(Palette.T1Color)),
        ("上下相碰的两行", "相碰"),
        ("控件里的文字", "嵌套"),
        ("合法的 15 px 正文", "合法正文"),
        ("BarFitPanel 有意藏起来的剩余时间", "藏起来的剩余时间"),
        ("图标按钮里的图标与文字", "图标按钮合法"),
        ("放得下的步骤条", "BarOk"),
        ("带换行的多段文字（LineBreak）", "换行合法"),
        ("放得下的读数", "合法读数"),
        ("pr:PrToggle 带原因", "SafeToggle"),
        ("用了 UiFontCjk 的粗体段", "粗体合法"),
        ("UiText.Keep 处理过的句子（当前字体的连接符）", "连接符合法"),
    };

    /// <summary>自绘文字的样例控件：画一段 11 px 的字并如实报告（IAuditText）。</summary>
    private sealed class DrawnSample : Avalonia.Controls.Control, IAuditText
    {
        private const string Label = "自绘刻度";
        private double Size => Px11;

        private FormattedText Text() => new(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(UiFonts.Create()), Size, Palette.T3Brush);

        protected override Size MeasureOverride(Size availableSize) => new(80, 20);

        public override void Render(DrawingContext context) => context.DrawText(Text(), new Point(0, 2));

        public IEnumerable<AuditTextRun> AuditTexts
        {
            get { var t = Text(); yield return new AuditTextRun(Label, Size, new Rect(0, 2, t.Width, t.Height), Palette.T3Color); }
        }
    }

    public static Avalonia.Controls.Control Build()
    {
        TextBlock T(string s, double? size = null) => new() { Text = s, FontSize = size ?? 15, Foreground = Palette.T2Brush };
        var canvas = new Canvas { Width = 900, Height = 820, Background = Palette.BgCardBrush };
        void At(Avalonia.Controls.Control c, double x, double y) { Canvas.SetLeft(c, x); Canvas.SetTop(c, y); canvas.Children.Add(c); }
        Avalonia.Controls.Control Th(string key, Avalonia.Controls.Control c)
        {
            if (Application.Current!.TryFindResource(key, out var t) && t is Avalonia.Styling.ControlTheme ct) c.Theme = ct;
            return c;
        }

        At(T("合法正文 15 px"), 20, 10);
        At(T("坏字号 11 px", Px11), 20, 40);
        At(T("中文 13 px", Px13), 20, 70);
        At(new LayoutTransformControl { LayoutTransform = new ScaleTransform(0.8, 0.8), Child = T("缩放后的中文", 14) }, 20, 100);
        At(T("「标点」『』", Px13), 200, 70);
        At(new Border { Width = 120, Height = 30, BorderBrush = new SolidColorBrush(OffRed), BorderThickness = new Thickness(2) }, 20, 130);
        At(new TextBlock { Text = "绿色文字", FontSize = 15, Foreground = new SolidColorBrush(OffGreen) }, 160, 134);
        var runs = new TextBlock { FontSize = 15, Foreground = Palette.T2Brush };
        runs.Inlines!.Add(new Run("正常 "));
        runs.Inlines!.Add(new Run("蓝色段") { Foreground = new SolidColorBrush(OffBlue) });
        At(runs, 260, 134);
        // 重叠：两段文字压在一起；一段文字压在「控件」上；两个控件压在一起；文字压在图标上
        At(T("重叠甲 overlap A"), 20, 180);
        At(T("重叠乙 overlap B"), 60, 184);
        At(new Border { Width = 120, Height = 32, Classes = { "audit-ctl" }, Background = Palette.BgSunkenBrush }, 300, 180);
        At(T("压住按钮的字"), 330, 186);
        At(new Border { Width = 60, Height = 30, Classes = { "audit-ctl" }, Background = Palette.BgSunkenBrush }, 440, 180);
        At(new Border { Width = 60, Height = 30, Classes = { "audit-ctl" }, Background = Palette.BgPressBrush }, 470, 186);
        At(new IconView { Icon = "warn", Size = 24, Foreground = Palette.T1Brush }, 780, 180);
        At(T("压住图标"), 786, 184);
        // 不算：上下相碰（重叠 2 px，< 30 %）
        At(T("相碰甲"), 560, 180);
        At(T("相碰乙"), 560, 196);
        // 不算：控件里的文字；图标按钮里的图标和文字
        At(new Border { Width = 160, Height = 32, Classes = { "audit-ctl" }, Background = Palette.BgSunkenBrush, Child = T("嵌套在控件里的字") }, 620, 220);
        var iconBtn = (Button)Th("PrButton", new Button { Content = "图标按钮合法" });
        Pr.SetIcon(iconBtn, "edit");
        At(iconBtn, 640, 270);
        // 省略：无提示（报）/ 有提示（不报）/ 状态字（总是报）/ 读数（总是报）
        At(new TextBlock { Text = "截断无提示的一段很长很长的文字", Width = 90, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Palette.T2Brush }, 20, 240);
        var tipped = new TextBlock { Text = "有提示的省略一段很长很长的文字", Width = 90, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Palette.T2Brush };
        ToolTip.SetTip(tipped, tipped.Text);
        At(tipped, 160, 240);
        var st = new TextBlock { Text = "超压报警", Width = 30, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Palette.RInkBrush, Classes = { "stword" } };
        ToolTip.SetTip(st, st.Text);
        At(st, 300, 240);
        var num = new TextBlock { Text = "119.8", Width = 22, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Palette.T1Brush, Classes = { "num" } };
        ToolTip.SetTip(num, "119.8 ℃");
        At(num, 340, 240);
        At(new Border { Width = 120, Child = new FitText { Text = "合法读数 1", Sizes = "15" } }, 380, 275);
        // 溢出：不省略、比自己的框宽
        At(new TextBlock { Text = "溢出自己框的一段文字", Width = 60, FontSize = 15, Foreground = Palette.T2Brush }, 380, 240);
        // 透明度变体：t-1 @ 40 %（合法）
        At(new Border { Width = 60, Height = 20, Background = new SolidColorBrush(Palette.T1Color, 0.4) }, 540, 240);
        // 丢行：MaxLines = 1 没有省略号；固定高度的框里换行的文字（后面的行被悄悄丢掉）
        At(new TextBlock { Width = 120, MaxLines = 1, TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = Palette.T2Brush, Text = "第一行文字很长 第二行会被丢掉 MaxLines" }, 20, 330);
        At(new Border { Width = 120, Height = 40, Background = Palette.BgSunkenBrush,
            Child = new TextBlock { Text = "竖向溢出卡片的一段很长很长很长的文字", TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = Palette.T2Brush, VerticalAlignment = VerticalAlignment.Top } }, 160, 330);
        // 不算：带 LineBreak 的多段文字
        var lb = new TextBlock { FontSize = 15, Foreground = Palette.T2Brush };
        lb.Inlines!.Add(new Run("换行合法"));
        lb.Inlines!.Add(new LineBreak());
        lb.Inlines!.Add(new Run("第二行"));
        At(lb, 300, 330);
        // 字重：粗体段直接跟在常规中文段后面（没用 UiFontCjk）→ 回退沿用常规字重；用了 UiFontCjk 的不报
        TextBlock Mixed(string bold, bool workaround)
        {
            var tb = new TextBlock { FontSize = 15, Foreground = Palette.T2Brush };
            tb.Inlines!.Add(new Run("急停"));
            var b = new Run(bold) { FontWeight = FontWeight.SemiBold };
            var after = new Run("，连接正常就会发送。");
            if (workaround) { b.FontFamily = UiFonts.CreateCjk(); after.FontFamily = UiFonts.CreateCjk(); }
            tb.Inlines!.Add(b);
            tb.Inlines!.Add(after);
            return tb;
        }
        At(Mixed("不需要控制权", false), 420, 330);
        At(Mixed("粗体合法", true), 420, 360);
        // 裁切 / 超框：固定宽度的文字放进更窄的框（裁剪的框 → clipped；不裁剪、有底色的框 → outside）
        At(new Border { Width = 100, Height = 30, Background = Palette.BgSunkenBrush, ClipToBounds = true,
            Child = new TextBlock { Text = "被框裁掉的一段文字", Width = 220, FontSize = 15, Foreground = Palette.T2Brush } }, 20, 420);
        At(new Border { Width = 100, Height = 30, Background = Palette.BgSunkenBrush,
            Child = new TextBlock { Text = "跑出框外的一段文字", Width = 220, FontSize = 15, Foreground = Palette.T2Brush } }, 380, 420);
        // 输入框：11 px 的字；文字比框宽
        At(Th("PrInput", new TextBox { Width = 160, FontSize = Px11, Text = "输入小字 input" }), 20, 470);
        At(Th("PrInput", new TextBox { Width = 90, Text = "192.168.100.200:8080/very-long" }), 200, 470);
        // 步骤条：12 段塞进 40 px 的框（挤扁）；8 段放进 90 px（合法）
        var squeezed = new StepBar();
        for (int i = 0; i < 12; i++) squeezed.Segments.Add(new StepSeg { State = StepSegState.Done });
        At(new Border { Width = 40, Height = 12, ClipToBounds = true, Child = squeezed }, 320, 480);
        var okBar = new StepBar { Name = "BarOk" };
        for (int i = 0; i < 8; i++) okBar.Segments.Add(new StepSeg { State = i < 3 ? StepSegState.Done : StepSegState.Pending });
        At(new Border { Width = 90, Child = okBar }, 380, 480);
        // 不算：BarFitPanel 放不下时有意藏起来的文字（排到行外、被行裁掉）
        var bar = new StepBar();
        for (int i = 0; i < 8; i++) bar.Segments.Add(new StepSeg { State = i < 3 ? StepSegState.Done : StepSegState.Pending });
        At(new Border { Width = 90, Background = Palette.BgCardBrush,
            Child = new BarFitPanel { Children = { bar, T("藏起来的剩余时间 42 min") } } }, 20, 530);
        // 开关带原因：普通 ToggleButton（报）/ pr:PrToggle（不报）
        var plainToggle = (ToggleButton)Th("PrChip", new ToggleButton { Name = "PlainToggle", Content = "普通开关" });
        Pr.SetReason(plainToggle, "未安装");
        At(plainToggle, 160, 530);
        var safeToggle = (ToggleButton)Th("PrChip", new PrToggle { Name = "SafeToggle", Content = "安全开关" });
        Pr.SetReason(safeToggle, "未安装");
        At(safeToggle, 300, 530);
        // 自绘文字：11 px（IAuditText 报告）
        At(new DrawnSample(), 460, 535);
        // 方块：Inter 首选时 U+2060 后面的中文留在 Inter 里（.notdef）；UiText.Keep 换成当前字体能显示的连接符（不报）
        At(new TextBlock { Text = "方块 RV\u2060超\u2060压", FontSize = 15, Foreground = Palette.T2Brush, FontFamily = new FontFamily("fonts:Inter#Inter, PingFang SC") }, 560, 535);
        At(T(UiText.Keep("连接符合法 超压 3.12 MPa · 已关闭 SV11")), 560, 565);
        // 横向滚动
        At(new ScrollViewer
        {
            Width = 300, Height = 60, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 900, Height = 30, Background = Palette.BgSunkenBrush },
        }, 20, 600);
        return canvas;
    }

    /// <summary>核对审计结果：返回（漏报, 误报）。</summary>
    public static (List<string> Missing, List<string> False) Verify(IReadOnlyList<string> issues)
    {
        var all = string.Join("\n", issues);
        var missing = Expected.Where(e => !all.Contains(e.Needle)).Select(e => $"{e.What}（期望包含「{e.Needle}」）").ToList();
        var falsePos = MustNot.Where(e => all.Contains(e.Needle)).Select(e => $"{e.What}（不应包含「{e.Needle}」）").ToList();
        return (missing, falsePos);
    }
}
#endif
