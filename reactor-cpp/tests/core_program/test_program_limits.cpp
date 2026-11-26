// C#: tests/ParallelReactor.Tests/ProgramLimitsTests.cs
#include "core/program/program_limits.hpp"
#include "core/program/step_fields.hpp"
#include "testing.hpp"

using namespace reactor::core;
using namespace reactor::core::program;
using namespace reactor::core::program::program_limits;

namespace {

const ProgramLimitSettings kDef{200, 250, 1000, 500, 510};   // TMaxSp, OverTemp, RpmMax, PMaxSp, OverPressure

ProgramLimitSettings with(ProgramLimitSettings l, double t_max_sp = -1, double over_temp = -1, double rpm_max = -1, double p_max_sp = -1,
                          double over_pressure = -1) {
    if (t_max_sp >= 0) l.t_max_sp = t_max_sp;
    if (over_temp >= 0) l.over_temp = over_temp;
    if (rpm_max >= 0) l.rpm_max = rpm_max;
    if (p_max_sp >= 0) l.p_max_sp = p_max_sp;
    if (over_pressure >= 0) l.over_pressure = over_pressure;
    return l;
}

StepSnapshot Heat(double t) { return StepSnapshot::create(StepType::Heat, 1, "升温", "", Params{{"temp", t}, {"wait", 15.0}}); }
StepSnapshot Curve(std::vector<Segment> segs) {
    return StepSnapshot::create(StepType::Heat, 1, "升温", "", Params{{"mode", std::string("curve")}}, std::move(segs));
}
StepSnapshot Stir(double rpm) { return StepSnapshot::create(StepType::Stir, 1, "搅拌", "", Params{{"rpm", rpm}}); }
StepSnapshot Press(double p) { return StepSnapshot::create(StepType::Press, 1, "升压", "", Params{{"pres", p}, {"gas", std::string("A")}}); }

StepSnapshot S(StepType t, std::initializer_list<Params::Item> kv = {}) {
    Params p = step_specs::defaults(t);
    for (const auto& i : kv) p.set(i.first, i.second);
    return StepSnapshot::create(t, 1, zh(t), "", p);
}

bool has(const std::optional<std::string>& r, const char* part) { return r && r->find(part) != std::string::npos; }

}  // namespace

TEST(core_program_limits, HeatFixed_Bounds) {
    CHECK(check_step(Heat(300), kDef).has_value());    // > TMaxSp
    CHECK(!check_step(Heat(200), kDef).has_value());   // = TMaxSp
    CHECK(!check_step(Heat(35), kDef).has_value());    // 下限
    CHECK(check_step(Heat(30), kDef).has_value());     // < 35
    CHECK(check_step(Heat(std::nan("")), kDef).has_value());
}

TEST(core_program_limits, HeatFixed_MustBeBelowOverTemp) {
    auto lim = with(kDef, 300, 250);
    CHECK(check_step(Heat(250), lim).has_value());    // = OverTemp 拒绝
    CHECK(!check_step(Heat(249.5), lim).has_value());
    CHECK(has(check_step(Heat(260), lim), "超温"));
}

TEST(core_program_limits, HeatCurve_EmptyOrOverLimitRejected) {
    CHECK(check_step(Curve({}), kDef).has_value());
    CHECK(!check_step(Curve({{80, 5, 10}, {150, 2, 30}}), kDef).has_value());
    auto r = check_step(Curve({{80, 5, 10}, {220, 2, 30}}), kDef);
    CHECK(r.has_value());
    CHECK(has(r, "第 2 段"));
    CHECK(check_step(Curve({{80, 60, 10}}), kDef).has_value());   // 斜率 > 50
    CHECK(check_step(Curve({{80, 5, -1}}), kDef).has_value());    // 保温 < 0
}

TEST(core_program_limits, Stir_Bounds) {
    CHECK(check_step(Stir(2000), kDef).has_value());
    CHECK(!check_step(Stir(1000), kDef).has_value());
    CHECK(!check_step(Stir(0), kDef).has_value());
    CHECK(check_step(Stir(-1), kDef).has_value());
}

TEST(core_program_limits, Press_Bounds) {
    CHECK(!check_step(Press(50), kDef).has_value());
    CHECK(check_step(Press(0), kDef).has_value());
    CHECK(check_step(Press(7.2), kDef).has_value());   // 声明下限 7.3 psi
    CHECK(!check_step(Press(7.3), kDef).has_value());
    auto low_op = with(kDef, -1, -1, -1, -1, 100);       // OverPressure 100 → 上限 90（不含）
    CHECK(check_step(Press(90), low_op).has_value());
    CHECK(!check_step(Press(89.9), low_op).has_value());
    CHECK(has(check_step(Press(50), with(kDef, -1, -1, -1, 40)), "SP 上限"));
    auto bad = StepSnapshot::create(StepType::Press, 1, "充压", "", Params{{"pres", 50.0}, {"gas", std::string("N2")}});
    CHECK(has(check_step(bad, kDef), "不是可选的值"));
}

TEST(core_program_limits, LegacyTypes_Rejected) {
    auto w = StepSnapshot::create(StepType::Wait, 1, "等待", "", Params{{"time", 30.0}});
    CHECK(has(check_step(w, kDef), "旧格式"));
}

TEST(core_program_limits, DeclaredRanges_AndCrossFieldRules) {
    for (StepType t : kAllStepTypes) CHECK(!check_step(S(t), with(kDef, -1, -1, 450)).has_value());   // 默认值都合法
    CHECK(check_step(S(StepType::Exchange, {{"cycles", 21.0}}), kDef).has_value());
    CHECK(check_step(S(StepType::Exchange, {{"cycles", 2.5}}), kDef).has_value());   // 整数
    CHECK(has(check_step(S(StepType::Exchange, {{"fill", 20.0}, {"release", 19.5}}), kDef), "低 1 psi"));
    CHECK(!check_step(S(StepType::Exchange, {{"fill", 20.0}, {"release", 19.0}}), kDef).has_value());
    CHECK(has(check_step(S(StepType::React, {{"time", 20.0}, {"minTime", 30.0}}), kDef), "最短时间"));
    CHECK(check_step(S(StepType::Leak, {{"pres", 14.0}}), kDef).has_value());   // 下限 14.5
    CHECK(check_step(S(StepType::Vent, {{"to", 80.0}}), kDef).has_value());     // 0–72.5
    // 不起作用的字段不校验
    CHECK(!check_step(S(StepType::Hold, {{"until", std::string("time")}, {"temp", 999.0}}), kDef).has_value());
    CHECK(has(check_step(S(StepType::Hold, {{"until", std::string("tempBelow")}, {"temp", 260.0}}), with(kDef, 300)), "超温"));
    // 保持的目标温度不是加热给定值：0 ℃ 合法
    CHECK(!check_step(S(StepType::Hold, {{"until", std::string("tempBelow")}, {"temp", 0.0}}), kDef).has_value());
    // 稳压关时补气阈值不校验
    CHECK(!check_step(S(StepType::Press, {{"regulate", std::string("off")}, {"refill", 99.0}}), kDef).has_value());
    CHECK(check_step(S(StepType::Press, {{"regulate", std::string("on")}, {"refill", 99.0}}), kDef).has_value());
}

TEST(core_program_limits, FieldRange_IntersectsLimits) {
    auto pr = field_range(StepType::Press, "pres", 7.3, 493.1, kDef);
    CHECK_EQ(pr.min, 7.3);
    CHECK_NEAR(pr.max, 458.9, 1e-6);
    CHECK_NEAR(field_range(StepType::Press, "pres", 7.3, 493.1, with(kDef, -1, -1, -1, -1, 100)).max, 89.9, 1e-6);
    auto p60 = with(kDef, -1, -1, -1, 60);
    CHECK_EQ(field_range(StepType::Press, "pres", 7.3, 493.1, p60).max, 60.0);
    CHECK_EQ(field_range(StepType::Leak, "pres", 14.5, 493.1, p60).max, 60.0);
    CHECK_EQ(field_range(StepType::Exchange, "fill", 7.3, 493.1, p60).max, 60.0);
    CHECK_EQ(field_range(StepType::Vent, "flushPres", 7.3, 493.1, p60).max, 60.0);
    CHECK_EQ(field_range(StepType::Vent, "to", 0, 72.5, p60).max, 72.5);   // 放到不受上限约束
    auto h = field_range(StepType::Heat, "temp", 0, 300, kDef);
    CHECK_EQ(h.min, 35.0); CHECK_EQ(h.max, 200.0);
    h = field_range(StepType::Heat, "temp", 0, 300, with(kDef, 300));
    CHECK_EQ(h.min, 35.0); CHECK_EQ(h.max, 249.0);
    auto ht = field_range(StepType::Hold, "temp", 0, 300, kDef);
    CHECK_EQ(ht.min, 0.0); CHECK_EQ(ht.max, 200.0);
    auto rpm = field_range(StepType::Stir, "rpm", 0, 3000, kDef);
    CHECK_EQ(rpm.min, 0.0); CHECK_EQ(rpm.max, 1000.0);
    auto w = field_range(StepType::Heat, "wait", 0, 120, kDef);
    CHECK_EQ(w.min, 0.0); CHECK_EQ(w.max, 120.0);
    auto empty = field_range(StepType::Heat, "temp", 0, 300, with(kDef, 30));
    CHECK_EQ(empty.min, 35.0); CHECK_EQ(empty.max, 35.0);
    CHECK_EQ(field_range(StepType::Exchange, "release", 0, 493.1, kDef, [](const std::string& k) { return k == "fill" ? 72.5 : 0; }).max, 71.5);
    CHECK_EQ(field_range(StepType::React, "minTime", 0, 4320, kDef, [](const std::string& k) { return k == "time" ? 20.0 : 0; }).max, 20.0);
}

TEST(core_program_limits, ClampField_ClampsDefaults) {
    CHECK_EQ(clamp_field(StepType::Stir, "rpm", 1500, 0, 3000, kDef), 1000.0);
    CHECK_EQ(clamp_field(StepType::Heat, "temp", 150, 0, 300, kDef), 150.0);
    CHECK_EQ(clamp_field(StepType::Heat, "temp", 150, 0, 300, with(kDef, 100)), 100.0);
}

TEST(core_program_limits, Validate_MessageIncludesStepIndex) {
    auto errs = validate({Stir(500), Heat(300), Press(50)}, kDef);
    CHECK_EQ(errs.size(), size_t(1));
    CHECK(errs[0].rfind("第 02 步 升温：", 0) == 0);
    CHECK(errs[0].find("300℃") != std::string::npos);
    CHECK(errs[0].find("200℃") != std::string::npos);
    CHECK(validate({Stir(500), Heat(150)}, kDef).empty());
}

TEST(core_program_limits, Messages_Verbatim) {
    CHECK_EQ(*check_step(Heat(300), kDef), std::string("目标 300℃ 高于温度 SP 上限 200℃"));
    CHECK_EQ(*check_step(Press(7.2), kDef), std::string("目标压力 0.050 MPa 无效（须不低于 0.050 MPa）"));
    CHECK_EQ(*check_step(S(StepType::Exchange, {{"cycles", 21.0}}), kDef), std::string("次数 21 次 超出 0 次–20 次"));
    CHECK_EQ(*check_step(S(StepType::Leak, {{"maxDrop", 200.0}}), kDef),
             std::string("允许压降 1.379 MPa/h 超出 0.001 MPa/h–0.689 MPa/h"));
}
