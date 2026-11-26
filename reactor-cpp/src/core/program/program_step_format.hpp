// 下发快照 StepSnapshot → §6.9 程序 JSON（ProgramStepDto），是 program_json::parse 的反方向。
// C#: Api/ProgramStepFormat.cs、Contracts/.../Api/ApiDtos.cs 的 ProgramStepDto
//  - 选项输出英文码（万一是界面 label 经 schema_codes 换成码）；数值输出 JSON 数字（压力 psi）；
//  - heat 步骤总是先写 mode（缺省 fixed），mode = curve 时追加 segments[{temp, rate, hold}]；
//  - GET /channels/{id}/program/run 用它导出运行中程序的冻结步骤；导出的 steps 可原样再 PUT。
// 输出用 nlohmann::ordered_json，键顺序与 C# 一致（参数按插入顺序）。
#pragma once
#include <string>
#include <vector>

#include "nlohmann/json.hpp"
#include "core/program/step_snapshot.hpp"

namespace reactor::core::program {

struct ProgramStepDto {
    std::string type;
    nlohmann::ordered_json params;   // { … }，可原样用于 PUT
    std::string name;                // 只读：中文名
    std::string detail;              // 只读：参数摘要
};

// 数值 → JSON：整数值输出为整数（与 C# Utf8JsonWriter 的 "300" 一致，nlohmann 的 double 会写成 "300.0"），
// 其余先按 15 位有效数字取整再输出（458.90000000000003 → 458.9）；非有限数输出 null。
nlohmann::ordered_json json_number(double v);

namespace program_step_format {
ProgramStepDto format(const StepSnapshot& s);
std::vector<ProgramStepDto> format(const std::vector<StepSnapshot>& steps);
// { "type", "params", "name", "detail" }（camelCase，同 C# ApiJson）
nlohmann::ordered_json to_json(const ProgramStepDto& d);
nlohmann::ordered_json to_json(const std::vector<ProgramStepDto>& list);
}  // namespace program_step_format

}  // namespace reactor::core::program
