#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 场景 fake2（仅 Debug，PR_CONTROL_SCENARIO=fake2；PORT-PLAN §5 M3「Debug 场景 fake2」）：四台<b>进程内的假 HMI</b>（Shot/FakeHmi，不运行模拟 HMI —— D9 未获同意），
/// 经<b>真 ReactorConnection</b>（REST + WS 的整条客户端路径）连到实时的 FleetVm / DeviceRegistry / LiveDeviceSource（重连监督）：
/// 1号机、2号机在线（1号机申请控制权 → autoGrant → 远程控制中；2号机被其他客户端持有、一条超压报警），3号机主机不通（「连接失败 · N s 后重试」），
/// 4号机连上后断线（「连接中断 · 重连中 N s」）。配置只在内存里（不读不写 devices.json），时钟是真的（倒计时照常走）。
/// <list type="bullet">
/// <item>有 PR_CONTROL_SHOT=目录：截 fake2-overview / fake2-device1 / fake2-reactor11 / fake2-device3 / fake2-device4 / fake2-devices（设备管理对话框）、
///   fake2-client（本机客户端名）/ fake2-exit（退出确认：1号机申请中，M3 复核新加）-&lt;w&gt;.png，各写 .audit.txt；
///   shot.log 记每台的连接 / 控制权 / 失败原因与出现过的提示。审计有问题退出码 1，异常退出码 2（error.txt）。</item>
/// <item>没有 PR_CONTROL_SHOT：打开主窗口（交互走查，UiTicker 照常每秒刷新）。</item>
/// </list>
/// </summary>
public static partial class ShotRunner
{
    public static bool IsFake2 => (Environment.GetEnvironmentVariable("PR_CONTROL_SCENARIO") ?? "").Trim() == "fake2";

    /// <summary>fake2 的截图：名字、路由、要打开的对话框。</summary>
    private static readonly (string Name, Route Route, string? Dialog)[] Fake2Shots =
    {
        ("fake2-overview", Route.Overview, null),
        ("fake2-device1", Route.Device(1), null),
        ("fake2-reactor11", Route.Reactor(1, 1), null),
        // 选中断线的两台：检查器的连接卡（从未连上「正在连接 …」/ 连上过「重连中 N s · 数据停在 …」+「立即重连」）、资源管理器滚到这台
        ("fake2-device3", Route.Device(3), null),
        ("fake2-device4", Route.Device(4), null),
        ("fake2-devices", Route.Overview, "devices"),
    };

    public static void StartFake2(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var dir = Environment.GetEnvironmentVariable("PR_CONTROL_SHOT");
        bool shot = !string.IsNullOrWhiteSpace(dir);
        var size = ParseSize(Environment.GetEnvironmentVariable("PR_CONTROL_SHOT_SIZE")) ?? new PixelSize(1440, 900);

        var hmis = FakeHmi.Fleet2();
        var byHost = hmis.ToDictionary(h => h.Host, StringComparer.OrdinalIgnoreCase);
        // 内存里的配置（CanWrite = false：不写 devices.json）：四台都记住了令牌、勾了启动时自动连接
        var config = ControlConfig.InMemory("PC-测试工位1");
        string[] aliases = { "1号机", "2号机", "3号机", "4号机" };
        for (int i = 0; i < hmis.Length; i++)
            config.Devices.Add(new DeviceEntry { Alias = aliases[i], Host = hmis[i].Host, Port = hmis[i].Port, Token = "ht-fake2-" + (i + 1), RememberToken = true, AutoConnect = true });
        IReactorConnection Connect(ClientOptions o) => new ReactorConnection(o, byHost[o.BaseUrl.Host].NewHandler(), SystemClock.Instance, byHost[o.BaseUrl.Host].NewTransport);

        var fleet = FleetVm.CreateLive(config, App.PostToUi, new AvaloniaSynchronizationContext(DispatcherPriority.Normal), Connect);
        var shell = new ShellVm(fleet);
        var window = new MainWindow { DataContext = shell, Width = size.Width, Height = size.Height };
        var ticker = new UiTicker();
        ticker.Tick += fleet.OnTick;
        window.Closed += (_, _) => { ticker.Dispose(); foreach (var h in hmis) h.Dispose(); };
        if (shot)
        {
            window.SystemDecorations = SystemDecorations.None;
            window.CanResize = false;
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() => _ = RunFake2Async(window, shell, fleet, hmis, desktop, dir!, size), DispatcherPriority.Background);
        }
        else
        {
            ticker.Start();
            Dispatcher.UIThread.Post(() => _ = Fake2DriveAsync(shell, fleet, hmis, null), DispatcherPriority.Background);
        }
        desktop.MainWindow = window;
    }

    /// <summary>把四台设备带到截图的状态：并行自动连接 → 1号机申请控制权（autoGrant）→ 等几帧 → 4号机断线、退避几轮。</summary>
    private static async Task Fake2DriveAsync(ShellVm shell, FleetVm fleet, FakeHmi[] hmis, StringBuilder? log)
    {
        var sw = Stopwatch.StartNew();
        await fleet.StartAsync();   // DeviceRegistry：并行连接勾了自动连接且存了令牌的设备，等各自第一次尝试的结果
        log?.AppendLine($"start: {sw.ElapsedMilliseconds} ms · " + States(fleet));
        await Until(() => On(fleet, 1) && On(fleet, 2) && On(fleet, 4) && fleet.Device(3)!.ConnStatus == ConnStatus.Offline, 15000, "1/2/4 号机上线、3 号机首次失败", fleet);
        var o = await fleet.Device(1)!.Actions.RunAsync(DeviceAction.LeaseRequest, quiet: true);
        log?.AppendLine("lease 1: " + o.Message);
        await Until(() => fleet.Device(1)!.LeaseStatus == LeaseStatus.Remote, 8000, "1号机远程控制中", fleet);
        await Task.Delay(2500);   // 几帧遥测（每秒一帧）
        hmis[3].Drop();           // 4号机断线：之后主机不通
        await Until(() => fleet.Device(4)!.ConnStatus == ConnStatus.Offline && fleet.Device(4)!.EverOnline
            && ((LiveDeviceSource)fleet.Device(4)!.Source).Connection?.Ws.FailedAttempts >= 3, 20000, "4号机断线、重连失败几轮", fleet);
        log?.AppendLine($"driven: {sw.ElapsedMilliseconds} ms · " + States(fleet));
    }

    private static bool On(FleetVm f, int k) => f.Device(k)?.ConnStatus == ConnStatus.Online;

    private static async Task Until(Func<bool> cond, int timeoutMs, string what, FleetVm fleet)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            fleet.OnTick(true);
            Dispatcher.UIThread.RunJobs();
            if (cond()) return;
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("fake2：等不到「" + what + "」 · " + States(fleet));
            await Task.Delay(100);
        }
    }

    private static string States(FleetVm fleet) => string.Join(" | ", fleet.Devices.Select(d =>
        $"{d.Alias} {d.ConnText} · {d.LeaseText}" + (((LiveDeviceSource)d.Source).LastError is { } e ? " · " + e : "")));

    private static async Task RunFake2Async(Window window, ShellVm shell, FleetVm fleet, FakeHmi[] hmis, IClassicDesktopStyleApplicationLifetime desktop, string dir, PixelSize size)
    {
        int code = 0;
        var log = new StringBuilder();
        var toasts = new List<string>();
        void OnToast(string t, ToastKind k) => toasts.Add(k + " " + t);
        fleet.Toast += OnToast;
        try
        {
            Directory.CreateDirectory(dir);
            log.AppendLine($"fake2 size={size.Width}x{size.Height} font={UiFonts.Stack()}");
            shell.Layout.Update(new Size(size.Width, size.Height));
            Frozen.Apply(window);
            await SettleAsync();
            await Fake2DriveAsync(shell, fleet, hmis, log);

            async Task ShotAsync(string name, Route route, Action? open)
            {
                shell.ResetView();                       // 每张都像新开的窗口：默认布局与页签、选中第一台
                shell.Navigate(route);
                open?.Invoke();
                fleet.OnTick(true);                      // 倒计时按此刻刷新
                shell.Toasts.Clear();                    // 提示记在 shot.log 里，不留在截图上（参照图没有提示）
                await SettleAsync();
                var stem = $"{name}-{size.Width}";
                var png = Path.Combine(dir, stem + ".png");
                Capture(window, png, log);
                log.AppendLine("  " + States(fleet));
                var issues = ShotAudit.Run(window);
                File.WriteAllText(Path.Combine(dir, stem + ".audit.txt"), issues.Count == 0 ? "" : string.Join("\n", issues) + "\n");
                log.AppendLine($"audit {stem}: {issues.Count} issue(s) · {ShotAudit.LastStats}");
                if (issues.Count > 0) code = 1;
                if (Flag("PR_CONTROL_TREE_DUMP")) File.WriteAllText(Path.Combine(dir, stem + ".tree.txt"), TreeDump(window));
            }

            foreach (var (name, route, dialog) in Fake2Shots)
                await ShotAsync(name, route, dialog == null ? null : () => shell.OpenDialog(dialog));

            // M3 复核新加的两个真机对话框（样稿没有，没有参照图，只审计）：本机客户端名；退出确认 —— 1号机释放控制权、改成要触屏确认、再申请 → 申请中 → 退出前先问
            await ShotAsync("fake2-client", Route.Overview, () => shell.OpenDialog("client"));
            var rel = await fleet.Device(1)!.Actions.RunAsync(DeviceAction.LeaseRelease, quiet: true);
            hmis[0].AutoGrant = false;
            var req = await fleet.Device(1)!.Actions.RunAsync(DeviceAction.LeaseRequest, quiet: true);
            log.AppendLine("lease 1 again: " + rel.Message + " · " + req.Message);
            await Until(() => fleet.Device(1)!.LeaseStatus == LeaseStatus.Requested && shell.PendingLeaseRequests.Count == 1, 8000, "1号机申请中", fleet);
            await ShotAsync("fake2-exit", Route.Overview, () => shell.TryExit());
            shell.Dialogs.Close();
            log.AppendLine("toasts:");
            foreach (var t in toasts) log.AppendLine("  " + t);
        }
        catch (Exception ex)
        {
            code = 2;
            log.AppendLine("ERROR: " + ex);
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); } catch { }
        }
        finally
        {
            fleet.Toast -= OnToast;
            try { await shell.ShutdownAsync(TimeSpan.FromSeconds(5)); } catch { }
            foreach (var h in hmis) h.Dispose();
            try { File.WriteAllText(Path.Combine(dir, "shot.log"), log.ToString()); } catch { }
            desktop.Shutdown(code);
        }
    }
}
#endif
