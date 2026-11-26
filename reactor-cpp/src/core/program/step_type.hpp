// 程序步骤类型 ↔ 协议小写码 ↔ 中文名。
// C#: Contracts/ParallelReactor.Contracts/Models/StepType.cs、StepTypeCodes.cs
// v0.8（STEPS-SPEC）：新程序只用 leak / exchange / stir / heat / press / hold / react / vent / end 九种；
// purge / wait 是旧格式，只在输入时认，读进来立即转换（step_migration.hpp），引擎不执行。
#pragma once
#include <array>
#include <optional>
#include <string>

namespace reactor::core::program {

// 枚举顺序照抄 C#（追加在末尾，旧值序号不变）
enum class StepType { Purge, Heat, Press, Stir, Wait, React, End, Leak, Exchange, Hold, Vent };

// 新程序可用的步骤（编辑器「添加步骤」的顺序，也是 /program-schema 的顺序）。C#: StepTypeCodes.All
constexpr std::array<StepType, 9> kAllStepTypes = {
    StepType::Leak, StepType::Exchange, StepType::Stir, StepType::Heat, StepType::Press,
    StepType::Hold, StepType::React, StepType::Vent, StepType::End,
};

// 旧格式的步骤类型（purge / wait）。C#: StepTypeCodes.IsLegacy
bool is_legacy(StepType t);
// 小写码 "leak" / "purge" …。C#: StepTypeCodes.ToCode
std::string to_code(StepType t);
// 码 → 类型（去首尾空白、不分大小写；含旧码 purge / wait）。C#: StepTypeCodes.TryParse
std::optional<StepType> parse_step_type(const std::string& code);
// 中文名（触屏 / PC 程序页一致）。C#: StepTypeCodes.Zh
std::string zh(StepType t);

}  // namespace reactor::core::program
