// 网关：每个请求在 I/O 线程上先过这里，再进路由处理器。
// TCP：白名单（403 IP_NOT_ALLOWED + 断开）→ 前缀（404）→ 免鉴权路由放行 → ?access_token= 拒绝（401）
//      → Bearer（401；同一 IP 每秒失败 > 10 次改回 429；审计每 IP 每分钟 1 条）→ 限流（全局读 50/s + 路由分区）。
// Unix：对端 uid 白名单（403）→ 前缀（404）→ 身份 = 本地（admin + estop），不限流。
// C#: Api/ApiHost.cs GateAsync + AddRateLimiter。线程安全（配置可热替换）。
#pragma once
#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "net/api_config.hpp"
#include "net/auth.hpp"
#include "net/cidr.hpp"
#include "net/http.hpp"
#include "net/rate_limit.hpp"
#include "net/router.hpp"

namespace reactor::net {

// 审计钩子：level = "info" / "warn" / "err"；标题和细节是给人看的中文。在 I/O 线程上调用，必须很快、不能阻塞。
using AuditFn = std::function<void(const std::string& level, const std::string& title, const std::string& detail)>;

// 接口运行统计（/device/comm 的 api 段）。C#: ApiErrors.cs ApiStats。全部原子，任意线程读。
struct ApiStats {
    std::atomic<int> clients{0};
    std::atomic<int64_t> dropped_telemetry_frames{0};
    std::atomic<int64_t> dropped_events{0};
    std::atomic<int64_t> frames_sent{0};
    std::atomic<int64_t> events_sent{0};
    std::atomic<int64_t> auth_failures{0};
    std::atomic<int64_t> requests{0};
};

struct GateOptions {
    std::string api_prefix = "/api/v1";
    std::vector<int64_t> unix_allowed_uids;   // 空 = 不限（开发机）；部署时填 reactor-hmi 的 uid
};

class Gate {
public:
    Gate(const ApiConfig& cfg, GateOptions opt, ApiStats* stats, AuditFn audit);

    struct Decision {
        std::optional<Response> reject;     // 有值 = 直接回这个响应，不进处理器
        bool close_connection = false;      // 回完断开（IP 不在白名单）
        std::shared_ptr<void> download_slot;   // Download 分区占用的并发许可；响应发完才释放
    };
    // 填好 req.auth / req.rate_class；match 为路由结果（未匹配 / 405 时 route 为空）
    Decision check(Request& req, const Router::Match& m, int64_t now_ms);

    // 热替换令牌 / 白名单 / 写限流（任意线程）。返回无效的 CIDR 条目。
    std::vector<std::string> update_config(const ApiConfig& cfg);
    bool token_name_valid(const std::string& name) const;   // 吊销检查（WS 连接用）
    std::vector<std::string> invalid_cidrs() const;
    size_t cidr_count() const;
    int write_rate_limit() const;

private:
    struct State {
        TokenTable tokens;
        CidrMatcher cidr;
        int write_rate;
        State(const ApiConfig& c) : tokens(c.tokens), cidr(c.allowed_cidrs), write_rate(c.write_rate_limit_per_sec) {}
    };
    std::shared_ptr<const State> state() const;
    Decision check_tcp(Request& req, const Router::Match& m, int64_t now_ms);
    Decision check_unix(Request& req, const Router::Match& m);
    bool prefix_ok(const std::string& path) const;

    GateOptions opt_;
    ApiStats* stats_;
    AuditFn audit_;
    mutable std::mutex mu_;
    std::shared_ptr<const State> st_;
    FailureLimiter auth_fail_{kAuthFailuresPerSec, 1000};
    KeyedThrottle auth_audit_{60 * 1000};
    FixedWindowLimiter read_{kReadRateLimitPerSec, 1000};
    FixedWindowLimiter write_{10, 1000};
    FixedWindowLimiter estop_{kEstopRateLimitPerSec, 1000};
    FixedWindowLimiter heartbeat_{kHeartbeatRateLimitPerSec, 1000};
    FixedWindowLimiter preflight_{kPreflightRateLimitPerSec, 1000};
    ConcurrencyLimiter downloads_{kDownloadConcurrency};
};

}  // namespace reactor::net
