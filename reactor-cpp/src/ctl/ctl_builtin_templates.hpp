// 内置程序模板（决定记录 2026-11-26「内置程序模板在哪」：把 PC 端 ProgramTemplates.BuiltIn 移植到后台，启动时种进 library/templates，只读）。
// C#: Control/ParallelReactor.Control.Core/Programs/Editor/ProgramTemplates.cs（Hydrogenation / LeakOnly / InertHeating / UptakeCheck / BuiltIn）
//     + StepDefaults.cs（Of / Fill / PsiToStored / RateToStored：缺的参数按 STEPS-SPEC §1 默认值补，压力按 MPa 取 2 位、压降速率取 4 位）
//     + PcProgram.Write（条目形状：params 先写选项、再写数值，曲线段放在 params.segments）。
// 条目形状同 PC 端 templates.json 的一项 { "note", "from", "program": { id, name, model, modified, by, draft, steps } }，另加 "builtIn": true。
// 压力存 MPa（同 PC 程序库）；pc_steps_to_wire 把它换成 HMI §6.9 的 psi 格式（StepDefaults.ToWire：压力 1 位、压降 2 位），可直接给 program_json::parse。
//
// 线程：纯函数；seed 调 ProgramLibrary（内部有锁，任何线程可调）。
#pragma once
#include <string>
#include <vector>

#include "nlohmann/json.hpp"
#include "store/program_library.hpp"

namespace reactor::ctl::builtin_templates {

using ojson = nlohmann::ordered_json;

constexpr const char* kModified = "2026-11-23T00:00:00";  // 内置模板的「修改时刻」固定（PC 端随软件版本）
constexpr const char* kBy = "内置";

// 四个内置模板（T1 加氢筛选 · T2 只做气密 · T3 惰性气氛加热 · T4 吸气量验证），顺序同 C# BuiltIn
std::vector<ojson> all(const std::string& model = "PR8");

struct SeedResult {
    int added = 0;       // 新放进去的
    int unchanged = 0;   // 已有且内容相同
    int outdated = 0;    // 已有但和本版不同、没能刷新（现在用 upsert_builtin 刷新，正常为 0）
    int updated = 0;     // 已有但和本版不同 → 已按本版刷新（revision + 1）
    std::vector<std::string> errors;
};
// 开机调用一次：内置模板经 ProgramLibrary::upsert_builtin 放进去 / 按本版刷新（软件升级时跟着更新，同 C#）
SeedResult seed(store::ProgramLibrary& lib, const std::string& model = "PR8");

// PC 形状的步骤（压力 MPa）→ HMI §6.9 的步骤（压力 psi；只换数值，其余原样）
nlohmann::json pc_steps_to_wire(const ojson& steps);

}  // namespace reactor::ctl::builtin_templates
