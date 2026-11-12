using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
/// 隐藏的页签排成 0 × 0（不画、不命中、不参与审计；再显示时里面的文字重新排，见 Controls/TextRelayout），也不是 Tab 键的停靠点（同样稿 .is-overflow 的 display: none）；
/// 收起时正拿着焦点的，焦点移到「›」上。
/// 隐藏集合变化时 <see cref="HiddenChanged"/>：外面更新「›」上的个数。
/// <para>
/// 隐藏集合只看页签宽度、当前页签和页签栏的宽度（<see cref="Layout"/>，同样稿新打开的页面：「›」先是 44、不写个数）：面板拿到的可用宽度已经减掉了「›」现在的宽度，
/// 加回它写着个数时多出来的那一截（量 <see cref="More"/>），就是「›」不写个数时的宽度。以前直接用拿到的宽度排 ——「›」写着个数时窄 20 px，
/// 页签总宽落在这 20 px 里时「放得下（不写个数）」与「放不下（写个数）」两种状态都自洽，结果看上一张截图（1600 上「全部曲线」时有时无）。
/// </para>
/// 子元素的 DataContext 实现 <see cref="IOverflowItem"/>（ItemsControl 生成的容器）。
/// </summary>
public sealed class TabOverflowPanel : Panel
{
    /// <summary>还没量过「›」写上这个位数的个数有多宽时的估计（一位数：左右内边距各 10 + 个数块 21 + 间距 4 + 箭头 18 + 左边线 1 − 44）。只用一遍：「›」真的写上之后下一遍排版量到真值再排。</summary>
    public const double DefaultExtra = 20;
    /// <summary>没见过「›」不写个数的样子时（面板建好时「›」已经写着个数）按主题的宽（Styles/Theme/Shell.axaml PrTabBarButton 的 Width）。</summary>
    public const double DefaultMoreWidth = 44;

    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    /// <summary>上一遍排成 0 × 0 的子元素（再显示时里面的文字要重新排，见 Controls/TextRelayout）。</summary>
    private readonly HashSet<Avalonia.Controls.Control> _zeroed = new();
    /// <summary>隐藏的页签里可以拿焦点的元素：IsTabStop 以 Animation 优先级压成 false（不顶掉主题、绑定），再显示时撤掉。</summary>
    private readonly Dictionary<Avalonia.Controls.Control, List<IDisposable>> _noTab = new();
    private bool[] _show = Array.Empty<bool>();
    /// <summary>「›」不写个数时的宽（量到的；还没量到时 &lt; 0）。</summary>
    private double _moreBase = -1;
    /// <summary>「›」写着 d 位数的个数时比不写宽多少（量到的）。</summary>
    private readonly Dictionary<int, double> _extra = new();

    /// <summary>隐藏集合变了（参数是隐藏页签的 Id）。在排版过程中触发；只在集合真的变化时触发。</summary>
    public event Action<IReadOnlyCollection<string>>? HiddenChanged;

    public IReadOnlyCollection<string> Hidden => _hidden;

    /// <summary>
    /// 页签栏右端的「›」（DocTabStrip.MoreButton 交过来；个数在它的 b:Pr.Count 上）。页签栏的 Grid 先量 Auto 列（「+」「›」）再量 * 列（页签条），
    /// 量页签条时它的宽度就是现在写着的个数的宽度。收起的页签正拿着焦点时焦点移到它上面。没有时按「›」不写个数排（测试里单独放的面板）。
    /// </summary>
    public Avalonia.Controls.Control? More { get; set; }

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
        // 「›」不写个数时的页签区宽度：拿到的宽度 + 「›」现在多出来的那一截
        double baseAvail = double.IsInfinity(avail) ? avail : avail + CurrentExtra();
        _show = Layout(widths, baseAvail, active, Extra);
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
    /// 样稿 layoutTabs 在新打开的页面上的结果（纯函数，测试直接调用）：先按「›」不写个数时的宽度 <paramref name="avail"/> 排；有放不下的，
    /// 「›」写上个数 k 变宽 <paramref name="extra"/>(k)，按 avail − extra(k) 再排一遍；新的个数让「›」的宽度又变了（位数不同）再排一遍（样稿最多三遍）。
    /// extra(0) 必须是 0。
    /// </summary>
    public static bool[] Layout(IReadOnlyList<double> widths, double avail, int active, Func<int, double> extra)
    {
        var show = Visible(widths, avail, active);
        int k = HiddenCount(show);
        if (k == 0 || double.IsInfinity(avail)) return show;
        double e = extra(k);
        show = Visible(widths, avail - e, active);
        double e1 = extra(HiddenCount(show));
        if (e1 != e) show = Visible(widths, avail - e1, active);
        return show;
    }

    private static int HiddenCount(bool[] show)
    {
        int k = 0;
        foreach (var s in show) if (!s) k++;
        return k;
    }

    /// <summary>
    /// 哪些页签显示（样稿 shell.js layoutTabs 一遍的隐藏顺序）：放得下就全显示；放不下时第一个（固定的总览）和当前页签（<paramref name="active"/>，-1 = 没有）
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

    /// <summary>「›」现在写着的个数让它比不写宽多少（顺便记下量到的宽度）。没有「›」或它还没量过时按 0。</summary>
    private double CurrentExtra()
    {
        var m = More;
        if (m == null || !m.IsMeasureValid || !(m.DesiredSize.Width > 0)) return 0;
        var count = Behaviors.Pr.GetCount(m);
        double w = m.DesiredSize.Width;
        if (string.IsNullOrEmpty(count)) { _moreBase = w; return 0; }
        double e = w - (_moreBase < 0 ? DefaultMoreWidth : _moreBase);
        _extra[count.Length] = e;
        return e;
    }

    /// <summary>
    /// 「›」写上个数 <paramref name="k"/> 时比不写宽多少：量到过这个位数的用量到的；没量到过的先估计（一遍之后「›」真的写上，下一遍量到真值）。
    /// 没有「›」时 0（不会变宽）。
    /// </summary>
    private double Extra(int k)
    {
        if (k <= 0 || More == null) return 0;
        int d = k.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        if (_extra.TryGetValue(d, out var e)) return e;
        // 等宽数字：每多一位多一个数字宽（按量到的一位数估计个数字宽 ≈ 一位数多出来的 − 11）
        return _extra.TryGetValue(1, out var e1) ? e1 + (d - 1) * Math.Max(1, e1 - 11) : DefaultExtra + (d - 1) * 9;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        var kids = Children;
        for (int i = 0; i < kids.Count; i++)
        {
            if (i < _show.Length && !_show[i])
            {
                kids[i].Arrange(default);
                _zeroed.Add(kids[i]);
                NoTabStop(kids[i]);
                continue;
            }
            // 收进「›」又显示出来的页签：名字按 0 宽排过，重新排（否则显示出来的页签名字是空的）
            if (_zeroed.Remove(kids[i])) TextRelayout.Revive(kids[i]);
            TabStopBack(kids[i]);
            var w = kids[i].DesiredSize.Width;
            kids[i].Arrange(new Rect(x, 0, w, finalSize.Height));
            x += w;
        }
        if (_zeroed.Count > 0) _zeroed.RemoveWhere(c => !kids.Contains(c));   // 关掉的页签
        if (_noTab.Count > 0)
            foreach (var c in _noTab.Keys.Where(c => !kids.Contains(c)).ToList()) TabStopBack(c);
        return finalSize;
    }

    /// <summary>
    /// 收进「›」的页签不是 Tab 键的停靠点（样稿 display: none）：排成 0 × 0 的按钮照样能拿焦点，焦点会落到看不见的地方。
    /// 焦点正在这个页签里（窗口变窄、别的页签变宽把它挤进「›」）：排完之后焦点移到「›」上，见 <see cref="FocusToMore"/>。
    /// </summary>
    private void NoTabStop(Avalonia.Controls.Control c)
    {
        if (HasFocusIn(c)) FocusToMore(c);
        if (_noTab.ContainsKey(c)) return;
        var list = new List<IDisposable>();
        foreach (var e in c.GetSelfAndVisualDescendants().OfType<InputElement>())
            if (e.Focusable && e.GetValue(InputElement.IsTabStopProperty) && e.SetValue(InputElement.IsTabStopProperty, false, Avalonia.Data.BindingPriority.Animation) is { } d)
                list.Add(d);
        _noTab[c] = list;
    }

    private void TabStopBack(Avalonia.Controls.Control c)
    {
        if (!_noTab.Remove(c, out var list)) return;
        foreach (var d in list) d.Dispose();
    }

    private bool _refocusPosted;

    private Visual? Focused => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;

    private bool HasFocusIn(Avalonia.Controls.Control c) => Focused is { } f && (ReferenceEquals(f, c) || c.IsVisualAncestorOf(f));

    /// <summary>
    /// 收起的页签正拿着焦点：排完之后（不在排版过程中改焦点）焦点移到「›」上 —— 页签收进了它的菜单，Tab 从这里接着走，回车打开的是菜单，
    /// 不会是看不见的页签。样稿 display: none 让焦点直接丢掉（落到页面上），焦点框也没了；这里键盘来的焦点在「›」上照样画焦点框，
    /// 指针点出来的不画。没有「›」时清掉焦点。
    /// </summary>
    private void FocusToMore(Avalonia.Controls.Control tab)
    {
        if (_refocusPosted) return;
        _refocusPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refocusPosted = false;
            // 这期间又显示出来、焦点已经走了：不动
            if (!_noTab.ContainsKey(tab) || !HasFocusIn(tab)) return;
            bool ring = (Focused as StyledElement)?.Classes.Contains(":focus-visible") == true;
            if (More is InputElement m && m.IsEffectivelyVisible && m.IsEffectivelyEnabled && m.Focusable)
                m.Focus(ring ? NavigationMethod.Tab : NavigationMethod.Unspecified);
            else TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        });
    }

    private IOverflowItem? Item(int i) => Children[i].DataContext as IOverflowItem;
}
