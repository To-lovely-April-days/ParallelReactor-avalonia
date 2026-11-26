// C#: tests/ParallelReactor.Tests/VolumeCalibTests.cs（随机噪声换成 std::mt19937，性质不变）
#include <random>
#include <string>
#include <utility>
#include <vector>

#include "core/volume_calib.hpp"
#include "testing.hpp"
#include <optional>

using namespace reactor::core;
namespace vc = reactor::core::volume_calib;
using Steps = std::vector<std::pair<double, double>>;

namespace {
// 按物理过程生成一遍数据：总管 vm 充到 preg，放进气相 vg 的釜里；crack = 单向阀开启压力
Steps Run(double vm, double vg, double preg, double p0 = 0, double crack = 0, int n = vc::kSteps,
          std::mt19937* noise = nullptr, double noise_psi = 0) {
    Steps list;
    std::uniform_real_distribution<double> u(0.0, 1.0);
    auto rd = [&](double x) { return noise ? x + (u(*noise) - 0.5) * 2 * noise_psi : x; };
    double p = p0;
    for (int k = 0; k < n; k++) {
        double next = (vm * (preg - crack) + vg * p) / (vm + vg);
        double a = rd(p);
        double b = rd(next);
        list.push_back({a, b});
        p = next;
    }
    return list;
}
bool contains(const std::optional<std::string>& s, const char* sub) {
    return s && s->find(sub) != std::string::npos;
}
}  // namespace

TEST(core_volume_calib, fit_recovers_ratio_without_knowing_supply_pressure) {
    auto r = vc::fit_steps(Run(8, 52.3, 80));
    CHECK(!r.why);
    CHECK(r.fit.has_value());
    if (!r.fit) return;
    CHECK_NEAR(r.fit->ratio(), 52.3 / 8, 1e-9);
    CHECK_NEAR(r.fit->supply_psi(), 80, 1e-6);
    CHECK_NEAR(r.fit->r2, 1, 1e-9);
}

TEST(core_volume_calib, fit_ignores_check_valve_cracking_pressure_and_start_pressure) {
    auto r = vc::fit_steps(Run(8, 40, 80, 5, 3));
    CHECK(!r.why);
    if (!r.fit) return;
    CHECK_NEAR(r.fit->ratio(), 5.0, 1e-9);
    CHECK_NEAR(r.fit->supply_psi(), 77, 1e-6);
}

TEST(core_volume_calib, fit_tolerates_small_noise) {
    std::mt19937 rnd(3);
    auto r = vc::fit_steps(Run(8, 52, 100, 0, 0, vc::kSteps, &rnd, 0.05));
    CHECK(!r.why);
    if (!r.fit) return;
    CHECK(r.fit->ratio() >= 6.5 * 0.98 && r.fit->ratio() <= 6.5 * 1.02);
}

TEST(core_volume_calib, fit_rejects_bad_data) {
    CHECK(vc::fit_steps(Run(8, 52, 80, 0, 0, 2)).why.has_value());
    CHECK(contains(vc::fit_steps(Run(8, 52, 0.5)).why, "只升了"));
    std::mt19937 rnd(1);
    CHECK(vc::fit_steps(Run(8, 52, 80, 0, 0, vc::kSteps, &rnd, 3)).why.has_value());
    auto bad = Run(8, 52, 80);
    bad[2].second = NAN;
    CHECK_EQ(vc::fit_steps(bad).why.value_or(""), std::string("压力读数无效"));
}

TEST(core_volume_calib, small_vessel_late_steps_near_supply_are_dropped) {
    auto steps = Run(20, 9.6, 70);
    CHECK(steps.back().second - steps.back().first < vc::kMinRisePsi);
    auto r = vc::fit_steps(steps);
    CHECK(!r.why);
    if (!r.fit) return;
    CHECK_NEAR(r.fit->ratio(), 9.6 / 20, 1e-9);
}

TEST(core_volume_calib, two_runs_ten_ml_vessel) {
    const double vm = 6, veff = 10.8, water = 5;
    auto r = vc::solve_two_runs(vc::fit_steps(Run(vm, veff, 70)).fit->ratio(),
                                vc::fit_steps(Run(vm, veff - water, 70)).fit->ratio(), water);
    CHECK(!r.why);
    CHECK_NEAR(r.manifold_ml.value_or(0), vm, 1e-6);
    CHECK_NEAR(r.effective_ml.value_or(0), veff, 1e-6);
}

TEST(core_volume_calib, two_runs_solve_manifold_and_effective_volume) {
    const double vm = 7.5, veff = 52.4, water = 20;
    auto f1 = vc::fit_steps(Run(vm, veff, 80));
    auto f2 = vc::fit_steps(Run(vm, veff - water, 80));
    auto r = vc::solve_two_runs(f1.fit->ratio(), f2.fit->ratio(), water);
    CHECK(!r.why);
    CHECK_NEAR(r.manifold_ml.value_or(0), vm, 1e-6);
    CHECK_NEAR(r.effective_ml.value_or(0), veff, 1e-6);

    auto f3 = vc::fit_steps(Run(vm, 49.1, 80));
    auto e3 = vc::solve_one_run(f3.fit->ratio(), r.manifold_ml.value_or(0));
    CHECK(!e3.why);
    CHECK_NEAR(e3.effective_ml.value_or(0), 49.1, 1e-6);
}

TEST(core_volume_calib, two_runs_reject_inconsistent_inputs) {
    CHECK(vc::solve_two_runs(6, 6.2, 20).why.has_value());
    CHECK(vc::solve_two_runs(6, 4, 0).why.has_value());
    CHECK(vc::solve_two_runs(6, 5.99, 20).why.has_value());
    CHECK(vc::solve_one_run(6, 0).why.has_value());
}

// 文案逐字（C# 无对应断言，补充核对格式化）
TEST(core_volume_calib, messages_verbatim) {
    CHECK_EQ(vc::fit_steps(Run(8, 52, 0.5)).why.value_or(""),
             std::string("每步压力只升了 0.07 psi · 惰性气体减压阀压力太低或气源没开"));
    CHECK_EQ(vc::solve_one_run(6, 0).why.value_or(""), std::string("还没有总管体积 · 先做一次「空釜 + 加水」两遍标定"));
    CHECK_EQ(vc::solve_two_runs(6, 5.99, 20).why.value_or(""),
             std::string("结果不合理（总管 2000 mL、有效容积 12000 mL）· 检查加水体积"));
    // f = vm/(vm+vg) = 0.95 越界（减压阀压力很高，3 步都升得明显）
    CHECK_EQ(vc::fit_steps(Run(19, 1, 10000, 0, 0, 3)).why.value_or(""),
             std::string("每步进气比例 0.95 不合理（应在 0.01–0.9）"));
}
