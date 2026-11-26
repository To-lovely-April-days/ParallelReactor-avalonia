// C#: tests/ParallelReactor.Tests/OverTempGuardTests.cs
#include <cmath>

#include "core/over_temp_guard.hpp"
#include "testing.hpp"

using namespace reactor::core;
using A = OverTempAction;

namespace {
constexpr double Limit = 250;
constexpr int64_t T0 = 1'000'000'000;  // 任意单调起点
int64_t At(double sec) { return T0 + std::llround(sec * 1000); }
}  // namespace

#define CHECK_ACT(expr, expected) CHECK((expr) == (expected))

TEST(core_overtemp, below_limit_no_action) {
    OverTempGuard g;
    CHECK_ACT(g.evaluate(0, 249.9, true, Limit, T0), A::None);
    CHECK(!g.is_tripped(0));
}

TEST(core_overtemp, rising_edge_trips_once_then_no_spam_while_in_flight) {
    OverTempGuard g;
    CHECK_ACT(g.evaluate(0, 250, true, Limit, At(0)), A::TripSend);
    CHECK(g.is_tripped(0));
    CHECK(g.in_flight(0));
    CHECK_ACT(g.evaluate(0, 260, true, Limit, At(1.2)), A::None);
    CHECK_ACT(g.evaluate(0, 260, true, Limit, At(10)), A::None);
}

TEST(core_overtemp, unconfirmed_retries_every_5s) {
    OverTempGuard g;
    g.evaluate(0, 255, true, Limit, At(0));
    g.on_stop_result(0, false, At(0.3));
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(1.2)), A::None);
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(4.8)), A::None);
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(5.0)), A::RetrySend);
    g.on_stop_result(0, false, At(5.3));
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(8)), A::None);
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(10.1)), A::RetrySend);
}

TEST(core_overtemp, write_ack_is_not_confirmation_readback_at4_is) {
    OverTempGuard g;
    g.evaluate(0, 255, true, Limit, At(0), false);
    g.on_stop_result(0, true, At(0.2));
    CHECK(!g.is_confirmed(0));
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(1.2), false), A::None);
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(2.4), true), A::Confirmed);
    CHECK(g.is_confirmed(0));
    for (int i = 3; i < 30; i++) CHECK_ACT(g.evaluate(0, 255, true, Limit, At(i * 1.2), true), A::None);
}

TEST(core_overtemp, acked_but_readback_never_at4_keeps_retrying_and_counts_for_escalation) {
    OverTempGuard g;
    g.evaluate(0, 255, true, Limit, At(0), false);
    for (int k = 1; k < over_temp::kEscalateAfterSends; k++) {
        g.on_stop_result(0, true, At(5 * (k - 1) + 0.2));
        CHECK_ACT(g.evaluate(0, 255, true, Limit, At(5 * k), false), A::RetrySend);
    }
    CHECK_EQ(g.unconfirmed_sends(0), over_temp::kEscalateAfterSends);
    CHECK(!g.is_confirmed(0));
}

TEST(core_overtemp, unknown_mode_falls_back_to_write_ack) {
    OverTempGuard g;
    g.evaluate(0, 255, true, Limit, At(0));
    g.on_stop_result(0, true, At(0.2));
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(1.2)), A::Confirmed);
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(30)), A::None);
}

TEST(core_overtemp, confirmed_then_readback_shows_output_running_reopens) {
    OverTempGuard g;
    g.evaluate(0, 255, true, Limit, At(0));
    g.on_stop_result(0, true, At(0.2));
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(1.2), true), A::Confirmed);
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(20), false), A::Reopened);
    CHECK(!g.is_confirmed(0));
    CHECK(g.in_flight(0));
    g.on_stop_result(0, true, At(20.2));
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(21.4), true), A::Confirmed);
}

TEST(core_overtemp, reopened_within_retry_interval_waits_then_reopens) {
    OverTempGuard g;
    g.evaluate(0, 255, true, Limit, At(0));
    g.on_stop_result(0, true, At(0.2));
    g.evaluate(0, 255, true, Limit, At(1.2), true);
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(2.4), false), A::None);
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(5.0), false), A::Reopened);
}

TEST(core_overtemp, failed_cut_keeps_retrying_in_hysteresis_band) {
    OverTempGuard g;
    g.evaluate(0, 251, true, Limit, At(0));
    g.on_stop_result(0, false, At(0.3));
    CHECK_ACT(g.evaluate(0, Limit - 1, true, Limit, At(5), false), A::RetrySend);
}

TEST(core_overtemp, offline_never_trips_and_does_not_retry_or_clear) {
    OverTempGuard g;
    CHECK_ACT(g.evaluate(0, 999, false, Limit, At(0)), A::None);
    CHECK(!g.is_tripped(0));
    CHECK_ACT(g.evaluate(0, NAN, true, Limit, At(1)), A::None);

    g.evaluate(0, 255, true, Limit, At(2));
    g.on_stop_result(0, false, At(2.2));
    CHECK_ACT(g.evaluate(0, 255, false, Limit, At(20)), A::None);
    CHECK_ACT(g.evaluate(0, 20, false, Limit, At(21)), A::None);
    CHECK(g.is_tripped(0));
    CHECK_ACT(g.evaluate(0, 255, true, Limit, At(22)), A::RetrySend);
}

TEST(core_overtemp, clears_only_below_hysteresis_then_rearms) {
    OverTempGuard g;
    g.evaluate(0, 251, true, Limit, At(0));
    g.on_stop_result(0, true, At(0.1));
    CHECK_ACT(g.evaluate(0, Limit - over_temp::kHysteresis + 0.1, true, Limit, At(1), true), A::Confirmed);
    CHECK_ACT(g.evaluate(0, 249.9, true, Limit, At(30), true), A::None);
    CHECK_ACT(g.evaluate(0, Limit - over_temp::kHysteresis - 0.1, true, Limit, At(40)), A::Cleared);
    CHECK(!g.is_tripped(0));
    CHECK_ACT(g.evaluate(0, 250.5, true, Limit, At(50)), A::TripSend);
}

TEST(core_overtemp, late_result_after_clear_is_ignored) {
    OverTempGuard g;
    g.evaluate(0, 251, true, Limit, At(0));
    g.evaluate(0, 200, true, Limit, At(1));
    g.on_stop_result(0, true, At(2));
    CHECK(!g.is_tripped(0));
    CHECK(!g.is_confirmed(0));
}

TEST(core_overtemp, channels_are_independent) {
    OverTempGuard g(8);
    CHECK_ACT(g.evaluate(6, 300, true, Limit, At(0)), A::TripSend);
    CHECK_ACT(g.evaluate(5, 100, true, Limit, At(0)), A::None);
    CHECK_ACT(g.evaluate(5, 300, true, Limit, At(1)), A::TripSend);
    CHECK(g.is_tripped(5) && g.is_tripped(6) && !g.is_tripped(0));
}

TEST(core_overtemp, limit_lowered_while_hot_trips_immediately) {
    OverTempGuard g;
    CHECK_ACT(g.evaluate(0, 80, true, 250, At(0)), A::None);
    CHECK_ACT(g.evaluate(0, 80, true, 60, At(1.2)), A::TripSend);
}

TEST(core_overtemp, first_evaluate_at_time_zero_still_sends) {
    // C# 的 DateTime 默认值很早；C++ 用哨兵，单调时钟从 0 开始也能立即下发 / 5 s 后重发
    OverTempGuard g;
    CHECK_ACT(g.evaluate(0, 300, true, Limit, 0), A::TripSend);
    g.on_stop_result(0, false);
    CHECK_ACT(g.evaluate(0, 300, true, Limit, 4999), A::None);
    CHECK_ACT(g.evaluate(0, 300, true, Limit, 5000), A::RetrySend);
}
