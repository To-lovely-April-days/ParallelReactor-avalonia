using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ParallelReactor.Control.Controls;

/// <summary>
/// 被排成 0 × 0 藏起来、之后又显示出来的子元素里的文字要重新排（Avalonia 11.1 的缺陷的绕法）。
/// <para>
/// 11.1 的 TextBlock 只在排版宽度<b>变小</b>时按新宽度重建文字（ArrangeOverride：finalSize.Width &lt; 上次的约束宽度时才重建 TextLayout）。
/// 面板把子元素排成 0 × 0 藏起来（TabOverflowPanel 收进「›」的页签、FlexRow 放不下时整个让出去的可选项）时，里面的文字按 0 宽重建；
/// 之后再按原来的大小排出来，宽度变大了不会重建 —— 页签显示出来了，名字却是空的（例如在「›」菜单里点一个没显示的页签、窗口变宽）。
/// 文字的测量本身没失效（可用宽度没变），所以要显式让它重新测量一次：测量时按新的约束重建，下一遍排版就对了（只多一遍，不会来回）。
/// </para>
/// </summary>
public static class TextRelayout
{
    /// <summary>让 <paramref name="root"/> 及其子树里的文字重新测量（在面板的 ArrangeOverride 里、子元素从 0 × 0 回到正常大小时调用）。</summary>
    public static void Revive(Visual root)
    {
        foreach (var tb in root.GetSelfAndVisualDescendants().OfType<TextBlock>()) tb.InvalidateMeasure();
    }
}
