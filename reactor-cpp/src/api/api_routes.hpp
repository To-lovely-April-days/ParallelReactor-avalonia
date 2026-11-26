// 接口层内部：路由登记分文件实现（只给 src/api/*.cpp 用）。
#pragma once
#include <chrono>
#include <future>

#include "api/api_runs.hpp"
#include "api/api_service.hpp"
#include "api/api_util.hpp"
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>

namespace reactor::api {

struct Routes {
    static void read(ApiService& s, net::Router& r);      // api_routes_read.cpp：只读端点、文件、会话
    static void write(ApiService& s, net::Router& r);     // api_routes_write.cpp：写端点
    static void control(ApiService& s, net::Router& r);   // api_routes_control.cpp：控制权
    static void local(ApiService& s, net::Router& r);     // api_routes_local.cpp：新增端点（多数只限本机）
    static void ws(ApiService& s, net::Router& r);        // api_service.cpp
    static void extras(ApiService& s, net::Router& r);    // api_extras.cpp：/trend、/leak-tests/current/samples、/storage、/users、/device/comm/{m}/…
    static void runs(ApiService& s, net::Router& r);      // api_runs.cpp：/runs
    static void system(ApiService& s, net::Router& r);    // api_routes_system.cpp：网络、U 盘、导出
};

// 在控制线程上算一个结果并等（最多 timeout_ms）。控制线程不在跑 / 超时返回 nullopt。
template <class T>
std::optional<T> ApiService::on_control(std::function<T()> fn, int64_t timeout_ms) {
    auto p = std::make_shared<std::promise<T>>();
    auto fut = p->get_future();
    bool posted = d_.core->post([p, fn] {
        try {
            p->set_value(fn());
        } catch (...) {
            try { p->set_exception(std::current_exception()); } catch (...) {}
        }
    });
    if (!posted) return std::nullopt;
    if (fut.wait_for(std::chrono::milliseconds(timeout_ms)) != std::future_status::ready) return std::nullopt;
    try {
        return fut.get();
    } catch (...) {
        return std::nullopt;
    }
}

// 常用的 503：首个快照之前
inline net::Response starting_response() {
    return api_error("STARTING", "尚未产生第一个控制周期的快照，请稍后重试");
}
inline net::Response control_timeout_response() {
    return api_error("UI_THREAD_TIMEOUT", "控制线程 5 s 内未响应 · 请稍后重试");
}

}  // namespace reactor::api
