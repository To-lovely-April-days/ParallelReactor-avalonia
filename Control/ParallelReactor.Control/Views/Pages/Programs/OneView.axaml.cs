using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ParallelReactor.Control.Controls.Programs;
using ParallelReactor.Control.Pages.Programs;

namespace ParallelReactor.Control.Views.Pages.Programs;

/// <summary>单路程序（见 OneView.axaml）：右列宽 372（窄屏 340）；预计曲线高 176（矮屏 112）。</summary>
public partial class OneView : UserControl
{
    private ProgramPageVm? _vm;

    // 拖动排序：按下的那一步、按下的位置、是否已经开始拖（移动超过 DragStart 才算，避免和点击选中冲突）
    private const double DragStart = 6;
    private int _dragFrom = -1;
    private Point _pressAt;
    private bool _dragging;
    private Avalonia.Controls.Control? _dragRow;
    private ItemsControl? _list;
    private Border? _line;

    public OneView()
    {
        AvaloniaXamlLoader.Load(this);
        _list = this.FindControl<ItemsControl>("StepList");
        _line = this.FindControl<Border>("DropLine");
        if (_list != null)
        {
            // 隧道 + 已处理的也收：行是 Button，按下时它会捕获指针并标记已处理
            _list.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            _list.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            _list.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            _list.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
            // 右键：先选中这一行（菜单的操作也是针对它）
            _list.AddHandler(ContextRequestedEvent, OnContext, RoutingStrategies.Tunnel, handledEventsToo: true);
            _list.KeyDown += OnKey;
        }
    }

    private static StepRowVm? RowOf(object? source) =>
        source is StyledElement e ? FindRow(e) : null;

    private static StepRowVm? FindRow(StyledElement? e)
    {
        for (; e != null; e = e.Parent) if (e.DataContext is StepRowVm r && e is Button) return r;
        return null;
    }

    private void OnContext(object? sender, ContextRequestedEventArgs e)
    {
        if (RowOf(e.Source) is { } r) r.PickCommand.Execute(null);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm?.One is { } one && one.DeleteReason is null) { one.DeleteCommand.Execute(null); e.Handled = true; }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        EndDrag();
        if (_list is null || _vm is null || !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
        if (RowOf(e.Source) is { } r && _vm.One.CanDrag(r.I))
        {
            _dragFrom = r.I;
            _pressAt = e.GetPosition(_list);
            _dragRow = _list.ContainerFromIndex(r.I);
        }
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom < 0 || _list is null) return;
        var pt = e.GetPosition(_list);
        if (!_dragging)
        {
            if (Math.Abs(pt.Y - _pressAt.Y) < DragStart) return;
            _dragging = true;
            if (_dragRow != null) _dragRow.Opacity = 0.45;
            _list.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        }
        var (gap, y) = GapAt(pt.Y);
        if (_line != null)
        {
            _line.IsVisible = gap != _dragFrom && gap != _dragFrom + 1;
            _line.Width = Math.Max(0, _list.Bounds.Width - 16);
            Canvas.SetLeft(_line, 8);
            Canvas.SetTop(_line, y - 1);
        }
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging && _list != null && _vm != null)
        {
            var (gap, _) = GapAt(e.GetPosition(_list).Y);
            _vm.One.MoveTo(_dragFrom, gap);
            e.Handled = true;   // 拖动结束不算点击
        }
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragRow != null) _dragRow.Opacity = 1;
        if (_list != null) _list.Cursor = null;
        if (_line != null) _line.IsVisible = false;
        _dragging = false;
        _dragFrom = -1;
        _dragRow = null;
    }

    /// <summary>指针在列表里的 y → 第几个空隙（0 = 第一步前面）和插入线的位置。「结束」那一行之后的空隙按它前面算（结束固定在最后）。</summary>
    private (int Gap, double Y) GapAt(double y)
    {
        if (_list is null) return (0, 0);
        int n = _list.ItemCount;
        int endRow = -1;
        for (int i = 0; i < n; i++) if (_list.ContainerFromIndex(i)?.DataContext is StepRowVm { IsEnd: true }) endRow = i;
        int limit = endRow >= 0 ? endRow : n;
        for (int i = 0; i < limit; i++)
        {
            if (_list.ContainerFromIndex(i) is not { } c) continue;
            var b = c.Bounds;
            if (y < b.Top + b.Height / 2) return (i, b.Top);
        }
        if (limit > 0 && _list.ContainerFromIndex(limit - 1) is { } last) return (limit, last.Bounds.Bottom);
        return (0, 0);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as ProgramPageVm;
        if (_vm != null) { _vm.PropertyChanged += OnVm; Apply(); }
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProgramPageVm.IsNarrow) or nameof(ProgramPageVm.IsShort)) Apply();
    }

    private void Apply()
    {
        if (_vm == null) return;
        var root = this.FindControl<Grid>("Root");
        if (root != null) root.ColumnDefinitions[2].Width = new GridLength(_vm.IsNarrow ? 340 : 372);
        if (this.FindControl<PlanPreviewChart>("Preview") is { } p) p.Height = _vm.IsShort ? 112 : 176;
    }
}
