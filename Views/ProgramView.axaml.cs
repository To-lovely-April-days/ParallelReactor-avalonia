using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using ParallelReactor.Models;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Controls;

public partial class ProgramView : UserControl
{
    private const string Fmt = "progitem";
    private Point _press;
    private object? _dragSrc;     // PaletteItem 或 ProgramStep
    private bool _dragging;

    public ProgramView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPreviewPointerMoved, RoutingStrategies.Tunnel);

        var dz = this.FindControl<Border>("DropZone");
        if (dz != null)
        {
            dz.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            dz.AddHandler(DragDrop.DropEvent, OnDrop);
        }
    }

    // 记录按下的拖拽源（步骤库卡片或时间线步骤块）
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragSrc = FindDragSource(e.Source as Control);
        _press = e.GetPosition(this);
        _dragging = false;
    }

    private async void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSrc == null || _dragging) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragSrc = null; return; }

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _press.X) + Math.Abs(p.Y - _press.Y) < 6) return;

        _dragging = true;
        var data = new DataObject();
        data.Set(Fmt, _dragSrc);
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _dragSrc = null;
            _dragging = false;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(Fmt) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(Fmt)) return;
        var src = e.Data.Get(Fmt);
        if (DataContext is not ProgramViewModel vm) return;

        int index = ComputeDropIndex(e);
        if (src is PaletteItem pi) vm.DropPaletteAt(pi.Type, index);
        else if (src is ProgramStep st) vm.ReorderTo(st, index);
        e.Handled = true;
    }

    // 计算插入位置：指针在某块中线以上 → 插到该块之前
    private int ComputeDropIndex(DragEventArgs e)
    {
        var list = this.FindControl<ListBox>("Timeline");
        if (list == null || DataContext is not ProgramViewModel vm) return 0;
        int n = vm.Steps.Count;
        var pt = e.GetPosition(list);
        for (int i = 0; i < n; i++)
        {
            if (list.ContainerFromIndex(i) is not Control c) continue;
            var top = c.TranslatePoint(new Point(0, 0), list) ?? default;
            double mid = top.Y + c.Bounds.Height / 2;
            if (pt.Y < mid) return i;
        }
        return n;
    }

    // 从被点元素向上找到拖拽数据源（仅时间线步骤块支持拖拽排序；步骤库用点击添加）
    private static object? FindDragSource(Control? start)
    {
        var v = start as Visual;
        while (v != null)
        {
            if (v is Control c && c.DataContext is ProgramStep step)
                return step;
            v = v.GetVisualParent();
        }
        return null;
    }
}
