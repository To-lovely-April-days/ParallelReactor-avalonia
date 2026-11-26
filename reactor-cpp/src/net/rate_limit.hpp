// 限流：按键（每令牌）的固定窗口计数器 + 并发许可（下载流）。C#: Api/ApiHost.cs AddRateLimiter（189–247）。
// 固定窗口、不排队、自动补充（FixedWindowRateLimiterOptions QueueLimit=0）。线程安全，时间由调用方传入。
#pragma once
#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>

namespace reactor::net {

// 路由的限流分区。C#: ApiHost 的 GlobalLimiter（读 50/s）+ 各 Policy
enum class RateClass {
    Auto,       // 按方法：GET/HEAD → Read，其余 → Write
    None,       // 不限（/health）—— 也不进全局 50/s
    Read,       // 只吃全局 50/s
    Write,      // 全局 50/s + 写 writeRateLimitPerSec/s
    Estop,      // 全局 + /estop 5/s（单独分区，不与写共用）
    Heartbeat,  // 全局 + /control/heartbeat 10/s
    Preflight,  // 全局 + /preflight 2/s
    Download,   // 全局 + 并发 2（429 DOWNLOAD_BUSY）
};

// C#: ApiHost 常量
inline constexpr int kReadRateLimitPerSec = 50;
inline constexpr int kEstopRateLimitPerSec = 5;
inline constexpr int kHeartbeatRateLimitPerSec = 10;
inline constexpr int kPreflightRateLimitPerSec = 2;
inline constexpr int kDownloadConcurrency = 2;
inline constexpr int kAuthFailuresPerSec = 10;

class FixedWindowLimiter {
public:
    FixedWindowLimiter(int permits, int64_t window_ms) : permits_(permits), window_ms_(window_ms) {}
    bool try_acquire(const std::string& key, int64_t now_ms);   // 放行则计数
    void set_permits(int p) { std::lock_guard<std::mutex> lk(mu_); permits_ = p; }

private:
    int permits_;
    int64_t window_ms_;
    std::mutex mu_;
    std::unordered_map<std::string, std::pair<int64_t, int>> m_;
};

// 全局并发许可（C#: AddConcurrencyLimiter(downloads)，不分令牌）。acquire 成功返回一个持有对象，析构即归还。
class ConcurrencyLimiter {
public:
    explicit ConcurrencyLimiter(int permits) : permits_(permits) {}
    std::shared_ptr<void> try_acquire();
    int in_use() const { return used_->load(); }

private:
    int permits_;
    std::shared_ptr<std::atomic<int>> used_ = std::make_shared<std::atomic<int>>(0);
};

}  // namespace reactor::net
