using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>可以收进溢出菜单的一项（文档页签）：身份 + 是否当前项。</summary>
public interface IOverflowItem
{
    string Id { get; }
    bool IsActive { get; }
}

/// <summary>
/// 文档页签条的排版面板（PORT-PLAN §2.1「文档页签栏」的 TabOverflowPanel；样稿 shell.js layoutTabs）：页签按自然宽度从左往右排，铺满高度；
/// 放不下时从右往左收进「›」菜单 —— 第一个（固定的总览）和当前页签始终显示，其余按顺序能放下就放（后面窄的仍可能放得下）。
/// 隐藏的页签排成 0 × 0（不画、不命中、不参与审计；再显示时里面的文字重新排，见 Controls/TextRelayout）。隐藏集合变化时 <see cref="HiddenChanged"/>：外面更新「›」上的个数，
/// 「›」变宽后面板拿到更窄的可用宽度、再排一次，直到稳定（样稿以前要等下一次刷新才改正）。
/// 子元素的 DataContext 实现 <see cref="IOverflowItem"/>（ItemsControl 生成的容器）。
/// </summary>
public sealed class TabOverflowPanel : Panel
{
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    /// <summary>上一遍排成 0 × 0 的子元素（再显示时里面的文字要重新排，见 Controls/TextRelayout）。</summary>
    private readonly HashSet<Avalonia.Controls.Control> _zeroed = new();
    private bool[] _show = Array.Empty<bool>();

    /// <summary>隐藏集合变了（参数是隐藏页签的 Id）。在排版过程中触发；只在集合真的变化时触发。</summary>
    public event Action<IReadOnlyCollection<string>>? HiddenChanged;

    public IReadOnlyCollection<string> Hidden => _hidden;

    protected override Size MeasureOverride(Size availableSize)
    {
        var kids = Children;
        int n = kids.Count;
        var widths = new double[n];
        double h = 0;
        for (int i = 0; i < n; i++)
        {
            kids[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            widths[i] = kids[i].DesiredSize.Width;
            h = Math.Max(h, kids[i].DesiredSize.Height);
        }
        double avail = availableSize.Width;
        int active = -1;
        for (int i = 0; i < n; i++) if (Item(i)?.IsActive == true) { active = i; break; }
        _show = Visible(widths, avail, active);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        double w = 0;
        for (int i = 0; i < n; i++)
        {
            if (_show[i]) w += widths[i];
            else if (Item(i) is { } it) hidden.Add(it.Id);
        }
        if (!hidden.SetEquals(_hidden))
        {
            _hidden.Clear();
            _hidden.UnionWith(hidden);
            HiddenChanged?.Invoke(hidden);
        }
        return new Size(double.IsInfinity(avail) ? w : Math.Min(w, avail), h);
    }

    /// <summary>
    /// 哪些页签显示（样稿 shell.js layoutTabs 的隐藏顺序）：放得下就全显示；放不下时第一个（固定的总览）和当前页签（<paramref name="active"/>，-1 = 没有）
    /// 始终显示，其余按从左到右的顺序能放下就放（后面窄的仍可能放得下）。纯函数，测试直接调用。
    /// </summary>
    public static bool[] Visible(IReadOnlyList<double> widths, double avail, int active)
    {
        int n = widths.Count;
        var show = new bool[n];
        double total = 0;
        foreach (var w in widths) total += w;
        if (double.IsInfinity(avail) || total <= avail + 0.01)
        {
            Array.Fill(show, true);
            return show;
        }
        double used = 0;
        if (n > 0) { show[0] = true; used += widths[0]; }
        if (active > 0 && active < n) { show[active] = true; used += widths[active]; }
        for (int i = 1; i < n; i++)
        {
            if (i == active) continue;
            if (used + widths[i] <= avail + 0.01) { show[i] = true; used += widths[i]; }
        }
        return show;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        var kids = Children;
        for (int i = 0; i < kids.Count; i++)
        {
            if (i < _show.Length && !_show[i]) { kids[i].Arrange(default); _zeroed.Add(kids[i]); continue; }
            // 收进「›」又显示出来的页签：名字按 0 宽排过，重新排（否则显示出来的页签名字是空的）
            if (_zeroed.Remove(kids[i])) TextRelayout.Revive(kids[i]);
            var w = kids[i].DesiredSize.Width;
            kids[i].Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        if (_zeroed.Count > 0) _zeroed.RemoveWhere(c => !kids.Contains(c));   // 关掉的页签
        return finalSize;
    }

    private IOverflowItem? Item(int i) => Children[i].DataContext as IOverflowItem;
}
