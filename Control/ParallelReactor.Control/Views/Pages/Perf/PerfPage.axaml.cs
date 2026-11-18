using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ParallelReactor.Control.Pages.Perf;

namespace ParallelReactor.Control.Views.Pages.Perf;

/// <summary>
/// 性能测试页（见 PerfPage.axaml）。按模式 / 有没有测试计划 / 宽窄 / 高矮换版面（样稿 grid-template 不能用样式改）：
/// 左列 400（窄屏 340），结果一行 230（矮屏 170）；进度条按比例设宽。回车 = 让出焦点（录入框照常提交）。
/// </summary>
public partial class PerfPage : UserControl
{
    private PerfPageVm? _vm;

    public PerfPage()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        this.FindControl<Border>("Bar")!.SizeChanged += (_, _) => ApplyBar();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as PerfPageVm;
        if (_vm != null) { _vm.PropertyChanged += OnVm; Apply(); }
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PerfPageVm.ModeKey) or nameof(PerfPageVm.PlanOk) or nameof(PerfPageVm.IsNarrow) or nameof(PerfPageVm.IsShort)) Apply();
        else if (e.PropertyName == nameof(PerfPageVm.Progress)) ApplyBar();
    }

    private void Apply()
    {
        if (_vm == null) return;
        bool leak = _vm.IsLeak, plan = _vm.PlanOk;
        this.FindControl<Border>("NoPlan")!.IsVisible = !leak && !plan;
        this.FindControl<Border>("Items")!.IsVisible = !leak && plan;
        this.FindControl<Grid>("Right")!.IsVisible = !leak && plan;
        this.FindControl<Border>("LeakSet")!.IsVisible = leak;
        this.FindControl<Border>("LeakRes")!.IsVisible = leak;
        var root = this.FindControl<Grid>("Root")!;
        double w = _vm.IsNarrow ? 340 : 400;
        if (root.ColumnDefinitions[0].Width.Value != w) root.ColumnDefinitions[0].Width = new GridLength(w);
        var right = this.FindControl<Grid>("Right")!;
        double h = _vm.IsShort ? 170 : 230;
        if (right.RowDefinitions[2].Height.Value != h) right.RowDefinitions[2].Height = new GridLength(h);
        ApplyBar();
    }

    private void ApplyBar()
    {
        if (_vm == null) return;
        var bar = this.FindControl<Border>("Bar")!;
        var fill = this.FindControl<Border>("BarFill")!;
        fill.Width = Math.Max(0, bar.Bounds.Width * Math.Clamp(_vm.Progress, 0, 1));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox && e.Key == Key.Enter)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }
}
