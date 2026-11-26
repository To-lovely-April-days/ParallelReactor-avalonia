// 控制循环 Tick 的防重入与超时统计。C#: Service/TickGate.cs（逐行照抄语义）
// 单一控制线程上 Tick 本身不会重入；门闸保留下来做统计（/device/comm 的 tick 段）和防御（测试直接调 tick()）。
// 线程：只在控制线程用。
#pragma once
#include <cstdint>

namespace reactor::ctl {

class TickGate {
public:
    explicit TickGate(int64_t interval_ms, int64_t log_every_ms = 60000)
        : interval_ms_(interval_ms), log_every_ms_(log_every_ms) {}

    int64_t interval_ms() const { return interval_ms_; }
    int64_t entered() const { return entered_; }
    int64_t skipped() const { return skipped_; }
    int64_t overruns() const { return overruns_; }
    double last_took_ms() const { return last_took_; }
    double max_took_ms() const { return max_took_; }
    double last_overrun_ms() const { return last_overrun_; }
    bool inside() const { return inside_; }

    // 已在 Tick 内时返回 false 并累加 skipped
    bool try_enter() {
        if (inside_) { ++skipped_; return false; }
        inside_ = true;
        ++entered_;
        return true;
    }
    // 记录耗时并离开。返回 true = 本次超时且距上次记录 ≥ log_every（调用方据此写一条事件）
    bool exit(double took_ms, int64_t now_mono_ms) {
        inside_ = false;
        last_took_ = took_ms;
        if (took_ms > max_took_) max_took_ = took_ms;
        if (took_ms <= static_cast<double>(interval_ms_)) return false;
        ++overruns_;
        last_overrun_ = took_ms;
        if (logged_once_ && now_mono_ms - last_logged_ < log_every_ms_) return false;
        logged_once_ = true;
        last_logged_ = now_mono_ms;
        return true;
    }

private:
    int64_t interval_ms_, log_every_ms_;
    bool inside_ = false;
    int64_t entered_ = 0, skipped_ = 0, overruns_ = 0;
    double last_took_ = 0, max_took_ = 0, last_overrun_ = 0;
    bool logged_once_ = false;
    int64_t last_logged_ = 0;
};

}  // namespace reactor::ctl
