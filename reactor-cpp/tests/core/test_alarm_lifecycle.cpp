// C#: tests/ParallelReactor.Tests/AlarmLifecycleTests.cs
#include <memory>
#include <string>

#include "core/alarm_lifecycle.hpp"
#include "testing.hpp"

using namespace reactor::core;
namespace al = reactor::core::alarm_lifecycle;

TEST(core_alarm, on_cleared) {
    CHECK_EQ(al::on_cleared("active"), std::string("cleared"));
    CHECK_EQ(al::on_cleared("ack"), std::string("done"));
    CHECK_EQ(al::on_cleared("done"), std::string("done"));
    CHECK_EQ(al::on_cleared("cleared"), std::string("cleared"));
}

TEST(core_alarm, on_ack) {
    CHECK_EQ(al::on_ack("active"), std::string("ack"));
    CHECK_EQ(al::on_ack("cleared"), std::string("done"));
    CHECK_EQ(al::on_ack("done"), std::string("done"));
    CHECK_EQ(al::on_ack("ack"), std::string("ack"));
}

TEST(core_alarm, cleared_then_ack_is_done_ack_then_cleared_is_done) {
    CHECK_EQ(al::on_ack(al::on_cleared("active")), std::string("done"));
    CHECK_EQ(al::on_cleared(al::on_ack("active")), std::string("done"));
}

namespace {
struct Item { std::string stat = "active"; };
}

TEST(core_alarm, index_groups_per_key_take_empties_second_clear_noop) {
    AlarmIndex<std::shared_ptr<Item>> idx;
    auto a = std::make_shared<Item>(), b = std::make_shared<Item>(), c = std::make_shared<Item>();
    idx.raise("ot:1", a);
    idx.raise("ot:1", b);
    idx.raise("op:1", c);
    CHECK_EQ(idx.count("ot:1"), 2);

    auto taken = idx.take_active("ot:1");
    CHECK_EQ(taken.size(), size_t(2));
    CHECK(taken[0] == a && taken[1] == b);
    for (auto& x : taken) x->stat = al::on_cleared(x->stat);
    CHECK_EQ(a->stat, std::string("cleared"));
    CHECK_EQ(c->stat, std::string("active"));  // 其他键不受影响
    CHECK(idx.take_active("ot:1").empty());     // 再次清除是空操作
    CHECK_EQ(idx.count("op:1"), 1);
}

TEST(core_alarm, raise_after_clear_registers_new_active) {
    AlarmIndex<std::shared_ptr<Item>> idx;
    auto old = std::make_shared<Item>();
    idx.raise("comm:0", old);
    idx.take_active("comm:0");
    auto fresh = std::make_shared<Item>();
    idx.raise("comm:0", fresh);
    auto t = idx.take_active("comm:0");
    CHECK_EQ(t.size(), size_t(1));
    CHECK(t[0] == fresh);
}
