using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using ParallelReactor.Models;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Controls;

public partial class ProgramView : UserControl
{
    // 拖拽阈值（手指/鼠标移动超过该距离才视为拖拽，避免误触影响点击）
    private const double DragThreshold = 6;

    private Point _press;
    private object? _dragSrc;       // PaletteItem（步骤库）或 ProgramStep（时间线）
    private Control? _dragRow;       // 被拖元素所在的卡片/行（用于淡化）
    private bool _dragging;

    // 浮层元素
    private Border? _dropZone;
    private ListBox? _timeline;
    private Border? _ghost;
    private Path? _ghostPath;
    private TextBlock? _ghostText;
    private Panel? _indicator;

    public ProgramView()
    {
        AvaloniaXamlLoader.Load(this);

        // 用隧道（Tunnel）预览事件在子控件之前拿到按下/移动，保证点击仍可正常触发
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPreviewPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost);

        _dropZone = this.FindControl<Border>("DropZone");
        _timeline = this.FindControl<ListBox>("Timeline");
        _ghost = this.FindControl<Border>("DragGhost");
        _ghostPath = this.FindControl<Path>("GhostPath");
        _ghostText = this.FindControl<TextBlock>("GhostText");
        _indicator = this.FindControl<Panel>("DropIndicator");
    }

    // 记录按下的拖拽源（步骤库卡片或时间线步骤块）
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragSrc = null; return; }
        _dragSrc = FindDragSource(e.Source as Control, out _dragRow);
        _press = e.GetPosition(this);
        _dragging = false;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSrc == null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndDrag(); return; }

        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _press.X) + Math.Abs(p.Y - _press.Y) < DragThreshold) return;
            BeginDrag(e);
        }
        UpdateDrag(p);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            CompleteDrag(e.GetPosition(this));
            e.Handled = true;
        }
        EndDrag();
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndDrag();

    // ============ 拖拽过程 ============

    private void BeginDrag(PointerEventArgs e)
    {
        _dragging = true;
        e.Pointer.Capture(this); // 捕获指针：移动/抬起都回到本控件，且抑制源按钮的点击（避免重复添加）

        // 填充幽灵预览内容
        string name = "";
        Geometry? icon = null;
        IBrush? brush = null;
        switch (_dragSrc)
        {
            case PaletteItem pi: name = pi.Name; icon = pi.Icon; brush = pi.IconBrush; break;
            case ProgramStep st: name = st.Name; icon = st.Icon; brush = st.IconBrush; break;
        }
        if (_ghostText != null) _ghostText.Text = name;
        if (_ghostPath != null) { _ghostPath.Data = icon; _ghostPath.Stroke = brush; }
        if (_ghost != null) _ghost.IsVisible = true;

        // 淡化源卡片
        _dragRow?.Classes.Add("dragsrc");
    }

    private void UpdateDrag(Point p)
    {
        // 幽灵跟随指针，略偏右上，避免被手指遮挡
        if (_ghost != null)
        {
            Canvas.SetLeft(_ghost, p.X + 14);
            Canvas.SetTop(_ghost, p.Y - 16);
        }

        bool over = PointerOverDropZone(p);
        SetDropActive(over);

        if (over && _timeline is { IsVisible: true } list && _indicator != null)
        {
            ComputeDropIndex(p, out double lineY, out double lineX, out double lineW);
            _indicator.Width = Math.Max(0, lineW - 12);
            _indicator.Height = 9;
            Canvas.SetLeft(_indicator, lineX + 6);
            Canvas.SetTop(_indicator, lineY - 4.5);
            _indicator.IsVisible = true;
        }
        else if (_indicator != null)
        {
            _indicator.IsVisible = false;
        }
    }

    private void CompleteDrag(Point p)
    {
        if (DataContext is not ProgramViewModel vm) return;
        if (!PointerOverDropZone(p)) return;

        int index = (_timeline is { IsVisible: true })
            ? ComputeDropIndex(p, out _, out _, out _)
            : vm.Steps.Count;

        if (_dragSrc is PaletteItem pi) vm.DropPaletteAt(pi.Type, index);
        else if (_dragSrc is ProgramStep st) vm.ReorderTo(st, index);
    }

    private void EndDrag()
    {
        if (_ghost != null) _ghost.IsVisible = false;
        if (_indicator != null) _indicator.IsVisible = false;
        SetDropActive(false);
        _dragRow?.Classes.Remove("dragsrc");
        _dragRow = null;
        _dragSrc = null;
        _dragging = false;
    }

    // ============ 命中与定位 ============

    private bool PointerOverDropZone(Point pThis)
    {
        if (_dropZone == null) return false;
        var tl = _dropZone.TranslatePoint(new Point(0, 0), this) ?? default;
        return pThis.X >= tl.X && pThis.Y >= tl.Y
            && pThis.X <= tl.X + _dropZone.Bounds.Width
            && pThis.Y <= tl.Y + _dropZone.Bounds.Height;
    }

    private void SetDropActive(bool on)
    {
        if (_dropZone == null) return;
        if (on) { if (!_dropZone.Classes.Contains("dropactive")) _dropZone.Classes.Add("dropactive"); }
        else _dropZone.Classes.Remove("dropactive");
    }

    // 计算插入位置（指针在某块中线以上 → 插到该块之前），并给出指示线在本控件中的几何
    private int ComputeDropIndex(Point pThis, out double lineY, out double lineX, out double lineW)
    {
        lineY = 0; lineX = 0; lineW = 0;
        var list = _timeline;
        if (list == null || DataContext is not ProgramViewModel vm) return 0;

        var listTL = list.TranslatePoint(new Point(0, 0), this) ?? default;
        lineX = listTL.X;
        lineW = list.Bounds.Width;

        int n = vm.Steps.Count;
        for (int i = 0; i < n; i++)
        {
            if (list.ContainerFromIndex(i) is not Control c) continue;
            var top = c.TranslatePoint(new Point(0, 0), list) ?? default;
            double mid = top.Y + c.Bounds.Height / 2;
            if (pThis.Y - listTL.Y < mid) { lineY = listTL.Y + top.Y - 4; return i; }
        }

        // 落在末尾：指示线放到最后一块下方
        if (n > 0 && list.ContainerFromIndex(n - 1) is Control last)
        {
            var top = last.TranslatePoint(new Point(0, 0), list) ?? default;
            lineY = listTL.Y + top.Y + last.Bounds.Height + 4;
        }
        else lineY = listTL.Y + 6;
        return n;
    }

    // 从被点元素向上找拖拽数据源：步骤库卡片(PaletteItem) 或 时间线步骤块(ProgramStep)
    private static object? FindDragSource(Control? start, out Control? row)
    {
        row = null;
        var v = start as Visual;
        while (v != null)
        {
            if (v is Control c && c.DataContext is PaletteItem pi) { row = NearestRow(c); return pi; }
            if (v is Control c2 && c2.DataContext is ProgramStep st) { row = NearestRow(c2); return st; }
            v = v.GetVisualParent();
        }
        return null;
    }

    // 向上找到可淡化的"行容器"：步骤库的卡片按钮 或 时间线的列表项
    private static Control? NearestRow(Visual? start)
    {
        var v = start;
        while (v != null)
        {
            if (v is ListBoxItem li) return li;
            if (v is Button b && b.Classes.Contains("pstep")) return b;
            v = v.GetVisualParent();
        }
        return start as Control;
    }
}
