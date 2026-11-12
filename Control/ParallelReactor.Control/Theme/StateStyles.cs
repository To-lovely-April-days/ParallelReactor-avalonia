using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Theme;

/// <summary>
/// 状态 → 样式（MOCK js/models.js 的 PR.ST，PORT-PLAN §4.5 / RESTYLE-BRIEF §2.1）。状态靠「字」区分，不靠色相：
/// 运行类浅绿点 + 正文色字；报警红点 + 红字 600；停用 / 已启用 / 已停止灰点 + 次要字；已结束灰 ✓；离线空心灰圈；未安装无点。
/// 键是 Core 的 <see cref="StateKey"/>（类别 <see cref="StateKind"/>、默认状态字都来自 Core 的 StateKeys）；
/// 也接受样稿的状态代码字符串（"heat" …，样式类名、演示数据用）。颜色全部取自 Palette。
/// </summary>
public static class StateStyles
{
    /// <summary>
    /// 一种状态的全部样式。画刷为 null = 透明 / 不画。
    /// </summary>
    /// <param name="Key">状态键。</param>
    /// <param name="Code">样稿的状态代码（"heat" …）。</param>
    /// <param name="Zh">默认状态字（「反应中」；报警的实际字由 Core StateMap 按原因给出）。</param>
    /// <param name="Kind">样式类别。</param>
    /// <param name="Dot">状态点填充（9 px，占 13 px 槽位）；null = 不画（未安装、离线是空心圈）。</param>
    /// <param name="DotLine">状态点描边（内侧 1 px；离线 1.5 px 空心圈）。</param>
    /// <param name="DotLineWidth">描边宽度。</param>
    /// <param name="DotCheck">已结束：状态点位置画 13 px ✓ 图标（t-3，描边 2.2）代替圆点（ui.keyDot）；Dot 仍是 n-dot（PR.ST 的数据）。</param>
    /// <param name="Word">状态字颜色。</param>
    /// <param name="WordWeight">状态字字重（报警 600，其余 400）。状态字绝不省略。</param>
    /// <param name="Bg">浅底（少用：徽标底色）。</param>
    /// <param name="VesselLine">釜体轮廓。</param>
    /// <param name="Liquid">釜内液体填充。</param>
    /// <param name="LiquidLine">液面线。</param>
    /// <param name="LiquidHatch">液体画灰色斜纹（离线：bg-sunken 底 + line-soft 斜线）。</param>
    /// <param name="VesselDashed">釜体只画虚线轮廓（未安装）。</param>
    /// <param name="CardBorder">卡片边框的特殊颜色（报警 r-line）；null = 卡片默认边框。</param>
    /// <param name="CardDashed">卡片画成 line-2 虚线块（未安装）。</param>
    /// <param name="Reading">卡片上读数的颜色（离线 t-3，其余 t-1）。</param>
    public sealed record Style(
        StateKey Key, string Code, string Zh, StateKind Kind,
        IImmutableSolidColorBrush? Dot, IImmutableSolidColorBrush? DotLine, double DotLineWidth, bool DotCheck,
        IImmutableSolidColorBrush Word, FontWeight WordWeight, IImmutableSolidColorBrush? Bg,
        IImmutableSolidColorBrush VesselLine, IImmutableSolidColorBrush? Liquid, IImmutableSolidColorBrush? LiquidLine, bool LiquidHatch, bool VesselDashed,
        IImmutableSolidColorBrush? CardBorder, bool CardDashed, IImmutableSolidColorBrush Reading)
    {
        /// <summary>占用通道（PR.ST.RUNNING：运行类 + 报警）。</summary>
        public bool IsRunning => StateKeys.IsRunning(Key);
        /// <summary>空心圈（离线）。</summary>
        public bool DotHollow => Kind == StateKind.Offline;
    }

    private static Style Make(StateKey key)
    {
        var code = StateKeys.Code(key);
        var zh = StateKeys.Word(key);
        var kind = StateKeys.KindOf(key);
        return kind switch
        {
            StateKind.Run => new(key, code, zh, kind,
                Palette.GDotBrush, Palette.GDotLineBrush, 1, false,
                Palette.T2Brush, FontWeight.Normal, Palette.BgCardBrush,
                Palette.VLineBrush, Palette.VLiqRunBrush, Palette.VLiqRunLineBrush, false, false,
                null, false, Palette.T1Brush),
            StateKind.Alarm => new(key, code, zh, kind,
                Palette.RBrush, Palette.RBrush, 1, false,
                Palette.RInkBrush, FontWeight.SemiBold, Palette.RBgBrush,
                Palette.VLineAlarmBrush, Palette.VLiqAlarmBrush, Palette.VLineAlarmBrush, false, false,
                Palette.RLineBrush, false, Palette.T1Brush),
            StateKind.Uninstalled => new(key, code, zh, kind,
                null, null, 0, false,
                Palette.T4Brush, FontWeight.Normal, null,
                Palette.LineBrush, null, null, false, true,
                Palette.Line2Brush, true, Palette.T4Brush),
            StateKind.Offline => new(key, code, zh, kind,
                null, Palette.NDotLineBrush, 1.5, false,
                Palette.T3Brush, FontWeight.Normal, Palette.BgSunkenBrush,
                Palette.VDetailBrush, Palette.VLiqBrush, Palette.VDetailBrush, true, false,
                null, false, Palette.T3Brush),
            // 停用 / 已启用 / 已停止 / 已结束：中性灰点（已结束画 ✓）；红色只给报警
            _ => new(key, code, zh, kind,
                Palette.NDotBrush, Palette.NDotLineBrush, 1, kind == StateKind.Done,
                Palette.T3Brush, FontWeight.Normal, Palette.BgSunkenBrush,
                Palette.VLineBrush, Palette.VLiqBrush, Palette.VDetailBrush, false, false,
                null, false, Palette.T1Brush),
        };
    }

    private static readonly Style[] ByKey = StateKeys.All.Select(Make).ToArray();

    /// <summary>全部状态键（陈列页、测试用；顺序同 PR.ST）。</summary>
    public static IReadOnlyList<StateKey> Keys => StateKeys.All;

    /// <summary>占用通道的状态（PR.ST.RUNNING）。</summary>
    public static IReadOnlyList<StateKey> RunningKeys => StateKeys.Running;

    /// <summary>按键取样式。</summary>
    public static Style Get(StateKey key) => ByKey[(int)key];

    /// <summary>按样稿的状态代码取样式（"heat" …）；未知代码按「停用」处理（同 PR.stVars）。</summary>
    public static Style Get(string? code) => StateKeys.TryParse(code, out var key) ? Get(key) : Get(StateKey.Idle);

    /// <summary>状态字：离线且从未连接过时写「未连接」（§4.5）。</summary>
    public static string Word(StateKey key, bool neverConnected = false)
        => key == StateKey.Offline ? StateKeys.OfflineText(!neverConnected) : StateKeys.Word(key);

    /// <summary>同上，按状态代码。</summary>
    public static string Word(string? code, bool neverConnected = false)
        => Word(StateKeys.TryParse(code, out var key) ? key : StateKey.Idle, neverConnected);

    /// <summary>离线液体斜纹的两种颜色（ui.js 的 #pr-hatch：bg-sunken 底 + line-soft 2.4 px 斜线，45°，周期 6）。</summary>
    public static readonly (Color Bg, Color Line) Hatch = (Palette.BgSunkenColor, Palette.LineSoftColor);
}
