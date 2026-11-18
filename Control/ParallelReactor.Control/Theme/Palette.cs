using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// 色板的 C# 镜像（融合版 F1）：与 Styles/Tokens.axaml 同名（BgAppColor / BgAppBrush …），两边都由 tools/gen-tokens.py
/// 从 tools/fusion-tokens.json（样稿 research/main-fusion.html 抽出的最终值）生成。自绘控件只从这里取颜色、画刷和画笔；
/// 别处不许写颜色字面量（PaletteLintTests）。语义画刷（PaneLineBrush、RunRowSelBrush …）只是色板颜色的别名。
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

    // BEGIN GENERATED COLORS（Control/tools/gen-tokens.py ← tools/fusion-tokens.json（样稿 research/main-fusion.html）；不要手改，改 JSON 后重新生成）
    // 底色
    public static readonly Color BgAppColor = Rgb(0xeceef1);   // --bg-app
    public static readonly Color BgPanelColor = Rgb(0xf7f8f9);   // --bg-panel
    public static readonly Color BgHeadColor = Rgb(0xfbfbfc);   // --bg-head
    public static readonly Color BgCardColor = Rgb(0xffffff);   // --bg-card
    public static readonly Color BgSideColor = Rgb(0xf3f4f6);   // --bg-side
    public static readonly Color BgSunkenColor = Rgb(0xf6f7f9);   // --bg-sunken
    public static readonly Color BgHoverColor = Rgb(0xf7f9fc);   // --bg-hover
    public static readonly Color BgPressColor = Rgb(0xeceef1);   // --bg-press
    public static readonly Color BgBarColor = Rgb(0xf0f0f0);   // --bg-bar
    // 线
    public static readonly Color LineStrongColor = Rgb(0xb9b9b9);   // --line-strong
    public static readonly Color LineColor = Rgb(0xd9dce1);   // --line
    public static readonly Color Line2Color = Rgb(0xc9ced6);   // --line-2
    public static readonly Color LineSoftColor = Rgb(0xe3e5e9);   // --line-soft
    public static readonly Color LineFaintColor = Rgb(0xf0f1f3);   // --line-faint
    public static readonly Color LineMenuColor = Rgb(0xdedede);   // --line-menu
    public static readonly Color LineFirmColor = Rgb(0xa8a8a8);   // --line-firm
    public static readonly Color LineSepColor = Rgb(0xcfcfcf);   // --line-sep
    public static readonly Color LineGreyColor = Rgb(0xd6d6d6);   // --line-grey
    // 文字
    public static readonly Color T1Color = Rgb(0x1f2329);   // --t-1
    public static readonly Color T2Color = Rgb(0x4a505a);   // --t-2
    public static readonly Color T3Color = Rgb(0x6b7280);   // --t-3
    public static readonly Color T4Color = Rgb(0x9097a1);   // --t-4
    public static readonly Color TDisColor = Rgb(0x9aa1ac);   // --t-dis
    public static readonly Color TInvColor = Rgb(0xffffff);   // --t-inv
    public static readonly Color TIconColor = Rgb(0x8a909a);   // --t-icon
    public static readonly Color TNoneColor = Rgb(0xb4bac3);   // --t-none
    public static readonly Color TSepColor = Rgb(0xc3c8cf);   // --t-sep
    // GBG 框架
    public static readonly Color GbgInkColor = Rgb(0x2b2b2b);   // --gbg-ink
    public static readonly Color GbgTitleColor = Rgb(0x2f2f2f);   // --gbg-title
    public static readonly Color GbgMenuColor = Rgb(0x3b3b3b);   // --gbg-menu
    public static readonly Color GbgIconColor = Rgb(0x4a4a4a);   // --gbg-icon
    public static readonly Color GbgTickColor = Rgb(0x6a6a6a);   // --gbg-tick
    // 品牌蓝
    public static readonly Color AColor = Rgb(0x204797);   // --a
    public static readonly Color ADColor = Rgb(0x173a7e);   // --a-d
    public static readonly Color AInkColor = Rgb(0x24427f);   // --a-ink
    public static readonly Color ASoftColor = Rgb(0xe5ecf7);   // --a-soft
    public static readonly Color ATintColor = Rgb(0xeef3fb);   // --a-tint
    public static readonly Color ALineColor = Rgb(0xc9d4ea);   // --a-line
    public static readonly Color ATabColor = Rgb(0x8fa7d4);   // --a-tab
    public static readonly Color AWashColor = Rgb(0xf3f6fb);   // --a-wash
    public static readonly Color ADoneColor = Rgb(0xb9c7e2);   // --a-done
    public static readonly Color ATrackColor = Rgb(0xdde4f2);   // --a-track
    public static readonly Color AMuteColor = Rgb(0x9fb3da);   // --a-mute
    public static readonly Color Side1Color = Rgb(0x1f3c7c);   // --side-1
    public static readonly Color Side2Color = Rgb(0x172f62);   // --side-2
    public static readonly Color SideLineColor = Rgb(0x172f62);   // --side-line
    public static readonly Color SideInkColor = Rgb(0xeef2fa);   // --side-ink
    public static readonly Color SideInk2Color = Rgb(0xe3e9f6);   // --side-ink-2
    public static readonly Color BrandOrangeColor = Rgb(0xec681e);   // --brand-orange
    // 运行（绿）
    public static readonly Color G1Color = Rgb(0xf2faf3);   // --g-1
    public static readonly Color G2Color = Rgb(0xeef7ef);   // --g-2
    public static readonly Color G3Color = Rgb(0xbfe0c2);   // --g-3
    public static readonly Color G4Color = Rgb(0x9fcfa4);   // --g-4
    public static readonly Color GLineColor = Rgb(0xb9dcbf);   // --g-line
    public static readonly Color GEdgeColor = Rgb(0xb7d9b9);   // --g-edge
    public static readonly Color GDotColor = Rgb(0x2e8b3e);   // --g-dot
    public static readonly Color GDotLineColor = Rgb(0x2e8b3e);   // --g-dot-line
    public static readonly Color GInkColor = Rgb(0x1f5f28);   // --g-ink
    public static readonly Color GInk2Color = Rgb(0x3c7a45);   // --g-ink-2
    // 报警（红）
    public static readonly Color RColor = Rgb(0xc62828);   // --r
    public static readonly Color RInkColor = Rgb(0xb71c1c);   // --r-ink
    public static readonly Color RInk2Color = Rgb(0xb4232f);   // --r-ink-2
    public static readonly Color RBgColor = Rgb(0xfff6f6);   // --r-bg
    public static readonly Color RSoftColor = Rgb(0xfdecec);   // --r-soft
    public static readonly Color RLineColor = Rgb(0xf0c6c6);   // --r-line
    public static readonly Color RTrackColor = Rgb(0xf6d6d6);   // --r-track
    public static readonly Color RBtnColor = Rgb(0xc50f1f);   // --r-btn
    public static readonly Color RBtnDColor = Rgb(0xd63a43);   // --r-btn-d
    // 提醒（琥珀）
    public static readonly Color AmbColor = Rgb(0xc98a00);   // --amb
    public static readonly Color AmbDotColor = Rgb(0xd6a000);   // --amb-dot
    public static readonly Color AmbInkColor = Rgb(0x8a5a00);   // --amb-ink
    public static readonly Color AmbInk2Color = Rgb(0x6b4a00);   // --amb-ink-2
    public static readonly Color AmbBgColor = Rgb(0xfff8e6);   // --amb-bg
    public static readonly Color AmbSoftColor = Rgb(0xfff4dc);   // --amb-soft
    public static readonly Color AmbLineColor = Rgb(0xecd59a);   // --amb-line
    public static readonly Color AmbLine2Color = Rgb(0xe0b252);   // --amb-line-2
    public static readonly Color AmbNoteLineColor = Rgb(0xf0d9a6);   // --amb-note-line
    public static readonly Color AmbEdgeColor = Rgb(0xd4a017);   // --amb-edge
    // 中性状态
    public static readonly Color NDotColor = Rgb(0xb8bec8);   // --n-dot
    public static readonly Color NDotLineColor = Rgb(0xaab1bb);   // --n-dot-line
    // 运行胶囊
    public static readonly Color CapBgColor = Rgb(0x151515);   // --cap-bg
    public static readonly Color CapBtnColor = Rgb(0x4d4d4d);   // --cap-btn
    public static readonly Color CapBtnOffColor = Rgb(0x333333);   // --cap-btn-off
    public static readonly Color CapSubColor = Rgb(0xa9b4c8);   // --cap-sub
    public static readonly Color CapGoColor = Rgb(0x28a745);   // --cap-go
    public static readonly Color CapEsColor = Rgb(0xd32f2f);   // --cap-es
    public static readonly Color CapEsHoverColor = Rgb(0xe53935);   // --cap-es-hover
    // 曲线
    public static readonly Color ChBgColor = Rgb(0xffffff);   // --ch-bg
    public static readonly Color ChGridColor = Rgb(0xf0f1f3);   // --ch-grid
    public static readonly Color ChAxisColor = Rgb(0xc9ced6);   // --ch-axis
    public static readonly Color ChTickColor = Rgb(0x6a6a6a);   // --ch-tick
    public static readonly Color ChTitleColor = Rgb(0x2b2b2b);   // --ch-title
    public static readonly Color ChTColor = Rgb(0xd9662b);   // --ch-t
    public static readonly Color ChTSpColor = Rgb(0x8e9299);   // --ch-t-sp
    public static readonly Color ChPColor = Rgb(0x204797);   // --ch-p
    public static readonly Color ChPSpColor = Rgb(0x8e9299);   // --ch-p-sp
    public static readonly Color ChS1Color = Rgb(0x4a505a);   // --ch-s1
    public static readonly Color ChS2Color = Rgb(0x8a909a);   // --ch-s2
    public static readonly Color ChS3Color = Rgb(0xb4bac3);   // --ch-s3
    public static readonly Color ChMarkColor = Rgb(0x2b2b2b);   // --ch-mark
    public static readonly Color ChLimitColor = Rgb(0xc62828);   // --ch-limit
    public static readonly Color Ch1Color = Rgb(0x2a78d6);   // --ch-1
    public static readonly Color Ch2Color = Rgb(0xeb6834);   // --ch-2
    public static readonly Color Ch3Color = Rgb(0x1baf7a);   // --ch-3
    public static readonly Color Ch4Color = Rgb(0xeda100);   // --ch-4
    public static readonly Color Ch5Color = Rgb(0xe87ba4);   // --ch-5
    public static readonly Color Ch6Color = Rgb(0x008300);   // --ch-6
    public static readonly Color Ch7Color = Rgb(0x6250d6);   // --ch-7
    public static readonly Color Ch8Color = Rgb(0xa0612b);   // --ch-8
    // 釜体 · 气路
    public static readonly Color VLineColor = Rgb(0xaab4c2);   // --v-line
    public static readonly Color VDetailColor = Rgb(0xb3bcc8);   // --v-detail
    public static readonly Color VLiqColor = Rgb(0xdbe8f8);   // --v-liq
    public static readonly Color VLiq2Color = Rgb(0xafcbee);   // --v-liq-2
    public static readonly Color VLiqRunColor = Rgb(0xdbe8f8);   // --v-liq-run
    public static readonly Color VLiqRunLineColor = Rgb(0x93b4e0);   // --v-liq-run-line
    public static readonly Color VLiqAlarmColor = Rgb(0xfdecec);   // --v-liq-alarm
    public static readonly Color VLineAlarmColor = Rgb(0xc62828);   // --v-line-alarm
    public static readonly Color VShaftColor = Rgb(0x7d8898);   // --v-shaft
    public static readonly Color VCapColor = Rgb(0xc3cad4);   // --v-cap
    public static readonly Color VTbColor = Rgb(0xeef1f5);   // --v-tb
    public static readonly Color VTbLineColor = Rgb(0xd6dce4);   // --v-tb-line
    public static readonly Color VSensorColor = Rgb(0x2b3646);   // --v-sensor
    public static readonly Color VBubColor = Rgb(0x8fb0de);   // --v-bub
    public static readonly Color PipeColor = Rgb(0xc3cbd6);   // --pipe
    public static readonly Color PipeHdrColor = Rgb(0x9aa5b4);   // --pipe-hdr
    public static readonly Color SymColor = Rgb(0x8d98a8);   // --sym
    // 杂项（rgba → #aarrggbb）
    public static readonly Color ToastColor = Argb(0xff2b2b2b);   // --toast
    public static readonly Color OverlayColor = Argb(0x52111827);   // --overlay

    public static readonly IImmutableSolidColorBrush BgAppBrush = new ImmutableSolidColorBrush(BgAppColor);
    public static readonly IImmutableSolidColorBrush BgPanelBrush = new ImmutableSolidColorBrush(BgPanelColor);
    public static readonly IImmutableSolidColorBrush BgHeadBrush = new ImmutableSolidColorBrush(BgHeadColor);
    public static readonly IImmutableSolidColorBrush BgCardBrush = new ImmutableSolidColorBrush(BgCardColor);
    public static readonly IImmutableSolidColorBrush BgSideBrush = new ImmutableSolidColorBrush(BgSideColor);
    public static readonly IImmutableSolidColorBrush BgSunkenBrush = new ImmutableSolidColorBrush(BgSunkenColor);
    public static readonly IImmutableSolidColorBrush BgHoverBrush = new ImmutableSolidColorBrush(BgHoverColor);
    public static readonly IImmutableSolidColorBrush BgPressBrush = new ImmutableSolidColorBrush(BgPressColor);
    public static readonly IImmutableSolidColorBrush BgBarBrush = new ImmutableSolidColorBrush(BgBarColor);
    public static readonly IImmutableSolidColorBrush LineStrongBrush = new ImmutableSolidColorBrush(LineStrongColor);
    public static readonly IImmutableSolidColorBrush LineBrush = new ImmutableSolidColorBrush(LineColor);
    public static readonly IImmutableSolidColorBrush Line2Brush = new ImmutableSolidColorBrush(Line2Color);
    public static readonly IImmutableSolidColorBrush LineSoftBrush = new ImmutableSolidColorBrush(LineSoftColor);
    public static readonly IImmutableSolidColorBrush LineFaintBrush = new ImmutableSolidColorBrush(LineFaintColor);
    public static readonly IImmutableSolidColorBrush LineMenuBrush = new ImmutableSolidColorBrush(LineMenuColor);
    public static readonly IImmutableSolidColorBrush LineFirmBrush = new ImmutableSolidColorBrush(LineFirmColor);
    public static readonly IImmutableSolidColorBrush LineSepBrush = new ImmutableSolidColorBrush(LineSepColor);
    public static readonly IImmutableSolidColorBrush LineGreyBrush = new ImmutableSolidColorBrush(LineGreyColor);
    public static readonly IImmutableSolidColorBrush T1Brush = new ImmutableSolidColorBrush(T1Color);
    public static readonly IImmutableSolidColorBrush T2Brush = new ImmutableSolidColorBrush(T2Color);
    public static readonly IImmutableSolidColorBrush T3Brush = new ImmutableSolidColorBrush(T3Color);
    public static readonly IImmutableSolidColorBrush T4Brush = new ImmutableSolidColorBrush(T4Color);
    public static readonly IImmutableSolidColorBrush TDisBrush = new ImmutableSolidColorBrush(TDisColor);
    public static readonly IImmutableSolidColorBrush TInvBrush = new ImmutableSolidColorBrush(TInvColor);
    public static readonly IImmutableSolidColorBrush TIconBrush = new ImmutableSolidColorBrush(TIconColor);
    public static readonly IImmutableSolidColorBrush TNoneBrush = new ImmutableSolidColorBrush(TNoneColor);
    public static readonly IImmutableSolidColorBrush TSepBrush = new ImmutableSolidColorBrush(TSepColor);
    public static readonly IImmutableSolidColorBrush GbgInkBrush = new ImmutableSolidColorBrush(GbgInkColor);
    public static readonly IImmutableSolidColorBrush GbgTitleBrush = new ImmutableSolidColorBrush(GbgTitleColor);
    public static readonly IImmutableSolidColorBrush GbgMenuBrush = new ImmutableSolidColorBrush(GbgMenuColor);
    public static readonly IImmutableSolidColorBrush GbgIconBrush = new ImmutableSolidColorBrush(GbgIconColor);
    public static readonly IImmutableSolidColorBrush GbgTickBrush = new ImmutableSolidColorBrush(GbgTickColor);
    public static readonly IImmutableSolidColorBrush ABrush = new ImmutableSolidColorBrush(AColor);
    public static readonly IImmutableSolidColorBrush ADBrush = new ImmutableSolidColorBrush(ADColor);
    public static readonly IImmutableSolidColorBrush AInkBrush = new ImmutableSolidColorBrush(AInkColor);
    public static readonly IImmutableSolidColorBrush ASoftBrush = new ImmutableSolidColorBrush(ASoftColor);
    public static readonly IImmutableSolidColorBrush ATintBrush = new ImmutableSolidColorBrush(ATintColor);
    public static readonly IImmutableSolidColorBrush ALineBrush = new ImmutableSolidColorBrush(ALineColor);
    public static readonly IImmutableSolidColorBrush ATabBrush = new ImmutableSolidColorBrush(ATabColor);
    public static readonly IImmutableSolidColorBrush AWashBrush = new ImmutableSolidColorBrush(AWashColor);
    public static readonly IImmutableSolidColorBrush ADoneBrush = new ImmutableSolidColorBrush(ADoneColor);
    public static readonly IImmutableSolidColorBrush ATrackBrush = new ImmutableSolidColorBrush(ATrackColor);
    public static readonly IImmutableSolidColorBrush AMuteBrush = new ImmutableSolidColorBrush(AMuteColor);
    public static readonly IImmutableSolidColorBrush Side1Brush = new ImmutableSolidColorBrush(Side1Color);
    public static readonly IImmutableSolidColorBrush Side2Brush = new ImmutableSolidColorBrush(Side2Color);
    public static readonly IImmutableSolidColorBrush SideLineBrush = new ImmutableSolidColorBrush(SideLineColor);
    public static readonly IImmutableSolidColorBrush SideInkBrush = new ImmutableSolidColorBrush(SideInkColor);
    public static readonly IImmutableSolidColorBrush SideInk2Brush = new ImmutableSolidColorBrush(SideInk2Color);
    public static readonly IImmutableSolidColorBrush BrandOrangeBrush = new ImmutableSolidColorBrush(BrandOrangeColor);
    public static readonly IImmutableSolidColorBrush G1Brush = new ImmutableSolidColorBrush(G1Color);
    public static readonly IImmutableSolidColorBrush G2Brush = new ImmutableSolidColorBrush(G2Color);
    public static readonly IImmutableSolidColorBrush G3Brush = new ImmutableSolidColorBrush(G3Color);
    public static readonly IImmutableSolidColorBrush G4Brush = new ImmutableSolidColorBrush(G4Color);
    public static readonly IImmutableSolidColorBrush GLineBrush = new ImmutableSolidColorBrush(GLineColor);
    public static readonly IImmutableSolidColorBrush GEdgeBrush = new ImmutableSolidColorBrush(GEdgeColor);
    public static readonly IImmutableSolidColorBrush GDotBrush = new ImmutableSolidColorBrush(GDotColor);
    public static readonly IImmutableSolidColorBrush GDotLineBrush = new ImmutableSolidColorBrush(GDotLineColor);
    public static readonly IImmutableSolidColorBrush GInkBrush = new ImmutableSolidColorBrush(GInkColor);
    public static readonly IImmutableSolidColorBrush GInk2Brush = new ImmutableSolidColorBrush(GInk2Color);
    public static readonly IImmutableSolidColorBrush RBrush = new ImmutableSolidColorBrush(RColor);
    public static readonly IImmutableSolidColorBrush RInkBrush = new ImmutableSolidColorBrush(RInkColor);
    public static readonly IImmutableSolidColorBrush RInk2Brush = new ImmutableSolidColorBrush(RInk2Color);
    public static readonly IImmutableSolidColorBrush RBgBrush = new ImmutableSolidColorBrush(RBgColor);
    public static readonly IImmutableSolidColorBrush RSoftBrush = new ImmutableSolidColorBrush(RSoftColor);
    public static readonly IImmutableSolidColorBrush RLineBrush = new ImmutableSolidColorBrush(RLineColor);
    public static readonly IImmutableSolidColorBrush RTrackBrush = new ImmutableSolidColorBrush(RTrackColor);
    public static readonly IImmutableSolidColorBrush RBtnBrush = new ImmutableSolidColorBrush(RBtnColor);
    public static readonly IImmutableSolidColorBrush RBtnDBrush = new ImmutableSolidColorBrush(RBtnDColor);
    public static readonly IImmutableSolidColorBrush AmbBrush = new ImmutableSolidColorBrush(AmbColor);
    public static readonly IImmutableSolidColorBrush AmbDotBrush = new ImmutableSolidColorBrush(AmbDotColor);
    public static readonly IImmutableSolidColorBrush AmbInkBrush = new ImmutableSolidColorBrush(AmbInkColor);
    public static readonly IImmutableSolidColorBrush AmbInk2Brush = new ImmutableSolidColorBrush(AmbInk2Color);
    public static readonly IImmutableSolidColorBrush AmbBgBrush = new ImmutableSolidColorBrush(AmbBgColor);
    public static readonly IImmutableSolidColorBrush AmbSoftBrush = new ImmutableSolidColorBrush(AmbSoftColor);
    public static readonly IImmutableSolidColorBrush AmbLineBrush = new ImmutableSolidColorBrush(AmbLineColor);
    public static readonly IImmutableSolidColorBrush AmbLine2Brush = new ImmutableSolidColorBrush(AmbLine2Color);
    public static readonly IImmutableSolidColorBrush AmbNoteLineBrush = new ImmutableSolidColorBrush(AmbNoteLineColor);
    public static readonly IImmutableSolidColorBrush AmbEdgeBrush = new ImmutableSolidColorBrush(AmbEdgeColor);
    public static readonly IImmutableSolidColorBrush NDotBrush = new ImmutableSolidColorBrush(NDotColor);
    public static readonly IImmutableSolidColorBrush NDotLineBrush = new ImmutableSolidColorBrush(NDotLineColor);
    public static readonly IImmutableSolidColorBrush CapBgBrush = new ImmutableSolidColorBrush(CapBgColor);
    public static readonly IImmutableSolidColorBrush CapBtnBrush = new ImmutableSolidColorBrush(CapBtnColor);
    public static readonly IImmutableSolidColorBrush CapBtnOffBrush = new ImmutableSolidColorBrush(CapBtnOffColor);
    public static readonly IImmutableSolidColorBrush CapSubBrush = new ImmutableSolidColorBrush(CapSubColor);
    public static readonly IImmutableSolidColorBrush CapGoBrush = new ImmutableSolidColorBrush(CapGoColor);
    public static readonly IImmutableSolidColorBrush CapEsBrush = new ImmutableSolidColorBrush(CapEsColor);
    public static readonly IImmutableSolidColorBrush CapEsHoverBrush = new ImmutableSolidColorBrush(CapEsHoverColor);
    public static readonly IImmutableSolidColorBrush ChBgBrush = new ImmutableSolidColorBrush(ChBgColor);
    public static readonly IImmutableSolidColorBrush ChGridBrush = new ImmutableSolidColorBrush(ChGridColor);
    public static readonly IImmutableSolidColorBrush ChAxisBrush = new ImmutableSolidColorBrush(ChAxisColor);
    public static readonly IImmutableSolidColorBrush ChTickBrush = new ImmutableSolidColorBrush(ChTickColor);
    public static readonly IImmutableSolidColorBrush ChTitleBrush = new ImmutableSolidColorBrush(ChTitleColor);
    public static readonly IImmutableSolidColorBrush ChTBrush = new ImmutableSolidColorBrush(ChTColor);
    public static readonly IImmutableSolidColorBrush ChTSpBrush = new ImmutableSolidColorBrush(ChTSpColor);
    public static readonly IImmutableSolidColorBrush ChPBrush = new ImmutableSolidColorBrush(ChPColor);
    public static readonly IImmutableSolidColorBrush ChPSpBrush = new ImmutableSolidColorBrush(ChPSpColor);
    public static readonly IImmutableSolidColorBrush ChS1Brush = new ImmutableSolidColorBrush(ChS1Color);
    public static readonly IImmutableSolidColorBrush ChS2Brush = new ImmutableSolidColorBrush(ChS2Color);
    public static readonly IImmutableSolidColorBrush ChS3Brush = new ImmutableSolidColorBrush(ChS3Color);
    public static readonly IImmutableSolidColorBrush ChMarkBrush = new ImmutableSolidColorBrush(ChMarkColor);
    public static readonly IImmutableSolidColorBrush ChLimitBrush = new ImmutableSolidColorBrush(ChLimitColor);
    public static readonly IImmutableSolidColorBrush Ch1Brush = new ImmutableSolidColorBrush(Ch1Color);
    public static readonly IImmutableSolidColorBrush Ch2Brush = new ImmutableSolidColorBrush(Ch2Color);
    public static readonly IImmutableSolidColorBrush Ch3Brush = new ImmutableSolidColorBrush(Ch3Color);
    public static readonly IImmutableSolidColorBrush Ch4Brush = new ImmutableSolidColorBrush(Ch4Color);
    public static readonly IImmutableSolidColorBrush Ch5Brush = new ImmutableSolidColorBrush(Ch5Color);
    public static readonly IImmutableSolidColorBrush Ch6Brush = new ImmutableSolidColorBrush(Ch6Color);
    public static readonly IImmutableSolidColorBrush Ch7Brush = new ImmutableSolidColorBrush(Ch7Color);
    public static readonly IImmutableSolidColorBrush Ch8Brush = new ImmutableSolidColorBrush(Ch8Color);
    public static readonly IImmutableSolidColorBrush VLineBrush = new ImmutableSolidColorBrush(VLineColor);
    public static readonly IImmutableSolidColorBrush VDetailBrush = new ImmutableSolidColorBrush(VDetailColor);
    public static readonly IImmutableSolidColorBrush VLiqBrush = new ImmutableSolidColorBrush(VLiqColor);
    public static readonly IImmutableSolidColorBrush VLiq2Brush = new ImmutableSolidColorBrush(VLiq2Color);
    public static readonly IImmutableSolidColorBrush VLiqRunBrush = new ImmutableSolidColorBrush(VLiqRunColor);
    public static readonly IImmutableSolidColorBrush VLiqRunLineBrush = new ImmutableSolidColorBrush(VLiqRunLineColor);
    public static readonly IImmutableSolidColorBrush VLiqAlarmBrush = new ImmutableSolidColorBrush(VLiqAlarmColor);
    public static readonly IImmutableSolidColorBrush VLineAlarmBrush = new ImmutableSolidColorBrush(VLineAlarmColor);
    public static readonly IImmutableSolidColorBrush VShaftBrush = new ImmutableSolidColorBrush(VShaftColor);
    public static readonly IImmutableSolidColorBrush VCapBrush = new ImmutableSolidColorBrush(VCapColor);
    public static readonly IImmutableSolidColorBrush VTbBrush = new ImmutableSolidColorBrush(VTbColor);
    public static readonly IImmutableSolidColorBrush VTbLineBrush = new ImmutableSolidColorBrush(VTbLineColor);
    public static readonly IImmutableSolidColorBrush VSensorBrush = new ImmutableSolidColorBrush(VSensorColor);
    public static readonly IImmutableSolidColorBrush VBubBrush = new ImmutableSolidColorBrush(VBubColor);
    public static readonly IImmutableSolidColorBrush PipeBrush = new ImmutableSolidColorBrush(PipeColor);
    public static readonly IImmutableSolidColorBrush PipeHdrBrush = new ImmutableSolidColorBrush(PipeHdrColor);
    public static readonly IImmutableSolidColorBrush SymBrush = new ImmutableSolidColorBrush(SymColor);
    public static readonly IImmutableSolidColorBrush ToastBrush = new ImmutableSolidColorBrush(ToastColor);
    public static readonly IImmutableSolidColorBrush OverlayBrush = new ImmutableSolidColorBrush(OverlayColor);
    /// <summary>焦点：边框 = --a；焦点环 = --a × 0.12。</summary>
    public static readonly IImmutableSolidColorBrush FocusBrush = ABrush;
    public static readonly IImmutableSolidColorBrush FocusRingBrush = new ImmutableSolidColorBrush(AColor, 0.12);

    // 语义画刷（新概念）：只指向色板里的颜色
    public static readonly Color PaneBgColor = BgCardColor;   // 面板 · .pwrap 底
    public static readonly IImmutableSolidColorBrush PaneBgBrush = BgCardBrush;
    public static readonly Color PaneLineColor = LineColor;   // 面板 · .pwrap 边框 1 px、圆角 4
    public static readonly IImmutableSolidColorBrush PaneLineBrush = LineBrush;
    public static readonly Color PaneHeadColor = BgHeadColor;   // 面板 · .ptool / .ph 面板工具条
    public static readonly IImmutableSolidColorBrush PaneHeadBrush = BgHeadBrush;
    public static readonly Color PaneHeadLineColor = LineSoftColor;   // 面板 · .ptool 下边线
    public static readonly IImmutableSolidColorBrush PaneHeadLineBrush = LineSoftBrush;
    public static readonly Color PaneTitleColor = T1Color;   // 面板 · .ph .pt 面板标题 15/600
    public static readonly IImmutableSolidColorBrush PaneTitleBrush = T1Brush;
    public static readonly Color DockBgColor = BgAppColor;   // 面板 · .dock 停靠区底
    public static readonly IImmutableSolidColorBrush DockBgBrush = BgAppBrush;
    public static readonly Color RailBgColor = BgPanelColor;   // 竖条 · .prail 面板左侧竖条
    public static readonly IImmutableSolidColorBrush RailBgBrush = BgPanelBrush;
    public static readonly Color RailLineColor = LineSoftColor;   // 竖条 · .prail 右边线
    public static readonly IImmutableSolidColorBrush RailLineBrush = LineSoftBrush;
    public static readonly Color RailInkColor = T2Color;   // 竖条 · .rt / .rl 竖排面板名
    public static readonly IImmutableSolidColorBrush RailInkBrush = T2Brush;
    public static readonly Color RailBtnColor = TIconColor;   // 竖条 · .rx 折叠圆钮的圈
    public static readonly IImmutableSolidColorBrush RailBtnBrush = TIconBrush;
    public static readonly Color RibbonBgColor = BgCardColor;   // 工具条 · .tbar 工具条底（细节打磨改白）
    public static readonly IImmutableSolidColorBrush RibbonBgBrush = BgCardBrush;
    public static readonly Color RibbonLineColor = LineSoftColor;   // 工具条 · .tbar 下边线
    public static readonly IImmutableSolidColorBrush RibbonLineBrush = LineSoftBrush;
    public static readonly Color RibbonBtnLineColor = LineStrongColor;   // 工具条 · .rb 工具条按钮边框
    public static readonly IImmutableSolidColorBrush RibbonBtnLineBrush = LineStrongBrush;
    public static readonly Color RibbonSepColor = LineSepColor;   // 工具条 · .tsep / .rsep 竖分隔
    public static readonly IImmutableSolidColorBrush RibbonSepBrush = LineSepBrush;
    public static readonly Color RibbonIconColor = GbgIconColor;   // 工具条 · .rb .ic 工具条图标
    public static readonly IImmutableSolidColorBrush RibbonIconBrush = GbgIconBrush;
    public static readonly Color StripInkColor = SideInkColor;   // 标题条 · .tb-title 窗口内深蓝条的文字
    public static readonly IImmutableSolidColorBrush StripInkBrush = SideInkBrush;
    public static readonly Color MenuInkColor = GbgMenuColor;   // 菜单栏 · .mi 菜单项 24/300
    public static readonly IImmutableSolidColorBrush MenuInkBrush = GbgMenuBrush;
    public static readonly Color MenuOnColor = AColor;   // 菜单栏 · .mi.on 当前页
    public static readonly IImmutableSolidColorBrush MenuOnBrush = ABrush;
    public static readonly Color MenuBadgeColor = RColor;   // 菜单栏 · .mi .mb 报警数徽标
    public static readonly IImmutableSolidColorBrush MenuBadgeBrush = RBrush;
    public static readonly Color RunHeadInkColor = T3Color;   // 运行表 · .rtab th 13/500
    public static readonly IImmutableSolidColorBrush RunHeadInkBrush = T3Brush;
    public static readonly Color RunSubColor = T4Color;   // 运行表 · .rtab th small / u 单位
    public static readonly IImmutableSolidColorBrush RunSubBrush = T4Brush;
    public static readonly Color RunRowLineColor = LineFaintColor;   // 运行表 · .rtab td 行线
    public static readonly IImmutableSolidColorBrush RunRowLineBrush = LineFaintBrush;
    public static readonly Color RunRowHoverColor = BgHoverColor;   // 运行表 · .rtab tr:hover
    public static readonly IImmutableSolidColorBrush RunRowHoverBrush = BgHoverBrush;
    public static readonly Color RunRowSelColor = ATintColor;   // 运行表 · .rtab tr.on + 左侧 3 px 品牌蓝
    public static readonly IImmutableSolidColorBrush RunRowSelBrush = ATintBrush;
    public static readonly Color RunRowErrColor = RBgColor;   // 运行表 · .rtab tr.e + 左侧 3 px 红
    public static readonly IImmutableSolidColorBrush RunRowErrBrush = RBgBrush;
    public static readonly Color RunRowErrSelColor = RSoftColor;   // 运行表 · .rtab tr.e.on
    public static readonly IImmutableSolidColorBrush RunRowErrSelBrush = RSoftBrush;
    public static readonly Color RunStepDoneColor = ADoneColor;   // 运行表 · .pgb i.done
    public static readonly IImmutableSolidColorBrush RunStepDoneBrush = ADoneBrush;
    public static readonly Color RunStepTrackColor = ATrackColor;   // 运行表 · .pgb i.cur 轨道
    public static readonly IImmutableSolidColorBrush RunStepTrackBrush = ATrackBrush;
    public static readonly Color RunStepNextColor = BgPressColor;   // 运行表 · .pgb i 未到的步
    public static readonly IImmutableSolidColorBrush RunStepNextBrush = BgPressBrush;
    public static readonly Color RunStepFillColor = AColor;   // 运行表 · .pgb i.cur b 进度
    public static readonly IImmutableSolidColorBrush RunStepFillBrush = ABrush;
    public static readonly Color ApopEdgeColor = RColor;   // 报警弹窗 · .apop 顶边 4 px
    public static readonly IImmutableSolidColorBrush ApopEdgeBrush = RBrush;
    public static readonly Color ApopIconBgColor = RSoftColor;   // 报警弹窗 · .apop .ai
    public static readonly IImmutableSolidColorBrush ApopIconBgBrush = RSoftBrush;
    public static readonly Color ApopBoxColor = BgHeadColor;   // 报警弹窗 · .apop .avs > div 读数块底 #fafbfc
    public static readonly IImmutableSolidColorBrush ApopBoxBrush = BgHeadBrush;
    public static readonly Color ApopBoxLineColor = LineSoftColor;   // 报警弹窗 · .apop .avs > div 边 #e6e8eb
    public static readonly IImmutableSolidColorBrush ApopBoxLineBrush = LineSoftBrush;
    public static readonly Color ApopBadColor = RBgColor;   // 报警弹窗 · .apop .avs .bad 底
    public static readonly IImmutableSolidColorBrush ApopBadBrush = RBgBrush;
    public static readonly Color ApopBadLineColor = RLineColor;   // 报警弹窗 · .apop .avs .bad 边
    public static readonly IImmutableSolidColorBrush ApopBadLineBrush = RLineBrush;
    public static readonly Color ApopInfoColor = AWashColor;   // 报警弹窗 · .apop .aauto 自动处理说明块
    public static readonly IImmutableSolidColorBrush ApopInfoBrush = AWashBrush;
    public static readonly Color ApopFootColor = BgHeadColor;   // 报警弹窗 · .apop .af 底栏
    public static readonly IImmutableSolidColorBrush ApopFootBrush = BgHeadBrush;
    public static readonly Color ToggleOffColor = LineColor;   // 开关 · .tgl 关 #d5dae2
    public static readonly IImmutableSolidColorBrush ToggleOffBrush = LineBrush;
    public static readonly Color ToggleOnColor = AColor;   // 开关 · .tgl.on
    public static readonly IImmutableSolidColorBrush ToggleOnBrush = ABrush;
    public static readonly Color ToggleKnobColor = BgCardColor;   // 开关 · .tgl::after 圆钮
    public static readonly IImmutableSolidColorBrush ToggleKnobBrush = BgCardBrush;
    public static readonly Color ScrollTrackColor = BgBarColor;   // 滚动条 · ::-webkit-scrollbar #f0f0f0
    public static readonly IImmutableSolidColorBrush ScrollTrackBrush = BgBarBrush;
    public static readonly Color ScrollThumbColor = LineSepColor;   // 滚动条 · ::-webkit-scrollbar-thumb #cdcdcd
    public static readonly IImmutableSolidColorBrush ScrollThumbBrush = LineSepBrush;
    public static readonly Color ScrollThumbHoverColor = LineFirmColor;   // 滚动条 · ::-webkit-scrollbar-thumb:hover #a6a6a6
    public static readonly IImmutableSolidColorBrush ScrollThumbHoverBrush = LineFirmBrush;

    public static readonly BoxShadows ShadowCard = BoxShadows.Parse("0 1 2 0 #0a101828");   // --shadow-card
    public static readonly BoxShadows ShadowCardHover = BoxShadows.Parse("0 2 10 0 #1c101828");   // --shadow-card-hover
    public static readonly BoxShadows ShadowStep = BoxShadows.Parse("0 1 5 0 #12101828");   // --shadow-step
    public static readonly BoxShadows ShadowPop = BoxShadows.Parse("0 10 30 0 #33000000");   // --shadow-pop
    public static readonly BoxShadows ShadowDlg = BoxShadows.Parse("0 24 60 0 #47111827, 0 2 8 0 #1f111827");   // --shadow-dlg
    public static readonly BoxShadows ShadowModal = BoxShadows.Parse("0 10 30 0 #38142032");   // --shadow-modal
    public static readonly BoxShadows ShadowFloat = BoxShadows.Parse("0 10 32 0 #38000000");   // --shadow-float
    public static readonly BoxShadows ShadowTip = BoxShadows.Parse("0 4 14 0 #24000000");   // --shadow-tip
    public static readonly BoxShadows ShadowToast = BoxShadows.Parse("0 6 18 0 #29142032");   // --shadow-toast
    public static readonly BoxShadows ShadowKnob = BoxShadows.Parse("0 1 2 0 #33000000");   // --shadow-knob
    public static readonly BoxShadows ShadowDotRun = BoxShadows.Parse("0 0 0 3 #242e8b3e");   // --shadow-dot-run
    public static readonly BoxShadows ShadowDotErr = BoxShadows.Parse("0 0 0 3 #29c62828");   // --shadow-dot-err
    public static readonly BoxShadows FocusRingShadow = BoxShadows.Parse("0 0 0 3 #1f204797");

    /// <summary>126 个颜色令牌（封闭色板，顺序同 fusion-tokens.json），分组同组件陈列页的色板。</summary>
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
        new("--bg-bar", "BgBar", "底色", BgBarColor),
        new("--line-strong", "LineStrong", "线", LineStrongColor),
        new("--line", "Line", "线", LineColor),
        new("--line-2", "Line2", "线", Line2Color),
        new("--line-soft", "LineSoft", "线", LineSoftColor),
        new("--line-faint", "LineFaint", "线", LineFaintColor),
        new("--line-menu", "LineMenu", "线", LineMenuColor),
        new("--line-firm", "LineFirm", "线", LineFirmColor),
        new("--line-sep", "LineSep", "线", LineSepColor),
        new("--line-grey", "LineGrey", "线", LineGreyColor),
        new("--t-1", "T1", "文字", T1Color),
        new("--t-2", "T2", "文字", T2Color),
        new("--t-3", "T3", "文字", T3Color),
        new("--t-4", "T4", "文字", T4Color),
        new("--t-dis", "TDis", "文字", TDisColor),
        new("--t-inv", "TInv", "文字", TInvColor),
        new("--t-icon", "TIcon", "文字", TIconColor),
        new("--t-none", "TNone", "文字", TNoneColor),
        new("--t-sep", "TSep", "文字", TSepColor),
        new("--gbg-ink", "GbgInk", "GBG 框架", GbgInkColor),
        new("--gbg-title", "GbgTitle", "GBG 框架", GbgTitleColor),
        new("--gbg-menu", "GbgMenu", "GBG 框架", GbgMenuColor),
        new("--gbg-icon", "GbgIcon", "GBG 框架", GbgIconColor),
        new("--gbg-tick", "GbgTick", "GBG 框架", GbgTickColor),
        new("--a", "A", "品牌蓝", AColor),
        new("--a-d", "AD", "品牌蓝", ADColor),
        new("--a-ink", "AInk", "品牌蓝", AInkColor),
        new("--a-soft", "ASoft", "品牌蓝", ASoftColor),
        new("--a-tint", "ATint", "品牌蓝", ATintColor),
        new("--a-line", "ALine", "品牌蓝", ALineColor),
        new("--a-tab", "ATab", "品牌蓝", ATabColor),
        new("--a-wash", "AWash", "品牌蓝", AWashColor),
        new("--a-done", "ADone", "品牌蓝", ADoneColor),
        new("--a-track", "ATrack", "品牌蓝", ATrackColor),
        new("--a-mute", "AMute", "品牌蓝", AMuteColor),
        new("--side-1", "Side1", "品牌蓝", Side1Color),
        new("--side-2", "Side2", "品牌蓝", Side2Color),
        new("--side-line", "SideLine", "品牌蓝", SideLineColor),
        new("--side-ink", "SideInk", "品牌蓝", SideInkColor),
        new("--side-ink-2", "SideInk2", "品牌蓝", SideInk2Color),
        new("--brand-orange", "BrandOrange", "品牌蓝", BrandOrangeColor),
        new("--g-1", "G1", "运行（绿）", G1Color),
        new("--g-2", "G2", "运行（绿）", G2Color),
        new("--g-3", "G3", "运行（绿）", G3Color),
        new("--g-4", "G4", "运行（绿）", G4Color),
        new("--g-line", "GLine", "运行（绿）", GLineColor),
        new("--g-edge", "GEdge", "运行（绿）", GEdgeColor),
        new("--g-dot", "GDot", "运行（绿）", GDotColor),
        new("--g-dot-line", "GDotLine", "运行（绿）", GDotLineColor),
        new("--g-ink", "GInk", "运行（绿）", GInkColor),
        new("--g-ink-2", "GInk2", "运行（绿）", GInk2Color),
        new("--r", "R", "报警（红）", RColor),
        new("--r-ink", "RInk", "报警（红）", RInkColor),
        new("--r-ink-2", "RInk2", "报警（红）", RInk2Color),
        new("--r-bg", "RBg", "报警（红）", RBgColor),
        new("--r-soft", "RSoft", "报警（红）", RSoftColor),
        new("--r-line", "RLine", "报警（红）", RLineColor),
        new("--r-track", "RTrack", "报警（红）", RTrackColor),
        new("--r-btn", "RBtn", "报警（红）", RBtnColor),
        new("--r-btn-d", "RBtnD", "报警（红）", RBtnDColor),
        new("--amb", "Amb", "提醒（琥珀）", AmbColor),
        new("--amb-dot", "AmbDot", "提醒（琥珀）", AmbDotColor),
        new("--amb-ink", "AmbInk", "提醒（琥珀）", AmbInkColor),
        new("--amb-ink-2", "AmbInk2", "提醒（琥珀）", AmbInk2Color),
        new("--amb-bg", "AmbBg", "提醒（琥珀）", AmbBgColor),
        new("--amb-soft", "AmbSoft", "提醒（琥珀）", AmbSoftColor),
        new("--amb-line", "AmbLine", "提醒（琥珀）", AmbLineColor),
        new("--amb-line-2", "AmbLine2", "提醒（琥珀）", AmbLine2Color),
        new("--amb-note-line", "AmbNoteLine", "提醒（琥珀）", AmbNoteLineColor),
        new("--amb-edge", "AmbEdge", "提醒（琥珀）", AmbEdgeColor),
        new("--n-dot", "NDot", "中性状态", NDotColor),
        new("--n-dot-line", "NDotLine", "中性状态", NDotLineColor),
        new("--cap-bg", "CapBg", "运行胶囊", CapBgColor),
        new("--cap-btn", "CapBtn", "运行胶囊", CapBtnColor),
        new("--cap-btn-off", "CapBtnOff", "运行胶囊", CapBtnOffColor),
        new("--cap-sub", "CapSub", "运行胶囊", CapSubColor),
        new("--cap-go", "CapGo", "运行胶囊", CapGoColor),
        new("--cap-es", "CapEs", "运行胶囊", CapEsColor),
        new("--cap-es-hover", "CapEsHover", "运行胶囊", CapEsHoverColor),
        new("--ch-bg", "ChBg", "曲线", ChBgColor),
        new("--ch-grid", "ChGrid", "曲线", ChGridColor),
        new("--ch-axis", "ChAxis", "曲线", ChAxisColor),
        new("--ch-tick", "ChTick", "曲线", ChTickColor),
        new("--ch-title", "ChTitle", "曲线", ChTitleColor),
        new("--ch-t", "ChT", "曲线", ChTColor),
        new("--ch-t-sp", "ChTSp", "曲线", ChTSpColor),
        new("--ch-p", "ChP", "曲线", ChPColor),
        new("--ch-p-sp", "ChPSp", "曲线", ChPSpColor),
        new("--ch-s1", "ChS1", "曲线", ChS1Color),
        new("--ch-s2", "ChS2", "曲线", ChS2Color),
        new("--ch-s3", "ChS3", "曲线", ChS3Color),
        new("--ch-mark", "ChMark", "曲线", ChMarkColor),
        new("--ch-limit", "ChLimit", "曲线", ChLimitColor),
        new("--ch-1", "Ch1", "曲线", Ch1Color),
        new("--ch-2", "Ch2", "曲线", Ch2Color),
        new("--ch-3", "Ch3", "曲线", Ch3Color),
        new("--ch-4", "Ch4", "曲线", Ch4Color),
        new("--ch-5", "Ch5", "曲线", Ch5Color),
        new("--ch-6", "Ch6", "曲线", Ch6Color),
        new("--ch-7", "Ch7", "曲线", Ch7Color),
        new("--ch-8", "Ch8", "曲线", Ch8Color),
        new("--v-line", "VLine", "釜体 · 气路", VLineColor),
        new("--v-detail", "VDetail", "釜体 · 气路", VDetailColor),
        new("--v-liq", "VLiq", "釜体 · 气路", VLiqColor),
        new("--v-liq-2", "VLiq2", "釜体 · 气路", VLiq2Color),
        new("--v-liq-run", "VLiqRun", "釜体 · 气路", VLiqRunColor),
        new("--v-liq-run-line", "VLiqRunLine", "釜体 · 气路", VLiqRunLineColor),
        new("--v-liq-alarm", "VLiqAlarm", "釜体 · 气路", VLiqAlarmColor),
        new("--v-line-alarm", "VLineAlarm", "釜体 · 气路", VLineAlarmColor),
        new("--v-shaft", "VShaft", "釜体 · 气路", VShaftColor),
        new("--v-cap", "VCap", "釜体 · 气路", VCapColor),
        new("--v-tb", "VTb", "釜体 · 气路", VTbColor),
        new("--v-tb-line", "VTbLine", "釜体 · 气路", VTbLineColor),
        new("--v-sensor", "VSensor", "釜体 · 气路", VSensorColor),
        new("--v-bub", "VBub", "釜体 · 气路", VBubColor),
        new("--pipe", "Pipe", "釜体 · 气路", PipeColor),
        new("--pipe-hdr", "PipeHdr", "釜体 · 气路", PipeHdrColor),
        new("--sym", "Sym", "釜体 · 气路", SymColor),
    };

    /// <summary>杂项：审计时也算合法颜色；ShadowOnly = 只许出现在阴影里。</summary>
    public static readonly IReadOnlyList<Token> Misc = new Token[]
    {
        new("--toast", "Toast", "杂项", Rgb(0x2b2b2b), ShadowOnly: false),
        new("--overlay", "Overlay", "杂项", Rgb(0x111827), ShadowOnly: false),
        new("--shadow-card", "ShadowCard", "杂项", Rgb(0x101828), ShadowOnly: true),
        new("--shadow-pop", "ShadowPop", "杂项", Rgb(0x000000), ShadowOnly: true),
        new("--shadow-modal", "ShadowModal", "杂项", Rgb(0x142032), ShadowOnly: true),
        new("--shadow-dot-run", "ShadowDotRun", "杂项", Rgb(0x2e8b3e), ShadowOnly: true),
        new("--shadow-dot-err", "ShadowDotErr", "杂项", Rgb(0xc62828), ShadowOnly: true),
    };
    // END GENERATED COLORS

    /// <summary>令牌按 CSS 名查找（「--bg-app」或「bg-app」）。</summary>
    public static Token? Find(string css)
    {
        var name = css.StartsWith("--", StringComparison.Ordinal) ? css : "--" + css;
        return Tokens.FirstOrDefault(t => t.Css == name) ?? Misc.FirstOrDefault(t => t.Css == name);
    }

    /// <summary>
    /// 与 pc-v3 样稿 PR.debug.palette 相同的判定：按 RGB 找最近的令牌（透明度变体视为同一令牌），最大通道差 ≤ 6 算合法。
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
