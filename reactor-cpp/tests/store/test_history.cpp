// C#: tests/ParallelReactor.Tests/SafePathTests.cs、HistoryFilesTests.cs
#include <cstdint>

#include "store/csv.hpp"
#include "store/history_files.hpp"
#include "store/store_util.hpp"
#include "store_test_util.hpp"
#include "testing.hpp"

using namespace reactor::store;
using store_test::at8;
using store_test::local_ms;
using store_test::spit;
using store_test::TempDir;

// ---------------- SafePath ----------------

TEST(store_history, valid_names_resolve_inside_root) {
    const char* ok[][2] = {
        {"telemetry", "telemetry-20261106.csv"},   {"telemetry", "telemetry-20261106-1.csv"},
        {"events", "events-20261106.csv"},         {"events", "events-20261106-2.csv"},
        {"events", "events-20261106.jsonl"},       {"sessions", "TP-017-20261106-070000.csv"},
        {"sessions", "催化剂A_批次12-20261106-070000.csv"}, {"leak", "leak-20261106-060000.csv"},
        {"leak", "leak-20261106-060000-summary.csv"},      {"leak", "leak-20261106-060000-summary.json"},
    };
    for (auto& c : ok) {
        auto p = safe_path::try_resolve("/tmp/pr-safepath-root", c[0], c[1]);
        CHECK(p.has_value());
        if (!p) { std::printf("    应接受：%s %s\n", c[0], c[1]); continue; }
        CHECK_EQ(p->rfind("/tmp/pr-safepath-root/", 0), size_t(0));
        CHECK(p->size() >= std::string(c[1]).size() && p->compare(p->size() - std::string(c[1]).size(), std::string::npos, c[1]) == 0);
    }
    CHECK_EQ(*safe_path::try_resolve("/tmp/r/", "events", "events-20261106.jsonl"), std::string("/tmp/r/events/events-20261106.jsonl"));
    CHECK_EQ(*safe_path::try_resolve("/tmp/r", "telemetry", "telemetry-20261106.csv"), std::string("/tmp/r/telemetry-20261106.csv"));
}

TEST(store_history, bad_names_are_rejected) {
    const std::pair<const char*, std::string> bad[] = {
        {"telemetry", "../telemetry-20261106.csv"}, {"telemetry", "..%2F..%2Fetc%2Fpasswd"}, {"telemetry", "../../etc/passwd"},
        {"telemetry", "..\\telemetry-20261106.csv"}, {"telemetry", "/etc/passwd"}, {"telemetry", "C:\\windows\\win.ini"},
        {"telemetry", std::string("telemetry-20261106.csv\0.txt", 27)}, {"telemetry", "settings.json"},
        {"telemetry", "telemetry-2026110.csv"}, {"telemetry", "events-20261106.csv"}, {"events", "events-20261106-1.jsonl"},
        {"events", ".seq"}, {"sessions", "..csv"}, {"sessions", ".hidden.csv"}, {"sessions", "a b.csv"}, {"sessions", "sub/x.csv"},
        {"sessions", "a\u3000b.csv"}, {"leak", "leak-20261106-060000.json"}, {"leak", "leak-2026110-060000.csv"},
        {"estop", "estop.latch"}, {"telemetry", ""},
        // 末尾换行（.NET 的 $ 会放过，白名单用 \z）
        {"telemetry", "telemetry-20261106.csv\n"}, {"events", "events-20261106.jsonl\n"},
        {"leak", "leak-20261106-060000-summary.json\n"}, {"sessions", "TP-017-20261106-070000.csv\n"},
    };
    for (auto& c : bad) {
        bool rejected = !safe_path::try_resolve("/tmp/pr-safepath-root", c.first, c.second).has_value();
        CHECK(rejected);
        if (!rejected) std::printf("    应拒绝：%s %s\n", c.first, c.second.c_str());
        CHECK(!safe_path::is_valid_name(c.first, c.second));
    }
}

TEST(store_history, nfd_name_rejected_nfc_accepted) {
    // é 的 NFD 形式（e + U+0301）：不接受
    std::string nfd = "催化剂e\xCC\x81-20261106-070000.csv";
    std::string nfc = "催化剂\xC3\xA9-20261106-070000.csv";
    CHECK(!safe_path::try_resolve("/tmp/r", "sessions", nfd));
    CHECK(safe_path::try_resolve("/tmp/r", "sessions", nfc).has_value());
}

TEST(store_history, session_name_length_limit_counts_utf16_units) {
    std::string ok(200, 'a');
    CHECK(safe_path::is_valid_name("sessions", ok + ".csv"));
    CHECK(!safe_path::is_valid_name("sessions", ok + "a.csv"));
    std::string zh;
    for (int i = 0; i < 200; ++i) zh += "中";   // 200 个 UTF-16 码元（600 字节）
    CHECK(safe_path::is_valid_name("sessions", zh + ".json"));
    CHECK(!safe_path::is_valid_name("sessions", zh + "中.json"));
}

TEST(store_history, sub_dir_and_leak_run_id) {
    CHECK_EQ(safe_path::sub_dir("telemetry", "telemetry-20261106.csv"), std::string(""));
    CHECK_EQ(safe_path::sub_dir("events", "events-20261106.csv"), std::string(""));
    CHECK_EQ(safe_path::sub_dir("events", "events-20261106.jsonl"), std::string("events"));
    CHECK_EQ(safe_path::sub_dir("sessions", "x.csv"), std::string("sessions"));
    CHECK_EQ(safe_path::sub_dir("leak", "leak-20261106-060000.csv"), std::string("leak"));
    CHECK(safe_path::is_leak_run_id("leak-20261106-060000"));
    CHECK(!safe_path::is_leak_run_id("leak-20261106-060000.csv"));
    CHECK(!safe_path::is_leak_run_id("../leak-20261106-060000"));
    CHECK(!safe_path::is_leak_run_id("leak-20261106-060000\n"));
}

// ---------------- HistoryFiles ----------------

// 写一个 telemetry 日文件：表头 + 每个时刻一行；half 不以换行结束时模拟半行
static std::string write_day(const std::string& dir, const std::string& name, const std::vector<WallTime>& times,
                             const std::string& half = "") {
    std::string s = std::string(kUtf8Bom) + "时间,标记,会话,RV1_PV(℃)\n";
    for (auto& t : times) s += csv::time(t) + ",,,25.0\n";
    s += half;
    std::string p = dir + "/" + name;
    spit(p, s);
    return p;
}

static WallTime LL(int y, int mo, int d, int h, int mi, int s) { return WallTime::local(local_ms(y, mo, d, h, mi, s)); }

TEST(store_history, day_of_parses_name_and_suffix) {
    CHECK(history_files::day_of("telemetry-20261106.csv") == (std::optional<std::pair<std::string, int>>{{"20261106", 0}}));
    CHECK(history_files::day_of("telemetry-20261106-2.csv") == (std::optional<std::pair<std::string, int>>{{"20261106", 2}}));
    CHECK(history_files::day_of("events-20261107.jsonl") == (std::optional<std::pair<std::string, int>>{{"20261107", 0}}));
    CHECK(!history_files::day_of("settings.json"));
}

TEST(store_history, select_daily_files_local_date_window) {
    TempDir d;
    write_day(d.path, "telemetry-20261105.csv", {at8(2026, 11, 5, 23, 59, 0)});
    write_day(d.path, "telemetry-20261106.csv", {at8(2026, 11, 6, 0, 0, 30)});
    write_day(d.path, "telemetry-20261106-1.csv", {at8(2026, 11, 6, 12, 0, 0)});
    write_day(d.path, "telemetry-20261107.csv", {at8(2026, 11, 7, 0, 0, 30)});
    write_day(d.path, "telemetry-20261108.csv", {at8(2026, 11, 8, 0, 0, 30)});
    auto names = [&](const std::vector<std::string>& v) {
        std::vector<std::string> n;
        for (auto& p : v) n.push_back(base_name(p));
        return n;
    };
    auto files = names(history_files::select_daily_files(d.path, local_ms(2026, 11, 6, 13, 0, 0), local_ms(2026, 11, 7, 11, 0, 0)));
    CHECK(files == (std::vector<std::string>{"telemetry-20261106.csv", "telemetry-20261106-1.csv", "telemetry-20261107.csv"}));
    CHECK(history_files::select_daily_files(d.path, local_ms(2026, 11, 7, 0, 0, 0), local_ms(2026, 11, 7, 0, 0, 0)).empty());
    // to 恰为 07 日 00:00（半开区间）：不取 07 日文件
    CHECK(names(history_files::select_daily_files(d.path, local_ms(2026, 11, 6, 1, 0, 0), local_ms(2026, 11, 7, 0, 0, 0))) ==
          (std::vector<std::string>{"telemetry-20261106.csv", "telemetry-20261106-1.csv"}));
}

TEST(store_history, write_range_half_open_header_once_skips_half_line) {
    TempDir d;
    auto f1 = write_day(d.path, "telemetry-20261106.csv", {at8(2026, 11, 6, 23, 59, 58, 800), at8(2026, 11, 6, 23, 59, 59, 999)});
    auto f2 = write_day(d.path, "telemetry-20261107.csv",
                        {at8(2026, 11, 7, 0, 0, 1, 200), at8(2026, 11, 7, 0, 0, 2, 400), at8(2026, 11, 7, 0, 0, 3, 600)},
                        "2026-11-07T00:00:04.800+08:00,,,2");   // 断电留下的半行
    std::string out;
    int64_t rows = history_files::write_range({f1, f2}, at8(2026, 11, 6, 23, 59, 59, 999).ms, at8(2026, 11, 7, 0, 0, 3, 600).ms,
                                              [&](const std::string& s) { out += s; return true; });
    CHECK_EQ(rows, int64_t(3));
    CHECK_EQ(out.rfind(std::string(kUtf8Bom) + "时间,", 0), size_t(0));   // BOM + 表头一次
    std::vector<std::string> ls;
    size_t p = 3;
    while (p < out.size()) {
        size_t n = out.find('\n', p);
        ls.push_back(out.substr(p, n - p));
        p = n + 1;
    }
    CHECK_EQ(ls.size(), size_t(4));
    CHECK(store_test::contains(ls[1], "2026-11-06T23:59:59.999+08:00"));   // from 含
    CHECK(store_test::contains(ls[3], "2026-11-07T00:00:02.400+08:00"));
    CHECK(!store_test::contains(out, "00:00:03.600"));   // to 不含
    CHECK(!store_test::contains(out, "00:00:04.800"));   // 半行不输出
    // 取消
    CHECK_EQ(history_files::write_range({f1}, 0, INT64_MAX, [](const std::string&) { return false; }), int64_t(-1));
}

TEST(store_history, write_range_no_files_writes_only_bom) {
    std::string out;
    CHECK_EQ(history_files::write_range({}, 0, 1, [&](const std::string& s) { out += s; return true; }), int64_t(0));
    CHECK_EQ(out, std::string(kUtf8Bom));
}

TEST(store_history, first_last_timestamp_skips_half_line) {
    TempDir d;
    auto f = write_day(d.path, "telemetry-20261106.csv", {at8(2026, 11, 6, 8, 0, 0), at8(2026, 11, 6, 8, 0, 1, 200)},
                       "2026-11-06T08:00:02.4");
    auto r = history_files::first_last_timestamp(f);
    CHECK(r.first == std::optional<int64_t>(store_test::ms_at(2026, 11, 6, 0, 0, 0)));
    CHECK(r.second == std::optional<int64_t>(store_test::ms_at(2026, 11, 6, 0, 0, 1, 200)));
    auto empty = write_day(d.path, "telemetry-20261107.csv", {});
    auto e = history_files::first_last_timestamp(empty);
    CHECK(!e.first && !e.second);
}

TEST(store_history, list_filters_by_type_and_valid_name) {
    TempDir d;
    write_day(d.path, "telemetry-20261106.csv", {at8(2026, 11, 6, 8, 0, 0)});
    spit(d / "telemetry-bad.csv", "x");
    spit(d / "settings.json", "{}");
    make_dirs(d / "events");
    spit(d / "events/events-20261106.jsonl", "{}\n");
    spit(d / "events-20261106.csv", "时间\n");
    auto tel = history_files::list(d.path, "telemetry");
    CHECK_EQ(tel.size(), size_t(1));
    CHECK_EQ(tel[0].name, std::string("telemetry-20261106.csv"));
    CHECK(tel[0].day == std::optional<std::string>("20261106"));
    CHECK(tel[0].from_ms.has_value());
    CHECK(tel[0].bytes > 0);
    auto ev = history_files::list(d.path, "events");
    CHECK_EQ(ev.size(), size_t(2));
    CHECK_EQ(ev[0].name, std::string("events-20261106.csv"));
    CHECK_EQ(ev[1].name, std::string("events-20261106.jsonl"));
    CHECK(history_files::list(d.path, "leak").empty());
    CHECK(history_files::list(d.path, "bogus").empty());
}

TEST(store_history, try_parse_query_time_requires_zone) {
    int64_t t = 0;
    CHECK(history_files::try_parse_query_time("2026-11-06T08:00:00Z", t));
    CHECK(history_files::try_parse_query_time("2026-11-06T16:00:00+08:00", t));
    CHECK_EQ(t, store_test::ms_at(2026, 11, 6, 8, 0, 0));
    CHECK(history_files::try_parse_query_time("2026-11-06T16:00:00.123-0500", t));
    CHECK(!history_files::try_parse_query_time("2026-11-06T08:00:00", t));
    CHECK(!history_files::try_parse_query_time("2026-11-06", t));
    CHECK(!history_files::try_parse_query_time("", t));
    CHECK(!history_files::try_parse_query_time("yesterday", t));
}

TEST(store_history, parse_row_time_handles_quoted_and_bad) {
    auto want = at8(2026, 11, 6, 8, 0, 0, 120).ms;
    CHECK(history_files::parse_row_time("2026-11-06T08:00:00.120+08:00,a,b") == std::optional<int64_t>(want));
    CHECK(history_files::parse_row_time("\"2026-11-06T08:00:00.120+08:00\",a") == std::optional<int64_t>(want));
    CHECK(!history_files::parse_row_time("时间,标记"));
    CHECK(!history_files::parse_row_time(""));
}

TEST(store_history, parse_session_name) {
    auto p = history_files::parse_session_name("TP-017-20261106-070000.csv");
    CHECK(p.has_value());
    CHECK_EQ(p->first, std::string("TP-017"));
    CHECK_EQ(p->second, std::string("20261106-070000"));
    CHECK(!history_files::parse_session_name("nope.csv"));
}

TEST(store_history, split_column_map_remap) {
    std::vector<std::string> target = {"时间", "标记", "会话", "RV1_PV(℃)"};
    CHECK(!history_files::column_map(target, target));
    auto map = history_files::column_map(target, {"时间", "标记", "RV1_PV(℃)", "会话", "新列"});
    CHECK(map == (std::optional<std::vector<int>>{{0, 1, 3, 2}}));
    CHECK_EQ(history_files::remap("2026-11-06T08:00:00.000+08:00,,25.5,S1,extra", *map), std::string("2026-11-06T08:00:00.000+08:00,,S1,25.5"));
    auto map2 = history_files::column_map(target, {"时间", "RV1_PV(℃)"});
    CHECK(map2 == (std::optional<std::vector<int>>{{0, -1, -1, 1}}));
    CHECK_EQ(history_files::remap("t,\"a,b\"", *map2), std::string("t,,,\"a,b\""));
}

TEST(store_history, write_range_maps_columns_by_name_when_header_changes) {
    TempDir d;
    auto f1 = write_day(d.path, "telemetry-20261106.csv", {at8(2026, 11, 6, 8, 0, 0)});
    std::string f2 = d / "telemetry-20261106-1.csv";
    spit(f2, std::string(kUtf8Bom) + "时间,标记,RV1_PV(℃),会话,新列\n" + csv::time(at8(2026, 11, 6, 8, 0, 1, 200)) + ",,26.5,S1,zzz\n");
    std::string out;
    int64_t rows = history_files::write_range({f1, f2}, at8(2026, 11, 6, 0, 0, 0).ms, at8(2026, 11, 7, 0, 0, 0).ms,
                                              [&](const std::string& s) { out += s; return true; });
    CHECK_EQ(rows, int64_t(2));
    CHECK_EQ(out, std::string(kUtf8Bom) + "时间,标记,会话,RV1_PV(℃)\n" + csv::time(at8(2026, 11, 6, 8, 0, 0)) + ",,,25.0\n" +
                      csv::time(at8(2026, 11, 6, 8, 0, 1, 200)) + ",,S1,26.5\n");
}

TEST(store_history, last_complete_line_tail_read_matches_full_scan) {
    TempDir d;
    auto only_header = write_day(d.path, "telemetry-20261105.csv", {});
    CHECK(history_files::last_complete_line(only_header) == std::optional<std::string>("时间,标记,会话,RV1_PV(℃)"));
    auto fl = history_files::compute_first_last(only_header);
    CHECK(!fl.first && !fl.second);

    std::vector<WallTime> times;
    auto base = at8(2026, 11, 6, 8, 0, 0);
    for (int i = 0; i < 4000; ++i) times.push_back(WallTime::with_offset(base.ms + 1200LL * i, 480));
    auto big = write_day(d.path, "telemetry-20261106.csv", times, "2026-11-06T09:20:00.0");
    CHECK(stat_path(big).size > static_cast<int64_t>(history_files::kTailChunk));
    CHECK(history_files::last_complete_line(big) == std::optional<std::string>(csv::time(times.back()) + ",,,25.0"));
    auto r = history_files::first_last_timestamp(big);
    CHECK(r.first == std::optional<int64_t>(times.front().ms));
    CHECK(r.second == std::optional<int64_t>(times.back().ms));
    // 追加一行后长度变了：缓存失效
    auto extra = at8(2026, 11, 6, 9, 30, 0);
    spit(big, store_test::slurp(big) + "\n" + csv::time(extra) + ",,,25.0\n");
    CHECK(history_files::first_last_timestamp(big).second == std::optional<int64_t>(extra.ms));
}

TEST(store_history, select_uses_local_dates_for_ll_times) {
    // LL 构造只是确认本机时区换算可用（不同时区的机器上都应成立）
    auto t = LL(2026, 11, 6, 13, 0, 0);
    CHECK_EQ(fmt_wall(t, "yyyyMMdd HH"), std::string("20261106 13"));
}
