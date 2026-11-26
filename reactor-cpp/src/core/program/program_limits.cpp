#include "core/program/program_limits.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

#include "core/program/step_fields.hpp"
#include "core/valve_frame.hpp"

namespace reactor::core::program::program_limits {

namespace {

std::string N(double v) { return fmt_n(v); }

std::optional<std::string> temp_reason(double t, const ProgramLimitSettings& l, double min) {
    if (!std::isfinite(t)) return std::string("目标温度无效");
    if (t < min) return "目标 " + N(t) + "℃ 低于 " + N(min) + "℃，无法启动加热";
    if (t > l.t_max_sp) return "目标 " + N(t) + "℃ 高于温度 SP 上限 " + N(l.t_max_sp) + "℃";
    if (t >= l.over_temp) return "目标 " + N(t) + "℃ 不低于超温报警值 " + N(l.over_temp) + "℃";
    return std::nullopt;
}

// 保持步骤的目标温度（不是加热给定值）：0 ≤ T ≤ TMaxSp 且 < OverTemp
std::optional<std::string> hold_temp_reason(double t, const ProgramLimitSettings& l) {
    if (t < 0) return "目标温度 " + N(t) + "℃ 无效";
    if (t > l.t_max_sp) return "目标 " + N(t) + "℃ 高于温度 SP 上限 " + N(l.t_max_sp) + "℃";
    if (t >= l.over_temp) return "目标 " + N(t) + "℃ 不低于超温报警值 " + N(l.over_temp) + "℃";
    return std::nullopt;
}

std::optional<std::string> press_reason(const FieldDef& f, double p, const ProgramLimitSettings& l, PressureUnit u) {
    if (p < f.min) return f.label + " " + format_p(p, u) + " 无效（须不低于 " + format_p(f.min, u) + "）";
    if (p > l.p_max_sp) return f.label + " " + format_p(p, u) + " 高于压力 SP 上限 " + format_p(l.p_max_sp, u);
    double cap = l.over_pressure * kPressRatio;
    if (p >= cap) return f.label + " " + format_p(p, u) + " 不低于超压报警值的 90%（" + format_p(cap, u) + "）";
    return std::nullopt;
}

std::string fmt_field(const FieldDef& f, double v, PressureUnit u) {
    if (f.pressure) return f.unit && *f.unit == "psi/h" ? step_specs::p_rate(v, u) : format_p(v, u);
    return N(v) + (f.unit && !f.unit->empty() ? " " + *f.unit : std::string());
}

bool is_gas_change(StepType t) {
    return t == StepType::Press || t == StepType::Leak || t == StepType::Exchange || t == StepType::Vent;
}

}  // namespace

double heat_temp_max(const ProgramLimitSettings& l) { return std::min(l.t_max_sp, l.over_temp - 1); }
double press_max(const ProgramLimitSettings& l) { return std::min(l.p_max_sp, l.over_pressure * kPressRatio - 0.1); }
double rpm_max_allowed(const ProgramLimitSettings& l) { return l.rpm_max; }

FieldRange field_range(StepType type, const std::string& key, double min, double max, const ProgramLimitSettings& l,
                       const Sibling& sibling) {
    double lo = min, hi = max;
    const StepSpec* spec = step_specs::find(type);
    const FieldDef* f = spec ? spec->field(key) : nullptr;
    switch (f ? f->limit : FieldLimit::None) {
        case FieldLimit::Temp:
            hi = std::min(hi, heat_temp_max(l));
            if (type == StepType::Heat) lo = std::max(lo, kHeatTempMin);
            break;
        case FieldLimit::Pressure: hi = std::min(hi, press_max(l)); break;
        case FieldLimit::Rpm: hi = std::min(hi, rpm_max_allowed(l)); break;
        default: break;
    }
    if (sibling) {
        if (type == StepType::Exchange && key == "release") hi = std::min(hi, sibling("fill") - 1);
        if (type == StepType::React && key == "minTime") hi = std::min(hi, sibling("time"));
    }
    if (hi < lo) hi = lo;
    return {lo, hi};
}

double clamp_field(StepType type, const std::string& key, double v, double min, double max, const ProgramLimitSettings& l) {
    FieldRange r = field_range(type, key, min, max, l);
    return std::isfinite(v) ? std::clamp(v, r.min, r.max) : r.min;
}

std::optional<std::string> check_step(const StepSnapshot& s, const ProgramLimitSettings& l, PressureUnit u) {
    if (is_legacy(s.type())) return "旧格式步骤「" + zh(s.type()) + "」须先转换（STEPS-SPEC §3）";
    const StepSpec* spec = step_specs::find(s.type());
    if (!spec) return "未知步骤类型「" + to_code(s.type()) + "」";
    bool curve = s.is_curve();
    for (const auto& f : spec->fields) {
        if (!step_specs::is_visible(s.type(), f, s.params())) continue;
        if (f.is_sel()) {
            std::string code = s.str(f.key, f.default_sel());
            auto c = f.code_of(code);
            if (!c || *c != code) return f.label + "「" + code + "」不是可选的值";
            continue;
        }
        double v = s.num(f.key, f.default_value.value_or(0));
        if (!std::isfinite(v)) return f.label + "无效";
        switch (f.limit) {
            case FieldLimit::Temp: {
                auto tw = s.type() == StepType::Heat ? temp_reason(v, l, kHeatTempMin) : hold_temp_reason(v, l);
                if (tw) return tw;
                continue;
            }
            case FieldLimit::Pressure: {
                auto pw = press_reason(f, v, l, u);
                if (pw) return pw;
                continue;
            }
            case FieldLimit::Rpm:
                if (v < 0) return "转速 " + N(v) + " rpm 无效";
                if (v > l.rpm_max) return "转速 " + N(v) + " rpm 高于搅拌转速上限 " + N(l.rpm_max) + " rpm";
                continue;
            default: break;
        }
        if (v < f.min || v > f.max)
            return f.label + " " + fmt_field(f, v, u) + " 超出 " + fmt_field(f, f.min, u) + "–" + fmt_field(f, f.max, u);
        if (f.integer && std::fabs(v - round_even(v)) > 1e-9) return f.label + " " + N(v) + " 须为整数";
    }
    if (curve) {
        const auto& segs = s.segments();
        if (segs.empty()) return std::string("升温曲线没有任何段");
        if (static_cast<int>(segs.size()) > kMaxCurveSegments) return "升温曲线最多 " + std::to_string(kMaxCurveSegments) + " 段";
        for (size_t i = 0; i < segs.size(); ++i) {
            const Segment& g = segs[i];
            std::string seg = "第 " + std::to_string(i + 1) + " 段";
            if (auto why = temp_reason(g.temp, l, 0)) return seg + *why;
            if (!std::isfinite(g.rate) || g.rate < 0 || g.rate > kRateMax)
                return seg + "斜率 " + N(g.rate) + "℃/min 超出 0–" + N(kRateMax) + "℃/min";
            if (!std::isfinite(g.hold) || g.hold < 0) return seg + "保温时长 " + N(g.hold) + " min 无效";
        }
    }
    if (s.type() == StepType::Exchange && s.num("release", 2.9) > s.num("fill", 72.5) - 1)
        return "「放到」" + format_p(s.num("release", 2.9), u) + " 须比「充到」" + format_p(s.num("fill", 72.5), u) + " 低 1 psi 以上";
    if (s.type() == StepType::React && s.num("minTime", 30) > s.num("time", 480))
        return "最短时间 " + N(s.num("minTime", 30)) + " min 不能超过最长时间 " + N(s.num("time", 480)) + " min";
    return std::nullopt;
}

std::vector<std::string> validate(const std::vector<StepSnapshot>& steps, const ProgramLimitSettings& l, PressureUnit u) {
    std::vector<std::string> errs;
    int i = 0;
    for (const auto& s : steps) {
        ++i;
        if (auto why = check_step(s, l, u)) errs.push_back("第 " + fmt_00(i) + " 步 " + s.name() + "：" + *why);
    }
    return errs;
}

double estimate_after_heat(double target_psig, double t_at_c, double t_after_c) {
    return (target_psig + leak_math::kAtmPsi) * (t_after_c + 273.15) / (t_at_c + 273.15) - leak_math::kAtmPsi;
}

std::optional<double> heat_target(const StepSnapshot& s) {
    if (s.is_curve()) {
        if (s.segments().empty()) return std::nullopt;
        return s.segments().back().temp;
    }
    return s.num("temp", 80);
}

PreRunResult pre_run(const std::vector<StepSnapshot>& steps, const ProgramLimitSettings& l, PressureUnit u) {
    PreRunResult r;
    r.errors = validate(steps, l, u);
    int ends = 0;
    for (const auto& s : steps)
        if (s.type() == StepType::End) ++ends;
    if (ends == 0) r.errors.push_back("程序没有「结束」步骤（必须是最后一步）");
    else if (ends > 1) r.errors.push_back("程序有 " + std::to_string(ends) + " 个「结束」步骤（只能有一个，且必须是最后一步）");
    else if (!steps.empty() && steps.back().type() != StepType::End) r.errors.push_back("「结束」必须是最后一步");

    std::optional<double> heat_before;
    for (size_t i = 0; i < steps.size(); ++i) {
        const auto& s = steps[i];
        switch (s.type()) {
            case StepType::Heat: heat_before = heat_target(s); break;
            case StepType::React: {
                // 吸气计量在充压完成后开始，遇到气密检查 / 置换 / 放空停止：往前找最近一个换气步骤，必须是充压
                const StepSnapshot* gas = nullptr;
                for (size_t j = 0; j < i; ++j)
                    if (is_gas_change(steps[j].type())) gas = &steps[j];
                if (!gas || gas->type() != StepType::Press)
                    r.warnings.push_back("第 " + fmt_00(static_cast<int>(i) + 1) + " 步 反应：前面没有充压，吸气量不计量（只按最长时间结束）");
                break;
            }
            case StepType::Press: {
                double target = s.num("pres", 290.1);
                double t_at = heat_before.value_or(kRoomTempC);
                double t_max = -std::numeric_limits<double>::infinity();
                for (size_t j = i + 1; j < steps.size(); ++j) {
                    const auto& n = steps[j];
                    if (is_gas_change(n.type()) || n.type() == StepType::End) break;
                    if (n.type() == StepType::Heat)
                        if (auto ht = heat_target(n)) t_max = std::max(t_max, *ht);
                }
                if (t_max > t_at) {
                    double est = estimate_after_heat(target, t_at, t_max);
                    std::string msg = "第 " + fmt_00(static_cast<int>(i) + 1) + " 步 充压在升温之前：升温后压力会高于目标（估算 " +
                                      format_p(est, u) + "），稳压不会放气";
                    double cap = press_max(l);
                    if (est > cap) r.errors.push_back(msg + "；超过压力上限 " + format_p(cap, u));
                    else r.warnings.push_back(msg);
                }
                break;
            }
            default: break;
        }
    }
    bool any_vent = false;
    for (const auto& s : steps)
        if (s.type() == StepType::Vent) any_vent = true;
    if (!steps.empty() && !any_vent) r.warnings.push_back("程序里没有「放空」：结束时釜里可能带压");
    return r;
}

}  // namespace reactor::core::program::program_limits
