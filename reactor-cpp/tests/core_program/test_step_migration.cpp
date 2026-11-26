// C#: tests/ParallelReactor.Tests/StepMigrationTests.cs
#include "core/program/step_migration.hpp"
#include "testing.hpp"
#include <string>
#include <utility>
#include <vector>

using namespace reactor::core;
using namespace reactor::core::program;

namespace {

MigStep M(const char* type, std::initializer_list<Params::Item> kv = {}) { return MigStep(type, Params(kv)); }

std::vector<MigStep> Run(bool& migrated, std::vector<MigStep> steps) {
    auto r = step_migration::migrate(steps);
    migrated = r.migrated;
    return r.steps;
}

std::vector<std::string> Types(const std::vector<MigStep>& s) {
    std::vector<std::string> t;
    for (const auto& x : s) t.push_back(x.type);
    return t;
}

using V = std::vector<std::string>;
double num(const MigStep& s, const char* k) { return s.num(k).value_or(-12345); }
std::string str(const MigStep& s, const char* k) { return s.str(k).value_or("<none>"); }

}  // namespace

TEST(core_program_migration, Purge_BecomesLineFlushOnlyExchange) {
    bool mig;
    auto o = Run(mig, {M("purge", {{"gas", std::string("A")}, {"cycles", 4.0}, {"fill", 3.0}, {"vent", 5.0}})});
    CHECK(mig);
    CHECK(Types(o) == (V{"exchange", "end"}));
    CHECK_EQ(str(o[0], "gas"), std::string("A"));
    CHECK_EQ(num(o[0], "cycles"), 0.0);
    CHECK_EQ(num(o[0], "lineFlush"), 4.0);
    CHECK(!o[0].p.contains("fill"));   // 其余取默认
}

TEST(core_program_migration, HeatAndPress_PassThrough) {
    MigStep heat("heat", Params{{"mode", std::string("curve")}, {"temp", 80.0}, {"wait", 10.0}}, {{60, 2, 5}, {120, 1, 30}});
    MigStep press = M("press", {{"gas", std::string("B")}, {"pres", 100.0}, {"regulate", std::string("off")}});
    bool mig;
    auto o = Run(mig, {heat, press, M("end")});
    CHECK(!mig);   // heat / press 没有旧参数
    CHECK(Types(o) == (V{"heat", "press", "end"}));
    CHECK(o[0] == heat);   // C#: Assert.Same（原样保留）
    CHECK_EQ(o[0].segments.size(), size_t(2));
    CHECK_EQ(str(o[1], "gas"), std::string("B"));
    CHECK_EQ(num(o[1], "pres"), 100.0);
}

TEST(core_program_migration, Stir_WithDuration_BecomesStirHoldStir) {
    bool mig;
    auto o = Run(mig, {M("stir", {{"rpm", 600.0}, {"dur", 12.0}})});
    CHECK(mig);
    CHECK(Types(o) == (V{"stir", "hold", "stir", "end"}));
    CHECK_EQ(num(o[0], "rpm"), 600.0);
    CHECK(!o[0].p.contains("dur"));
    CHECK_EQ(str(o[1], "until"), std::string("time"));
    CHECK_EQ(num(o[1], "time"), 12.0);
    CHECK_EQ(num(o[2], "rpm"), 0.0);
}

TEST(core_program_migration, Stir_ZeroDuration_OnlyStir) {
    bool mig;
    auto o = Run(mig, {M("stir", {{"rpm", 300.0}, {"dur", 0.0}})});
    CHECK(mig);   // 带 dur 就是旧格式
    CHECK(Types(o) == (V{"stir", "end"}));
    CHECK_EQ(num(o[0], "rpm"), 300.0);
}

TEST(core_program_migration, Stir_StopWithDuration_KeepsTheWait) {
    bool mig;
    auto o = Run(mig, {M("stir", {{"rpm", 0.0}, {"dur", 5.0}})});
    CHECK(Types(o) == (V{"stir", "hold", "end"}));
    CHECK_EQ(num(o[1], "time"), 5.0);
}

TEST(core_program_migration, WaitFixed_BecomesHoldTime) {
    bool mig;
    auto o = Run(mig, {M("wait", {{"mode", std::string("fixed")}, {"time", 25.0}})});
    CHECK(mig);
    CHECK(Types(o) == (V{"hold", "end"}));
    CHECK_EQ(str(o[0], "until"), std::string("time"));
    CHECK_EQ(num(o[0], "time"), 25.0);
}

TEST(core_program_migration, WaitTemp_UsesPreviousHeatTarget_Or40) {
    bool mig;
    auto o = Run(mig, {M("heat", {{"mode", std::string("fixed")}, {"temp", 120.0}}), M("wait", {{"mode", std::string("temp")}, {"time", 45.0}})});
    CHECK_EQ(str(o[1], "until"), std::string("tempAbove"));
    CHECK_EQ(num(o[1], "temp"), 120.0);
    CHECK_EQ(num(o[1], "maxWait"), 45.0);
    CHECK_EQ(str(o[1], "onTimeout"), std::string("continue"));

    MigStep curve("heat", Params{{"mode", std::string("curve")}}, {{80, 2, 0}, {150, 1, 30}});
    o = Run(mig, {curve, M("wait", {{"mode", std::string("temp")}, {"time", 10.0}})});
    CHECK_EQ(num(o[1], "temp"), 150.0);   // 曲线取最后一段

    o = Run(mig, {M("wait", {{"mode", std::string("temp")}, {"time", 10.0}})});
    CHECK_EQ(num(o[0], "temp"), 40.0);    // 前面没有升温
}

TEST(core_program_migration, WaitPressure_UsesPreviousPressTimes097_Or14_5) {
    bool mig;
    auto o = Run(mig, {M("press", {{"gas", std::string("A")}, {"pres", 100.0}}), M("wait", {{"mode", std::string("pressure")}, {"time", 30.0}})});
    CHECK_EQ(str(o[1], "until"), std::string("pressAbove"));
    CHECK_EQ(num(o[1], "pres"), 97.0);
    CHECK_EQ(num(o[1], "maxWait"), 30.0);
    CHECK_EQ(str(o[1], "onTimeout"), std::string("continue"));
    o = Run(mig, {M("wait", {{"mode", std::string("pressure")}, {"time", 30.0}})});
    CHECK_EQ(num(o[0], "pres"), 14.5);
}

TEST(core_program_migration, React_EndBy_MapsToMmolOn) {
    const std::pair<const char*, const char*> rows[] = {{"time", "off"}, {"gasConsumed", "on"}, {"either", "on"}};
    for (const auto& r : rows) {
        bool mig;
        auto o = Run(mig, {M("react", {{"endBy", std::string(r.first)}, {"time", 90.0}, {"mmol", 2.5}})});
        CHECK(mig);
        CHECK(Types(o) == (V{"react", "end"}));
        CHECK_EQ(num(o[0], "time"), 90.0);
        CHECK_EQ(num(o[0], "minTime"), 0.0);
        CHECK_EQ(str(o[0], "mmolOn"), std::string(r.second));
        CHECK_EQ(num(o[0], "mmol"), 2.5);
        CHECK_EQ(str(o[0], "stallOn"), std::string("off"));
        CHECK(!o[0].p.contains("endBy"));
    }
}

TEST(core_program_migration, EndSeal_BecomesHoldTempBelowThenEnd) {
    bool mig;
    auto o = Run(mig, {M("end", {{"mode", std::string("seal")}, {"cool", 45.0}})});
    CHECK(mig);
    CHECK(Types(o) == (V{"hold", "end"}));
    CHECK_EQ(str(o[0], "until"), std::string("tempBelow"));
    CHECK_EQ(num(o[0], "temp"), 45.0);
    CHECK_EQ(num(o[0], "maxWait"), 60.0);
    CHECK_EQ(str(o[0], "onTimeout"), std::string("continue"));
    CHECK(o[1].p.empty());   // 新的 end 没有参数
}

TEST(core_program_migration, EndVentOrQuench_BecomesHoldVentEnd) {
    for (const char* mode : {"vent", "quench"}) {
        bool mig;
        auto o = Run(mig, {M("end", {{"mode", std::string(mode)}, {"cool", 50.0}})});
        CHECK(Types(o) == (V{"hold", "vent", "end"}));
        CHECK_EQ(num(o[0], "temp"), 50.0);
        CHECK_EQ(num(o[1], "flush"), 0.0);
    }
}

TEST(core_program_migration, EndPurge_BecomesHoldExchangeEnd) {
    bool mig;
    auto o = Run(mig, {M("end", {{"mode", std::string("purge")}, {"cool", 40.0}})});
    CHECK(Types(o) == (V{"hold", "exchange", "end"}));
    CHECK_EQ(str(o[1], "gas"), std::string("inert"));
    CHECK_EQ(num(o[1], "cycles"), 0.0);
    CHECK_EQ(num(o[1], "lineFlush"), 3.0);
}

TEST(core_program_migration, NewFormat_Unchanged_EndAppendedIfMissing) {
    std::vector<MigStep> steps = {M("exchange", {{"gas", std::string("inert")}}), M("stir", {{"rpm", 400.0}}),
                                  M("hold", {{"until", std::string("time")}, {"time", 5.0}})};
    bool mig;
    auto o = Run(mig, steps);
    CHECK(!mig);   // 只补 end 不算转换
    CHECK(Types(o) == (V{"exchange", "stir", "hold", "end"}));
    CHECK(o[1] == steps[1]);
}

TEST(core_program_migration, ExtraEnds_KeepOnlyTheLast_AndFlagMigrated) {
    bool mig;
    auto o = Run(mig, {M("end"), M("stir", {{"rpm", 400.0}}), M("end")});
    CHECK(Types(o) == (V{"stir", "end"}));
    CHECK(mig);
}

TEST(core_program_migration, EndInTheMiddle_MovedToLast) {
    bool mig;
    auto o = Run(mig, {M("stir", {{"rpm", 400.0}}), M("end"), M("hold", {{"until", std::string("time")}, {"time", 1.0}})});
    CHECK(Types(o) == (V{"stir", "hold", "end"}));
    CHECK(mig);
}

TEST(core_program_migration, LegacyRecognition) {
    CHECK(step_migration::is_legacy(M("purge")));
    CHECK(step_migration::is_legacy(M("wait")));
    CHECK(step_migration::is_legacy(M("stir", {{"rpm", 1.0}, {"dur", 1.0}})));
    CHECK(step_migration::is_legacy(M("end", {{"mode", std::string("seal")}})));
    CHECK(step_migration::is_legacy(M("end", {{"cool", 40.0}})));
    CHECK(step_migration::is_legacy(M("react", {{"endBy", std::string("time")}})));
    CHECK(!step_migration::is_legacy(M("stir", {{"rpm", 400.0}})));
    CHECK(!step_migration::is_legacy(M("end")));
    CHECK(!step_migration::is_legacy(M("react", {{"time", 60.0}, {"mmolOn", std::string("on")}})));
}

TEST(core_program_migration, WholeLegacyProgram_ConvertsInOrder) {
    bool mig;
    auto o = Run(mig, {M("purge", {{"gas", std::string("inert")}, {"cycles", 10.0}}), M("heat", {{"temp", 150.0}, {"wait", 15.0}}),
                       M("press", {{"gas", std::string("A")}, {"pres", 50.0}, {"regulate", std::string("on")}}),
                       M("stir", {{"rpm", 400.0}, {"dur", 5.0}}),
                       M("react", {{"endBy", std::string("either")}, {"time", 120.0}, {"mmol", 5.0}}),
                       M("end", {{"mode", std::string("vent")}, {"cool", 40.0}})});
    CHECK(mig);
    CHECK(Types(o) == (V{"exchange", "heat", "press", "stir", "hold", "stir", "react", "hold", "vent", "end"}));
}

TEST(core_program_migration, MigStepNum_ParsesStrings) {
    MigStep s = M("x", {{"a", std::string("1.5")}, {"b", std::string("abc")}, {"c", 2.0}});
    CHECK_EQ(num(s, "a"), 1.5);
    CHECK(!s.num("b").has_value());
    CHECK_EQ(num(s, "c"), 2.0);
    CHECK(!s.str("c").has_value());
}
