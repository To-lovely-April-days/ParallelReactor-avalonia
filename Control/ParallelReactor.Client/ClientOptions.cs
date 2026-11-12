using System;
using System.Threading;
using ParallelReactor.Api;

namespace ParallelReactor.Client;

/// <summary>客户端配置（地址、令牌、客户端名、各类超时）。构造后只读；UI 层按连接对话框的输入新建一份。</summary>
public sealed class ClientOptions
{
    public ClientOptions(Uri baseUrl, string token, string clientName)
    {
        if (baseUrl == null) throw new ArgumentNullException(nameof(baseUrl));
        if (!baseUrl.IsAbsoluteUri) throw new ArgumentException("baseUrl 必须是绝对地址，如 http://192.168.10.20:8080", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token 不能为空", nameof(token));
        if (string.IsNullOrWhiteSpace(clientName)) throw new ArgumentException("clientName 不能为空", nameof(clientName));
        BaseUrl = baseUrl;
        Token = token.Trim();
        ClientName = ClientNames.Sanitize(clientName);   // 与 HMI 同一规则（否则 GET /control 的 holder 与本地名不等，重连后会误判「不是我」）
        if (ClientName.Length == 0) throw new ArgumentException("clientName 清洗后为空", nameof(clientName));
    }

    /// <summary>服务端根地址（http(s)://host:port，不含 /api/v1）。</summary>
    public Uri BaseUrl { get; }
    /// <summary>Bearer 令牌（明文；不落盘）。</summary>
    public string Token { get; }
    /// <summary>申请控制权时报给触屏的客户端名（§5.3）。</summary>
    public string ClientName { get; }
    /// <summary>X-Client-Version。</summary>
    public string ClientVersion { get; init; } = "pc-1.0";

    /// <summary>单个 HTTP 请求超时。</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>申请 pending 时 GET /control/requests/{id} 的轮询间隔。</summary>
    public TimeSpan RequestPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>申请等待触屏确认的本地上限（服务端 controlRequestTimeoutSec 默认 60，再加余量）。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(70);
    /// <summary>心跳间隔上限（实际 = min(heartbeatTimeoutSec / 2, 此值)，下限 0.5 s）。</summary>
    public TimeSpan MaxHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(4);
    /// <summary>租约丢失（expired / 心跳被拒）后自动用同令牌 + 同 clientName 重申请（§14.2 宽限期内重申请即拿新租约）。</summary>
    public bool AutoReacquireLease { get; init; } = true;

    /// <summary>WS 保活 ping 间隔与 pong 超时。</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PongTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>WS 重连退避：起点 / 上限（指数，±20% 抖动）。</summary>
    public TimeSpan ReconnectMin { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ReconnectMax { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>首次连接时是否从 seq 0 开始回放事件（默认只从 hello.eventSeqHead 之后开始）。</summary>
    public bool ReplayEventsFromStart { get; init; }

    /// <summary>写命令重试（网络异常 / 504 OUTCOME_UNKNOWN / 409 IDEMPOTENCY_IN_PROGRESS：同键重发）次数与间隔。</summary>
    public int WriteRetries { get; init; } = 5;
    public TimeSpan WriteRetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>503 STARTING / UI_THREAD_TIMEOUT 的重试次数。</summary>
    public int BusyRetries { get; init; } = 3;
    /// <summary>写命令之间的最小间隔（服务端 writeRateLimitPerSec 默认 10 → 100 ms 一条；60 ms 留给并发客户端）。</summary>
    public TimeSpan MinWriteGap { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>幂等键工厂（默认 Guid.N）。</summary>
    public Func<string> IdempotencyKeyFactory { get; init; } = IdempotencyKeys.New;

    /// <summary>非空时 <see cref="ReactorConnection"/> 的全部事件经它 Post 到 UI 线程；为 null 直接在线程池回调（引擎 / 单测）。</summary>
    public SynchronizationContext? UiContext { get; init; }

    private readonly int _messageQueueCapacity = 2000;
    /// <summary>
    /// <see cref="ReactorConnection.Messages"/> 队列的容量（M3 增补）：默认 2000，满了丢最旧；<b>0 = 不入队</b>（<see cref="ReactorConnection.Messages"/> 一直是空的，
    /// 直到连接停止时完成）。目前没有代码读这个队列（界面与性能测试引擎都订阅事件，见 PerfTest 的 TelemetryFeedAdapter），
    /// 多台设备同时连接时设 0 省掉每台最多 2000 条消息的内存；以后要按顺序消费队列的连接必须 &gt; 0。不能为负。
    /// </summary>
    public int MessageQueueCapacity
    {
        get => _messageQueueCapacity;
        init => _messageQueueCapacity = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(MessageQueueCapacity), value, "队列容量不能为负（0 = 不入队）");
    }

    /// <summary>REST 根（以 / 结尾，便于相对路径拼接）。</summary>
    public Uri ApiBase => new(BaseUrl, ApiHeaders.Prefix.TrimStart('/') + "/");

    /// <summary>WebSocket 地址（http→ws、https→wss）。</summary>
    public Uri WsUri
    {
        get
        {
            var b = new UriBuilder(BaseUrl) { Path = ApiHeaders.WsPath, Query = "" };
            b.Scheme = BaseUrl.Scheme == "https" ? "wss" : "ws";
            return b.Uri;
        }
    }
}
