#include "common/clock.hpp"
#include "testing.hpp"
#include <cstdint>
#include <string>

using namespace reactor;

TEST(clock, iso_roundtrip) {
    int64_t ms = 0;
    CHECK(parse_iso("2026-11-26T08:02:15.123Z", ms));
    CHECK_EQ(iso_utc(ms), std::string("2026-11-26T08:02:15.123Z"));
    int64_t ms2 = 0;
    CHECK(parse_iso("2026-11-26T16:02:15.123+08:00", ms2));
    CHECK_EQ(ms, ms2);
    CHECK(!parse_iso("2026-11-26T08:02:15", ms));  // 没有时区
}
