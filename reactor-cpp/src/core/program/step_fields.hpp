// v0.8 九种步骤的字段表（STEPS-SPEC §1）：字段、单位、默认值、范围、显示条件、参数摘要；时长估算（§5）；推荐模板。
// C#: Models/StepFields.cs（FieldDef / StepSpec / StepSpecs / DurationEstimate / StepDuration / StepTemplates）
// 触屏检查器、接口 PUT 解析、/program-schema、下发前校验共用这一张表。
#pragma once
#include <functional>
#include <optional>
#include <string>
#include <vector>

#include "core/program/params.hpp"
#include "core/program/schema_codes.hpp"
#include "core/program/step_type.hpp"
#include "core/program/text_format.hpp"

namespace reactor::core::program {

class StepSnapshot;

// 字段受哪一个全局上限约束（ProgramLimits.FieldRange / CheckStep 用）
enum class FieldLimit { None, Temp, Pressure, Rpm };

// 显示条件：同一步骤里选项字段 key 的当前码在 in 里才显示 / 才起作用
struct VisibleWhen {
    std::string key;
    std::vector<std::string> in;
};

// 步骤的一个参数字段。C#: FieldDef
struct FieldDef {
    std::string key;
    std::string label;
    std::string kind = "num";              // num | sel
    std::optional<std::string> unit;       // 压力字段为 "psi" / "psi/h"
    const std::vector<SchemaOption>* options = nullptr;   // 取自 schema_codes
    std::optional<double> default_value;   // 数值默认值
    std::optional<std::string> default_code;   // 选项默认码（缺省取第一个选项）
    double min = 0;
    double max = 9999;
    bool integer = false;                  // 只取整数（次数、转速）
    bool pressure = false;                 // 压力类字段：存 psi，显示按压力单位
    FieldLimit limit = FieldLimit::None;
    std::optional<VisibleWhen> visible_when;
    bool custom_editor = false;            // 触屏检查器不按普通字段显示（升温方式）

    bool is_sel() const { return kind == "sel"; }
    std::string default_sel() const;
    // 码 → 界面文字（找不到原样返回）
    std::string label_of(const std::string& code) const;
    // 界面文字或码 → 码（找不到 nullopt）
    std::optional<std::string> code_of(const std::string& text) const;
};

using DetailFn = std::function<std::string(const Params&, const std::vector<Segment>&, PressureUnit)>;

// 步骤静态定义（名称 / 角标 / 帮助 / 字段 / 摘要）。C#: StepSpec
struct StepSpec {
    StepType type = StepType::End;
    std::string name;
    std::string chip;
    std::string help;
    std::vector<FieldDef> fields;
    DetailFn detail;

    const FieldDef* field(const std::string& key) const;
};

namespace step_specs {

constexpr double kLeakRateDefault = 2;        // C#: StepSpecs.LeakRateDefault（psi/h，设置 LeakRate 出厂值）
constexpr double kPressDeclaredMax = 493.1;   // C#: StepSpecs.PressDeclaredMax（psi，34 bar）
constexpr double kTempDeclaredMax = 300;      // C#: StepSpecs.TempDeclaredMax（℃）
constexpr double kRpmDeclaredMax = 3000;      // C#: StepSpecs.RpmDeclaredMax（rpm）

// 九种步骤（旧码 purge / wait 不在表里）：找不到返回 nullptr
const StepSpec* find(StepType t);
// 九种步骤之一（调用方保证在表里）
const StepSpec& of(StepType t);

// 新建步骤的参数（数值 = 默认值，选项 = 默认码），按字段顺序
Params defaults(StepType t);
// 字段在当前参数下是否显示 / 起作用（visibleWhen）；控制字段没写时按它的默认码判断
bool is_visible(StepType t, const FieldDef& f, const Params& p);
// 参数摘要（压力按 unit 显示）；不在表里的类型返回中文名
std::string detail(StepType t, const Params& p, const std::vector<Segment>& segs, PressureUnit unit = PressureUnit::MPa);
// 曲线总时长（分钟，从 start_temp 起算；同曲线编辑器预览）
double curve_minutes(const std::vector<Segment>& segs, double start_temp = 25);
// 气体码 → 界面文字（惰性气体 / 气体 A / 气体 B）
std::string gas_label(const std::string& type, const std::string& code);
// 气体码 → 气源下标（0 = 惰性，1 = A，2 = B；同 ValveFrame）
int gas_index(const std::string& code);
// 压降速率 psi/h → 「x 单位/h」
std::string p_rate(double psi_per_hour, PressureUnit unit = PressureUnit::MPa);

}  // namespace step_specs

// 程序时长估算（STEPS-SPEC §5，分钟）。approx = 含按条件结束的保持（「约」），upper_bound = 含反应（「≤」）。
struct DurationEstimate {
    double minutes = 0;
    bool approx = false;
    bool upper_bound = false;

    DurationEstimate operator+(const DurationEstimate& b) const {
        return {minutes + b.minutes, approx || b.approx, upper_bound || b.upper_bound};
    }
    // 「约 ≤ 9h 10m」
    std::string text() const;
};

namespace step_duration {
DurationEstimate of(StepType t, const Params& p, const std::vector<Segment>& segs);
DurationEstimate of(const StepSnapshot& s);
DurationEstimate total(const std::vector<StepSnapshot>& steps);
}  // namespace step_duration

// 推荐模板。C#: StepTemplates
struct TemplateStep {
    StepType type;
    Params params;
};
namespace step_templates {
// 加氢筛选（默认模板）：leak → exchange 惰性 → exchange A → stir → heat → press → react → vent → end
std::vector<TemplateStep> hydrogenation(double leak_rate = step_specs::kLeakRateDefault);
}  // namespace step_templates

}  // namespace reactor::core::program
