#include "core/program/program_json.hpp"

#include <algorithm>
#include <cctype>
#include <cmath>

#include "core/program/program_step_format.hpp"
#include "core/program/program_types.hpp"
#include "core/program/schema_codes.hpp"
#include "core/program/step_migration.hpp"
#include <optional>
#include <string>
#include <vector>

namespace reactor::core::program::program_json {

namespace {

using ojson = nlohmann::ordered_json;
using json = nlohmann::json;

const char* const kTypeList = "leak / exchange / stir / heat / press / hold / react / vent / end";

std::string N(double v) { return fmt_dec(v, 3, 0); }   // C#: ProgramJson.N（"0.###"）

ojson step_detail(int ordinal) { return ojson{{"step", ordinal}}; }

// 旧格式选项的 label → 码（purge.gas / wait.mode / react.endBy / end.mode 只在输入时认）
std::string legacy_code(const std::string& type, const std::string& key, const std::string& v) {
    const auto* o = schema_codes::options(type + "." + key);
    if (!o) return v;
    for (const auto& x : *o)
        if (x.code == v || x.label == v) return x.code;
    return v;
}

struct ToMigOut {
    std::optional<MigStep> step;
    std::optional<ProgramJsonError> error;
};

ToMigOut to_mig(const json& s, int ordinal) {
    auto bad = [&](const char* code, const std::string& msg, ojson detail = ojson()) {
        if (detail.is_null()) detail = step_detail(ordinal);
        return ToMigOut{std::nullopt, ProgramJsonError{code, "第 " + std::to_string(ordinal) + " 步：" + msg, detail}};
    };
    if (!s.is_object()) return bad(op_codes::kBadRequest, "每步须为对象 { type, params }");
    auto t_it = s.find("type");
    if (t_it == s.end() || !t_it->is_string()) return bad(op_codes::kBadRequest, "缺少 type");
    std::string raw_type = t_it->get<std::string>();
    auto st = parse_step_type(raw_type);
    // C#: (tEl.GetString() ?? "").Trim().ToLowerInvariant()
    std::string type;
    {
        size_t b = 0, e = raw_type.size();
        while (b < e && std::isspace(static_cast<unsigned char>(raw_type[b]))) ++b;
        while (e > b && std::isspace(static_cast<unsigned char>(raw_type[e - 1]))) --e;
        for (size_t i = b; i < e; ++i) type += static_cast<char>(std::tolower(static_cast<unsigned char>(raw_type[i])));
    }
    if (!st)
        return bad(op_codes::kProgramInvalid, "未知步骤类型「" + type + "」（" + kTypeList + "；旧码 purge / wait 自动转换）",
                   ojson{{"step", ordinal}, {"type", type}});
    auto p_it = s.find("params");
    const json& p = (p_it != s.end() && p_it->is_object()) ? *p_it : s;

    Params d;
    std::vector<Segment> segs;
    for (auto it = p.begin(); it != p.end(); ++it) {
        const std::string& name = it.key();
        const json& v = it.value();
        if (name == "type" || name == "name" || name == "detail" || name == "params") continue;
        if (name == "segments") {
            if (!v.is_array())
                return bad(op_codes::kProgramInvalid, "segments 须为数组 [{temp, rate, hold}]", ojson{{"step", ordinal}, {"field", "segments"}});
            int k = 0;
            for (const auto& g : v) {
                ++k;
                if (k > schema_codes::kSegmentsMax)
                    return bad(op_codes::kParamOutOfRange, "曲线最多 " + std::to_string(schema_codes::kSegmentsMax) + " 段",
                               ojson{{"step", ordinal}, {"field", "segments"}, {"max", schema_codes::kSegmentsMax}});
                double vals[3] = {0, 0, 0};
                for (int f = 0; f < 3; ++f) {
                    const SegmentFieldSpec& sf = schema_codes::kSegmentFields[f];
                    json::const_iterator sv;
                    if (!g.is_object() || (sv = g.find(sf.key)) == g.end() || !sv->is_number() ||
                        !std::isfinite(sv->get<double>()))
                        return bad(op_codes::kBadRequest, "第 " + std::to_string(k) + " 段缺少数值字段 " + sf.key);
                    double x = sv->get<double>();
                    if (x < sf.min || x > sf.max)
                        return bad(op_codes::kParamOutOfRange,
                                   "第 " + std::to_string(k) + " 段 " + sf.label + " " + N(x) + sf.unit + " 超出 " + N(sf.min) + "–" +
                                       N(sf.max) + sf.unit,
                                   ojson{{"step", ordinal}, {"segment", k}, {"field", sf.key}, {"value", json_number(x)},
                                         {"min", json_number(sf.min)}, {"max", json_number(sf.max)}});
                    vals[f] = x;
                }
                segs.push_back({vals[0], vals[1], vals[2]});
            }
            continue;
        }
        if (v.is_string()) {
            std::string sv = v.get<std::string>();
            // 旧格式的选项先换成码（StepMigration 按码转换）
            bool legacy_key = is_legacy(*st) || (type == "react" && name == "endBy") || (type == "end" && name == "mode");
            d.set(name, legacy_key ? legacy_code(type, name, sv) : sv);
        } else if (v.is_number()) {
            double num = v.get<double>();
            if (!std::isfinite(num)) return bad(op_codes::kBadRequest, name + " 须为数值");
            d.set(name, num);
        } else if (v.is_null()) {
            // 忽略
        } else {
            return bad(op_codes::kBadRequest, name + " 须为数值或枚举码字符串");
        }
    }
    return ToMigOut{MigStep(type, d, segs), std::nullopt};
}

struct BuildOut {
    std::optional<StepSnapshot> snap;
    std::optional<ProgramJsonError> error;
};

BuildOut build(const MigStep& m, int ordinal, bool migrated, double leak_rate, std::vector<std::string>& notes, PressureUnit unit) {
    std::string where = migrated ? "第 " + std::to_string(ordinal) + " 步（转换后）" : "第 " + std::to_string(ordinal) + " 步";
    auto bad = [&](const char* code, const std::string& msg, ojson detail = ojson()) {
        if (detail.is_null()) detail = step_detail(ordinal);
        return BuildOut{std::nullopt, ProgramJsonError{code, where + "：" + msg, detail}};
    };
    auto type = parse_step_type(m.type);
    const StepSpec* spec = type ? step_specs::find(*type) : nullptr;
    if (!spec)
        return bad(op_codes::kProgramInvalid, "未知步骤类型「" + m.type + "」（" + kTypeList + "）", ojson{{"step", ordinal}, {"type", m.type}});
    Params p = step_specs::defaults(*type);
    if (*type == StepType::Leak) p.set("maxDrop", leak_rate);
    for (const auto& f : spec->fields) {
        const ParamValue* raw = m.p.find(f.key);
        if (!raw) continue;   // 缺省：默认值
        if (schema_codes::is_unavailable_field(m.type + "." + f.key))
            return bad(op_codes::kProgramInvalid, "字段 " + f.key + " 当前不可用（" + schema_codes::kGasUnavailableReason + "）",
                       ojson{{"step", ordinal}, {"field", f.key}});
        if (f.is_sel()) {
            const std::string* code = std::get_if<std::string>(raw);
            if (!code) return bad(op_codes::kBadRequest, f.key + " 须为枚举码字符串");
            const SchemaOption* opt = nullptr;
            if (f.options)
                for (const auto& o : *f.options)
                    if (o.code == *code || o.label == *code) { opt = &o; break; }
            if (!opt)
                return bad(op_codes::kProgramInvalid, f.key + " 的值「" + *code + "」不在枚举里",
                           ojson{{"step", ordinal}, {"field", f.key}, {"value", *code}});
            if (!opt->available)
                return bad(op_codes::kProgramInvalid, f.key + " = " + *code + " 当前不可用（" + opt->reason.value_or("") + "）",
                           ojson{{"step", ordinal}, {"field", f.key}, {"value", *code}});
            p.set(f.key, opt->code);
            continue;
        }
        auto numv = m.num(f.key);
        if (!numv || std::holds_alternative<std::string>(*raw)) return bad(op_codes::kBadRequest, f.key + " 须为数值");
        double num = *numv;
        double lo = f.min, hi = f.max;
        std::string unit_s = f.unit.value_or("");
        if (num < lo || num > hi || (f.integer && std::fabs(num - round_even(num)) > 1e-9)) {
            if (!migrated) {
                if (f.integer && num >= lo && num <= hi) return bad(op_codes::kBadRequest, f.label + " 须为整数");
                return bad(op_codes::kParamOutOfRange, f.label + " " + N(num) + unit_s + " 超出 " + N(lo) + "–" + N(hi) + unit_s,
                           ojson{{"step", ordinal}, {"field", f.key}, {"value", json_number(num)}, {"min", json_number(lo)},
                                 {"max", json_number(hi)}});
            }
            double c = std::clamp(f.integer ? round_even(num) : num, lo, hi);
            notes.push_back(where + " " + spec->name + "：" + f.label + " " + N(num) + unit_s + " 超出新范围，已改为 " + N(c) + unit_s);
            num = c;
        }
        p.set(f.key, num);
    }
    std::vector<Segment> segs = m.segments;
    if (*type == StepType::Heat && param_str(p, "mode", "fixed") == "curve") {
        if (static_cast<int>(segs.size()) < schema_codes::kSegmentsMin)
            return bad(op_codes::kProgramInvalid, "曲线模式须给 segments[{temp, rate, hold}]（至少 1 段）",
                       ojson{{"step", ordinal}, {"field", "segments"}});
    } else {
        segs.clear();
    }
    std::string det = step_specs::detail(*type, p, segs, unit);
    return BuildOut{StepSnapshot::create(*type, ordinal, spec->name, det, p, segs), std::nullopt};
}

}  // namespace

ProgramParseResult parse(const json& steps, double leak_rate, PressureUnit unit) {
    ProgramParseResult r;
    auto fail = [&](ProgramJsonError e) {
        ProgramParseResult f;
        f.error = std::move(e);
        return f;
    };
    if (!steps.is_array()) return fail(ProgramJsonError{op_codes::kBadRequest, "steps 必须是数组", ojson()});

    // —— 1. JSON → MigStep ——
    std::vector<MigStep> input;
    int i = 0;
    for (const auto& s : steps) {
        ++i;
        ToMigOut m = to_mig(s, i);
        if (m.error) return fail(*m.error);
        input.push_back(*m.step);
    }
    if (input.empty()) return fail(ProgramJsonError{op_codes::kProgramInvalid, "程序为空", ojson()});

    // —— 2. 旧格式 → v0.8 ——
    MigrationResult mig = step_migration::migrate(input);
    r.migrated = mig.migrated;

    // —— 3. 逐步校验 → 快照（转换后的序号）——
    std::vector<StepSnapshot> list;
    int n = 0;
    for (const auto& m : mig.steps) {
        ++n;
        BuildOut b = build(m, n, r.migrated, leak_rate, r.notes, unit);
        if (b.error) return fail(*b.error);
        list.push_back(*b.snap);
    }
    r.steps = std::move(list);
    return r;
}

}  // namespace reactor::core::program::program_json
