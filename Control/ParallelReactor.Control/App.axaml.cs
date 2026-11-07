using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.ViewModels;
using ParallelReactor.Control.Views;

namespace ParallelReactor.Control;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var config = ControlConfig.Load();
            var vm = new MainViewModel(new ConnectionService(), config);
            var window = new MainWindow { DataContext = vm };

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
                window.Opened += (_, _) => Shot.ShotRunner.Start(window, vm, desktop, shotDir);
#endif
            }

            // 退出：关窗 → ShutdownRequested（可取消）→ 先异步释放租约 / 断开，再真正 Shutdown。
            // 不能在 Exit 里 GetResult() 等 ShutdownAsync：续体要回 UI 线程，而 UI 线程正被阻塞 → 死锁（需强杀）。
            bool shuttingDown = false;
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (shuttingDown) return;
                e.Cancel = true; shuttingDown = true;
                try { await vm.ShutdownAsync(TimeSpan.FromSeconds(5)); } catch { }
                desktop.Shutdown();
            };
            // 程序化 desktop.Shutdown()（不经 ShutdownRequested）：在线程池上带超时收尾，不回 UI 线程
            desktop.Exit += (_, _) => { if (!shuttingDown) { shuttingDown = true; try { Task.Run(() => vm.ShutdownAsync(TimeSpan.FromSeconds(3))).Wait(TimeSpan.FromSeconds(4)); } catch { } } };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
