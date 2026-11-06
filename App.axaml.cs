using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ParallelReactor.Services;
using ParallelReactor.ViewModels;
using ParallelReactor.Views;

namespace ParallelReactor;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>控制循环（C1）：在 View 之前创建并启动，与 View 是否加载无关。</summary>
    private ControlLoop? _loop;

    public override void OnFrameworkInitializationCompleted()
    {
        var vm = new MainViewModel();
        _loop = new ControlLoop(vm);
        vm.Loop = _loop;
        _loop.Start();

        // X11/桌面：窗口外壳 MainWindow 托管 MainView。
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 只在真正退出（Exit）时停循环：ShutdownRequested 之后仍可能被取消（Window.Closing / 其他处理器置 Cancel），
            // 那时停掉循环会让程序继续运行却没有超温 / 超压联锁与急停补发。ConfirmExit 走 ShutdownApp，自己先停循环。
            desktop.Exit += (_, _) => _loop?.Stop();
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        // DRM/单视图直出（PR_DRM=1，无 X）：直接把 MainView 上屏。
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            System.AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _loop?.Stop());
            single.MainView = new MainView { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
