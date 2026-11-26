// §6.9 程序 JSON（PUT /channels/{id}/program 的 steps）→ 下发快照 StepSnapshot（v0.8，STEPS-SPEC §6）。
// C#: Api/ProgramJson.cs
//  1. 每步 { type, params{…} }（也接受参数直接写在步骤对象里）；选项传英文码（也容忍中文 label），数值传 JSON 数字，压力 psi；
//  2. 新旧格式都收：先整体换成 MigStep，按 step_migration::migrate 转换；末尾保证只有一个 end；
//  3. 逐步校验：未知类型 / 选项 → PROGRAM_INVALID；数值超出声明范围 → PARAM_OUT_OF_RANGE（detail{ step, field, value, min, max }）；
//     旧格式转换来的程序则把超出新范围的值收紧并写进 notes；全局限值由调用方再用 program_limits::validate 查；
//  4. heat.mode = curve 时取 segments[{temp, rate, hold}]（1..16 段）；缺省字段取默认值。
#pragma once
#include <optional>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"
#include "core/program/step_fields.hpp"
#include "core/program/step_snapshot.hpp"
#include "core/program/text_format.hpp"

namespace reactor::core::program {

// 失败结果（对应 C# OpResult.Fail(code, message, "err", detail)）
struct ProgramJsonError {
    std::string code;            // §11 错误码
    std::string message;         // 中文消息（「第 2 步：…」）
    nlohmann::ordered_json detail;   // error.detail（{ step, … }，字段顺序同 C# 匿名对象）
};

struct ProgramParseResult {
    std::optional<std::vector<StepSnapshot>> steps;   // 失败为 nullopt
    bool migrated = false;
    std::vector<std::string> notes;                   // 旧值超出新范围被收紧等
    std::optional<ProgramJsonError> error;
};

namespace program_json {
// steps：JSON 数组。leak_rate = 气密检查没给 maxDrop 时的默认值（设置 LeakRate，psi/h）；unit = 步骤 detail 文字的压力单位。
ProgramParseResult parse(const nlohmann::json& steps, double leak_rate = step_specs::kLeakRateDefault,
                         PressureUnit unit = PressureUnit::MPa);
}  // namespace program_json

}  // namespace reactor::core::program
