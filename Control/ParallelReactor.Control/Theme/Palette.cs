using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// 色板的 C# 镜像（PORT-PLAN §4.1 / §4.2）：与 Styles/Tokens.axaml 同名（BgAppColor / BgAppBrush …），两边都由 tools/gen-tokens.py
/// 从 MOCK css/base.css :root 生成。自绘控件只从这里取颜色、画刷和画笔；别处不许写颜色字面量（PaletteLintTests）。
/// </summary>
public static class Palette
{
    /// <summary>一个令牌：Css = 「--bg-app」，Key = 「BgApp」（资源键 = Key + Color / Brush），Group = 色板分组（同 MOCK #/gallery）。</summary>
    public sealed record Token(string Css, string Key, string Group, Color Color, bool ShadowOnly = false)
    {
        public string Hex => $"#{Color.R:x2}{Color.G:x2}{Color.B:x2}";
    }

    private static Color Rgb(uint rgb) => Color.FromUInt32(0xFF000000u | rgb);
    private static Color Argb(uint argb) => Color.FromUInt32(argb);

    // BEGIN GENERATED COLORS（Control/tools/gen-tokens.py ← MOCK/css/base.css :root；不要手改，改样稿后重新生成）
    // 底色
    public static readonly Color BgAppColor = Rgb(0xececee);   // --bg-app
    public static readonly Color BgPanelColor = Rgb(0xf7f7f8);   // --bg-panel
    public static readonly Color BgHeadColor = Rgb(0xfafafb);   // --bg-head
    public static readonly Color BgCardColor = Rgb(0xffffff);   // --bg-card
    public static readonly Color BgSideColor = Rgb(0xf4f4f5);   // --bg-side
    public static readonly Color BgSunkenColor = Rgb(0xf1f1f3);   // --bg-sunken
    public static readonly Color BgHoverColor = Rgb(0xececf1);   // --bg-hover
    public static readonly Color BgPressColor = Rgb(0xe7e7eb);   // --bg-press
    // 线
    public static readonly Color LineStrongColor = Rgb(0xaaaab0);   // --line-strong
    public static readonly Color LineColor = Rgb(0xc9c9cd);   // --line
    public static readonly Color Line2Color = Rgb(0xd5d5d8);   // --line-2
    public static readonly Color LineSoftColor = Rgb(0xdedee1);   // --line-soft
    public static readonly Color LineFaintColor = Rgb(0xededf0);   // --line-faint
    public static readonly Color LineMenuColor = Rgb(0x2e2e31);   // --line-menu
    // 文字
    public static readonly Color T1Color = Rgb(0x2f2f35);   // --t-1
    public static readonly Color T2Color = Rgb(0x3f3f45);   // --t-2
    public static readonly Color T3Color = Rgb(0x5f5f66);   // --t-3
    public static readonly Color T4Color = Rgb(0x75757c);   // --t-4
    public static readonly Color TDisColor = Rgb(0xa5a5ab);   // --t-dis
    public static readonly Color TInvColor = Rgb(0xffffff);   // --t-inv
    // 强调色
    public static readonly Color AColor = Rgb(0x765cc5);   // --a
    public static readonly Color ADColor = Rgb(0x6550b1);   // --a-d
    public static readonly Color AInkColor = Rgb(0x554677);   // --a-ink
    public static readonly Color ASoftColor = Rgb(0xe5e0f5);   // --a-soft
    public static readonly Color ATintColor = Rgb(0xf1eefa);   // --a-tint
    public static readonly Color ALineColor = Rgb(0xa999da);   // --a-line
    public static readonly Color ATabColor = Rgb(0x8a78cd);   // --a-tab
    public static readonly Color Side1Color = Rgb(0x8269d0);   // --side-1
    public static readonly Color Side2Color = Rgb(0x6e57ba);   // --side-2
    public static readonly Color SideLineColor = Rgb(0x6753a8);   // --side-line
    public static readonly Color SideInkColor = Rgb(0xeeeaff);   // --side-ink
    // 运行（浅绿）
    public static readonly Color G1Color = Rgb(0xf7fff1);   // --g-1
    public static readonly Color G2Color = Rgb(0xefffe4);   // --g-2
    public static readonly Color G3Color = Rgb(0xe5ffd0);   // --g-3
    public static readonly Color G4Color = Rgb(0xd9fbb9);   // --g-4
    public static readonly Color GLineColor = Rgb(0xdcf8c7);   // --g-line
    public static readonly Color GEdgeColor = Rgb(0xd9f9bf);   // --g-edge
    public static readonly Color GDotColor = Rgb(0xb7e99c);   // --g-dot
    public static readonly Color GDotLineColor = Rgb(0xa0d782);   // --g-dot-line
    public static readonly Color GInkColor = Rgb(0x4a7a31);   // --g-ink
    // 报警（暗红）
    public static readonly Color RColor = Rgb(0xc4505d);   // --r
    public static readonly Color RInkColor = Rgb(0xb0404d);   // --r-ink
    public static readonly Color RBgColor = Rgb(0xfbeff1);   // --r-bg
    public static readonly Color RLineColor = Rgb(0xebc0c6);   // --r-line
    public static readonly Color RBtnColor = Rgb(0xc43f4c);   // --r-btn
    public static readonly Color RBtnDColor = Rgb(0xad3441);   // --r-btn-d
    // 中性状态
    public static readonly Color NDotColor = Rgb(0xc4c4ca);   // --n-dot
    public static readonly Color NDotLineColor = Rgb(0xb0b0b6);   // --n-dot-line
    // 曲线
    public static readonly Color ChBgColor = Rgb(0xfafafa);   // --ch-bg
    public static readonly Color ChGridColor = Rgb(0xe3e3e6);   // --ch-grid
    public static readonly Color ChAxisColor = Rgb(0xaaaeb1);   // --ch-axis
    public static readonly Color ChTColor = Rgb(0x6f797b);   // --ch-t
    public static readonly Color ChTSpColor = Rgb(0xaab1b3);   // --ch-t-sp
    public static readonly Color ChPColor = Rgb(0x7aa088);   // --ch-p
    public static readonly Color ChPSpColor = Rgb(0xaac5b2);   // --ch-p-sp
    public static readonly Color ChS1Color = Rgb(0x4f5559);   // --ch-s1
    public static readonly Color ChS2Color = Rgb(0x7d8789);   // --ch-s2
    public static readonly Color ChS3Color = Rgb(0xa9b0b2);   // --ch-s3
    public static readonly Color ChMarkColor = Rgb(0x8a78cd);   // --ch-mark
    public static readonly Color ChLimitColor = Rgb(0xc4505d);   // --ch-limit
    // 釜体
    public static readonly Color VLineColor = Rgb(0x8d8d94);   // --v-line
    public static readonly Color VDetailColor = Rgb(0xa5a5ab);   // --v-detail
    public static readonly Color VLiqColor = Rgb(0xededf0);   // --v-liq
    public static readonly Color VLiqRunColor = Rgb(0xe6f7da);   // --v-liq-run
    public static readonly Color VLiqRunLineColor = Rgb(0xb9dca3);   // --v-liq-run-line
    public static readonly Color VLiqAlarmColor = Rgb(0xf8e4e7);   // --v-liq-alarm
    public static readonly Color VLineAlarmColor = Rgb(0xc98b94);   // --v-line-alarm
    // 杂项（§2 Misc：rgba → #aarrggbb）
    public static readonly Color ToastColor = Argb(0xeb2a2a30);   // --toast
    public static readonly Color OverlayColor = Argb(0x401e1e26);   // --overlay

    public static readonly IImmutableSolidColorBrush BgAppBrush = new ImmutableSolidColorBrush(BgAppColor);
    public static readonly IImmutableSolidColorBrush BgPanelBrush = new ImmutableSolidColorBrush(BgPanelColor);
    public static readonly IImmutableSolidColorBrush BgHeadBrush = new ImmutableSolidColorBrush(BgHeadColor);
    public static readonly IImmutableSolidColorBrush BgCardBrush = new ImmutableSolidColorBrush(BgCardColor);
    public static readonly IImmutableSolidColorBrush BgSideBrush = new ImmutableSolidColorBrush(BgSideColor);
    public static readonly IImmutableSolidColorBrush BgSunkenBrush = new ImmutableSolidColorBrush(BgSunkenColor);
    public static readonly IImmutableSolidColorBrush BgHoverBrush = new ImmutableSolidColorBrush(BgHoverColor);
    public static readonly IImmutableSolidColorBrush BgPressBrush = new ImmutableSolidColorBrush(BgPressColor);
    public static readonly IImmutableSolidColorBrush LineStrongBrush = new ImmutableSolidColorBrush(LineStrongColor);
    public static readonly IImmutableSolidColorBrush LineBrush = new ImmutableSolidColorBrush(LineColor);
    public static readonly IImmutableSolidColorBrush Line2Brush = new ImmutableSolidColorBrush(Line2Color);
    public static readonly IImmutableSolidColorBrush LineSoftBrush = new ImmutableSolidColorBrush(LineSoftColor);
    public static readonly IImmutableSolidColorBrush LineFaintBrush = new ImmutableSolidColorBrush(LineFaintColor);
    public static readonly IImmutableSolidColorBrush LineMenuBrush = new ImmutableSolidColorBrush(LineMenuColor);
    public static readonly IImmutableSolidColorBrush T1Brush = new ImmutableSolidColorBrush(T1Color);
    public static readonly IImmutableSolidColorBrush T2Brush = new ImmutableSolidColorBrush(T2Color);
    public static readonly IImmutableSolidColorBrush T3Brush = new ImmutableSolidColorBrush(T3Color);
    public static readonly IImmutableSolidColorBrush T4Brush = new ImmutableSolidColorBrush(T4Color);
    public static readonly IImmutableSolidColorBrush TDisBrush = new ImmutableSolidColorBrush(TDisColor);
    public static readonly IImmutableSolidColorBrush TInvBrush = new ImmutableSolidColorBrush(TInvColor);
    public static readonly IImmutableSolidColorBrush ABrush = new ImmutableSolidColorBrush(AColor);
    public static readonly IImmutableSolidColorBrush ADBrush = new ImmutableSolidColorBrush(ADColor);
    public static readonly IImmutableSolidColorBrush AInkBrush = new ImmutableSolidColorBrush(AInkColor);
    public static readonly IImmutableSolidColorBrush ASoftBrush = new ImmutableSolidColorBrush(ASoftColor);
    public static readonly IImmutableSolidColorBrush ATintBrush = new ImmutableSolidColorBrush(ATintColor);
    public static readonly IImmutableSolidColorBrush ALineBrush = new ImmutableSolidColorBrush(ALineColor);
    public static readonly IImmutableSolidColorBrush ATabBrush = new ImmutableSolidColorBrush(ATabColor);
    public static readonly IImmutableSolidColorBrush Side1Brush = new ImmutableSolidColorBrush(Side1Color);
    public static readonly IImmutableSolidColorBrush Side2Brush = new ImmutableSolidColorBrush(Side2Color);
    public static readonly IImmutableSolidColorBrush SideLineBrush = new ImmutableSolidColorBrush(SideLineColor);
    public static readonly IImmutableSolidColorBrush SideInkBrush = new ImmutableSolidColorBrush(SideInkColor);
    public static readonly IImmutableSolidColorBrush G1Brush = new ImmutableSolidColorBrush(G1Color);
    public static readonly IImmutableSolidColorBrush G2Brush = new ImmutableSolidColorBrush(G2Color);
    public static readonly IImmutableSolidColorBrush G3Brush = new ImmutableSolidColorBrush(G3Color);
    public static readonly IImmutableSolidColorBrush G4Brush = new ImmutableSolidColorBrush(G4Color);
    public static readonly IImmutableSolidColorBrush GLineBrush = new ImmutableSolidColorBrush(GLineColor);
    public static readonly IImmutableSolidColorBrush GEdgeBrush = new ImmutableSolidColorBrush(GEdgeColor);
    public static readonly IImmutableSolidColorBrush GDotBrush = new ImmutableSolidColorBrush(GDotColor);
    public static readonly IImmutableSolidColorBrush GDotLineBrush = new ImmutableSolidColorBrush(GDotLineColor);
    public static readonly IImmutableSolidColorBrush GInkBrush = new ImmutableSolidColorBrush(GInkColor);
    public static readonly IImmutableSolidColorBrush RBrush = new ImmutableSolidColorBrush(RColor);
    public static readonly IImmutableSolidColorBrush RInkBrush = new ImmutableSolidColorBrush(RInkColor);
    public static readonly IImmutableSolidColorBrush RBgBrush = new ImmutableSolidColorBrush(RBgColor);
    public static readonly IImmutableSolidColorBrush RLineBrush = new ImmutableSolidColorBrush(RLineColor);
    public static readonly IImmutableSolidColorBrush RBtnBrush = new ImmutableSolidColorBrush(RBtnColor);
    public static readonly IImmutableSolidColorBrush RBtnDBrush = new ImmutableSolidColorBrush(RBtnDColor);
    public static readonly IImmutableSolidColorBrush NDotBrush = new ImmutableSolidColorBrush(NDotColor);
    public static readonly IImmutableSolidColorBrush NDotLineBrush = new ImmutableSolidColorBrush(NDotLineColor);
    public static readonly IImmutableSolidColorBrush ChBgBrush = new ImmutableSolidColorBrush(ChBgColor);
    public static readonly IImmutableSolidColorBrush ChGridBrush = new ImmutableSolidColorBrush(ChGridColor);
    public static readonly IImmutableSolidColorBrush ChAxisBrush = new ImmutableSolidColorBrush(ChAxisColor);
    public static readonly IImmutableSolidColorBrush ChTBrush = new ImmutableSolidColorBrush(ChTColor);
    public static readonly IImmutableSolidColorBrush ChTSpBrush = new ImmutableSolidColorBrush(ChTSpColor);
    public static readonly IImmutableSolidColorBrush ChPBrush = new ImmutableSolidColorBrush(ChPColor);
    public static readonly IImmutableSolidColorBrush ChPSpBrush = new ImmutableSolidColorBrush(ChPSpColor);
    public static readonly IImmutableSolidColorBrush ChS1Brush = new ImmutableSolidColorBrush(ChS1Color);
    public static readonly IImmutableSolidColorBrush ChS2Brush = new ImmutableSolidColorBrush(ChS2Color);
    public static readonly IImmutableSolidColorBrush ChS3Brush = new ImmutableSolidColorBrush(ChS3Color);
    public static readonly IImmutableSolidColorBrush ChMarkBrush = new ImmutableSolidColorBrush(ChMarkColor);
    public static readonly IImmutableSolidColorBrush ChLimitBrush = new ImmutableSolidColorBrush(ChLimitColor);
    public static readonly IImmutableSolidColorBrush VLineBrush = new ImmutableSolidColorBrush(VLineColor);
    public static readonly IImmutableSolidColorBrush VDetailBrush = new ImmutableSolidColorBrush(VDetailColor);
    public static readonly IImmutableSolidColorBrush VLiqBrush = new ImmutableSolidColorBrush(VLiqColor);
    public static readonly IImmutableSolidColorBrush VLiqRunBrush = new ImmutableSolidColorBrush(VLiqRunColor);
    public static readonly IImmutableSolidColorBrush VLiqRunLineBrush = new ImmutableSolidColorBrush(VLiqRunLineColor);
    public static readonly IImmutableSolidColorBrush VLiqAlarmBrush = new ImmutableSolidColorBrush(VLiqAlarmColor);
    public static readonly IImmutableSolidColorBrush VLineAlarmBrush = new ImmutableSolidColorBrush(VLineAlarmColor);
    public static readonly IImmutableSolidColorBrush ToastBrush = new ImmutableSolidColorBrush(ToastColor);
    public static readonly IImmutableSolidColorBrush OverlayBrush = new ImmutableSolidColorBrush(OverlayColor);
    /// <summary>焦点环 = --focus = var(--a-d)。</summary>
    public static readonly IImmutableSolidColorBrush FocusBrush = ADBrush;

    public static readonly BoxShadows ShadowCard = BoxShadows.Parse("0 1 2 0 #0f282832");   // --shadow-card
    public static readonly BoxShadows ShadowPop = BoxShadows.Parse("0 8 28 0 #29000000");   // --shadow-pop
    public static readonly BoxShadows ShadowDlg = BoxShadows.Parse("0 10 45 0 #40000000");   // --shadow-dlg
    public static readonly BoxShadows ShadowCardHover = BoxShadows.Parse("0 2 10 0 #1c282832");   // 卡片悬停 / 选中（overview.css .overview-card:hover，颜色同 --shadow-card）

    /// <summary>67 个颜色令牌（与 PR.PALETTE 同序），分组同 MOCK #/gallery 的色板。</summary>
    public static readonly IReadOnlyList<Token> Tokens = new Token[]
    {
        new("--bg-app", "BgApp", "底色", BgAppColor),
        new("--bg-panel", "BgPanel", "底色", BgPanelColor),
        new("--bg-head", "BgHead", "底色", BgHeadColor),
        new("--bg-card", "BgCard", "底色", BgCardColor),
        new("--bg-side", "BgSide", "底色", BgSideColor),
        new("--bg-sunken", "BgSunken", "底色", BgSunkenColor),
        new("--bg-hover", "BgHover", "底色", BgHoverColor),
        new("--bg-press", "BgPress", "底色", BgPressColor),
        new("--line-strong", "LineStrong", "线", LineStrongColor),
        new("--line", "Line", "线", LineColor),
        new("--line-2", "Line2", "线", Line2Color),
        new("--line-soft", "LineSoft", "线", LineSoftColor),
        new("--line-faint", "LineFaint", "线", LineFaintColor),
        new("--line-menu", "LineMenu", "线", LineMenuColor),
        new("--t-1", "T1", "文字", T1Color),
        new("--t-2", "T2", "文字", T2Color),
        new("--t-3", "T3", "文字", T3Color),
        new("--t-4", "T4", "文字", T4Color),
        new("--t-dis", "TDis", "文字", TDisColor),
        new("--t-inv", "TInv", "文字", TInvColor),
        new("--a", "A", "强调色", AColor),
        new("--a-d", "AD", "强调色", ADColor),
        new("--a-ink", "AInk", "强调色", AInkColor),
        new("--a-soft", "ASoft", "强调色", ASoftColor),
        new("--a-tint", "ATint", "强调色", ATintColor),
        new("--a-line", "ALine", "强调色", ALineColor),
        new("--a-tab", "ATab", "强调色", ATabColor),
        new("--side-1", "Side1", "强调色", Side1Color),
        new("--side-2", "Side2", "强调色", Side2Color),
        new("--side-line", "SideLine", "强调色", SideLineColor),
        new("--side-ink", "SideInk", "强调色", SideInkColor),
        new("--g-1", "G1", "运行（浅绿）", G1Color),
        new("--g-2", "G2", "运行（浅绿）", G2Color),
        new("--g-3", "G3", "运行（浅绿）", G3Color),
        new("--g-4", "G4", "运行（浅绿）", G4Color),
        new("--g-line", "GLine", "运行（浅绿）", GLineColor),
        new("--g-edge", "GEdge", "运行（浅绿）", GEdgeColor),
        new("--g-dot", "GDot", "运行（浅绿）", GDotColor),
        new("--g-dot-line", "GDotLine", "运行（浅绿）", GDotLineColor),
        new("--g-ink", "GInk", "运行（浅绿）", GInkColor),
        new("--r", "R", "报警（暗红）", RColor),
        new("--r-ink", "RInk", "报警（暗红）", RInkColor),
        new("--r-bg", "RBg", "报警（暗红）", RBgColor),
        new("--r-line", "RLine", "报警（暗红）", RLineColor),
        new("--r-btn", "RBtn", "报警（暗红）", RBtnColor),
        new("--r-btn-d", "RBtnD", "报警（暗红）", RBtnDColor),
        new("--n-dot", "NDot", "中性状态", NDotColor),
        new("--n-dot-line", "NDotLine", "中性状态", NDotLineColor),
        new("--ch-bg", "ChBg", "曲线", ChBgColor),
        new("--ch-grid", "ChGrid", "曲线", ChGridColor),
        new("--ch-axis", "ChAxis", "曲线", ChAxisColor),
        new("--ch-t", "ChT", "曲线", ChTColor),
        new("--ch-t-sp", "ChTSp", "曲线", ChTSpColor),
        new("--ch-p", "ChP", "曲线", ChPColor),
        new("--ch-p-sp", "ChPSp", "曲线", ChPSpColor),
        new("--ch-s1", "ChS1", "曲线", ChS1Color),
        new("--ch-s2", "ChS2", "曲线", ChS2Color),
        new("--ch-s3", "ChS3", "曲线", ChS3Color),
        new("--ch-mark", "ChMark", "曲线", ChMarkColor),
        new("--ch-limit", "ChLimit", "曲线", ChLimitColor),
        new("--v-line", "VLine", "釜体", VLineColor),
        new("--v-detail", "VDetail", "釜体", VDetailColor),
        new("--v-liq", "VLiq", "釜体", VLiqColor),
        new("--v-liq-run", "VLiqRun", "釜体", VLiqRunColor),
        new("--v-liq-run-line", "VLiqRunLine", "釜体", VLiqRunLineColor),
        new("--v-liq-alarm", "VLiqAlarm", "釜体", VLiqAlarmColor),
        new("--v-line-alarm", "VLineAlarm", "釜体", VLineAlarmColor),
    };

    /// <summary>杂项（PR.PALETTE_MISC）：审计时也算合法颜色；ShadowOnly = 只许出现在阴影里。</summary>
    public static readonly IReadOnlyList<Token> Misc = new Token[]
    {
        new("--toast", "Toast", "杂项", Color.FromRgb(42, 42, 48), ShadowOnly: false),
        new("--overlay", "Overlay", "杂项", Color.FromRgb(30, 30, 38), ShadowOnly: false),
        new("--shadow-card", "ShadowCard", "杂项", Color.FromRgb(40, 40, 50), ShadowOnly: true),
        new("--shadow-dlg", "ShadowDlg", "杂项", Color.FromRgb(0, 0, 0), ShadowOnly: true),
    };
    // END GENERATED COLORS

    /// <summary>令牌按 CSS 名查找（「--bg-app」或「bg-app」）。</summary>
    public static Token? Find(string css)
    {
        var name = css.StartsWith("--", StringComparison.Ordinal) ? css : "--" + css;
        return Tokens.FirstOrDefault(t => t.Css == name) ?? Misc.FirstOrDefault(t => t.Css == name);
    }

    /// <summary>
    /// 与 MOCK PR.debug.palette 相同的判定：按 RGB 找最近的令牌（透明度变体视为同一令牌），最大通道差 ≤ 6 算合法。
    /// shadow = 这个颜色来自阴影（只在阴影里才允许 shadow-card / shadow-dlg）。
    /// </summary>
    public static (Token Token, int Delta) Nearest(Color c, bool shadow = false)
    {
        Token? best = null; int bestD = int.MaxValue;
        foreach (var t in Tokens.Concat(Misc))
        {
            if (t.ShadowOnly && !shadow) continue;
            int d = Math.Max(Math.Abs(c.R - t.Color.R), Math.Max(Math.Abs(c.G - t.Color.G), Math.Abs(c.B - t.Color.B)));
            if (d < bestD) { bestD = d; best = t; }
        }
        return (best!, bestD);
    }

    /// <summary>合法 = 与某个令牌的最大通道差 ≤ 6（PR.debug.palette 的阈值）。</summary>
    public const int MaxDelta = 6;

    private static readonly ConcurrentDictionary<(uint Color, double Width, string Dash, PenLineCap Cap), ImmutablePen> Pens = new();

    /// <summary>
    /// 缓存的不可变画笔（按颜色、线宽、虚线、线帽）。dash 用 CSS 写法「7 4」（单位 = 线宽的倍数，Avalonia 的 DashStyle 语义），null = 实线。
    /// </summary>
    public static ImmutablePen Pen(Color color, double width = 1, string? dash = null, PenLineCap cap = PenLineCap.Flat)
        => Pens.GetOrAdd((color.ToUInt32(), width, dash ?? "", cap), k =>
        {
            ImmutableDashStyle? ds = null;
            if (!string.IsNullOrWhiteSpace(k.Dash))
                ds = new ImmutableDashStyle(k.Dash.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)), 0);
            return new ImmutablePen(new ImmutableSolidColorBrush(Color.FromUInt32(k.Color)), k.Width, ds, k.Cap, PenLineJoin.Round);
        });

    /// <summary>令牌颜色 + 透明度（PR.tokA）：只改 alpha，RGB 仍是令牌。</summary>
    public static Color WithAlpha(Color c, double alpha) => new((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);
}
