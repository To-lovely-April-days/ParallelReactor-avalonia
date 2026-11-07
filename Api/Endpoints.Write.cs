using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using ParallelReactor.Commands;

namespace ParallelReactor.Api;

/// <summary>
/// 写命令端点（设计稿 §7.3–§7.10、命令层设计 §12.1 映射表；P3）。每个端点 = 一条 <see cref="WriteSpec"/> + 参数绑定 + 一次 <see cref="IRemoteCommands"/> 调用。
/// 公共管线 <see cref="CommandAsync"/>（顺序固定）：角色（viewer → 403；停止类 operator 或 estop 位）→ /estop 急停位 → 正文 JSON（400）→ 幂等（按令牌隔离；必填 428 / 重放 / mismatch 409 / in-progress）
/// → 动作类（Control 须带有效租约：无头 409 NOT_CONTROL_HOLDER、本地模式 409 MODE_LOCAL、作废 409 CONTROL_LEASE_EXPIRED；Stop / Record 免租约）
/// → 参数绑定（400）→ 命令 → <see cref="CommandResults"/> → 审计 <c>op.&lt;action&gt;</c>（其 seq 即 opEventSeq）→ WS <c>opEvent</c> → 幂等表 Complete。
/// 不暴露的动作（附录 B / §7.13）不映射路由，落到统一 404。
/// </summary>
public static partial class Endpoints
{
    /// <summary>一个写端点的静态属性：动作名（事件 kind <c>op.{Action}</c>）、仲裁类别、是否必填幂等键、所需角色、是否按急停权限位放行。</summary>
    public sealed record WriteSpec(string Action, ActionClass Class, bool IdemRequired = false, string Role = "operator", bool Estop = false);

    public static readonly TimeSpan InProgressWait = TimeSpan.FromSeconds(5);

    private static void MapWrites(RouteGroupBuilder g, ApiHost host)
    {
        var cmd = host.Sources.Commands;
        var w = g.MapGroup("").RequireRateLimiting(ApiHost.WritesPolicy);

        // —— §7.3 急停 / 全局停止 ——
        g.MapPost("/estop", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("estop", ActionClass.Stop, Estop: true),
            (b, c) => cmd.EstopAsync(c, Note(b, "note")))))
            .RequireRateLimiting(ApiHost.EstopPolicy);
        w.MapPost("/stop-all", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("stopAll", ActionClass.Stop),
            (b, c) => cmd.StopAllAsync(c, Bool(b, "stopStir") ?? false, ctx.RequestAborted))));

        // —— §7.4 运行 / 通道 ——
        w.MapPost("/run", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("run", ActionClass.Control, IdemRequired: true),
            (b, c) => cmd.RunAsync(c, ctx.RequestAborted))));
        w.MapPost("/channels/{id}/enable", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("channel.enable", ActionClass.Control),
            (b, c) => cmd.EnableChannelAsync(c, Channel(id), ctx.RequestAborted)));
        w.MapPost("/channels/{id}/start", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("channel.start", ActionClass.Control),
            (b, c) => cmd.StartChannelAsync(c, Channel(id), Num(b, "tempC"), ctx.RequestAborted)));
        w.MapPost("/channels/{id}/stop", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("channel.stop", ActionClass.Stop),
            (b, c) => cmd.StopChannelAsync(c, Channel(id), false, ctx.RequestAborted)));
        w.MapPost("/channels/{id}/disable", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("channel.disable", ActionClass.Stop),
            (b, c) => cmd.StopChannelAsync(c, Channel(id), true, ctx.RequestAborted)));
        w.MapPost("/channels/copy-config", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("channel.copyConfig", ActionClass.Control, IdemRequired: true),
            (b, c) => cmd.CopyChannelConfigAsync(c, Channel(Require(b, "from")), IntList(b, "to", required: false), ctx.RequestAborted))));
        w.MapPost("/channels/{id}/gas-counter/reset", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("channel.gasCounterReset", ActionClass.Control),
            (b, c) => cmd.ResetGasCounterAsync(c, Channel(id), ctx.RequestAborted)));

        // —— §7.5 温度 ——
        w.MapPost("/channels/{id}/heat/start", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("heat.start", ActionClass.Control),
            (b, c) => cmd.StartHeatAsync(c, Channel(id), Num(b, "tempC"), ctx.RequestAborted)));
        w.MapPost("/channels/{id}/heat/stop", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("heat.stop", ActionClass.Stop),
            (b, c) => cmd.StopHeatAsync(c, Channel(id), ctx.RequestAborted)));
        w.MapPut("/channels/{id}/temperature/setpoint", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("temp.setpoint", ActionClass.Control),
            (b, c) => cmd.SetSetpointAsync(c, Channel(id), Num(b, "tempC") ?? throw new ApiBadRequestException("缺少 tempC"), ctx.RequestAborted)));
        w.MapPut("/channels/{id}/temperature/mode", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("temp.mode", ActionClass.Control),
            (b, c) => cmd.SetSpModeAsync(c, Channel(id), Str(b, "spMode") ?? throw new ApiBadRequestException("缺少 spMode（fixed / curve）"), ctx.RequestAborted)));
        w.MapPut("/channels/{id}/profile", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("profile.replace", ActionClass.Control),
            (b, c) => cmd.ReplaceProfileAsync(c, Channel(id), Segments(b), ctx.RequestAborted)));
        w.MapPost("/channels/{id}/profile/start", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("profile.start", ActionClass.Control),
            (b, c) => cmd.StartProfileAsync(c, Channel(id), ctx.RequestAborted)));
        w.MapPost("/channels/{id}/profile/stop", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("profile.stop", ActionClass.Stop),
            (b, c) => cmd.StopProfileAsync(c, Channel(id), ctx.RequestAborted)));

        // —— §7.6 PID / 自整定（PID 需 admin）——
        w.MapPut("/channels/{id}/pid/{band}", (HttpContext ctx, string id, string band) => CommandAsync(ctx, host, new WriteSpec("pid.set", ActionClass.Control, Role: "admin"),
            (b, c) => cmd.SetPidAsync(c, Channel(id), Int(band, "band", 0, 2),
                Num(b, "p") ?? throw new ApiBadRequestException("缺少 p"), Num(b, "i") ?? throw new ApiBadRequestException("缺少 i"),
                Num(b, "d") ?? throw new ApiBadRequestException("缺少 d"), ctx.RequestAborted)));
        w.MapPost("/channels/{id}/autotune", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("autotune.start", ActionClass.Control),
            (b, c) => cmd.StartAutotuneAsync(c, Channel(id), Num(b, "tempC") ?? throw new ApiBadRequestException("缺少 tempC"), ctx.RequestAborted)));
        w.MapDelete("/channels/{id}/autotune", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("autotune.cancel", ActionClass.Stop),
            (b, c) => cmd.CancelAutotuneAsync(c, Channel(id), ctx.RequestAborted)));

        // —— §7.7 搅拌：{on?, rpm?}；只有 on=false（不带 rpm）才是停止类 ——
        w.MapPut("/stir", H(async (HttpContext ctx) =>
        {
            // 先看正文决定动作类：只有 {on:false}（不带 rpm）是停止类（免租约）；其余为控制类
            var body = ParseBody(await ReadBodyAsync(ctx).ConfigureAwait(false), out _);
            bool? on = null; int? rpm = null;
            try { on = Bool(body, "on"); rpm = Num(body, "rpm") is { } r ? (int)Math.Round(r) : null; } catch (ApiRejectException) { /* 管线里再报 400 */ }
            var cls = on == false && rpm == null ? ActionClass.Stop : ActionClass.Control;
            return await CommandAsync(ctx, host, new WriteSpec("stir.set", cls), (b, c) =>
            {
                bool? on2 = Bool(b, "on");
                int? rpm2 = Num(b, "rpm") is { } r2 ? (int)Math.Round(r2) : null;
                if (on2 == null && rpm2 == null) throw new ApiBadRequestException("on / rpm 至少给一个");
                return cmd.SetStirAsync(c, on2, rpm2, ctx.RequestAborted);
            }).ConfigureAwait(false);
        }));

        // —— §7.8 泄漏测试 ——
        w.MapPost("/leak-tests", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("leakTest.start", ActionClass.Control, IdemRequired: true),
            (b, c) => cmd.StartLeakTestAsync(c, IntList(b, "channels", required: true)!, Num(b, "testPsi"), ctx.RequestAborted))));
        w.MapDelete("/leak-tests/current", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("leakTest.abort", ActionClass.Stop),
            (b, c) => cmd.AbortLeakTestAsync(c, ctx.RequestAborted))));

        // —— §7.9 程序 ——
        w.MapPut("/channels/{id}/program", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("program.replace", ActionClass.Control),
            (b, c) => cmd.ReplaceProgramAsync(c, Channel(id), Steps(b), ctx.RequestAborted)));
        w.MapPost("/channels/{id}/program/run", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("program.run", ActionClass.Control, IdemRequired: true),
            (b, c) =>
            {
                // §10.3：带 sessionId 时必须是当前活动会话（否则 400），避免把运行记到错误的会话
                var sid = Str(b, "sessionId");
                if (sid != null && sid != host.Sources.CurrentSession?.Id)
                    throw new ApiBadRequestException($"sessionId「{sid}」不是当前活动会话（{host.Sources.CurrentSession?.Id ?? "无"}）");
                return cmd.RunProgramAsync(c, Channel(id), ctx.RequestAborted);
            }));
        w.MapDelete("/channels/{id}/program/run", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("program.abort", ActionClass.Stop),
            (b, c) => cmd.AbortProgramAsync(c, Channel(id), ctx.RequestAborted)));
        w.MapPost("/channels/{id}/program/copy", (HttpContext ctx, string id) => CommandAsync(ctx, host, new WriteSpec("program.copy", ActionClass.Control, IdemRequired: true),
            (b, c) => cmd.CopyProgramAsync(c, Channel(id), IntList(b, "to", required: true)!, ctx.RequestAborted)));

        // —— §7.10 报警确认 ——
        w.MapPost("/alarms/{alarmId}/ack", (HttpContext ctx, string alarmId) => CommandAsync(ctx, host, new WriteSpec("alarm.ack", ActionClass.Record),
            (b, c) => cmd.AckAlarmAsync(c, alarmId, ctx.RequestAborted)));
    }

    /// <summary>
    /// 把只带 HttpContext 的处理器固定为 Func&lt;HttpContext, Task&lt;IResult&gt;&gt;：否则 C# 会把 <c>(HttpContext ctx) =&gt; Task&lt;IResult&gt;</c>
    /// 匹配到 <c>RequestDelegate</c> 重载（返回的 IResult 不会被执行，响应成空 200）。
    /// </summary>
    private static Delegate H(Func<HttpContext, Task<IResult>> f) => f;
    private static Delegate H(Func<HttpContext, IResult> f) => f;

    // ===================== 公共管线 =====================

    private const string BodyItemKey = "pr.body";

    /// <summary>读正文（最多一次；Kestrel 已限 64 KB）。缓存在 ctx.Items 里，端点可先 Peek 再进管线。</summary>
    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(BodyItemKey, out var cached) && cached is string s) return s;
        using var r = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        var body = await r.ReadToEndAsync(ctx.RequestAborted).ConfigureAwait(false);
        ctx.Items[BodyItemKey] = body;
        return body;
    }

    private static JsonElement? ParseBody(string body, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { error = "正文必须是 JSON 对象"; return null; }
            return doc.RootElement.Clone();
        }
        catch (JsonException ex) { error = "正文不是合法 JSON：" + ex.Message; return null; }
    }

    private static IResult Text(int status, string body) => Results.Text(body, "application/json", Encoding.UTF8, status);

    /// <summary>审计明细里 note 一类自由文本的上限（§4.2：不让 64 KB 正文整段进事件标题 / manifest）。</summary>
    public const int NoteMax = 200;

    /// <summary>写命令公共管线（见类说明）。run 在参数绑定失败时抛 <see cref="ApiBadRequestException"/>。</summary>
    public static async Task<IResult> CommandAsync(HttpContext ctx, ApiHost host, WriteSpec spec, Func<JsonElement?, RemoteCaller, Task<CommandOutcome>> run)
    {
        var auth = ApiHost.AuthOf(ctx);
        var ctl = host.Control;
        string path = ctx.Request.Path.Value ?? "";
        string method = ctx.Request.Method;
        string key = ctx.Request.Headers[ApiHost.IdempotencyHeader].ToString().Trim();
        string opId = key.Length > 0 ? key : Guid.NewGuid().ToString();
        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "-";
        string leaseHeader = ctx.Request.Headers[ApiHost.LeaseHeader].ToString().Trim();
        string leaseTag = leaseHeader.Length == 0 ? "-" : leaseHeader.Length > 8 ? leaseHeader[..8] + "…" : leaseHeader;
        string clientName = auth.Name;
        IdemEntry? entry = null;
        bool owned = false;   // entry 是否由本次请求新建（只有自己建的才能 Abandon）
        string? raw = null;

        // 0. 来源标签：带了有效租约就用申请时的客户端名（成功与拒绝两条路径记同一个 client）
        if (leaseHeader.Length > 0)
        {
            var pre = ctl.Lease.Validate(leaseHeader, auth.Name, ctl.Now);
            if (pre.Ok) clientName = pre.ClientName;
        }

        // 1. 角色 / 急停权限位（§4.2 / §7.1：停止类动作 operator 或 estop 权限皆可）
        if (spec.Estop)
        {
            if (!auth.Estop) return Reject(OpCodes.ForbiddenRole, "该令牌没有急停权限（estop）");
        }
        else if (!TokenAuth.HasRole(auth, spec.Role) && !(spec.Class == ActionClass.Stop && auth.Estop))
            return Reject(OpCodes.ForbiddenRole, spec.Role == "admin" ? "该操作需要 admin 角色"
                : spec.Class == ActionClass.Stop ? "停止类命令需要 operator 角色或 estop 权限位" : "viewer 令牌只能读取 · 写命令需要 operator 及以上角色");

        // 2. 正文
        raw = await ReadBodyAsync(ctx).ConfigureAwait(false);
        var body = ParseBody(raw, out var bodyErr);
        if (bodyErr != null) return Reject(OpCodes.BadRequest, bodyErr);

        // 3. 幂等（§13.3；表内键按令牌隔离）
        if (key.Length > 0)
        {
            if (key.Length > 128) return Reject(OpCodes.BadRequest, "Idempotency-Key 过长（≤ 128 字符）");
            var hash = IdempotencyStore.Hash(method, path, raw);
            var scoped = IdempotencyStore.ScopedKey(auth.Name, key);
            while (true)
            {
                var begin = host.Idem.Begin(scoped, hash, opId, DateTime.UtcNow, out entry);
                if (begin == IdemBegin.New) { owned = true; break; }
                if (begin == IdemBegin.Mismatch)
                {
                    entry = null;
                    return Reject(OpCodes.IdempotencyMismatch, "同一个 Idempotency-Key 带了不同的请求（方法 / 路径 / 正文）");
                }
                if (begin == IdemBegin.Replay)
                {
                    ctx.Response.Headers["Idempotent-Replay"] = "true";
                    return Text(entry.Status, entry.Body!);
                }
                // InProgress：等原请求完成（≤ 5 s）；原请求撤销了条目（取消）→ 重新登记
                var done = await Task.WhenAny(entry.Completed.Task, Task.Delay(InProgressWait, ctx.RequestAborted)).ConfigureAwait(false);
                if (done != entry.Completed.Task)
                {
                    var inProgressOp = entry.OpId;
                    entry = null;   // 不是自己的条目：不撤
                    return Reject(OpCodes.IdempotencyInProgress, "同键命令仍在执行 · 请稍后用同一 Idempotency-Key 查询", inProgressOp);
                }
                if (entry.Completed.Task.IsCanceled) continue;
                ctx.Response.Headers["Idempotent-Replay"] = "true";
                return Text(entry.Status, entry.Body!);
            }
        }
        else if (spec.IdemRequired)
            return Reject(OpCodes.PreconditionRequired, $"{method} {path} 非幂等 · 必须带 Idempotency-Key（§7.1）");

        try
        {
            // 4. 动作类 → 租约（§7.1）。停止 / 记录类免租约
            if (spec.Class == ActionClass.Control)
            {
                var chk = ctl.Lease.Validate(leaseHeader, auth.Name, ctl.Now);
                if (!chk.Ok) return Reject(chk.Code!, chk.Message!);
                clientName = chk.ClientName;
            }
            var caller = new RemoteCaller(auth.Name, clientName, opId);

            // 5. 参数绑定 + 6. 执行。投递到 UI 线程之后客户端断线不再中断（UiCommandBridge），这里的取消只可能来自投递前
            CommandOutcome outcome;
            try { outcome = await run(body, caller).ConfigureAwait(false); }
            catch (ApiRejectException ex) { return Reject(ex.Code, ex.Message); }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { if (owned && entry != null) host.Idem.Abandon(entry); throw; }
            catch (Exception ex)
            {
                // 命令层 / 适配层异常：原文进审计（err），响应只给通用文字（不把路径 / 栈信息泄给客户端）
                try { ctl.AuditOp("err", $"远程命令 {spec.Action} · 执行异常", Detail(ex.GetBaseException().ToString()), clientName, opId, "op." + spec.Action + ".exception"); } catch { }
                outcome = CommandOutcome.Done(OpResult.Fail(OpCodes.Internal, "命令执行异常 · 详见事件记录（按 opId 查）", "err"));
            }

            // 7. 审计 op.<action>（seq = opEventSeq）→ 响应 → opEvent 推送 → 幂等表
            string kind = "op." + spec.Action;
            var preview = CommandResults.Build(outcome, opId, 0);
            long seq = ctl.AuditOp(preview.Ok ? "info" : "warn", $"远程命令 {spec.Action} · {CommandResults.Summary(preview)}",
                Detail(preview.Message), clientName, opId, kind);
            var http = CommandResults.Build(outcome, opId, seq);
            ctl.PublishOp(new OpEventMsg("opEvent", opId, spec.Action, clientName, "remote", http.Status, http.StatusWord, http.Code, http.Message, seq, DateTime.UtcNow));

            if (outcome.Pending != null)
            {
                // HW_UNCONFIRMED / OUTCOME_UNKNOWN：内层继续跑，完成后记 op.<action>.result（同一 opId）；幂等条目此时才算完成（重放拿到的是最终结果）
                var pending = outcome.Pending;
                var e = entry;
                bool completeLater = e != null;
                string client = clientName;
                _ = pending.ContinueWith(t =>
                {
                    var r = t.IsCompletedSuccessfully ? t.Result : OpResult.InternalOf(t.Exception);
                    var final = CommandResults.FromResult(r, opId, 0);
                    long s2 = ctl.AuditOp(r.Ok ? "info" : "warn", $"远程命令 {spec.Action} · 最终结果 {CommandResults.Summary(final)}",
                        $"{client}（令牌 {auth.Name} · ip {ip} · 租约 {leaseTag}）· {r.Code}{(r.Message.Length > 0 ? " · " + r.Message : "")}", client, opId, kind + ".result");
                    final = CommandResults.FromResult(r, opId, s2);
                    ctl.PublishOp(new OpEventMsg("opEvent", opId, spec.Action + ".result", client, "remote", final.Status, final.StatusWord, final.Code, final.Message, s2, DateTime.UtcNow));
                    if (completeLater) host.Idem.Complete(e!, final.Status, final.Body);
                }, TaskScheduler.Default);
                return Text(http.Status, http.Body);
            }
            if (entry != null) host.Idem.Complete(entry, http.Status, http.Body);
            return Text(http.Status, http.Body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 内部异常：全文进审计，响应只给通用文字 + opId（不把路径 / 栈信息泄给客户端）
            if (owned && entry != null) host.Idem.Abandon(entry);
            try { ctl.AuditOp("err", $"远程命令 {spec.Action} · 管线异常", Detail(ex.GetBaseException().ToString()), clientName, opId, "op." + spec.Action); } catch { }
            var http = CommandResults.Error(opId, OpCodes.Internal, "命令管线内部错误 · 详见事件记录（按 opId 查）", null, 0);
            return Text(http.Status, http.Body);
        }

        // §4.2 审计字段：source=remote（Audit 固定）、client、ip、leaseId（前 8 位）、opId、action（kind）、params（正文节选）、result（标题）
        string Detail(string message)
            => $"{method} {path} · {clientName}（令牌 {auth.Name} · ip {ip} · 租约 {leaseTag}）· {Trunc(raw ?? "")}{(message.Length > 0 ? " · " + message : "")}";

        IResult Reject(string code, string message, string? inProgressOp = null)
        {
            // 管线前段的拒绝（角色 / 幂等 / 租约 / 参数）：不进 UI 线程，也记一条 op.<action> 审计（§7.1：每条远程命令都有记录）
            long seq = 0;
            try
            {
                seq = ctl.AuditOp("warn", $"远程命令 {spec.Action} · {ApiErrors.StatusOf(code)} {code}", Detail(message), clientName, inProgressOp ?? opId, "op." + spec.Action);
            }
            catch { }
            var http = CommandResults.Error(inProgressOp ?? opId, code, message, null, seq);
            // 未执行就被拒（角色 / 租约 / 参数）：不缓存到幂等表，客户端修正后用同一键重发可以正常执行。只撤自己新建的条目
            if (owned && entry != null && !entry.Done) host.Idem.Abandon(entry);
            return Text(http.Status, http.Body);
        }
    }

    private static string Trunc(string s) => s.Length <= 200 ? s.Replace('\n', ' ') : s[..200].Replace('\n', ' ') + "…";

    // ===================== 参数绑定（失败抛 ApiBadRequestException → 400）=====================

    /// <summary>路由里的通道号：1..8，否则 404 CHANNEL_NOT_FOUND（不进 UI 线程）。</summary>
    private static int Channel(string id)
    {
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > ChannelRange.ChannelCount)
            throw new ApiRejectException(OpCodes.ChannelNotFound, $"通道 {id} 不存在（有效范围 1–{ChannelRange.ChannelCount}）");
        return n;
    }

    private static int Int(string s, string name, int min, int max)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
            throw new ApiBadRequestException($"{name} 须为 {min}–{max} 的整数");
        return n;
    }

    private static JsonElement? Prop(JsonElement? b, string name)
        => b is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    private static string Require(JsonElement? b, string name)
        => Prop(b, name) is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()) : throw new ApiBadRequestException($"缺少 {name}");

    private static string? Str(JsonElement? b, string name)
    {
        if (Prop(b, name) is not { } v) return null;
        if (v.ValueKind != JsonValueKind.String) throw new ApiBadRequestException($"{name} 须为字符串");
        return v.GetString();
    }

    /// <summary>自由文本（note 等）：截到 <see cref="NoteMax"/> 字符、去掉换行。</summary>
    private static string? Note(JsonElement? b, string name)
    {
        var v = Str(b, name);
        if (v == null) return null;
        v = v.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return v.Length <= NoteMax ? v : v[..NoteMax];
    }

    private static double? Num(JsonElement? b, string name)
    {
        if (Prop(b, name) is not { } v) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || !double.IsFinite(d)) throw new ApiBadRequestException($"{name} 须为数值");
        return d;
    }

    private static bool? Bool(JsonElement? b, string name)
    {
        if (Prop(b, name) is not { } v) return null;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ApiBadRequestException($"{name} 须为 true / false");
        return v.GetBoolean();
    }

    private static IReadOnlyList<int>? IntList(JsonElement? b, string name, bool required)
    {
        if (Prop(b, name) is not { } v) return required ? throw new ApiBadRequestException($"缺少 {name}（整数数组）") : null;
        if (v.ValueKind != JsonValueKind.Array) throw new ApiBadRequestException($"{name} 须为整数数组");
        var list = new List<int>();
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out var n)) throw new ApiBadRequestException($"{name} 须为整数数组");
            list.Add(n);
        }
        return list;
    }

    /// <summary>PUT /channels/{id}/profile 的 segments[{tempC, minutes}]（enabled 忽略：段数即启用段数）。</summary>
    private static IReadOnlyList<(double TempC, double Minutes)> Segments(JsonElement? b)
    {
        if (Prop(b, "segments") is not { ValueKind: JsonValueKind.Array } arr) throw new ApiBadRequestException("缺少 segments 数组");
        var list = new List<(double, double)>();
        foreach (var s in arr.EnumerateArray())
        {
            var t = Num(s, "tempC") ?? throw new ApiBadRequestException("每段须有 tempC");
            var m = Num(s, "minutes") ?? throw new ApiBadRequestException("每段须有 minutes");
            list.Add((t, m));
        }
        if (list.Count == 0) throw new ApiBadRequestException("segments 不能为空");
        return list;
    }

    /// <summary>PUT /channels/{id}/program 的 steps 数组（§6.9），逐步转换在 HMI 侧（ProgramJson）。</summary>
    private static JsonElement Steps(JsonElement? b)
        => Prop(b, "steps") is { ValueKind: JsonValueKind.Array } arr ? arr : throw new ApiBadRequestException("缺少 steps 数组");
}
