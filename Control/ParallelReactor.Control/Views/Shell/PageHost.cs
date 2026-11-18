using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 看板（融合版 F2；样稿 #board）：每种页面一个视图实例（ShellVm.Pages），只显示当前页面的那一个 —— 换页面再回来，滚动位置、输入、展开状态都不丢。
/// 页面视图模型 → 视图在 <see cref="Create"/>（新页面在这里接上）。
/// </summary>
public sealed class PageHost : Panel
{
    private readonly Dictionary<PageVm, Avalonia.Controls.Control> _views = new();
    private ShellVm? _vm;

    public PageHost() => ClipToBounds = false;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.Pages.CollectionChanged -= OnPages;
            _vm.PropertyChanged -= OnVm;
        }
        _vm = DataContext as ShellVm;
        if (_vm != null)
        {
            _vm.Pages.CollectionChanged += OnPages;
            _vm.PropertyChanged += OnVm;
        }
        Sync();
    }

    private void OnPages(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellVm.Page)) Sync();
    }

    /// <summary>视图与页面一一对应：新页面建视图，去掉的页面去掉视图；只显示当前页面。</summary>
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

    /// <summary>页面视图模型 → 视图（F3 起主页换成真正的看板；程序 / 性能测试 F6 / F8 前是旧样式页面）。</summary>
    private static Avalonia.Controls.Control Create(PageVm p) => p switch
    {
        HomePageVm => new HomePage(),
        PlaceholderPageVm => new PlaceholderPage(),
        LegacyProgramPageVm => new LegacyProgramPage(),
        PerfPageVm => new PerfPage(),
        GalleryPageVm => new ScrollViewer { Content = new GalleryPage(), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        _ => new Panel(),
    };

    /// <summary>当前页面的视图。</summary>
    public Avalonia.Controls.Control? CurrentView => _vm?.Page is { } p && _views.TryGetValue(p, out var v) ? v : null;
}
