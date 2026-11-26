// C#: tests/ParallelReactor.Tests/SessionManifestTests.cs
#include "store/session.hpp"
#include "store/store_util.hpp"
#include "store_test_util.hpp"
#include "testing.hpp"
#include <cstdint>
#include <optional>
#include <string>
#include <utility>
#include <vector>

using namespace reactor::store;
using store_test::contains;
using store_test::ms_at;
using store_test::spit;
using store_test::TempDir;

static const int64_t T0 = ms_at(2026, 11, 7, 8, 0, 0);
static const int64_t MIN = 60000;

TEST(store_session, begin_marker_tags_end_write_each_version) {
    std::vector<std::pair<std::string, std::string>> writes;
    SessionBook book([&](const std::string& p, const std::string& j) { writes.emplace_back(p, j); });
    CHECK(!book.current());
    auto m = book.begin("TP-017-20261107-080000", "TP-017", " 性能测试 ", T0, "remote", std::string("PC-1"), true, 100,
                        "TP-017-20261107-080000.csv");
    CHECK(book.current_id() == std::optional<std::string>("TP-017-20261107-080000"));
    CHECK(m.note == std::optional<std::string>("性能测试"));
    CHECK_EQ(m.state, std::string("active"));
    CHECK_EQ(writes[0].first, std::string("sessions/TP-017-20261107-080000.json"));

    auto mk = book.add_marker("开始升温", T0 + MIN, 105, "remote", std::string("PC-1"));
    CHECK(mk && mk->id == 1);
    CHECK_EQ(book.add_marker("保温", T0 + 2 * MIN, 106, "local", std::nullopt)->id, 2);
    book.tag_program_run(1, T0 + 3 * MIN, "远程（PC-1）");
    book.tag_leak_test("leak-20261107-080500", T0 + 5 * MIN);
    auto cur = *book.current();
    CHECK_EQ(cur.markers.size(), size_t(2));
    CHECK_EQ(cur.program_runs.size(), size_t(1));
    CHECK_EQ(cur.program_runs[0].started_by, std::string("远程（PC-1）"));
    CHECK_EQ(cur.leak_tests.size(), size_t(1));

    auto ended = book.end(T0 + 10 * MIN, 120);
    CHECK(!book.current());
    CHECK_EQ(ended->state, std::string("ended"));
    CHECK(ended->end_event_seq == std::optional<int64_t>(120));
    CHECK_EQ(writes.size(), size_t(6));   // begin + 2 markers + 2 tags + end

    // JSON 往返（camelCase、嵌套数组）
    auto back = SessionManifest::from_json(writes.back().second);
    CHECK(back.has_value());
    CHECK_EQ(back->markers[0].text, std::string("开始升温"));
    CHECK(back->markers[0].client == std::optional<std::string>("PC-1"));
    CHECK_EQ(back->markers[1].seq, int64_t(106));
    CHECK(back->ended_at_ms == std::optional<int64_t>(T0 + 10 * MIN));
    CHECK_EQ(back->start_event_seq, int64_t(100));
    CHECK(back->simulated);
    CHECK_EQ(back->leak_tests[0].run_id, std::string("leak-20261107-080500"));
    CHECK(contains(writes.back().second, "\"testId\": \"TP-017\""));
    CHECK(contains(writes.back().second, "\"startedAt\": \"2026-11-07T08:00:00Z\""));
    CHECK_EQ(back->to_json(), writes.back().second);
}

TEST(store_session, no_session_marker_and_tags_are_noops) {
    int writes = 0;
    SessionBook book([&](const std::string&, const std::string&) { ++writes; });
    CHECK(!book.add_marker("x", T0, 1, "local", std::nullopt));
    book.tag_program_run(1, T0, "local");
    book.tag_leak_test("leak-20261107-080500", T0);
    CHECK(!book.end(T0, 1));
    CHECK_EQ(writes, 0);
}

TEST(store_session, reads_csharp_manifest_case_insensitive) {
    // C# 写出的样子（System.Text.Json camelCase + 缩进；中文按默认编码器转义成 \uXXXX；前面有 BOM）
    std::string cs = std::string(kUtf8Bom) + R"({
  "id": "A-20261107-080000",
  "testId": "A",
  "note": null,
  "startedAt": "2026-11-07T08:00:00.1234567Z",
  "endedAt": null,
  "state": "active",
  "source": "local",
  "client": null,
  "simulated": false,
  "startEventSeq": 1,
  "endEventSeq": null,
  "file": "A-20261107-080000.csv",
  "markers": [ { "id": 1, "ts": "2026-11-07T08:01:00Z", "seq": 3, "text": "开始", "source": "local", "client": null } ],
  "programRuns": [],
  "LeakTests": [ { "RunId": "leak-20261107-080500", "StartedAt": "2026-11-07T08:05:00Z" } ]
})";
    auto m = SessionManifest::from_json(cs);
    CHECK(m.has_value());
    CHECK_EQ(m->started_at_ms, T0 + 123);
    CHECK_EQ(m->markers[0].text, std::string("开始"));
    CHECK_EQ(m->leak_tests.size(), size_t(1));
    CHECK_EQ(m->leak_tests[0].run_id, std::string("leak-20261107-080500"));
    CHECK(!SessionManifest::from_json("{not json"));
}

TEST(store_session, recover_interrupted_marks_active_manifests) {
    TempDir d;
    reactor::store::make_dirs(d / "sessions");
    SessionManifest active;
    active.id = "A-20261107-080000";
    active.test_id = "A";
    active.started_at_ms = T0;
    active.state = SessionManifest::kActive;
    active.source = "local";
    active.start_event_seq = 1;
    active.file = "A-20261107-080000.csv";
    SessionManifest ended = active;
    ended.id = "B-20261107-080000";
    ended.state = SessionManifest::kEnded;
    ended.ended_at_ms = T0 + 3600000;
    spit(d / "sessions/A-20261107-080000.json", active.to_json());
    spit(d / "sessions/B-20261107-080000.json", ended.to_json());
    spit(d / "sessions/bad.json", "{not json");
    spit(d / "sessions/A-20261107-080000.csv", "时间\n");
    auto csv_mtime = reactor::store::stat_path(d / "sessions/A-20261107-080000.csv").mtime_ms;
    auto fixed = SessionBook::recover_interrupted(d.path);
    CHECK(fixed == (std::vector<std::string>{"A-20261107-080000"}));
    auto a = SessionManifest::load(d / "sessions/A-20261107-080000.json");
    CHECK(a && a->state == "interrupted");
    CHECK(a->ended_at_ms == std::optional<int64_t>(csv_mtime));   // endedAt = CSV 修改时间
    CHECK_EQ(store_test::slurp(d / "sessions/A-20261107-080000.json").compare(0, 3, kUtf8Bom), 0);
    CHECK_EQ(SessionManifest::load(d / "sessions/B-20261107-080000.json")->state, std::string("ended"));
    CHECK(SessionBook::recover_interrupted(d.path).empty());   // 幂等
}
