using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ParallelReactor.Control.Views.Pages.Settings;

/// <summary>「取消整定」确认对话框的内容（PidCancelDialogVm.Body）。</summary>
public partial class PidCancelView : UserControl
{
    public PidCancelView() => AvaloniaXamlLoader.Load(this);
}
