// 通用 HTTP/1.1 + WebSocket 服务器：一个 poll() I/O 线程 + 工作线程池；TCP（IPv4 / IPv6）和 Unix socket 两个入口。
//
// 线程约定（详见 doc/模块报告-net-网络.md）：
//   - I/O 线程：accept、读写、HTTP 解析、网关（白名单 / 鉴权 / 限流）、WS 帧编解码、超时。从不调用业务代码。
//   - 工作线程（ServerOptions::worker_threads 个）：路由处理器、WS 回调（同一连接串行）。可以阻塞（等控制线程回结果），
//     但池子是共享的 —— 长时间阻塞的操作请用 Responder 延迟回复，把线程还回来。
//   - 任意线程：Responder::send / stream、WsSession::send / close、Server::ws_broadcast / update_config / stats。
//   - Router 在 start() 之前登记完，之后不能再改。
#pragma once
#include <atomic>
#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include "net/api_config.hpp"
#include "net/http.hpp"
#include "net/router.hpp"
#include "net/security.hpp"
#include "net/tls.hpp"

namespace reactor::net {

// WS 出站队列（C#: WsHub.Conn 的两个有界队列）
enum class WsQueue {
    Telemetry,   // 容量 50，满了丢最旧（计 dropped_telemetry_frames）
    Event,       // 容量 2000（事件 / 报警 / control / opEvent / hello / pong 都走这里），满了以 1008 断开让客户端重同步
};

struct ServerOptions {
    // —— TCP 入口（PC 端）——
    bool tcp_enabled = true;
    std::string tcp_bind = "0.0.0.0";   // "0.0.0.0" = IPv4 全部；"::" = IPv6 双栈；或具体地址
    int tcp_port = 8080;                // 0 = 系统分配（tcp_port() 取实际值）
    int64_t tcp_bind_retry_ms = 10000;  // 绑定失败后的重试间隔（C#: ApiHost.BindRetry）；0 = 不重试，start() 直接失败
    int max_connections_tcp = 64;       // C#: Kestrel MaxConcurrentConnections
    // 加密：非空 = TCP 入口走 TLS（HTTPS / WSS），每条新连接取 tls->server_config() 的当前证书（重新生成后新连接立刻用新的）。
    // 空 = 明文（api.json "tls": false 给老 PC 端用）。Unix 入口永远明文。
    std::shared_ptr<TlsCertStore> tls;
    int64_t tls_handshake_timeout_ms = 10000;   // 连上后这么久握手还没完成 → 断开

    // —— Unix socket 入口（本机触屏）——
    std::string unix_path;              // 空 = 不开；部署 /run/reactord/hmi.sock
    int unix_mode = 0660;
    int max_connections_unix = 16;

    // —— 处理器 ——
    int worker_threads = 4;

    // —— HTTP ——
    ParseLimits limits;                 // 头 16 KB、正文 64 KB
    int64_t header_timeout_ms = 10000;  // 请求开始后 10 s 内要收齐（防慢速攻击）
    int64_t idle_timeout_ms = 130000;   // keep-alive 空闲（C#: Kestrel KeepAliveTimeout 130 s）
    size_t stream_high_water = 256 * 1024;   // StreamWriter 背压高水位
    int64_t stream_stall_timeout_ms = 10000; // 流式响应 10 s 发不动 → 断开

    // —— WebSocket（C#: WsHub）——
    int ws_max_clients = 4;             // TCP 上的上限（api.json maxClients）；先占位再握手
    int ws_max_clients_local = 4;       // Unix 上单独计数（触屏不和 PC 抢名额）
    size_t ws_max_message = 64 * 1024;  // 入站消息上限，超了 1009 断开
    size_t ws_telemetry_queue = 50;
    size_t ws_event_queue = 2000;
    int64_t ws_send_timeout_ms = 10000; // 单条消息 10 s 发不完 → 1008 断开
    int64_t ws_keepalive_ms = 5000;     // 主动发空 Pong 保活（C#: KeepAliveInterval 5 s）；0 = 不发
    int64_t ws_close_timeout_ms = 2000; // 发出关闭帧后等对方回关闭帧的时间

    bool method_mismatch_as_404 = false; // true：路径存在但方法不对时走兜底 404（逐字兼容 C#：ASP.NET 有 MapFallback 时不回 405）

    GateOptions gate;                   // 前缀、Unix uid 白名单
    AuditFn audit;                      // 审计钩子（I/O 线程调用，必须很快）
};

// 用 api.json 填 TCP 绑定 / 端口 / WS 上限
ServerOptions server_options_from_config(const ApiConfig& cfg);

namespace detail { struct Core; struct ConnAccess; }

// 一条 WebSocket 连接。由服务器创建，通过 WsHandlers 回调交给 api 层；可以长期持有（连接断后 send 返回 false）。
class WsSession : public std::enable_shared_from_this<WsSession> {
public:
    uint64_t id() const { return id_; }
    const AuthContext& auth() const { return auth_; }
    const PeerInfo& peer() const { return peer_; }
    const std::string& path() const { return path_; }

    // 入队一条文本消息（任意线程，不阻塞）。连接已关 / 队列溢出而断开 → false。
    bool send(std::string text, WsQueue q = WsQueue::Event);
    bool send(std::shared_ptr<const std::string> text, WsQueue q);   // 广播时多连接共用同一份序列化结果
    bool send_binary(std::string data);                              // 走 Event 队列
    // 主动关闭：丢弃未发的消息，发关闭帧（任意线程）
    void close(uint16_t code = 1000, const std::string& reason = "");
    bool is_open() const;
    std::string close_reason() const;   // 断开原因（中文，给审计用）

    int64_t dropped_telemetry() const { return dropped_.load(); }
    int64_t frames_sent() const { return frames_.load(); }
    int64_t events_sent() const { return events_.load(); }

    // api 层的附加数据（订阅开关等）：在 on_open 里设好，之后只读；里面可变的字段请自己用原子量 / 锁
    std::shared_ptr<void> user;

private:
    friend struct detail::Core;
    friend struct detail::ConnAccess;
    friend class Server;
    struct Msg { std::shared_ptr<const std::string> data; bool binary = false; };
    void fail_locked(uint16_t code, const std::string& reason);

    uint64_t id_ = 0;
    AuthContext auth_;
    PeerInfo peer_;
    std::string path_;
    size_t tele_cap_ = 50, evt_cap_ = 2000;
    ApiStats* stats_ = nullptr;
    std::function<void()> wake_;

    mutable std::mutex mu_;
    std::deque<Msg> tele_, evt_;
    bool open_ = true;            // 可以入队
    bool close_requested_ = false;
    uint16_t close_code_ = 1000;
    std::string close_reason_;
    std::string close_wire_reason_;
    std::atomic<bool> broadcast_ready_{false};
    std::atomic<int64_t> dropped_{0}, frames_{0}, events_{0};

    // 回调串行队列
    std::mutex cb_mu_;
    std::deque<std::function<void()>> cb_queue_;
    bool cb_running_ = false;
};

class Server {
public:
    Server(ServerOptions opt, const ApiConfig& cfg);
    ~Server();
    Server(const Server&) = delete;
    Server& operator=(const Server&) = delete;

    Router& router();   // start() 之前登记路由
    // 绑定监听并启动线程。Unix 入口失败 → false；TCP 失败且 tcp_bind_retry_ms > 0 → 记审计、后台重试、仍返回 true。
    bool start(std::string* error = nullptr);
    // 关闭全部连接（WS 回调 on_close 照常调用）、停线程、删除 Unix socket 文件。可重复调用；不要在工作线程 / I/O 线程里调。
    void stop();

    int tcp_port() const;                 // 实际监听端口（未监听为 0）
    ApiStats& stats();
    Gate& gate();
    // 热替换令牌 / 白名单 / 限流 / WS 上限；已吊销令牌的 WS 连接以 1008 断开（C# 设计稿 §4.2「吊销」）
    void update_config(const ApiConfig& cfg);

    // 广播：同一份消息投给所有已就绪（on_open 已返回）的连接；filter 返回 false 的跳过。返回投递数。任意线程。
    size_t ws_broadcast(std::shared_ptr<const std::string> msg, WsQueue q,
                        const std::function<bool(const WsSession&)>& filter = {});
    size_t ws_broadcast(const std::string& msg, WsQueue q, const std::function<bool(const WsSession&)>& filter = {}) {
        return ws_broadcast(std::make_shared<const std::string>(msg), q, filter);
    }
    std::vector<WsSessionPtr> ws_sessions() const;
    // 把任务丢进工作线程池（api 层做异步工作用）。服务器停止后丢弃。
    void submit(std::function<void()> fn);

private:
    std::shared_ptr<detail::Core> core_;
};

}  // namespace reactor::net
