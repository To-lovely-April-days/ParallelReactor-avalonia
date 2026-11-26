// 一个可编辑的程序步骤（触屏程序页 / 程序草稿用），以及检查器字段的显示单位换算。
// C#: Models/ProgramStep.cs 的纯逻辑部分（ProgramStep、HeatSeg、StepField 的范围 / 写回规则）；
// 图标、颜色、Avalonia 绑定不搬。
#pragma once
#include <string>
#include <vector>

#include "core/heat_curve_segments.hpp"
#include "core/program/params.hpp"
#include "core/program/step_fields.hpp"
#include "core/program/step_snapshot.hpp"
#include "core/program/step_type.hpp"
#include "core/program/text_format.hpp"

namespace reactor::core::program {

class ProgramStep {
public:
    explicit ProgramStep(StepType type) : type_(type) {}

    // 按字段默认值新建（数值 = 默认值，选项 = 默认码）。C#: ProgramStep.New
    static ProgramStep create(StepType type);
    // 由快照还原为可编辑的步骤（远程整体替换程序用）。C#: ProgramStep.FromSnapshot
    static ProgramStep from_snapshot(const StepSnapshot& snap);

    StepType type() const { return type_; }
    int ordinal() const { return ordinal_; }
    void set_ordinal(int o) { ordinal_ = o; }
    // "01" / "02" …
    std::string index_text() const { return fmt_00(ordinal_); }

    const Params& params() const { return p_; }
    ProgramStep& with(const std::string& k, ParamValue v) { p_.set(k, std::move(v)); return *this; }
    double num(const std::string& k, double def = 0) const { return param_num(p_, k, def); }
    std::string str(const std::string& k, const std::string& def = "") const { return param_str(p_, k, def); }
    void set_num(const std::string& k, double v) { p_.set(k, v); }
    void set_str(const std::string& k, const std::string& v) { p_.set(k, v); }

    // 升温曲线段（仅 curve 模式用）
    std::vector<Segment>& segments() { return segs_; }
    const std::vector<Segment>& segments() const { return segs_; }
    // 没有段时补默认四段（80/5/10、130/3/20、150/2/30、150/0/60）。C#: EnsureSegments
    void ensure_segments();

    // 静态定义（旧码 purge / wait 不在表里时按「结束」的定义，同 C# Def 的回退）
    const StepSpec& spec() const;
    const std::string& name() const { return spec().name; }
    std::string detail(PressureUnit unit = PressureUnit::MPa) const { return step_specs::detail(type_, p_, segs_, unit); }
    DurationEstimate duration() const { return step_duration::of(type_, p_, segs_); }
    double duration_min() const { return duration().minutes; }

    // 下发用的不可变快照：曲线模式下缺省段只补在副本上。C#: ToSnapshot
    StepSnapshot to_snapshot(PressureUnit unit = PressureUnit::MPa) const;

private:
    StepType type_;
    int ordinal_ = 0;
    Params p_;
    std::vector<Segment> segs_;
};

// 检查器数值字段的显示范围（C#: StepField.DisplayRange）：range 为 psi / 原单位（通常是 ProgramLimits::field_range 的结果）；
// 压力字段换成显示单位后按显示小数位向内取整（键盘能输入的最大 / 最小值换回 psi 后仍在范围内）。
struct DisplayRange { double min; double max; };
DisplayRange display_range(const FieldDef& f, double lo, double hi, PressureUnit unit);
// 检查器显示的当前数值（压力字段换成显示单位并按小数位取整）。C#: StepField.Number
double display_number(const FieldDef& f, double stored, PressureUnit unit);
// 屏幕键盘确认后写回的存储值（v 为显示单位）。C#: StepField.SetNumber
double stored_from_display(const FieldDef& f, double v, PressureUnit unit);

// 步骤参数里的曲线段 → core 曲线换算的输入（再交给 core::heat_curve_to_profile 得到曲线引擎分段）
std::vector<HeatSegment> to_heat_segments(const std::vector<Segment>& segs);

}  // namespace reactor::core::program
