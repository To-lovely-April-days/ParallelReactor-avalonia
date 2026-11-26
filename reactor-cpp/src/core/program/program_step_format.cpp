#include "core/program/program_step_format.hpp"

#include <cmath>
#include <cstdio>
#include <cstdlib>

#include "core/program/schema_codes.hpp"

namespace reactor::core::program {

using ojson = nlohmann::ordered_json;

ojson json_number(double v) {
    if (!std::isfinite(v)) return nullptr;
    // 先按 15 位有效数字取整（去掉 510 × 0.9 − 0.1 = 458.90000000000003 这类二进制尾巴，输出 458.9，同 C# 接口里的值）
    char buf[40];
    std::snprintf(buf, sizeof buf, "%.15g", v);
    v = std::strtod(buf, nullptr);
    if (v == std::floor(v) && std::fabs(v) < 9.0e15) return static_cast<int64_t>(v);
    return v;
}

namespace program_step_format {

ProgramStepDto format(const StepSnapshot& s) {
    std::string type = to_code(s.type());
    ojson p = ojson::object();
    bool heat = s.type() == StepType::Heat;
    if (heat) p["mode"] = s.is_curve() ? "curve" : "fixed";
    for (const auto& kv : s.params()) {
        if (heat && kv.first == "mode") continue;
        if (const std::string* str = std::get_if<std::string>(&kv.second)) {
            // 界面 label → 枚举码；已是枚举码或不在映射表里的字符串原样输出
            p[kv.first] = schema_codes::code_of(type, kv.first, *str).value_or(*str);
        } else {
            p[kv.first] = json_number(std::get<double>(kv.second));
        }
    }
    if (s.is_curve()) {
        ojson segs = ojson::array();
        for (const auto& g : s.segments())
            segs.push_back(ojson{{"temp", json_number(g.temp)}, {"rate", json_number(g.rate)}, {"hold", json_number(g.hold)}});
        p["segments"] = segs;
    }
    return ProgramStepDto{type, p, s.name(), s.detail()};
}

std::vector<ProgramStepDto> format(const std::vector<StepSnapshot>& steps) {
    std::vector<ProgramStepDto> list;
    list.reserve(steps.size());
    for (const auto& s : steps) list.push_back(format(s));
    return list;
}

ojson to_json(const ProgramStepDto& d) {
    ojson j = ojson::object();
    j["type"] = d.type;
    j["params"] = d.params;
    j["name"] = d.name;
    j["detail"] = d.detail;
    return j;
}

ojson to_json(const std::vector<ProgramStepDto>& list) {
    ojson a = ojson::array();
    for (const auto& d : list) a.push_back(to_json(d));
    return a;
}

}  // namespace program_step_format
}  // namespace reactor::core::program
