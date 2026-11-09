using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 工作区（PORT-PLAN §2.1「工作区 / 页面宿主」；样稿 .pr-ws：bg-app）：每个打开的页签一个页面视图实例，只显示当前页签的那一个 ——
/// 切换页签时滚动位置、输入、展开状态都不丢（不像旧界面那样所有页面一直叠在一起、或每次重建）。页签关掉后视图释放。
/// 面板放大 / 关闭（PrPanel 的 □ / ×）：放大 = 原地铺满工作区（同一页的其它面板隐藏、所在网格的行列跨满，不搬动控件）；
/// 关闭 = 隐藏 + toast「已关闭「…」· 视图 → 重置布局 可恢复」；视图 › 重置布局全部恢复；Esc 先还原放大的面板。
/// </summary>
public sealed class PageHost : Panel
{
    private readonly Dictionary<PageVm, Avalonia.Controls.Control> _views = new();
    private ShellVm? _vm;

    static PageHost()
    {
        PrPanel.IsMaximizedProperty.Changed.AddClassHandler<PrPanel>((p, e) =>
            p.FindAncestorOfType<PageHost>()?.OnMaximized(p, e.GetNewValue<bool>()));
    }

    public PageHost()
    {
        ClipToBounds = true;
        if (Application.Current?.TryFindResource("BgAppBrush", out var b) == true && b is Avalonia.Media.IBrush br) Background = br;
        AddHandler(PrPanel.ClosedEvent, OnPanelClosed);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.Pages.CollectionChanged -= OnPages;
            _vm.PropertyChanged -= OnVm;
            _vm.LayoutReset -= OnReset;
        }
        _vm = DataContext as ShellVm;
        if (_vm != null)
        {
            _vm.Pages.CollectionChanged += OnPages;
            _vm.PropertyChanged += OnVm;
            _vm.LayoutReset += OnReset;
        }
        Sync();
    }

    private void OnPages(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellVm.Page)) Sync();
    }

    /// <summary>视图与页面一一对应：新页面建视图，关掉的页面去掉视图；只显示当前页面。</summary>
    private void Sync()
    {
        var pages = _vm?.Pages.ToHashSet() ?? new HashSet<PageVm>();
        foreach (var gone in _views.Keys.Where(p => !pages.Contains(p)).ToList())
        {
            Children.Remove(_views[gone]);
            _views.Remove(gone);
        }
        foreach (var p in pages)
        {
            if (_views.ContainsKey(p)) continue;
            var v = Create(p);
            v.DataContext = p;
            _views[p] = v;
            Children.Add(v);
        }
        var cur = _vm?.Page;
        foreach (var (p, v) in _views) v.IsVisible = ReferenceEquals(p, cur);
    }

    /// <summary>页面视图模型 → 视图（M2 的骨架页 + 旧样式的程序页 / 性能测试页；M4 / M5 / M7 换成真正的页面）。</summary>
    private static Avalonia.Controls.Control Create(PageVm p) => p switch
    {
        OverviewPageVm => new OverviewPage(),
        DevicePageVm => new DevicePage(),
        ReactorPageVm => new ReactorPage(),
        PlaceholderPageVm => new PlaceholderPage(),
        LegacyProgramPageVm => new LegacyProgramPage(),
        PerfPageVm => new PerfPage(),
        GalleryPageVm => new ScrollViewer { Content = new GalleryPage(), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        _ => new Panel(),
    };

    /// <summary>当前页面的视图。</summary>
    public Avalonia.Controls.Control? CurrentView => _vm?.Page is { } p && _views.TryGetValue(p, out var v) ? v : null;

    // ---------------- 面板放大 / 关闭 ----------------

    /// <summary>放大前的值（IsVisible 只在本来就是本地值时恢复成本地值，否则清掉 —— 关闭的面板由主题的 :closed 样式隐藏，不能被钉成本地值）。</summary>
    private sealed record Saved(bool VisibleSet, bool Visible, int Row, int Col, int RowSpan, int ColSpan, double MinHeight);
    private readonly Dictionary<Avalonia.Controls.Control, Saved> _saved = new();
    private PrPanel? _max;

    private void OnMaximized(PrPanel panel, bool on)
    {
        if (on)
        {
            if (_max != null && !ReferenceEquals(_max, panel)) _max.IsMaximized = false;
            _max = panel;
            Lift(panel);
        }
        else if (ReferenceEquals(_max, panel))
        {
            Restore();
            _max = null;
        }
    }

    /// <summary>放大：从面板往上到页面根，每一层把兄弟元素藏起来；网格里的那一层跨满所有行列；面板至少和工作区一样高（滚动区里）。</summary>
    private void Lift(PrPanel panel)
    {
        var root = CurrentView;
        Avalonia.Controls.Control c = panel;
        while (root != null && !ReferenceEquals(c, root) && c.GetVisualParent() is Avalonia.Controls.Control parent)
        {
            if (parent is Panel pp)
                foreach (var sib in pp.Children.Where(s => !ReferenceEquals(s, c)))
                {
                    Save(sib);
                    sib.IsVisible = false;
                }
            if (parent is Grid g)
            {
                Save(c);
                Grid.SetRow(c, 0); Grid.SetColumn(c, 0);
                Grid.SetRowSpan(c, Math.Max(1, g.RowDefinitions.Count)); Grid.SetColumnSpan(c, Math.Max(1, g.ColumnDefinitions.Count));
            }
            c = parent;
        }
        Save(panel);
        panel.MinHeight = Bounds.Height;
    }

    private void Save(Avalonia.Controls.Control c)
    {
        if (!_saved.ContainsKey(c))
            _saved[c] = new Saved(c.IsSet(IsVisibleProperty), c.IsVisible, Grid.GetRow(c), Grid.GetColumn(c), Grid.GetRowSpan(c), Grid.GetColumnSpan(c), c.MinHeight);
    }

    private void Restore()
    {
        foreach (var (c, s) in _saved)
        {
            if (s.VisibleSet) c.IsVisible = s.Visible; else c.ClearValue(IsVisibleProperty);
            Grid.SetRow(c, s.Row); Grid.SetColumn(c, s.Col); Grid.SetRowSpan(c, s.RowSpan); Grid.SetColumnSpan(c, s.ColSpan);
            c.MinHeight = s.MinHeight;
        }
        _saved.Clear();
    }

    private void OnPanelClosed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not PrPanel p) return;
        if (p.IsMaximized) p.IsMaximized = false;
        var title = p.TitleTip ?? (p.Title as string) ?? (p.Title as Crumb)?.Text ?? "面板";
        _vm?.Toast("已关闭「" + title.Trim() + "」· 视图 → 重置布局 可恢复", ToastKind.Info);
    }

    /// <summary>视图 › 重置布局：还原放大的面板、重新显示关闭的面板（所有页面）。</summary>
    private void OnReset(object? sender, EventArgs e)
    {
        if (_max != null) _max.IsMaximized = false;
        foreach (var v in _views.Values)
            foreach (var p in v.GetVisualDescendants().OfType<PrPanel>()) p.IsClosed = false;
    }

    /// <summary>Esc：有放大的面板就还原它（返回是否处理了）。</summary>
    public bool Escape()
    {
        if (_max == null) return false;
        _max.IsMaximized = false;
        return true;
    }
}
