using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>Toast 宿主（距外框底 48、居中、可堆叠，不接收指针）。数据：ShellVm.Toasts（Services/ToastService）。</summary>
public partial class ToastHost : UserControl
{
    public ToastHost() => InitializeComponent();
}
