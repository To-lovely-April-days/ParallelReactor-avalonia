// C#: tests/ParallelReactor.Tests/GasUptakeTests.cs（时刻由秒换成毫秒；噪声用例的随机数换成 std::mt19937，性质不变）
#include <cmath>
#include <random>

#include "core/gas_uptake.hpp"
#include "testing.hpp"
#include <cstdint>
#include <optional>
#include <string>

using namespace reactor::core;
namespace gu = reactor::core::gas_uptake;

namespace {
constexpr double V = 40;  // 气相体积 mL
int64_t ms(double sec) { return std::llround(sec * 1000); }
double Dn(double p0, double t0, double p1, double t1, double v = V) {
    return gu::gas_mmol(p0, t0, v) - gu::gas_mmol(p1, t1, v);
}
// 开始计量并越过静置期：返回下一个采样时刻（秒）
double Begin(GasUptakeMeter& m, double p, double t, int64_t gen = 0) {
    CHECK(m.start(0, gen));
    for (double s = 0; s <= gu::kSettleSec; s += 1) m.sample(ms(s), true, p, t, V, false, gen);
    return gu::kSettleSec + 1;
}
}  // namespace

TEST(core_gas_uptake, gas_mmol_matches_ideal_gas) {
    CHECK_NEAR(gu::gas_mmol(0, 25, 50), 2.0437, 5e-4);
    CHECK_NEAR(gu::gas_volume_ml(50, 6, 2), 46.0, 1e-12);
    CHECK_NEAR(gu::gas_volume_ml(50, -3, 2), 52.0, 1e-12);
    CHECK_NEAR(gu::resolution_mmol(0.145038, 40), 0.0161, 5e-4);
}

TEST(core_gas_uptake, constant_pressure_no_uptake) {
    GasUptakeMeter m;
    double t = Begin(m, 300, 100);
    for (int i = 0; i < 100; i++) m.sample(ms(t + i), true, 300, 100, V, false, 0);
    CHECK(m.valid());
    CHECK_NEAR(m.mmol(), 0, 1e-9);
}

TEST(core_gas_uptake, pressure_drop_valve_closed_counts_as_uptake) {
    GasUptakeMeter m;
    double t = Begin(m, 300, 100);
    for (int i = 1; i <= 100; i++) m.sample(ms(t + i), true, 300 - 0.1 * i, 100, V, false, 0);
    CHECK_NEAR(m.mmol(), Dn(300, 100, 290, 100), 1e-9);
    CHECK_NEAR(m.mmol(), 0.8889, 5e-4);
}

TEST(core_gas_uptake, refill_is_not_counted_but_consumption_before_and_after_is) {
    GasUptakeMeter m;
    double t = Begin(m, 300, 100);
    for (int i = 1; i <= 10; i++) m.sample(ms(t++), true, 300 - i, 100, V, false, 0);
    double before = m.mmol();
    m.sample(ms(t++), true, 300, 100, V, false, 1);
    CHECK_NEAR(m.mmol(), before, 1e-9);
    CHECK_EQ(m.refills(), 1);
    m.sample(ms(t++), true, 299.5, 100, V, false, 1);
    m.sample(ms(t++), true, 300, 100, V, false, 1);
    CHECK_NEAR(m.mmol(), before, 1e-9);
    m.sample(ms(t++), true, 300, 100, V, false, 1);
    for (int i = 1; i <= 5; i++) m.sample(ms(t++), true, 300 - i, 100, V, false, 1);
    CHECK_NEAR(m.mmol(), Dn(300, 100, 290, 100) + Dn(300, 100, 295, 100), 1e-9);
}

TEST(core_gas_uptake, valve_open_during_sample_is_skipped) {
    GasUptakeMeter m;
    double t = Begin(m, 300, 100);
    m.sample(ms(t++), true, 295, 100, V, false, 0);
    double a = m.mmol();
    m.sample(ms(t++), true, 280, 100, V, true, 0);
    m.sample(ms(t++), true, 260, 100, V, true, 0);
    CHECK_NEAR(m.mmol(), a, 1e-9);
    for (int i = 0; i <= 3; i++) m.sample(ms(t++), true, 260, 100, V, false, 0);
    CHECK_NEAR(m.mmol(), a, 1e-9);
    m.sample(ms(t++), true, 258, 100, V, false, 0);
    CHECK_NEAR(m.mmol(), a + Dn(260, 100, 258, 100), 1e-9);
}

TEST(core_gas_uptake, noise_does_not_accumulate) {
    GasUptakeMeter m;
    double t = Begin(m, 300, 100);
    std::mt19937 rnd(7);
    std::uniform_real_distribution<double> u(0.0, 1.0);
    for (int i = 0; i < 20000; i++) m.sample(ms(t + i), true, 300 + (u(rnd) - 0.5), 100, V, false, 0);
    CHECK(std::fabs(m.mmol()) <= Dn(300.5, 100, 299.5, 100) + 1e-9);
}

TEST(core_gas_uptake, heating_at_constant_gas_amount_is_compensated) {
    GasUptakeMeter m;
    double p_abs0 = 300 + gu::kAtmPsi;
    double t = Begin(m, 300, 25);
    for (int i = 1; i <= 50; i++) {
        double tc = 25 + i;
        double p_abs = p_abs0 * (tc + 273.15) / (25 + 273.15);
        m.sample(ms(t + i), true, p_abs - gu::kAtmPsi, tc, V, false, 0);
    }
    CHECK_NEAR(m.mmol(), 0, 1e-9);
}

TEST(core_gas_uptake, offline_skips_samples_but_keeps_baseline) {
    GasUptakeMeter m;
    double t = Begin(m, 300, 100);
    m.sample(ms(t++), false, NAN, NAN, V, false, 0);
    m.sample(ms(t++), false, 0, 0, V, false, 0);
    m.sample(ms(t++), true, 296, 100, V, false, 0);
    CHECK_NEAR(m.mmol(), Dn(300, 100, 296, 100), 1e-9);
}

TEST(core_gas_uptake, volume_change_rebaselines_without_jump) {
    GasUptakeMeter m;
    double t = Begin(m, 300, 100);
    m.sample(ms(t++), true, 298, 100, V, false, 0);
    double a = m.mmol();
    m.sample(ms(t++), true, 298, 100, V - 5, false, 0);
    CHECK_NEAR(m.mmol(), a, 1e-9);
    m.sample(ms(t++), true, 297, 100, V - 5, false, 0);
    CHECK_NEAR(m.mmol(), a + Dn(298, 100, 297, 100, V - 5), 1e-9);
    m.sample(ms(t++), true, 297, 100, 0.1, false, 0);
    m.sample(ms(t++), true, 290, 100, 0.1, false, 0);
    CHECK_NEAR(m.mmol(), a + Dn(298, 100, 297, 100, V - 5), 1e-9);
}

TEST(core_gas_uptake, rate_is_last_minute_average) {
    GasUptakeMeter m;
    double t = Begin(m, 300, 100);
    CHECK(!m.rate_mmol_per_min().has_value());
    for (int i = 1; i <= 180; i++) m.sample(ms(t + i), true, 300 - 0.05 * i, 100, V, false, 0);
    CHECK(m.rate_mmol_per_min().has_value());
    CHECK_NEAR(m.rate_mmol_per_min().value_or(0), Dn(300, 100, 297, 100), 1e-6);
}

TEST(core_gas_uptake, lifecycle_start_stop_reset) {
    GasUptakeMeter m;
    CHECK(!m.valid());
    CHECK(!m.sample(0, true, 300, 100, V, false, 0));
    double t = Begin(m, 300, 100, 5);
    CHECK(!m.start(ms(t), 5));
    m.sample(ms(t++), true, 295, 100, V, false, 5);
    double a = m.mmol();
    CHECK(a > 0);
    m.stop();
    CHECK(!m.counting());
    CHECK(m.valid());
    m.sample(ms(t++), true, 250, 100, V, false, 5);
    CHECK_NEAR(m.mmol(), a, 1e-9);
    CHECK(!m.rate_mmol_per_min().has_value());
    m.reset();
    CHECK(!m.valid());
    CHECK_EQ(m.mmol(), 0.0);
    CHECK(m.start(ms(t), 5));
    CHECK_EQ(m.mmol(), 0.0);
}

// 选气相体积（MainViewModel.GasVolumeMl / VolumeBasis 的纯逻辑；C# 无对应单测，补充）
TEST(core_gas_uptake, channel_volume_selection_and_basis_text) {
    CHECK_NEAR(gu::channel_gas_volume_ml(10.8, 3, 10, 0), 7.8, 1e-12);
    CHECK_NEAR(gu::channel_gas_volume_ml(10.8, -1, 10, 0), 10.8, 1e-12);
    CHECK_NEAR(gu::channel_gas_volume_ml(std::nullopt, 3, 10, 0.5), 7.5, 1e-12);
    CHECK_EQ(gu::volume_basis_text(10.8, 3, 10, 0), std::string("有效容积 10.8（已标定）− 溶液 3"));
    CHECK_EQ(gu::volume_basis_text(std::nullopt, 2.55, 10, 0), std::string("釜 10 − 溶液 2.55 + 管路 0（未标定）"));
}

// 冷热双温区（釜内热区按 T釜，釜外死体积按 25 ℃ 环境温度）
TEST(core_gas_uptake, two_zone_separates_hot_vessel_and_cold_dead_volume) {
    auto z_cal = gu::split_channel_gas_volume_ml(12.8, 3.0, 10.0, 0.0);
    CHECK_NEAR(z_cal.total_ml, 9.8, 1e-12);
    CHECK_NEAR(z_cal.hot_ml, 7.0, 1e-12);
    CHECK_NEAR(z_cal.cold_ml, 2.8, 1e-12);

    auto z_uncal = gu::split_channel_gas_volume_ml(std::nullopt, 3.0, 10.0, 1.5);
    CHECK_NEAR(z_uncal.total_ml, 8.5, 1e-12);
    CHECK_NEAR(z_uncal.hot_ml, 7.0, 1e-12);
    CHECK_NEAR(z_uncal.cold_ml, 1.5, 1e-12);

    // 25 ℃ 常温下与单温区完全一致
    CHECK_NEAR(gu::gas_mmol_two_zone(300, 25.0, 7.0, 2.8, 25.0), gu::gas_mmol(300, 25.0, 9.8), 1e-12);

    // 150 ℃ 高温反应下：死体积仍按 25 ℃ 折算，消除高温低估误差
    GasUptakeMeter m;
    CHECK(m.start(0, 0));
    for (double s = 0; s <= gu::kSettleSec; s += 1) m.sample(ms(s), true, 300, 150.0, 9.8, false, 0, 2.8, 25.0);
    m.sample(ms(gu::kSettleSec + 1), true, 290, 150.0, 9.8, false, 0, 2.8, 25.0);
    double expected = gu::gas_mmol_two_zone(300, 150.0, 7.0, 2.8, 25.0) - gu::gas_mmol_two_zone(290, 150.0, 7.0, 2.8, 25.0);
    double single_zone = Dn(300, 150.0, 290, 150.0, 9.8);
    CHECK_NEAR(m.mmol(), expected, 1e-9);
    CHECK(m.mmol() > single_zone * 1.10);  // 高温下找回约 12% 被低估的常温死体积摩尔数
}

// 稳压补气盲区速率外推补偿：补气脉冲+静置期间按补气前速率补偿消耗；泄压/未升压时不乱补
TEST(core_gas_uptake, refill_blind_window_extrapolates_pre_refill_rate_when_enabled) {
    GasUptakeMeter m;
    double t = Begin(m, 300, 100);  // t = 2 s 建立基准，下一拍 t = 3 s
    // 1..12 s 匀速每秒降 1 psi（跨度 12 s >= 10 s，建立稳定正速率）
    for (int i = 1; i <= 12; ++i) m.sample(ms(t++), true, 300 - i, 100, V, false, 0, 0.0, 25.0, true);
    // 此时最后有效点 t = 14 s，p = 288 psi；接下来 15 s 开阀补气，16 s 关阀，17 s 静置中，18 s 静置满 2 s 读到 300 psi（盲区共 4 s）
    m.sample(ms(t++), true, 295, 100, V, true, 1, 0.0, 25.0, true);   // t = 15
    m.sample(ms(t++), true, 300, 100, V, false, 1, 0.0, 25.0, true);  // t = 16（closed_since = 16）
    m.sample(ms(t++), true, 300, 100, V, false, 1, 0.0, 25.0, true);  // t = 17（静置 1 s）
    m.sample(ms(t++), true, 300, 100, V, false, 1, 0.0, 25.0, true);  // t = 18（静置满 2 s，补偿 4 s 的匀速消耗）
    // 之后再降 5 s（1 psi/s）：累计值应等于连续 12 + 4 + 5 = 21 s 的总消耗
    for (int i = 1; i <= 5; ++i) m.sample(ms(t++), true, 300 - i, 100, V, false, 1, 0.0, 25.0, true);
    CHECK_NEAR(m.mmol(), Dn(300, 100, 279, 100), 1e-9);

    // 若开阀期间压力反而降低（例如排空泄压到 250 psi），即使开启补偿也不应外推虚增吸气量
    double before_vent = m.mmol();
    m.sample(ms(t++), true, 250, 100, V, true, 2, 0.0, 25.0, true);
    for (int i = 0; i <= 2; ++i) m.sample(ms(t++), true, 250, 100, V, false, 2, 0.0, 25.0, true);
    CHECK_NEAR(m.mmol(), before_vent, 1e-9);
}
