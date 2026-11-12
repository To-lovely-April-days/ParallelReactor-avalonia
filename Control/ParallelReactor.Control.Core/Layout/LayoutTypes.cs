using System.Globalization;

namespace ParallelReactor.Control.Core.Layout;

// ============================================================================
// 页面布局计算器的共用类型（PORT-PLAN §4.8 第 2 层：OverviewLayout / DeviceLayout）。
// 样稿用容器查询（@container ov / ovc / dv / dvcard / dvreads …）和页面 JS 的测量决定版面；Avalonia 11.1 没有容器查询，
// 这里把那些数字规则原样搬成纯函数：输入是父元素给出的尺寸（R4：只看父元素给的尺寸，不看自己的结果，不会来回振荡）、型号、装机，
// 输出是记录。每个阈值都注明样稿出处（文件:行，2026-11-09 的 research/pc-v3）。单位一律是设计 px（= Avalonia 的 DIP）。
// ============================================================================

/// <summary>
/// 卡片种类：Wide = HT-PR4 宽卡（每釜独立搅拌，多一行 搅拌 / 加热）；Row = 8 路一排的卡；Compact = 16 路（总览里还有 8 路装满）的紧凑卡。
/// 总览按型号与装机数（<see cref="OverviewLayout.CardKindOf"/>），设备页只按型号（<see cref="DeviceLayout.CardKindOf"/>）。
/// </summary>
public enum CardKind { Wide, Row, Compact }

/// <summary>网格的一列：Star = 平分剩余宽度（minmax(0, 1fr)）；Fixed = 未安装窄列（总览 96 / 设备页 140）。<see cref="Width"/> 是算出来的宽度。</summary>
public readonly record struct GridTrack(bool Fixed, double Width);

/// <summary>
/// 网格里的一块：已安装的反应釜卡片（<see cref="Installed"/>，<see cref="From"/> = <see cref="To"/> = 通道号），或一段合并的未安装通道（「RV5–RV8 / 未安装」）。
/// <see cref="Column"/> / <see cref="Row"/> 从 0 起，<see cref="Span"/> = 跨几列；<see cref="X"/> / <see cref="Y"/> 相对网格的边框盒（含内边距），
/// <see cref="Width"/> / <see cref="Height"/> 是这一块的外框尺寸。
/// </summary>
public sealed record GridTile(int From, int To, bool Installed, int Column, int Row, int Span, double X, double Y, double Width, double Height)
{
    /// <summary>反应釜卡片（不是未安装块）。</summary>
    public bool IsCard => Installed;

    /// <summary>'RV5–RV8' / 'RV5'（未安装块的标签；卡片为 'RV1'）。</summary>
    public string Label => From == To ? "RV" + From.ToString(CultureInfo.InvariantCulture)
        : "RV" + From.ToString(CultureInfo.InvariantCulture) + "–RV" + To.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 一个读数的字号：<see cref="Size"/> 数字字号、<see cref="LineHeight"/> 行高（读数行的高度跟着宽度那一档，长读数缩字号时行高不变，卡片不跳）、
/// <see cref="Unit"/> 单位是否显示（最窄的一档去掉单位，完整读数在悬停提示里）。
/// </summary>
public readonly record struct ReadingFont(double Size, double LineHeight, bool Unit = true);

/// <summary>样稿读数的 data-len（读数的字符数：'199.9' → 5，'—' / null → 0，长读数阶梯只看它）。</summary>
public static class ReadingLength
{
    /// <summary>读数文字的字符数；没有值（null / 空 / '—'）为 0。</summary>
    public static int Of(string? text) => string.IsNullOrEmpty(text) || text == Format.Fmt.Dash ? 0 : text.Length;
}
