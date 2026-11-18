using Avalonia.Controls;
using Avalonia.Input;
using ParallelReactor.Control.Pages.Home;

namespace ParallelReactor.Control.Views.Pages.Home;

/// <summary>灰带上的共用搅拌（见 StirCtlView.axaml）。转速框：聚焦时刷新不覆盖，回车 / 失焦写入（同共用资源面板）。</summary>
public partial class StirCtlView : UserControl
{
    public StirCtlView()
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

    /// <summary>不写电流（面板窄于 800，样稿 @container pm (max-width:800px) .amp）。</summary>
    public bool ShowAmp { get => Amp.IsVisible; set => Amp.IsVisible = value; }
}
