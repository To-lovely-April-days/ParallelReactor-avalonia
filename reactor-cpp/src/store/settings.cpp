#include "store/settings.hpp"

#include <cmath>
#include <ctime>
#include <limits>
#include <stdexcept>

#include "common/clock.hpp"
#include "store/csv.hpp"
#include "store/store_util.hpp"
#include <array>
#include <cstdint>
#include <iterator>
#include <optional>
#include <string>
#include <vector>

namespace reactor::store {

using nlohmann::json;

// ============================ 默认值 ============================

Pid default_band_pid(int band) {
    return Pid{40.0 + 20.0 * band, 100.0 + 25.0 * band, 15.0 + 5.0 * band};
}

std::array<BandRange, kBandCount> band_ranges(const std::array<double, 2>& b) {
    return {BandRange{0, b[0]}, BandRange{b[0], b[1]}, BandRange{b[1], 400}};
}

SettingsState::SettingsState() {
    for (auto& row : band_pids)
        for (int b = 0; b < kBandCount; ++b) row[static_cast<size_t>(b)] = default_band_pid(b);
}

// ============================ JSON ============================

namespace {

// AppSettings 的数值字段（按 C# 属性声明顺序；夹在中间的非 double 字段在写的时候按位置插入）
struct NumField { const char* name; double AppSettings::*field; };
const NumField kNumFieldsA[] = {
    {"TMaxSp", &AppSettings::t_max_sp},
    {"PMaxSp", &AppSettings::p_max_sp},
    {"RpmMax", &AppSettings::rpm_max},
    {"VolMax", &AppSettings::vol_max},
    {"StirCurrent", &AppSettings::stir_current},
    {"StirStandbyPct", &AppSettings::stir_standby_pct},
    {"StirRampMs", &AppSettings::stir_ramp_ms},
    {"StirMicrostep", &AppSettings::stir_microstep},
    {"StirGearRatio", &AppSettings::stir_gear_ratio},
    {"PressFullScaleMPa", &AppSettings::press_full_scale_mpa},
    {"ApproachDelta", &AppSettings::approach_delta},
    {"ApproachRate", &AppSettings::approach_rate},
    {"ApproachFastRate", &AppSettings::approach_fast_rate},
    {"ApproachFinalZone", &AppSettings::approach_final_zone},
    {"ApproachSettleBand", &AppSettings::approach_settle_band},
    {"ApproachSettleSec", &AppSettings::approach_settle_sec},
    {"OverPressure", &AppSettings::over_pressure},
    {"OverTemp", &AppSettings::over_temp},
};
// StopHeatOnOverPressure (bool)
const NumField kNumFieldsB[] = {
    {"HeatTimeout", &AppSettings::heat_timeout},
    {"PressDeviation", &AppSettings::press_deviation},
    {"LeakRate", &AppSettings::leak_rate},
    {"LeakReminderDays", &AppSettings::leak_reminder_days},
    {"ProgramGasWaitMin", &AppSettings::program_gas_wait_min},
    {"LeakTestPsi", &AppSettings::leak_test_psi},
    {"LeakSettleMin", &AppSettings::leak_settle_min},
    {"LeakMeasureMin", &AppSettings::leak_measure_min},
};
// LastLeakTestUtc
const NumField kNumFieldsC[] = {
    {"VesselVolume", &AppSettings::vessel_volume},
    {"GasLineVolume", &AppSettings::gas_line_volume},
};
// ManifoldVolume, EffectiveVolume, VolumeCalibUtc, Language

struct BadFile : std::runtime_error {
    using std::runtime_error::runtime_error;
};

ojson opt_num(const std::optional<double>& v) { return v ? json_num(*v) : ojson(nullptr); }
ojson opt_time(const std::optional<int64_t>& v) { return v ? ojson(fmt_utc_json(*v)) : ojson(nullptr); }

double need_num(const json& j, const char* name) {
    if (!j.is_number()) throw BadFile(std::string(name) + " 不是数值");
    return j.get<double>();
}
std::optional<double> opt_num_in(const json& j, const char* name) {
    if (j.is_null()) return std::nullopt;
    return need_num(j, name);
}
std::optional<int64_t> opt_time_in(const json& j, const char* name) {
    if (j.is_null()) return std::nullopt;
    if (!j.is_string()) throw BadFile(std::string(name) + " 不是时间字符串");
    int64_t ms = 0;
    if (!parse_time_loose(j.get<std::string>(), ms)) throw BadFile(std::string(name) + " 时间格式不对");
    return ms;
}

ojson settings_obj(const AppSettings& s) {
    ojson o = ojson::object();
    for (auto& f : kNumFieldsA) o[f.name] = json_num(s.*(f.field));
    o["StopHeatOnOverPressure"] = s.stop_heat_on_over_pressure;
    for (auto& f : kNumFieldsB) o[f.name] = json_num(s.*(f.field));
    o["LastLeakTestUtc"] = opt_time(s.last_leak_test_utc_ms);
    for (auto& f : kNumFieldsC) o[f.name] = json_num(s.*(f.field));
    o["ManifoldVolume"] = opt_num(s.manifold_volume);
    ojson ev = ojson::array(), vc = ojson::array();
    for (int i = 0; i < kChannelCount; ++i) {
        ev.push_back(opt_num(s.effective_volume[static_cast<size_t>(i)]));
        vc.push_back(opt_time(s.volume_calib_utc_ms[static_cast<size_t>(i)]));
    }
    o["EffectiveVolume"] = ev;
    o["VolumeCalibUtc"] = vc;
    o["Language"] = s.language;
    o["UiPressureUnit"] = s.ui_pressure_unit;
    o["UiTempUnit"] = s.ui_temp_unit;
    o["UiTheme"] = s.ui_theme;
    o["UiKeyboard"] = s.ui_keyboard;
    o["UiPreflightChecklist"] = s.ui_preflight_checklist;
    o["StorageRetentionDays"] = s.storage_retention_days;
    return o;
}

void read_settings(const json& o, AppSettings& s) {
    if (!o.is_object()) throw BadFile("Settings 不是对象");
    auto num_fields = [&](const NumField* begin, const NumField* end) {
        for (auto* f = begin; f != end; ++f)
            if (auto it = o.find(f->name); it != o.end()) s.*(f->field) = need_num(*it, f->name);
    };
    num_fields(std::begin(kNumFieldsA), std::end(kNumFieldsA));
    num_fields(std::begin(kNumFieldsB), std::end(kNumFieldsB));
    num_fields(std::begin(kNumFieldsC), std::end(kNumFieldsC));
    if (auto it = o.find("StopHeatOnOverPressure"); it != o.end()) {
        if (!it->is_boolean()) throw BadFile("StopHeatOnOverPressure 不是布尔");
        s.stop_heat_on_over_pressure = it->get<bool>();
    }
    if (auto it = o.find("LastLeakTestUtc"); it != o.end()) s.last_leak_test_utc_ms = opt_time_in(*it, "LastLeakTestUtc");
    if (auto it = o.find("ManifoldVolume"); it != o.end()) s.manifold_volume = opt_num_in(*it, "ManifoldVolume");
    if (auto it = o.find("EffectiveVolume"); it != o.end() && !it->is_null()) {
        if (!it->is_array()) throw BadFile("EffectiveVolume 不是数组");
        for (size_t i = 0; i < it->size(); ++i) {
            auto v = opt_num_in((*it)[i], "EffectiveVolume[]");
            if (i < s.effective_volume.size()) s.effective_volume[i] = v;
        }
    }
    if (auto it = o.find("VolumeCalibUtc"); it != o.end() && !it->is_null()) {
        if (!it->is_array()) throw BadFile("VolumeCalibUtc 不是数组");
        for (size_t i = 0; i < it->size(); ++i) {
            auto v = opt_time_in((*it)[i], "VolumeCalibUtc[]");
            if (i < s.volume_calib_utc_ms.size()) s.volume_calib_utc_ms[i] = v;
        }
    }
    if (auto it = o.find("Language"); it != o.end() && !it->is_null()) {
        if (!it->is_string()) throw BadFile("Language 不是字符串");
        s.language = it->get<std::string>();
    }
    // 新增的界面偏好：类型不对就按默认（不把整个文件当成读不了）
    auto str_field = [&](const char* name, std::string& dst) {
        auto it = o.find(name);
        if (it != o.end() && it->is_string()) dst = it->get<std::string>();
    };
    str_field("UiPressureUnit", s.ui_pressure_unit);
    str_field("UiTempUnit", s.ui_temp_unit);
    str_field("UiTheme", s.ui_theme);
    str_field("UiKeyboard", s.ui_keyboard);
    if (auto it = o.find("UiPreflightChecklist"); it != o.end() && it->is_boolean()) s.ui_preflight_checklist = it->get<bool>();
    if (auto it = o.find("StorageRetentionDays"); it != o.end() && it->is_number())
        s.storage_retention_days = it->get<double>();
}

}  // namespace

std::string settings_to_json(const SettingsState& st) {
    ojson root = ojson::object();
    root["Settings"] = settings_obj(st.settings);
    root["BandBounds"] = ojson::array({json_num(st.band_bounds[0]), json_num(st.band_bounds[1])});
    ojson pids = ojson::array();
    for (auto& row : st.band_pids) {
        ojson r = ojson::array();
        for (auto& p : row) r.push_back(ojson::array({json_num(p.p), json_num(p.i), json_num(p.d)}));
        pids.push_back(r);
    }
    root["BandPids"] = pids;
    ojson tune = ojson::array();
    for (auto& row : st.band_tune) {
        ojson r = ojson::array();
        for (auto& m : row) {
            if (!m) { r.push_back(nullptr); continue; }
            ojson o = ojson::object();
            o["TunedSpC"] = opt_num(m->tuned_sp_c);
            o["TunedAt"] = opt_time(m->tuned_at_ms);
            o["ManualEditedAt"] = opt_time(m->manual_edited_at_ms);
            r.push_back(o);
        }
        tune.push_back(r);
    }
    root["BandTune"] = tune;
    root["Revision"] = st.revision;
    return dump_json(root, 2);
}

bool settings_from_json(const std::string& text_in, SettingsState& out, std::string* err) {
    try {
        json root = json::parse(strip_bom(text_in));
        if (!root.is_object()) throw BadFile("根不是对象");
        SettingsState st;
        if (auto it = root.find("Settings"); it != root.end() && !it->is_null()) read_settings(*it, st.settings);
        if (auto it = root.find("BandBounds"); it != root.end() && !it->is_null()) {
            if (!it->is_array()) throw BadFile("BandBounds 不是数组");
            std::vector<double> v;
            for (auto& x : *it) v.push_back(need_num(x, "BandBounds[]"));
            if (v.size() == 2) st.band_bounds = {v[0], v[1]};
        }
        if (auto it = root.find("BandPids"); it != root.end() && !it->is_null()) {
            if (!it->is_array()) throw BadFile("BandPids 不是数组");
            st.has_band_pids = true;
            for (size_t ch = 0; ch < it->size(); ++ch) {
                const json& row = (*it)[ch];
                if (row.is_null()) continue;
                if (!row.is_array()) throw BadFile("BandPids[] 不是数组");
                for (size_t b = 0; b < row.size(); ++b) {
                    const json& v = row[b];
                    if (v.is_null()) continue;
                    if (!v.is_array()) throw BadFile("BandPids[][] 不是数组");
                    std::vector<double> pid;
                    for (auto& x : v) pid.push_back(need_num(x, "BandPids[][][]"));
                    if (ch < static_cast<size_t>(kChannelCount) && b < static_cast<size_t>(kBandCount) && pid.size() == 3)
                        st.band_pids[ch][b] = Pid{pid[0], pid[1], pid[2]};
                }
            }
        }
        if (auto it = root.find("BandTune"); it != root.end() && !it->is_null()) {
            if (!it->is_array()) throw BadFile("BandTune 不是数组");
            for (size_t ch = 0; ch < it->size(); ++ch) {
                const json& row = (*it)[ch];
                if (row.is_null()) continue;
                if (!row.is_array()) throw BadFile("BandTune[] 不是数组");
                for (size_t b = 0; b < row.size(); ++b) {
                    const json& m = row[b];
                    if (m.is_null()) continue;
                    if (!m.is_object()) throw BadFile("BandTune[][] 不是对象");
                    BandTuneMeta meta;
                    if (auto f = m.find("TunedSpC"); f != m.end()) meta.tuned_sp_c = opt_num_in(*f, "TunedSpC");
                    if (auto f = m.find("TunedAt"); f != m.end()) meta.tuned_at_ms = opt_time_in(*f, "TunedAt");
                    if (auto f = m.find("ManualEditedAt"); f != m.end()) meta.manual_edited_at_ms = opt_time_in(*f, "ManualEditedAt");
                    if (meta.tuned_sp_c && !std::isfinite(*meta.tuned_sp_c)) meta.tuned_sp_c.reset();
                    if (ch < static_cast<size_t>(kChannelCount) && b < static_cast<size_t>(kBandCount) &&
                        (meta.tuned_sp_c || meta.tuned_at_ms || meta.manual_edited_at_ms))
                        st.band_tune[ch][b] = meta;
                }
            }
        }
        if (auto it = root.find("Revision"); it != root.end() && !it->is_null()) {
            if (!it->is_number_integer()) throw BadFile("Revision 不是整数");
            st.revision = it->get<int64_t>();
        }
        out = std::move(st);
        return true;
    } catch (const std::exception& e) {
        if (err) *err = e.what();
        return false;
    }
}

void tune_complete(SettingsState& st, int ch, int band, std::optional<double> sp, int64_t now) {
    if (ch < 0 || ch >= kChannelCount || band < 0 || band >= kBandCount) return;
    st.band_tune[static_cast<size_t>(ch)][static_cast<size_t>(band)] = BandTuneMeta{sp, now, std::nullopt};
}

void tune_mark_manual(SettingsState& st, int ch, int band, int64_t now) {
    if (ch < 0 || ch >= kChannelCount || band < 0 || band >= kBandCount) return;
    auto& slot = st.band_tune[static_cast<size_t>(ch)][static_cast<size_t>(band)];
    if (!slot) slot = BandTuneMeta{std::nullopt, std::nullopt, now};
    else slot->manual_edited_at_ms = now;
}

// ============================ 设置项表 ============================

namespace setting_specs {

namespace {
SettingSpec S(SettingKey k, const char* name, const char* label, const char* unit, double min, double max, double step,
              double AppSettings::*f, const char* sub = nullptr, bool device = false) {
    return SettingSpec{k, name, label, unit, min, max, step, sub, device ? Exposure::LocalOnly : Exposure::Remote, device, f};
}
}  // namespace

// C#: Models/SettingSpecs.cs All（标签 / 单位 / 范围 / 步进 / 说明逐个照抄）
const std::vector<SettingSpec>& all() {
    using K = SettingKey;
    using A = AppSettings;
    static const std::vector<SettingSpec> v = {
        S(K::TMaxSp, "TMaxSp", "温度 SP 上限", "°C", 50, 300, 5, &A::t_max_sp),
        S(K::PMaxSp, "PMaxSp", "压力 SP 上限", "psi", 100, 1000, 10, &A::p_max_sp),
        S(K::RpmMax, "RpmMax", "搅拌转速上限", "rpm", 50, 3000, 10, &A::rpm_max,
          "釜里搅拌子的转速上限（电机转速 = 釜转速 × 传动比）"),
        S(K::VolMax, "VolMax", "溶液体积上限", "mL", 5, 200, 5, &A::vol_max),

        S(K::OverPressure, "OverPressure", "超压报警", "psi", 100, 1000, 5, &A::over_pressure, "压力 ≥ 此值触发硬报警"),
        S(K::OverTemp, "OverTemp", "超温报警", "°C", 50, 400, 5, &A::over_temp, "温度 ≥ 此值触发报警"),
        S(K::HeatTimeout, "HeatTimeout", "升温超时报警", "分钟", 5, 60, 1, &A::heat_timeout, "升温超过此时长未到 SP 时提示"),
        S(K::PressDeviation, "PressDeviation", "压力偏离 SP 报警", "psi", 1, 50, 1, &A::press_deviation, "稳压后偏离超过此值时提示"),
        S(K::LeakRate, "LeakRate", "泄漏率阈值", "psi/hr", 0.5, 10, 0.5, &A::leak_rate, "泄漏测试判定不通过的阈值"),
        S(K::LeakReminderDays, "LeakReminderDays", "泄漏测试提醒周期", "天", 1, 30, 1, &A::leak_reminder_days, "超过此天数未测试则提醒"),
        S(K::ProgramGasWaitMin, "ProgramGasWaitMin", "程序等待气路上限", "分钟", 1, 600, 5, &A::program_gas_wait_min,
          "多程序并行时气路步骤排队等待其他程序释放气路的上限；超时该步骤故障、程序中止"),

        S(K::LeakTestPsi, "LeakTestPsi", "测试压力", "psi", 10, 150, 5, &A::leak_test_psi,
          "低压即可检出密封问题；须低于超压报警值的 90%，惰性气体减压阀调到略高于此值"),
        S(K::LeakSettleMin, "LeakSettleMin", "平衡时间", "分钟", 1, 30, 1, &A::leak_settle_min, "充压后静置，等气体温度回稳再开始测量"),
        S(K::LeakMeasureMin, "LeakMeasureMin", "测量时间", "分钟", 5, 120, 5, &A::leak_measure_min,
          "越长越能分辨小泄漏；压降速率按温度补偿后的最小二乘拟合计算"),

        S(K::ApproachDelta, "ApproachDelta", "预留温差 Δ", "°C", 0, 30, 0.5, &A::approach_delta,
          "须大于实测最大冲温（建议取 1.5 倍）；0 = 关闭逼近、直接写目标"),
        S(K::ApproachFastRate, "ApproachFastRate", "快段速率", "°C/min", 0.1, 10, 0.1, &A::approach_fast_rate,
          "离目标大于末段范围时使用；PV 领先时 SP 直接跟随，不暂停"),
        S(K::ApproachFinalZone, "ApproachFinalZone", "末段范围", "°C", 0.3, 5, 0.1, &A::approach_final_zone,
          "只有最后这段慢推，PV 偏高才暂停；建议略大于实测冲温"),
        S(K::ApproachRate, "ApproachRate", "末段速率", "°C/min", 0.02, 2, 0.02, &A::approach_rate, "防超调的关键：越慢越稳、越省不了时间"),
        S(K::ApproachSettleBand, "ApproachSettleBand", "判稳范围", "±°C", 0.1, 2, 0.1, &A::approach_settle_band,
          "PV 到达 中间SP−此值 后开始判峰；持续落在 ±此值 内也可开始逼近"),
        S(K::ApproachSettleSec, "ApproachSettleSec", "判稳时长", "s", 10, 900, 10, &A::approach_settle_sec,
          "兜底条件：PV 过峰（不再上升）会提前开始逼近，不必等满此时长"),

        S(K::StirCurrent, "StirCurrent", "峰值电流", "A", 0.3, 3.2, 0.1, &A::stir_current,
          "按电机铭牌额定电流设：带负载卡顿调大、发烫调小（驱动器硬限 0.3–3.2A）", true),
        S(K::StirStandbyPct, "StirStandbyPct", "待机电流", "%", 0, 100, 5, &A::stir_standby_pct, "停转后自动降到此百分比，减少电机发热", true),
        S(K::StirRampMs, "StirRampMs", "加减速时间", "ms/1000rpm", 50, 10000, 50, &A::stir_ramp_ms,
          "起停平缓度：越大越缓。值小起停顿挫/像“起飞”，值大更柔和（1000≈300rpm 用 0.3s 升起）", true),
        S(K::StirMicrostep, "StirMicrostep", "细分", "脉冲/转", 200, 51200, 200, &A::stir_microstep,
          "指令脉冲数/转：越高低速越平顺、越能压共振。常用 10000 / 20000 / 25600", true),

        S(K::PressFullScaleMPa, "PressFullScaleMPa", "变送器满量程", "MPa", 0.1, 60, 0.1, &A::press_full_scale_mpa,
          "必须与变送器铭牌量程一致（如 0–10MPa 填 10），否则压力读数按比例偏", true),

        S(K::VesselVolume, "VesselVolume", "釜容积", "mL", 5, 500, 1, &A::vessel_volume,
          "反应釜内腔容积。气相体积 = 釜容积 − 溶液体积 + 管路死体积，用于把压降折算成吸气量 mmol"),
        S(K::GasLineVolume, "GasLineVolume", "管路死体积", "mL", 0, 50, 0.1, &A::gas_line_volume,
          "进气阀 SV 到釜之间的管路与接头容积（标定后填入；未标定填 0，吸气量会略偏小）。换釜、换管路后要重新标定"),

        S(K::StirGearRatio, "StirGearRatio", "搅拌传动比", "电机÷釜", 0.1, 20, 0.001, &A::stir_gear_ratio,
          "一台电机接 85 齿大齿轮带 8 个釜 38 齿小齿轮 → 38 ÷ 85 = 0.447。界面上填的都是釜里的转速，下发电机时乘以此值", true),
    };
    return v;
}

const SettingSpec& of(SettingKey key) {
    for (auto& s : all())
        if (s.key == key) return s;
    return all().front();   // 不会到这里：表覆盖全部键（测试核对）
}

const SettingSpec* find(const std::string& name) {
    for (auto& s : all())
        if (name == s.name) return &s;
    return nullptr;
}

double get(const AppSettings& s, SettingKey key) { return s.*(of(key).field); }
void set(AppSettings& s, SettingKey key, double v) { s.*(of(key).field) = v; }

std::optional<RuleViolation> validate(SettingKey key, double value) {
    auto& s = of(key);
    return rules::range(s.label, value, s.min, s.max, s.unit);
}

std::optional<RuleViolation> cross_check(const AppSettings& current, SettingKey key, double value) {
    auto V = [&](SettingKey k) { return k == key ? value : get(current, k); };
    double t_max = V(SettingKey::TMaxSp), over_t = V(SettingKey::OverTemp);
    const double ninf = -std::numeric_limits<double>::infinity();
    if (t_max >= over_t) return RuleViolation{"PARAM_CROSS_CHECK", "温度 SP 上限须低于超温报警值", ninf, over_t, t_max};
    double leak = V(SettingKey::LeakTestPsi), over_p = V(SettingKey::OverPressure);
    if (leak >= over_p * kLeakTestMaxRatio)
        return RuleViolation{"PARAM_CROSS_CHECK", "测试压力须低于超压报警值的 90%", ninf, over_p * kLeakTestMaxRatio, leak};
    return std::nullopt;
}

}  // namespace setting_specs

// ============================ ValueRules（本地拷贝）============================

namespace rules {

std::string fmt_n(double v) { return csv::net_format(v, "0.##"); }

std::optional<RuleViolation> range(const std::string& label, double v, double min, double max, const std::string& unit) {
    if (std::isfinite(v) && v >= min && v <= max) return std::nullopt;
    std::string u = unit.empty() ? "" : " " + unit;
    return RuleViolation{"PARAM_OUT_OF_RANGE", label + "须在 " + fmt_n(min) + "–" + fmt_n(max) + u + " 之间", min, max, v};
}

std::optional<RuleViolation> pid(int band, double p, double i, double d, int band_count) {
    if (auto r = range("PID 档位", band, 0, band_count - 1)) return r;
    if (auto r = range("比例带 P ", p, 0, kPidPMax)) return r;
    if (auto r = range("积分时间 I ", i, 0, kPidIMax, "s")) return r;
    return range("微分时间 D ", d, 0, kPidDMax, "s");
}

std::optional<RuleViolation> band_bound(int which, double value, double low_hi, double mid_hi) {
    if (which == 1) return range("低温 / 中温 分界", value, kBandBoundMin, mid_hi - kBandGap, "℃");
    if (which == 2) return range("中温 / 高温 分界", value, low_hi + kBandGap, kBandBoundMax, "℃");
    return range("档位分界编号", which, 1, 2);
}

}  // namespace rules

// ============================ 修改 ============================

static PatchResult fail(const SettingsState& st, std::string code, std::string msg) {
    PatchResult r;
    r.code = std::move(code);
    r.message = std::move(msg);
    r.revision = st.revision;
    return r;
}

PatchResult patch_settings(SettingsState& st, const std::vector<SettingChange>& changes,
                           std::optional<int64_t> expected_revision, bool allow_local_only) {
    if (expected_revision && *expected_revision != st.revision)
        return fail(st, "REVISION_CONFLICT", "设置已被别处修改（当前版本 " + std::to_string(st.revision) + "），请刷新后再改");
    AppSettings work = st.settings;
    std::vector<SettingKey> changed;
    for (auto& c : changes) {
        auto& spec = setting_specs::of(c.key);
        if (!allow_local_only && spec.exposure == Exposure::LocalOnly) {
            auto r = fail(st, "NOT_EXPOSED", std::string(spec.label) + "只能在触屏上修改");
            r.bad_key = c.key;
            return r;
        }
        auto bad = setting_specs::validate(c.key, c.value);
        if (bad) {
            auto r = fail(st, bad->code, bad->message);
            r.violation = bad;
            r.bad_key = c.key;
            return r;
        }
        if (setting_specs::get(work, c.key) != c.value) {
            setting_specs::set(work, c.key, c.value);
            bool seen = false;
            for (auto k : changed) seen |= k == c.key;
            if (!seen) changed.push_back(c.key);
        }
    }
    // 交叉条件按全部改完之后的值核对（一次 PATCH 同时降低超温报警值和温度 SP 上限是合法的）。
    // 单项修改时等价于 C# SettingSpecs.CrossCheck(current, key, value)。
    if (auto bad = setting_specs::cross_check(work, SettingKey::TMaxSp, work.t_max_sp)) {
        auto r = fail(st, bad->code, bad->message);
        r.violation = bad;
        return r;
    }
    PatchResult r;
    r.ok = true;
    if (!changed.empty()) {
        st.settings = work;
        ++st.revision;
    }
    r.changed = std::move(changed);
    r.revision = st.revision;
    return r;
}

PatchResult set_band_bounds(SettingsState& st, double low_mid, double mid_high, std::optional<int64_t> expected_revision) {
    if (expected_revision && *expected_revision != st.revision)
        return fail(st, "REVISION_CONFLICT", "设置已被别处修改（当前版本 " + std::to_string(st.revision) + "），请刷新后再改");
    auto bad = rules::band_bound(1, low_mid, low_mid, mid_high);
    if (!bad) bad = rules::band_bound(2, mid_high, low_mid, mid_high);
    if (bad) {
        auto r = fail(st, bad->code, bad->message);
        r.violation = bad;
        return r;
    }
    PatchResult r;
    r.ok = true;
    if (st.band_bounds[0] != low_mid || st.band_bounds[1] != mid_high) {
        st.band_bounds = {low_mid, mid_high};
        ++st.revision;
    }
    r.revision = st.revision;
    return r;
}

// ============================ 文件 ============================

namespace settings_guard {

const char kPreV06Tag[] = "pre-v0.6";

std::string backup_once(const std::string& path, const std::string& text, const std::string& tag) {
    std::string bak = path + ".bak-" + tag;
    if (stat_path(bak).exists) return "";
    if (!write_file_atomic(bak, text, true)) return "";
    return bak;
}

}  // namespace settings_guard

SettingsStore::SettingsStore(std::string config_dir) : dir_(std::move(config_dir)) {}

std::string SettingsStore::path() const { return join_path(dir_, "settings.json"); }

SettingsStore::LoadResult SettingsStore::load(std::optional<int64_t> now_utc_ms) const {
    LoadResult r;
    const std::string p = path();
    if (!file_exists(p)) return r;
    r.found = true;
    std::string text;
    if (!read_file(p, text)) {
        r.error = "读不了 " + p;
        return r;
    }
    if (text.find("\"BandTune\"") == std::string::npos) {
        auto b = settings_guard::backup_once(p, text, settings_guard::kPreV06Tag);
        if (!b.empty()) r.backup_path = b;
    }
    SettingsState st;
    std::string err;
    if (!settings_from_json(text, st, &err)) {
        int64_t now = now_utc_ms ? *now_utc_ms : wall_ms();
        auto b = settings_guard::backup_once(p, text, "unreadable-" + fmt_wall(WallTime::local(now), "yyyyMMdd-HHmmss"));
        if (!b.empty()) r.backup_path = b;
        r.error = err;
        return r;
    }
    r.state = std::move(st);
    return r;
}

bool SettingsStore::save_now(const SettingsState& st, std::string* err) {
    std::string e;
    bool ok = write_file_atomic(path(), settings_to_json(st), true, &e);
    last_error_ = ok ? "" : e;
    if (err) *err = e;
    return ok;
}

void SettingsStore::save_soon(const SettingsState& st, int64_t now_mono_ms) {
    pending_ = st;
    due_mono_ms_ = now_mono_ms + kDebounceMs;
}

bool SettingsStore::poll(int64_t now_mono_ms) {
    if (!pending_ || now_mono_ms < due_mono_ms_) return false;
    return flush();
}

bool SettingsStore::flush() {
    if (!pending_) return false;
    SettingsState st = std::move(*pending_);
    pending_.reset();
    return save_now(st);
}

}  // namespace reactor::store
