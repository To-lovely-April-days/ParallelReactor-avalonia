// 控制核心的时钟抽象：生产用系统时钟，测试用可手动推进的时钟（定时器、超时、计量全部按它走）。
// 线程：两个实现都可以被任何线程读；ManualClock 的 set/advance 只由测试线程调。
#pragma once
#include <atomic>
#include <cstdint>

namespace reactor::ctl {

class Clock {
public:
    virtual ~Clock() = default;
    virtual int64_t mono_ms() const = 0;  // 单调毫秒（节拍、超时、计量）
    virtual int64_t wall_ms() const = 0;  // UNIX 毫秒 UTC（事件、快照时间戳）
};

class SystemClock : public Clock {
public:
    int64_t mono_ms() const override;
    int64_t wall_ms() const override;
};

// 测试用：初值 mono = 1'000'000、wall = 2026-11-26T00:00:00Z，两者同步推进。
class ManualClock : public Clock {
public:
    ManualClock() = default;
    int64_t mono_ms() const override { return mono_.load(); }
    int64_t wall_ms() const override { return wall_.load(); }
    void advance(int64_t ms) { mono_ += ms; wall_ += ms; }
    void set_mono(int64_t ms) { int64_t d = ms - mono_.load(); advance(d); }

private:
    std::atomic<int64_t> mono_{1000000};
    std::atomic<int64_t> wall_{1795651200000};  // 2026-11-26T00:00:00Z
};

}  // namespace reactor::ctl
