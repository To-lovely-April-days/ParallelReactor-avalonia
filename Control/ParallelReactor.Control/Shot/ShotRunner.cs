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
using ParallelReactor.Control.ViewModels;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 截图模式（仅 Debug + PR_CONTROL_HEADLESS=1 + PR_CONTROL_SHOT=目录）。
/// 场景 PR_CONTROL_SCENARIO：
///  main        连接 PR_CONTROL_URL（令牌 PR_CONTROL_TOKEN）→ 申请控制权 → 启用 RV1–RV4 → RV1 加热 60 ℃、RV2 80 ℃ → 搅拌 300 rpm →
///              RV3 / RV4 下发 3 步程序并运行 → 等 ≥4 帧 → main-rv1.png / main-rv3.png / main-curves.png / main-overview.png / main-connect-dialog.png
///  perf        连接 → 控制权 → 性能测试视图 → 勾 S1Q（最短合成项：搅拌 300 rpm 10 s + 3 次转速表读数）+ RV1 → perf-checklist.png →
///              开始 → Hold 中 perf-running.png → 等待录入时 perf-prompt.png → 自动填 300 提交 → 等结束 → 收尾
///  perf-report 同上但勾 T0Q + S1Q（≈1 min，含加热 60 ℃ 保温 30 s），全自动应答 → 生成报告视图 → perf-report.png；shot.log 记 report.html 路径
///  输出目录：PR_CONTROL_DATA/perf/&lt;runId&gt;/（截图时请把 PR_CONTROL_DATA 指到 scratch）。
/// 全部完成 Shutdown(0)；异常写 &lt;dir&gt;/error.txt 并 Shutdown(2)。
/// </summary>
public static class ShotRunner
{
    public static void Start(Window window, MainViewModel vm, IClassicDesktopStyleApplicationLifetime desktop, string dir)
        => Dispatcher.UIThread.Post(() => _ = RunAsync(window, vm, desktop, dir), DispatcherPriority.Background);

    private static async Task RunAsync(Window window, MainViewModel vm, IClassicDesktopStyleApplicationLifetime desktop, string dir)
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

            // 先截一张未连接 + 连接对话框
            await SettleAsync();
            vm.OpenConnectDialog();
            var u = new Uri(url);
            vm.ConnectDialog.Host = u.Host; vm.ConnectDialog.Port = u.Port.ToString(); vm.ConnectDialog.Token = token;
            await SettleAsync();
            Capture(window, Path.Combine(dir, "main-connect-dialog.png"), log);

            await vm.ConnectAsync();
            if (!vm.IsConnected) throw new Exception("连接失败：" + vm.ConnectDialog.Status);
            log.AppendLine("connected: " + vm.DeviceText);

            if (scenario is "perf" or "perf-report") { await PerfScenarioAsync(window, vm, dir, log, scenario == "perf-report"); return; }
            if (scenario != "main") throw new NotSupportedException($"未知场景「{scenario}」（main / perf / perf-report）");

            await vm.RequestControlAsync();
            if (!vm.CanControl) throw new Exception("未取得控制权：" + vm.ControlBadge);
            log.AppendLine("control: " + vm.ControlBadge);
            var c = vm.Connection!;
            var ct = CancellationToken.None;

            for (int rv = 1; rv <= 4; rv++) { var r = await c.Control.EnableChannelAsync(rv, ct); log.AppendLine($"enable rv{rv}: {r.HttpStatus} {r.Status}"); }
            var h1 = await c.Control.StartHeatAsync(1, 60, ct); log.AppendLine($"heat rv1 60: {h1.HttpStatus} {h1.Status}");
            var h2 = await c.Control.StartHeatAsync(2, 80, ct); log.AppendLine($"heat rv2 80: {h2.HttpStatus} {h2.Status}");
            var st = await c.Control.SetStirAsync(true, 300, ct); log.AppendLine($"stir 300: {st.HttpStatus} {st.Status}");

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
            var r3 = await c.Control.ReplaceProgramAsync(3, p3, ct); log.AppendLine($"put program rv3: {r3.HttpStatus}");
            var r4 = await c.Control.ReplaceProgramAsync(4, p4, ct); log.AppendLine($"put program rv4: {r4.HttpStatus} (purge={hasPurge})");
            vm.SetDraft(3, p3); vm.SetDraft(4, p4);
            var run3 = await c.Control.RunProgramAsync(3, null, IdempotencyKeys.New("shot-run-rv3"), ct); log.AppendLine($"run rv3: {run3.HttpStatus} {run3.Status}");
            var run4 = await c.Control.RunProgramAsync(4, null, IdempotencyKeys.New("shot-run-rv4"), ct); log.AppendLine($"run rv4: {run4.HttpStatus} {run4.Status}");

            // 等 ≥ 4 帧遥测
            int frames = 0; var tcs = new TaskCompletionSource();
            Action<ParallelReactor.Api.TelemetrySnapshot> h = _ => { if (++frames >= 16) tcs.TrySetResult(); };
            c.TelemetryReceived += h;
            await Task.WhenAny(tcs.Task, Task.Delay(40000));
            c.TelemetryReceived -= h;
            log.AppendLine($"frames={frames} latest seq={c.Telemetry.Latest?.Seq}");
            var latest = c.Telemetry.Latest;
            if (latest != null) foreach (var ch in latest.Channels.Take(4)) log.AppendLine($"  rv{ch.Id} state={ch.State} sp={ch.TempSpC} pv={ch.TempC} prog={ch.Program?.State} {ch.Program?.StepIndex}/{ch.Program?.StepCount}");

            vm.SelectReactor(1); await SettleAsync(); Capture(window, Path.Combine(dir, "main-rv1.png"), log);
            vm.SelectReactor(3); await SettleAsync(); Capture(window, Path.Combine(dir, "main-rv3.png"), log);
            vm.SelectKind(TabKind.Curves); await SettleAsync(); Capture(window, Path.Combine(dir, "main-curves.png"), log);
            vm.SelectKind(TabKind.Overview); await SettleAsync(); Capture(window, Path.Combine(dir, "main-overview.png"), log);
            vm.SelectReactor(1); vm.ToggleExpand("graph"); await SettleAsync(); Capture(window, Path.Combine(dir, "main-expanded-graph.png"), log); vm.CloseExpanded();
            vm.OpenControlDialog(); await SettleAsync(); Capture(window, Path.Combine(dir, "main-control-dialog.png"), log); vm.CloseControlDialog();

            // 收尾：停掉自己开的东西，释放控制权
            try { await c.Rest.AbortProgramAsync(3, ct); await c.Rest.AbortProgramAsync(4, ct); await c.Rest.StopHeatAsync(1, ct); await c.Rest.StopHeatAsync(2, ct); await c.Rest.StopStirAsync(ct); } catch (Exception ex) { log.AppendLine("cleanup: " + ex.Message); }
            await vm.ReleaseControlAsync();
            log.AppendLine("released: " + vm.ControlBadge);
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
            try { await vm.ShutdownAsync(); } catch { }
            desktop.Shutdown(code);
        }
    }

    private static async Task PerfScenarioAsync(Window window, MainViewModel vm, string dir, System.Text.StringBuilder log, bool report)
    {
        await vm.RequestControlAsync();
        if (!vm.CanControl) throw new Exception("未取得控制权：" + vm.ControlBadge);
        log.AppendLine("control: " + vm.ControlBadge);
        var perf = vm.Perf;
        vm.ShowView("perf");
        perf.ClearAll();
        perf.SelectChannels(1);
        if (report) perf.Check("T0Q");
        perf.Check("S1Q");
        log.AppendLine($"plan={perf.Plan.PlanId} items={perf.Plan.AllItems().Count()} selected={string.Join(",", perf.SelectedItemIds)} channels={string.Join(",", perf.SelectedChannels)}");
        // 等几帧遥测，让实时卡 / 曲线有数据
        await WaitFramesAsync(vm.Connection!, 3, 10000);
        await SettleAsync();
        if (!report) Capture(window, Path.Combine(dir, "perf-checklist.png"), log);

        var run = perf.StartAsync();   // 引擎在线程池跑；本方法在 UI 线程继续观察
        if (!perf.IsRunning) throw new Exception("性能测试未能开始（见 Toast）：" + vm.ToastText);
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
        await vm.ReleaseControlAsync();
        log.AppendLine("released: " + vm.ControlBadge);
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
