using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ParallelReactor.Control.ViewModels;

namespace ParallelReactor.Control.Views;

/// <summary>性能测试：测试进度 / 报告面板（DataContext = PerfViewModel）。展开 / 关闭按钮经可视树找 MainViewModel；录入框回车 = 确认 / 继续。</summary>
public partial class PerfProgressView : UserControl
{
    public PerfProgressView()
    {
        InitializeComponent();
        this.FindControl<Button>("ExpandButton")!.Click += (_, _) => Main()?.ToggleExpand("perfprogress");
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Main()?.NotImplemented("关闭面板");
        // 任意录入框回车 = 「确认 / 继续」
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter && e.Source is TextBox && DataContext is PerfViewModel vm && vm.IsWaitingEntry) { vm.ConfirmContinue(); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    private MainViewModel? Main() => this.FindAncestorOfType<MainView>()?.DataContext as MainViewModel;
}
