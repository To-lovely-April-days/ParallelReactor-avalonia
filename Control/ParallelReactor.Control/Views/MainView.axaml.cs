using Avalonia.Controls;
using Avalonia.Input;
using ParallelReactor.Control.ViewModels;

namespace ParallelReactor.Control.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        // 点击菜单外 / 背板：收起菜单、关闭展开面板
        this.FindControl<Border>("MenuCatcher")!.PointerPressed += (_, e) => { (DataContext as MainViewModel)?.CloseMenus(); e.Handled = true; };
        this.FindControl<Border>("Backdrop")!.PointerPressed += (_, e) => { (DataContext as MainViewModel)?.CloseExpanded(); e.Handled = true; };
    }
}
