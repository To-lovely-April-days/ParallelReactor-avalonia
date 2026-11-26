// C#: tests/ParallelReactor.Tests/LeakFitTests.cs
// LeakRecord（逐点记录 / 汇总 / CSV）归存储模块；Compensation 用例只翻译其中拟合 + 温度补偿的数学部分，
// Summary_OnlyUsesMeasureWindow 整条属于 LeakRecord，不翻译。
#include <random>
#include <utility>
#include <vector>

#include "core/leak_fit.hpp"
#include "core/valve_frame.hpp"
#include "testing.hpp"
#include <cmath>

using namespace reactor::core;
namespace lf = reactor::core::leak_fit;
using Pts = std::vector<std::pair<double, double>>;

TEST(core_leak_fit, perfect_line_slope_intercept_and_r2) {
    Pts pts;
    for (int i = 0; i < 10; i++) pts.push_back({i, 3.0 - 0.5 * i});
    auto f = lf::fit(pts);
    CHECK_NEAR(f.slope, -0.5, 1e-12);
    CHECK_NEAR(f.intercept, 3.0, 1e-12);
    CHECK_NEAR(f.r2, 1.0, 1e-12);
    CHECK_EQ(f.n, 10);
}

TEST(core_leak_fit, known_noisy_data_r2) {
    auto f = lf::fit({{1, 2}, {2, 4}, {3, 5}, {4, 4}, {5, 5}});
    CHECK_NEAR(f.slope, 0.6, 1e-10);
    CHECK_NEAR(f.intercept, 2.2, 1e-10);
    CHECK_NEAR(f.r2, 0.6, 1e-10);
}

TEST(core_leak_fit, degenerate_is_nan) {
    CHECK(!lf::fit({}).valid());
    CHECK(!lf::fit({{1, 2}}).valid());
    CHECK(!lf::fit({{1, 2}, {1, 3}}).valid());
    auto flat = lf::fit({{0, 5}, {1, 5}, {2, 5}});
    CHECK_NEAR(flat.slope, 0, 1e-12);
    CHECK_NEAR(flat.r2, 1, 1e-12);
}

TEST(core_leak_fit, fit_per_hour_converts_seconds_slope) {
    Pts pts;
    for (int i = 0; i < 7; i++) pts.push_back({i * 10.0, 50 - 0.001 * i * 10});
    CHECK_NEAR(lf::fit_per_hour(pts).slope, -3.6, 1e-9);
}

TEST(core_leak_fit, slope_matches_existing_drop_rate_with_opposite_sign) {
    std::mt19937 rnd(1);
    std::uniform_real_distribution<double> u(0.0, 1.0);
    Pts min_psi, sec_psi;
    for (int i = 0; i < 30; i++) {
        double m = i / 6.0, p = 50 - 0.05 * i + u(rnd) * 0.02;
        min_psi.push_back({m, p});
        sec_psi.push_back({m * 60, p});
    }
    double drop = leak_math::drop_rate_per_hour(min_psi);
    CHECK_NEAR(lf::fit_per_hour(sec_psi).slope, -drop, 1e-9);
}

TEST(core_leak_fit, compensation_removes_pure_thermal_drift) {
    // 密闭无泄漏：P_abs ∝ T。降温 30→28 ℃ 时表压下降，补偿到 T_ref = 30 ℃ 后绝压恒定
    double p_abs0 = lf::psig_to_abs_kpa(50), t_ref = 30;
    Pts raw, comp;
    for (int i = 0; i <= 10; i++) {
        double t = t_ref - 0.2 * i;
        double p_abs = p_abs0 * (t + 273.15) / (t_ref + 273.15);
        double psig = (p_abs - lf::kAtmKpa) / lf::kKpaPerPsi;
        raw.push_back({i * 10.0, psig});
        comp.push_back({i * 10.0, lf::corrected_abs_kpa(psig, t, t_ref)});
    }
    auto fr = lf::fit_per_hour(raw);
    auto fc = lf::fit_per_hour(comp);
    CHECK(fr.slope < -1);               // 未补偿：看起来在漏
    CHECK_NEAR(fc.slope, 0, 1e-6);      // 补偿后：无泄漏
    CHECK_NEAR(fr.r2, 1.0, 1e-9);
    CHECK_NEAR(raw[0].second, 50, 1e-6);
    CHECK(std::isnan(lf::corrected_abs_kpa(50, NAN, 30)));
    CHECK(std::isnan(lf::corrected_abs_kpa(50, -300, 30)));
}
