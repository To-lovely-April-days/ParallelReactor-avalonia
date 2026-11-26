// 程序步骤参数与全局限值的校验（C4 + STEPS-SPEC §1 / §4），下发前检查 PreRun。
// C#: Models/ProgramLimits.cs
//  - 每个起作用的字段（visibleWhen 成立）都在 StepSpecs 声明的范围内，选项是表里的码；
//  - 「上限」：温度 ≤ min(TMaxSp, OverTemp − 1)（校验只要求 < OverTemp），压力 ≤ PMaxSp 且 < OverPressure × 0.9，转速 ≤ RpmMax；
//  - 跨字段：exchange.release ≤ fill − 1，react.minTime ≤ time；升温曲线 1–16 段，段温度同上限、斜率 0–50、保温 ≥ 0。
// 文案里的压力按 unit 显示（C# 用 HMI 当前的压力单位，默认 MPa）。
#pragma once
#include <functional>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "core/program/step_snapshot.hpp"
#include "core/program/step_type.hpp"
#include "core/program/text_format.hpp"

namespace reactor::core::program {

// 全局限值（取自设置；不直接引用设置模块）。C#: ProgramLimitSettings
struct ProgramLimitSettings {
    double t_max_sp = 0;        // 温度 SP 上限 ℃
    double over_temp = 0;       // 超温报警值 ℃（加热目标必须严格低于它）
    double rpm_max = 0;         // 搅拌转速上限 rpm（釜转速）
    double p_max_sp = 0;        // 压力 SP 上限 psi
    double over_pressure = 0;   // 超压报警值 psi（压力目标须低于其 90%）
};

// 下发前检查的结果：errors 拒绝下发，warnings 只提示
struct PreRunResult {
    std::vector<std::string> errors;
    std::vector<std::string> warnings;
};

struct FieldRange { double min; double max; };

namespace program_limits {

constexpr double kHeatTempMin = 35;    // C#: ProgramLimits.HeatTempMin（定值升温的最低目标温度）
constexpr double kRateMax = 50;        // C#: ProgramLimits.RateMax（曲线斜率上限 ℃/min）
constexpr double kPressRatio = 0.9;    // C#: ProgramLimits.PressRatio（压力目标 / 超压报警值）
constexpr double kRoomTempC = 25;      // C#: ProgramLimits.RoomTempC（没有前置升温时充压时釜温）
constexpr int kMaxCurveSegments = 16;  // C#: TempProfile.MaxSegments

// 编辑器里加热目标的上限：min(TMaxSp, OverTemp − 1)
double heat_temp_max(const ProgramLimitSettings& l);
// 编辑器里压力目标的上限：min(PMaxSp, OverPressure × 0.9 − 0.1)
double press_max(const ProgramLimitSettings& l);
double rpm_max_allowed(const ProgramLimitSettings& l);

using Sibling = std::function<double(const std::string&)>;
// 数值字段的可输入范围：声明范围 ∩ 全局限值；sibling 给出同一步骤其他字段时再按跨字段规则收紧。交集为空返回 (min, min)。
FieldRange field_range(StepType type, const std::string& key, double min, double max, const ProgramLimitSettings& l,
                       const Sibling& sibling = nullptr);
// 把数值裁剪到 field_range（新建步骤的默认值用）；非有限数返回下限
double clamp_field(StepType type, const std::string& key, double v, double min, double max, const ProgramLimitSettings& l);

// 单步校验：通过 nullopt，否则原因（不含步骤序号）
std::optional<std::string> check_step(const StepSnapshot& s, const ProgramLimitSettings& l, PressureUnit unit = PressureUnit::MPa);
// 整段逐步校验：全部违规项（「第 02 步 升温：…」），空 = 通过
std::vector<std::string> validate(const std::vector<StepSnapshot>& steps, const ProgramLimitSettings& l,
                                  PressureUnit unit = PressureUnit::MPa);
// 下发前检查（STEPS-SPEC §4）
PreRunResult pre_run(const std::vector<StepSnapshot>& steps, const ProgramLimitSettings& l, PressureUnit unit = PressureUnit::MPa);
// 升温后的压力估算（psi 表压）：等容理想气体
double estimate_after_heat(double target_psig, double t_at_c, double t_after_c);
// 升温步骤的目标温度：定值取 temp，曲线取最后一段（曲线无段 nullopt）
std::optional<double> heat_target(const StepSnapshot& s);

}  // namespace program_limits
}  // namespace reactor::core::program
