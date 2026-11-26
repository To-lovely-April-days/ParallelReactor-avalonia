// C#: tests/ParallelReactor.Tests/StepSpecsTests.cs、StepSnapshotTests.cs
// （ProgramStepTests.cs 的 HeatCurvePlanTests 已由 core 模块翻译在 tests/core/test_heat_curve.cpp，这里不重复）
#include <algorithm>
#include <set>

#include "core/program/program_limits.hpp"
#include "core/program/program_step.hpp"
#include "core/program/step_fields.hpp"
#include "core/program/step_snapshot.hpp"
#include "testing.hpp"
#include <string>
#include <vector>

using namespace reactor::core;
using namespace reactor::core::program;

namespace {

const ProgramLimitSettings kDef{200, 250, 450, 500, 510};

StepSnapshot S(StepType t, std::initializer_list<Params::Item> kv = {}) {
    Params p = step_specs::defaults(t);
    for (const auto& i : kv) p.set(i.first, i.second);
    return StepSnapshot::create(t, 1, zh(t), "", p);
}

bool contains(const std::vector<std::string>& v, const std::string& part) {
    return std::any_of(v.begin(), v.end(), [&](const std::string& s) { return s.find(part) != std::string::npos; });
}

}  // namespace

// ============ StepSpecsTests ============

TEST(core_program_specs, Registry_HasExactlyTheNineTypes_NoCool) {
    int n = 0;
    for (StepType t : kAllStepTypes) { CHECK(step_specs::find(t) != nullptr); ++n; }
    CHECK_EQ(n, 9);
    CHECK(step_specs::find(StepType::Purge) == nullptr);
    CHECK(step_specs::find(StepType::Wait) == nullptr);
    CHECK(step_specs::of(StepType::End).fields.empty());
}

TEST(core_program_specs, Defaults_AndMinimums_PerSpec) {
    struct Row { StepType t; const char* key; double def; double min; };
    const Row rows[] = {
        {StepType::Leak, "pres", 435.1, 14.5}, {StepType::Leak, "settle", 5, 0}, {StepType::Leak, "hold", 30, 5},
        {StepType::Leak, "maxDrop", 2, 0.1}, {StepType::Exchange, "fill", 72.5, 7.3}, {StepType::Exchange, "release", 2.9, 0},
        {StepType::Exchange, "cycles", 3, 0}, {StepType::Exchange, "lineFlush", 2, 0}, {StepType::Exchange, "maxSec", 60, 10},
        {StepType::Stir, "rpm", 400, 0}, {StepType::Heat, "temp", 80, 35}, {StepType::Heat, "rate", 0, 0},
        {StepType::Heat, "band", 2, 0.5}, {StepType::Heat, "wait", 15, 0}, {StepType::Heat, "maxWait", 120, 10},
        {StepType::Press, "pres", 290.1, 7.3}, {StepType::Press, "tol", 2, 0.5}, {StepType::Press, "refill", 3, 1},
        {StepType::Press, "maxSec", 180, 30}, {StepType::Hold, "time", 10, 0}, {StepType::Hold, "temp", 40, 0},
        {StepType::Hold, "pres", 14.5, 0}, {StepType::Hold, "maxWait", 60, 1}, {StepType::React, "time", 480, 1},
        {StepType::React, "minTime", 30, 0}, {StepType::React, "mmol", 5, 0.01}, {StepType::React, "stallRate", 0.005, 0.001},
        {StepType::React, "stallMin", 30, 5}, {StepType::Vent, "to", 2.9, 0}, {StepType::Vent, "maxSec", 120, 10},
        {StepType::Vent, "flush", 3, 0}, {StepType::Vent, "flushPres", 43.5, 7.3},
    };
    for (const auto& r : rows) {
        const FieldDef* f = step_specs::of(r.t).field(r.key);
        CHECK(f != nullptr);
        if (!f) continue;
        CHECK_EQ(f->default_value.value_or(-1), r.def);
        CHECK_EQ(f->min, r.min);
        CHECK_EQ(param_num(step_specs::defaults(r.t), r.key, -1), r.def);
    }
}

TEST(core_program_specs, SelDefaults_PerSpec) {
    struct Row { StepType t; const char* key; const char* code; };
    const Row rows[] = {
        {StepType::Leak, "onFail", "stop"}, {StepType::Leak, "after", "vent"}, {StepType::Exchange, "gas", "inert"},
        {StepType::Heat, "mode", "fixed"}, {StepType::Heat, "onTimeout", "stop"}, {StepType::Press, "gas", "A"},
        {StepType::Press, "regulate", "on"}, {StepType::Hold, "until", "time"}, {StepType::Hold, "onTimeout", "continue"},
        {StepType::React, "mmolOn", "off"}, {StepType::React, "stallOn", "on"},
    };
    for (const auto& r : rows) CHECK_EQ(param_str(step_specs::defaults(r.t), r.key), std::string(r.code));
}

TEST(core_program_specs, PressureFields_AreMarked) {
    std::vector<std::string> pressure;
    for (StepType t : kAllStepTypes)
        for (const auto& f : step_specs::of(t).fields)
            if (f.pressure) pressure.push_back(to_code(t) + "." + f.key);
    std::vector<std::string> want = {"leak.pres", "leak.maxDrop", "exchange.fill", "exchange.release",
                                     "press.pres", "hold.pres", "vent.to", "vent.flushPres"};
    CHECK(pressure == want);
}

TEST(core_program_specs, VisibleWhen) {
    const StepSpec& hold = step_specs::of(StepType::Hold);
    Params p = step_specs::defaults(StepType::Hold);
    CHECK(step_specs::is_visible(StepType::Hold, *hold.field("time"), p));
    CHECK(!step_specs::is_visible(StepType::Hold, *hold.field("temp"), p));
    CHECK(!step_specs::is_visible(StepType::Hold, *hold.field("maxWait"), p));
    p.set("until", std::string("tempBelow"));
    CHECK(step_specs::is_visible(StepType::Hold, *hold.field("temp"), p));
    CHECK(!step_specs::is_visible(StepType::Hold, *hold.field("pres"), p));
    CHECK(step_specs::is_visible(StepType::Hold, *hold.field("onTimeout"), p));
    // 控制字段没写：按默认码
    const StepSpec& react = step_specs::of(StepType::React);
    CHECK(!step_specs::is_visible(StepType::React, *react.field("mmol"), Params{}));
    CHECK(step_specs::is_visible(StepType::React, *react.field("stallRate"), Params{}));
    const StepSpec& heat = step_specs::of(StepType::Heat);
    CHECK(!step_specs::is_visible(StepType::Heat, *heat.field("temp"), Params{{"mode", std::string("curve")}}));
    CHECK(step_specs::is_visible(StepType::Heat, *heat.field("band"), Params{{"mode", std::string("curve")}}));
}

TEST(core_program_specs, Duration_PerSpec5) {
    CHECK_EQ(step_duration::of(S(StepType::Leak)).minutes, 36.0);   // 5 + 30 + 1
    CHECK_NEAR(step_duration::of(S(StepType::Exchange)).minutes, (2 * 9 + 3 * 20) / 60.0, 1e-9);
    CHECK_EQ(step_duration::of(S(StepType::Stir)).minutes, 0.0);
    CHECK_NEAR(step_duration::of(S(StepType::Heat)).minutes, 55 / 3.5 + 15, 1e-9);   // |80−25| / 3.5 + 15
    CHECK_NEAR(step_duration::of(S(StepType::Heat, {{"rate", 5.0}})).minutes, 55 / 5.0 + 15, 1e-9);
    CHECK_EQ(step_duration::of(S(StepType::Press)).minutes, 3.0);
    CHECK_EQ(step_duration::of(S(StepType::Hold)).minutes, 10.0);
    auto cond = step_duration::of(S(StepType::Hold, {{"until", std::string("tempBelow")}, {"maxWait", 60.0}}));
    CHECK_EQ(cond.minutes, 15.0);
    CHECK(cond.approx);
    CHECK_EQ(step_duration::of(S(StepType::Hold, {{"until", std::string("pressAbove")}, {"maxWait", 5.0}})).minutes, 5.0);
    auto react = step_duration::of(S(StepType::React));
    CHECK_EQ(react.minutes, 480.0);
    CHECK(react.upper_bound);
    CHECK_EQ(step_duration::of(S(StepType::Vent)).minutes, 2.0);   // 1 + 3 × 20 / 60
    CHECK_EQ(step_duration::of(S(StepType::End)).minutes, 0.0);
    auto curve = StepSnapshot::create(StepType::Heat, 1, "升温", "", Params{{"mode", std::string("curve")}}, {{80, 5, 10}, {120, 2, 20}});
    CHECK_NEAR(step_duration::of(curve).minutes, 11 + 10 + 20 + 20, 1e-9);
    auto total = step_duration::total({S(StepType::Hold, {{"until", std::string("tempBelow")}}), S(StepType::React, {{"time", 60.0}})});
    CHECK_EQ(total.text(), std::string("约 ≤ 1h 15m"));
}

TEST(core_program_specs, PreRun_Template_PassesWithoutWarnings) {
    std::vector<StepSnapshot> steps;
    int i = 0;
    for (const auto& t : step_templates::hydrogenation()) steps.push_back(StepSnapshot::create(t.type, ++i, zh(t.type), "", t.params));
    auto r = program_limits::pre_run(steps, kDef);
    CHECK(r.errors.empty());
    CHECK(r.warnings.empty());
    std::vector<StepType> want = {StepType::Leak, StepType::Exchange, StepType::Exchange, StepType::Stir, StepType::Heat,
                                  StepType::Press, StepType::React, StepType::Vent, StepType::End};
    CHECK_EQ(steps.size(), want.size());
    for (size_t k = 0; k < want.size() && k < steps.size(); ++k) CHECK(steps[k].type() == want[k]);
    CHECK_EQ(steps[1].str("gas"), std::string("inert"));
    CHECK_EQ(steps[2].str("gas"), std::string("A"));
    for (const auto& s : steps) CHECK(!is_legacy(s.type()));
}

TEST(core_program_specs, PreRun_EndRules) {
    auto no_end = program_limits::pre_run({S(StepType::Stir)}, kDef);
    CHECK(contains(no_end.errors, "没有「结束」"));
    auto two = program_limits::pre_run({S(StepType::End), S(StepType::End)}, kDef);
    CHECK(contains(two.errors, "2 个「结束」"));
    auto mid = program_limits::pre_run({S(StepType::End), S(StepType::Stir)}, kDef);
    CHECK(contains(mid.errors, "必须是最后一步"));
}

TEST(core_program_specs, PreRun_Warnings) {
    // 反应前没有充压 + 没有放空
    auto r = program_limits::pre_run({S(StepType::Heat), S(StepType::React), S(StepType::End)}, kDef);
    CHECK(r.errors.empty());
    CHECK(contains(r.warnings, "吸气量不计量"));
    CHECK(contains(r.warnings, "结束时釜里可能带压"));
    // 充压之后放空再反应：同样不计量
    r = program_limits::pre_run({S(StepType::Press), S(StepType::Vent), S(StepType::React), S(StepType::End)}, kDef);
    CHECK(contains(r.warnings, "吸气量不计量"));
    // 先充压后升温：提示估算值
    r = program_limits::pre_run({S(StepType::Press, {{"pres", 100.0}}), S(StepType::Heat, {{"temp", 80.0}}), S(StepType::Vent), S(StepType::End)}, kDef);
    CHECK(r.errors.empty());
    CHECK(std::any_of(r.warnings.begin(), r.warnings.end(), [](const std::string& w) {
        return w.find("升温后压力会高于目标") != std::string::npos && w.find("稳压不会放气") != std::string::npos;
    }));
    double est = program_limits::estimate_after_heat(100, 25, 80);
    CHECK_NEAR(est, (100 + 14.696) * 353.15 / 298.15 - 14.696, 1e-6);
    // 估算超过压力上限 → 拒绝
    r = program_limits::pre_run({S(StepType::Press, {{"pres", 420.0}}), S(StepType::Heat, {{"temp", 150.0}}), S(StepType::Vent), S(StepType::End)}, kDef);
    CHECK(contains(r.errors, "超过压力上限"));
    // 先升温到位再充压：不提示
    r = program_limits::pre_run({S(StepType::Heat, {{"temp", 80.0}}), S(StepType::Press, {{"pres", 100.0}}), S(StepType::Heat, {{"temp", 80.0}}),
                                 S(StepType::Vent), S(StepType::End)},
                                kDef);
    CHECK(!contains(r.warnings, "升温后压力"));
}

TEST(core_program_specs, Detail_UsesPressureDisplayUnit) {
    CHECK_EQ(step_specs::detail(StepType::Press, step_specs::defaults(StepType::Press), {}), std::string("气体 A 到 2.000 MPa · 稳压开"));
    CHECK_EQ(step_specs::detail(StepType::Exchange, step_specs::defaults(StepType::Exchange), {}),
             std::string("惰性气体 · 0.500 MPa → 0.020 MPa × 3 · 先吹总管 2 次"));
    CHECK_EQ(step_specs::detail(StepType::End, Params{}, {}), std::string("关加热、阀全关、停搅拌"));
    // 其他单位（C# 里由 Units.SetPressure 切换）
    CHECK_EQ(step_specs::detail(StepType::Press, step_specs::defaults(StepType::Press), {}, PressureUnit::Psi),
             std::string("气体 A 到 290.1 psi · 稳压开"));
}

// ============ StepSnapshotTests ============

TEST(core_program_snapshot, MutatingSource_DoesNotAffectSnapshot) {
    Params p{{"temp", 150.0}, {"mode", std::string("curve")}};
    std::vector<Segment> segs = {{80, 5, 10}, {150, 2, 30}};
    auto s = StepSnapshot::create(StepType::Heat, 2, "升温", "曲线", p, segs);
    p.set("temp", 300.0);
    p.set("extra", std::string("x"));
    p.erase("mode");
    segs[0] = {250, 50, 0};
    segs.clear();
    CHECK_EQ(s.num("temp"), 150.0);
    CHECK_EQ(s.str("mode"), std::string("curve"));
    CHECK(!s.params().contains("extra"));
    CHECK_EQ(s.segments().size(), size_t(2));
    CHECK(s.segments()[0] == (Segment{80, 5, 10}));
    CHECK(s.is_curve());
    CHECK_EQ(s.ordinal(), 2);
}

TEST(core_program_snapshot, Snapshot_IsReadOnly) {
    // C++：StepSnapshot 只暴露 const 访问（编译期保证）；这里只查空段
    auto s = StepSnapshot::create(StepType::Stir, 1, "搅拌", "", Params{{"rpm", 500.0}});
    CHECK(s.segments().empty());
}

TEST(core_program_snapshot, NumStr_ParseLikeProgramStep) {
    auto s = StepSnapshot::create(StepType::Press, 1, "升压", "",
                                  Params{{"pres", std::string("42.5")}, {"bad", std::string("abc")}, {"gas", std::string("气体 B")}, {"num", 3.0}});
    CHECK_EQ(s.num("pres"), 42.5);
    CHECK_EQ(s.num("bad", 7), 7.0);
    CHECK_EQ(s.num("missing", 9), 9.0);
    CHECK_EQ(s.str("gas"), std::string("气体 B"));
    CHECK_EQ(s.str("num", "d"), std::string("d"));
    CHECK_EQ(s.str("missing"), std::string(""));
    CHECK(!s.is_curve());
}

// ============ ProgramStep（编辑中的步骤）============

TEST(core_program_step, NewToSnapshot_CurveGetsDefaultSegmentsOnCopyOnly) {
    ProgramStep st = ProgramStep::create(StepType::Heat);
    st.set_ordinal(4);
    st.set_str("mode", "curve");
    auto snap = st.to_snapshot();
    CHECK(st.segments().empty());               // 编辑中的步骤不变
    CHECK_EQ(snap.segments().size(), size_t(4));
    CHECK_EQ(snap.ordinal(), 4);
    CHECK_EQ(snap.name(), std::string("升温"));
    CHECK(snap.detail().find("16 段曲线 · 4 段 · 终温 150°C") == 0);
    auto back = ProgramStep::from_snapshot(snap);
    CHECK_EQ(back.segments().size(), size_t(4));
    CHECK_EQ(back.str("mode"), std::string("curve"));
    CHECK_EQ(st.index_text(), std::string("04"));
    // 曲线段交给 core 的曲线换算（C# ProgramStep.ToProfileSegments）
    auto prof = heat_curve_to_profile(to_heat_segments(snap.segments()), 25);
    CHECK(!prof.empty());
    CHECK_NEAR(prof[0].temp, 80, 1e-12);
    CHECK_NEAR(prof[0].minutes, 11, 1e-12);
}

TEST(core_program_step, DisplayRange_RoundsInwardForPressure) {
    const FieldDef& pres = *step_specs::of(StepType::Press).field("pres");
    auto r = display_range(pres, 7.3, 458.9, PressureUnit::MPa);
    CHECK_NEAR(r.min, 0.051, 1e-12);   // 7.3 psi = 0.05033 MPa → 向上取到 0.051
    CHECK_NEAR(r.max, 3.164, 1e-12);   // 458.9 psi = 3.16400 MPa → 向下
    CHECK_NEAR(stored_from_display(pres, 2.0, PressureUnit::MPa), 290.075, 1e-9);
    const FieldDef& rpm = *step_specs::of(StepType::Stir).field("rpm");
    CHECK_EQ(stored_from_display(rpm, 400.6, PressureUnit::MPa), 401.0);
    CHECK_NEAR(display_number(pres, 290.1, PressureUnit::MPa), 2.0, 1e-12);
}

// ============ 文案格式 ============

TEST(core_program_format, NumberFormats_MatchDotNet) {
    CHECK_EQ(fmt_n(300), std::string("300"));
    CHECK_EQ(fmt_n(0.005), std::string("0.005"));
    CHECK_EQ(fmt_n(2.5), std::string("2.5"));
    CHECK_EQ(fmt_n(1.23456), std::string("1.235"));
    CHECK_EQ(fmt_dec(12.0, 2, 2), std::string("12.00"));
    CHECK_EQ(fmt_dec(2.675, 2, 2), std::string("2.68"));   // 15 位有效数字后远离零
    CHECK_EQ(fmt_dec(0.25, 1, 0), std::string("0.3"));
    CHECK_EQ(fmt_dec(-0.04, 1, 0), std::string("0"));
    CHECK_EQ(fmt_dec(9.96, 1, 1), std::string("10.0"));
    CHECK_EQ(format_p(290.1, PressureUnit::MPa), std::string("2.000 MPa"));
    CHECK_EQ(format_p(2.9, PressureUnit::MPa), std::string("0.020 MPa"));
    CHECK_EQ(format_p(50, PressureUnit::Psi), std::string("50.0 psi"));
    CHECK_EQ(format_p(50, PressureUnit::Bar), std::string("3.45 bar"));
    CHECK_EQ(fmt_00(2), std::string("02"));
    CHECK_EQ(round_even(2.5), 2.0);
    CHECK_EQ(round_even(3.5), 4.0);
}
