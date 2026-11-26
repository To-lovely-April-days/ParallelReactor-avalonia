// 下发时冻结的一个程序步骤（C3）：参数与曲线段都是独立副本，之后再编辑程序不影响正在执行的程序。
// C#: Models/StepSnapshot.cs（StepSnapshot；StepParams 见 params.hpp）
#pragma once
#include <string>
#include <vector>

#include "core/program/params.hpp"
#include "core/program/step_type.hpp"

namespace reactor::core::program {

class StepSnapshot {
public:
    // 复制输入：之后修改来源不影响快照。C#: StepSnapshot.Create
    static StepSnapshot create(StepType type, int ordinal, std::string name, std::string detail, Params params,
                               std::vector<Segment> segments = {}) {
        return StepSnapshot(type, ordinal, std::move(name), std::move(detail), std::move(params), std::move(segments));
    }

    StepType type() const { return type_; }
    // 在时间线中的序号（1 起）
    int ordinal() const { return ordinal_; }
    const std::string& name() const { return name_; }
    const std::string& detail() const { return detail_; }
    const Params& params() const { return params_; }
    // 曲线段（仅升温曲线模式有意义）
    const std::vector<Segment>& segments() const { return segments_; }

    double num(const std::string& k, double def = 0) const { return param_num(params_, k, def); }
    std::string str(const std::string& k, const std::string& def = "") const { return param_str(params_, k, def); }
    // 升温步骤是否为 16 段曲线模式
    bool is_curve() const { return type_ == StepType::Heat && str("mode", "fixed") == "curve"; }

private:
    StepSnapshot(StepType t, int o, std::string n, std::string d, Params p, std::vector<Segment> s)
        : type_(t), ordinal_(o), name_(std::move(n)), detail_(std::move(d)), params_(std::move(p)), segments_(std::move(s)) {}

    StepType type_;
    int ordinal_;
    std::string name_;
    std::string detail_;
    Params params_;
    std::vector<Segment> segments_;
};

}  // namespace reactor::core::program
