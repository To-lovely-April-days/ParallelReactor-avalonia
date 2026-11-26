// 旧格式 → v0.8 步骤（STEPS-SPEC §3）。纯逻辑，HMI 与 PC 共用。
// C#: Contracts/ParallelReactor.Contracts/Models/StepMigration.cs（MigStep / StepMigration）
//  - 旧格式的识别：类型 purge / wait；stir 带 dur；end 带 mode / cool；react 带 endBy。
//  - 一个旧步骤可能变成几个新步骤；转换后末尾保证只有一个 end（没有就补，多余的去掉，只留最后）。
//  - 新格式的程序原样返回（只补末尾的 end）；Result::migrated 告诉调用方有没有转换过。
#pragma once
#include <optional>
#include <string>
#include <vector>

#include "core/program/params.hpp"

namespace reactor::core::program {

// 转换用的一步（线上层面）：类型码、参数（选项 = 英文码字符串，数值 = double，压力 = psi）、曲线段
struct MigStep {
    std::string type;
    Params p;
    std::vector<Segment> segments;

    MigStep() = default;
    explicit MigStep(std::string t, Params params = {}, std::vector<Segment> segs = {})
        : type(std::move(t)), p(std::move(params)), segments(std::move(segs)) {}

    // 数值：double 直接取，字符串按不变区域解析；缺失 / 无法解析 nullopt。C#: MigStep.Num
    std::optional<double> num(const std::string& k) const;
    // 字符串：只认 string。C#: MigStep.Str
    std::optional<std::string> str(const std::string& k) const;
    MigStep& with(const std::string& k, ParamValue v) { p.set(k, std::move(v)); return *this; }
    bool operator==(const MigStep& o) const { return type == o.type && p == o.p && segments == o.segments; }
};

struct MigrationResult {
    std::vector<MigStep> steps;
    bool migrated = false;
};

namespace step_migration {
// 这一步是不是旧格式
bool is_legacy(const MigStep& s);
MigrationResult migrate(const std::vector<MigStep>& input);
}  // namespace step_migration

}  // namespace reactor::core::program
