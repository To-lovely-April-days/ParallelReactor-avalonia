// 路由：方法 + 路径模式（/api/v1/channels/{id}/valve，末段可 {*rest} 吃掉余下路径）、
// 每条路由的限流分区 / 免鉴权 / 只限本地 标志，WebSocket 路由，404 兜底钩子，405。
// 字面段大小写不敏感（与 ASP.NET 路由一致），末尾 '/' 忽略；字面段多的模式优先，其次按登记顺序。
// 线程：服务器 start() 之前登记完；之后只读（任意线程 match）。
#pragma once
#include <functional>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

#include "net/http.hpp"
#include <cstdint>

namespace reactor::net {

class WsSession;
using WsSessionPtr = std::shared_ptr<WsSession>;

// 处理器：在工作线程上调用。同步处理器直接返回响应；异步处理器拿 Responder，可以稍后在任意线程回复。
using Handler = std::function<void(const Request&, Responder)>;
using SyncHandler = std::function<Response(const Request&)>;

// WebSocket 回调：每条连接的回调在工作线程上「串行」执行（同一连接不会并发进回调），顺序：
// on_open（一次）→ on_message（0..n）→ on_close（一次，必定调用，哪怕 on_open 时就断了）。
// on_open 返回之后该连接才进入 Server::ws_broadcast 的范围（所以 on_open 里先发的 hello 一定是第一条）。
struct WsHandlers {
    std::function<void(const WsSessionPtr&, const Request&)> on_open;
    std::function<void(const WsSessionPtr&, const std::string& data, bool binary)> on_message;
    std::function<void(const WsSessionPtr&, uint16_t code, const std::string& reason)> on_close;
};

struct RouteOptions {
    RateClass rate = RateClass::Auto;
    bool no_auth = false;      // 免鉴权（/health）；仍受 CIDR 约束
    bool local_only = false;   // 只在 Unix socket 上存在；TCP 上当作不存在（404，设计文档 §5.2）
};

class Router {
public:
    struct Route {
        std::string method;    // 大写；WS 路由为 GET
        std::string pattern;
        struct Seg { std::string text; bool param = false; bool catch_all = false; };
        std::vector<Seg> segs;
        Handler handler;
        std::shared_ptr<WsHandlers> ws;
        RouteOptions opts;
        int literal_count = 0;
    };
    struct Match {
        const Route* route = nullptr;
        std::unordered_map<std::string, std::string> params;
        bool method_mismatch = false;   // 路径存在但方法不对 → 405
        std::string allow;              // 405 的 Allow 头
    };

    Router& add(const std::string& method, const std::string& pattern, Handler h, RouteOptions o = {});
    Router& add_sync(const std::string& method, const std::string& pattern, SyncHandler h, RouteOptions o = {});
    Router& get(const std::string& p, SyncHandler h, RouteOptions o = {}) { return add_sync("GET", p, std::move(h), o); }
    Router& post(const std::string& p, SyncHandler h, RouteOptions o = {}) { return add_sync("POST", p, std::move(h), o); }
    Router& put(const std::string& p, SyncHandler h, RouteOptions o = {}) { return add_sync("PUT", p, std::move(h), o); }
    Router& patch(const std::string& p, SyncHandler h, RouteOptions o = {}) { return add_sync("PATCH", p, std::move(h), o); }
    Router& del(const std::string& p, SyncHandler h, RouteOptions o = {}) { return add_sync("DELETE", p, std::move(h), o); }
    Router& ws(const std::string& pattern, WsHandlers h, RouteOptions o = {});
    // 兜底（未匹配的路径，包括 TCP 上的 local_only 路由）。不设时回 404 NOT_FOUND「没有这个端点」（C#: MapFallback）
    void set_fallback(Handler h) { fallback_ = std::move(h); }
    const Handler& fallback() const { return fallback_; }

    // HEAD 可匹配 GET 路由。is_local = Unix socket 入口。
    Match match(const std::string& method, const std::string& raw_path, bool is_local) const;

private:
    std::vector<Route> routes_;
    Handler fallback_;
};

// 路由选项里的 Auto 按方法落到 Read / Write
RateClass effective_rate_class(RateClass c, const std::string& method);

}  // namespace reactor::net
