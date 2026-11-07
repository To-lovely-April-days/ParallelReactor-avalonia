using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ParallelReactor.Control.ViewModels;

namespace ParallelReactor.Control.Views;

/// <summary>曲线面板；DataContext 为 ChartVm，展开 / 关闭按钮经可视树找到 MainViewModel。</summary>
public partial class GraphView : UserControl
{
    public static readonly StyledProperty<string> PanelKeyProperty = AvaloniaProperty.Register<GraphView, string>(nameof(PanelKey), "graph");
    public string PanelKey { get => GetValue(PanelKeyProperty); set => SetValue(PanelKeyProperty, value); }

    public GraphView()
    {
        InitializeComponent();
        this.FindControl<Button>("ExpandButton")!.Click += (_, _) => Main()?.ToggleExpand(PanelKey);
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Main()?.NotImplemented("关闭面板");
    }

    private MainViewModel? Main() => this.FindAncestorOfType<MainView>()?.DataContext as MainViewModel;
}
