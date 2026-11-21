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
}
