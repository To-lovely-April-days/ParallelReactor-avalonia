#include "core/program/step_migration.hpp"

#include <algorithm>

#include "core/program/text_format.hpp"

namespace reactor::core::program {

std::optional<double> MigStep::num(const std::string& k) const {
    const ParamValue* v = p.find(k);
    if (!v) return std::nullopt;
    if (const double* d = std::get_if<double>(v)) return *d;
    double x;
    if (parse_double(std::get<std::string>(*v), x)) return x;
    return std::nullopt;
}

std::optional<std::string> MigStep::str(const std::string& k) const {
    const ParamValue* v = p.find(k);
    if (!v) return std::nullopt;
    if (const std::string* s = std::get_if<std::string>(v)) return *s;
    return std::nullopt;
}

namespace step_migration {

namespace {
std::optional<double> heat_target(const MigStep& s) {
    if (s.str("mode").value_or("") == "curve" && !s.segments.empty()) return s.segments.back().temp;
    return s.num("temp");
}
}  // namespace

bool is_legacy(const MigStep& s) {
    if (s.type == "purge" || s.type == "wait") return true;
    if (s.type == "stir") return s.p.contains("dur");
    if (s.type == "end") return s.p.contains("mode") || s.p.contains("cool");
    if (s.type == "react") return s.p.contains("endBy");
    return false;
}

MigrationResult migrate(const std::vector<MigStep>& input) {
    std::vector<MigStep> o;
    bool migrated = false;
    std::optional<double> last_heat, last_press;
    for (const auto& s : input) {
        if (!is_legacy(s)) {
            if (s.type == "heat") { auto h = heat_target(s); if (h) last_heat = h; }
            if (s.type == "press") { auto pr = s.num("pres"); if (pr) last_press = pr; }
            o.push_back(s);
            continue;
        }
        migrated = true;
        if (s.type == "purge") {
            MigStep e("exchange");
            e.with("gas", s.str("gas").value_or("inert")).with("cycles", 0.0).with("lineFlush", s.num("cycles").value_or(10));
            o.push_back(e);
        } else if (s.type == "stir") {
            double rpm = s.num("rpm").value_or(400), dur = s.num("dur").value_or(0);
            o.push_back(MigStep("stir").with("rpm", rpm));
            if (dur > 0) {
                // 旧搅拌步骤停留 dur 分钟（rpm = 0 时也停留）：保持 dur，转过的再停，行为不变
                o.push_back(MigStep("hold").with("until", std::string("time")).with("time", dur));
                if (rpm > 0) o.push_back(MigStep("stir").with("rpm", 0.0));
            }
        } else if (s.type == "wait") {
            std::string mode = s.str("mode").value_or("fixed");
            double time = s.num("time").value_or(30);
            if (mode == "temp")
                o.push_back(MigStep("hold").with("until", std::string("tempAbove")).with("temp", last_heat.value_or(40))
                                .with("maxWait", std::max(1.0, time)).with("onTimeout", std::string("continue")));
            else if (mode == "pressure")
                o.push_back(MigStep("hold").with("until", std::string("pressAbove"))
                                .with("pres", last_press ? round_even(*last_press * 0.97, 1) : 14.5)
                                .with("maxWait", std::max(1.0, time)).with("onTimeout", std::string("continue")));
            else
                o.push_back(MigStep("hold").with("until", std::string("time")).with("time", time));
        } else if (s.type == "react") {
            std::string end_by = s.str("endBy").value_or("time");
            MigStep r("react");
            r.with("time", s.num("time").value_or(120)).with("minTime", 0.0)
                .with("mmolOn", std::string(end_by == "gasConsumed" || end_by == "either" ? "on" : "off"))
                .with("mmol", s.num("mmol").value_or(5)).with("stallOn", std::string("off"));
            o.push_back(r);
        } else if (s.type == "end") {
            std::string mode = s.str("mode").value_or("seal");
            if (auto cool = s.num("cool"))
                o.push_back(MigStep("hold").with("until", std::string("tempBelow")).with("temp", *cool).with("maxWait", 60.0)
                                .with("onTimeout", std::string("continue")));
            if (mode == "vent" || mode == "quench") o.push_back(MigStep("vent").with("flush", 0.0));
            else if (mode == "purge")
                o.push_back(MigStep("exchange").with("gas", std::string("inert")).with("cycles", 0.0).with("lineFlush", 3.0));
            o.push_back(MigStep("end"));
        }
    }
    // 末尾只有一个 end：去掉中间的，没有就补
    int last = -1, ends = 0;
    for (int i = 0; i < static_cast<int>(o.size()); ++i)
        if (o[static_cast<size_t>(i)].type == "end") { last = i; ++ends; }
    migrated = migrated || ends > 1;   // 多余的 end 被去掉：程序变了，也要提示检查
    std::vector<MigStep> fin;
    fin.reserve(o.size() + 1);
    for (int i = 0; i < static_cast<int>(o.size()); ++i)
        if (o[static_cast<size_t>(i)].type != "end" || i == last) fin.push_back(o[static_cast<size_t>(i)]);
    if (last < 0 || fin.back().type != "end") {
        // 末尾不是 end（例如 end 后面还有步骤）：挪到最后
        fin.erase(std::remove_if(fin.begin(), fin.end(), [](const MigStep& x) { return x.type == "end"; }), fin.end());
        fin.push_back(MigStep("end"));
        migrated = migrated || last >= 0;
    }
    return MigrationResult{fin, migrated};
}

}  // namespace step_migration
}  // namespace reactor::core::program
