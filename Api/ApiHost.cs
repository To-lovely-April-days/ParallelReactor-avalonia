using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParallelReactor.Commands;

namespace ParallelReactor.Api;

/// <summary>
/// 内嵌 Kestrel（设计稿 §3.2）：minimal API + WebSocket，在后台线程运行，不直接碰 VM：读走 <see cref="IApiSources"/> 的只读快照 / 事件，
/// 写（P3）走 <see cref="IRemoteCommands"/>（HMI 实现经 UiCommandBridge 进 UI 线程），控制权走 <see cref="ControlService"/>（纯逻辑租约状态机）。
/// 管线：白名单（403 IP_NOT_ALLOWED）→ /health 免鉴权 → Bearer（401 UNAUTHORIZED；同一 IP 每秒失败超过 <see cref="AuthFailuresPerSec"/> 次后改回 429，
/// 有效令牌不受影响；鉴权失败审计按 IP 每分钟最多一条）→ 限流（50 次/秒/令牌；下载并发 2；/preflight 每令牌 2 次/秒；写命令 writeRateLimitPerSec 次/秒/令牌，/estop 不限）。
/// 绑定失败记事件 <c>system.apiBindFailed</c>（首次之后每 10 分钟最多一条）并每 10 s 重试；<c>enabled=false</c> 时 <see cref="TryStart"/> 返回 null，Kestrel 完全不构造。
/// 只有 HTTP：<c>httpsPort / certPath</c> 在 P2 不生效（配置了会记一条 warn 事件），令牌明文传输，依赖 §4.1 的网段隔离。
/// </summary>
public sealed class ApiHost
{
    public static readonly TimeSpan BindRetry = TimeSpan.FromSeconds(10);
    public const string AuthItemKey = "pr.auth";
    public const string DownloadsPolicy = "downloads";
    public const string PreflightPolicy = "preflight";
    public const string WritesPolicy = "writes";
    /// <summary>/estop 自己的限流分区：每令牌 <see cref="EstopRateLimitPerSec"/> 次/秒、不排队、不与写命令共用（§7.3「不限流」的实际含义是不受写限流影响）。</summary>
    public const string EstopPolicy = "estop";
    /// <summary>/control/heartbeat 自己的分区：不与写命令共用（写命令刷满配额时心跳不能被 429 掉）。</summary>
    public const string HeartbeatPolicy = "heartbeat";
    public const int EstopRateLimitPerSec = 5;
    public const int HeartbeatRateLimitPerSec = 10;
    public const string LeaseHeader = "X-Control-Lease";
    public const string IdempotencyHeader = "Idempotency-Key";
    /// <summary>GET /control/requests/{id} 用的申请凭据（POST /control/request 响应 result.requestKey）：带它才返回 leaseId（§5.3）。</summary>
    public const string RequestKeyHeader = "X-Control-Request-Key";
    public const int ReadRateLimitPerSec = 50;
    public const int PreflightRateLimitPerSec = 2;
    public const int DownloadConcurrency = 2;
    public const int AuthFailuresPerSec = 10;

    private readonly IApiSources _src;
    private readonly CidrMatcher _cidr;
    private readonly KeyedThrottle _authThrottle = new(TimeSpan.FromMinutes(1));
    private readonly FailureLimiter _authFailLimiter = new(AuthFailuresPerSec, TimeSpan.FromSeconds(1));
    private readonly KeyedThrottle _bindThrottle = new(TimeSpan.FromMinutes(10));
    private readonly CancellationTokenSource _cts = new();
    private readonly object _appLock = new();
    private WebApplication? _app;
    private int _stopped;
    private int _bindFailures;

    public ApiConfig Config { get; }
    public ApiStats Stats { get; } = new();
    public WsHub Hub { get; }
    public IApiSources Sources => _src;
    /// <summary>控制权（租约 / 心跳 / 申请；P3）。</summary>
    public ControlService Control { get; }
    /// <summary>写命令幂等表（§13.3）。</summary>
    public IdempotencyStore Idem { get; } = new();

    /// <summary>实际监听的端口（port=0 时为系统分配的端口；未启动为 0）。</summary>
    public int Port => Stats.Port;
    public string BaseUrl => $"http://{(Config.Bind is "0.0.0.0" or "::" or "[::]" ? "127.0.0.1" : Config.Bind)}:{Port}";

    public ApiHost(IApiSources src, ApiConfig cfg)
    {
        _src = src;
        Config = cfg;
        _cidr = new CidrMatcher(cfg.AllowedCidrs);
        Hub = new WsHub(src, cfg, Stats);
        Control = new ControlService(src, cfg, Hub);
    }

    /// <summary>
    /// App 启动时调用：未启用返回 null（不构造 Kestrel）；启用则在后台启动，绑定失败每 10 s 重试，直到 <see cref="Stop"/>。
    /// </summary>
    public static ApiHost? TryStart(IApiSources src, ApiConfig cfg, string? configError)
    {
        if (configError != null) src.Audit("err", "接口配置读取失败 · 按默认（未启用）", configError);
        if (!cfg.Enabled) return null;
        var host = new ApiHost(src, cfg);
        if (host._cidr.Invalid.Count > 0)
            src.Audit("warn", "接口白名单含无效条目 · 已忽略", string.Join("、", host._cidr.Invalid));
        if (cfg.Tokens.Count == 0)
            src.Audit("warn", "接口未配置任何令牌 · 除 /health 外所有请求都会返回 401",
                $"请在 {cfg.SourceFileName} 的 tokens 里加入令牌的 SHA-256{(cfg.FileFound ? "" : "（该文件不存在，当前为 PR_API=1 强制启用）")}");
        if (cfg.HttpsPort > 0 || !string.IsNullOrWhiteSpace(cfg.CertPath))
            src.Audit("warn", "接口 HTTPS 未实现 · 已忽略 httpsPort / certPath", "P2 只提供 HTTP（令牌明文传输），依赖网段隔离与白名单；HTTPS 随 P5");
        foreach (var w in cfg.Warnings) src.Audit("warn", "接口配置已修正", $"{cfg.SourceFileName}：{w}");
        if (cfg.AutoGrantRemote)
            src.Audit("warn", "远程控制申请免触屏确认（autoGrantRemote）", "任何 operator 令牌申请即获控制权；只用于无人值守 / 验收，现场请关闭");
        _ = host.RunWithRetryAsync();
        return host;
    }

    private async Task RunWithRetryAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            if (await StartOnceAsync(ct).ConfigureAwait(false)) return;
            try { await Task.Delay(BindRetry, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>构造并启动一次 Kestrel；绑定失败返回 false（已记事件，可重试）。测试直接调用它。</summary>
    public async Task<bool> StartOnceAsync(CancellationToken ct = default)
    {
        WebApplication? app = null;
        try
        {
            app = Build();
            await app.StartAsync(ct).ConfigureAwait(false);
            var addr = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            int port = Config.HttpPort;
            if (addr != null && Uri.TryCreate(addr.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost"), UriKind.Absolute, out var u)) port = u.Port;
            // Stop 可能在 StartAsync 期间到来（绑定重试循环里操作员退出）：锁内核对，已停止就把刚起来的实例收掉，不让它逃出 Stop
            bool stoppedMeanwhile;
            lock (_appLock)
            {
                stoppedMeanwhile = Volatile.Read(ref _stopped) != 0;
                if (!stoppedMeanwhile) _app = app;
            }
            if (stoppedMeanwhile)
            {
                try { await app.StopAsync().ConfigureAwait(false); } catch { }
                await DisposeQuietly(app).ConfigureAwait(false);
                return true;   // 不再重试
            }
            Stats.Port = port;
            Stats.Bind = Config.Bind;
            Stats.StartedAt = DateTime.UtcNow;
            _src.Audit("info", "接口已启动", $"http://{Config.Bind}:{port}/api/v1 · 白名单 {(_cidr.Count == 0 ? "仅本机" : string.Join(" ", Config.AllowedCidrs))} · 令牌 {Config.Tokens.Count} 枚 · WS 上限 {Config.MaxClients} · 配置 {Config.SourceFileName}{(Config.FileFound ? "" : "（不存在，用默认值）")}{(_bindFailures > 0 ? $" · 此前绑定失败 {_bindFailures} 次" : "")}");
            return true;
        }
        catch (OperationCanceledException) { if (app != null) await DisposeQuietly(app).ConfigureAwait(false); return false; }
        catch (Exception ex)
        {
            if (app != null) await DisposeQuietly(app).ConfigureAwait(false);
            int n = Interlocked.Increment(ref _bindFailures);
            // 端口被长期占用时每 10 s 一条会刷满事件文件与重同步环：首次记一条，之后每 10 分钟最多一条（带累计次数）
            if (_bindThrottle.Allow("bind", DateTime.UtcNow))
                _src.Audit("err", "接口绑定失败 · 10 秒后重试", $"{Config.Bind}:{Config.HttpPort} · {ex.GetBaseException().Message} · 累计失败 {n} 次（同类事件每 10 分钟最多一条，重试照常进行）");
            return false;
        }
    }

    private static async Task DisposeQuietly(WebApplication app)
    {
        try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    private WebApplication Build()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "ParallelReactor.Api", EnvironmentName = "Production" });
        // 不往 stdout 写框架日志（systemd 下会刷 journal）：Warning 以上转成「接口」事件
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddProvider(new AuditLoggerProvider(_src));
        builder.WebHost.UseUrls();   // 不受 ASPNETCORE_URLS 影响，只听下面显式配置的地址
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.Limits.MaxConcurrentConnections = 64;
            var ip = Config.Bind switch
            {
                "0.0.0.0" or "" => IPAddress.Any,
                "::" or "[::]" => IPAddress.IPv6Any,
                _ => IPAddress.TryParse(Config.Bind, out var a) ? a : IPAddress.Any,
            };
            k.Listen(ip, Config.HttpPort);
        });
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
        // 进程生命周期归 HMI（App / ConfirmExit / systemd SIGTERM → ProcessExit）：换掉默认的 ConsoleLifetime，
        // 否则它会接管 SIGTERM 并在 ProcessExit 里等待一个永远不会由我们触发的「主机停止」，进程挂住不退出。
        builder.Services.AddSingleton<IHostLifetime, PassiveLifetime>();
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            {
                if (ctx.Request.Path.Equals("/api/v1/health", StringComparison.OrdinalIgnoreCase)) return RateLimitPartition.GetNoLimiter("health");
                string key = ctx.Items.TryGetValue(AuthItemKey, out var a) && a is AuthResult ar ? "tok:" + ar.Name : "ip:" + ctx.Connection.RemoteIpAddress;
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = ReadRateLimitPerSec, Window = TimeSpan.FromSeconds(1), QueueLimit = 0, AutoReplenishment = true,
                });
            });
            o.AddConcurrencyLimiter(DownloadsPolicy, l => { l.PermitLimit = DownloadConcurrency; l.QueueLimit = 0; });
            // /preflight 要进 UI 线程并重算预运行检查：单独压到每令牌 2 次/秒，远程轮询不能成为 UI 线程抖动源
            o.AddPolicy(PreflightPolicy, ctx =>
            {
                string key = ctx.Items.TryGetValue(AuthItemKey, out var a) && a is AuthResult ar ? "tok:" + ar.Name : "ip:" + ctx.Connection.RemoteIpAddress;
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PreflightRateLimitPerSec, Window = TimeSpan.FromSeconds(1), QueueLimit = 0, AutoReplenishment = true,
                });
            });
            // 写命令（P3）：每令牌 writeRateLimitPerSec 次/秒（默认 10）；/estop 不限流（§7.3）
            o.AddPolicy(WritesPolicy, ctx =>
            {
                string key = ctx.Items.TryGetValue(AuthItemKey, out var a) && a is AuthResult ar ? "tok:" + ar.Name : "ip:" + ctx.Connection.RemoteIpAddress;
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Config.WriteRateLimitPerSec, Window = TimeSpan.FromSeconds(1), QueueLimit = 0, AutoReplenishment = true,
                });
            });
            o.AddPolicy(EstopPolicy, ctx =>
            {
                string key = ctx.Items.TryGetValue(AuthItemKey, out var a) && a is AuthResult ar ? "tok:" + ar.Name : "ip:" + ctx.Connection.RemoteIpAddress;
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = EstopRateLimitPerSec, Window = TimeSpan.FromSeconds(1), QueueLimit = 0, AutoReplenishment = true,
                });
            });
            o.AddPolicy(HeartbeatPolicy, ctx =>
            {
                string key = ctx.Items.TryGetValue(AuthItemKey, out var a) && a is AuthResult ar ? "tok:" + ar.Name : "ip:" + ctx.Connection.RemoteIpAddress;
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = HeartbeatRateLimitPerSec, Window = TimeSpan.FromSeconds(1), QueueLimit = 0, AutoReplenishment = true,
                });
            });
            o.OnRejected = async (rc, ct) =>
            {
                string? policy = rc.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                bool dl = policy == DownloadsPolicy;
                await ApiErrors.WriteAsync(rc.HttpContext.Response,
                    dl ? OpCodes.DownloadBusy : OpCodes.RateLimited,
                    dl ? $"同时最多 {DownloadConcurrency} 个下载流"
                    : policy == PreflightPolicy ? $"/preflight 限 {PreflightRateLimitPerSec} 次/秒（要进 UI 线程）"
                    : policy == WritesPolicy ? $"写命令限 {Config.WriteRateLimitPerSec} 次/秒/令牌"
                    : policy == EstopPolicy ? $"/estop 限 {EstopRateLimitPerSec} 次/秒/令牌（单独分区，不与写命令共用）"
                    : policy == HeartbeatPolicy ? $"/control/heartbeat 限 {HeartbeatRateLimitPerSec} 次/秒/令牌（单独分区）"
                    : $"只读请求限 {ReadRateLimitPerSec} 次/秒", null, ct).ConfigureAwait(false);
            };
        });

        var app = builder.Build();
        app.Use(GateAsync);
        app.UseRateLimiter();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5) });
        Endpoints.Map(app.MapGroup("/api/v1"), this);
        return app;
    }

    /// <summary>白名单 + 鉴权中间件（/health 免鉴权但仍受白名单约束）。</summary>
    private async Task GateAsync(HttpContext ctx, RequestDelegate next)
    {
        Stats.Request();
        var ip = ctx.Connection.RemoteIpAddress;
        if (!_cidr.IsAllowed(ip))
        {
            ctx.Response.Headers.Connection = "close";
            await ApiErrors.WriteAsync(ctx.Response, OpCodes.IpNotAllowed, $"{ip} 不在允许的网段内", null, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/api/v1"))
        {
            await ApiErrors.WriteAsync(ctx.Response, OpCodes.NotFound, "接口前缀为 /api/v1", null, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (path.Equals("/api/v1/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx).ConfigureAwait(false);
            return;
        }
        if (ctx.Request.Query.ContainsKey("access_token"))
        {
            await ApiErrors.WriteAsync(ctx.Response, OpCodes.Unauthorized, "不支持 ?access_token=，请用 Authorization: Bearer 头（§4.2）", null, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }
        string ipKey = ip?.ToString() ?? "-";
        var now = DateTime.UtcNow;
        var auth = TokenAuth.Authenticate(ctx.Request.Headers.Authorization, Config);
        if (!auth.Ok)
        {
            Stats.AuthFailed();
            // 鉴权失败限速（§4.4）：未通过鉴权的请求不进后面按令牌分区的限流器，这里是唯一的闸。同一 IP 每秒失败超过上限 → 429。
            // 先校验再判限速：同一 NAT 后面的合法客户端不会因为别人猜令牌而被挡（一次 SHA-256 的开销可以忽略）
            if (_authFailLimiter.IsBlocked(ipKey, now))
            {
                ctx.Response.Headers.RetryAfter = "1";
                await ApiErrors.WriteAsync(ctx.Response, OpCodes.RateLimited, $"鉴权失败过多：同一地址每秒最多 {AuthFailuresPerSec} 次", null, ctx.RequestAborted).ConfigureAwait(false);
                return;
            }
            _authFailLimiter.RecordFailure(ipKey, now);
            if (_authThrottle.Allow(ipKey, now))
                _src.Audit("warn", "接口鉴权失败", $"{ipKey} {ctx.Request.Method} {path}{(string.IsNullOrEmpty(ctx.Request.Headers.Authorization) ? " · 未带令牌" : " · 令牌无效")}（同一地址每分钟只记一条）");
            ctx.Response.Headers.WWWAuthenticate = "Bearer";
            await ApiErrors.WriteAsync(ctx.Response, OpCodes.Unauthorized, "缺少或无效的 Bearer 令牌", null, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }
        ctx.Items[AuthItemKey] = auth;
        await next(ctx).ConfigureAwait(false);
    }

    /// <summary>当前请求的鉴权结果（中间件放入）。</summary>
    public static AuthResult AuthOf(HttpContext ctx)
        => ctx.Items.TryGetValue(AuthItemKey, out var a) && a is AuthResult r ? r : AuthResult.Denied;

    /// <summary>停止：关 WebSocket、停 Kestrel（最多等 timeout），记事件。可重复调用。</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _cts.Cancel();
        var t = timeout ?? TimeSpan.FromSeconds(2);
        try { Hub.Dispose(); } catch { }
        WebApplication? app;
        lock (_appLock) { app = _app; _app = null; }   // 与 StartOnceAsync 的赋值互斥：之后再起来的实例自己收掉
        if (app != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(t);
                await app.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch { }
            await DisposeQuietly(app).ConfigureAwait(false);
        }
        try { _src.Audit("info", "接口已停止", $"端口 {Stats.Port} · 累计请求 {Stats.Requests} · 推送帧 {Stats.FramesSent} · 丢帧 {Stats.DroppedTelemetryFrames} · 丢事件 {Stats.DroppedEvents}"); } catch { }
    }

    /// <summary>
    /// 同步停止（退出路径；最多阻塞 timeout）。在 UI 线程上调用会把 Tick 冻住至多 timeout：退出路径上加热 / 搅拌已先停掉，可接受；
    /// 不会死锁（内部全部 ConfigureAwait(false)，挂着的 /preflight 由 Kestrel 的 2 s ShutdownTimeout 收掉）。
    /// </summary>
    public void Stop(TimeSpan? timeout = null)
    {
        try { StopAsync(timeout).Wait(timeout ?? TimeSpan.FromSeconds(2)); } catch { }
    }

    /// <summary>不挂任何进程信号 / 控制台处理：启动与停止完全由 <see cref="ApiHost"/> 显式调用。</summary>
    private sealed class PassiveLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>把 ASP.NET Core 的 Warning+ 日志转成「接口」事件（去掉 stdout 噪音）。</summary>
    private sealed class AuditLoggerProvider : ILoggerProvider
    {
        private readonly IApiSources _src;
        private readonly KeyedThrottle _throttle = new(TimeSpan.FromMinutes(1));
        public AuditLoggerProvider(IApiSources src) => _src = src;
        public ILogger CreateLogger(string categoryName) => new AuditLogger(this, categoryName);
        public void Dispose() { }

        private sealed class AuditLogger : ILogger
        {
            private readonly AuditLoggerProvider _p;
            private readonly string _cat;
            public AuditLogger(AuditLoggerProvider p, string cat) { _p = p; _cat = cat; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                // 客户端断连之类的噩梦级刷屏：同一类别每分钟最多一条
                if (!_p._throttle.Allow(_cat + eventId.Id.ToString(CultureInfo.InvariantCulture), DateTime.UtcNow)) return;
                try { _p._src.Audit(logLevel >= LogLevel.Error ? "err" : "warn", "接口框架日志", $"{_cat} · {formatter(state, exception)}{(exception != null ? " · " + exception.GetBaseException().Message : "")}"); }
                catch { }
            }
        }
    }
}
