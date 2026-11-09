using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views;

namespace ParallelReactor.Control;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // UiFont 按操作系统选择（PORT-PLAN §4.3），必须在创建任何窗口之前写入；旧样式的页面用 LegacyUiFont，不受影响
        Resources["UiFont"] = UiFonts.Create();
        Resources["UiFontCjk"] = UiFonts.CreateCjk();
        // Fluent 资源覆盖（强调色 → 紫色令牌、内容字体 → UiFont、内容字号 → 15）：M2 起全局（Fluent 的派生画刷在应用级解析，只能挂在这里）
        Resources.MergedDictionaries.Add(FluentOverrides.Create());
    }

    /// <summary>把一次刷新排到 UI 线程（Background：不抢输入与渲染；多次请求由 FleetVm / LiveDeviceSource 自己合并）。</summary>
    public static void PostToUi(Action a) => Dispatcher.UIThread.Post(a, DispatcherPriority.Background);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if DEBUG
            // 演示模式（PR_CONTROL_DEMO=1，仅 Debug）：不连接任何设备，新外壳 + Core 的演示数据（Shot/ShotRunner.Demo.cs）
            if (Shot.ShotRunner.IsDemo) { Shot.ShotRunner.StartDemo(desktop); base.OnFrameworkInitializationCompleted(); return; }
#endif
            // 实时：新外壳 + FleetVm（开始时没有设备；「设备 › 连接…」用临时的「连接设备」对话框连上一台，M3 起多台）。
            // Client 的事件经 AvaloniaSynchronizationContext 投递到 UI 线程；数据源把它们合并后就地刷新（R10）。
            var config = ControlConfig.Load();
            var fleet = FleetVm.CreateLive(config, PostToUi, new AvaloniaSynchronizationContext(DispatcherPriority.Normal));
            var shell = new ShellVm(fleet);
            var ticker = new UiTicker();
            ticker.Tick += fleet.OnTick;
            ticker.Start();
            var window = new MainWindow { DataContext = shell };

            // 截图 / 无头尺寸：PR_CONTROL_SHOT_SIZE=宽x高（默认 1440x900）
            var size = Environment.GetEnvironmentVariable("PR_CONTROL_SHOT_SIZE");
            if (!string.IsNullOrWhiteSpace(size))
            {
                var parts = size.ToLowerInvariant().Split('x');
                if (parts.Length == 2 && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h))
                { window.Width = w; window.Height = h; }
            }

            var shotDir = Environment.GetEnvironmentVariable("PR_CONTROL_SHOT");
            if (!string.IsNullOrWhiteSpace(shotDir))
            {
                window.SystemDecorations = SystemDecorations.None;
                window.CanResize = false;
                if (string.IsNullOrWhiteSpace(size)) { window.Width = 1440; window.Height = 900; }
#if DEBUG
                window.Opened += (_, _) => Shot.ShotRunner.Start(window, shell, desktop, shotDir);
#endif
            }

            // 退出：关窗 → ShutdownRequested（可取消）→ 先异步收尾（中止性能测试、释放控制权、断开，≤ 5 s），再真正 Shutdown。
            // 不能在 Exit 里 GetResult() 等收尾：续体要回 UI 线程，而 UI 线程正被阻塞 → 死锁（需强杀）。
            bool shuttingDown = false;
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (shuttingDown) return;
                e.Cancel = true; shuttingDown = true;
                ticker.Dispose();
                try { await shell.ShutdownAsync(TimeSpan.FromSeconds(5)); } catch { }
                desktop.Shutdown();
            };
            // 程序化 desktop.Shutdown()（不经 ShutdownRequested）：在线程池上带超时收尾，不回 UI 线程
            desktop.Exit += (_, _) =>
            {
                ticker.Dispose();
                if (!shuttingDown) { shuttingDown = true; try { Task.Run(() => shell.ShutdownAsync(TimeSpan.FromSeconds(3))).Wait(TimeSpan.FromSeconds(4)); } catch { } }
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
