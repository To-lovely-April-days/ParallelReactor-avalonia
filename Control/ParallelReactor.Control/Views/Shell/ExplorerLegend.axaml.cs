using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>状态图例（资源管理器标题栏 ⓘ 弹出）：颜色只表示三件事（浅绿 = 正在运行，红 = 报警，灰 = 其它），具体状态看字。</summary>
public partial class ExplorerLegend : UserControl
{
    public ExplorerLegend() => InitializeComponent();
}
