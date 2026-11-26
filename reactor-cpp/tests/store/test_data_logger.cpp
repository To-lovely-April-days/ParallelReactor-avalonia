// C#: tests/ParallelReactor.Tests/DataLoggerTests.cs（+ SessionManifestTests.DataLogger_SessionKey_IsFileStem）
#include <atomic>

#include "store/csv.hpp"
#include "store/data_logger.hpp"
#include "store_test_util.hpp"
#include "testing.hpp"

using namespace reactor::store;
using store_test::at8;
using store_test::lines_of;
using store_test::slurp;
using store_test::spit;
using store_test::TempDir;

static const WallTime T0 = at8(2026, 11, 6, 10, 0, 0);
static WallTime plus(const WallTime& t, int64_t ms) { return WallTime::with_offset(t.ms + ms, t.offset_min); }
static TelemetryRow Row(const WallTime& t) {
    TelemetryRow r;
    r.time = t;
    return r;
}
static int count_bom(const std::string& b) {
    int n = 0;
    for (size_t i = 0; i + 2 < b.size(); ++i)
        if (b.compare(i, 3, kUtf8Bom) == 0) ++n;
    return n;
}

TEST(store_logger, telemetry_bom_and_header_once_appends_across_restarts) {
    TempDir d;
    {
        DataLogger log(d.path);
        log.telemetry(Row(T0));
        log.telemetry(Row(plus(T0, 1200)));
    }
    {
        DataLogger log2(d.path);   // 重启后追加到同一个日文件
        log2.telemetry(Row(plus(T0, 2400)));
    }
    std::string bytes = slurp(d / "telemetry-20261106.csv");
    CHECK_EQ(bytes.compare(0, 3, kUtf8Bom), 0);
    CHECK_EQ(count_bom(bytes), 1);   // 追加时不再写 BOM
    auto lines = lines_of(d / "telemetry-20261106.csv");
    CHECK_EQ(lines.size(), size_t(4));
    CHECK_EQ(lines[0], telemetry_csv::header());
    int headers = 0;
    for (auto& l : lines) headers += l == telemetry_csv::header();
    CHECK_EQ(headers, 1);
}

TEST(store_logger, telemetry_rolls_daily_by_row_timestamp) {
    TempDir d;
    auto late = at8(2026, 11, 6, 23, 59, 59);
    {
        DataLogger log(d.path);
        log.telemetry(Row(late));
        log.telemetry(Row(plus(late, 2000)));
    }
    CHECK_EQ(lines_of(d / "telemetry-20261106.csv").size(), size_t(2));
    CHECK_EQ(lines_of(d / "telemetry-20261107.csv").size(), size_t(2));
}

TEST(store_logger, header_mismatch_starts_new_file) {
    TempDir d;
    spit(d / "telemetry-20261106.csv", std::string(kUtf8Bom) + "旧,表头\n1,2\n");
    {
        DataLogger log(d.path);
        log.telemetry(Row(T0));
    }
    CHECK_EQ(lines_of(d / "telemetry-20261106.csv")[1], std::string("1,2"));   // 旧文件不动
    CHECK_EQ(lines_of(d / "telemetry-20261106-1.csv")[0], telemetry_csv::header());
    // 再来一个不同表头的 -1：另起 -2
    spit(d / "telemetry-20261107.csv", "x\n");
    spit(d / "telemetry-20261107-1.csv", "y\n");
    {
        DataLogger log(d.path);
        log.telemetry(Row(at8(2026, 11, 7, 1, 0, 0)));
    }
    CHECK_EQ(lines_of(d / "telemetry-20261107-2.csv").size(), size_t(2));
}

TEST(store_logger, marker_consumed_by_next_row_and_session_writes_second_file) {
    TempDir d;
    std::string session_path;
    {
        DataLogger log(d.path);
        session_path = log.begin_session("T 01/a", T0);
        log.add_marker("开始升温");
        log.telemetry(Row(T0));
        log.telemetry(Row(plus(T0, 1000)));
        log.end_session();
        log.telemetry(Row(plus(T0, 2000)));
    }
    CHECK_EQ(reactor::store::base_name(session_path), std::string("T_01_a-20261106-100000.csv"));
    auto daily = lines_of(d / "telemetry-20261106.csv");
    CHECK_EQ(daily.size(), size_t(4));
    CHECK_EQ(csv::split(daily[1])[1], std::string("开始升温"));
    CHECK_EQ(csv::split(daily[1])[2], std::string("T 01/a"));
    CHECK_EQ(csv::split(daily[2])[1], std::string(""));
    CHECK_EQ(csv::split(daily[3])[2], std::string(""));   // 会话已结束
    auto ses = lines_of(session_path);
    CHECK_EQ(ses.size(), size_t(3));   // 表头 + 2 行
    CHECK_EQ(ses[0], telemetry_csv::header());
}

TEST(store_logger, events_csv_and_write_file) {
    TempDir d;
    DataLogger log(d.path);
    log.event(T0, "info", "系统", "程序启动", "a,b");
    log.write_file("leak/x.csv", "h\n1\n");
    log.sync();
    auto ev = lines_of(d / "events-20261106.csv");
    CHECK_EQ(ev[0], telemetry_csv::event_header());
    CHECK_EQ(ev[1], std::string("2026-11-06T10:00:00.000+08:00,info,系统,程序启动,\"a,b\""));
    CHECK_EQ(slurp(d / "leak/x.csv"), std::string(kUtf8Bom) + "h\n1\n");
}

TEST(store_logger, event_also_goes_to_event_store) {
    TempDir d;
    EventStore events(d.path);
    DataLogger log(d.path, {}, &events);
    auto rec = log.event(T0, "warn", "RV2", "超温 · 切断加热", "x", "local", std::nullopt, std::string("op-1"), "");
    CHECK(rec.has_value());
    CHECK_EQ(rec->seq, int64_t(1));
    CHECK_EQ(rec->kind, std::string("alarm.overTemp"));
    log.stop();
    events.stop();
    CHECK_EQ(lines_of(d / "events/events-20261106.jsonl").size(), size_t(1));
    CHECK_EQ(lines_of(d / "events-20261106.csv").size(), size_t(2));
}

TEST(store_logger, write_failure_does_not_throw_and_reports_once) {
    TempDir d;
    std::string blocker = d / "notadir";
    spit(blocker, "这是一个文件，不是目录");   // 数据目录无法创建
    std::atomic<int> reports{0};
    DataLogger log(blocker, [&](const std::string&) { ++reports; });
    log.telemetry(Row(T0));
    log.event(T0, "info", "x", "y", "z");
    log.telemetry(Row(plus(T0, 1000)));
    log.sync();
    log.stop();
    CHECK_EQ(reports.load(), 1);
    CHECK(log.dropped_lines() >= 2);
}

TEST(store_logger, end_session_with_final_row_puts_end_marker_in_session_file) {
    TempDir d;
    std::string session_path;
    {
        DataLogger log(d.path);
        session_path = log.begin_session("S1", T0);
        log.telemetry(Row(T0));
        log.add_marker("会话结束 S1");   // 日运行记录：下一行
        log.end_session(Row(plus(T0, 500)), "会话结束 S1");
        log.telemetry(Row(plus(T0, 1200)));
    }
    auto ses = lines_of(session_path);
    CHECK_EQ(ses.size(), size_t(3));
    CHECK_EQ(csv::split(ses[2])[1], std::string("会话结束 S1"));
    CHECK_EQ(csv::split(ses[2])[2], std::string("S1"));
    auto daily = lines_of(d / "telemetry-20261106.csv");
    CHECK_EQ(daily.size(), size_t(3));   // 会话最后一行只写会话文件
    CHECK_EQ(csv::split(daily[2])[1], std::string("会话结束 S1"));
    CHECK_EQ(csv::split(daily[2])[2], std::string(""));
}

TEST(store_logger, append_after_crash_left_partial_line_starts_on_new_line) {
    TempDir d;
    spit(d / "telemetry-20261106.csv", std::string(kUtf8Bom) + telemetry_csv::header() + "\n2026-11-06T09:59:59.000+08:00,半行");
    {
        DataLogger log(d.path);
        log.telemetry(Row(T0));
    }
    auto lines = lines_of(d / "telemetry-20261106.csv");
    CHECK_EQ(lines.size(), size_t(3));
    CHECK(lines[1].size() >= 7 && lines[1].compare(lines[1].size() - 7, 7, ",半行") == 0);
    CHECK_EQ(lines[2], telemetry_csv::format(Row(T0)));   // 新行完整、列数正确
}

TEST(store_logger, session_key_is_file_stem) {
    TempDir d;
    DataLogger log(d.path);
    CHECK(!log.session_key());
    auto path = log.begin_session("TP 017", WallTime::with_offset(store_test::ms_at(2026, 11, 7, 8, 0, 0), 0));
    CHECK(log.session_id() == std::optional<std::string>("TP 017"));             // testId 原样（CSV「会话」列）
    CHECK(log.session_key() == std::optional<std::string>("TP_017-20261107-080000"));   // 文件主名：空白 / 非法字符换成下划线
    CHECK_EQ(reactor::store::base_name(path), *log.session_key() + ".csv");
    log.end_session();
    CHECK(!log.session_key());
    CHECK_EQ(DataLogger::safe_name("a:b*c?\"d<e>f|g\\h/i\tj　k"), std::string("a_b_c__d_e_f_g_h_i_j_k"));
    auto p2 = log.begin_session("   ", T0);
    CHECK(log.session_id() == std::optional<std::string>("test"));
    (void)p2;
}
