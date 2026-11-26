// C#: tests/ParallelReactor.Tests/GasLeaseTests.cs、ProgramConcurrencyTests.cs（GasQueueTests、GasLeasePerChannelTests 的租约部分）
#include <map>
#include <string>
#include <vector>

#include "core/gas_lease.hpp"
#include "testing.hpp"

using namespace reactor::core;
using RS = ReactorState;

namespace {
std::map<int, RS> st(std::initializer_list<std::pair<const int, RS>> xs) { return std::map<int, RS>(xs); }
auto all(RS s) { return [s](int) -> std::optional<RS> { return s; }; }
bool contains(const std::optional<std::string>& s, const std::string& sub) {
    return s.has_value() && s->find(sub) != std::string::npos;
}
const std::map<int, RS> kNone;
}  // namespace

TEST(core_gas_lease, only_owner_can_abort) {
    GasLease l;
    CHECK(!l.can_abort(GasOwner::Leak));
    CHECK(!l.can_abort(GasOwner::None));
    l.begin(GasOwner::Program, "1 号 程序 · 升压", {1}, {1}, st({{1, RS::Pressing}}));
    CHECK(!l.can_abort(GasOwner::Leak));
    CHECK(l.can_abort(GasOwner::Program));
    CHECK(!l.can_abort(GasOwner::None));

    l.begin(GasOwner::Leak, "泄漏测试", {2, 3}, {2, 3}, st({{2, RS::Idle}, {3, RS::Done}}));
    CHECK(!l.can_abort(GasOwner::Program));
    CHECK(l.can_abort(GasOwner::Leak));
}

TEST(core_gas_lease, try_end_wrong_owner_is_noop) {
    GasLease l;
    int64_t g = l.begin(GasOwner::Leak, "泄漏测试", {2}, {2}, st({{2, RS::Idle}}));
    CHECK(!l.try_end(GasOwner::Program));
    CHECK(l.busy());
    CHECK_EQ(l.task(), std::string("泄漏测试"));
    CHECK(l.watch() == std::vector<int>{2});
    CHECK(!l.try_end(GasOwner::Leak, g + 1));
    CHECK(l.try_end(GasOwner::Leak, g));
    CHECK(!l.busy());
    CHECK(l.watch().empty());
    CHECK(!l.try_end(GasOwner::Leak));
}

TEST(core_gas_lease, same_owner_renewal_ends_with_owner) {
    GasLease l;
    int64_t g1 = l.begin(GasOwner::Program, "升压", {1}, {1}, kNone);
    int64_t g2 = l.begin(GasOwner::Program, "稳压", {1}, {1}, kNone);
    CHECK(g1 != g2);
    CHECK(!l.try_end(GasOwner::Program, g1));
    CHECK(l.try_end(GasOwner::Program));
}

TEST(core_gas_lease, includes_and_invalidate) {
    GasLease l;
    CHECK(!l.includes(1));
    l.begin(GasOwner::Leak, "泄漏测试", {1, 2}, {1, 2}, st({{1, RS::Done}, {2, RS::Done}}));
    CHECK(l.includes(2));
    CHECK(!l.includes(5));
    CHECK(!l.fault_reason(all(RS::Done)).has_value());
    l.invalidate(5);
    CHECK(!l.fault_reason(all(RS::Done)).has_value());
    l.invalidate(2);
    CHECK(l.is_invalidated(2));
    CHECK(contains(l.fault_reason(all(RS::Done)), "2 号"));
    l.begin(GasOwner::Leak, "泄漏测试", {2}, {}, st({{2, RS::Done}}));
    CHECK(contains(l.fault_reason(all(RS::Done)), "2 号"));
    CHECK(l.try_end(GasOwner::Leak));
    l.begin(GasOwner::Leak, "泄漏测试", {2}, {}, st({{2, RS::Done}}));
    CHECK(!l.fault_reason(all(RS::Done)).has_value());
}

TEST(core_gas_lease, transition_into_done_or_idle_faults) {
    GasLease l;
    l.begin(GasOwner::Leak, "泄漏测试", {1, 2}, {}, st({{1, RS::Ready}, {2, RS::Idle}}));
    auto f = [](RS a, RS b) { return [a, b](int id) -> std::optional<RS> { return id == 1 ? a : b; }; };
    CHECK(!l.fault_reason(f(RS::Ready, RS::Idle)).has_value());
    CHECK(contains(l.fault_reason(f(RS::Done, RS::Idle)), "1 号 已被停止"));
    CHECK(contains(l.fault_reason(f(RS::Idle, RS::Idle)), "1 号 已被停用"));
    CHECK(contains(l.fault_reason(f(RS::Ready, RS::Done)), "2 号 已被停止"));  // Idle → Done
}

TEST(core_gas_lease, program_state_walk_does_not_fault_alarm_does) {
    GasLease l;
    l.begin(GasOwner::Program, "1 号 程序 · 稳压", {1}, {1}, st({{1, RS::Pressing}}));
    for (RS s : {RS::Pressing, RS::Wait, RS::React, RS::Stir, RS::Heating, RS::Cooling})
        CHECK(!l.fault_reason(all(s)).has_value());
    CHECK(contains(l.fault_reason(all(RS::Alarm)), "报警"));
    CHECK(l.fault_reason(all(RS::Done)).has_value());
    CHECK(!l.fault_reason([](int) -> std::optional<RS> { return std::nullopt; }).has_value());  // 未知通道不判
}

TEST(core_gas_lease, idle_no_fault) {
    GasLease l;
    CHECK(!l.fault_reason(all(RS::Alarm)).has_value());
    l.begin(GasOwner::Program, "吹扫", {}, {}, kNone);
    CHECK(!l.fault_reason(all(RS::Alarm)).has_value());
    l.clear();
    CHECK(!l.busy());
}

TEST(core_gas_lease, renew_same_owner_keeps_invalidation_and_start_state) {
    GasLease l;
    l.begin(GasOwner::Program, "升压", {1}, {1}, st({{1, RS::Pressing}}));
    l.invalidate(1);
    l.begin(GasOwner::Program, "稳压", {1}, {1}, st({{1, RS::Done}}));
    CHECK(l.is_invalidated(1));
    CHECK(l.fault_reason(all(RS::Done)).has_value());

    GasLease m;
    m.begin(GasOwner::Program, "升压", {1}, {1}, st({{1, RS::Pressing}}));
    m.begin(GasOwner::Program, "稳压", {1}, {1}, st({{1, RS::Done}}));
    CHECK(m.fault_reason(all(RS::Done)).has_value());
}

TEST(core_gas_lease, new_owner_clears_invalidation_renew_drops_non_participants) {
    GasLease l;
    l.begin(GasOwner::Leak, "泄漏测试", {1, 2}, {1, 2}, st({{1, RS::Done}, {2, RS::Done}}));
    l.invalidate(1);
    l.invalidate(2);
    l.begin(GasOwner::Leak, "泄漏测试", {2}, {2}, st({{2, RS::Done}}));
    CHECK(!l.is_invalidated(1));
    CHECK(l.is_invalidated(2));
    CHECK(l.try_end(GasOwner::Leak));
    l.begin(GasOwner::Program, "升压", {2}, {2}, st({{2, RS::Pressing}}));
    CHECK(!l.is_invalidated(2));
    CHECK(!l.fault_reason(all(RS::Pressing)).has_value());
}

TEST(core_gas_lease, begin_none_owner_rejected) {
    // C# 抛 ArgumentException；C++ 不改状态、返回 0
    GasLease l;
    CHECK_EQ(l.begin(GasOwner::None, "x", {1}, {}, kNone), int64_t(0));
    CHECK(!l.busy());
}

// ---- GasLeasePerChannelTests ----

TEST(core_gas_lease, owner_rv_recorded_and_cleared_abort_per_channel) {
    GasLease l;
    l.begin(GasOwner::Program, "1 号 程序 · 升压", {1}, {1}, kNone, 1);
    CHECK_EQ(l.owner_rv().value(), 1);
    CHECK(l.can_abort(GasOwner::Program));
    CHECK(l.can_abort(GasOwner::Program, 1));
    CHECK(!l.can_abort(GasOwner::Program, 2));
    CHECK(!l.can_abort(GasOwner::Leak, 1));
    CHECK(l.try_end(GasOwner::Program));
    CHECK(!l.owner_rv().has_value());
    l.begin(GasOwner::Leak, "泄漏测试", {2}, {2}, kNone);
    CHECK(!l.owner_rv().has_value());
}

TEST(core_gas_lease, different_rv_is_new_lease_not_renewal) {
    GasLease l;
    l.begin(GasOwner::Program, "1 号 程序 · 升压", {1}, {1}, kNone, 1);
    l.invalidate(1);
    CHECK(l.is_invalidated(1));
    l.begin(GasOwner::Program, "1 号 程序 · 稳压", {1}, {1}, kNone, 1);
    CHECK(l.is_invalidated(1));
    l.begin(GasOwner::Program, "2 号 程序 · 升压", {1, 2}, {2}, kNone, 2);
    CHECK(!l.is_invalidated(1));
    CHECK_EQ(l.owner_rv().value(), 2);
}

// ---- GasQueueTests（单调时间，毫秒）----

namespace {
constexpr int64_t T0 = 8LL * 3600 * 1000;
constexpr int64_t kSec = 1000, kMin = 60 * 1000;
}  // namespace

TEST(core_gas_queue, fifo_head_advances_on_remove) {
    GasQueue q;
    int64_t a = q.enqueue(1, T0), b = q.enqueue(3, T0 + kSec), c = q.enqueue(2, T0 + 2 * kSec);
    CHECK(q.is_head(a));
    CHECK(!q.is_head(b));
    CHECK(q.waiting() == (std::vector<int>{1, 3, 2}));
    CHECK(q.remove(a));
    CHECK(q.is_head(b));
    CHECK(!q.is_head(c));
    CHECK_EQ(q.count(), 2);
}

TEST(core_gas_queue, remove_middle_keeps_order) {
    GasQueue q;
    int64_t a = q.enqueue(1, T0), b = q.enqueue(2, T0), c = q.enqueue(3, T0);
    CHECK(q.remove(b));
    CHECK(!q.remove(b));
    CHECK(q.is_head(a));
    q.remove(a);
    CHECK(q.is_head(c));
    CHECK(q.waiting() == std::vector<int>{3});
}

TEST(core_gas_queue, duplicate_enqueue_returns_same_ticket_keeps_since) {
    GasQueue q;
    int64_t a = q.enqueue(4, T0);
    int64_t a2 = q.enqueue(4, T0 + 5 * kMin);
    CHECK_EQ(a, a2);
    CHECK_EQ(q.count(), 1);
    CHECK_EQ(q.waited_ms(a, T0 + 5 * kMin), 5 * kMin);
    CHECK(q.contains(4));
}

TEST(core_gas_queue, timeout_boundary_60min) {
    GasQueue q;
    int64_t a = q.enqueue(1, T0);
    int64_t max = 60 * kMin;
    CHECK(!q.timed_out(a, T0 + static_cast<int64_t>(59.99 * kMin), max));
    CHECK(q.timed_out(a, T0 + 60 * kMin, max));
    CHECK(!q.timed_out(a, T0 + 300 * kMin, 0));    // 0 = 不限
    CHECK(!q.timed_out(999, T0 + 300 * kMin, max)); // 不在队中
    CHECK_EQ(q.waited_ms(999, T0), int64_t(0));
}
