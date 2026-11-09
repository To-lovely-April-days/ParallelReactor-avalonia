using Avalonia.Controls;
using Avalonia.Input;
using ParallelReactor.Control.ViewModels;
using ParallelReactor.Control.Views.Legacy;

namespace ParallelReactor.Control.Views;

/// <summary>性能测试：测试进度 / 报告面板（DataContext = PerfViewModel）。展开 / 关闭按钮交给所在的旧样式页面（Pages.ILegacyPage）；录入框回车 = 确认 / 继续。</summary>
public partial class PerfProgressView : UserControl
{
    public PerfProgressView()
    {
        InitializeComponent();
        this.FindControl<Button>("ExpandButton")!.Click += (_, _) => LegacyHost.PageOf(this)?.ToggleExpand("perfprogress");
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => LegacyHost.PageOf(this)?.NotImplemented("关闭面板");
        // 任意录入框回车 = 「确认 / 继续」
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter && e.Source is TextBox && DataContext is PerfViewModel vm && vm.IsWaitingEntry) { vm.ConfirmContinue(); e.Handled = true; } }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

}
