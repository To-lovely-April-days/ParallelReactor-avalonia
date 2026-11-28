#include "net/server.hpp"

#include <arpa/inet.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/un.h>
#include <unistd.h>

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <thread>
#include <unordered_map>

#include "common/clock.hpp"
#include "net/api_errors.hpp"
#include "net/websocket.hpp"
#include <atomic>
#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace reactor::net {

ServerOptions server_options_from_config(const ApiConfig& cfg) {
    ServerOptions o;
    o.tcp_bind = cfg.bind;
    o.tcp_port = cfg.http_port;
    o.ws_max_clients = cfg.max_clients > 0 ? cfg.max_clients : 4;
    return o;
}

namespace detail {

#ifdef MSG_NOSIGNAL
constexpr int kSendFlags = MSG_NOSIGNAL;   // Linux：写已断开的 socket 不发 SIGPIPE
#else
constexpr int kSendFlags = 0;              // macOS：每个 socket 设 SO_NOSIGPIPE
#endif

void set_nonblock_cloexec(int fd) {
    int fl = fcntl(fd, F_GETFL, 0);
    if (fl >= 0) fcntl(fd, F_SETFL, fl | O_NONBLOCK);
    int fdfl = fcntl(fd, F_GETFD, 0);
    if (fdfl >= 0) fcntl(fd, F_SETFD, fdfl | FD_CLOEXEC);
}

void set_nosigpipe(int fd) {
#ifdef SO_NOSIGPIPE
    int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
#else
    (void)fd;
#endif
}

// ———————————————————————————— 线程池 ————————————————————————————

class ThreadPool {
public:
    void start(int n) {
        std::lock_guard<std::mutex> lk(mu_);
        stopping_ = false;
        for (int i = 0; i < std::max(1, n); ++i) threads_.emplace_back([this] { run(); });
    }
    bool submit(std::function<void()> fn) {
        {
            std::lock_guard<std::mutex> lk(mu_);
            if (threads_.empty() || stopping_done_) return false;
            q_.push_back(std::move(fn));
        }
        cv_.notify_one();
        return true;
    }
    // 跑完队列里剩下的任务（含跑的过程中新提交的）再退出
    void stop() {
        {
            std::lock_guard<std::mutex> lk(mu_);
            stopping_ = true;
        }
        cv_.notify_all();
        for (auto& t : threads_)
            if (t.joinable()) t.join();
        std::deque<std::function<void()>> rest;
        {
            std::lock_guard<std::mutex> lk(mu_);
            threads_.clear();
            stopping_done_ = true;
            rest.swap(q_);
        }
        // 剩下的（理论上没有）在锁外销毁：析构里可能回调 post
    }

private:
    void run() {
        while (true) {
            std::function<void()> fn;
            {
                std::unique_lock<std::mutex> lk(mu_);
                cv_.wait(lk, [&] { return stopping_ || !q_.empty(); });
                if (q_.empty()) return;
                fn = std::move(q_.front());
                q_.pop_front();
            }
            try { fn(); } catch (...) {}
            fn = nullptr;
        }
    }
    std::mutex mu_;
    std::condition_variable cv_;
    std::deque<std::function<void()>> q_;
    std::vector<std::thread> threads_;
    bool stopping_ = false;
    bool stopping_done_ = false;
};

// ———————————————————————————— 回复状态 ————————————————————————————

struct Exchange {
    std::weak_ptr<Core> core;
    uint64_t conn_id = 0, gen = 0;
    bool head = false;
    int version_minor = 1;
    std::atomic<bool> responded{false};
    std::atomic<bool> conn_closed{false};
    ~Exchange();
};

struct StreamState {
    std::weak_ptr<Core> core;
    std::mutex mu;
    std::condition_variable cv;
    std::string pending;
    bool chunked = true;
    int64_t declared = -1;
    int64_t written = 0;
    bool ended = false, aborted = false, conn_closed = false;
    int64_t last_progress_ms = 0;
    size_t high_water = 256 * 1024;
    int64_t stall_ms = 10000;
    void wake();
};

// ———————————————————————————— 连接 ————————————————————————————

enum class CState { Reading, Handling, Writing, WebSocket };

struct Conn {
    int fd = -1;
    uint64_t id = 0;
    PeerInfo peer;
    CState st = CState::Reading;
    bool dead = false;
    std::string rbuf, wbuf;
    size_t wpos = 0;
    int64_t last_activity = 0, req_start = 0, last_write_progress = 0;
    bool continue_sent = false;
    bool keep_alive = true;
    bool close_after_write = false;
    // HTTP 响应
    uint64_t gen = 0;
    std::weak_ptr<Exchange> exchange;
    std::shared_ptr<FileBody> file;
    uint64_t file_off = 0, file_left = 0;
    std::shared_ptr<StreamState> stream;
    bool stream_done = false;
    std::shared_ptr<void> download_slot;
    // WebSocket
    WsSessionPtr ws;
    std::shared_ptr<WsHandlers> ws_handlers;
    std::unique_ptr<ws::MessageAssembler> asmb;
    std::deque<std::string> ctrl_out;
    bool ws_local_slot = false;
    bool ws_close_sent = false, ws_close_received = false, ws_finish_after_flush = false;
    int64_t ws_close_deadline = 0, ws_frame_start = 0, ws_last_keepalive = 0;
    uint16_t ws_close_code = ws::kCloseAbnormal;
    // TLS（只有 TCP 且开了加密才有）：密文进出走 tls，rbuf / wbuf 里永远是明文
    std::unique_ptr<TlsStream> tls;
    int64_t accepted_at = 0;
    bool tls_reject = false;   // 对加密端口说明文 HTTP：回一段明文提示后断开
    bool tls_bye = false;      // 已做过最后一次发送（close_notify），不再重复
};

// 还有没发出去的数据（明文 wbuf，或 TLS 已加密未发出的密文）
inline bool out_pending(const Conn& c) {
    return c.wpos < c.wbuf.size() || (c.tls && c.tls->out_pending());
}

struct ConnAccess {
    static void set_reason(WsSession& s, const std::string& r) {
        std::lock_guard<std::mutex> lk(s.mu_);
        if (s.close_reason_.empty()) s.close_reason_ = r;
    }
};

// ———————————————————————————— 核心 ————————————————————————————

struct Core : std::enable_shared_from_this<Core> {
    ServerOptions opt;
    Router router;
    ApiStats stats;
    Gate gate;
    ThreadPool pool;
    std::thread io;
    std::atomic<bool> running{false}, stop_req{false};
    bool started = false;

    int wake_r = -1, wake_w = -1;
    std::atomic<bool> wake_pending{false};
    std::mutex post_mu;
    std::vector<std::function<void()>> posted;

    int tcp_fd = -1, unix_fd = -1;
    std::atomic<int> tcp_port{0};
    int64_t next_bind_retry = 0;
    int bind_failures = 0;
    KeyedThrottle bind_throttle{10 * 60 * 1000};
    bool unix_created = false;
    int64_t accept_pause_until = 0;

    std::unordered_map<uint64_t, std::unique_ptr<Conn>> conns;   // 只在 I/O 线程
    uint64_t next_id = 1;
    int tcp_conn_count = 0, unix_conn_count = 0;

    std::atomic<int> ws_tcp{0}, ws_unix{0};
    std::atomic<int> ws_cap_tcp{4}, ws_cap_unix{4};
    mutable std::mutex sess_mu;
    std::unordered_map<uint64_t, WsSessionPtr> sessions;
    KeyedThrottle ws_audit_throttle{10 * 1000};
    KeyedThrottle tls_audit_throttle{10 * 60 * 1000};
    std::atomic<int64_t> ws_audit_suppressed{0};

    Core(ServerOptions o, const ApiConfig& cfg)
        : opt(std::move(o)), gate(cfg, opt.gate, &stats, opt.audit) {
        ws_cap_tcp = opt.ws_max_clients;
        ws_cap_unix = opt.ws_max_clients_local;
    }

    void audit(const char* level, const std::string& title, const std::string& detail) {
        if (opt.audit) {
            try { opt.audit(level, title, detail); } catch (...) {}
        }
    }

    // —— 跨线程 ——
    void wake() {
        if (wake_w >= 0 && !wake_pending.exchange(true)) {
            char c = 1;
            ssize_t n = ::write(wake_w, &c, 1);
            (void)n;
        }
    }
    void post(std::function<void()> fn) {
        if (!running.load()) return;
        {
            std::lock_guard<std::mutex> lk(post_mu);
            posted.push_back(std::move(fn));
        }
        wake();
    }

    // —— WS 回调串行队列 ——
    void enqueue_cb(const WsSessionPtr& s, std::function<void()> fn) {
        {
            std::lock_guard<std::mutex> lk(s->cb_mu_);
            s->cb_queue_.push_back(std::move(fn));
            if (s->cb_running_) return;
            s->cb_running_ = true;
        }
        bool ok = pool.submit([s] { drain_cb(s); });
        if (!ok) {   // 池已停：就地丢弃
            std::lock_guard<std::mutex> lk(s->cb_mu_);
            s->cb_queue_.clear();
            s->cb_running_ = false;
        }
    }
    static void drain_cb(const WsSessionPtr& s) {
        while (true) {
            std::function<void()> fn;
            {
                std::lock_guard<std::mutex> lk(s->cb_mu_);
                if (s->cb_queue_.empty()) { s->cb_running_ = false; return; }
                fn = std::move(s->cb_queue_.front());
                s->cb_queue_.pop_front();
            }
            try { fn(); } catch (...) {}
        }
    }

    // —— 监听 ——
    bool open_tcp(std::string* err);
    bool open_unix(std::string* err);
    void close_listeners();
    void accept_on(int lfd, bool unix_sock, int64_t now);

    // —— I/O 循环 ——
    void io_loop();
    void on_readable(Conn& c, int64_t now);
    void try_parse(Conn& c, int64_t now);
    void dispatch(Conn& c, Request req, int64_t now);
    void handle_upgrade(Conn& c, Request req, const Router::Route* route, int64_t now);
    void write_response(Conn& c, Response r, bool head, int64_t now);
    void install_response(uint64_t id, uint64_t gen, bool head, Response r);
    void install_stream(uint64_t id, uint64_t gen, bool head, Response r, const std::shared_ptr<StreamState>& st);
    void pump_output(Conn& c, int64_t now);
    void pump_output_tls(Conn& c, int64_t now);
    bool tls_pump_in(Conn& c, int64_t now);
    void tls_drain(Conn& c, int64_t now);
    bool tls_wants_read(const Conn& c) const;
    void tls_reject_plain(Conn& c, int64_t now);
    void final_send(Conn& c, const std::string& plain);
    bool refill(Conn& c, int64_t now);
    bool ws_refill(Conn& c, int64_t now);
    void after_flush(Conn& c, int64_t now);
    void finish_response(Conn& c, int64_t now);
    void ws_process_input(Conn& c);
    void timers(Conn& c, int64_t now);
    void close_conn(Conn& c);
    void ws_audit(const std::string& title, const std::string& key, std::string detail, int64_t now);
};

Exchange::~Exchange() {
    if (responded.load()) return;
    // 处理器把所有 Responder 副本都丢了也没回复：自动 500，连接不会挂住
    if (auto c = core.lock()) {
        uint64_t id = conn_id, g = gen;
        bool h = head;
        Core* raw = c.get();
        c->post([raw, id, g, h] {
            raw->install_response(id, g, h, Response::error(codes::kInternal, "处理器未返回响应"));
        });
    }
}

void StreamState::wake() {
    if (auto c = core.lock()) c->wake();
}

// ———————————————————————————— 监听 ————————————————————————————

bool Core::open_tcp(std::string* err) {
    std::string host = opt.tcp_bind;
    if (host.size() >= 2 && host.front() == '[' && host.back() == ']') host = host.substr(1, host.size() - 2);
    if (host.empty()) host = "0.0.0.0";
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_flags = AI_PASSIVE | AI_NUMERICHOST | AI_NUMERICSERV;
    addrinfo* res = nullptr;
    std::string port = std::to_string(opt.tcp_port);
    if (getaddrinfo(host.c_str(), port.c_str(), &hints, &res) != 0 || !res) {
        // C#: 无法解析的 bind 按 IPAddress.Any
        audit("warn", "接口 bind 地址无法解析 · 按 0.0.0.0", opt.tcp_bind);
        if (getaddrinfo("0.0.0.0", port.c_str(), &hints, &res) != 0 || !res) {
            if (err) *err = "bind 地址无法解析：" + opt.tcp_bind;
            return false;
        }
    }
    int fd = ::socket(res->ai_family, SOCK_STREAM, 0);
    if (fd < 0) {
        if (err) *err = std::string("socket: ") + std::strerror(errno);
        freeaddrinfo(res);
        return false;
    }
    int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one);
    if (res->ai_family == AF_INET6) {
        int zero = 0;   // "::" 双栈：IPv4 客户端以 ::ffff:a.b.c.d 出现，CIDR 按 IPv4 判定
        setsockopt(fd, IPPROTO_IPV6, IPV6_V6ONLY, &zero, sizeof zero);
    }
    set_nonblock_cloexec(fd);
    if (::bind(fd, res->ai_addr, res->ai_addrlen) != 0 || ::listen(fd, 128) != 0) {
        if (err) *err = host + ":" + port + " · " + std::strerror(errno);
        ::close(fd);
        freeaddrinfo(res);
        return false;
    }
    freeaddrinfo(res);
    sockaddr_storage ss{};
    socklen_t sl = sizeof ss;
    if (getsockname(fd, reinterpret_cast<sockaddr*>(&ss), &sl) == 0) {
        if (ss.ss_family == AF_INET) tcp_port = ntohs(reinterpret_cast<sockaddr_in*>(&ss)->sin_port);
        else if (ss.ss_family == AF_INET6) tcp_port = ntohs(reinterpret_cast<sockaddr_in6*>(&ss)->sin6_port);
    }
    tcp_fd = fd;
    return true;
}

bool Core::open_unix(std::string* err) {
    const std::string& path = opt.unix_path;
    sockaddr_un sa{};
    if (path.size() >= sizeof(sa.sun_path)) {
        if (err) *err = "Unix socket 路径过长：" + path;
        return false;
    }
    sa.sun_family = AF_UNIX;
    std::memcpy(sa.sun_path, path.c_str(), path.size() + 1);
    struct stat st{};
    if (::lstat(path.c_str(), &st) == 0) {
        if (!S_ISSOCK(st.st_mode)) {
            if (err) *err = path + " 已存在且不是 socket（不删）";
            return false;
        }
        // 探测：连得上 = 另一个实例在跑；拒绝连接 = 残留文件，删掉
        int probe = ::socket(AF_UNIX, SOCK_STREAM, 0);
        if (probe >= 0) {
            set_nosigpipe(probe);
            int rc = ::connect(probe, reinterpret_cast<sockaddr*>(&sa), sizeof sa);
            int e = errno;
            ::close(probe);
            if (rc == 0) {
                if (err) *err = path + " 已有进程在监听";
                return false;
            }
            if (e != ECONNREFUSED && e != ENOENT) {
                if (err) *err = path + " 探测失败：" + std::strerror(e);
                return false;
            }
        }
        ::unlink(path.c_str());
    }
    int fd = ::socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) {
        if (err) *err = std::string("socket: ") + std::strerror(errno);
        return false;
    }
    set_nonblock_cloexec(fd);
    if (::bind(fd, reinterpret_cast<sockaddr*>(&sa), sizeof sa) != 0) {
        if (err) *err = path + " · " + std::strerror(errno);
        ::close(fd);
        return false;
    }
    unix_created = true;
    ::chmod(path.c_str(), static_cast<mode_t>(opt.unix_mode));
    if (::listen(fd, 64) != 0) {
        if (err) *err = path + " · listen: " + std::strerror(errno);
        ::close(fd);
        ::unlink(path.c_str());
        unix_created = false;
        return false;
    }
    unix_fd = fd;
    return true;
}

void Core::close_listeners() {
    if (tcp_fd >= 0) { ::close(tcp_fd); tcp_fd = -1; }
    if (unix_fd >= 0) { ::close(unix_fd); unix_fd = -1; }
    if (unix_created) { ::unlink(opt.unix_path.c_str()); unix_created = false; }
    tcp_port = 0;
}

namespace {
void peer_creds(int fd, PeerInfo& p) {
#if defined(__linux__)
    struct ucred cr{};
    socklen_t l = sizeof cr;
    if (getsockopt(fd, SOL_SOCKET, SO_PEERCRED, &cr, &l) == 0) {
        p.uid = cr.uid;
        p.gid = cr.gid;
        p.pid = cr.pid;
    }
#else
    uid_t u;
    gid_t g;
    if (getpeereid(fd, &u, &g) == 0) {
        p.uid = u;
        p.gid = g;
    }
#if defined(LOCAL_PEERPID)
    pid_t pid = 0;
    socklen_t l = sizeof pid;
    if (getsockopt(fd, SOL_LOCAL, LOCAL_PEERPID, &pid, &l) == 0) p.pid = pid;
#endif
#endif
}
}  // namespace

void Core::accept_on(int lfd, bool unix_sock, int64_t now) {
    while (true) {
        sockaddr_storage ss{};
        socklen_t sl = sizeof ss;
        int fd = ::accept(lfd, reinterpret_cast<sockaddr*>(&ss), &sl);
        if (fd < 0) {
            if (errno == EINTR) continue;
            if (errno == EMFILE || errno == ENFILE || errno == ENOBUFS || errno == ENOMEM) accept_pause_until = now + 100;
            return;
        }
        set_nonblock_cloexec(fd);
        set_nosigpipe(fd);
        int& count = unix_sock ? unix_conn_count : tcp_conn_count;
        int cap = unix_sock ? opt.max_connections_unix : opt.max_connections_tcp;
        if (count >= cap) {
            ::close(fd);
            continue;
        }
        auto c = std::make_unique<Conn>();
        c->fd = fd;
        c->id = next_id++;
        c->last_activity = now;
        c->accepted_at = now;
        if (!unix_sock && opt.tls) {
            std::string terr;
            c->tls = TlsStream::create(opt.tls->server_config(), &terr);
            if (!c->tls) {   // 证书没装上：宁可拒绝也不退回明文
                ::close(fd);
                if (tls_audit_throttle.allow("init", now)) audit("err", "加密连接建立失败 · 已断开", terr);
                continue;
            }
        }
        if (unix_sock) {
            c->peer.transport = Transport::Unix;
            c->peer.ip_text = "local";
            peer_creds(fd, c->peer);
        } else {
            int one = 1;
            setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
            c->peer.transport = Transport::Tcp;
            IpAddr ip;
            if (ss.ss_family == AF_INET) {
                auto* a = reinterpret_cast<sockaddr_in*>(&ss);
                ip.bytes.assign(reinterpret_cast<uint8_t*>(&a->sin_addr), reinterpret_cast<uint8_t*>(&a->sin_addr) + 4);
                c->peer.port = ntohs(a->sin_port);
            } else if (ss.ss_family == AF_INET6) {
                auto* a = reinterpret_cast<sockaddr_in6*>(&ss);
                ip.bytes.assign(reinterpret_cast<uint8_t*>(&a->sin6_addr), reinterpret_cast<uint8_t*>(&a->sin6_addr) + 16);
                c->peer.port = ntohs(a->sin6_port);
            }
            if (!ip.bytes.empty()) {
                c->peer.ip = normalize_ip(ip);
                c->peer.ip_text = ip_to_string(*c->peer.ip);
            } else {
                c->peer.ip_text = "-";
            }
        }
        ++count;
        uint64_t id = c->id;
        conns[id] = std::move(c);
    }
}

// ———————————————————————————— 读 / 解析 / 分发 ————————————————————————————

void Core::on_readable(Conn& c, int64_t now) {
    char buf[16384];
    size_t cap = opt.limits.max_header_bytes + opt.limits.max_body_bytes + 65536;
    for (int i = 0; i < 32; ++i) {
        if (c.tls ? c.tls->buffered() >= cap : (c.st != CState::WebSocket && c.rbuf.size() >= cap)) break;
        ssize_t n = ::recv(c.fd, buf, sizeof buf, 0);
        if (n > 0) {
            if (c.tls) c.tls->feed(buf, static_cast<size_t>(n));
            else c.rbuf.append(buf, static_cast<size_t>(n));
            c.last_activity = now;
            continue;
        }
        if (n == 0) {   // 对端关闭
            if (c.ws) ConnAccess::set_reason(*c.ws, c.ws_close_received ? "客户端关闭" : "连接中断");
            c.dead = true;
            return;
        }
        if (errno == EINTR) continue;
        if (errno == EAGAIN || errno == EWOULDBLOCK) break;
        if (c.ws) ConnAccess::set_reason(*c.ws, "连接中断");
        c.dead = true;
        return;
    }
    if (c.tls && !tls_pump_in(c, now)) return;
    if (c.st == CState::Reading) try_parse(c, now);
    else if (c.st == CState::WebSocket) ws_process_input(c);
}

// —— TLS：握手 + 把能解密的都解密进 rbuf。返回 false = 还不能处理明文（握手中 / 已断开）——
bool Core::tls_pump_in(Conn& c, int64_t now) {
    TlsStream& t = *c.tls;
    if (c.tls_reject) return false;
    if (!t.handshake_done()) {
        // 第一个字节是大写字母 = 明文 HTTP（老 PC 端 / 浏览器写了 http://）：回一句明文提示再断开，别让对方干等
        const std::string& in = t.peek_input();
        if (!in.empty() && in[0] >= 'A' && in[0] <= 'Z') {
            tls_reject_plain(c, now);
            return false;
        }
        TlsStream::Status st = t.handshake();
        if (st == TlsStream::Status::WantMore) return false;
        if (st != TlsStream::Status::Ok) {
            c.dead = true;   // 握手失败：PC 端拒绝了证书（指纹对不上）也是这里，不逐条记
            return false;
        }
    }
    size_t cap = opt.limits.max_header_bytes + opt.limits.max_body_bytes + 65536;
    while (c.st == CState::WebSocket || c.rbuf.size() < cap) {
        TlsStream::Status st = t.read(c.rbuf, 16384);
        if (st == TlsStream::Status::Ok) {
            c.last_activity = now;
            continue;
        }
        if (st == TlsStream::Status::WantMore) break;
        if (c.ws)
            ConnAccess::set_reason(*c.ws, st == TlsStream::Status::Closed ? (c.ws_close_received ? "客户端关闭" : "连接中断")
                                                                          : "连接中断（加密数据出错）");
        c.dead = true;
        return false;
    }
    return true;
}

bool Core::tls_wants_read(const Conn& c) const {
    size_t rcap = opt.limits.max_header_bytes + opt.limits.max_body_bytes + 65536;
    return c.st == CState::WebSocket ? !c.ws_close_received : c.rbuf.size() < rcap;
}

// poll 没报可读，但 mbedTLS / 密文缓冲里还有数据（上次因为 rbuf 满了没解完）：接着解
void Core::tls_drain(Conn& c, int64_t now) {
    if (c.dead || !c.tls || !c.tls->pending() || !tls_wants_read(c)) return;
    if (!tls_pump_in(c, now)) return;
    if (c.st == CState::Reading) try_parse(c, now);
    else if (c.st == CState::WebSocket) ws_process_input(c);
}

void Core::tls_reject_plain(Conn& c, int64_t now) {
    static const std::string body =
        "{\"ok\":false,\"error\":{\"code\":\"TLS_REQUIRED\",\"message\":\"此端口已加密，请用 https:// 或 wss:// 连接"
        "（老版本 PC 端请在设备 api.json 里设 \\\"tls\\\": false）\"}}";
    std::string resp = "HTTP/1.1 400 Bad Request\r\nContent-Type: application/json; charset=utf-8\r\nConnection: close\r\n"
                       "Content-Length: " + std::to_string(body.size()) + "\r\n\r\n" + body;
    c.tls->out().assign(resp);   // 绕过 TLS 直接发明文
    c.tls->out_pos = 0;
    c.tls_reject = true;
    c.last_write_progress = now;
    if (tls_audit_throttle.allow("plain:" + c.peer.ip_text, now))
        audit("warn", "有客户端用明文 HTTP 连加密端口 · 已拒绝",
              c.peer.ip_text + " · 新版 PC 端请用 https:// / wss://；老版本 PC 端要在 api.json 里设 \"tls\": false"
                               "（同一地址每 10 分钟最多记一条）");
}

void Core::try_parse(Conn& c, int64_t now) {
    if (c.dead || c.st != CState::Reading || c.rbuf.empty()) return;
    if (!c.req_start) c.req_start = now;
    Request req;
    ParseResult r = parse_request(c.rbuf.data(), c.rbuf.size(), opt.limits, req);
    if (r.kind == ParseResult::Incomplete) {
        if (r.headers_complete && r.expect_continue && !c.continue_sent) {
            c.wbuf += "HTTP/1.1 100 Continue\r\n\r\n";
            c.continue_sent = true;
        }
        return;
    }
    if (r.kind == ParseResult::Error) {
        c.rbuf.clear();
        c.close_after_write = true;
        write_response(c, Response::error(r.code, r.message, nlohmann::json(), r.status), false, now);
        return;
    }
    c.rbuf.erase(0, r.consumed);
    c.req_start = 0;
    c.continue_sent = false;
    c.keep_alive = req.keep_alive();
    dispatch(c, std::move(req), now);
}

void Core::dispatch(Conn& c, Request req, int64_t now) {
    stats.requests++;
    req.peer = c.peer;
    req.conn_id = c.id;
    req.received_mono_ms = now;
    bool head = req.method == "HEAD";
    auto m = router.match(req.method, req.raw_path, c.peer.transport == Transport::Unix);
    Gate::Decision d = gate.check(req, m, now);
    if (d.reject) {
        if (d.close_connection) c.close_after_write = true;
        write_response(c, std::move(*d.reject), head, now);
        return;
    }
    req.params = std::move(m.params);
    if (m.route && m.route->ws) {
        handle_upgrade(c, std::move(req), m.route, now);
        return;
    }
    if (m.method_mismatch && !opt.method_mismatch_as_404) {
        Response r = Response::error(codes::kMethodNotAllowed, "该路径不支持 " + req.method + " 方法", nlohmann::json(), 405);
        r.headers.set("Allow", m.allow);
        write_response(c, std::move(r), head, now);
        return;
    }
    const Handler* h = nullptr;
    if (m.route) h = &m.route->handler;
    else if (router.fallback()) h = &router.fallback();
    if (!h || !*h) {
        write_response(c, Response::error(codes::kNotFound, "没有这个端点"), head, now);
        return;
    }
    c.st = CState::Handling;
    c.gen++;
    c.download_slot = std::move(d.download_slot);
    auto ex = std::make_shared<Exchange>();
    ex->core = weak_from_this();
    ex->conn_id = c.id;
    ex->gen = c.gen;
    ex->head = head;
    ex->version_minor = req.version_minor;
    c.exchange = ex;
    auto rq = std::make_shared<const Request>(std::move(req));
    bool ok = pool.submit([h, rq, ex]() mutable {
        Responder res(std::move(ex));
        try {
            (*h)(*rq, res);
        } catch (const std::exception& e) {
            res.send(Response::error(codes::kInternal, std::string("处理器异常：") + e.what()));
        } catch (...) {
            res.send(Response::error(codes::kInternal, "处理器异常"));
        }
    });
    if (!ok) c.dead = true;
}

namespace {
std::string trim_sp(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && (s[a] == ' ' || s[a] == '\t')) ++a;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t')) --b;
    return s.substr(a, b - a);
}
}  // namespace

void Core::handle_upgrade(Conn& c, Request req, const Router::Route* route, int64_t now) {
    std::string key = trim_sp(req.header("Sec-WebSocket-Key"));
    bool ok = req.method == "GET" && req.headers.has_token("Upgrade", "websocket") &&
              req.headers.has_token("Connection", "upgrade") && ws::valid_client_key(key);
    if (!ok) {
        write_response(c, Response::error(codes::kBadRequest, "此路径只接受 WebSocket 升级请求"), false, now);
        return;
    }
    if (trim_sp(req.header("Sec-WebSocket-Version")) != "13") {
        Response r = Response::error(codes::kBadRequest, "只支持 WebSocket 协议版本 13");
        r.headers.set("Sec-WebSocket-Version", "13");
        write_response(c, std::move(r), false, now);
        return;
    }
    bool local = c.peer.transport == Transport::Unix;
    auto& cnt = local ? ws_unix : ws_tcp;
    int cap = local ? ws_cap_unix.load() : ws_cap_tcp.load();
    // 先占位再握手（C#: WsHub.HandleAsync 的 Interlocked.Increment）
    if (cnt.fetch_add(1) >= cap) {
        cnt.fetch_sub(1);
        write_response(c, Response::error(codes::kTooManyClients, "WebSocket 连接数已达上限 " + std::to_string(cap)), false, now);
        return;
    }
    c.wbuf += "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " +
              ws::accept_key(key) + "\r\n\r\n";
    c.st = CState::WebSocket;
    c.ws_local_slot = local;
    c.ws_last_keepalive = now;
    c.asmb = std::make_unique<ws::MessageAssembler>(opt.ws_max_message, true);
    c.ws_handlers = route->ws;

    auto s = std::make_shared<WsSession>();
    s->id_ = c.id;
    s->auth_ = req.auth;
    s->peer_ = c.peer;
    s->path_ = req.path;
    s->tele_cap_ = opt.ws_telemetry_queue;
    s->evt_cap_ = opt.ws_event_queue;
    s->stats_ = &stats;
    std::weak_ptr<Core> wk = weak_from_this();
    s->wake_ = [wk] {
        if (auto k = wk.lock()) k->wake();
    };
    c.ws = s;
    size_t n;
    {
        std::lock_guard<std::mutex> lk(sess_mu);
        sessions[s->id_] = s;
        n = sessions.size();
    }
    stats.clients++;
    std::string who = s->auth_.name + "@" + c.peer.ip_text;
    ws_audit("接口客户端已连接", "c:" + who,
             s->auth_.name + " @ " + c.peer.ip_text + " · WS #" + std::to_string(s->id_) + " · 当前 " + std::to_string(n) + " 个", now);

    auto rq = std::make_shared<const Request>(std::move(req));
    auto h = route->ws;
    enqueue_cb(s, [h, s, rq] {
        try {
            if (h->on_open) h->on_open(s, *rq);
        } catch (...) {
        }
        s->broadcast_ready_ = true;   // on_open 之后才进广播（hello 必为第一条）
    });
    if (!c.rbuf.empty()) ws_process_input(c);
}

// ———————————————————————————— 写 ————————————————————————————

void Core::write_response(Conn& c, Response r, bool head, int64_t now) {
    c.st = CState::Writing;
    if (r.headers.has_token("Connection", "close") || !c.keep_alive) c.close_after_write = true;
    bool ka = !c.close_after_write && !stop_req.load();
    bool no_body = (r.status >= 100 && r.status < 200) || r.status == 204 || r.status == 304;
    if (r.file) {
        uint64_t len = r.file->length;
        c.wbuf += serialize_head(r, static_cast<int64_t>(len), ka, false);
        if (!head && !no_body && len > 0) {
            c.file = r.file;
            c.file_off = r.file->offset;
            c.file_left = len;
        }
    } else {
        c.wbuf += serialize_head(r, static_cast<int64_t>(r.body.size()), ka, false);
        if (!head && !no_body) c.wbuf += r.body;
    }
    c.last_write_progress = now;
}

void Core::install_response(uint64_t id, uint64_t gen, bool head, Response r) {
    auto it = conns.find(id);
    if (it == conns.end()) return;
    Conn& c = *it->second;
    if (c.dead || c.st != CState::Handling || c.gen != gen) return;
    write_response(c, std::move(r), head, mono_ms());
}

void Core::install_stream(uint64_t id, uint64_t gen, bool head, Response r, const std::shared_ptr<StreamState>& st) {
    auto mark_closed = [&] {
        {
            std::lock_guard<std::mutex> lk(st->mu);
            st->conn_closed = true;
        }
        st->cv.notify_all();
    };
    auto it = conns.find(id);
    if (it == conns.end()) { mark_closed(); return; }
    Conn& c = *it->second;
    if (c.dead || c.st != CState::Handling || c.gen != gen) { mark_closed(); return; }
    int64_t now = mono_ms();
    c.st = CState::Writing;
    if (r.headers.has_token("Connection", "close") || !c.keep_alive) c.close_after_write = true;
    bool chunked, declared_known;
    int64_t declared;
    {
        std::lock_guard<std::mutex> lk(st->mu);
        chunked = st->chunked;
        declared = st->declared;
        declared_known = declared >= 0;
    }
    if (!chunked && !declared_known) c.close_after_write = true;   // HTTP/1.0：以断开为结束
    bool ka = !c.close_after_write && !stop_req.load();
    c.wbuf += serialize_head(r, chunked ? -1 : declared, ka, chunked);
    c.last_write_progress = now;
    if (head) {
        mark_closed();
        c.stream_done = true;
        return;
    }
    c.stream = st;
    c.stream_done = false;
}

void Core::pump_output(Conn& c, int64_t now) {
    if (c.dead || c.fd < 0) return;
    if (c.tls) {
        pump_output_tls(c, now);
        return;
    }
    while (true) {
        if (c.wpos < c.wbuf.size()) {
            ssize_t n = ::send(c.fd, c.wbuf.data() + c.wpos, c.wbuf.size() - c.wpos, kSendFlags);
            if (n > 0) {
                c.wpos += static_cast<size_t>(n);
                c.last_write_progress = now;
                continue;
            }
            if (n < 0 && errno == EINTR) continue;
            if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) return;
            if (c.ws) ConnAccess::set_reason(*c.ws, "发送失败（连接中断）");
            c.dead = true;
            return;
        }
        c.wbuf.clear();
        c.wpos = 0;
        c.ws_frame_start = 0;
        if (c.dead || !refill(c, now)) break;
    }
    if (!c.dead) after_flush(c, now);
}

// TLS 版：先把密文发干净，再加密下一条记录（每次最多 16 KB 明文），明文也发完了才 refill
void Core::pump_output_tls(Conn& c, int64_t now) {
    TlsStream& t = *c.tls;
    while (true) {
        std::string& o = t.out();
        while (t.out_pos < o.size()) {
            ssize_t n = ::send(c.fd, o.data() + t.out_pos, o.size() - t.out_pos, kSendFlags);
            if (n > 0) {
                t.out_pos += static_cast<size_t>(n);
                c.last_write_progress = now;
                continue;
            }
            if (n < 0 && errno == EINTR) continue;
            if (n < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) return;
            if (c.ws) ConnAccess::set_reason(*c.ws, "发送失败（连接中断）");
            c.dead = true;
            return;
        }
        o.clear();
        t.out_pos = 0;
        if (c.tls_reject) {
            c.dead = true;
            return;
        }
        if (!t.handshake_done()) return;
        if (c.wpos < c.wbuf.size()) {
            long k = t.write(c.wbuf.data() + c.wpos, c.wbuf.size() - c.wpos);
            if (k < 0) {
                if (c.ws) ConnAccess::set_reason(*c.ws, "加密失败");
                c.dead = true;
                return;
            }
            if (k == 0) return;
            c.wpos += static_cast<size_t>(k);
            continue;
        }
        c.wbuf.clear();
        c.wpos = 0;
        c.ws_frame_start = 0;
        if (c.dead || !refill(c, now)) break;
    }
    if (!c.dead) after_flush(c, now);
}

bool Core::refill(Conn& c, int64_t now) {
    if (c.st == CState::WebSocket) return ws_refill(c, now);
    if (c.st != CState::Writing) return false;
    if (c.file && c.file_left > 0) {
        size_t chunk = static_cast<size_t>(std::min<uint64_t>(64 * 1024, c.file_left));
        c.wbuf.resize(chunk);
        ssize_t n = ::pread(c.file->fd, &c.wbuf[0], chunk, static_cast<off_t>(c.file_off));
        if (n <= 0) {   // 文件被截短：已发的头声明了长度，只能断开
            c.wbuf.clear();
            c.dead = true;
            return false;
        }
        c.wbuf.resize(static_cast<size_t>(n));
        c.file_off += static_cast<uint64_t>(n);
        c.file_left -= static_cast<uint64_t>(n);
        return true;
    }
    if (c.stream) {
        bool got = false;
        {
            std::lock_guard<std::mutex> lk(c.stream->mu);
            if (!c.stream->pending.empty()) {
                c.wbuf.swap(c.stream->pending);
                c.stream->pending.clear();
                c.stream->last_progress_ms = now;
                got = true;
            } else if (c.stream->aborted) {
                c.dead = true;
            } else if (c.stream->ended) {
                c.stream_done = true;
            }
        }
        if (got) c.stream->cv.notify_all();
        return got;
    }
    return false;
}

bool Core::ws_refill(Conn& c, int64_t now) {
    if (!c.ctrl_out.empty()) {
        c.wbuf = std::move(c.ctrl_out.front());
        c.ctrl_out.pop_front();
        c.ws_frame_start = now;
        return true;
    }
    if (c.ws_close_sent) return false;
    WsSession& s = *c.ws;
    WsSession::Msg m;
    bool is_event = false;
    {
        std::lock_guard<std::mutex> lk(s.mu_);
        if (s.close_requested_) {   // 服务端主动关闭：丢弃未发消息，发关闭帧，等对方回关闭帧
            s.tele_.clear();
            s.evt_.clear();
            s.open_ = false;
            c.wbuf = ws::encode_close(s.close_code_, s.close_wire_reason_);
            c.ws_close_sent = true;
            c.ws_close_code = s.close_code_;
            c.ws_close_deadline = now + opt.ws_close_timeout_ms;
            c.ws_frame_start = now;
            return true;
        }
        if (!s.evt_.empty()) {
            m = std::move(s.evt_.front());
            s.evt_.pop_front();
            is_event = true;
        } else if (!s.tele_.empty()) {
            m = std::move(s.tele_.front());
            s.tele_.pop_front();
        } else {
            return false;
        }
    }
    c.wbuf = ws::encode_frame(m.binary ? ws::kBinary : ws::kText, *m.data);
    c.ws_frame_start = now;
    if (is_event) { s.events_++; stats.events_sent++; }
    else { s.frames_++; stats.frames_sent++; }
    return true;
}

void Core::after_flush(Conn& c, int64_t now) {
    if (c.st == CState::Writing) {
        bool done = (!c.file || c.file_left == 0) && (!c.stream || c.stream_done);
        if (done) finish_response(c, now);
    } else if (c.st == CState::WebSocket) {
        if (c.ws_finish_after_flush && c.ctrl_out.empty()) c.dead = true;
    }
}

void Core::finish_response(Conn& c, int64_t now) {
    c.file.reset();
    c.file_left = 0;
    c.stream.reset();
    c.stream_done = false;
    c.download_slot.reset();
    c.exchange.reset();
    if (c.close_after_write || stop_req.load()) {
        c.dead = true;
        return;
    }
    c.st = CState::Reading;
    c.last_activity = now;
    c.req_start = 0;
    try_parse(c, now);
}

void Core::ws_process_input(Conn& c) {
    if (c.ws_close_received || c.ws_finish_after_flush) {
        c.rbuf.clear();
        return;
    }
    std::vector<ws::MessageAssembler::Event> evs;
    c.asmb->feed(c.rbuf, evs);
    auto s = c.ws;
    for (auto& e : evs) {
        switch (e.kind) {
            case ws::MessageAssembler::Event::Message: {
                if (c.ws_close_sent) break;   // 已发关闭帧：后续数据丢弃
                auto h = c.ws_handlers;
                auto data = std::make_shared<std::string>(std::move(e.data));
                bool bin = e.binary;
                enqueue_cb(s, [h, s, data, bin] {
                    if (h->on_message) h->on_message(s, *data, bin);
                });
                break;
            }
            case ws::MessageAssembler::Event::Ping:
                if (!c.ws_close_sent) c.ctrl_out.push_back(ws::encode_frame(ws::kPong, e.data));
                break;
            case ws::MessageAssembler::Event::Pong:
                break;
            case ws::MessageAssembler::Event::Close: {
                c.ws_close_received = true;
                ConnAccess::set_reason(*s, "客户端关闭");
                if (!c.ws_close_sent) {
                    c.ws_close_code = e.code;
                    uint16_t echo = e.code == ws::kCloseNoStatus ? ws::kCloseNormal : e.code;
                    c.ctrl_out.push_back(ws::encode_close(echo, ""));
                    c.ws_close_sent = true;
                    std::lock_guard<std::mutex> lk(s->mu_);
                    s->open_ = false;
                    s->tele_.clear();
                    s->evt_.clear();
                }
                c.ws_finish_after_flush = true;
                c.rbuf.clear();
                return;
            }
            case ws::MessageAssembler::Event::Error: {
                ConnAccess::set_reason(*s, e.data);
                if (!c.ws_close_sent) {
                    c.ws_close_code = e.code;
                    c.ctrl_out.clear();
                    c.ctrl_out.push_back(ws::encode_close(e.code, ""));
                    c.ws_close_sent = true;
                    std::lock_guard<std::mutex> lk(s->mu_);
                    s->open_ = false;
                    s->tele_.clear();
                    s->evt_.clear();
                }
                c.ws_finish_after_flush = true;
                c.rbuf.clear();
                return;
            }
        }
    }
}

void Core::timers(Conn& c, int64_t now) {
    if (c.dead) return;
    switch (c.st) {
        case CState::Reading:
            if (c.tls && !c.tls->handshake_done() && now - c.accepted_at > opt.tls_handshake_timeout_ms) c.dead = true;
            else if (c.req_start && now - c.req_start > opt.header_timeout_ms) c.dead = true;
            else if (!c.req_start && !out_pending(c) && now - c.last_activity > opt.idle_timeout_ms) c.dead = true;
            break;
        case CState::Writing:
            if (out_pending(c) && now - c.last_write_progress > opt.stream_stall_timeout_ms) c.dead = true;
            break;
        case CState::Handling:
            break;
        case CState::WebSocket:
            if (c.ws_frame_start && out_pending(c) && now - c.ws_frame_start > opt.ws_send_timeout_ms) {
                // 单条消息 10 s 没发完：慢客户端（C#: WsHub.Conn.SendAsync 超时 → 1008）
                ConnAccess::set_reason(*c.ws, "发送超时 " + std::to_string(opt.ws_send_timeout_ms / 1000) + " s（慢客户端）");
                c.ws_close_code = ws::kClosePolicyViolation;
                c.dead = true;
                break;
            }
            if (c.ws_close_sent && c.ws_close_deadline && now > c.ws_close_deadline && !c.ws_close_received) {
                c.dead = true;
                break;
            }
            if (opt.ws_keepalive_ms > 0 && !c.ws_close_sent && !out_pending(c) && c.wbuf.empty() && c.ctrl_out.empty() &&
                now - c.ws_last_keepalive >= opt.ws_keepalive_ms) {
                c.ctrl_out.push_back(ws::encode_frame(ws::kPong, std::string()));   // 单向保活（同 Kestrel KeepAliveInterval）
                c.ws_last_keepalive = now;
            }
            break;
    }
}

void Core::ws_audit(const std::string& title, const std::string& key, std::string detail, int64_t now) {
    if (!opt.audit) return;
    if (!ws_audit_throttle.allow(key, now)) {
        ws_audit_suppressed++;
        return;
    }
    int64_t sup = ws_audit_suppressed.exchange(0);
    if (sup > 0) detail += " · 期间省略 " + std::to_string(sup) + " 条连接 / 断开记录（同一客户端 10 秒内只记一条）";
    audit("info", title, detail);
}

// 关之前最后发一次（不等、不重试）：明文直接 send；TLS 先加密再发，末尾带 close_notify
void Core::final_send(Conn& c, const std::string& plain) {
    if (c.fd < 0) return;
    if (!c.tls) {
        if (!plain.empty()) {
            ssize_t n = ::send(c.fd, plain.data(), plain.size(), kSendFlags);
            (void)n;
        }
        return;
    }
    if (c.tls_bye) return;
    c.tls_bye = true;
    std::string& o = c.tls->out();
    // 握手没完成 / 明文拒绝：只把已有的（警报 / 明文提示）发出去；上一条记录发一半时也不能插新记录
    if (c.tls->handshake_done() && !c.tls_reject && c.tls->out_pos >= o.size()) {
        o.clear();
        c.tls->out_pos = 0;
        if (!plain.empty()) c.tls->write(plain.data(), plain.size());
        c.tls->close_notify();
    }
    if (c.tls->out_pos < o.size()) {
        ssize_t n = ::send(c.fd, o.data() + c.tls->out_pos, o.size() - c.tls->out_pos, kSendFlags);
        (void)n;
    }
}

void Core::close_conn(Conn& c) {
    if (c.tls && c.fd >= 0) final_send(c, std::string());   // 干净收尾：close_notify
    if (c.fd >= 0) {
        ::close(c.fd);
        c.fd = -1;
    }
    if (auto ex = c.exchange.lock()) ex->conn_closed = true;
    if (c.stream) {
        {
            std::lock_guard<std::mutex> lk(c.stream->mu);
            c.stream->conn_closed = true;
        }
        c.stream->cv.notify_all();
        c.stream.reset();
    }
    c.download_slot.reset();
    c.file.reset();
    if (c.peer.transport == Transport::Unix) --unix_conn_count;
    else --tcp_conn_count;
    if (c.ws) {
        auto s = c.ws;
        std::string reason;
        {
            std::lock_guard<std::mutex> lk(s->mu_);
            s->open_ = false;
            s->tele_.clear();
            s->evt_.clear();
            if (s->close_reason_.empty()) s->close_reason_ = c.ws_close_received ? "客户端关闭" : "连接中断";
            reason = s->close_reason_;
        }
        (c.ws_local_slot ? ws_unix : ws_tcp).fetch_sub(1);
        stats.clients--;
        {
            std::lock_guard<std::mutex> lk(sess_mu);
            sessions.erase(s->id_);
        }
        auto h = c.ws_handlers;
        uint16_t code = c.ws_close_code;
        enqueue_cb(s, [h, s, code, reason] {
            if (h && h->on_close) h->on_close(s, code, reason);
        });
        ws_audit("接口客户端已断开", "d:" + s->auth_.name + "@" + c.peer.ip_text,
                 s->auth_.name + " @ " + c.peer.ip_text + " · WS #" + std::to_string(s->id_) + " · " + reason + " · 发送帧 " +
                     std::to_string(s->frames_.load()) + " · 丢帧 " + std::to_string(s->dropped_.load()),
                 mono_ms());
        c.ws.reset();
    }
}

void Core::io_loop() {
    std::vector<pollfd> pfds;
    std::vector<uint64_t> ids;
    std::vector<uint64_t> dead;
    while (!stop_req.load()) {
        int64_t now = mono_ms();
        // TCP 绑定重试（C#: RunWithRetryAsync 每 10 s）
        if (opt.tcp_enabled && tcp_fd < 0 && next_bind_retry && now >= next_bind_retry) {
            std::string err;
            if (open_tcp(&err)) {
                next_bind_retry = 0;
                audit("info", "接口已启动", std::string(opt.tls ? "https://" : "http://") + opt.tcp_bind + ":" + std::to_string(tcp_port.load()) + opt.gate.api_prefix +
                                                " · 此前绑定失败 " + std::to_string(bind_failures) + " 次");
            } else {
                ++bind_failures;
                next_bind_retry = now + opt.tcp_bind_retry_ms;
                if (bind_throttle.allow("bind", now))
                    audit("err", "接口绑定失败 · 10 秒后重试",
                          err + " · 累计失败 " + std::to_string(bind_failures) + " 次（同类事件每 10 分钟最多一条，重试照常进行）");
            }
        }
        pfds.clear();
        ids.clear();
        pfds.push_back({wake_r, POLLIN, 0});
        int tcp_idx = -1, unix_idx = -1;
        bool accept_ok = now >= accept_pause_until;
        if (tcp_fd >= 0 && accept_ok) { tcp_idx = static_cast<int>(pfds.size()); pfds.push_back({tcp_fd, POLLIN, 0}); }
        if (unix_fd >= 0 && accept_ok) { unix_idx = static_cast<int>(pfds.size()); pfds.push_back({unix_fd, POLLIN, 0}); }
        size_t conn_base = pfds.size();
        size_t rcap = opt.limits.max_header_bytes + opt.limits.max_body_bytes + 65536;
        bool tls_ready = false;
        for (auto& kv : conns) {
            Conn& c = *kv.second;
            short ev = 0;
            bool want_read = c.st == CState::WebSocket ? !c.ws_close_received : c.rbuf.size() < rcap;
            if (c.tls) {
                if (want_read && c.tls->pending()) tls_ready = true;   // 不用等 poll：缓冲里就有能解密的数据
                want_read = !c.tls_reject && (c.st == CState::WebSocket ? !c.ws_close_received : c.tls->buffered() < rcap);
            }
            if (want_read) ev |= POLLIN;
            if (out_pending(c)) ev |= POLLOUT;
            pfds.push_back({c.fd, ev, 0});
            ids.push_back(kv.first);
        }
        int rc = ::poll(pfds.data(), static_cast<nfds_t>(pfds.size()), tls_ready ? 0 : 100);
        if (rc < 0 && errno != EINTR) break;
        now = mono_ms();
        if (pfds[0].revents & POLLIN) {
            wake_pending = false;
            char b[256];
            while (::read(wake_r, b, sizeof b) > 0) {
            }
        }
        std::vector<std::function<void()>> tasks;
        {
            std::lock_guard<std::mutex> lk(post_mu);
            tasks.swap(posted);
        }
        for (auto& t : tasks) t();
        tasks.clear();
        if (tcp_idx >= 0 && (pfds[tcp_idx].revents & POLLIN)) accept_on(tcp_fd, false, now);
        if (unix_idx >= 0 && (pfds[unix_idx].revents & POLLIN)) accept_on(unix_fd, true, now);
        for (size_t i = 0; i < ids.size(); ++i) {
            short re = pfds[conn_base + i].revents;
            if (!re) continue;
            auto it = conns.find(ids[i]);
            if (it == conns.end()) continue;
            Conn& c = *it->second;
            if (c.dead) continue;
            if (re & POLLNVAL) { c.dead = true; continue; }
            if (re & (POLLIN | POLLHUP | POLLERR)) {
                if ((pfds[conn_base + i].events & POLLIN) || (re & POLLIN)) on_readable(c, now);
                else if (re & (POLLHUP | POLLERR)) c.dead = true;
            }
        }
        if (opt.tls)
            for (auto& kv : conns) tls_drain(*kv.second, now);
        dead.clear();
        for (auto& kv : conns) {
            Conn& c = *kv.second;
            timers(c, now);
            pump_output(c, now);
            if (c.dead) dead.push_back(kv.first);
        }
        for (auto id : dead) {
            auto it = conns.find(id);
            if (it == conns.end()) continue;
            close_conn(*it->second);
            conns.erase(it);
        }
    }
    // 收尾：WS 尽量发一个 1001 关闭帧，然后全部关掉
    for (auto& kv : conns) {
        Conn& c = *kv.second;
        if (c.ws && !c.ws_close_sent && c.fd >= 0) {
            final_send(c, ws::encode_close(ws::kCloseGoingAway, "server stopping"));
            ConnAccess::set_reason(*c.ws, "服务端停止");
            c.ws_close_code = ws::kCloseGoingAway;
        }
        close_conn(c);
    }
    conns.clear();
    close_listeners();
}

}  // namespace detail

// ———————————————————————————— WsSession ————————————————————————————

void WsSession::fail_locked(uint16_t code, const std::string& reason) {
    close_requested_ = true;
    close_code_ = code;
    if (close_reason_.empty()) close_reason_ = reason.empty() ? "服务端关闭" : reason;
    // 线上的 reason 用 ASCII（C#: failStatus != null ? "resync required" : "bye"）
    close_wire_reason_ = code == ws::kCloseNormal ? "bye" : "resync required";
}

bool WsSession::send(std::shared_ptr<const std::string> text, WsQueue q) {
    if (!text) return false;
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (!open_ || close_requested_) return false;
        if (q == WsQueue::Telemetry) {
            if (tele_.size() >= tele_cap_) {
                tele_.pop_front();
                dropped_++;
                if (stats_) stats_->dropped_telemetry_frames++;
            }
            tele_.push_back(Msg{std::move(text), false});
        } else {
            if (evt_.size() >= evt_cap_) {
                fail_locked(ws::kClosePolicyViolation, "事件队列溢出（慢客户端）");
                tele_.clear();
                evt_.clear();
            } else {
                evt_.push_back(Msg{std::move(text), false});
            }
        }
    }
    if (wake_) wake_();
    std::lock_guard<std::mutex> lk(mu_);
    return !close_requested_;
}

bool WsSession::send(std::string text, WsQueue q) {
    return send(std::make_shared<const std::string>(std::move(text)), q);
}

bool WsSession::send_binary(std::string data) {
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (!open_ || close_requested_) return false;
        if (evt_.size() >= evt_cap_) {
            fail_locked(ws::kClosePolicyViolation, "事件队列溢出（慢客户端）");
            tele_.clear();
            evt_.clear();
        } else {
            evt_.push_back(Msg{std::make_shared<const std::string>(std::move(data)), true});
        }
    }
    if (wake_) wake_();
    std::lock_guard<std::mutex> lk(mu_);
    return !close_requested_;
}

void WsSession::close(uint16_t code, const std::string& reason) {
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (!open_ || close_requested_) return;
        if (!ws::valid_close_code(code)) code = ws::kCloseNormal;
        fail_locked(code, reason);
    }
    if (wake_) wake_();
}

bool WsSession::is_open() const {
    std::lock_guard<std::mutex> lk(mu_);
    return open_ && !close_requested_;
}

std::string WsSession::close_reason() const {
    std::lock_guard<std::mutex> lk(mu_);
    return close_reason_;
}

// ———————————————————————————— Responder / StreamWriter ————————————————————————————

bool Responder::send(Response r) const {
    if (!ex_ || ex_->responded.exchange(true)) return false;
    auto core = ex_->core.lock();
    if (!core) return false;
    detail::Core* raw = core.get();
    uint64_t id = ex_->conn_id, gen = ex_->gen;
    bool head = ex_->head;
    auto rp = std::make_shared<Response>(std::move(r));
    core->post([raw, id, gen, head, rp] { raw->install_response(id, gen, head, std::move(*rp)); });
    return !ex_->conn_closed.load();
}

std::unique_ptr<StreamWriter> Responder::stream(int status, Headers headers, int64_t content_length) const {
    if (!ex_ || ex_->responded.exchange(true)) return nullptr;
    auto core = ex_->core.lock();
    if (!core) return nullptr;
    auto st = std::make_shared<detail::StreamState>();
    st->core = core;
    st->declared = content_length;
    st->chunked = content_length < 0 && ex_->version_minor >= 1;
    st->high_water = core->opt.stream_high_water;
    st->stall_ms = core->opt.stream_stall_timeout_ms;
    st->last_progress_ms = mono_ms();
    Response r;
    r.status = status;
    r.headers = std::move(headers);
    detail::Core* raw = core.get();
    uint64_t id = ex_->conn_id, gen = ex_->gen;
    bool head = ex_->head;
    auto rp = std::make_shared<Response>(std::move(r));
    core->post([raw, id, gen, head, rp, st] { raw->install_stream(id, gen, head, std::move(*rp), st); });
    if (!core->running.load()) st->conn_closed = true;
    return std::make_unique<StreamWriter>(st);
}

bool Responder::connection_closed() const { return !ex_ || ex_->conn_closed.load(); }

StreamWriter::~StreamWriter() {
    if (!st_) return;
    bool woke = false;
    {
        std::lock_guard<std::mutex> lk(st_->mu);
        if (!st_->ended && !st_->aborted) {
            st_->aborted = true;
            woke = true;
        }
    }
    if (woke) st_->wake();
}

bool StreamWriter::write(const char* data, size_t len) {
    auto& s = *st_;
    std::unique_lock<std::mutex> lk(s.mu);
    if (s.aborted || s.conn_closed || s.ended) return false;
    if (len == 0) return true;   // chunked 下零长度块会被当成结束，直接忽略
    if (s.declared >= 0 && s.written + static_cast<int64_t>(len) > s.declared) {
        s.aborted = true;
        lk.unlock();
        s.wake();
        return false;
    }
    if (s.pending.empty()) s.last_progress_ms = mono_ms();
    while (s.pending.size() >= s.high_water && !s.conn_closed && !s.aborted) {
        int64_t now = mono_ms();
        int64_t left = s.stall_ms - (now - s.last_progress_ms);
        if (left <= 0) {
            s.aborted = true;
            lk.unlock();
            s.wake();
            return false;
        }
        s.cv.wait_for(lk, std::chrono::milliseconds(std::min<int64_t>(left, 200)));
    }
    if (s.conn_closed || s.aborted) return false;
    if (s.chunked) {
        char hdr[32];
        std::snprintf(hdr, sizeof hdr, "%zx\r\n", len);
        s.pending += hdr;
        s.pending.append(data, len);
        s.pending += "\r\n";
    } else {
        s.pending.append(data, len);
    }
    s.written += static_cast<int64_t>(len);
    lk.unlock();
    s.wake();
    return true;
}

bool StreamWriter::end() {
    auto& s = *st_;
    {
        std::lock_guard<std::mutex> lk(s.mu);
        if (s.aborted || s.conn_closed) return false;
        if (s.ended) return true;
        if (s.declared >= 0 && s.written != s.declared) {
            s.aborted = true;
        } else {
            if (s.chunked) s.pending += "0\r\n\r\n";
            s.ended = true;
        }
    }
    s.wake();
    std::lock_guard<std::mutex> lk(s.mu);
    return s.ended;
}

bool StreamWriter::closed() const {
    std::lock_guard<std::mutex> lk(st_->mu);
    return st_->aborted || st_->conn_closed;
}

// ———————————————————————————— Server ————————————————————————————

Server::Server(ServerOptions opt, const ApiConfig& cfg) : core_(std::make_shared<detail::Core>(std::move(opt), cfg)) {}

Server::~Server() { stop(); }

Router& Server::router() { return core_->router; }
ApiStats& Server::stats() { return core_->stats; }
Gate& Server::gate() { return core_->gate; }
int Server::tcp_port() const { return core_->tcp_port.load(); }

bool Server::start(std::string* error) {
    auto& k = *core_;
    if (k.started) {
        if (error) *error = "服务器已启动过";
        return false;
    }
    int p[2];
    if (::pipe(p) != 0) {
        if (error) *error = std::string("pipe: ") + std::strerror(errno);
        return false;
    }
    detail::set_nonblock_cloexec(p[0]);
    detail::set_nonblock_cloexec(p[1]);
    k.wake_r = p[0];
    k.wake_w = p[1];
    std::string err;
    if (!k.opt.unix_path.empty() && !k.open_unix(&err)) {
        k.close_listeners();
        ::close(k.wake_r); ::close(k.wake_w);
        k.wake_r = k.wake_w = -1;
        if (error) *error = err;
        return false;
    }
    if (k.opt.tcp_enabled && !k.open_tcp(&err)) {
        if (k.opt.tcp_bind_retry_ms <= 0) {
            k.close_listeners();
            ::close(k.wake_r); ::close(k.wake_w);
            k.wake_r = k.wake_w = -1;
            if (error) *error = err;
            return false;
        }
        k.bind_failures = 1;
        k.next_bind_retry = mono_ms() + k.opt.tcp_bind_retry_ms;
        k.bind_throttle.allow("bind", mono_ms());
        k.audit("err", "接口绑定失败 · 10 秒后重试", err + " · 累计失败 1 次（同类事件每 10 分钟最多一条，重试照常进行）");
        if (error) *error = err;
    }
    auto bad = k.gate.invalid_cidrs();
    if (!bad.empty()) {
        std::string list;
        for (size_t i = 0; i < bad.size(); ++i) list += (i ? "、" : "") + bad[i];
        k.audit("warn", "接口白名单含无效条目 · 已忽略", list);
    }
    k.started = true;
    k.running = true;
    k.pool.start(k.opt.worker_threads);
    k.io = std::thread([&k] { k.io_loop(); });
    if (k.tcp_fd >= 0 || k.unix_fd >= 0) {
        std::string where;
        if (k.tcp_fd >= 0) where += std::string(k.opt.tls ? "https://" : "http://") + k.opt.tcp_bind + ":" + std::to_string(k.tcp_port.load()) + k.opt.gate.api_prefix;
        if (k.unix_fd >= 0) where += (where.empty() ? "" : " · ") + std::string("unix:") + k.opt.unix_path;
        k.audit("info", "接口已启动",
                where + " · 白名单 " + (k.gate.cidr_count() == 0 ? std::string("仅本机") : std::to_string(k.gate.cidr_count()) + " 段") +
                    " · WS 上限 " + std::to_string(k.ws_cap_tcp.load()));
    }
    return true;
}

void Server::stop() {
    auto& k = *core_;
    if (!k.started || k.stop_req.exchange(true)) return;
    k.wake();
    if (k.io.joinable()) k.io.join();
    k.running = false;
    {
        std::lock_guard<std::mutex> lk(k.post_mu);
        k.posted.clear();
    }
    k.pool.stop();
    if (k.wake_r >= 0) ::close(k.wake_r);
    if (k.wake_w >= 0) ::close(k.wake_w);
    k.wake_r = k.wake_w = -1;
    k.audit("info", "接口已停止",
            "累计请求 " + std::to_string(k.stats.requests.load()) + " · 推送帧 " + std::to_string(k.stats.frames_sent.load()) +
                " · 丢帧 " + std::to_string(k.stats.dropped_telemetry_frames.load()));
}

void Server::update_config(const ApiConfig& cfg) {
    auto& k = *core_;
    k.gate.update_config(cfg);
    k.ws_cap_tcp = cfg.max_clients > 0 ? cfg.max_clients : 4;
    for (auto& s : ws_sessions())
        if (!s->auth().local && !k.gate.token_name_valid(s->auth().name)) s->close(ws::kClosePolicyViolation, "令牌已吊销");
}

size_t Server::ws_broadcast(std::shared_ptr<const std::string> msg, WsQueue q,
                            const std::function<bool(const WsSession&)>& filter) {
    size_t n = 0;
    for (auto& s : ws_sessions()) {
        if (!s->broadcast_ready_.load()) continue;
        if (filter && !filter(*s)) continue;
        if (s->send(msg, q)) ++n;
    }
    return n;
}

std::vector<WsSessionPtr> Server::ws_sessions() const {
    std::lock_guard<std::mutex> lk(core_->sess_mu);
    std::vector<WsSessionPtr> v;
    v.reserve(core_->sessions.size());
    for (auto& kv : core_->sessions) v.push_back(kv.second);
    return v;
}

void Server::submit(std::function<void()> fn) {
    if (!core_->running.load()) return;
    core_->pool.submit(std::move(fn));
}

}  // namespace reactor::net
