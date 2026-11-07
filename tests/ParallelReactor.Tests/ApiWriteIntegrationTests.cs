using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Commands;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>
/// P3 / P4 进程内集成（Kestrel + FakeApiSources + 脚本化 FakeRemoteCommands）：控制权申请 / 授权 / 心跳 / 过期、写管线（角色、幂等、租约、响应映射、审计）、
/// 急停权限位、UI 超时 503 / 504、会话端点、WS control / opEvent / hb 租约。租约时钟由测试注入（<see cref="FakeApiSources.Clock"/> + <c>host.Control.Tick</c>），不等真实计时器。
/// </summary>
public class ApiWriteIntegrationTests
{
    private const string Viewer = "viewer-token", Operator = "op-token", Admin = "admin-token", Guard = "guard-token", Other = "other-op-token";
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "pr-api-w-" + Guid.NewGuid().ToString("N"));

    private static ApiConfig Cfg(bool autoGrant = true, int writesPerSec = 1000) => new()
    {
        Enabled = true, Bind = "127.0.0.1", HttpPort = 0, MaxClients = 4, AutoGrantRemote = autoGrant,
        HeartbeatTimeoutSec = 10, LeaseGraceSec = 60, ControlRequestTimeoutSec = 60, WriteRateLimitPerSec = writesPerSec,
        Tokens = new List<ApiToken>
        {
            new() { Name = "it", Sha256 = TokenAuth.Hash(Viewer), Role = "viewer" },
            new() { Name = "guard", Sha256 = TokenAuth.Hash(Guard), Role = "viewer", Estop = true },
            new() { Name = "op", Sha256 = TokenAuth.Hash(Operator), Role = "operator" },
            new() { Name = "op2", Sha256 = TokenAuth.Hash(Other), Role = "operator" },
            new() { Name = "adm", Sha256 = TokenAuth.Hash(Admin), Role = "admin" },
        },
    };

    private static async Task<(ApiHost Host, FakeApiSources Src)> StartAsync(ApiConfig? cfg = null)
    {
        var src = new FakeApiSources(Tmp(), TimeSpan.FromMilliseconds(100));
        var host = new ApiHost(src, cfg ?? Cfg());
        Assert.True(await host.StartOnceAsync(), string.Join("\n", src.Audits));
        return (host, src);
    }

    private static HttpClient Client(ApiHost host, string token)
    {
        var c = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static HttpRequestMessage Req(HttpMethod m, string path, string? json = null, string? lease = null, string? idem = null)
    {
        var r = new HttpRequestMessage(m, "/api/v1" + path);
        if (json != null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (lease != null) r.Headers.Add(ApiHost.LeaseHeader, lease);
        if (idem != null) r.Headers.Add(ApiHost.IdempotencyHeader, idem);
        return r;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Resp)> SendAsync(HttpClient c, HttpRequestMessage r)
    {
        var resp = await c.SendAsync(r);
        var text = await resp.Content.ReadAsStringAsync();
        var body = text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (resp.StatusCode, body, resp);
    }

    private static string Code(JsonElement b) => b.GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>申请控制权（autoGrant）并返回 leaseId。</summary>
    private static async Task<(string LeaseId, string RequestId)> AcquireAsync(HttpClient op, string client = "PC-1")
    {
        var (lease, requestId, _) = await AcquireFullAsync(op, client);
        return (lease, requestId);
    }

    private static async Task<(string LeaseId, string RequestId, string RequestKey)> AcquireFullAsync(HttpClient op, string client = "PC-1")
    {
        var (st, b, _) = await SendAsync(op, Req(HttpMethod.Post, "/control/request", $"{{\"clientName\":\"{client}\"}}", idem: Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Accepted, st);
        var res = b.GetProperty("result");
        Assert.Equal("granted", res.GetProperty("result").GetString());
        return (res.GetProperty("leaseId").GetString()!, res.GetProperty("requestId").GetString()!, res.GetProperty("requestKey").GetString()!);
    }

    private static async Task<JsonElement> GetAsync(HttpClient c, string path, string? requestKey = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, "/api/v1" + path);
        if (requestKey != null) r.Headers.Add(ApiHost.RequestKeyHeader, requestKey);
        var resp = await c.SendAsync(r);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Control_Request_AutoGrant_Write_Expire()
    {
        var (host, src) = await StartAsync();
        try
        {
            using var op = Client(host, Operator);
            using var viewer = Client(host, Viewer);

            // 本地模式：控制类写 → 409 MODE_LOCAL（不进命令层）
            var (st0, b0, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}"));
            Assert.Equal(HttpStatusCode.Conflict, st0);
            Assert.Equal("MODE_LOCAL", Code(b0));
            Assert.Empty(src.Fake.Calls);

            // 申请缺 Idempotency-Key → 428；viewer 申请 → 403
            var (st1, b1, _) = await SendAsync(op, Req(HttpMethod.Post, "/control/request", "{\"clientName\":\"PC-1\"}"));
            Assert.Equal((HttpStatusCode)428, st1);
            Assert.Equal("PRECONDITION_REQUIRED", Code(b1));
            var (st2, _, _) = await SendAsync(viewer, Req(HttpMethod.Post, "/control/request", "{\"clientName\":\"PC-v\"}", idem: Guid.NewGuid().ToString()));
            Assert.Equal(HttpStatusCode.Forbidden, st2);

            var (lease, requestId, requestKey) = await AcquireFullAsync(op);
            Assert.Equal(ControlMode.Remote, src.Arbiter.Mode);
            Assert.Equal("PC-1", src.Arbiter.HolderName);
            Assert.Contains(src.Audits, a => a.Contains("control.granted"));

            // GET /control（无 leaseId）与 GET /control/requests/{id}（只对申请令牌给 leaseId）
            var ctl = JsonDocument.Parse(await op.GetStringAsync("/api/v1/control")).RootElement;
            Assert.Equal("remote", ctl.GetProperty("mode").GetString());
            Assert.Equal("PC-1", ctl.GetProperty("holder").GetString());
            Assert.Equal("remote", ctl.GetProperty("state").GetString());
            Assert.DoesNotContain("leaseId", ctl.GetRawText());
            // leaseId 只给「申请令牌 + 申请凭据」：同令牌不带凭据（另一台 PC）拿不到，别的令牌更拿不到
            var rq = await GetAsync(op, "/control/requests/" + requestId, requestKey);
            Assert.Equal(lease, rq.GetProperty("leaseId").GetString());
            Assert.Equal(JsonValueKind.Null, (await GetAsync(op, "/control/requests/" + requestId)).GetProperty("leaseId").ValueKind);
            Assert.Equal(JsonValueKind.Null, (await GetAsync(op, "/control/requests/" + requestId, "0123456789abcdef0123456789abcdef")).GetProperty("leaseId").ValueKind);
            var rq2 = await GetAsync(viewer, "/control/requests/" + requestId, requestKey);
            Assert.Equal("granted", rq2.GetProperty("result").GetString());
            Assert.Equal(JsonValueKind.Null, rq2.GetProperty("leaseId").ValueKind);

            // 远程模式：无头 409 NOT_CONTROL_HOLDER；错头 409；正确租约 → 202 accepted，命令层收到 ClientName = PC-1
            var (st3, b3, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}"));
            Assert.Equal("NOT_CONTROL_HOLDER", Code(b3));
            var (st4, b4, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}", lease: "0123456789abcdef0123456789abcdef"));
            Assert.Equal("NOT_CONTROL_HOLDER", Code(b4));
            src.Fake.Next = () => CommandOutcome.Done(OpResult.Accepted("RV1 加热已启动", "ok", new { tempC = 80.0 }));
            var (st5, b5, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}", lease: lease));
            Assert.Equal(HttpStatusCode.Accepted, st5);
            Assert.True(b5.GetProperty("ok").GetBoolean());
            Assert.Equal("accepted", b5.GetProperty("status").GetString());
            Assert.Equal(80.0, b5.GetProperty("result").GetProperty("tempC").GetDouble());
            Assert.True(b5.GetProperty("opEventSeq").GetInt64() > 0);
            var call = src.Fake.Calls.Single();
            Assert.Equal(("StartHeat", "1,80"), (call.Method, call.Args));
            Assert.Equal("op", call.Caller.TokenName);
            Assert.Equal("PC-1", call.Caller.ClientName);
            Assert.Equal(b5.GetProperty("opId").GetString(), call.Caller.OpId);
            var ev = src.OpEvents.Last();
            Assert.Equal("op.heat.start", ev.Kind);
            Assert.Equal("remote", ev.Source);
            Assert.Equal("PC-1", ev.Client);
            Assert.Equal(call.Caller.OpId, ev.OpId);
            Assert.Equal(b5.GetProperty("opEventSeq").GetInt64(), ev.Seq);

            // 另一令牌拿同一 leaseId → NOT_CONTROL_HOLDER（同一令牌两台 PC 只有持有租约的能写；不同令牌更不行）
            using var other = Client(host, Other);
            var (_, b6, _) = await SendAsync(other, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}", lease: lease));
            Assert.Equal("NOT_CONTROL_HOLDER", Code(b6));

            // 心跳续租；停心跳：10 s 后 Lost，再 60 s 后 Expired → 本地 + control.expired 事件
            var (st7, b7, _) = await SendAsync(op, Req(HttpMethod.Post, "/control/heartbeat", null, lease: lease));
            Assert.Equal(HttpStatusCode.OK, st7);
            Assert.Equal("remote", b7.GetProperty("result").GetProperty("mode").GetString());
            src.Clock += TimeSpan.FromSeconds(11);
            host.Control.Tick(src.Clock);
            Assert.Equal(LeaseState.RemoteLost, src.Lease.State);
            Assert.Equal(ControlMode.Remote, src.Arbiter.Mode);
            Assert.Contains(src.Audits, a => a.Contains("control.lost"));
            src.Clock += TimeSpan.FromSeconds(61);
            host.Control.Tick(src.Clock);
            Assert.Equal(LeaseState.Local, src.Lease.State);
            Assert.Equal(ControlMode.Local, src.Arbiter.Mode);
            var expired = src.OpEvents.Last(e => e.Kind.StartsWith("control."));
            Assert.Equal("control.expired", expired.Kind);
            Assert.Contains("keep", expired.Message);
            var (_, b8, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}", lease: lease));
            Assert.Equal("MODE_LOCAL", Code(b8));
            var (_, b9, _) = await SendAsync(op, Req(HttpMethod.Post, "/control/heartbeat", null, lease: lease));
            Assert.Equal("MODE_LOCAL", Code(b9));
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task Roles_StopClass_Estop_Release()
    {
        var (host, src) = await StartAsync();
        try
        {
            using var viewer = Client(host, Viewer);
            using var guard = Client(host, Guard);
            using var op = Client(host, Operator);
            using var adm = Client(host, Admin);

            // viewer：任何写都 403（含停止类与急停）
            foreach (var r in new[] { Req(HttpMethod.Post, "/channels/1/heat/stop"), Req(HttpMethod.Post, "/estop"), Req(HttpMethod.Post, "/sessions", "{\"testId\":\"T\"}", idem: Guid.NewGuid().ToString()) })
            {
                var (st, b, _) = await SendAsync(viewer, r);
                Assert.Equal(HttpStatusCode.Forbidden, st);
                Assert.Equal("FORBIDDEN_ROLE", Code(b));
            }
            Assert.Empty(src.Fake.Calls);
            // 带 estop 位的 viewer 可以急停与停止类（§4.2 / §7.1「operator 或 estop 权限」），不能做控制类 / 记录类
            src.Fake.Next = () => CommandOutcome.Done(OpResult.Accepted("", "ok", new { source = "远程（guard）" }));
            var (stE, bE, _) = await SendAsync(guard, Req(HttpMethod.Post, "/estop", "{\"note\":\"压力异常\"}"));
            Assert.Equal(HttpStatusCode.Accepted, stE);
            Assert.Equal(("Estop", "压力异常"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(guard, Req(HttpMethod.Post, "/channels/1/heat/stop"))).Status);
            Assert.Equal(("StopHeat", "1"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(guard, Req(HttpMethod.Post, "/stop-all"))).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(guard, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}"))).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(guard, Req(HttpMethod.Post, "/sessions/current/markers", "{\"text\":\"x\"}", idem: Guid.NewGuid().ToString()))).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(guard, Req(HttpMethod.Post, "/control/heartbeat"))).Status);
            // estop 位 + 超长 note：截到 200 字符
            var (stN, _, _) = await SendAsync(guard, Req(HttpMethod.Post, "/estop", "{\"note\":\"" + new string('长', 500) + "\"}"));
            Assert.Equal(HttpStatusCode.OK, stN);   // Fake 默认结局 Done → 200
            Assert.Equal(Endpoints.NoteMax, src.Fake.Calls.Last().Args.Length);

            // 停止类在本地模式下远程也允许（免租约）：heat/stop、stop-all、PUT /stir {on:false}、DELETE program/run
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, Req(HttpMethod.Post, "/channels/2/heat/stop"))).Status);
            Assert.Equal(("StopHeat", "2"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, Req(HttpMethod.Post, "/stop-all", "{\"stopStir\":true}"))).Status);
            Assert.Equal(("StopAll", "True"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, Req(HttpMethod.Put, "/stir", "{\"on\":false}"))).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, Req(HttpMethod.Delete, "/channels/3/program/run"))).Status);
            Assert.Equal(("AbortProgram", "3"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));
            // 开搅拌 / 改转速是控制类 → 本地模式 409
            Assert.Equal("MODE_LOCAL", Code((await SendAsync(op, Req(HttpMethod.Put, "/stir", "{\"on\":true}"))).Body));
            Assert.Equal("MODE_LOCAL", Code((await SendAsync(op, Req(HttpMethod.Put, "/stir", "{\"on\":false,\"rpm\":300}"))).Body));
            Assert.Equal("MODE_LOCAL", Code((await SendAsync(op, Req(HttpMethod.Put, "/stir", "{}"))).Body));   // 租约先于参数校验（§7.1 管线顺序）
            // 通道号越界 → 404 CHANNEL_NOT_FOUND；不暴露的动作 → 404 NOT_FOUND
            var (st9, b9, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/9/heat/stop"));
            Assert.Equal(HttpStatusCode.NotFound, st9);
            Assert.Equal("CHANNEL_NOT_FOUND", Code(b9));
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(op, Req(HttpMethod.Post, "/estop/reset"))).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(adm, Req(HttpMethod.Patch, "/settings", "{}"))).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(op, Req(HttpMethod.Post, "/gas/purge", "{}"))).Status);

            // PID 需 admin
            var (lease, _) = await AcquireAsync(op);
            Assert.Equal("BAD_REQUEST", Code((await SendAsync(op, Req(HttpMethod.Put, "/stir", "{}", lease: lease))).Body));
            var (stP, bP, _) = await SendAsync(op, Req(HttpMethod.Put, "/channels/1/pid/0", "{\"p\":1,\"i\":2,\"d\":3}", lease: lease));
            Assert.Equal(HttpStatusCode.Forbidden, stP);
            // admin 拿 op 的租约 → NOT_CONTROL_HOLDER（租约绑定令牌）；本地切回后 admin 自己申请 → 可写 PID；band 越界 400
            Assert.Equal("NOT_CONTROL_HOLDER", Code((await SendAsync(adm, Req(HttpMethod.Put, "/channels/1/pid/0", "{\"p\":1,\"i\":2,\"d\":3}", lease: lease))).Body));
            Assert.True(src.Lease.TakeBack(src.Clock));
            (lease, _) = await AcquireAsync(adm, "PC-adm");
            Assert.Equal("BAD_REQUEST", Code((await SendAsync(adm, Req(HttpMethod.Put, "/channels/1/pid/5", "{\"p\":1,\"i\":2,\"d\":3}", lease: lease))).Body));
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(adm, Req(HttpMethod.Put, "/channels/1/pid/2", "{\"p\":1,\"i\":2,\"d\":3}", lease: lease))).Status);
            Assert.Equal(("SetPid", "1,2,1,2,3"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));

            // 急停（operator 默认带 estop 位）：两种模式都允许，并强制回本地、租约作废
            src.Fake.Next = () => { src.Arbiter.ReturnToLocal("急停"); return CommandOutcome.Done(OpResult.Accepted("", "ok", new { source = "远程（PC-1）" })); };
            var (stX, bX, _) = await SendAsync(op, Req(HttpMethod.Post, "/estop"));
            Assert.Equal(HttpStatusCode.Accepted, stX);
            Assert.Equal(ControlMode.Local, src.Arbiter.Mode);
            Assert.Equal(LeaseState.Local, src.Lease.State);
            Assert.Contains(src.OpEvents, e => e.Kind == "control.mode" && e.Message.Contains("急停"));
            Assert.Equal("MODE_LOCAL", Code((await SendAsync(adm, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}", lease: lease))).Body));

            // 重新申请 → 释放
            var (lease2, _) = await AcquireAsync(op);
            Assert.Equal("NOT_CONTROL_HOLDER", Code((await SendAsync(op, Req(HttpMethod.Post, "/control/release"))).Body));
            var (stR, bR, _) = await SendAsync(op, Req(HttpMethod.Post, "/control/release", null, lease: lease2));
            Assert.Equal(HttpStatusCode.OK, stR);
            Assert.Equal(ControlMode.Local, src.Arbiter.Mode);
            Assert.Contains(src.Audits, a => a.Contains("control.released"));
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task Idempotency_Required_Replay_Mismatch_InProgress()
    {
        var (host, src) = await StartAsync();
        try
        {
            using var op = Client(host, Operator);
            var (lease, _) = await AcquireAsync(op);
            // /run 必填
            var (st0, b0, _) = await SendAsync(op, Req(HttpMethod.Post, "/run", null, lease: lease));
            Assert.Equal((HttpStatusCode)428, st0);
            Assert.Equal("PRECONDITION_REQUIRED", Code(b0));

            string key = Guid.NewGuid().ToString();
            src.Fake.Next = () => CommandOutcome.Done(OpResult.Accepted("ok", "ok", new { requested = new[] { 1 } }));
            var (st1, b1, r1) = await SendAsync(op, Req(HttpMethod.Post, "/run", "{}", lease: lease, idem: key));
            Assert.Equal(HttpStatusCode.Accepted, st1);
            Assert.Equal(key, b1.GetProperty("opId").GetString());
            Assert.False(r1.Headers.Contains("Idempotent-Replay"));
            int calls = src.Fake.Calls.Count;
            // 同键同体 → 重放，不再执行
            var (st2, b2, r2) = await SendAsync(op, Req(HttpMethod.Post, "/run", "{}", lease: lease, idem: key));
            Assert.Equal(HttpStatusCode.Accepted, st2);
            Assert.Equal(b1.GetRawText(), b2.GetRawText());
            Assert.Equal("true", r2.Headers.GetValues("Idempotent-Replay").Single());
            Assert.Equal(calls, src.Fake.Calls.Count);
            // 同键不同体 → 409
            var (st3, b3, _) = await SendAsync(op, Req(HttpMethod.Post, "/run", "{\"dryRun\":true}", lease: lease, idem: key));
            Assert.Equal(HttpStatusCode.Conflict, st3);
            Assert.Equal("IDEMPOTENCY_MISMATCH", Code(b3));

            // 执行中（OUTCOME_UNKNOWN）：504，同键重发等待 → 内层完成后得到最终结果；条目回填
            var tcs = new TaskCompletionSource<OpResult>();
            string key2 = Guid.NewGuid().ToString();
            src.Fake.Next = () => CommandOutcome.Unknown(tcs.Task);
            var (st4, b4, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}", lease: lease, idem: key2));
            Assert.Equal(HttpStatusCode.GatewayTimeout, st4);
            Assert.Equal("OUTCOME_UNKNOWN", Code(b4));
            var retry = SendAsync(op, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}", lease: lease, idem: key2));
            await Task.Delay(200);
            Assert.False(retry.IsCompleted);
            tcs.SetResult(OpResult.Accepted("RV1 加热已启动", "ok", new { tempC = 80.0 }));
            var (st5, b5, r5) = await retry;
            Assert.Equal(HttpStatusCode.Accepted, st5);
            Assert.Equal("accepted", b5.GetProperty("status").GetString());
            Assert.Equal("true", r5.Headers.GetValues("Idempotent-Replay").Single());
            Assert.Contains(src.OpEvents, e => e.Kind == "op.heat.start.result" && e.OpId == key2);

            // 未执行就被拒（无租约）的键不缓存：拿到租约后同键重发可以执行
            string key3 = Guid.NewGuid().ToString();
            var (_, b6, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/2/program/run", "{}", idem: key3));
            Assert.Equal("NOT_CONTROL_HOLDER", Code(b6));
            var (st7, _, r7) = await SendAsync(op, Req(HttpMethod.Post, "/channels/2/program/run", "{}", lease: lease, idem: key3));
            Assert.Equal(HttpStatusCode.OK, st7);
            Assert.False(r7.Headers.Contains("Idempotent-Replay"));
            Assert.Equal(("RunProgram", "2"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));

            // UI 线程超时 → 503，可重试
            src.Fake.Next = () => CommandOutcome.UiTimeout;
            var (st8, b8, _) = await SendAsync(op, Req(HttpMethod.Put, "/channels/1/temperature/setpoint", "{\"tempC\":90}", lease: lease));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, st8);
            Assert.Equal("UI_THREAD_TIMEOUT", Code(b8));
            // HW_UNCONFIRMED → 202 + warnings
            var tail = new TaskCompletionSource<OpResult>();
            src.Fake.Next = () => CommandOutcome.Unconfirmed(tail.Task);
            var (st9, b9, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/heat/stop"));
            Assert.Equal(HttpStatusCode.Accepted, st9);
            Assert.Equal("HW_UNCONFIRMED", b9.GetProperty("warnings")[0].GetString());
            tail.SetResult(OpResult.Done("RV1 加热已停止"));
            await Task.Delay(100);
            Assert.Contains(src.OpEvents, e => e.Kind == "op.heat.stop.result");
            // 参数错误 → 400（不进命令层）
            var (st10, b10, _) = await SendAsync(op, Req(HttpMethod.Put, "/channels/1/temperature/setpoint", "{\"tempC\":\"hot\"}", lease: lease));
            Assert.Equal(HttpStatusCode.BadRequest, st10);
            Assert.Equal("BAD_REQUEST", Code(b10));
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(op, Req(HttpMethod.Put, "/channels/1/temperature/setpoint", "not json", lease: lease))).Status);
            // 命令层失败 → 按码映射（422）
            src.Fake.Next = () => CommandOutcome.Done(OpResult.Fail(OpCodes.ParamOutOfRange, "越界", "err", new { min = 10, max = 200 }));
            var (st11, b11, _) = await SendAsync(op, Req(HttpMethod.Put, "/channels/1/temperature/setpoint", "{\"tempC\":500}", lease: lease));
            Assert.Equal((HttpStatusCode)422, st11);
            Assert.Equal(200, b11.GetProperty("error").GetProperty("detail").GetProperty("max").GetInt32());
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task Idempotency_InProgressReject_DoesNotDropOthersEntry_And_TokenScoped()
    {
        var (host, src) = await StartAsync();
        try
        {
            using var op = Client(host, Operator);
            using var adm = Client(host, Admin);
            var (lease, _) = await AcquireAsync(op);

            // 504 OUTCOME_UNKNOWN 后：同键重发等 5 s 仍未完 → 409 IN_PROGRESS，但原条目必须还在；内层完成后第三次重发拿到最终结果而不是再执行
            var tcs = new TaskCompletionSource<OpResult>();
            string key = Guid.NewGuid().ToString();
            src.Fake.Next = () => CommandOutcome.Unknown(tcs.Task);
            var (st1, b1, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/program/run", "{}", lease: lease, idem: key));
            Assert.Equal(HttpStatusCode.GatewayTimeout, st1);
            int calls = src.Fake.Calls.Count;
            var (st2, b2, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/program/run", "{}", lease: lease, idem: key));
            Assert.Equal(HttpStatusCode.Conflict, st2);
            Assert.Equal("IDEMPOTENCY_IN_PROGRESS", Code(b2));
            Assert.Equal(key, b2.GetProperty("opId").GetString());
            Assert.Equal(calls, src.Fake.Calls.Count);
            // 同键不同体 → 409 MISMATCH，同样不能撤掉别人的条目
            int entries = host.Idem.Count;
            Assert.Equal("IDEMPOTENCY_MISMATCH", Code((await SendAsync(op, Req(HttpMethod.Post, "/channels/1/program/run", "{\"x\":1}", lease: lease, idem: key))).Body));
            Assert.Equal(entries, host.Idem.Count);
            tcs.SetResult(OpResult.Accepted("RV1 程序已下发", "ok", new { rv = 1 }));
            await Task.Delay(100);
            var (st3, b3, r3) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/program/run", "{}", lease: lease, idem: key));
            Assert.Equal(HttpStatusCode.Accepted, st3);
            Assert.Equal("true", r3.Headers.GetValues("Idempotent-Replay").Single());
            Assert.Equal(1, b3.GetProperty("result").GetProperty("rv").GetInt32());
            Assert.Equal(calls, src.Fake.Calls.Count);   // 没有第二次执行

            // 幂等键按令牌隔离：admin 用 op 的键 + 同体 → 不是重放（自己执行一次，拿到自己的结果）
            src.Fake.Next = () => CommandOutcome.Done(OpResult.Done("ok", "ok", new { who = "adm" }));
            var (st4, b4, r4) = await SendAsync(adm, Req(HttpMethod.Post, "/sessions/current/markers", "{\"text\":\"m\"}", idem: key));
            Assert.Equal(HttpStatusCode.OK, st4);
            Assert.False(r4.Headers.Contains("Idempotent-Replay"));
            Assert.Equal("adm", b4.GetProperty("result").GetProperty("who").GetString());
            // op 自己的 key 仍是 program/run 的那条
            Assert.Equal("IDEMPOTENCY_MISMATCH", Code((await SendAsync(op, Req(HttpMethod.Post, "/sessions/current/markers", "{\"text\":\"m\"}", idem: key))).Body));

            // HW_UNCONFIRMED：202 先回，幂等条目等尾部结束才登记最终结果（尾部失败 → 重放拿到 502，不是永远 202）
            var tail = new TaskCompletionSource<OpResult>();
            string key5 = Guid.NewGuid().ToString();
            src.Fake.Next = () => CommandOutcome.Unconfirmed(tail.Task);
            var (st5, b5, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/2/heat/start", "{\"tempC\":50}", lease: lease, idem: key5));
            Assert.Equal(HttpStatusCode.Accepted, st5);
            Assert.Equal("HW_UNCONFIRMED", b5.GetProperty("warnings")[0].GetString());
            var replay = SendAsync(op, Req(HttpMethod.Post, "/channels/2/heat/start", "{\"tempC\":50}", lease: lease, idem: key5));
            await Task.Delay(150);
            Assert.False(replay.IsCompleted);
            tail.SetResult(OpResult.Fail(OpCodes.HardwareWriteFailed, "写仪表失败", "err"));
            var (st6, b6, r6) = await replay;
            Assert.Equal(HttpStatusCode.BadGateway, st6);
            Assert.Equal("HARDWARE_WRITE_FAILED", Code(b6));
            Assert.Equal("true", r6.Headers.GetValues("Idempotent-Replay").Single());

            // 命令层异常：500 通用文字（不含异常原文），原文进审计
            src.Fake.Next = () => throw new InvalidOperationException("/Users/secret/path.cs 崩了");
            var (st7, b7, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/3/heat/start", "{\"tempC\":50}", lease: lease));
            Assert.Equal(HttpStatusCode.InternalServerError, st7);
            Assert.Equal("INTERNAL", Code(b7));
            Assert.DoesNotContain("secret", b7.GetRawText());
            Assert.Contains(src.Audits, a => a.Contains("secret/path.cs"));

            // 审计明细带 ip / 租约前缀 / 令牌（§4.2）
            var last = src.OpEvents.Last(e => e.Kind == "op.heat.start");
            Assert.Contains("ip 127.0.0.1", last.Message);
            Assert.Contains("租约 " + lease[..8], last.Message);
            Assert.Contains("令牌 op", last.Message);
            Assert.Equal("PC-1", last.Client);
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task Sessions_Endpoints_And_ProgramRunSessionId()
    {
        var (host, src) = await StartAsync();
        try
        {
            using var op = Client(host, Operator);
            Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync("/api/v1/sessions/current")).StatusCode);
            Assert.Equal((HttpStatusCode)428, (await SendAsync(op, Req(HttpMethod.Post, "/sessions", "{\"testId\":\"TP-017\"}"))).Status);
            Assert.Equal("BAD_REQUEST", Code((await SendAsync(op, Req(HttpMethod.Post, "/sessions", "{}", idem: Guid.NewGuid().ToString()))).Body));

            var manifest = new SessionManifest("TP-017-20261107-080000", "TP-017", "性能测试", DateTime.UtcNow, null, SessionManifest.Active, "remote", "op", true, 5, null,
                "TP-017-20261107-080000.csv", Array.Empty<SessionMarker>(), Array.Empty<SessionProgramRun>(), Array.Empty<SessionLeakTest>());
            src.Fake.Next = () => { src.CurrentSession = manifest; return CommandOutcome.Done(OpResult.Done("测试会话已开始", "ok", new { id = manifest.Id, testId = "TP-017" })); };
            var (st1, b1, _) = await SendAsync(op, Req(HttpMethod.Post, "/sessions", "{\"testId\":\"TP-017\",\"note\":\"性能测试\"}", idem: Guid.NewGuid().ToString()));
            Assert.Equal(HttpStatusCode.OK, st1);
            Assert.Equal(("BeginSession", "TP-017,性能测试"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));
            Assert.Equal("TP-017-20261107-080000", b1.GetProperty("result").GetProperty("id").GetString());

            var cur = JsonDocument.Parse(await op.GetStringAsync("/api/v1/sessions/current")).RootElement;
            Assert.True(cur.GetProperty("active").GetBoolean());
            Assert.Equal("TP-017-20261107-080000", cur.GetProperty("id").GetString());
            Assert.Equal("性能测试", cur.GetProperty("manifest").GetProperty("note").GetString());
            var byId = JsonDocument.Parse(await op.GetStringAsync("/api/v1/sessions/TP-017-20261107-080000")).RootElement;
            Assert.Equal("TP-017", byId.GetProperty("testId").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync("/api/v1/sessions/nope-20260101-000000")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync("/api/v1/sessions/..%2Fx")).StatusCode);

            // 打点（必填幂等键）；program/run 的 sessionId 不符 → 400
            Assert.Equal((HttpStatusCode)428, (await SendAsync(op, Req(HttpMethod.Post, "/sessions/current/markers", "{\"text\":\"开始升温\"}"))).Status);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, Req(HttpMethod.Post, "/sessions/current/markers", "{\"text\":\"开始升温\"}", idem: Guid.NewGuid().ToString()))).Status);
            Assert.Equal(("AddMarker", "开始升温"), (src.Fake.Calls.Last().Method, src.Fake.Calls.Last().Args));
            var (lease, _) = await AcquireAsync(op);
            var (stB, bB, _) = await SendAsync(op, Req(HttpMethod.Post, "/channels/1/program/run", "{\"sessionId\":\"S-wrong\"}", lease: lease, idem: Guid.NewGuid().ToString()));
            Assert.Equal(HttpStatusCode.BadRequest, stB);
            Assert.Contains("TP-017-20261107-080000", bB.GetProperty("error").GetProperty("message").GetString());
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, Req(HttpMethod.Post, "/channels/1/program/run", "{\"sessionId\":\"TP-017-20261107-080000\"}", lease: lease, idem: Guid.NewGuid().ToString()))).Status);

            // 结束：命令层判归属（这里模拟 SESSION_NOT_OWNER → 403）
            src.Fake.Next = () => CommandOutcome.Done(OpResult.Fail(OpCodes.SessionNotOwner, "只有会话创建者或设备触屏可以结束该会话", "warn"));
            var (stN, bN, _) = await SendAsync(op, Req(HttpMethod.Delete, "/sessions/current"));
            Assert.Equal(HttpStatusCode.Forbidden, stN);
            Assert.Equal("SESSION_NOT_OWNER", Code(bN));
            src.Fake.Next = () => { src.CurrentSession = null; return CommandOutcome.Done(OpResult.Done("测试会话已结束")); };
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, Req(HttpMethod.Delete, "/sessions/current"))).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync("/api/v1/sessions/current")).StatusCode);
            // GET /sessions 的 current 与 /sessions/current 同源（不是晚一个 Tick 的遥测快照）
            Assert.False(JsonDocument.Parse(await op.GetStringAsync("/api/v1/sessions")).RootElement.GetProperty("current").GetProperty("active").GetBoolean());
            // 第二个会话 → 命令层 SESSION_ACTIVE → 409
            src.Fake.Next = () => CommandOutcome.Done(OpResult.Fail(OpCodes.SessionActive, "已有测试会话进行中", "warn"));
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(op, Req(HttpMethod.Post, "/sessions", "{\"testId\":\"X\"}", idem: Guid.NewGuid().ToString()))).Status);
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[256 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buf, ct); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
        if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("closed");
        return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray()));
    }

    [Fact]
    public async Task WebSocket_Control_OpEvent_And_HbLease()
    {
        var (host, src) = await StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;
        try
        {
            // op 连接（申请方令牌）与 viewer 连接：control 消息都不带 leaseId（同一令牌的另一台 PC 也在听）
            async Task<ClientWebSocket> Connect(string token)
            {
                var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                await ws.ConnectAsync(new Uri(host.BaseUrl.Replace("http://", "ws://") + "/api/v1/ws"), ct);
                return ws;
            }
            using var wsOp = await Connect(Operator);
            using var wsV = await Connect(Viewer);
            await ReceiveAsync(wsOp, ct); await ReceiveAsync(wsV, ct);   // hello

            using var op = Client(host, Operator);
            var (lease, requestId) = await AcquireAsync(op);
            src.Fake.Next = () => CommandOutcome.Done(OpResult.Accepted("ok", "ok"));
            await SendAsync(op, Req(HttpMethod.Post, "/channels/1/heat/start", "{\"tempC\":80}", lease: lease));

            async Task<(JsonElement? Granted, JsonElement? Op)> Collect(ClientWebSocket ws)
            {
                JsonElement? granted = null, opEv = null;
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline && (granted == null || opEv == null))
                {
                    using var m = await ReceiveAsync(ws, ct);
                    var t = m.RootElement.GetProperty("type").GetString();
                    if (t == "control" && m.RootElement.GetProperty("event").GetString() == "granted") granted = m.RootElement.Clone();
                    if (t == "opEvent" && m.RootElement.GetProperty("action").GetString() == "heat.start") opEv = m.RootElement.Clone();
                }
                return (granted, opEv);
            }
            var (gOp, oOp) = await Collect(wsOp);
            var (gV, oV) = await Collect(wsV);
            Assert.NotNull(gOp); Assert.NotNull(gV);
            Assert.Equal(JsonValueKind.Null, gOp!.Value.GetProperty("leaseId").ValueKind);
            Assert.Equal(JsonValueKind.Null, gV!.Value.GetProperty("leaseId").ValueKind);
            Assert.Equal("remote", gOp.Value.GetProperty("state").GetString());
            Assert.Equal("remote", gV.Value.GetProperty("mode").GetString());
            Assert.Equal("PC-1", gV.Value.GetProperty("holder").GetString());
            Assert.Equal(requestId, gV.Value.GetProperty("requestId").GetString());
            Assert.NotNull(oOp); Assert.NotNull(oV);
            Assert.Equal("heat.start", oOp!.Value.GetProperty("action").GetString());
            Assert.Equal(202, oOp.Value.GetProperty("httpStatus").GetInt32());
            Assert.Equal("PC-1", oOp.Value.GetProperty("client").GetString());

            // hb 带租约 = 心跳：推进 9 s 发 hb，再推进 9 s 仍为 Remote（不发则 10 s 就 Lost）
            src.Clock += TimeSpan.FromSeconds(9);
            await wsOp.SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":\"hb\",\"lease\":\"{lease}\"}}"), WebSocketMessageType.Text, true, ct);
            await Task.Delay(300);
            src.Clock += TimeSpan.FromSeconds(9);
            host.Control.Tick(src.Clock);
            Assert.Equal(LeaseState.Remote, src.Lease.State);
            // 不带租约 / 错租约的 hb：只保活（错租约回 error）
            await wsV.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"hb\"}"), WebSocketMessageType.Text, true, ct);
            await wsV.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"hb\",\"lease\":\"deadbeef\"}"), WebSocketMessageType.Text, true, ct);
            bool err = false;
            var dl = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < dl && !err)
            {
                using var m = await ReceiveAsync(wsV, ct);
                if (m.RootElement.GetProperty("type").GetString() == "error") { err = true; Assert.Equal("NOT_CONTROL_HOLDER", m.RootElement.GetProperty("code").GetString()); }
            }
            Assert.True(err);
            src.Clock += TimeSpan.FromSeconds(11);
            host.Control.Tick(src.Clock);
            Assert.Equal(LeaseState.RemoteLost, src.Lease.State);
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task Writes_AreRateLimitedPerToken_EstopIsNot()
    {
        var (host, src) = await StartAsync(Cfg(writesPerSec: 10));
        try
        {
            using var op = Client(host, Operator);
            var rs = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => op.SendAsync(Req(HttpMethod.Post, "/channels/1/heat/stop"))));
            Assert.Contains(rs, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Contains(rs, r => r.StatusCode == HttpStatusCode.TooManyRequests);
            // /estop 单独分区：写命令刷满配额后急停仍放行（每令牌 5 次/秒，不排队）
            var es = await Task.WhenAll(Enumerable.Range(0, ApiHost.EstopRateLimitPerSec).Select(_ => op.SendAsync(Req(HttpMethod.Post, "/estop"))));
            Assert.All(es, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));   // 默认结局 Done → 200
            await Task.Delay(1100);
            var flood = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => op.SendAsync(Req(HttpMethod.Post, "/estop"))));
            Assert.Contains(flood, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Contains(flood, r => r.StatusCode == HttpStatusCode.TooManyRequests);
            // 心跳单独分区：写命令被 429 时心跳照常
            await Task.Delay(1100);
            var (lease, _) = await AcquireAsync(op);
            var ws = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => op.SendAsync(Req(HttpMethod.Post, "/channels/1/heat/stop"))));
            Assert.Contains(ws, r => r.StatusCode == HttpStatusCode.TooManyRequests);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(op, Req(HttpMethod.Post, "/control/heartbeat", null, lease: lease))).Status);
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }

    [Fact]
    public async Task ManualRequest_PendingThenGrantByLocal_AndRequestTimeout()
    {
        var (host, src) = await StartAsync(Cfg(autoGrant: false));
        try
        {
            using var op = Client(host, Operator);
            var (st, b, _) = await SendAsync(op, Req(HttpMethod.Post, "/control/request", "{\"clientName\":\"PC-1\"}", idem: Guid.NewGuid().ToString()));
            Assert.Equal(HttpStatusCode.Accepted, st);
            var res = b.GetProperty("result");
            Assert.Equal("pending", res.GetProperty("result").GetString());
            string requestId = res.GetProperty("requestId").GetString()!;
            string requestKey = res.GetProperty("requestKey").GetString()!;
            Assert.Equal(JsonValueKind.Null, res.GetProperty("leaseId").ValueKind);
            var ctl = JsonDocument.Parse(await op.GetStringAsync("/api/v1/control")).RootElement;
            Assert.Equal("requested", ctl.GetProperty("state").GetString());
            Assert.Equal(requestId, ctl.GetProperty("pending").GetProperty("requestId").GetString());
            // 第二个申请 → 409 CONTROL_REQUEST_PENDING
            using var other = Client(host, Other);
            Assert.Equal("CONTROL_REQUEST_PENDING", Code((await SendAsync(other, Req(HttpMethod.Post, "/control/request", "{\"clientName\":\"PC-2\"}", idem: Guid.NewGuid().ToString()))).Body));
            // 触屏允许（命令层 TryGrantControl → Lease.Grant）
            var lease = src.Lease.Grant(requestId, src.Clock)!;
            var rq = await GetAsync(op, "/control/requests/" + requestId, requestKey);
            Assert.Equal("granted", rq.GetProperty("result").GetString());
            Assert.Equal(lease.LeaseId, rq.GetProperty("leaseId").GetString());
            // 本地切回 → 旧租约 CONTROL_LEASE_EXPIRED（下一次授权后可区分）
            src.Lease.TakeBack(src.Clock);
            Assert.Equal(ControlMode.Local, src.Arbiter.Mode);
            Assert.Contains(src.Audits, a => a.Contains("control.takenBack"));
            // 申请 60 s 未处理 → 过期（切回本地只作废租约，不进入拒绝冷却）
            var (st2, b2, _) = await SendAsync(op, Req(HttpMethod.Post, "/control/request", "{\"clientName\":\"PC-1\"}", idem: Guid.NewGuid().ToString()));
            Assert.Equal(HttpStatusCode.Accepted, st2);
            string req2 = b2.GetProperty("result").GetProperty("requestId").GetString()!;
            src.Clock += TimeSpan.FromSeconds(61);
            host.Control.Tick(src.Clock);
            Assert.Equal("expired", JsonDocument.Parse(await op.GetStringAsync("/api/v1/control/requests/" + req2)).RootElement.GetProperty("result").GetString());
            Assert.Contains(src.Audits, a => a.Contains("control.requestExpired"));
            // 超时后同令牌 30 s 内再申请 → 429 RATE_LIMITED（防刷弹窗）；过了冷却可再申请
            var (st3, b3, _) = await SendAsync(op, Req(HttpMethod.Post, "/control/request", "{\"clientName\":\"PC-1\"}", idem: Guid.NewGuid().ToString()));
            Assert.Equal(HttpStatusCode.TooManyRequests, st3);
            Assert.Equal("RATE_LIMITED", Code(b3));
            src.Clock += TimeSpan.FromSeconds(31);
            Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(op, Req(HttpMethod.Post, "/control/request", "{\"clientName\":\"PC-1\"}", idem: Guid.NewGuid().ToString()))).Status);
        }
        finally { await host.StopAsync(); src.Dispose(); }
    }
}
