using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ParallelReactor.Control.Pages.Settings.Pid;

namespace ParallelReactor.Control.Views.Pages.Settings;

/// <summary>
/// 设置 → 温控 / PID（见 PidSection.axaml）。窄屏切换右边的宽（600 / 440）、内边距（12 16 / 10 12）和监视标题的排法
/// （宽屏一行，「取消整定」在右；窄屏标题折行、按钮在标题下面，同样稿 .pidmon .pmh{flex-wrap}）。
/// </summary>
public partial class PidSection : UserControl
{
    private PidSectionVm? _vm;

    public PidSection()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as PidSectionVm;
        if (_vm != null) _vm.PropertyChanged += OnVm;
        Apply();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PidSectionVm.IsNarrow)) Apply();
    }

    private void Apply()
    {
        bool nar = _vm?.IsNarrow == true;
        if (this.FindControl<Border>("Side") is { } side) side.Width = nar ? 440 : 600;
        if (this.FindControl<Panel>("SidePad") is { } pad) pad.Margin = nar ? new Avalonia.Thickness(12, 10) : new Avalonia.Thickness(16, 12);
        if (this.FindControl<Button>("CancelBtn") is { } btn)
        {
            DockPanel.SetDock(btn, nar ? Dock.Bottom : Dock.Right);
            btn.HorizontalAlignment = nar ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
            btn.Margin = nar ? new Avalonia.Thickness(0, 8, 0, 0) : default;
        }
    }
}
