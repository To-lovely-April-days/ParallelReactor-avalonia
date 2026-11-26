// 程序 schema 的枚举码 ↔ 界面文案映射表（设计稿 §6.9），曲线段字段范围。
// C#: Contracts/ParallelReactor.Contracts/Api/ProgramSchemaCodes.cs
#pragma once
#include <optional>
#include <string>
#include <vector>

namespace reactor::core::program {

// sel 字段的一个枚举项：协议用英文码，中文只是 label。C#: SchemaOption
struct SchemaOption {
    SchemaOption(std::string c, std::string l, bool avail = true, std::optional<std::string> why = std::nullopt)
        : code(std::move(c)), label(std::move(l)), available(avail), reason(std::move(why)) {}

    std::string code;
    std::string label;
    bool available = true;
    std::optional<std::string> reason;
};

// 曲线段字段（ProgramViewModel.EditSegTemp / Rate / Hold 的键盘范围）
struct SegmentFieldSpec {
    const char* key;
    const char* label;
    const char* unit;
    double min;
    double max;
};

namespace schema_codes {

// 历史常量：v0.5 之前耗气量未实现时的说明文字（保留供旧客户端引用）
constexpr const char* kGasUnavailableReason = "耗气量计算未实现";
// 曲线段数范围（TempProfile.MaxSegments = 16）
constexpr int kSegmentsMin = 1;
constexpr int kSegmentsMax = 16;

// 段字段：temp 0–300 °C、rate 0–50 °C/min、hold 0–600 分钟
extern const SegmentFieldSpec kSegmentFields[3];

// 键 "stepType.fieldKey" 的选项（含旧格式 purge.gas / wait.mode / react.endBy / end.mode）；找不到返回 nullptr
const std::vector<SchemaOption>* options(const std::string& key);
// 旧格式才有的选项键（/program-schema 不列出）
bool is_legacy_key(const std::string& key);
// 不可用的数值字段（目前为空）
bool is_unavailable_field(const std::string& key);

// 界面文案 → 枚举码（找不到 nullopt）
std::optional<std::string> code_of(const std::string& step_type, const std::string& key, const std::string& label);
// 枚举码 → 界面文案（找不到 nullopt）
std::optional<std::string> label_of(const std::string& step_type, const std::string& key, const std::string& code);

}  // namespace schema_codes
}  // namespace reactor::core::program
