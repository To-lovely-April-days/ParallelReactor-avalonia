// 搅拌业务服务（DM2C，全机一台）。C#: Service/StirController.cs + MainViewModel 的搅拌部分
//   - 开：复位报警 → 速度模式触发（釜转速 × 传动比 = 电机转速，限幅 1000）；改速只在开着时重发速度路径；停：0x6002=0x40；
//   - 故障轮询：控制周期每 3 次读一次报警码 + 母线电压（C# _stirFaultTick % 3）；
//   - 报警码跳变（C# StirFaultWatch）：0 → 非 0 / 换码 = Raise，回到 0 = Clear，由核心登记报警 stir:fault；
//   - 通讯统计逐笔计（C# 口径）；通讯错误节流 5 s 上报一次（on_comm_error）。
// 线程：只在控制线程调用；回调在控制线程上执行。
#pragma once
#include <cstdint>
#include <functional>
#include <optional>
#include <string>

#include "ctl/ctl_io.hpp"
#include "hw/comm_stats.hpp"

namespace reactor::ctl {

class StirService {
public:
    static constexpr int kFaultPollEvery = 3;
    static constexpr int64_t kCommReportGapMs = 5000;
    using DoneFn = std::function<void(bool ok)>;

    explicit StirService(const CoreIo& io) : io_(io) {}

    bool configured() const { return io_.hw->stir_configured(); }
    bool online() const { return stats_.online(); }
    const hw::CommStats& stats() const { return stats_; }
    bool on() const { return on_; }
    int rpm() const { return rpm_; }
    int ramp_ms() const { return ramp_ms_; }
    double gear_ratio() const { return gear_ratio_; }
    int to_motor(int vessel_rpm) const { return hw::stir_gear::to_motor(vessel_rpm, gear_ratio_); }
    double motor_current_a() const { return motor_current_; }
    double bus_voltage_v() const { return bus_voltage_; }
    std::optional<int> alarm_code() const { return alarm_code_; }
    const std::string& fault_text() const { return fault_text_; }

    void set_ramp_ms(int v) { ramp_ms_ = v; }
    void set_gear_ratio(double r) { gear_ratio_ = r; }
    void set_rpm_value(int rpm) { rpm_ = rpm; }  // 只改记录的转速（关着时）

    // 开机：读峰值电流（显示用）、报警码、细分（回填设置）
    void init(std::function<void(int microstep)> on_microstep);
    // guard_epoch：全部停止之后不再把启动帧写下去
    void start(int rpm, uint64_t guard_epoch, DoneFn cb);
    void stop(DoneFn cb);
    void set_rpm(int rpm, uint64_t guard_epoch, DoneFn cb);
    void clear_alarm(DoneFn cb);
    // 控制周期调用：每 3 次读一次报警码 + 母线电压
    void tick_poll();
    // 全部停止的快速路径已发停车帧：记录为关
    void mark_stopped() { on_ = false; }

    // DM2C 报警码 → 中文（C# StirFaultName）
    static std::string fault_name(int code);

    std::function<void(int code)> on_alarm_code;            // 每读到一次报警码（核心做跳变判定）
    std::function<void(const std::string& msg)> on_comm_error;

private:
    void track(bool ok, const std::string& err, bool report);

    CoreIo io_;
    hw::CommStats stats_;
    bool on_ = false;
    int rpm_ = 200;          // C# MainViewModel._stirRpm 初值 200
    int ramp_ms_ = 1000;     // C# StirController.RampMs 默认 1000（由设置 StirRampMs 覆盖）
    double gear_ratio_ = hw::stir_gear::kDefaultRatio;
    double motor_current_ = 0, bus_voltage_ = 0;
    std::optional<int> alarm_code_;
    std::string fault_text_;
    int fault_tick_ = 0;
    int64_t last_report_ = INT64_MIN / 2;
};

}  // namespace reactor::ctl
