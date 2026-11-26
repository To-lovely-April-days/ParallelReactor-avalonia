// 超压联锁判定（纯逻辑）+ 单个执行器的「下发 → 确认 → 重发 / 复发」状态。
// C#: Models/OverPressureGuard.cs（ActuatorRetry、OverPressureActions、OverPressureGuard）
//  - 压力在线且 P ≥ 限值的上升沿：Trip，同时下发关阀（该通道 SV + 三路气源，排空保持）与停加热（heat_enabled 时）；
//  - 阀门「已确认」= 写入回显正常 且 界面阀态里该 SV 与三路气源均为关；之后又显示打开 → 复关；
//  - 加热「已确认」= 回读 At=4；回读未知时以写入回显为准；之后回读 At≠4 → 复切；
//  - 未确认时各执行器每 5 s 最多重发一帧，在途时不发；
//  - 离线或 NaN：不触发、不下发、不解除；
//  - P < 限值 × 0.98：Cleared，重新布防（通道状态由调用方保持报警，需人工复位）。
#pragma once
#include <cstdint>
#include <optional>
#include <vector>

#include "core/over_temp_guard.hpp"

namespace reactor::core {

enum class ActuatorStep {
    None,       // 无动作
    Retry,      // 未确认 → 重发
    Confirmed,  // 刚确认（只在跳变时出现一次）
    Reopened,   // 已确认后又被打开 → 重发
};

class ActuatorRetry {
public:
    bool in_flight() const { return in_flight_; }
    bool acked() const { return acked_; }
    bool confirmed() const { return confirmed_; }
    int sends() const { return sends_; }
    // 当前在途写入序号（每次下发 +1，reset 也 +1）：结果回来时序号不符即为过期结果
    int seq() const { return seq_; }

    void reset();
    // 触发时立即下发（不受重发间隔限制）
    void send_now(int64_t now_ms);
    // done = 执行器是否已处于安全态（回读），未知 nullopt
    ActuatorStep step(std::optional<bool> done, int64_t now_ms);
    // 写入结果回来；active=false（已解除）时只清在途标记；seq 不符时整体忽略（nullopt = 不校验）
    void on_result(bool ok, bool active, std::optional<int> seq = std::nullopt);

private:
    bool in_flight_ = false, acked_ = false, confirmed_ = false, reopen_pending_ = false;
    int sends_ = 0;
    int seq_ = 0;
    int64_t last_send_ = over_temp::kNeverMs;
};

// 超压联锁在某个 Tick 对某通道的判定（可同时有多项）
struct OverPressureActions {
    bool trip = false;           // 上升沿：置报警 + 中止气路 + 关阀 + 停加热
    bool cleared = false;        // 回落到 限值 × 98% 以下 → 解除
    bool send_valves = false;    // 本 Tick 需下发关阀（触发 / 重发 / 复关）
    bool send_heat = false;      // 本 Tick 需下发 At=4
    bool valves_retry = false, heat_retry = false;
    bool valves_confirmed = false, heat_confirmed = false;
    bool valves_reopened = false, heat_reopened = false;
    bool any() const { return trip || cleared || send_valves || send_heat || valves_confirmed || heat_confirmed; }
};

namespace over_pressure {
constexpr double kRearmRatio = 0.98;  // C#: OverPressureGuard.RearmRatio
}

class OverPressureGuard {
public:
    explicit OverPressureGuard(int channels = 8);
    int count() const { return static_cast<int>(tripped_.size()); }
    bool is_tripped(int ch) const { return tripped_[static_cast<size_t>(ch)]; }
    bool is_valves_confirmed(int ch) const { return valves_[static_cast<size_t>(ch)].confirmed(); }
    bool is_heat_confirmed(int ch) const { return heat_[static_cast<size_t>(ch)].confirmed(); }
    bool valves_in_flight(int ch) const { return valves_[static_cast<size_t>(ch)].in_flight(); }
    bool heat_in_flight(int ch) const { return heat_[static_cast<size_t>(ch)].in_flight(); }
    // 调用方在 evaluate 返回 send_x 后立即读取序号，结果回来时原样传回 on_x_result
    int valve_seq(int ch) const { return valves_[static_cast<size_t>(ch)].seq(); }
    int heat_seq(int ch) const { return heat_[static_cast<size_t>(ch)].seq(); }
    int valve_sends(int ch) const { return valves_[static_cast<size_t>(ch)].sends(); }
    int heat_sends(int ch) const { return heat_[static_cast<size_t>(ch)].sends(); }

    // valves_closed_shown：界面（已确认写入）阀态里该 SV 与三路气源是否全关；valves_enabled：阀门模块已配置；
    // heat_stopped：回读是否 At=4（未知 nullopt）；heat_enabled：超压时停止加热（设置开关 且 温控已配置）
    OverPressureActions evaluate(int ch, double p, bool online, double limit, int64_t now_ms, bool valves_closed_shown,
                                 bool valves_enabled, std::optional<bool> heat_stopped, bool heat_enabled);
    void on_valve_result(int ch, bool ok, std::optional<int> seq = std::nullopt);
    void on_heat_result(int ch, bool ok, std::optional<int> seq = std::nullopt);

private:
    std::vector<bool> tripped_;
    std::vector<ActuatorRetry> valves_, heat_;
};

}  // namespace reactor::core
