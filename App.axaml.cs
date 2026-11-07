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

    /// <summary>对外接口（设计稿 P2 只读 + P3 控制权 / 写命令 + P4 会话）：api.json enabled=true 或 PR_API=1 时在后台启动 Kestrel；否则为 null。</summary>
    private ParallelReactor.Api.ApiHost? _api;
    /// <summary>控制权的 HMI 侧桥（租约计时、onRemoteLost）；接口未启用为 null。</summary>
    private ParallelReactor.Api.Hmi.ControlBridge? _controlBridge;

    public override void OnFrameworkInitializationCompleted()
    {
        // systemd stop（SIGTERM）等非「退出程序」路径：两种生命周期都先停接口（WS 客户端收到关闭帧、「接口已停止」能落盘）。
        // 必须在 MainViewModel 之前注册：ProcessExit 按注册顺序执行，VM 构造函数里注册的 Logger.Stop 要排在接口之后
        System.AppDomain.CurrentDomain.ProcessExit += (_, _) => _api?.Stop(System.TimeSpan.FromSeconds(2));
        var vm = new MainViewModel();
        _loop = new ControlLoop(vm);
        vm.Loop = _loop;
        _loop.Start();

        // 接口在 MainViewModel / ControlLoop 之后启动：Kestrel 线程只读 ControlLoop.Snapshots 与 Logger.Events，不碰 VM
        var apiCfg = ParallelReactor.Api.ApiConfig.LoadWithEnv(SettingsStore.ApiConfigPath, out var apiCfgError);
        _api = ParallelReactor.Api.ApiHost.TryStart(new ParallelReactor.Api.Hmi.HmiApiSources(vm, _loop), apiCfg, apiCfgError);
        if (_api != null)
        {
            _controlBridge = new ParallelReactor.Api.Hmi.ControlBridge(vm, _api);
            vm.ApiStop = () => { _controlBridge?.Dispose(); _api.Stop(System.TimeSpan.FromSeconds(2)); };
        }
#if DEBUG
        DevHooks.Install(vm);   // 仅 Debug 构建 + PR_SIM=1 + PR_DEV_PROGRAMS 时生效（多程序并行的无头验收，经命令层下发）；发布构建不含此路径
#endif

        // X11/桌面：窗口外壳 MainWindow 托管 MainView。
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 只在真正退出（Exit）时停循环：ShutdownRequested 之后仍可能被取消（Window.Closing / 其他处理器置 Cancel），
            // 那时停掉循环会让程序继续运行却没有超温 / 超压联锁与急停补发。ConfirmExit 走 ShutdownApp，自己先停循环。
            desktop.Exit += (_, _) => { _api?.Stop(System.TimeSpan.FromSeconds(2)); _loop?.Stop(); };
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        // DRM/单视图直出（PR_DRM=1，无 X）：直接把 MainView 上屏。
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            System.AppDomain.CurrentDomain.ProcessExit += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _loop?.Stop());
            single.MainView = new MainView { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
