using Avalonia.Controls;
using Avalonia.Input;
using ParallelReactor.Control.Pages.Home;

namespace ParallelReactor.Control.Views.Pages.Home;

/// <summary>共用资源面板（见 ResPaneView.axaml）。转速框：聚焦时刷新不覆盖，回车 / 失焦写入（釜转速，按设备的上限夹住、取整到 10）。</summary>
public partial class ResPaneView : UserControl
{
    public ResPaneView()
    {
        InitializeComponent();
        ToolsRow.SizeChanged += (_, _) => FitTools();
        RpmIn.GotFocus += (_, _) => { if (DataContext is ResPaneVm vm) vm.EditingRpm = true; };
        RpmIn.LostFocus += (_, _) => { if (DataContext is ResPaneVm vm && vm.EditingRpm) vm.ApplyRpmCommand.Execute(null); };
        RpmIn.KeyDown += (_, e) =>
        {
            if (DataContext is not ResPaneVm vm) return;
            if (e.Key == Key.Enter) { vm.ApplyRpmCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Escape) { vm.EditingRpm = false; e.Handled = true; }
        };
    }

    /// <summary>点气路小块：开关这一路的阀（HMI v0.7，不经维护模式）。</summary>
    private void OnValvePill(object? sender, TappedEventArgs e)
    {
        if (DataContext is ResPaneVm vm && sender is Avalonia.Controls.Control { DataContext: ValvePillVm v }) { vm.TogglePill(v); e.Handled = true; }
    }

    /// <summary>窄的时候会话另起一行（样稿窄屏 .res .sess{margin-left:0} + 换行）。</summary>
    private void FitTools()
    {
        var inf = new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity);
        Sess.Measure(inf);
        double btns = 0;
        foreach (var c in ((StackPanel)ToolsRow.Children[1]).Children) { c.Measure(inf); btns += c.DesiredSize.Width + 8; }
        bool wrap = ToolsRow.Bounds.Width < btns + Sess.DesiredSize.Width + 8;
        var dock = wrap ? Dock.Bottom : Dock.Right;
        if (DockPanel.GetDock(Sess) != dock)
        {
            DockPanel.SetDock(Sess, dock);
            Sess.Margin = wrap ? new Avalonia.Thickness(0, 8, 0, 0) : new Avalonia.Thickness(8, 0, 0, 0);
            Sess.HorizontalAlignment = wrap ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
        }
    }
}
