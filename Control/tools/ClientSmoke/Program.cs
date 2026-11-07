using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Models;

namespace ParallelReactor.Tools.ClientSmoke;

/// <summary>对真 HMI 的客户端库走通（见 csproj 注释）。每一步打印 HTTP 状态 / opId / status；最后打印 WS 消息类型计数。失败退出码 1。</summary>
internal static class Program
{
    private static readonly List<string> Log = new();
    private static int _fail;

    private static void Step(string name, string result, bool ok = true)
    {
        var line = $"{(ok ? "OK " : "ERR")} {name,-46} {result}";
        Console.WriteLine(line);
        Log.Add(line);
        if (!ok) _fail++;
    }

    private static async Task<int> Main(string[] args)
    {
        string url = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("PR_SMOKE_URL") ?? "http://127.0.0.1:18100";
        string opTok = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("PR_SMOKE_TOKEN") ?? "optoken";
        string viewTok = args.Length > 2 ? args[2] : Environment.GetEnvironmentVariable("PR_SMOKE_VIEW_TOKEN") ?? "viewtoken";
        string? samples = args.Length > 3 ? args[3] : Environment.GetEnvironmentVariable("PR_SMOKE_SAMPLES");
        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token;
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine($"ClientSmoke → {url}  (samples: {samples ?? "-"})");

        // ---- 1. viewer：只读 ----
        var viewOpt = new ClientOptions(new Uri(url), viewTok, "PC-smoke-viewer");
        using (var viewer = new ReactorApiClient(viewOpt))
        {
            var health = await viewer.HealthAsync(ct);
            Step("viewer GET /health", $"200 ok={health.Ok} simulated={health.Simulated} bootId={health.BootId}");
            var dev = await viewer.DeviceAsync(ct);
            Step("viewer GET /device", $"200 model={dev.Model} serial={dev.Serial} installed={string.Join(",", dev.InstalledChannels)} hmi={dev.HmiVersion}");
            var snap = await viewer.SnapshotAsync(ct);
            Step("viewer GET /snapshot", $"200 seq={snap.Seq} mode={snap.Mode.Control} RV1.state={snap.Channels[0].State} press={snap.Channels[0].PressurePsi:0.#}psi");
            try { await viewer.StopHeatAsync(1, ct); Step("viewer POST heat/stop (应 403)", "unexpected 2xx", false); }
            catch (ApiException ex) { Step("viewer POST heat/stop (应 403)", $"{ex.Status} {ex.Code}", ex.Status == 403); }
        }

        // ---- 2. operator：连接门面（REST + WS + 租约）----
        var opt = new ClientOptions(new Uri(url), opTok, "PC-测试工位1");
        var rawWs = new List<(string Type, string Json)>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var leaseLog = new List<string>();
        await using var conn = new ReactorConnection(opt);
        conn.Ws.RawReceived += (t, j) => { lock (rawWs) { rawWs.Add((t, j)); counts[t] = counts.GetValueOrDefault(t) + 1; } };
        conn.LeaseTransitioned += t => { lock (leaseLog) leaseLog.Add($"{t.From}->{t.To}:{t.Reason}"); };
        conn.Error += e => Console.WriteLine("  [error] " + e.GetType().Name + ": " + e.Message);
        await conn.StartAsync(ct);
        Step("GET /device + /control + /snapshot", $"200 mode={conn.ControlState!.Mode} autoGrant={conn.ControlState.AutoGrant} hbTimeout={conn.ControlState.HeartbeatTimeoutSec}s grace={conn.ControlState.LeaseGraceSec}s");
        await WaitAsync(() => conn.Ws.State == ConnectionState.Connected, 15000, "WS hello/subscribed");
        Step("WS connect + hello + subscribe", $"state={conn.Ws.State} bootId={conn.Ws.BootId} eventSeqHead={conn.Ws.Hello!.EventSeqHead}");

        // 无租约写 → 409 MODE_LOCAL（本地模式）
        try { await conn.Rest.StartHeatAsync("00000000000000000000000000000000", 1, 60, ct); Step("POST heat/start 无效租约（应 409）", "unexpected 2xx", false); }
        catch (ApiException ex) { Step("POST heat/start 无效租约（应 409）", $"{ex.Status} {ex.Code} opId={ex.OpId}", ex.Status == 409); }

        bool got = await conn.Lease.AcquireAsync(ct);
        Step("POST /control/request → 租约", $"202 granted={got} lease={Short(conn.Lease.LeaseId)} hbInterval={conn.Lease.HeartbeatInterval.TotalSeconds:0.#}s");
        if (!got) return Finish(samples, counts, leaseLog, rawWs);

        // 心跳：等 2 个间隔，看 WS hb 是否发出 + 服务端状态仍 remote
        long reqBefore = conn.Rest.Requests;
        await Task.Delay(conn.Lease.HeartbeatInterval * 2.2, ct);
        var ctl = await conn.Rest.ControlAsync(ct);
        Step("心跳 ×2（WS hb 优先）", $"GET /control 200 mode={ctl.Mode} state={ctl.State} holder={ctl.Holder} restHb={conn.Rest.Requests - reqBefore - 1} wsHbSent≥{(ctl.State == "remote" ? "1" : "?")}");
        var hb = await conn.Rest.HeartbeatAsync(conn.Lease.LeaseId!, ct);
        Step("POST /control/heartbeat (REST)", $"200 mode={hb.Mode} resumed={hb.Resumed} expires={hb.LeaseExpiresAt:HH:mm:ss}");

        var en = await conn.Control.EnableChannelAsync(1, ct);
        Step("POST /channels/1/enable", $"{en.HttpStatus} {en.Status} opId={Short(en.OpId)} seq={en.OpEventSeq}");
        var heat = await conn.Control.StartHeatAsync(1, 60, ct);
        Step("POST /channels/1/heat/start 60℃", $"{heat.HttpStatus} {heat.Status} opId={Short(heat.OpId)}");
        var sp = await conn.Control.SetSetpointAsync(1, 70, ct);
        Step("PUT /channels/1/temperature/setpoint 70℃", $"{sp.HttpStatus} {sp.Status} tempSpC={sp.Result?.TempSpC} replanned={sp.Result?.ApproachReplanned}");

        var en2 = await conn.Control.EnableChannelAsync(2, ct);
        Step("POST /channels/2/enable", $"{en2.HttpStatus} {en2.Status}");
        var prog = new ProgramDoc { Name = "smoke-2step" }.Add(StepType.Wait, new { mode = "fixed", time = 0.2 }).Add(StepType.Heat, new { mode = "fixed", temp = 50, wait = 0.1 });
        var put = await conn.Control.ReplaceProgramAsync(2, prog, ct);
        Step("PUT /channels/2/program (wait 0.2 min, heat 50℃)", $"{put.HttpStatus} {put.Status} result={put.Result.GetRawText()}");
        var run = await conn.Control.RunProgramAsync(2, null, IdempotencyKeys.New("smoke-run-rv2"), ct);
        Step("POST /channels/2/program/run", $"{run.HttpStatus} {run.Status} rv={run.Result?.Rv} steps={run.Result?.Steps} opId={run.OpId}");

        var sess = await conn.Rest.BeginSessionAsync("PT-SMOKE", "客户端库走通", IdempotencyKeys.New("smoke-sess"), ct);
        Step("POST /sessions", $"{sess.HttpStatus} {sess.Status} id={sess.Result?.Id} startSeq={sess.Result?.StartEventSeq}");
        var mkKey = IdempotencyKeys.New("smoke-mk");
        var mk = await conn.Rest.AddMarkerAsync("开始升温", mkKey, ct);
        Step("POST /sessions/current/markers", $"{mk.HttpStatus} {mk.Status} id={mk.Result?.Id} seq={mk.Result?.Seq}");
        var mk2 = await conn.Rest.AddMarkerAsync("开始升温", mkKey, ct);
        Step("同键重放 markers（Idempotent-Replay）", $"{mk2.HttpStatus} replayed={mk2.Replayed} id={mk2.Result?.Id}", mk2.Replayed && mk2.Result?.Id == mk.Result?.Id);
        var cur = await conn.Rest.SessionCurrentAsync(ct);
        Step("GET /sessions/current", $"200 active={cur?.Active} markers={cur?.Manifest?.Markers.Length}");

        // ---- 3. WS：写命令之后再收 ≥ 5 帧遥测（帧里应看到 RV1 加热 / RV2 程序 / 会话），+ control / opEvent ----
        int baseFrames; lock (rawWs) baseFrames = counts.GetValueOrDefault("telemetry");
        await WaitAsync(() => { lock (rawWs) return counts.GetValueOrDefault("telemetry") >= baseFrames + 5; }, 20000, "写命令后 ≥5 帧遥测");
        var latest = conn.Telemetry.Latest!;
        var rv1 = latest.Channels[0]; var rv2 = latest.Channels[1];
        Step("WS telemetry（写后 ≥5 帧）", $"frames={counts.GetValueOrDefault("telemetry")} store={conn.Telemetry.Count} seq={latest.Seq} mode={latest.Mode.Control} RV1 state={rv1.State} sp={rv1.TempSpC} pv={rv1.TempC:0.0} RV2 program={rv2.Program?.State} step={rv2.Program?.StepIndex}/{rv2.Program?.StepCount} session={latest.Session.Active}:{latest.Session.TestId}",
            rv1.TempSpC == 70 && latest.Session.Active && rv2.Program != null);
        lock (rawWs)
        {
            Step("WS control 消息", $"count={counts.GetValueOrDefault("control")} events=[{string.Join(",", rawWs.Where(x => x.Type == "control").Select(x => JsonDocument.Parse(x.Json).RootElement.GetProperty("event").GetString()))}]", counts.GetValueOrDefault("control") >= 1);
            Step("WS opEvent 消息", $"count={counts.GetValueOrDefault("opEvent")} actions=[{string.Join(",", rawWs.Where(x => x.Type == "opEvent").Select(x => JsonDocument.Parse(x.Json).RootElement.GetProperty("action").GetString()).Distinct())}]", counts.GetValueOrDefault("opEvent") >= 5);
            Step("WS event 消息", $"count={counts.GetValueOrDefault("event")} lastSeq={conn.Ws.LastEventSeq} dup={counts.GetValueOrDefault("eventDup")}");
        }

        // ---- 4. 收尾 ----
        await conn.Lease.ReleaseAsync(ct);
        var ctl2 = await conn.Rest.ControlAsync(ct);
        Step("POST /control/release", $"200 → mode={ctl2.Mode} state={ctl2.State} leaseState={conn.Lease.State}", ctl2.Mode == "local");
        var end = await conn.Rest.EndSessionAsync(ct);
        Step("DELETE /sessions/current", $"{end.HttpStatus} {end.Status}");
        var stop = await conn.Rest.StopHeatAsync(1, ct);
        Step("POST /channels/1/heat/stop（无租约 · 停止类）", $"{stop.HttpStatus} {stop.Status}");
        var abort = await conn.Rest.AbortProgramAsync(2, ct);
        Step("DELETE /channels/2/program/run（停止类）", $"{abort.HttpStatus} {abort.Status}");
        try { await conn.Control.StartHeatAsync(1, 60, ct); Step("释放后受控写（应本地 409）", "unexpected", false); }
        catch (ApiException ex) { Step("释放后受控写（应本地快速失败 409）", $"{ex.Status} {ex.Code}", ex.Status == 409); }
        await Task.Delay(1500, ct);
        var ev = await conn.Rest.EventsAsync(0, 1000, ct);
        Step("GET /events?afterSeq=0", $"200 count={ev.Count} head={ev.EventSeqHead} op.*={ev.Items.Count(e => e.Kind.StartsWith("op."))} control.*={ev.Items.Count(e => e.Kind.StartsWith("control."))} session.*={ev.Items.Count(e => e.Kind.StartsWith("session"))}");

        int rc = Finish(samples, counts, leaseLog, rawWs);
        await conn.StopAsync();
        return rc;
    }

    private static int Finish(string? samples, Dictionary<string, int> counts, List<string> leaseLog, List<(string Type, string Json)> rawWs)
    {
        Console.WriteLine();
        Console.WriteLine("WS 消息类型计数: " + string.Join("  ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        Console.WriteLine("租约迁移: " + string.Join(" | ", leaseLog));
        if (samples != null)
        {
            Directory.CreateDirectory(samples);
            lock (rawWs)
            {
                foreach (var type in new[] { "hello", "telemetry", "event", "control", "opEvent", "subscribed", "pong" })
                {
                    var picks = rawWs.Where(x => x.Type == type).Take(type == "telemetry" ? 2 : 6).Select(x => x.Json).ToList();
                    if (picks.Count > 0) File.WriteAllLines(Path.Combine(samples, $"ws-{type.ToLowerInvariant()}.jsonl"), picks);
                }
            }
            File.WriteAllLines(Path.Combine(samples, "smoke-log.txt"), Log);
            Console.WriteLine("样本已写入 " + samples);
        }
        Console.WriteLine(_fail == 0 ? "SMOKE_OK" : $"SMOKE_FAIL ({_fail})");
        return _fail == 0 ? 0 : 1;
    }

    private static async Task WaitAsync(Func<bool> cond, int ms, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) { Step("等待 " + what, "超时", false); return; }
            await Task.Delay(100);
        }
    }

    private static string Short(string? s) => s == null ? "-" : s.Length > 10 ? s[..10] + "…" : s;
}
