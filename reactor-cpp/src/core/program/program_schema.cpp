#include "core/program/program_schema.hpp"

#include <algorithm>

#include "core/program/program_step_format.hpp"
#include "core/program/schema_codes.hpp"

namespace reactor::core::program::program_schema {

namespace {

using ojson = nlohmann::ordered_json;

ojson opt_str(const std::optional<std::string>& s) { return s ? ojson(*s) : ojson(nullptr); }
ojson opt_num(const std::optional<double>& v) { return v ? json_number(*v) : ojson(nullptr); }

ojson vis_json(const std::optional<VisibleWhen>& w) {
    if (!w) return nullptr;
    ojson in = ojson::array();
    for (const auto& c : w->in) in.push_back(c);
    return ojson{{"key", w->key}, {"in", in}};
}

ojson options_json(const std::vector<SchemaOption>* o) {
    if (!o) return nullptr;
    ojson a = ojson::array();
    for (const auto& x : *o) a.push_back(ojson{{"code", x.code}, {"label", x.label}, {"available", x.available}, {"reason", opt_str(x.reason)}});
    return a;
}

// FieldSchemaDto 的全部 17 个字段（顺序同 C# record）
ojson field_json(const std::string& key, const std::string& label, const std::string& kind, const std::optional<std::string>& unit,
                 const std::optional<double>& dflt, const std::optional<double>& min, const std::optional<double>& max,
                 const std::optional<double>& dmin, const std::optional<double>& dmax, const ojson& options, bool available,
                 const std::optional<std::string>& reason, const ojson& item_fields, const std::optional<int>& min_items,
                 const std::optional<int>& max_items, const ojson& visible_when, const std::optional<std::string>& default_code) {
    ojson f = ojson::object();
    f["key"] = key;
    f["label"] = label;
    f["kind"] = kind;
    f["unit"] = opt_str(unit);
    f["default"] = opt_num(dflt);
    f["min"] = opt_num(min);
    f["max"] = opt_num(max);
    f["declaredMin"] = opt_num(dmin);
    f["declaredMax"] = opt_num(dmax);
    f["options"] = options;
    f["available"] = available;
    f["reason"] = opt_str(reason);
    f["itemFields"] = item_fields;
    f["minItems"] = min_items ? ojson(*min_items) : ojson(nullptr);
    f["maxItems"] = max_items ? ojson(*max_items) : ojson(nullptr);
    f["visibleWhen"] = visible_when;
    f["defaultCode"] = opt_str(default_code);
    return f;
}

}  // namespace

ojson build(const ProgramLimitSettings& limits, int64_t settings_revision, double leak_rate) {
    ojson steps = ojson::array();
    ojson types = ojson::array();
    for (StepType type : kAllStepTypes) {
        const StepSpec& spec = step_specs::of(type);
        ojson fields = ojson::array();
        for (const auto& f : spec.fields) {
            std::string key = to_code(type) + "." + f.key;
            bool available = !schema_codes::is_unavailable_field(key);
            ojson vis = vis_json(f.visible_when);
            if (f.is_sel()) {
                fields.push_back(field_json(f.key, f.label, "sel", std::nullopt, std::nullopt, std::nullopt, std::nullopt, std::nullopt,
                                            std::nullopt, options_json(f.options), true, std::nullopt, nullptr, std::nullopt,
                                            std::nullopt, vis, f.default_sel()));
                continue;
            }
            FieldRange r = program_limits::field_range(type, f.key, f.min, f.max, limits);
            std::optional<double> dflt = type == StepType::Leak && f.key == "maxDrop" ? std::optional<double>(leak_rate) : f.default_value;
            fields.push_back(field_json(f.key, f.label, "num", f.unit, dflt, r.min, r.max, f.min, f.max, nullptr, available,
                                        available ? std::nullopt : std::optional<std::string>(schema_codes::kGasUnavailableReason),
                                        nullptr, std::nullopt, std::nullopt, vis, std::nullopt));
        }
        if (type == StepType::Heat) {
            // 曲线段：曲线编辑器的键盘范围；段温度同样受 TMaxSp / OverTemp 封顶
            ojson seg_fields = ojson::array();
            for (const auto& sf : schema_codes::kSegmentFields) {
                double hi = std::string(sf.key) == "temp" ? std::min(sf.max, program_limits::heat_temp_max(limits)) : sf.max;
                seg_fields.push_back(field_json(sf.key, sf.label, "num", std::string(sf.unit), std::nullopt, sf.min, hi, sf.min, sf.max,
                                                nullptr, true, std::nullopt, nullptr, std::nullopt, std::nullopt, nullptr, std::nullopt));
            }
            fields.push_back(field_json("segments", "曲线段（mode=curve 时使用）", "array", std::nullopt, std::nullopt, std::nullopt,
                                        std::nullopt, std::nullopt, std::nullopt, nullptr, true, std::nullopt, seg_fields,
                                        schema_codes::kSegmentsMin, schema_codes::kSegmentsMax,
                                        vis_json(VisibleWhen{"mode", {"curve"}}), std::nullopt));
        }
        ojson st = ojson::object();
        st["type"] = to_code(type);
        st["label"] = spec.name;
        st["chip"] = spec.chip;
        st["help"] = spec.help;
        st["fields"] = fields;
        steps.push_back(st);
        types.push_back(to_code(type));
    }
    ojson root = ojson::object();
    root["apiVersion"] = kApiVersion;
    root["settingsRevision"] = settings_revision;
    root["stepTypes"] = types;
    root["steps"] = steps;
    return root;
}

}  // namespace reactor::core::program::program_schema
