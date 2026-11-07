using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ParallelReactor.Commands;

namespace ParallelReactor.Api;

/// <summary>
/// 控制权端点（设计稿 §5.3 / §7.2；P3）。全部不进 UI 线程（租约是纯逻辑状态机）；申请的「允许 / 拒绝」由触屏经 OperatorCommands.TryGrantControl / TryDenyControl 完成，
/// autoGrantRemote 时在申请当场授权。响应沿用写命令的 <c>{ ok, opId, status, result }</c> 外层。
/// </summary>
public static partial class Endpoints
{
    private static void MapControl(RouteGroupBuilder g, ApiHost host)
    {
        var ctl = host.Control;
        var cfg = host.Config;

        g.MapGet("/control", () => ApiErrors.Ok(ctl.State()));

        // POST /control/request {clientName}：本地模式 → 登记申请（202 pending；autoGrant → 202 granted 带 leaseId）；需要 operator 角色与 Idempotency-Key
        g.MapPost("/control/request", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("control.request", ActionClass.Record, IdemRequired: true),
            (b, c) =>
            {
                var client = Str(b, "clientName") ?? Str(b, "client") ?? c.TokenName;
                if (client.Length > ControlLease.ClientNameMax) throw new ApiBadRequestException($"clientName 过长（≤ {ControlLease.ClientNameMax} 字符）");
                var r = ctl.Lease.Request(c.TokenName, client, ctl.Now, cfg.AutoGrantRemote);
                if (!r.Ok) return Task.FromResult(CommandOutcome.Done(OpResult.Fail(r.Code!, r.Message!)));
                var dto = ctl.ToDto(r);
                return Task.FromResult(CommandOutcome.Done(OpResult.Accepted(r.Lease != null ? "已授权远程控制" : "申请已登记 · 等待触屏确认", "ok", dto)));
            }))).RequireRateLimiting(ApiHost.WritesPolicy);

        // GET /control/requests/{id}：申请方轮询；leaseId 只对申请令牌且带申请凭据头 X-Control-Request-Key（POST /control/request 响应里的 requestKey）给出
        g.MapGet("/control/requests/{requestId}", (HttpContext ctx, string requestId) =>
        {
            var auth = ApiHost.AuthOf(ctx);
            var s = ctl.Lease.QueryRequest(requestId, auth.Name, ctx.Request.Headers[ApiHost.RequestKeyHeader].ToString().Trim(), ctl.Now);
            return s == null ? ApiErrors.Result(OpCodes.NotFound, "没有这个申请（或已过期被清理）") : ApiErrors.Ok(ctl.ToDto(s));
        });

        // POST /control/heartbeat（带 X-Control-Lease）：续租；宽限期内恢复为 Remote
        g.MapPost("/control/heartbeat", H((HttpContext ctx) =>
        {
            var auth = ApiHost.AuthOf(ctx);
            if (!TokenAuth.HasRole(auth, "operator")) return ApiErrors.Result(OpCodes.ForbiddenRole, "心跳需要 operator 及以上角色");
            var hb = ctl.Lease.Heartbeat(ctx.Request.Headers[ApiHost.LeaseHeader].ToString(), auth.Name, ctl.Now);
            if (!hb.Ok) return ApiErrors.Result(hb.Code!, hb.Message!);
            return Results.Text(ApiJson.Serialize(new CommandResponseDto(true, Guid.NewGuid().ToString(), CommandResults.Confirmed,
                ApiJson.ToElement(ctl.ToDto(hb)), Array.Empty<string>(), 0)), "application/json", Encoding.UTF8, 200);
        })).RequireRateLimiting(ApiHost.HeartbeatPolicy);

        // POST /control/release（带 X-Control-Lease）：交还控制权
        g.MapPost("/control/release", H((HttpContext ctx) => CommandAsync(ctx, host, new WriteSpec("control.release", ActionClass.Record),
            (b, c) =>
            {
                var r = ctl.Lease.Release(ctx.Request.Headers[ApiHost.LeaseHeader].ToString(), c.TokenName, ctl.Now);
                return Task.FromResult(CommandOutcome.Done(r.Ok
                    ? OpResult.Done("已交还控制权 · 回到本地", "ok", new { mode = "local" })
                    : OpResult.Fail(r.Code!, r.Message!)));
            }))).RequireRateLimiting(ApiHost.WritesPolicy);
    }
}
