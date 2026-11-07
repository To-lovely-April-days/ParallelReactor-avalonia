namespace ParallelReactor.Api;

/// <summary>接口的自定义 HTTP 头与前缀（设计稿 §7.1 / §13；HMI 的 ApiHost 与 PC 客户端共用常量）。</summary>
public static class ApiHeaders
{
    /// <summary>受控写命令必带：当前租约 id。</summary>
    public const string Lease = "X-Control-Lease";
    /// <summary>幂等键（§13）：/run、program/run、program/copy、/leak-tests、/sessions、markers、copy-config、/control/request 必带。</summary>
    public const string Idempotency = "Idempotency-Key";
    /// <summary>GET /control/requests/{id} 带它才返回 leaseId（POST /control/request 响应里的 requestKey）。</summary>
    public const string RequestKey = "X-Control-Request-Key";
    /// <summary>响应头：同键重放时为 "true"。</summary>
    public const string IdempotentReplay = "Idempotent-Replay";
    /// <summary>客户端自报版本（可选）。</summary>
    public const string ClientVersion = "X-Client-Version";
    /// <summary>REST / WS 路径前缀。</summary>
    public const string Prefix = "/api/v1";
    /// <summary>WebSocket 路径。</summary>
    public const string WsPath = "/api/v1/ws";
}
