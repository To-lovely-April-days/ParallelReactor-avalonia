using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 几个候选里只显示<b>第一个</b>按自然宽度放得下的（看得见的子元素，按先后）；都放不下时显示最后一个（它自己按给的宽度省略）。
/// 别的子元素排成 0 × 0、透明、不接受指针（从 0 × 0 回来时让里面的文字重新排，见 <see cref="TextRelayout"/>）。
/// M6：检查器控制权卡的第 5 种控制权 —— 完整的「远程控制中（其他客户端 PC-工位2）」连一整行都放不下时换成短的「其他客户端 PC-工位2」，
/// 不按省略号把持有者的名字切掉（完整文字在悬停提示里）。放在 <see cref="WrapEnd"/> 里：先按完整文字的宽度决定换不换行（同样稿 flex-wrap），
/// WrapEnd 按一整行的宽度再量一次时才换短的。
/// </summary>
public sealed class FirstFit : Panel
{
    private Avalonia.Controls.Control? _shown;
    private readonly HashSet<Avalonia.Controls.Control> _zeroed = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        Avalonia.Controls.Control? pick = null, last = null;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Measure(Size.Infinity);
            last = c;
            if (pick == null && c.DesiredSize.Width <= availableSize.Width + 0.5) pick = c;
        }
        _shown = pick ?? last;
        if (_shown == null) return default;
        _shown.Measure(availableSize);
        return _shown.DesiredSize;
    }

    /// <summary>
    /// 不显示的排成 0 × 0，并且透明、不接受指针：排成 0 × 0 的徽标里面的横排面板照旧按自然宽度排字（字伸到框外，审计报 outside、还会挡住指针）。
    /// 透明度与命中只影响绘制，改它们不会再引起排版（IsVisible 绑定在视图模型上，不能在这里改）。
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var c in Children)
        {
            bool shown = ReferenceEquals(c, _shown);
            c.Opacity = shown ? 1 : 0;
            c.IsHitTestVisible = shown;
            if (shown)
            {
                if (_zeroed.Remove(c)) TextRelayout.Revive(c);
                c.Arrange(new Rect(finalSize));
            }
            else
            {
                c.Arrange(default);
                _zeroed.Add(c);
            }
        }
        return finalSize;
    }
}
