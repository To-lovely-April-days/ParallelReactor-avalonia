// C#: tests/ParallelReactor.Tests/StirGearTests.cs、ProgramConcurrencyTests.cs（StirShareTests）
#include <vector>

#include "core/stir.hpp"
#include "testing.hpp"

using namespace reactor::core;

TEST(core_stir_gear, default_ratio_is_38_over_85) {
    CHECK_NEAR(stir_gear::kDefaultRatio, 38.0 / 85.0, 5e-7);
    // C# 另查了 AppSettings.StirGearRatio 的默认值：设置归设置模块，这里不重复
}

TEST(core_stir_gear, to_motor_multiplies_by_ratio_and_clamps) {
    CHECK_EQ(stir_gear::to_motor(1000, stir_gear::kDefaultRatio, 1000), 447);  // 釜 1000 → 电机约 447
    CHECK_EQ(stir_gear::to_motor(400, stir_gear::kDefaultRatio, 1000), 179);
    CHECK_EQ(stir_gear::to_motor(0, stir_gear::kDefaultRatio, 1000), 0);
    CHECK_EQ(stir_gear::to_motor(2500, stir_gear::kDefaultRatio, 1000), 1000);  // 超过电机上限 1000：限幅
}

TEST(core_stir_gear, to_vessel_divides_by_ratio) {
    CHECK_NEAR(stir_gear::to_vessel(447, stir_gear::kDefaultRatio), 999.87, 0.01);
    CHECK_NEAR(stir_gear::to_vessel(300, 0), 300, 1e-6);  // 传动比无效按 1
}

TEST(core_stir_share, request_returns_overridden_holder_only_when_rpm_differs) {
    StirShare s;
    CHECK(!s.request(1, 1000).has_value());              // 第一个
    CHECK_EQ(s.request(2, 600).value(), (StirHolder{1, 1000}));  // 改写 RV1 的 1000
    CHECK(!s.request(3, 600).has_value());               // 同转速：不算改写
    CHECK_EQ(s.count(), 3);
    CHECK_EQ(s.last_rpm().value(), 600);
    CHECK_EQ(s.request(2, 800).value(), (StirHolder{3, 600}));   // 最近由 RV3 设定
    CHECK(!s.request(2, 900).has_value());               // 自己改自己刚设的
}

TEST(core_stir_share, release_last_one_stops) {
    StirShare s;
    s.request(1, 1000);
    s.request(2, 600);
    CHECK(s.release(1));  // RV2 仍持有
    CHECK(s.holds(2));
    CHECK(!s.holds(1));
    CHECK(!s.release(2));  // 最后一个
    CHECK_EQ(s.count(), 0);
    CHECK(!s.release(7));  // 未登记
}

TEST(core_stir_share, clear_resets_everything) {
    StirShare s;
    s.request(1, 500);
    s.clear();
    CHECK_EQ(s.count(), 0);
    CHECK(!s.last_rpm().has_value());
    CHECK(!s.request(2, 600).has_value());
}

TEST(core_stir_share, single_program_behaves_like_today) {
    StirShare s;
    CHECK(!s.request(1, 1000).has_value());
    CHECK(!s.release(1));
}

TEST(core_stir_share, release_last_setter_restores_previous_holder_rpm) {
    StirShare s;
    s.request(1, 600);
    CHECK_EQ(s.request(2, 900).value(), (StirHolder{1, 600}));
    CHECK(s.release(2));
    CHECK_EQ(s.current().value(), (StirHolder{1, 600}));
    CHECK_EQ(s.last_rpm().value(), 600);
    CHECK(!s.request(3, 600).has_value());
    CHECK_EQ(s.request(4, 800).value(), (StirHolder{3, 600}));
}

TEST(core_stir_share, release_non_last_setter_keeps_last_rpm) {
    StirShare s;
    s.request(1, 600);
    s.request(2, 900);
    CHECK(s.release(1));
    CHECK_EQ(s.last_rpm().value(), 900);
    CHECK_EQ(s.current().value(), (StirHolder{2, 900}));
}

TEST(core_stir_share, request_moves_to_end_so_current_is_most_recent) {
    StirShare s;
    s.request(1, 600);
    s.request(2, 900);
    s.request(1, 700);
    CHECK_EQ(s.current().value(), (StirHolder{1, 700}));
    CHECK(s.holders() == (std::vector<int>{2, 1}));
}
