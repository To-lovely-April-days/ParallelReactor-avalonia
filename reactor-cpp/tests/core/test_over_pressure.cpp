// C#: tests/ParallelReactor.Tests/OverPressureGuardTests.cs
#include <cmath>

#include "core/over_pressure_guard.hpp"
#include "testing.hpp"
#include <cstdint>
#include <optional>

using namespace reactor::core;

namespace {
constexpr double Limit = 510;
constexpr int64_t T0 = 1'000'000'000;
int64_t At(double sec) { return T0 + std::llround(sec * 1000); }

struct Opt {
    bool closed = false;
    std::optional<bool> heat_stopped;
    bool online = true, valves = true, heat = true;
    int ch = 0;
};
OverPressureActions Ev(OverPressureGuard& g, double p, double sec, Opt o = {}) {
    return g.evaluate(o.ch, p, o.online, Limit, At(sec), o.closed, o.valves, o.heat_stopped, o.heat);
}
Opt closed() { Opt o; o.closed = true; return o; }
Opt closed_hs(std::optional<bool> hs) { Opt o; o.closed = true; o.heat_stopped = hs; return o; }
Opt hs(std::optional<bool> h) { Opt o; o.heat_stopped = h; return o; }
Opt no_heat() { Opt o; o.heat = false; return o; }
Opt no_valves() { Opt o; o.valves = false; return o; }
}  // namespace

TEST(core_overpressure, below_limit_no_action) {
    OverPressureGuard g;
    CHECK(!Ev(g, 509.9, 0).any());
    CHECK(!g.is_tripped(0));
}

TEST(core_overpressure, offline_no_trip_and_nan_ignored) {
    OverPressureGuard g;
    Opt off; off.online = false;
    CHECK(!Ev(g, 600, 0, off).any());
    CHECK(!Ev(g, NAN, 0).any());
    CHECK(!g.is_tripped(0));
}

TEST(core_overpressure, rising_edge_trips_and_sends_both_once) {
    OverPressureGuard g;
    auto a = Ev(g, 510, 0);
    CHECK(a.trip);
    CHECK(a.send_valves);
    CHECK(a.send_heat);
    CHECK(g.is_tripped(0));
    CHECK(g.valves_in_flight(0));
    CHECK(g.heat_in_flight(0));
    auto b = Ev(g, 530, 10);
    CHECK(!b.trip);
    CHECK(!b.send_valves);
    CHECK(!b.send_heat);
}

TEST(core_overpressure, heat_disabled_no_heat_send) {
    OverPressureGuard g;
    auto a = Ev(g, 520, 0, no_heat());
    CHECK(a.trip);
    CHECK(a.send_valves);
    CHECK(!a.send_heat);
    CHECK(!g.heat_in_flight(0));
    CHECK(!Ev(g, 520, 30, no_heat()).send_heat);
}

TEST(core_overpressure, valves_not_configured_no_valve_send) {
    OverPressureGuard g;
    auto a = Ev(g, 520, 0, no_valves());
    CHECK(a.trip);
    CHECK(!a.send_valves);
    CHECK(a.send_heat);
}

TEST(core_overpressure, rearm_only_below_98_percent) {
    OverPressureGuard g;
    Ev(g, 520, 0);
    CHECK(!Ev(g, Limit * 0.99, 1).cleared);
    CHECK(g.is_tripped(0));
    CHECK(!Ev(g, Limit * 0.98, 2).cleared);
    auto c = Ev(g, Limit * 0.979, 3);
    CHECK(c.cleared);
    CHECK(!g.is_tripped(0));
    CHECK(Ev(g, 515, 4).trip);
}

TEST(core_overpressure, valves_and_heat_confirm_independently) {
    OverPressureGuard g;
    Ev(g, 520, 0);
    g.on_valve_result(0, true);
    auto a = Ev(g, 520, 1, closed());
    CHECK(a.valves_confirmed);
    CHECK(!a.heat_confirmed);
    CHECK(g.is_valves_confirmed(0));
    CHECK(!g.is_heat_confirmed(0));
    g.on_heat_result(0, true);
    CHECK(!Ev(g, 520, 2, closed_hs(false)).heat_confirmed);
    CHECK(Ev(g, 520, 3, closed_hs(true)).heat_confirmed);
    CHECK(g.is_heat_confirmed(0));
    auto z = Ev(g, 520, 4, closed_hs(true));
    CHECK(!(z.valves_confirmed || z.heat_confirmed || z.send_valves || z.send_heat));
}

TEST(core_overpressure, valves_not_confirmed_without_echo_even_if_shown_closed) {
    OverPressureGuard g;
    Ev(g, 520, 0);
    g.on_valve_result(0, false);
    CHECK(!Ev(g, 520, 1, closed()).valves_confirmed);
    CHECK(!g.is_valves_confirmed(0));
}

TEST(core_overpressure, heat_unknown_readback_confirms_on_echo) {
    OverPressureGuard g;
    Ev(g, 520, 0, hs(std::nullopt));
    g.on_heat_result(0, true);
    CHECK(Ev(g, 520, 1, hs(std::nullopt)).heat_confirmed);
}

TEST(core_overpressure, retry_at_most_every_5s_not_while_in_flight) {
    OverPressureGuard g;
    Ev(g, 520, 0);
    g.on_valve_result(0, false);
    g.on_heat_result(0, false);
    CHECK(!Ev(g, 520, 4.9).send_valves);
    auto a = Ev(g, 520, 5.0);
    CHECK(a.send_valves && a.valves_retry);
    CHECK(a.send_heat && a.heat_retry);
    CHECK_EQ(g.valve_sends(0), 2);
    CHECK(!Ev(g, 520, 20).send_valves);
    g.on_valve_result(0, false);
    CHECK(!Ev(g, 520, 9.9).send_valves);
    CHECK(Ev(g, 520, 10.1).send_valves);
}

TEST(core_overpressure, escalation_counter_reaches_three) {
    OverPressureGuard g;
    Ev(g, 520, 0, no_heat());
    CHECK_EQ(g.valve_sends(0), 1);
    g.on_valve_result(0, false);
    Ev(g, 520, 5, no_heat());
    g.on_valve_result(0, false);
    Ev(g, 520, 10, no_heat());
    CHECK_EQ(g.valve_sends(0), over_temp::kEscalateAfterSends);
    g.on_valve_result(0, true);
    Opt o = closed(); o.heat = false;
    CHECK(Ev(g, 520, 11, o).valves_confirmed);
    CHECK_EQ(g.valve_sends(0), 0);
}

TEST(core_overpressure, reopen_valves_shown_open_after_confirm) {
    OverPressureGuard g;
    Ev(g, 520, 0, no_heat());
    g.on_valve_result(0, true);
    Opt c = closed(); c.heat = false;
    CHECK(Ev(g, 520, 1, c).valves_confirmed);
    auto r = Ev(g, 520, 6, no_heat());
    CHECK(r.valves_reopened);
    CHECK(r.send_valves);
    CHECK(!g.is_valves_confirmed(0));
    CHECK_EQ(g.valve_sends(0), 1);
}

TEST(core_overpressure, reopen_heat_readback_not4_after_confirm) {
    OverPressureGuard g;
    Ev(g, 520, 0, no_valves());
    g.on_heat_result(0, true);
    Opt t = hs(true); t.valves = false;
    CHECK(Ev(g, 520, 1, t).heat_confirmed);
    Opt f = hs(false); f.valves = false;
    auto r = Ev(g, 520, 2, f);
    CHECK(!r.send_heat);
    auto r2 = Ev(g, 520, 5.5, f);
    CHECK(r2.heat_reopened && r2.send_heat);
}

TEST(core_overpressure, late_result_after_clear_ignored) {
    OverPressureGuard g;
    Ev(g, 520, 0);
    CHECK(Ev(g, 100, 1).cleared);
    g.on_valve_result(0, true);
    g.on_heat_result(0, true);
    CHECK(!g.is_tripped(0));
    CHECK(Ev(g, 520, 2).trip);
    CHECK(!Ev(g, 520, 3, closed_hs(std::nullopt)).valves_confirmed);
}

TEST(core_overpressure, stale_result_after_retrip_ignored_by_seq) {
    OverPressureGuard g;
    Ev(g, 520, 0);
    int old_v = g.valve_seq(0), old_h = g.heat_seq(0);
    CHECK(Ev(g, 100, 1).cleared);
    CHECK(Ev(g, 520, 2).trip);
    int new_v = g.valve_seq(0);
    CHECK(old_v != new_v);
    g.on_valve_result(0, true, old_v);
    g.on_heat_result(0, true, old_h);
    CHECK(g.valves_in_flight(0));
    CHECK(g.heat_in_flight(0));
    g.on_valve_result(0, false, new_v);
    CHECK(!Ev(g, 520, 3, closed()).valves_confirmed);
    CHECK(Ev(g, 520, 7.5, closed()).send_valves);
}

TEST(core_overpressure, offline_mid_trip_keeps_state) {
    OverPressureGuard g;
    Ev(g, 520, 0);
    g.on_valve_result(0, false);
    Opt off; off.online = false;
    CHECK(!Ev(g, 0, 10, off).any());
    CHECK(g.is_tripped(0));
    CHECK(Ev(g, 520, 11).send_valves);
}

TEST(core_overpressure, channels_are_independent) {
    OverPressureGuard g;
    Opt o; o.ch = 2;
    CHECK(Ev(g, 520, 0, o).trip);
    CHECK(!g.is_tripped(0));
    CHECK(g.is_tripped(2));
}

TEST(core_overpressure, heat_enabled_mid_trip_starts_sending) {
    OverPressureGuard g;
    Ev(g, 520, 0, no_heat());
    auto a = Ev(g, 520, 1);
    CHECK(a.send_heat && a.heat_retry);
}
