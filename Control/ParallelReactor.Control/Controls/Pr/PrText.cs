using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 按 WebKit 的方式量宽的 TextBlock（PORT-PLAN R1「文字度量」）：框宽 = 文字的前进宽度（含行尾空白），文字从框的左边画起。
/// <para>
/// Avalonia 11.1 的 TextBlock 量宽时另加墨迹外伸（TextLayout.OverhangLeading + OverhangTrailing：苹方的中文每边 1 px），画字时再右移
/// OverhangLeading —— 每个中文标签比网页宽 2 px（「总览」32 对 30），并排的标签越排越偏（菜单项、页签、状态栏 2–10 px），
/// 刚好放得下的一排页签就会被挤进「›」，分成两段的名字（「1号机」「 · RV3」）中间多出 2–4 px 的空。
/// 这里只量前进宽度（向上取整到整像素，同基类），不加外伸、不右移；不裁剪（ClipToBounds = false），伸出框的那 1 px 墨迹照样画出来（同网页）。
/// </para>
/// 样式照旧按 TextBlock 匹配（StyleKeyOverride），换掉 TextBlock 不影响任何样式与绑定。外框里的字符串内容（按钮、菜单项、页签、图标栏…）
/// 经 ShellView 的字符串 DataTemplate 用它；并排的单行标签（页签名、检查器路径、状态栏、菜单栏右端）直接用它。
/// </summary>
public class PrText : TextBlock
{
    static PrText()
    {
        ClipToBoundsProperty.OverrideDefaultValue<PrText>(false);
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>
    /// 最近一次测量时文字的前进宽度（小数，含行尾空白与内边距；没取整）。布局面板判断「放得下吗」时用它（同 WebKit 的小数排版）——
    /// 不能事后读 TextLayout：11.1 的 TextBlock 被排成更窄（例如 0 × 0 让出去）时会按排版宽度重建 TextLayout，之后测量约束没变就不再重建。
    /// </summary>
    public double NaturalWidth { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        var layout = TextLayout;
        var pad = Padding;
        NaturalWidth = layout.WidthIncludingTrailingWhitespace + pad.Left + pad.Right;
        // 前进宽度（不含墨迹外伸）；向上取整到整像素（同基类的 RoundLayoutSizeUp）。减一点点：前进宽度的和有浮点误差（30.000000000004），
        // 不能因此进位成 31（框不能比文字窄，所以只减一个远小于任何真实宽度差的量）
        double w = Math.Ceiling(layout.WidthIncludingTrailingWhitespace + pad.Left + pad.Right - 1e-6);
        return new Size(Math.Min(size.Width, Math.Max(0, w)), size.Height);
    }

    /// <summary>从框的左边画（基类右移 OverhangLeading，见类说明）。</summary>
    protected override void RenderTextLayout(DrawingContext context, Point origin) => TextLayout.Draw(context, origin);
}
