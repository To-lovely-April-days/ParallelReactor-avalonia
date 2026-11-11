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
/// 关闭 = 隐藏 + toast「已关闭「…」· 视图 → 重置布局 可恢复」；视图 › 重置布局全部恢复（所有页签）；Esc 先还原当前页面上放大的面板。
/// </summary>
public sealed class PageHost : Panel
{
    private readonly Dictionary<PageVm, Avalonia.Controls.Control> _views = new();
    private ShellVm? _vm;

    /// <summary>
    /// 页面自己放大这个面板（样稿总览：放大的分区以滚动区为定位框 —— 顶部报警条照常显示，滚动区停在顶部，还原后回到原来的滚动位置）：
    /// 宿主不再「藏兄弟 + 跨满网格」，只记着它（Esc、视图 › 重置布局照常还原它）。
    /// </summary>
    public static readonly AttachedProperty<bool> InPlaceMaxProperty =
        AvaloniaProperty.RegisterAttached<PageHost, PrPanel, bool>("InPlaceMax");

    public static bool GetInPlaceMax(PrPanel p) => p.GetValue(InPlaceMaxProperty);
    public static void SetInPlaceMax(PrPanel p, bool v) => p.SetValue(InPlaceMaxProperty, v);

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
            var view = _views[gone];
            // 放大的面板在这一页里：先放掉（去掉视图之后面板不在宿主下面，IsMaximized 的变化再也到不了 OnMaximized ——
            // 复核：关掉放大了面板的页签之后 Esc 一直被这里吞掉，外壳的 Esc 再也收不起浮层资源管理器）
            if (_max != null && (ReferenceEquals(_max, view) || view.IsVisualAncestorOf(_max))) DropMax();
            Children.Remove(view);
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

    /// <summary>放大前的网格位置与最小高度（还原时写回）。</summary>
    private sealed record Saved(int Row, int Col, int RowSpan, int ColSpan, double MinHeight);
    private readonly Dictionary<Avalonia.Controls.Control, Saved> _saved = new();
    /// <summary>
    /// 放大时藏起来的兄弟元素：IsVisible 以 Animation 优先级压成 false（盖过本地值、绑定与样式），还原时撤掉这一层 —— 原来的本地值 / 绑定 / 样式原样回来
    /// （M4：设备页窄工作区的页签面板、宽工作区的两块面板，IsVisible 绑在视图模型上；以前写成本地值会顶掉绑定，还原之后再也不跟着变）。
    /// </summary>
    private readonly Dictionary<Avalonia.Controls.Control, IDisposable?> _hidden = new();
    /// <summary>放大的面板和它到页面根之间的各层的 IsVisible 订阅（<see cref="WatchChain"/>）。</summary>
    private readonly List<IDisposable> _watch = new();
    private PrPanel? _max;

    private void OnMaximized(PrPanel panel, bool on)
    {
        bool self = GetInPlaceMax(panel);
        if (on)
        {
            if (_max != null && !ReferenceEquals(_max, panel)) _max.IsMaximized = false;
            _max = panel;
            if (!self) Lift(panel);
            WatchChain(panel);
        }
        else if (ReferenceEquals(_max, panel))
        {
            Unwatch();
            if (!self) Restore();
            _max = null;
        }
    }

    /// <summary>
    /// 放大的面板自己（或它到页面根之间的某一层）被藏起来了 —— 例如设备页跨过 1100 换了一组下排面板，放大的那块跟着宽度类不显示 ——
    /// 就还原放大：不然兄弟元素还被放大藏着、放大的又看不见，整页一片空白（M4 复核）。页面视图本身（切页签时整个藏起来）不算。
    /// 在下一个 Normal 优先级的任务里还原（在渲染之前；不在别人改 IsVisible 的半中间改别的元素）。
    /// </summary>
    private void WatchChain(PrPanel panel)
    {
        Unwatch();
        var root = CurrentView;
        for (Avalonia.Controls.Control? c = panel; c != null && !ReferenceEquals(c, root) && !ReferenceEquals(c, this); c = c.GetVisualParent() as Avalonia.Controls.Control)
        {
            var who = c;
            _watch.Add(who.GetPropertyChangedObservable(IsVisibleProperty).Subscribe(new HiddenObserver(() =>
            {
                if (who.IsVisible) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_max, panel) && panel.IsMaximized && !StillShown(panel, root)) panel.IsMaximized = false;
                }, Avalonia.Threading.DispatcherPriority.Normal);
            })));
        }
    }

    /// <summary>面板到页面根之间的每一层都显示着（页面视图本身不看）。</summary>
    private static bool StillShown(Avalonia.Controls.Control panel, Avalonia.Controls.Control? root)
    {
        for (Avalonia.Controls.Control? c = panel; c != null && !ReferenceEquals(c, root); c = c.GetVisualParent() as Avalonia.Controls.Control)
        {
            if (c is PageHost) break;
            if (!c.IsVisible) return false;
        }
        return true;
    }

    private void Unwatch()
    {
        foreach (var w in _watch) w.Dispose();
        _watch.Clear();
    }

    /// <summary>
    /// 放掉放大的面板（它的页面视图要去掉了，或者它已经不在这个宿主下面）：撤掉订阅、还原兄弟元素与网格位置（不是页面自己放大的时候）、
    /// 清掉放大标记。先把 _max 清空再改 IsMaximized：面板若还在宿主下面，类处理器进来时不再重复还原。
    /// </summary>
    private void DropMax()
    {
        if (_max is not { } m) return;
        _max = null;
        Unwatch();
        if (!GetInPlaceMax(m)) Restore();
        m.IsMaximized = false;
    }

    /// <summary>放大的面板已经不在这个宿主下面（视图被去掉了等）：放掉它，Esc / 重置布局不再被它挡住。</summary>
    private void DropDetachedMax()
    {
        if (_max != null && !this.IsVisualAncestorOf(_max)) DropMax();
    }

    private sealed class HiddenObserver : IObserver<AvaloniaPropertyChangedEventArgs>
    {
        private readonly Action _changed;
        public HiddenObserver(Action changed) => _changed = changed;
        public void OnNext(AvaloniaPropertyChangedEventArgs value) => _changed();
        public void OnError(Exception error) { }
        public void OnCompleted() { }
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
                    if (_hidden.ContainsKey(sib)) continue;
                    _hidden[sib] = sib.SetValue(IsVisibleProperty, false, Avalonia.Data.BindingPriority.Animation);
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
            _saved[c] = new Saved(Grid.GetRow(c), Grid.GetColumn(c), Grid.GetRowSpan(c), Grid.GetColumnSpan(c), c.MinHeight);
    }

    private void Restore()
    {
        foreach (var (c, s) in _saved)
        {
            Grid.SetRow(c, s.Row); Grid.SetColumn(c, s.Col); Grid.SetRowSpan(c, s.RowSpan); Grid.SetColumnSpan(c, s.ColSpan);
            c.MinHeight = s.MinHeight;
        }
        _saved.Clear();
        foreach (var (c, h) in _hidden)
        {
            if (h != null) h.Dispose();
            else c.ClearValue(IsVisibleProperty);
        }
        _hidden.Clear();
    }

    private void OnPanelClosed(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not PrPanel p) return;
        if (p.IsMaximized) p.IsMaximized = false;
        // 面板名：标题是控件（设备页的「设备信息 / 报警与事件」页签）时写在 AutomationProperties.Name 上（样稿 ui.panel 的 name）
        var title = Avalonia.Automation.AutomationProperties.GetName(p) is { Length: > 0 } an ? an
            : p.TitleTip ?? (p.Title as string) ?? (p.Title as Crumb)?.Text ?? "面板";
        _vm?.Toast("已关闭「" + title.Trim() + "」· 视图 → 重置布局 可恢复", ToastKind.Info);
    }

    /// <summary>视图 › 重置布局：还原放大的面板、重新显示关闭的面板（所有页面）。</summary>
    private void OnReset(object? sender, EventArgs e)
    {
        DropDetachedMax();
        if (_max != null) _max.IsMaximized = false;
        foreach (var v in _views.Values)
            foreach (var p in v.GetVisualDescendants().OfType<PrPanel>()) p.IsClosed = false;
    }

    /// <summary>
    /// Esc：当前页面上有放大的面板就还原它（返回是否处理了）。已经不在宿主下面的放掉；在藏起来的页签里的不动（切回去仍是放大的），
    /// 这次 Esc 交给外壳 —— 样稿只在当前页面（els.ws）里找 .is-max（复核：以前切走页签之后 Esc 被它吞掉，悄悄还原了看不见的面板，浮层资源管理器收不起）。
    /// </summary>
    public bool Escape()
    {
        DropDetachedMax();
        if (_max == null || CurrentView is not { } cur || !(ReferenceEquals(cur, _max) || cur.IsVisualAncestorOf(_max))) return false;
        _max.IsMaximized = false;
        return true;
    }
}
