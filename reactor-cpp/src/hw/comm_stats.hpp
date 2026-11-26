// 单个通讯模块的累计统计：事务次数、失败次数、最近一次成功时刻、连续失败次数（在线判定）。
// C#: Service/CommStats.cs（polls / fails / lastOkAt）+ 各 Service 里的 `_fails`（初值 3 = 离线，连续 3 次失败 = 离线）。
//
// 线程：全部原子操作，任何线程都可以读写。
#pragma once
#include <atomic>
#include <cstdint>

namespace reactor::hw {

class CommStats {
public:
    static constexpr int kOfflineAfter = 3;  // C#: `Online => _fails < 3`

    // 记一次成功（响应到达时刻，UNIX 毫秒）。
    void ok(int64_t wall_ms) {
        polls_.fetch_add(1, std::memory_order_relaxed);
        last_ok_ms_.store(wall_ms, std::memory_order_relaxed);
        consecutive_fails_.store(0, std::memory_order_relaxed);
    }
    // 记一次失败。
    void fail() {
        polls_.fetch_add(1, std::memory_order_relaxed);
        fails_.fetch_add(1, std::memory_order_relaxed);
        int c = consecutive_fails_.load(std::memory_order_relaxed);
        while (c < kOfflineAfter && !consecutive_fails_.compare_exchange_weak(c, c + 1, std::memory_order_relaxed)) {
        }
    }

    int64_t polls() const { return polls_.load(std::memory_order_relaxed); }        // 累计事务数（含失败）
    int64_t fails() const { return fails_.load(std::memory_order_relaxed); }        // 累计失败数
    int64_t last_ok_ms() const { return last_ok_ms_.load(std::memory_order_relaxed); }  // 0 = 尚无成功
    int consecutive_fails() const { return consecutive_fails_.load(std::memory_order_relaxed); }  // 封顶 3
    // 在线：连续失败 < 3。初始为离线，首次成功才算在线（同 C# `_fails = 3` 初值）。
    bool online() const { return consecutive_fails() < kOfflineAfter; }

private:
    std::atomic<int64_t> polls_{0};
    std::atomic<int64_t> fails_{0};
    std::atomic<int64_t> last_ok_ms_{0};
    std::atomic<int> consecutive_fails_{kOfflineAfter};
};

}  // namespace reactor::hw
