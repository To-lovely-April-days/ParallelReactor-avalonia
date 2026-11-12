using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>状态栏（30 px）：连接、远程控制、报警（可点，选中有报警的对象）、会话 …… 示例型号说明（演示时带「演示数据」）、时钟。数据：ShellVm.Status。</summary>
public partial class StatusBarView : UserControl
{
    public StatusBarView() => InitializeComponent();
}
