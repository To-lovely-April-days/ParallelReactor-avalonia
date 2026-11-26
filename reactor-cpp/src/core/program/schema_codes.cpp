#include "core/program/schema_codes.hpp"

#include <map>

namespace reactor::core::program::schema_codes {

const SegmentFieldSpec kSegmentFields[3] = {
    {"temp", "段目标温度", "°C", 0, 300},
    {"rate", "升温速率", "°C/min", 0, 50},
    {"hold", "保温时间", "分钟", 0, 600},
};

namespace {

std::vector<SchemaOption> gas3() { return {{"inert", "惰性气体"}, {"A", "气体 A"}, {"B", "气体 B"}}; }
std::vector<SchemaOption> on_off() { return {{"on", "开启"}, {"off", "关闭"}}; }

// C#: ProgramSchemaCodes.BuildOptions（v0.8 + 旧格式）
const std::map<std::string, std::vector<SchemaOption>>& table() {
    static const std::map<std::string, std::vector<SchemaOption>> t = {
        // —— v0.8 ——
        {"leak.onFail", {{"stop", "停止这一路"}, {"continue", "记录后继续"}}},
        {"leak.after", {{"vent", "放空"}, {"keep", "保持压力"}}},
        {"exchange.gas", gas3()},
        {"heat.mode", {{"fixed", "定值"}, {"curve", "曲线"}}},
        {"heat.onTimeout", {{"stop", "停止这一路"}, {"continue", "继续"}}},
        {"press.gas", gas3()},
        {"press.regulate", on_off()},
        {"hold.until",
         {{"time", "时间到"}, {"tempAbove", "温度 ≥"}, {"tempBelow", "温度 ≤"}, {"pressAbove", "压力 ≥"}, {"pressBelow", "压力 ≤"}}},
        {"hold.onTimeout", {{"continue", "继续"}, {"stop", "停止这一路"}}},
        {"react.mmolOn", on_off()},
        {"react.stallOn", on_off()},
        // —— 旧格式（输入时认，转换用） ——
        {"purge.gas", gas3()},
        {"wait.mode", {{"fixed", "固定时长"}, {"temp", "等待温度到位"}, {"pressure", "等待压力到位"}}},
        {"react.endBy", {{"time", "时间"}, {"gasConsumed", "气体消耗"}, {"either", "两者先到"}}},
        {"end.mode", {{"seal", "密封 (推荐)"}, {"purge", "吹扫"}, {"quench", "淬灭"}, {"vent", "排空"}}},
    };
    return t;
}

}  // namespace

const std::vector<SchemaOption>* options(const std::string& key) {
    auto it = table().find(key);
    return it == table().end() ? nullptr : &it->second;
}

bool is_legacy_key(const std::string& key) {
    return key == "purge.gas" || key == "wait.mode" || key == "react.endBy" || key == "end.mode";
}

bool is_unavailable_field(const std::string&) { return false; }   // C#: UnavailableFields 为空

std::optional<std::string> code_of(const std::string& step_type, const std::string& key, const std::string& label) {
    const auto* o = options(step_type + "." + key);
    if (!o) return std::nullopt;
    for (const auto& x : *o)
        if (x.label == label) return x.code;
    return std::nullopt;
}

std::optional<std::string> label_of(const std::string& step_type, const std::string& key, const std::string& code) {
    const auto* o = options(step_type + "." + key);
    if (!o) return std::nullopt;
    for (const auto& x : *o)
        if (x.code == code) return x.label;
    return std::nullopt;
}

}  // namespace reactor::core::program::schema_codes
