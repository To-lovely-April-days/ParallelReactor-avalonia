#include "store/telemetry_csv.hpp"

#include "store/csv.hpp"

namespace reactor::store::telemetry_csv {

namespace {
const char* const kModuleNames[] = {"温控", "压力", "阀门", "搅拌"};

std::vector<std::string> build_header() {
    std::vector<std::string> h = {"时间", "标记", "会话"};
    for (int i = 1; i <= TelemetryRow::kChannelCount; ++i) {
        std::string p = "RV" + std::to_string(i) + "_";
        for (const char* s : {"PV(℃)", "PV原码", "SP已下发(℃)", "目标TSp(℃)", "输出(%)", "At", "逼近阶段", "逼近SP(℃)", "曲线SP(℃)",
                              "压力原码", "压力(psi)", "压力(MPa)", "状态", "超温切断", "进气阀"})
            h.push_back(p + s);
    }
    for (const char* s : {"AI8_Srun", "AI8_Ctn", "搅拌开", "搅拌设定(rpm)", "搅拌加减速(ms/1000rpm)", "搅拌报警码", "母线电压(V)",
                          "阀_惰性气体", "阀_气体A", "阀_气体B", "阀_排空", "急停"})
        h.push_back(s);
    for (const char* m : kModuleNames)
        for (const char* s : {"_已配置", "_在线", "_事务数", "_失败数", "_最近响应"}) h.push_back(std::string(m) + s);
    h.push_back("模拟");
    // v0.5：每通道程序步，按「只能在末尾追加」规则放末尾
    for (int i = 1; i <= TelemetryRow::kChannelCount; ++i) h.push_back("RV" + std::to_string(i) + "_程序步");
    // v0.6：吸气量
    for (int i = 1; i <= TelemetryRow::kChannelCount; ++i) h.push_back("RV" + std::to_string(i) + "_吸气量(mmol)");
    h.push_back("搅拌电机(rpm)");
    return h;
}
}  // namespace

const std::vector<std::string>& header_fields() {
    static const std::vector<std::string> h = build_header();
    return h;
}

const std::string& header() {
    static const std::string h = csv::line(header_fields());
    return h;
}

std::vector<std::string> row_fields(const TelemetryRow& r) {
    std::vector<std::string> f;
    f.reserve(header_fields().size());
    f.push_back(csv::time(r.time));
    f.push_back(r.marker);
    f.push_back(r.session.value_or(""));
    for (const auto& c : r.ch) {
        f.push_back(csv::num(c.pv, "0.0##"));
        f.push_back(csv::integer(c.pv_raw));
        f.push_back(csv::num(c.sp_written, "0.0##"));
        f.push_back(csv::num(c.target_sp, "0.0##"));
        f.push_back(csv::num(c.output_pct, "0.##"));
        f.push_back(csv::integer(c.at));
        f.push_back(c.approach);
        f.push_back(csv::num(c.approach_sp, "0.0##"));
        f.push_back(csv::num(c.curve_sp, "0.0##"));
        f.push_back(csv::integer(c.press_raw));
        f.push_back(csv::num(c.press_psi, "0.0##"));
        f.push_back(csv::num(c.press_psi ? std::optional<double>(*c.press_psi * kMpaPerPsi) : std::nullopt, "0.00000"));
        f.push_back(c.state);
        f.push_back(csv::boolean(c.over_temp));
        f.push_back(csv::boolean(c.valve));
    }
    f.push_back(csv::integer(r.srun));
    f.push_back(csv::integer(r.ctn));
    f.push_back(csv::boolean(r.stir_on));
    f.push_back(csv::integer(r.stir_rpm_set));
    f.push_back(csv::integer(r.stir_ramp_ms));
    f.push_back(csv::integer(r.stir_alarm));
    f.push_back(csv::num(r.bus_voltage, "0.0#"));
    for (bool g : r.gas) f.push_back(csv::boolean(g));
    f.push_back(csv::boolean(r.vent));
    f.push_back(csv::boolean(r.estopped));
    for (const TelemetryModule* m : {&r.temp, &r.press, &r.io, &r.stir}) {
        f.push_back(csv::boolean(m->configured));
        f.push_back(csv::boolean(m->online));
        f.push_back(csv::integer(m->polls));
        f.push_back(csv::integer(m->fails));
        f.push_back(csv::time(m->last_ok_at));
    }
    f.push_back(csv::boolean(r.sim));
    for (const auto& c : r.ch) f.push_back(c.program_step);
    for (const auto& c : r.ch) f.push_back(csv::num(c.gas_mmol, "0.000"));
    f.push_back(csv::integer(r.stir_motor_rpm));
    return f;
}

std::string format(const TelemetryRow& r) { return csv::line(row_fields(r)); }

const std::string& event_header() {
    static const std::string h = csv::line({"时间", "级别", "来源", "标题", "详情"});
    return h;
}

std::string format_event(const WallTime& t, const std::string& level, const std::string& source, const std::string& title,
                         const std::string& detail) {
    return csv::line({csv::time(t), level, source, title, detail});
}

}  // namespace reactor::store::telemetry_csv
