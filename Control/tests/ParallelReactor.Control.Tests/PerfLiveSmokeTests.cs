using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Client;
using ParallelReactor.PerfTest.Engine;
using ParallelReactor.PerfTest.Model;
using ParallelReactor.PerfTest.Results;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 对真 HMI（PR_SIM=1 + PR_API=1，autoGrantRemote=true）的引擎走通：PR_SMOKE_URL 与 PR_SMOKE_TOKEN 存在时才跑。
/// 用真 ReactorConnection + ReactorApiAdapter / TelemetryFeedAdapter 跑 smoke.json 的 T0Q（启用 RV1 → 60 ℃ → 记录 30 s → 自动录入 → 计算 → 停止加热），
/// 输出到 PR_SMOKE_OUT（默认 临时目录）。
/// </summary>
[Trait("Category", "Live")]
public class PerfLiveSmokeTests
{
    [Fact]
    public async Task T0Q_AgainstSimHmi_WritesRunFolder()
    {
        var url = Environment.GetEnvironmentVariable("PR_SMOKE_URL");
        var token = Environment.GetEnvironmentVariable("PR_SMOKE_TOKEN");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) return; // 未配置 → 跳过（CI 默认）
        var outRoot = Environment.GetEnvironmentVariable("PR_SMOKE_OUT") ?? Path.Combine(Path.GetTempPath(), "pr-perf-live");
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        var opt = new ClientOptions(new Uri(url), token, "PC-perf-smoke");
        await using var conn = new ReactorConnection(opt);
        var log = new List<string>();
        conn.Error += e => log.Add("error: " + e.Message);
        await conn.StartAsync(ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (conn.Ws.State != ConnectionState.Connected && sw.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(100, ct);
        Assert.Equal(ConnectionState.Connected, conn.Ws.State);
        Assert.True(await conn.Lease.AcquireAsync(ct), "申请控制权失败（需 autoGrantRemote=true）");

        var api = new ReactorApiAdapter(conn);
        using var feed = new TelemetryFeedAdapter(conn);
        var plan = PlanLoader.LoadEmbedded(PlanLoader.SmokePlan);
        var prompt = new AutoPrompt { DefaultValue = 300 };
        var progress = new List<PerfProgress>();
        var runner = new PerfRunner(plan, api, feed, prompt, null, new PerfRunnerOptions { OutputRoot = outRoot });
        runner.Progress += p => { lock (progress) progress.Add(p); };
        var req = new RunRequest { PlanId = plan.PlanId, ItemIds = new List<string> { "T0Q" }, Channels = new List<int> { 1 }, Operator = "live-smoke", SoftwareVersion = "pc-test" };
        var result = await runner.RunAsync(req, ct);

        File.WriteAllText(Path.Combine(result.OutputDir!, "live-progress.log"), string.Join("\n", progress.Select(p => $"{p.At:HH:mm:ss} {p.Status} {p.ItemId}/{p.StepId} {p.Phase} step={p.StepPercent:0} overall={p.OverallPercent:0.0} T={string.Join(",", p.LiveTempC.Select(t => t?.ToString("0.0") ?? "-"))}")) + "\n" + string.Join("\n", log));

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.False(result.Simulated == false && result.DeviceSerial == null);
        var item = result.Items.Single();
        Assert.Equal(ItemOutcome.Completed, item.Outcome);
        Assert.All(item.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.All(item.Steps.Where(s => s.Kind == "Command").SelectMany(s => s.Commands), c => Assert.True(c.Ok, c.Code + " " + c.Message));
        Assert.True(item.Measures.Single(m => m.Key == "frames").Value >= 15, "30 s 内应收到 ≥ 15 帧遥测");
        Assert.NotNull(result.SessionId);
        foreach (var f in new[] { "result.json", "report.html", "summary.csv", "events.csv", "manual-entries.csv", "settings-snapshot.json", Path.Combine("raw", "telemetry.csv") })
            Assert.True(File.Exists(Path.Combine(result.OutputDir!, f)), f);
        Assert.True(Directory.GetFiles(Path.Combine(result.OutputDir!, "hmi")).Length >= 1, "应下载触屏会话 CSV");

        await conn.Lease.ReleaseAsync(ct);
    }
}
