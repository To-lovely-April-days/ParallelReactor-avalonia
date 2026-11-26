// C#: tests/ParallelReactor.Tests/EventStoreTests.cs
#include <algorithm>
#include <atomic>
#include <mutex>
#include <thread>

#include "store/event_store.hpp"
#include "store_test_util.hpp"
#include "testing.hpp"
#include <cstdint>
#include <cstdio>
#include <optional>
#include <string>
#include <vector>

using namespace reactor::store;
using store_test::contains;
using store_test::lines_of;
using store_test::slurp;
using store_test::spit;
using store_test::TempDir;

// 2026-11-06 16:12:30.120 +08:00
static WallTime T0(int64_t add_ms = 0) {
    return WallTime::with_offset(store_test::ms_at(2026, 11, 6, 16, 12, 30, 120, 480) + add_ms, 480);
}
static constexpr int64_t kDay = 86400000;

TEST(store_events, append_assigns_seq_writes_jsonl_and_notifies) {
    TempDir d;
    std::string jsonl = d / "events/events-20261106.jsonl";
    EventRecord a, b;
    {
        EventStore store(d.path);
        std::optional<EventRecord> seen;
        store.subscribe([&](const EventRecord& r) { seen = r; });
        a = store.append(T0(), "err", "RV3", "超压 · 联锁动作", "压力 515 psi");
        b = store.append(T0(1000), "info", "系统", "程序启动", "数据目录 x");
        CHECK_EQ(a.seq, int64_t(1));
        CHECK_EQ(b.seq, int64_t(2));
        CHECK_EQ(store.head(), int64_t(2));
        CHECK(seen && *seen == b);
        CHECK_EQ(a.kind, std::string("alarm.overPressure"));
        CHECK(a.channel == std::optional<int>(3));
        CHECK_EQ(b.kind, std::string("system.boot"));
        CHECK(!b.channel);
        CHECK_EQ(a.ts_ms, store_test::ms_at(2026, 11, 6, 8, 12, 30, 120));
        CHECK_EQ(a.ts_local, std::string("2026-11-06T16:12:30.120+08:00"));
        // 显式 kind 优先
        auto c = store.append(T0(2000), "info", "接口", "x", "y", "remote", std::string("PC-1"), std::string("op-9"), "op.heat.result");
        CHECK_EQ(c.kind, std::string("op.heat.result"));
        store.stop();
    }
    CHECK(reactor::store::file_exists(jsonl));
    auto lines = lines_of(jsonl);
    CHECK_EQ(lines.size(), size_t(3));
    CHECK(contains(lines[0], "\"seq\":1"));
    CHECK(contains(lines[0], "超压"));   // 中文不转义
    CHECK_EQ(lines[0].rfind("{\"seq\":1,\"seqEpoch\":1,\"ts\":\"2026-11-06T08:12:30.12Z\",\"tsLocal\":\"2026-11-06T16:12:30.120+08:00\"", 0),
             size_t(0));   // 字段顺序、ts 写法同 C#
    CHECK(contains(lines[2], "\"client\":\"PC-1\",\"opId\":\"op-9\""));
    CHECK_EQ(reactor::store::trim(slurp(d / "events/.seq")), std::string("3"));
    auto parsed = event_from_json(lines[1]);
    CHECK(parsed && *parsed == b);
}

TEST(store_events, seq_continues_across_restart_from_seq_file_or_tail) {
    TempDir d;
    {
        EventStore s1(d.path);
        for (int i = 0; i < 5; ++i) s1.append(T0(i * 1000), "info", "系统", "标记", "m" + std::to_string(i));
    }
    {
        EventStore s2(d.path);
        CHECK_EQ(s2.head(), int64_t(5));
        CHECK_EQ(s2.append(T0(60000), "info", "系统", "标记", "again").seq, int64_t(6));
    }
    std::remove((d / "events/.seq").c_str());   // 删掉 .seq 后仍能从 JSONL 尾部恢复
    EventStore s3(d.path);
    CHECK_EQ(s3.head(), int64_t(6));
    // .seq 比 JSONL 大（行丢了但 seq 已分配）：取大的，不重复使用 seq
    s3.stop();
    spit(d / "events/.seq", "42");
    EventStore s4(d.path);
    CHECK_EQ(s4.head(), int64_t(42));
}

TEST(store_events, corrupt_jsonl_uses_max_parsable_seq_plus_one) {
    TempDir d;
    std::string file = d / "events/events-20261106.jsonl";
    {
        EventStore good(d.path);
        good.append(T0(), "info", "系统", "标记", "a");
        good.append(T0(), "info", "系统", "标记", "b");
    }
    std::string extra =
        "{\"seq\":99,\"garbage\n{\"seq\":7,\"seqEpoch\":1,\"ts\":\"2026-11-06T08:00:00Z\",\"tsLocal\":\"x\",\"level\":\"info\",\"kind\":\"k\","
        "\"channel\":null,\"source\":\"system\",\"origin\":\"系统\",\"client\":null,\"opId\":null,\"title\":\"t\",\"message\":\"m\"}\n{half";
    spit(file, slurp(file) + extra);
    spit(d / "events/.seq", "not a number");
    {
        EventStore s(d.path);
        CHECK_EQ(s.head(), int64_t(7));
        CHECK_EQ(s.append(T0(), "info", "系统", "标记", "c").seq, int64_t(8));
    }
    // 半行被收尾后，新行独立成行
    bool found = false;
    for (auto& l : lines_of(file))
        if (l.rfind("{\"seq\":8", 0) == 0) found = true;
    CHECK(found);
}

TEST(store_events, concurrent_append_keeps_seq_order_everywhere) {
    TempDir d;
    const int N = 3000;
    EventStore store(d.path);
    std::vector<int64_t> seen;
    std::mutex gate;
    store.subscribe([&](const EventRecord& r) {
        std::lock_guard<std::mutex> lk(gate);
        seen.push_back(r.seq);
    });
    std::atomic<int> next{0};
    std::vector<std::thread> ths;
    for (int t = 0; t < 8; ++t)
        ths.emplace_back([&] {
            for (int i; (i = next++) < N;)
                store.append(T0(i), "info", i % 2 == 0 ? "系统" : "接口", "标记", "m" + std::to_string(i));
        });
    for (auto& t : ths) t.join();
    CHECK_EQ(store.head(), int64_t(N));
    bool in_order = static_cast<int>(seen.size()) == N;
    for (int i = 0; in_order && i < N; ++i) in_order = seen[static_cast<size_t>(i)] == i + 1;
    CHECK(in_order);
    CHECK_EQ(store.oldest_buffered(), int64_t(1));
    auto r = store.resync(0, N + 10);
    CHECK(!r.gap);
    CHECK_EQ(r.items.size(), size_t(N));
    store.stop();
    auto lines = lines_of(d / "events/events-20261106.jsonl");
    bool file_order = static_cast<int>(lines.size()) == N;
    for (int i = 0; file_order && i < N; ++i) file_order = event_from_json(lines[static_cast<size_t>(i)])->seq == i + 1;
    CHECK(file_order);
}

static std::vector<int64_t> seqs(const std::vector<EventRecord>& v) {
    std::vector<int64_t> s;
    for (auto& r : v) s.push_back(r.seq);
    return s;
}

TEST(store_events, read_files_skips_files_entirely_below_after_seq) {
    TempDir d;
    {
        EventStore s(d.path);
        for (int i = 0; i < 4; ++i) s.append(T0(), "info", "系统", "标记", "d0");           // 1..4 → 20261106
        for (int i = 0; i < 3; ++i) s.append(T0(kDay), "info", "系统", "标记", "d1");       // 5..7 → 20261107
        for (int i = 0; i < 2; ++i) s.append(T0(2 * kDay), "info", "系统", "标记", "d2");   // 8..9 → 20261108
    }
    std::string ev = d / "events";
    CHECK_EQ(EventStore::first_seq_in(ev + "/events-20261107.jsonl"), int64_t(5));
    CHECK_EQ(EventStore::first_seq_in(ev + "/nope.jsonl"), int64_t(0));
    CHECK(seqs(EventStore::read_files(ev, 5, 100)) == (std::vector<int64_t>{6, 7, 8, 9}));
    CHECK(seqs(EventStore::read_files(ev, 4, 2)) == (std::vector<int64_t>{5, 6}));
    CHECK(seqs(EventStore::read_files(ev, 8, 100)) == (std::vector<int64_t>{9}));
    CHECK(EventStore::read_files(ev, 9, 100).empty());
    CHECK_EQ(EventStore::read_files(ev, 0, 100).size(), size_t(9));
}

TEST(store_events, resync_hit_and_limits) {
    TempDir d;
    EventStore s(d.path);
    for (int i = 0; i < 20; ++i) s.append(T0(), "info", "系统", "标记", "m");
    auto r = s.resync(15, 100);
    CHECK(!r.gap);
    CHECK(seqs(r.items) == (std::vector<int64_t>{16, 17, 18, 19, 20}));
    CHECK_EQ(r.head, int64_t(20));
    CHECK_EQ(r.oldest_buffered, int64_t(1));
    auto none = s.resync(20, 100);
    CHECK(!none.gap);
    CHECK(none.items.empty());
    CHECK(seqs(s.resync(0, 3).items) == (std::vector<int64_t>{1, 2, 3}));
}

TEST(store_events, resync_gap_when_ring_wrapped_or_across_restart) {
    TempDir d;
    const int64_t R = EventStore::kRingSize;
    {
        EventStore s(d.path);
        for (int64_t i = 0; i < R + 10; ++i) s.append(T0(), "info", "系统", "标记", "m");
        CHECK_EQ(s.oldest_buffered(), int64_t(11));
        CHECK(s.resync(5, 10).gap);
        auto ok = s.resync(10, 10);
        CHECK(!ok.gap);
        CHECK_EQ(ok.items[0].seq, int64_t(11));
    }
    EventStore s2(d.path);
    s2.append(T0(kDay), "info", "系统", "标记", "after-restart");
    CHECK(s2.resync(100, 10).gap);   // 重启后环是空的
    s2.sync();
    auto from_files = s2.read_after(R + 8, 10);   // 回落到文件
    CHECK(seqs(from_files) == (std::vector<int64_t>{R + 9, R + 10, R + 11}));
}

TEST(store_events, query_page_defaults_and_has_more) {
    TempDir d;
    EventStore s(d.path);
    for (int i = 0; i < 12; ++i) s.append(T0(), "info", "系统", "标记", "m");
    auto p = s.query(std::nullopt, 5);
    CHECK_EQ(p.after_seq, int64_t(0));
    CHECK_EQ(p.count, 5);
    CHECK(p.has_more);
    CHECK_EQ(p.head, int64_t(12));
    CHECK_EQ(p.seq_epoch, 1);
    auto q = s.query(int64_t(10), std::nullopt);
    CHECK_EQ(q.count, 2);
    CHECK(!q.has_more);
    auto neg = s.query(int64_t(-5), 0);   // afterSeq 负数 → 0；limit 夹到 1
    CHECK_EQ(neg.after_seq, int64_t(0));
    CHECK_EQ(neg.count, 1);
    CHECK(neg.has_more);
    auto big = s.query(int64_t(0), 100000);   // 上限 1000
    CHECK_EQ(big.count, 12);
}

TEST(store_events, write_failure_is_counted_not_thrown) {
    TempDir d;
    spit(d / "blocker", "这是一个文件");
    std::atomic<int> reports{0};
    EventStore s(d / "blocker", [&](const std::string&) { ++reports; });
    s.append(T0(), "info", "系统", "标记", "a");
    s.append(T0(), "info", "系统", "标记", "b");
    s.sync();
    CHECK(s.dropped_lines() >= 2);
    CHECK_EQ(s.head(), int64_t(2));   // seq 照常分配
    CHECK(reports >= 1);
}

TEST(store_events, derive_kinds) {
    struct Case { const char* origin; const char* title; const char* kind; int channel; };
    Case cases[] = {
        {"RV1", "超温 · 切断加热", "alarm.overTemp", 1},
        {"RV1", "超温 · 解除", "alarm.overTempCleared", 1},
        {"RV2", "超温切断已确认", "alarm.overTempStopConfirmed", 2},
        {"RV3", "超压 · 联锁动作", "alarm.overPressure", 3},
        {"RV3", "超压 · 解除", "alarm.overPressureCleared", 3},
        {"通讯", "温控 AI-8 离线", "alarm.commLost", 0},
        {"通讯", "温控 AI-8 通讯恢复 · 报警解除", "alarm.commRestored", 0},
        {"搅拌", "搅拌电机故障 · 代码 0x80 · 在触屏清除", "alarm.stirFault", 0},
        {"搅拌", "搅拌电机故障已清除 · 报警解除", "alarm.stirFaultCleared", 0},
        {"搅拌", "搅拌开启", "event.generic", 0},
        {"系统", "急停按下 · 停止指令下发", "estop.triggered", 0},
        {"系统", "急停已复位", "estop.reset", 0},
        {"系统", "上次急停未复位，开机保持锁定", "estop.restoredOnBoot", 0},
        {"系统", "急停 · 加热已确认", "estop.part", 0},
        {"系统", "程序启动", "system.boot", 0},
        {"系统", "程序退出", "system.shutdown", 0},
        {"系统", "测试会话开始", "session.begin", 0},
        {"系统", "测试会话结束", "session.end", 0},
        {"系统", "标记", "session.marker", 0},
        {"系统", "泄漏测试开始", "leak.started", 0},
        {"阀门", "阀门整帧下发", "valve.frame", 0},
        {"设置", "修改 · 超温报警", "system.settingsChanged", 0},
        {"接口", "接口客户端已连接", "system.clientConnected", 0},
        {"接口", "接口客户端已断开", "system.clientDisconnected", 0},
        {"接口", "接口已停止", "system.apiStopped", 0},
        {"接口", "接口 HTTPS 未实现 · 已忽略 httpsPort / certPath", "api.generic", 0},
        {"接口", "接口鉴权失败", "api.authFailed", 0},
        {"接口", "接口绑定失败 · 10 秒后重试", "system.apiBindFailed", 0},
        {"接口", "接口已启动", "system.apiStarted", 0},
        {"RV9", "随便什么", "event.generic", 0},
        {"RV12", "随便什么", "event.generic", 0},
    };
    for (auto& c : cases) {
        CHECK_EQ(event_kinds::derive(c.origin, c.title), std::string(c.kind));
        auto ch = event_kinds::channel_of(c.origin);
        CHECK(c.channel == 0 ? !ch : ch == std::optional<int>(c.channel));
    }
}

// 标题以「n 号」开头的系统事件也认出通道（PC 端优先用 channel 字段；标题不再带 RVn）
TEST(event_store, channel_from_title_prefix) {
    using reactor::store::event_kinds::channel_of;
    CHECK(channel_of("系统", "3 号 程序提示") == std::optional<int>(3));
    CHECK(channel_of("RV5", "超温 · 切断加热") == std::optional<int>(5));
    CHECK(!channel_of("系统", "8 路已停止").has_value());
    CHECK(!channel_of("系统", "程序启动").has_value());
    CHECK(!channel_of("系统", "9 号 x").has_value());
}
