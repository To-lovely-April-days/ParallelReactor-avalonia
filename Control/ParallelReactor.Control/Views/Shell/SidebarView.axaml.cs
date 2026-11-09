using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>紫色图标栏（外框第 1 列，跨工作区与时间轴两行）：设备开关、总览、第二阶段的五个视图、报警数、底部收起 / 展开（专注模式）。数据：ShellVm.Sidebar。</summary>
public partial class SidebarView : UserControl
{
    public SidebarView() => InitializeComponent();
}
