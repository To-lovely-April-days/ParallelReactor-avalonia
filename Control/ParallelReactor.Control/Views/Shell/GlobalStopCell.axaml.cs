using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>全局停止格（页签栏右端、检查器上方）：全部停止 + 唯一的红色急停，ZIndex 201 压在对话框遮罩之上。数据：ShellVm.StopCell（对话框在 M3）。</summary>
public partial class GlobalStopCell : UserControl
{
    public GlobalStopCell() => InitializeComponent();
}
