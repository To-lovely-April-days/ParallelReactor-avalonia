// 定值加热防超调逼近（每个反应釜一份，控制线程周期推进）。C#: Models/HeatApproach.cs
// 不改 PID，只控制下发的 SP：先写 目标−Δ 让它冲，PV 过峰后从当前 PV 起步，远处快推、末段 FinalZone 内慢推。
// 无冷却手段：末段 PV 高于 SP、SP 超前 PV 过多、或判定放热时暂停上推；放热超出控温能力时只能提示。
// 时刻一律为调用方传入的毫秒（C# 用 DateTime.UtcNow；这里建议传单调毫秒）。
#pragma once
#include <cstdint>
#include <deque>
#include <optional>
#include <string>
#include <utility>

namespace reactor::core {

// 可调参数（C#: ApproachParams，来自设置；默认值见 AppSettings：Δ 5、末段 0.2 ℃/min、判稳 ±0.2 ℃ / 90 s、快段 1.0 ℃/min、末段范围 1.0 ℃）
struct ApproachParams {
    double delta = 5;         // 预留温差 Δ ℃；≤0 关闭逼近（直接写目标）
    double rate = 0.2;        // 末段慢推速率 ℃/min
    double settle_band = 0.2; // 判稳范围 ±℃
    double settle_sec = 90;   // 判稳时长 s
    double fast_rate = 1.0;   // 快段逼近速率 ℃/min
    double final_zone = 1.0;  // 末段范围 ℃
};

enum class ApproachPhase {
    Off,     // 未运行
    Settle,  // SP = 目标−Δ，冲到安全位置并等待稳定
    Creep,   // 从中间 SP 小步推到目标
    Hold,    // SP = 目标，恒温
};

enum class ApproachEvent {
    None,
    PreheatDone,  // 预热段结束，可读 preheat_overshoot()
    ExoPause,     // 逼近中检测到放热 → 暂停上推
    OverLimit,    // PV > 目标+0.1 且输出≈0 持续 10 s（无冷却，软件已无法压回）
};

namespace heat_approach {
constexpr double kPauseMargin = 0.05;     // PV 高于当前 SP 超过此值 → 暂停上推
constexpr double kMaxLead = 0.5;          // SP 超前 PV 超过此值 → 暂停上推
constexpr double kPeakSlope = 0.02;       // 预热段斜率 ≤ 此值（℃/min）视为已过峰
constexpr double kExoOutPct = 2.0;        // 输出 ≤ 此值视为加热已关
constexpr double kExoRisePerMin = 0.05;   // 输出已关仍以 ≥ 此斜率上升 → 放热
constexpr double kOverLimit = 0.1;        // 不允许超过 目标 + 此值
constexpr int64_t kOverLimitHoldMs = 10000;
constexpr int64_t kSlopeWindowMs = 60000;
constexpr int64_t kMaxStepGapMs = 5000;   // 单周期最多推进 5 s
constexpr int64_t kForceRewriteMs = 30000;
}  // namespace heat_approach

class HeatApproach {
public:
    ApproachPhase phase() const { return phase_; }
    bool active() const { return phase_ != ApproachPhase::Off; }
    const std::string& status_text() const { return status_; }
    double target() const { return target_; }
    double current_sp() const { return current_sp_; }
    // 应下发的 SP（0.1 ℃ 分辨率，向下取整；恒温时 = 目标）
    double sp_to_write() const;
    // 串口写入进行中（调用方置位 / 清除）
    bool write_in_flight = false;
    // 最近一次预热段实测冲温（峰值 − 中间 SP）
    double preheat_overshoot() const { return preheat_overshoot_; }

    // 启动加热：按当前 PV 规划，返回首个应写入的 SP
    double start(double target, double pv, int64_t now_ms, const ApproachParams& p);
    // 加热中改目标（未运行时忽略）
    void retarget(double target, double pv, int64_t now_ms, const ApproachParams& p);
    void stop();
    // 每个轮询周期推进一次；out_pct = 仪表输出 0..100%
    ApproachEvent step(int64_t now_ms, double pv, double out_pct, const ApproachParams& p);
    // 是否需要下发：SP 变化 ≥0.05 ℃，或距上次成功下发 ≥30 s
    bool should_write(int64_t now_ms) const;
    // 记录一次成功下发（失败不调用）
    void mark_written(double sp, int64_t now_ms);

private:
    void plan(double target, double pv, int64_t now_ms, const ApproachParams& p);
    void enter_creep(double pv);
    void enter_hold();
    double slope_per_min() const;
    static double floor1(double x);

    ApproachPhase phase_ = ApproachPhase::Off;
    std::string status_;
    double target_ = 0;
    double current_sp_ = 0;
    double preheat_overshoot_ = 0;
    std::deque<std::pair<int64_t, double>> hist_;
    std::optional<int64_t> in_band_since_;
    std::optional<int64_t> reached_at_;
    double peak_ = 0;
    std::optional<int64_t> over_since_;
    bool over_reported_ = false;
    bool exo_ = false;
    int64_t last_step_ = 0;
    double last_written_ = 0;
    bool has_written_ = false;
    int64_t last_write_ms_ = 0;
};

}  // namespace reactor::core
