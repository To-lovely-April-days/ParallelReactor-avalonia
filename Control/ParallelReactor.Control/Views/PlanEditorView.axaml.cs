using Avalonia.Controls;
using ParallelReactor.Control.Pages;

namespace ParallelReactor.Control.Views;

public partial class PlanEditorView : UserControl
{
    public PlanEditorView()
    {
        InitializeComponent();
        // 分隔线 left 56 / right 45 需要画布实际宽度
        var host = this.FindControl<Border>("CanvasHost")!;
        host.SizeChanged += (_, e) => { if (DataContext is LegacyProgramPageVm vm && e.NewSize.Width > 0) vm.Plan.CanvasWidth = e.NewSize.Width - 8; };
        // Canvas 只读直接子级（容器 ContentPresenter）的 Canvas.Left/Top：容器生成时绑到项的 Left/Top
        var items = this.FindControl<ItemsControl>("PlanItems")!;
        items.ContainerPrepared += (_, e) =>
        {
            if (e.Container is Avalonia.Controls.Presenters.ContentPresenter cp)
            {
                cp.Bind(Canvas.LeftProperty, new Avalonia.Data.Binding("Left"));
                cp.Bind(Canvas.TopProperty, new Avalonia.Data.Binding("Top"));
            }
        };
    }
}
