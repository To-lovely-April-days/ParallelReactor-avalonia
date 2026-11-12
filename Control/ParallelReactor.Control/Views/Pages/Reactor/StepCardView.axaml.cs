using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 一张步骤卡片（样稿 ui.stepCard，视图模型 <see cref="Pages.StepCardVm"/>）：反应釜页的「程序」画布与组件陈列「步骤卡片」分区共用。
/// 宽度由画布（<see cref="Views.Pages.Reactor.StepFlowPanel"/>）给，高度按内容（三节固定行高：339）。
/// </summary>
public partial class StepCardView : UserControl
{
    public StepCardView() => InitializeComponent();
}
