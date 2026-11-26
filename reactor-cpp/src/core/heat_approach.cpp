#include "core/heat_approach.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

#include "core/num_format.hpp"
#include <cstdint>
#include <string>
#include <utility>

namespace reactor::core {

using namespace heat_approach;

namespace {
std::string f1(double v) { return fmt_dec(v, 1, 1); }  // C#: "0.0"
std::string f0(double v) { return fmt_dec(v, 0, 0); }  // C#: "0"
}  // namespace

double HeatApproach::floor1(double x) { return std::floor(x * 10 + 1e-6) / 10; }

double HeatApproach::sp_to_write() const { return phase_ == ApproachPhase::Hold ? target_ : floor1(current_sp_); }

double HeatApproach::start(double target, double pv, int64_t now_ms, const ApproachParams& p) {
    phase_ = ApproachPhase::Off;
    hist_.clear();
    over_since_.reset();
    over_reported_ = false;
    exo_ = false;
    has_written_ = false;
    plan(target, pv, now_ms, p);
    return sp_to_write();
}

void HeatApproach::retarget(double target, double pv, int64_t now_ms, const ApproachParams& p) {
    if (!active()) return;
    over_since_.reset();
    over_reported_ = false;
    plan(target, pv, now_ms, p);
}

void HeatApproach::stop() {
    phase_ = ApproachPhase::Off;
    status_.clear();
    write_in_flight = false;
}

void HeatApproach::plan(double target, double pv, int64_t now_ms, const ApproachParams& p) {
    ApproachPhase prev = phase_;
    target_ = target;
    last_step_ = now_ms;

    // Off/Settle 以 PV 为基准；Creep/Hold 以当前 SP 为基准
    bool sp_anchored = prev == ApproachPhase::Creep || prev == ApproachPhase::Hold;
    double anchor = sp_anchored ? current_sp_ : pv;

    // 关闭逼近 / 往低改 / 已在目标之上：直接恒温
    if (p.delta <= 0 || target <= anchor + kPauseMargin || target <= pv) {
        enter_hold();
        return;
    }

    double mid = floor1(target - p.delta);
    if (mid > anchor + p.settle_band) {
        if (prev != ApproachPhase::Settle || std::fabs(mid - current_sp_) > 1e-6) {
            in_band_since_.reset();
            reached_at_.reset();
            peak_ = std::numeric_limits<double>::lowest();
        }
        current_sp_ = mid;
        phase_ = ApproachPhase::Settle;
        status_ = "预热至 " + f1(mid) + "℃";
        return;
    }

    // 离目标已在 Δ 以内：直接逼近
    if (!sp_anchored) current_sp_ = std::min(floor1(pv), target);
    phase_ = ApproachPhase::Creep;
    status_ = "逼近中 " + f1(current_sp_) + " → " + f1(target) + "℃";
}

void HeatApproach::enter_creep(double pv) {
    // 从 max(中间SP, 当前PV) 起步
    current_sp_ = std::min(std::max(current_sp_, pv), target_);
    if (current_sp_ >= target_ - 1e-9) { enter_hold(); return; }
    phase_ = ApproachPhase::Creep;
    status_ = "逼近中 " + f1(current_sp_) + " → " + f1(target_) + "℃";
}

void HeatApproach::enter_hold() {
    current_sp_ = target_;
    phase_ = ApproachPhase::Hold;
    status_ = "恒温 " + f1(target_) + "℃";
}

ApproachEvent HeatApproach::step(int64_t now_ms, double pv, double out_pct, const ApproachParams& p) {
    if (!active()) return ApproachEvent::None;

    hist_.emplace_back(now_ms, pv);
    hist_.erase(std::remove_if(hist_.begin(), hist_.end(),
                               [&](const std::pair<int64_t, double>& h) { return now_ms - h.first > kSlopeWindowMs; }),
                hist_.end());

    int64_t dt = now_ms - last_step_;
    if (dt > kMaxStepGapMs) dt = kMaxStepGapMs;
    if (dt < 0) dt = 0;
    last_step_ = now_ms;
    double dt_min = static_cast<double>(dt) / 60000.0;

    bool heater_off = out_pct <= kExoOutPct;
    ApproachEvent evt = ApproachEvent::None;

    // 超限监测（各阶段通用）
    if (pv > target_ + kOverLimit && heater_off) {
        if (!over_since_) over_since_ = now_ms;
        if (!over_reported_ && now_ms - *over_since_ >= kOverLimitHoldMs) {
            over_reported_ = true;
            evt = ApproachEvent::OverLimit;
        }
    } else {
        over_since_.reset();
        if (pv <= target_) over_reported_ = false;
    }

    switch (phase_) {
        case ApproachPhase::Settle: {
            peak_ = std::max(peak_, pv);
            if (!reached_at_ && pv >= current_sp_ - p.settle_band) reached_at_ = now_ms;

            if (std::fabs(pv - current_sp_) <= p.settle_band) {
                if (!in_band_since_) in_band_since_ = now_ms;
            } else {
                in_band_since_.reset();
            }

            double stable_sec = in_band_since_ ? static_cast<double>(now_ms - *in_band_since_) / 1000.0 : 0;
            // 判峰：已到中间 SP 附近 ≥30 s、且最近 60 s 不再上升
            bool peaked = reached_at_ && static_cast<double>(now_ms - *reached_at_) / 1000.0 >= 30 &&
                          slope_per_min() <= kPeakSlope;
            if (stable_sec >= p.settle_sec || peaked) {
                preheat_overshoot_ = std::max(0.0, peak_ - current_sp_);
                enter_creep(pv);
                if (evt == ApproachEvent::None) evt = ApproachEvent::PreheatDone;
            } else if (in_band_since_) {
                status_ = "预热至 " + f1(current_sp_) + "℃ · 稳定中 " + f0(stable_sec) + "/" + f0(p.settle_sec) + "s";
            } else if (heater_off && pv > current_sp_ + p.settle_band) {
                status_ = "预热至 " + f1(current_sp_) + "℃ · 输出已关，等待回落";
            } else {
                status_ = "预热至 " + f1(current_sp_) + "℃";
            }
            break;
        }

        case ApproachPhase::Creep: {
            bool exo = heater_off && slope_per_min() >= kExoRisePerMin;
            if (exo && !exo_ && evt == ApproachEvent::None) evt = ApproachEvent::ExoPause;
            exo_ = exo;

            // 末段：PV 或 SP 任一已进入 目标−FinalZone 以内
            bool fin = target_ - std::max(current_sp_, pv) <= p.final_zone;

            if (exo) {
                status_ = "放热中 · 暂停上推（SP " + f1(current_sp_) + "℃）";
                break;
            }

            // 快段：PV 领先就把 SP 跟到 PV（不进末段）
            if (!fin && pv > current_sp_) current_sp_ = std::min(pv, target_ - p.final_zone);

            if (fin && pv > current_sp_ + kPauseMargin) {
                if (slope_per_min() > kPeakSlope) {
                    status_ = "末段 · PV 偏高 · 暂停上推（SP " + f1(current_sp_) + "℃）";
                } else {
                    current_sp_ = std::min(pv, target_);
                    if (current_sp_ >= target_ - 1e-9) enter_hold();
                    else status_ = "末段慢推 " + f1(current_sp_) + " → " + f1(target_) + "℃";
                }
            } else if (current_sp_ - pv > kMaxLead) {
                status_ = "等待 PV 跟上（SP " + f1(current_sp_) + "℃）";
            } else {
                double rate = fin ? p.rate : p.fast_rate;
                current_sp_ = std::min(target_, current_sp_ + rate * dt_min);
                if (current_sp_ >= target_ - 1e-9) enter_hold();
                else status_ = std::string(fin ? "末段慢推" : "快速逼近") + " " + f1(current_sp_) + " → " + f1(target_) + "℃";
            }
            break;
        }

        case ApproachPhase::Hold:
            status_ = heater_off && pv > target_ + kOverLimit ? "恒温 " + f1(target_) + "℃ · 放热超出控温能力"
                                                              : "恒温 " + f1(target_) + "℃";
            break;

        case ApproachPhase::Off: break;
    }
    return evt;
}

double HeatApproach::slope_per_min() const {
    if (hist_.size() < 3) return 0;
    int64_t t0 = hist_.front().first;
    if (static_cast<double>(hist_.back().first - t0) / 1000.0 < 30) return 0;
    double n = static_cast<double>(hist_.size()), sx = 0, sy = 0, sxx = 0, sxy = 0;
    for (auto& h : hist_) {
        double x = static_cast<double>(h.first - t0) / 60000.0;
        sx += x;
        sy += h.second;
        sxx += x * x;
        sxy += x * h.second;
    }
    double den = n * sxx - sx * sx;
    return den <= 1e-12 ? 0 : (n * sxy - sx * sy) / den;
}

bool HeatApproach::should_write(int64_t now_ms) const {
    if (!active() || write_in_flight) return false;
    if (!has_written_) return true;
    if (std::fabs(sp_to_write() - last_written_) >= 0.05) return true;
    return now_ms - last_write_ms_ >= kForceRewriteMs;
}

void HeatApproach::mark_written(double sp, int64_t now_ms) {
    last_written_ = sp;
    has_written_ = true;
    last_write_ms_ = now_ms;
}

}  // namespace reactor::core
