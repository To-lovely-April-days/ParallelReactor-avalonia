using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>
/// 时间轴：标题栏三列的标签列宽 = ShellLayout.TimelineLabelWidth（资源管理器停靠时 300，与它的右边线对齐）；
/// 收起时标题栏没有下边线；刻度文字按 TimelineVm.Ticks 生成。正文：组头 / 行标签的单击（组头 = 展开 / 收起，行 = 选中）、
/// 双击行标签打开反应釜页、右键菜单（行 = 反应釜菜单），以及跟随（TimelineVm.FollowRequested：组头滚到顶部、行滚到可见处，组头吸顶时不藏在组头下面）。
/// </summary>
public partial class TimelineView : UserControl
{
    private ShellVm? _vm;

    public TimelineView()
    {
        InitializeComponent();
        Body.AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble);
        Body.AddHandler(DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
        Body.AddHandler(ContextRequestedEvent, OnContext, RoutingStrategies.Bubble);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) { _vm.Layout.Changed -= OnLayout; _vm.Timeline.PropertyChanged -= OnTimeline; _vm.Timeline.FollowRequested -= OnFollow; }
        _vm = DataContext as ShellVm;
        if (_vm == null) return;
        _vm.Layout.Changed += OnLayout;
        _vm.Timeline.PropertyChanged += OnTimeline;
        _vm.Timeline.FollowRequested += OnFollow;
        OnLayout(null, EventArgs.Empty);
        BuildTicks();
    }

    private void OnLayout(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        var w = new GridLength(_vm.Layout.TimelineLabelWidth);
        if (HeadGrid.ColumnDefinitions[0].Width != w) HeadGrid.ColumnDefinitions[0].Width = w;
        Head.BorderThickness = new Thickness(0, 0, 0, _vm.Layout.TimelineFolded ? 0 : 1);
    }

    private void OnTimeline(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineVm.Ticks)) BuildTicks();
    }

    private void BuildTicks()
    {
        Ticks.Children.Clear();
        if (_vm == null) return;
        var fs = this.TryFindResource("FsNum", out var f) && f is double d ? d : 13;
        var fg = this.TryFindResource("T3Brush", out var b) && b is IBrush br ? br : null;
        foreach (var t in _vm.Timeline.Ticks)
        {
            var tb = new TextBlock { Text = t.Text, FontSize = fs, Foreground = fg, Margin = new Thickness(0, 11, 0, 0) };
            tb.Classes.Add("num");
            RelXPanel.SetX(tb, t.X);
            Ticks.Children.Add(tb);
        }
    }

    private static T? Ctx<T>(RoutedEventArgs e, string cls) where T : class
    {
        if (e.Source is not Visual v) return null;
        if (v.FindAncestorOfType<Button>(includeSelf: true) != null) return null;   // 行 / 组头里的按钮自己处理
        for (Visual? x = v; x != null; x = x.GetVisualParent())
            if (x is StyledElement se && se.Classes.Contains(cls)) return se.DataContext as T;
        return null;
    }

    /// <summary>单击：组头标签 = 展开 / 收起（样稿 data-tlg）；行标签 = 选中（样稿 data-sel）。</summary>
    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (Ctx<TlGroupVm>(e, "glabel") is { } g) { g.ToggleCommand.Execute(null); e.Handled = true; return; }
        if (Ctx<TlRowVm>(e, "tllabel") is { } r) { r.SelectCommand.Execute(null); e.Handled = true; }
    }

    /// <summary>双击行标签：打开反应釜页（样稿 data-open）。</summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Ctx<TlRowVm>(e, "tllabel") is { } r) { r.OpenCommand.Execute(null); e.Handled = true; }
    }

    /// <summary>右键行标签：先选中，再弹出反应釜菜单（样稿 data-ctx="rv:k/n"，与资源管理器同一份）。</summary>
    private void OnContext(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm == null || Ctx<TlRowVm>(e, "tllabel") is not { } r || e.Source is not Avalonia.Controls.Control c) return;
        _vm.Select(r.D.K, r.R.N);
        ContextMenus.Open(c, MenuModel.ReactorItems(_vm, r.D, r.R), e);
        e.Handled = true;
    }

    /// <summary>
    /// 跟随（样稿 tlFollow）：换了设备时那一组的组头滚到顶部；再让目标行可见（组头吸顶：行不能藏在组头下面，也不能在可视区下方）；
    /// 另一台设备上选中的行同样滚到可见处。等这一轮排版完成再滚。
    /// </summary>
    private void OnFollow(object? sender, TlFollow f)
    {
        Dispatcher.UIThread.Post(() => Follow(f), DispatcherPriority.Background);
    }

    private void Follow(TlFollow f)
    {
        if (_vm == null || !Body.IsEffectivelyVisible) return;
        var group = GroupPanel(f.K);
        if (group == null) return;
        double groupTop = Top(group);
        if (f.ScrollGroupToTop) Body.Offset = Body.Offset.WithY(Math.Max(0, Math.Min(groupTop, Body.Extent.Height - Body.Viewport.Height)));
        if (f.N is int n) Reveal(group, f.K, n);
        if (f.Other is { } o && GroupPanel(o.K) is { } g2) Reveal(g2, o.K, o.N);
    }

    private StickyHeadPanel? GroupPanel(int k) =>
        Groups.GetVisualDescendants().OfType<StickyHeadPanel>().FirstOrDefault(p => p.DataContext is TlGroupVm g && g.K == k);

    /// <summary>元素在正文内容里的纵向位置（滚动前）。</summary>
    private double Top(Visual v) => (v.TranslatePoint(default, Groups) ?? default).Y;

    private void Reveal(StickyHeadPanel group, int k, int n)
    {
        var row = group.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tlrow") && b.DataContext is TlRowVm r && r.R.N == n && r.D.K == k);
        if (row == null) return;
        double head = group.Children.Count > 0 ? group.Children[0].Bounds.Height : 36;
        double top = Top(row), bot = top + row.Bounds.Height;
        double y = Body.Offset.Y, h = Body.Viewport.Height;
        if (top < y + head) Body.Offset = Body.Offset.WithY(Math.Max(0, top - head));
        else if (bot > y + h) Body.Offset = Body.Offset.WithY(bot - h);
    }
}
