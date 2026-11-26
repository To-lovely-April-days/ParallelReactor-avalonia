#include "core/valve_frame.hpp"

#include <algorithm>

namespace reactor::core {

namespace valve_frame {

ValveFrame all_closed() {
    ValveFrame f;
    f.fill(false);
    return f;
}

namespace {
ValveFrame with_sv(const std::vector<int>& rv_ids) {
    ValveFrame f = all_closed();
    for (int id : rv_ids)
        if (id >= 1 && id <= 8) f[static_cast<size_t>(id - 1)] = true;
    return f;
}
// 下标 8..11（三路气源 + 排空）
bool source(const ValveFrame& f, int i) { return f[static_cast<size_t>(i)]; }
}  // namespace

ValveFrame gas(int g, const std::vector<int>& rv_ids) {
    ValveFrame f = with_sv(rv_ids);
    f[static_cast<size_t>(kGasBase + std::clamp(g, 0, 2))] = true;
    return f;
}

ValveFrame vent_only(const std::vector<int>& rv_ids) {
    ValveFrame f = with_sv(rv_ids);
    f[kVent] = true;
    return f;
}

bool is_safe(const ValveFrame& f) {
    int n = 0;
    for (int i = kGasBase; i < kGasBase + 3; i++)
        if (f[static_cast<size_t>(i)]) n++;
    return n <= 1 && !(n == 1 && f[kVent]);
}

std::optional<std::string> check_apply(const ValveFrame& f, bool lease_busy, const std::vector<int>& participants,
                                       const std::string& lease_task) {
    if (!is_safe(f)) return std::string("阀态不安全（多路气源同开，或气源与排空同开），已拒绝下发");
    if (lease_busy)
        for (int i = 0; i < 8; i++)
            if (f[static_cast<size_t>(i)] &&
                std::find(participants.begin(), participants.end(), i + 1) == participants.end())
                return "阀态越权：SV" + std::to_string(i + 1) + " 不属于「" + lease_task + "」的参与通道，已拒绝下发";
    return std::nullopt;
}

bool needs_dead_time(const ValveFrame& prev, const ValveFrame& next) {
    bool prev_any = false;
    for (int i = kGasBase; i <= kVent; i++) prev_any = prev_any || source(prev, i);
    if (!prev_any) return false;
    for (int i = kGasBase; i <= kVent; i++)
        if (source(next, i) && !source(prev, i)) return true;
    return false;
}

ValveFrame close_all(bool keep_vent, bool vent_on) {
    ValveFrame f = all_closed();
    if (keep_vent) f[kVent] = vent_on;
    return f;
}

std::string valve_name(int i) {
    if (i < 8) return "SV" + std::to_string(i + 1);
    if (i == kGasBase) return "惰性气体";
    if (i == kGasBase + 1) return "气体A";
    if (i == kGasBase + 2) return "气体B";
    return "排空";
}

std::optional<std::string> change_text(const ValveFrame& prev, const ValveFrame& next, const std::string& gas_task) {
    std::string diff, open;
    for (int i = 0; i < kCount; i++) {
        size_t k = static_cast<size_t>(i);
        if (prev[k] != next[k]) {
            if (!diff.empty()) diff += " ";
            diff += valve_name(i) + (next[k] ? "开" : "关");
        }
        if (next[k]) {
            if (!open.empty()) open += "、";
            open += valve_name(i);
        }
    }
    if (diff.empty()) return std::nullopt;
    std::string s = diff + " · 当前开启：" + (open.empty() ? std::string("无") : open);
    if (!gas_task.empty()) s += " · " + gas_task;
    return s;
}

}  // namespace valve_frame

namespace purge_plan {

int gas_index(const std::string& label) {
    if (label == "气体 A") return 1;
    if (label == "气体 B") return 2;
    return 0;
}

std::vector<PurgeAction> build(int gas, int cycles, double fill_sec, double vent_sec) {
    // 先全关一个死区：开始前若有阀开着（如排空），不与第一帧充气在切换瞬间重叠
    std::vector<PurgeAction> list{{1, "准备", valve_frame::all_closed(), kDeadSec}};
    for (int c = 1; c <= cycles; c++) {
        list.push_back({c, "充气", valve_frame::gas(gas), fill_sec});
        list.push_back({c, "切换", valve_frame::all_closed(), kDeadSec});
        list.push_back({c, "排气", valve_frame::vent_only(), vent_sec});
        list.push_back({c, "切换", valve_frame::all_closed(), kDeadSec});
    }
    return list;
}

}  // namespace purge_plan

namespace leak_math {

double compensate(double psig, double t_c, double ref_c) {
    return (psig + kAtmPsi) * (ref_c + 273.15) / (t_c + 273.15) - kAtmPsi;
}

double drop_rate_per_hour(const std::vector<std::pair<double, double>>& s) {
    size_t n = s.size();
    if (n < 2) return 0;
    double mx = 0, my = 0;
    for (auto& p : s) { mx += p.first; my += p.second; }
    mx /= static_cast<double>(n);
    my /= static_cast<double>(n);
    double sxx = 0, sxy = 0;  // 去均值后累加，避免大数相减丢精度
    for (auto& p : s) {
        sxx += (p.first - mx) * (p.first - mx);
        sxy += (p.first - mx) * (p.second - my);
    }
    if (sxx < 1e-12) return 0;
    return -sxy / sxx * 60.0;
}

}  // namespace leak_math

}  // namespace reactor::core
