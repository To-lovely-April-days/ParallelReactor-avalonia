using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Pages;

/// <summary>
/// 反应釜页（M5）：状态条（报警 / 提醒 / 等待进气 / 急停 / 连接中断，带这条报警唯一的「确认」与「立即重连」）+ 面板网格（Views/Pages/Reactor/ReactorGridPanel）：
/// 「程序」（ReactorProgramPanel）｜「釜体 / 属性查看器」（ReactorVesselPanel）+「曲线」（ReactorCurvesPanel）；未安装的位置是 ReactorUnPanel。
/// 视图模型 Pages/Reactor/ReactorPageVm。
/// </summary>
public partial class ReactorPage : UserControl
{
    public ReactorPage() => InitializeComponent();
}
