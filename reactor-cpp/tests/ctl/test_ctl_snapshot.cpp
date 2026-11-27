// 快照 JSON 形状：字段名 / 顺序逐个对照 C# 记录（Contracts/ParallelReactor.Contracts/Api/TelemetrySnapshot.cs 的
// TelemetrySnapshot / ModeSnap / EstopSnap / CommSnap / ChannelSnap / ApproachSnap / ProfileSnap / ProfileSegmentSnap /
// StirSnap / GasSnap / GasInletSnap / ProgramSnap / ProgramsSnap / LeakSnap / SessionSnap，System.Text.Json camelCase）。
// 以及遥测行列数、快照发布与订阅。
#include "ctl_test_util.hpp"
#include "store/telemetry_csv.hpp"
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

using namespace ctltest;
using reactor::ctl::ojson;

namespace {

std::vector<std::string> keys_of(const ojson& j) {
    std::vector<std::string> k;
    for (auto it = j.begin(); it != j.end(); ++it) k.push_back(it.key());
    return k;
}

void check_keys(const ojson& j, const std::vector<std::string>& want, const char* what) {
    auto got = keys_of(j);
    if (got != want) {
        std::printf("  [%s] 字段不符：\n    得到：", what);
        for (auto& s : got) std::printf("%s ", s.c_str());
        std::printf("\n    期望：");
        for (auto& s : want) std::printf("%s ", s.c_str());
        std::printf("\n");
    }
    CHECK(got == want);
}

// C# 记录的位置参数顺序（camelCase）
const std::vector<std::string> kTelemetry{"type", "seq", "ts", "simulated", "eventSeqApplied", "mode", "estop", "comm", "channels",
                                          "stir", "gas", "program", "programs", "leakTest", "session", "calib",
                                          "autotune"};  // autotune：第二阶段新增（只加在末尾）
const std::vector<std::string> kMode{"control", "holder"};
const std::vector<std::string> kEstop{"active", "heat", "stir", "valves", "since", "latched", "restoredOnBoot"};
const std::vector<std::string> kComm{"temp", "pressure", "stir", "io"};
const std::vector<std::string> kChannel{"id", "installed", "state", "alarmCause", "overTempTripped", "overPressureTripped",
                                        "startedBy", "tempC", "tempSpC", "tempSpDeviceC", "heatOutputPct", "tempOnline",
                                        "autotuning", "atMode", "spMode", "approach", "profile", "pressurePsi", "pressureRaw",
                                        "pressureSpPsi", "gasConsumed", "valveOpen", "programOwned", "program", "gasRate",
                                        "solutionVolume"};
const std::vector<std::string> kApproach{"phase", "targetC", "currentSpC", "statusText"};
const std::vector<std::string> kProfile{"running", "segCount", "segments", "elapsedMin", "totalMinutes", "complete", "statusText"};
const std::vector<std::string> kSegment{"index", "tempC", "minutes", "enabled", "active"};
const std::vector<std::string> kStir{"on", "rpmSp", "online", "locked", "motorCurrentA", "busVoltageV", "fault", "alarmCode",
                                     "rampMsPer1000Rpm"};
const std::vector<std::string> kGas{"inlets", "ventOpen", "manualValve", "task", "owner"};
const std::vector<std::string> kInlet{"id", "open"};
const std::vector<std::string> kProgram{"running", "channel", "status", "stepIndex", "stepCount", "phase", "detail"};
const std::vector<std::string> kPrograms{"running", "channels", "gasHolder", "waitingGas", "summary"};
const std::vector<std::string> kLeak{"running", "progress", "phase", "runId", "done", "failed", "stage", "stageLeftSec",
                                     "totalLeftSec", "startedAt", "startedBy", "targetPsi", "thresholdPsiH", "channels", "vented",
                                     "aborted", "failReason", "finishedAt"};
const std::vector<std::string> kSession{"active", "id", "testId", "file"};
const std::vector<std::string> kTick{"intervalMs", "entered", "skipped", "overruns", "lastTookMs", "maxTookMs", "lastOverrunMs"};
const std::vector<std::string> kModuleComm{"configured", "online", "polls", "fails", "lastOkAt", "valuesAt"};

}  // namespace

TEST(ctl_snapshot, json_shape_matches_csharp_dto) {
    Rig r;
    r.ticks(6);
    r.c().channel_mut(2).set_state(reactor::core::ReactorState::Idle);  // 开机默认启用；这里把 2 号停用验证 idle 形状
    r.c().set_sp_mode(3, "curve");
    r.c().start_curve(3, std::string("local"));
    r.c().start_fixed_heat(1, 60.0, std::string("local"));
    r.ticks(2);
    auto s = r.c().snapshot();
    CHECK(s != nullptr);
    if (!s) return;
    ojson j = reactor::ctl::to_json(*s);
    check_keys(j, kTelemetry, "telemetry");
    check_keys(j["mode"], kMode, "mode");
    check_keys(j["estop"], kEstop, "estop");
    check_keys(j["comm"], kComm, "comm");
    CHECK_EQ(j["channels"].size(), 8u);
    for (const auto& c : j["channels"]) {
        check_keys(c, kChannel, "channel");
        check_keys(c["approach"], kApproach, "approach");
    }
    check_keys(j["channels"][2]["profile"], kProfile, "profile");
    CHECK_EQ(j["channels"][2]["profile"]["segments"].size(), 16u);
    check_keys(j["channels"][2]["profile"]["segments"][0], kSegment, "segment");
    check_keys(j["stir"], kStir, "stir");
    check_keys(j["gas"], kGas, "gas");
    CHECK_EQ(j["gas"]["inlets"].size(), 3u);
    check_keys(j["gas"]["inlets"][0], kInlet, "inlet");
    CHECK_EQ(j["gas"]["inlets"][0]["id"].get<std::string>(), std::string("inert"));
    CHECK_EQ(j["gas"]["inlets"][1]["id"].get<std::string>(), std::string("A"));
    check_keys(j["program"], kProgram, "program");
    check_keys(j["programs"], kPrograms, "programs");
    check_keys(j["leakTest"], kLeak, "leakTest");
    check_keys(j["session"], kSession, "session");
    CHECK(j["calib"].is_null());
    check_keys(reactor::ctl::tick_stats_to_json(s->tick), kTick, "tick");
    check_keys(reactor::ctl::module_comm_to_json(s->temp_comm), kModuleComm, "moduleComm");

    // 值的形状
    CHECK_EQ(j["type"].get<std::string>(), std::string("telemetry"));
    std::string ts = j["ts"].get<std::string>();
    CHECK(!ts.empty() && ts.back() == 'Z');
    CHECK(j["simulated"].get<bool>());
    CHECK_EQ(j["estop"]["active"].get<bool>(), false);
    CHECK_EQ(j["estop"]["heat"].get<std::string>(), std::string("pending"));
    CHECK_EQ(j["channels"][0]["state"].get<std::string>(), std::string("heating"));
    CHECK_EQ(j["channels"][1]["state"].get<std::string>(), std::string("idle"));
    CHECK(j["channels"][1]["startedBy"].is_null());
    CHECK_EQ(j["channels"][0]["startedBy"].get<std::string>(), std::string("local"));
    CHECK_EQ(j["channels"][0]["approach"]["phase"].get<std::string>(), std::string("settle"));
    CHECK(j["channels"][0]["profile"].is_null());  // fixed 模式不带曲线
    CHECK(j["channels"][0]["tempC"].is_number());
    CHECK(j["channels"][0]["atMode"].is_number_integer());
    CHECK(j["channels"][0]["pressureRaw"].is_number_integer());
    CHECK(j["channels"][0]["tempSpC"].is_number_integer());  // 60.0 写成 60（同 System.Text.Json）
    CHECK(j["channels"][0]["gasConsumed"].is_null());
    CHECK(j["channels"][0]["program"].is_null());
    CHECK(j["stir"]["online"].get<bool>());
    CHECK_EQ(j["stir"]["rampMsPer1000Rpm"].get<int>(), 1000);
    CHECK(j["seq"].get<int64_t>() == s->seq);
    // 字符串化后能被再解析（中文不转义）
    std::string text = reactor::store::dump_json(j);
    CHECK(text.find("\"telemetry\"") != std::string::npos);
    CHECK(nlohmann::json::parse(text)["channels"][0]["id"].get<int>() == 1);
}

TEST(ctl_snapshot, publish_and_subscribe_each_tick) {
    Rig r;
    int got = 0;
    int64_t last_seq = 0;
    r.c().snapshots().subscribe([&](const reactor::ctl::SnapshotStore::Ptr& p) {
        ++got;
        CHECK(p->seq > last_seq);
        last_seq = p->seq;
    });
    CHECK(r.c().snapshot() == nullptr);  // 首个周期之前没有快照（接口层回 503 STARTING）
    r.ticks(3);
    CHECK_EQ(got, 3);
    CHECK_EQ(r.c().snapshot()->seq, 3);
    CHECK_EQ(r.c().snapshot()->tick.entered, 3);
    CHECK(!r.c().snapshot()->alarms.empty());  // 「程序启动」等
}

TEST(ctl_snapshot, telemetry_row_column_count) {
    Rig r;
    r.ticks(6);
    r.c().start_fixed_heat(2, 70.0, std::string("local"));
    r.ticks(2);
    auto row = r.c().build_telemetry_row();
    auto fields = reactor::store::telemetry_csv::row_fields(row);
    CHECK_EQ(fields.size(), reactor::store::telemetry_csv::header_fields().size());
    CHECK_EQ(fields.size(), 173u);
    CHECK_EQ(row.ch[1].state, std::string("Heating"));
    CHECK_EQ(row.ch[1].approach, std::string("Settle"));
    CHECK(row.ch[0].pv.has_value());
    CHECK(row.ch[0].press_psi.has_value());
    CHECK(row.temp.configured && row.temp.online);
    CHECK(!row.estopped);
    CHECK_EQ(row.stir_motor_rpm.value_or(0), reactor::hw::stir_gear::to_motor(200, reactor::hw::stir_gear::kDefaultRatio));
}

// 改设置后快照里的设置立即更新（GET /settings 读它），不等下一个周期，也不多推一帧
TEST(ctl_snapshot, settings_change_visible_before_next_tick) {
    Rig r;
    int got = 0;
    r.c().snapshots().subscribe([&](const reactor::ctl::SnapshotStore::Ptr&) { ++got; });
    r.ticks(1);
    int64_t rev = r.c().snapshot()->settings.revision;
    r.c().update_settings([](reactor::store::SettingsState& s) { s.settings.leak_settle_min = 3; }, true);
    CHECK_EQ(r.c().snapshot()->settings.revision, rev + 1);
    CHECK_EQ(r.c().snapshot()->settings.settings.leak_settle_min, 3.0);
    CHECK_EQ(r.c().snapshot()->seq, 1);
    CHECK_EQ(got, 1);
}
