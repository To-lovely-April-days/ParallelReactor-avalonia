// GET /program-schema（v0.8，STEPS-SPEC §6）。
// C#: Api/ProgramSchemaBuilder.cs、Contracts/.../Api/ApiDtos.cs（ProgramSchemaDto / StepSchemaDto / FieldSchemaDto / VisibleWhenDto）
// 只列九种步骤（kAllStepTypes 顺序），字段取自 step_specs。JSON 形状与 C# 一致（camelCase，null 照常输出）：
//   { apiVersion, settingsRevision, stepTypes[], steps[{ type, label, chip, help, fields[] }] }
//   field = { key, label, kind, unit, default, min, max, declaredMin, declaredMax, options, available, reason,
//             itemFields, minItems, maxItems, visibleWhen, defaultCode }
//   option = { code, label, available, reason }；visibleWhen = { key, in[] } 或 null
// min / max 是按设备设置收紧的生效范围，declaredMin / declaredMax 是字段声明范围；压力单位一律 "psi"（maxDrop 为 "psi/h"）；
// leak.maxDrop 的 default = 当前设置 LeakRate；heat 先列 mode，最后是 segments（itemFields + minItems 1 / maxItems 16）。
#pragma once
#include <cstdint>

#include "nlohmann/json.hpp"
#include "core/program/program_limits.hpp"
#include "core/program/step_fields.hpp"

namespace reactor::core::program {

namespace program_schema {
constexpr const char* kApiVersion = "1.0";   // C#: ApiVersionInfo.ApiVersion
nlohmann::ordered_json build(const ProgramLimitSettings& limits, int64_t settings_revision,
                             double leak_rate = step_specs::kLeakRateDefault);
}  // namespace program_schema

}  // namespace reactor::core::program
