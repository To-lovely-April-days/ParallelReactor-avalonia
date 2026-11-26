// C#: tests/ParallelReactor.Tests/TelemetryCsvTests.cs（Csv 部分）+ .NET 数值格式对照
#include <cmath>
#include <limits>

#include "store/csv.hpp"
#include "store_test_util.hpp"
#include "testing.hpp"
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

using namespace reactor::store;
using store_test::at8;

TEST(store_csv, escape_quotes_only_when_needed) {
    CHECK_EQ(csv::escape(""), std::string(""));
    CHECK_EQ(csv::escape("abc"), std::string("abc"));
    CHECK_EQ(csv::escape("中文标记"), std::string("中文标记"));
    CHECK_EQ(csv::escape("a,b"), std::string("\"a,b\""));
    CHECK_EQ(csv::escape("say \"hi\""), std::string("\"say \"\"hi\"\"\""));
    CHECK_EQ(csv::escape("line1\nline2"), std::string("\"line1\nline2\""));
    CHECK_EQ(csv::escape("cr\rhere"), std::string("\"cr\rhere\""));
    CHECK_EQ(csv::escape("=1+1"), std::string("'=1+1"));   // 防 Excel 公式注入
    CHECK_EQ(csv::escape("-0.02 psi"), std::string("'-0.02 psi"));
    CHECK_EQ(csv::escape("@x"), std::string("'@x"));
    CHECK_EQ(csv::escape("+a,b"), std::string("\"'+a,b\""));
    CHECK_EQ(csv::escape("-1.5"), std::string("-1.5"));   // 纯数字不动
    CHECK_EQ(csv::escape("+3e2"), std::string("+3e2"));
    CHECK_EQ(csv::escape("=HYPERLINK(\"http://x\")"), std::string("\"'=HYPERLINK(\"\"http://x\"\")\""));
    CHECK_EQ(csv::escape("-Infinity"), std::string("-Infinity"));   // .NET double.TryParse 认得
    CHECK_EQ(csv::escape("-"), std::string("'-"));
}

TEST(store_csv, num_invariant_and_empty_for_missing) {
    CHECK_EQ(csv::num(1.5), std::string("1.5"));
    CHECK_EQ(csv::num(std::nullopt), std::string(""));
    CHECK_EQ(csv::num(std::numeric_limits<double>::quiet_NaN()), std::string(""));
    CHECK_EQ(csv::num(std::numeric_limits<double>::infinity()), std::string(""));
    CHECK_EQ(csv::integer(std::nullopt), std::string(""));
    CHECK_EQ(csv::integer(-12), std::string("-12"));
    CHECK_EQ(csv::boolean(true), std::string("1"));
    CHECK_EQ(csv::boolean(false), std::string("0"));
    CHECK_EQ(csv::boolean(std::nullopt), std::string(""));
}

TEST(store_csv, net_custom_formats) {
    CHECK_EQ(csv::net_format(25.4, "0.0##"), std::string("25.4"));
    CHECK_EQ(csv::net_format(25, "0.0##"), std::string("25.0"));
    CHECK_EQ(csv::net_format(25.12345, "0.0##"), std::string("25.123"));
    CHECK_EQ(csv::net_format(12.34567, "0.000"), std::string("12.346"));
    CHECK_EQ(csv::net_format(0, "0.000"), std::string("0.000"));
    CHECK_EQ(csv::net_format(145.037738 * (1 / 145.037738), "0.00000"), std::string("1.00000"));
    CHECK_EQ(csv::net_format(2.0005, "0.000"), std::string("2.001"));   // .NET：15 位有效数字后十进制四舍五入
    CHECK_EQ(csv::net_format(0.0005, "0.000"), std::string("0.001"));
    CHECK_EQ(csv::net_format(0.0004, "0.###"), std::string("0"));
    CHECK_EQ(csv::net_format(-0.0004, "0.###"), std::string("0"));     // 舍成 0 不带负号
    CHECK_EQ(csv::net_format(-1.25, "0.#"), std::string("-1.3"));      // 远离零
    CHECK_EQ(csv::net_format(9.9996, "0.###"), std::string("10"));
    CHECK_EQ(csv::net_format(99.95, "0.0#"), std::string("99.95"));
    CHECK_EQ(csv::net_format(123456.789, "0.##"), std::string("123456.79"));
    CHECK_EQ(csv::net_format(1e-7, "0.0####"), std::string("0.0"));
    CHECK_EQ(csv::net_format(5, "0.##"), std::string("5"));
    CHECK_EQ(csv::net_format(0.1 + 0.2, "0.###"), std::string("0.3"));
}

TEST(store_csv, time_is_iso8601_with_offset_and_ms) {
    CHECK_EQ(csv::time(at8(2026, 11, 6, 14, 3, 21, 123)), std::string("2026-11-06T14:03:21.123+08:00"));
    CHECK_EQ(csv::time(std::nullopt), std::string(""));
    CHECK_EQ(csv::time(WallTime::with_offset(store_test::ms_at(2026, 1, 2, 3, 4, 5, 6), 0)), std::string("2026-01-02T03:04:05.006+00:00"));
    CHECK_EQ(csv::time(WallTime::with_offset(store_test::ms_at(2026, 1, 2, 3, 4, 5, 6, -330), -330)),
             std::string("2026-01-02T03:04:05.006-05:30"));
}

TEST(store_csv, line_and_split_roundtrip) {
    std::vector<std::string> f = {"a", "", "x,y", "q\"r", "=bad", "中文"};
    auto l = csv::line(f);
    CHECK_EQ(l, std::string("a,,\"x,y\",\"q\"\"r\",'=bad,中文"));
    auto back = csv::split(l);
    CHECK_EQ(back.size(), size_t(6));
    CHECK_EQ(back[2], std::string("x,y"));
    CHECK_EQ(back[3], std::string("q\"r"));
    CHECK_EQ(back[4], std::string("'=bad"));
    auto s = csv::split("a,,\"x,y\",\"q\"\"r\",");
    CHECK_EQ(s.size(), size_t(5));
    CHECK_EQ(s[4], std::string(""));
}

TEST(store_csv, utc_json_time_trims_trailing_zeros) {
    CHECK_EQ(fmt_utc_json(store_test::ms_at(2026, 11, 18, 2, 0, 0)), std::string("2026-11-18T02:00:00Z"));
    CHECK_EQ(fmt_utc_json(store_test::ms_at(2026, 11, 6, 8, 12, 30, 120)), std::string("2026-11-06T08:12:30.12Z"));
    CHECK_EQ(fmt_utc_json(store_test::ms_at(2026, 11, 6, 8, 12, 30, 5)), std::string("2026-11-06T08:12:30.005Z"));
    int64_t ms = 0;
    bool zone = true;
    CHECK(parse_time_loose("2026-11-01T08:00:00.1234567Z", ms, &zone));
    CHECK(zone);
    CHECK_EQ(ms, store_test::ms_at(2026, 11, 1, 8, 0, 0, 123));
    CHECK(parse_time_loose("2026-11-01T08:00:00", ms, &zone));
    CHECK(!zone);
    CHECK_EQ(ms, store_test::ms_at(2026, 11, 1, 8, 0, 0));
    CHECK(parse_time_loose("2026-11-01T16:00:00+08:00", ms));
    CHECK_EQ(ms, store_test::ms_at(2026, 11, 1, 8, 0, 0));
    CHECK(!parse_time_loose("yesterday", ms));
}
