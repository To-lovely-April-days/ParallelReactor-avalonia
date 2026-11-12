#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 截图模式（仅 Debug + PR_CONTROL_HEADLESS=1 + PR_CONTROL_SHOT=目录）：对一台<b>活的 HMI</b> 跑脚本（新外壳 + FleetVm 的实时设备 + DeviceActions）。
/// 场景 PR_CONTROL_SCENARIO：
///  main        「添加设备」对话框填 PR_CONTROL_URL / PR_CONTROL_TOKEN、测试连接（main-add-dialog.png）→ 添加并连接 → 申请控制权（等触屏 / autoGrant）→
///              启用 RV1–RV4 → RV1 加热 60 ℃、RV2 80 ℃ → 搅拌 300 rpm → RV3 / RV4 下发 3 步程序并运行 → 等 ≥ 16 帧 →
///              main-rv1.png（反应釜页 1/1）/ main-rv3.png / main-device1.png / main-overview.png / main-program.png（程序页，旧样式）→
///              收尾（中止程序、停止加热与搅拌、释放控制权）
///  perf        连接 → 控制权 → 性能测试页（旧样式）→ 勾 S1Q（最短合成项：搅拌 300 rpm 10 s + 3 次转速表读数）+ RV1 → perf-checklist.png →
///              开始 → Hold 中 perf-running.png → 等待录入时 perf-prompt.png → 自动填 300 提交 → 等结束 → 收尾
///  perf-report 同上但勾 T0Q + S1Q（≈1 min，含加热 60 ℃ 保温 30 s），全自动应答 → 生成报告视图 → perf-report.png；shot.log 记 report.html 路径
///  输出目录：PR_CONTROL_DATA/perf/&lt;runId&gt;/（截图时请把 PR_CONTROL_DATA 指到 scratch）。
/// 全部完成 Shutdown(0)；异常写 &lt;dir&gt;/error.txt 并 Shutdown(2)。这些场景会真的下命令，<b>只对模拟 HMI / 测试台架运行</b>。
/// 演示模式（PR_CONTROL_DEMO=1，不连接任何设备，按 ShotPlan 截图 + 审计 + 对照）见 ShotRunner.Demo.cs。
/// </summary>
public static partial class ShotRunner
{
    public static void Start(Window window, ShellVm shell, IClassicDesktopStyleApplicationLifetime desktop, string dir)
        => Dispatcher.UIThread.Post(() => _ = RunAsync(window, shell, desktop, dir), DispatcherPriority.Background);

    private static async Task RunAsync(Window window, ShellVm shell, IClassicDesktopStyleApplicationLifetime desktop, string dir)
    {
        int code = 0;
        var log = new System.Text.StringBuilder();
        try
        {
            Directory.CreateDirectory(dir);
            var scenario = Environment.GetEnvironmentVariable("PR_CONTROL_SCENARIO") ?? "main";
            var url = Environment.GetEnvironmentVariable("PR_CONTROL_URL") ?? "http://127.0.0.1:8080";
            var token = Environment.GetEnvironmentVariable("PR_CONTROL_TOKEN") ?? "";
            log.AppendLine($"scenario={scenario} url={url}");

            // 先截一张「添加设备」对话框（填好地址和令牌，测试连接），再「添加并连接」（M3：多台；截图模式不写 devices.json）
            await SettleAsync();
            shell.OpenDialog("add");
            var dlg = shell.Dialogs.Current as AddDeviceDialogVm ?? throw new Exception("「添加设备」对话框没有注册");
            var u = new Uri(url);
            dlg.Host = u.Host; dlg.Port = u.Port.ToString(System.Globalization.CultureInfo.InvariantCulture); dlg.Token = token;
            dlg.Test.Button.Command.Execute(null);
            for (int i = 0; i < 100 && (dlg.Test.IsBusy || !dlg.Test.HasText); i++) await Task.Delay(100);
            log.AppendLine("test: " + dlg.Test.Text);
            await SettleAsync();
            Capture(window, Path.Combine(dir, "main-add-dialog.png"), log);

            dlg.AddButton.Command.Execute(null);
            var d = shell.Fleet.LiveDevice ?? throw new Exception("添加设备失败：" + dlg.SubmitReason);
            for (int i = 0; i < 200 && d.Conn != Controls.Pr.ConnState.Online; i++) await Task.Delay(100);
            if (d.Conn != Controls.Pr.ConnState.Online) throw new Exception("连接失败：" + ((LiveDeviceSource)d.Source).LastError);
            log.AppendLine($"connected: {d.Alias} {d.ModelLabel} SN {d.Sn} · {d.ConnText}");

            if (scenario is "perf" or "perf-report") { await PerfScenarioAsync(window, shell, d, dir, log, scenario == "perf-report"); return; }
            if (scenario != "main") throw new NotSupportedException($"未知场景「{scenario}」（main / perf / perf-report）");

            await AcquireLeaseAsync(d, log);
            var c = shell.Fleet.LiveConnection ?? throw new Exception("连接已断开");
            var live = (LiveDeviceSource)d.Source;
            var ct = CancellationToken.None;

            // 操作一律走 DeviceActions（同界面上的按钮：执行前查守卫、结果 / 警告记进日志）；守卫拦下的（已启用、已在加热、搅拌已在运行…）只记一笔
            async Task Act(string what, Core.Guard.DeviceAction a, Core.Guard.ActionPayload? p = null)
            {
                var o = await d.Actions.RunAsync(a, p, quiet: true);
                log.AppendLine($"{what}: {(o.Ok ? "ok" : "skipped")} {o.Message}" + (o.Warnings.Count > 0 ? " · " + string.Join(",", o.Warnings) : ""));
            }
            for (int rv = 1; rv <= 4; rv++) await Act($"enable rv{rv}", Core.Guard.DeviceAction.Enable, Core.Guard.ActionPayload.Ch(rv));
            await Act("heat rv1 60", Core.Guard.DeviceAction.HeatStart, new Core.Guard.ActionPayload(N: 1, Sp: 60));
            await Act("heat rv2 80", Core.Guard.DeviceAction.HeatStart, new Core.Guard.ActionPayload(N: 2, Sp: 80));
            await Act("stir 300", Core.Guard.DeviceAction.StirOn, new Core.Guard.ActionPayload(Rpm: 300));

            var schema = await c.Rest.ProgramSchemaAsync(ct);
            bool hasPurge = schema.StepTypes.Contains("purge");
            var p3 = new ProgramDoc { Name = "加氢测试-03" };
            p3.Add(StepType.Wait, new { mode = "fixed", time = 0.3 });
            p3.Add(StepType.Heat, new { mode = "fixed", temp = 50.0, wait = 0.1 });
            p3.Add(StepType.Wait, new { mode = "fixed", time = 0.3 });
            var p4 = new ProgramDoc { Name = "吹扫测试-04" };
            if (hasPurge) p4.Add(StepType.Purge, new { gas = "inert", cycles = 2, fill = 3, vent = 3 });
            else p4.Add(StepType.Wait, new { mode = "fixed", time = 0.3 });
            p4.Add(StepType.Heat, new { mode = "fixed", temp = 45.0, wait = 0.1 });
            p4.Add(StepType.Wait, new { mode = "fixed", time = 0.3 });
            var r3 = await live.UploadProgramAsync(3, p3, ct); log.AppendLine($"put program rv3: {r3.Message}");
            var r4 = await live.UploadProgramAsync(4, p4, ct); log.AppendLine($"put program rv4: {r4.Message} (purge={hasPurge})");
            shell.Fleet.RefreshNow();
            await Act("run rv3", Core.Guard.DeviceAction.ProgRun, Core.Guard.ActionPayload.Ch(3));
            await Act("run rv4", Core.Guard.DeviceAction.ProgRun, Core.Guard.ActionPayload.Ch(4));

            // 等 ≥ 16 帧遥测
            await WaitFramesAsync(c, 16, 40000);
            var latest = c.Telemetry.Latest;
            log.AppendLine($"latest seq={latest?.Seq}");
            if (latest != null) foreach (var ch in latest.Channels.Take(4)) log.AppendLine($"  rv{ch.Id} state={ch.State} sp={ch.TempSpC} pv={ch.TempC} prog={ch.Program?.State} {ch.Program?.StepIndex}/{ch.Program?.StepCount}");
            foreach (var r in d.Reactors.Take(4)) log.AppendLine($"  vm RV{r.N} {r.Key} {r.Word} {r.TempText} ℃ {r.PressText} MPa");

            shell.Navigate(Route.Reactor(1, 1)); await SettleAsync(); Capture(window, Path.Combine(dir, "main-rv1.png"), log);
            shell.Navigate(Route.Reactor(1, 3)); await SettleAsync(); Capture(window, Path.Combine(dir, "main-rv3.png"), log);
            shell.Navigate(Route.Device(1)); await SettleAsync(); Capture(window, Path.Combine(dir, "main-device1.png"), log);
            shell.Navigate(Route.Overview); await SettleAsync(); Capture(window, Path.Combine(dir, "main-overview.png"), log);
            shell.Navigate(Route.Placeholder("program"));
            if (shell.OpenPage<LegacyProgramPageVm>() is { } prog) { prog.SetDraft(3, p3); prog.SelectRv(3); }
            await SettleAsync(); Capture(window, Path.Combine(dir, "main-program.png"), log);

            // 收尾：停掉自己开的东西（停止类，同样走 DeviceActions），释放控制权
            await Act("abort rv3", Core.Guard.DeviceAction.ProgAbort, Core.Guard.ActionPayload.Ch(3));
            await Act("abort rv4", Core.Guard.DeviceAction.ProgAbort, Core.Guard.ActionPayload.Ch(4));
            await Act("heat stop rv1", Core.Guard.DeviceAction.HeatStop, Core.Guard.ActionPayload.Ch(1));
            await Act("heat stop rv2", Core.Guard.DeviceAction.HeatStop, Core.Guard.ActionPayload.Ch(2));
            await Act("stir off", Core.Guard.DeviceAction.StirOff);
            await d.Source.ReleaseLeaseAsync();
            shell.Fleet.RefreshNow();
            log.AppendLine("released: " + d.LeaseText);
        }
        catch (Exception ex)
        {
            code = 2;
            log.AppendLine("ERROR: " + ex);
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); } catch { }
        }
        finally
        {
            try { File.WriteAllText(Path.Combine(dir, "shot.log"), log.ToString()); } catch { }
            try { await shell.ShutdownAsync(TimeSpan.FromSeconds(5)); } catch { }
            desktop.Shutdown(code);
        }
    }

    /// <summary>申请控制权并等到远程控制中（触屏确认或 autoGrant，最多 75 s）。</summary>
    private static async Task AcquireLeaseAsync(DeviceVm d, System.Text.StringBuilder log)
    {
        var o = await d.Actions.RunAsync(Core.Guard.DeviceAction.LeaseRequest);
        log.AppendLine("lease request: " + o.Message);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (d.LeaseStatus != LeaseStatus.Remote && sw.Elapsed < TimeSpan.FromSeconds(75))
        {
            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs();
            d.Fleet.RefreshNow();
        }
        if (d.LeaseStatus != LeaseStatus.Remote) throw new Exception("未取得控制权：" + d.LeaseText);
        log.AppendLine("control: " + d.LeaseText);
    }

    private static async Task PerfScenarioAsync(Window window, ShellVm shell, DeviceVm d, string dir, System.Text.StringBuilder log, bool report)
    {
        await AcquireLeaseAsync(d, log);
        shell.Navigate(Route.Placeholder("perf"));
        var page = shell.OpenPage<PerfPageVm>() ?? throw new Exception("性能测试页没有打开");
        var perf = page.Perf;
        perf.ClearAll();
        perf.SelectChannels(1);
        if (report) perf.Check("T0Q");
        perf.Check("S1Q");
        log.AppendLine($"plan={perf.Plan.PlanId} items={perf.Plan.AllItems().Count()} selected={string.Join(",", perf.SelectedItemIds)} channels={string.Join(",", perf.SelectedChannels)}");
        // 等几帧遥测，让实时卡 / 曲线有数据
        await WaitFramesAsync(shell.Fleet.LiveConnection!, 3, 10000);
        await SettleAsync();
        if (!report) Capture(window, Path.Combine(dir, "perf-checklist.png"), log);

        var run = perf.StartAsync();   // 引擎在线程池跑；本方法在 UI 线程继续观察
        if (!perf.IsRunning) throw new Exception("性能测试未能开始（见 Toast）：" + shell.Toasts.Items.LastOrDefault()?.Text);
        log.AppendLine("started: " + perf.StatusText);

        bool gotRunning = false, gotPrompt = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (perf.IsRunning && sw.Elapsed < TimeSpan.FromMinutes(4))
        {
            await Task.Delay(200);
            Dispatcher.UIThread.RunJobs();
            var p = perf.LastProgress;
            if (!report && !gotRunning && p != null && p.StepKind is "Hold" or "WaitStable" && p.Waiting == ParallelReactor.PerfTest.Engine.WaitingKind.None && p.StepPercent > 15)
            {
                gotRunning = true;
                await SettleAsync();
                Capture(window, Path.Combine(dir, "perf-running.png"), log);
                log.AppendLine($"  running: {p.ItemId}/{p.StepId} {p.Phase} {p.StepPercent:0}% countdown={p.Countdown}");
            }
            if (perf.IsWaitingEntry)
            {
                if (!report && !gotPrompt)
                {
                    gotPrompt = true;
                    await SettleAsync();
                    Capture(window, Path.Combine(dir, "perf-prompt.png"), log);
                    log.AppendLine($"  prompt: {perf.ManualDev} · {perf.ManualPrompt} · {perf.EntryFields.Count} 字段");
                }
                perf.AutoFillEntry(300);
                log.AppendLine("  entry auto-filled 300");
            }
            if (perf.IsWaitingConfirm) { perf.ConfirmContinue(); log.AppendLine("  confirm auto-answered"); }
            if (perf.IsWaitingSafety) { log.AppendLine("  SAFETY: " + perf.SafetyText); perf.ConfirmContinue(); }
        }
        await run;
        var r = perf.Result ?? throw new Exception("无运行结果");
        log.AppendLine($"finished: status={r.Status} items={r.Items.Count} outputDir={r.OutputDir}");
        foreach (var it in r.Items) log.AppendLine($"  item {it.ItemId} outcome={it.Outcome} steps={string.Join(",", it.Steps.Select(st => st.StepId + ":" + st.Status))} measures={it.Measures.Count}");
        foreach (var it in r.Items) foreach (var st in it.Steps) foreach (var cmd in st.Commands) log.AppendLine($"    cmd {cmd.Op} rv{cmd.Channel} → {cmd.HttpStatus} {cmd.Status} {cmd.Code} opId={cmd.OpId}");
        log.AppendLine($"report.html = {perf.ReportPath} exists={File.Exists(perf.ReportPath)}");
        if (!report && !gotRunning) log.AppendLine("WARN: 未截到 perf-running.png（Hold 太短？）");
        if (!report && !gotPrompt) log.AppendLine("WARN: 未截到 perf-prompt.png");
        if (report)
        {
            perf.GenerateReport();
            await SettleAsync();
            Capture(window, Path.Combine(dir, "perf-report.png"), log);
            try { if (File.Exists(perf.ReportPath)) File.Copy(perf.ReportPath, Path.Combine(dir, "report.html"), true); } catch (Exception ex) { log.AppendLine("copy report: " + ex.Message); }
        }
        else
        {
            await SettleAsync();
            Capture(window, Path.Combine(dir, "perf-finished.png"), log);
        }
        await d.Source.ReleaseLeaseAsync();
        shell.Fleet.RefreshNow();
        log.AppendLine("released: " + d.LeaseText);
    }

    private static async Task WaitFramesAsync(IReactorConnection c, int n, int timeoutMs)
    {
        int frames = 0; var tcs = new TaskCompletionSource();
        Action<ParallelReactor.Api.TelemetrySnapshot> h = _ => { if (++frames >= n) tcs.TrySetResult(); };
        c.TelemetryReceived += h;
        await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        c.TelemetryReceived -= h;
    }

    /// <summary>等布局 / 绑定稳定并强制渲染两帧（无头平台的渲染计时器不会自己跑）。</summary>
    private static async Task SettleAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Capture(Window window, string path, System.Text.StringBuilder log)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var bmp = window.CaptureRenderedFrame();
        if (bmp == null) { log.AppendLine("capture null: " + path); return; }
        bmp.Save(path);
        log.AppendLine($"saved {path} {bmp.PixelSize.Width}x{bmp.PixelSize.Height}");
    }
}
#endif
