// 压力业务服务（8AI，8 路变送器，psi 表压）。C#: Service/PressureService.cs
//   - 每个控制周期读一次（skip_if_busy：上一次还没回来就跳过、不计失败）；序列 / 泄漏测试的即时读用 read_now（不跳过）；
//   - 原码 → psi 用当前满量程（设置页可改，运行时生效）；最近一帧整体替换（原码 + psi + 响应时刻同属一次读取）；
//   - 每次读都计入 CommStats（C# 口径），连续 3 次失败 = 离线，离线时读数无效。
// 线程：只在控制线程调用；回调在控制线程上执行。
#pragma once
#include <array>
#include <cstdint>
#include <functional>

#include "ctl/ctl_io.hpp"
#include "hw/comm_stats.hpp"

namespace reactor::ctl {

struct PressureSample {
    std::array<uint16_t, 8> raw{};
    std::array<double, 8> psi{};
    int64_t at_wall_ms = 0;  // 0 = 尚未读到
    bool valid() const { return at_wall_ms != 0; }
};

class PressureService {
public:
    PressureService(const CoreIo& io, double fullscale_psi, hw::PressSignal sig)
        : io_(io), fullscale_psi_(fullscale_psi), sig_(sig) {}

    bool configured() const { return io_.hw->press_configured(); }
    bool online() const { return stats_.online(); }
    const hw::CommStats& stats() const { return stats_; }
    const PressureSample& last() const { return last_; }
    double fullscale_psi() const { return fullscale_psi_; }
    void set_fullscale_psi(double v) { fullscale_psi_ = v; }

    // 周期轮询（skip_if_busy）
    void poll() { read(true, nullptr); }
    // 即时读一次（不跳过；第二阶段的气路序列 / 泄漏测试 / StepRunner fresh_pressure 用）
    void read_now(std::function<void(bool ok)> cb) { read(false, std::move(cb)); }

private:
    void read(bool skip_if_busy, std::function<void(bool)> cb);

    CoreIo io_;
    double fullscale_psi_;
    hw::PressSignal sig_;
    hw::CommStats stats_;
    PressureSample last_;
    int in_flight_ = 0;
};

}  // namespace reactor::ctl
