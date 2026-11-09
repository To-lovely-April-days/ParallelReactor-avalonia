using Avalonia.Controls;
using Avalonia.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>对话框宿主：点遮罩、点对话框的 × 都关闭（同样稿 closeDialog）；Esc 由窗口交给 ShellVm.Escape。</summary>
public partial class DialogHost : UserControl
{
    public DialogHost()
    {
        InitializeComponent();
        Scrim.PointerPressed += (_, e) => { (DataContext as ShellVm)?.Dialogs.Close(); e.Handled = true; };
        AddHandler(DialogFrame.CloseRequestedEvent, (_, e) => { (DataContext as ShellVm)?.Dialogs.Close(); e.Handled = true; });
    }
}
