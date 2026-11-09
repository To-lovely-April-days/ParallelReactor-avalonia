using Avalonia;
using Avalonia.Controls;
using ParallelReactor.Control.Views.Legacy;

namespace ParallelReactor.Control.Views;

/// <summary>曲线面板（旧样式）；DataContext 为 ChartVm，展开 / 关闭按钮交给所在的旧样式页面（LegacyHost 的 DataContext，Pages.ILegacyPage）。</summary>
public partial class GraphView : UserControl
{
    public static readonly StyledProperty<string> PanelKeyProperty = AvaloniaProperty.Register<GraphView, string>(nameof(PanelKey), "graph");
    public string PanelKey { get => GetValue(PanelKeyProperty); set => SetValue(PanelKeyProperty, value); }

    public GraphView()
    {
        InitializeComponent();
        this.FindControl<Button>("ExpandButton")!.Click += (_, _) => LegacyHost.PageOf(this)?.ToggleExpand(PanelKey);
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => LegacyHost.PageOf(this)?.NotImplemented("关闭面板");
    }
}
