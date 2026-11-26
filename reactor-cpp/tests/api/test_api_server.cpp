// 接口层集成测试：进程内起完整后台（模拟硬件 + 控制线程），用真 socket（TCP 127.0.0.1:随机端口 + 临时 Unix socket）打。
#include <fstream>

#include "api/api_test_util.hpp"
#include "common/clock.hpp"
#include "store/history_files.hpp"
#include "testing.hpp"
#include <cstdint>
#include <cstdio>
#include <string>
#include <utility>
#include <vector>

using namespace apitest;

TEST(api_server, auth_roles_and_local_only) {
    ApiRig t;
    // /health 免鉴权
    auto h = t.tcp("GET", "/api/v1/health", "");
    CHECK_EQ(h.status, 200);
    CHECK(h.body["ok"].get<bool>());
    CHECK_EQ(h.body["apiVersion"].get<std::string>(), std::string("1.0"));
    CHECK_EQ(t.tcp("GET", "/api/v1/snapshot", "").status, 401);
    CHECK_EQ(t.tcp("GET", "/api/v1/snapshot", "viewtoken").status, 200);
    // viewer 写命令 → 403；estop 位的 viewer 能做停止类
    auto v = t.tcp("POST", "/api/v1/stop-all", "viewtoken", "{}");
    CHECK_EQ(v.status, 403);
    CHECK_EQ(v.body["error"]["code"].get<std::string>(), std::string("FORBIDDEN_ROLE"));
    CHECK_EQ(v.body["error"]["message"].get<std::string>(), std::string("停止类命令需要 operator 角色或 estop 权限位"));
    CHECK(v.body["opEventSeq"].get<int64_t>() > 0);   // 被拒也审计
    CHECK_EQ(t.tcp("POST", "/api/v1/stop-all", "esttoken", "{}").status, 200);
    // admin 才能改 PID
    t.tcp("GET", "/api/v1/health", "");
    // 本机专用端点：TCP 上 404「没有这个端点」，Unix 上存在
    for (auto pr : std::vector<std::pair<std::string, std::string>>{{"PATCH", "/api/v1/settings"}, {"GET", "/api/v1/network"}, {"GET", "/api/v1/usb"},
                                                                    {"POST", "/api/v1/control/requests/x/grant"}, {"PUT", "/api/v1/pid/bands"},
                                                                    {"GET", "/api/v1/calib"}, {"PUT", "/api/v1/system/language"}}) {
        auto r = t.tcp(pr.first, pr.second, "admtoken", "{}");
        CHECK_EQ(r.status, 404);
        CHECK_EQ(r.body["error"]["message"].get<std::string>(), std::string("没有这个端点"));
    }
    CHECK_EQ(t.local("GET", "/api/v1/usb").status, 200);
    // 方法不对 → 404（同 C# MapFallback），不是 405
    CHECK_EQ(t.tcp("DELETE", "/api/v1/snapshot", "optoken").status, 404);
    // Unix 入口：本地身份，不要令牌
    CHECK_EQ(t.local("GET", "/api/v1/snapshot").status, 200);
}

TEST(api_server, lease_flow_and_categories) {
    ApiRig t;
    // 本地模式：远程控制类 → 409 MODE_LOCAL；停止类免租约
    auto c = t.tcp("POST", "/api/v1/channels/1/heat/start", "optoken", "{\"tempC\":60}");
    CHECK_EQ(c.status, 409);
    CHECK_EQ(c.body["error"]["code"].get<std::string>(), std::string("MODE_LOCAL"));
    CHECK_EQ(t.tcp("POST", "/api/v1/channels/1/heat/stop", "optoken").status, 200);
    // 申请：缺幂等键 428；带键 202 pending
    CHECK_EQ(t.tcp("POST", "/api/v1/control/request", "optoken", "{\"clientName\":\"PC-1\"}").status, 428);
    nettest::WsClient ws;
    CHECK(ws.open_fd(nettest::connect_unix(t.sock), "/api/v1/ws", ""));
    std::string m;
    CHECK(ws.next_text(m));
    CHECK(json::parse(m)["type"] == "hello");
    auto r = t.tcp("POST", "/api/v1/control/request", "optoken", "{\"clientName\":\"PC-1\"}", ApiRig::idem("r1"));
    CHECK_EQ(r.status, 202);
    CHECK_EQ(r.body["status"].get<std::string>(), std::string("accepted"));
    CHECK_EQ(r.body["result"]["result"].get<std::string>(), std::string("pending"));
    std::string rid = r.body["result"]["requestId"], key = r.body["result"]["requestKey"];
    // 触屏收到带 pending 的 control 消息
    bool saw = false;
    for (int i = 0; i < 20 && !saw && ws.next_text(m); ++i) {
        auto j = json::parse(m);
        if (j["type"] == "control" && j["event"] == "requested") {
            saw = j["pending"]["requestId"] == rid && j["leaseId"].is_null();
        }
    }
    CHECK(saw);
    auto st = t.tcp("GET", "/api/v1/control", "viewtoken");
    CHECK_EQ(st.body["state"].get<std::string>(), std::string("requested"));
    CHECK_EQ(st.body["pending"]["clientName"].get<std::string>(), std::string("PC-1"));
    // 触屏允许
    auto g = t.local("POST", "/api/v1/control/requests/" + rid + "/grant", "{}");
    CHECK_EQ(g.status, 200);
    auto noKey = t.tcp("GET", "/api/v1/control/requests/" + rid, "optoken");
    CHECK(noKey.body["leaseId"].is_null());
    auto q = t.tcp("GET", "/api/v1/control/requests/" + rid, "optoken", "", "X-Control-Request-Key: " + key + "\r\n");
    CHECK_EQ(q.body["result"].get<std::string>(), std::string("granted"));
    std::string lease = q.body["leaseId"];
    CHECK_EQ(lease.size(), static_cast<size_t>(32));
    // 无头 / 别人 / 正确
    CHECK_EQ(t.tcp("PUT", "/api/v1/channels/1/temperature/setpoint", "optoken", "{\"tempC\":60}").body["error"]["code"].get<std::string>(),
             std::string("NOT_CONTROL_HOLDER"));
    CHECK_EQ(t.tcp("PUT", "/api/v1/channels/1/temperature/setpoint", "optoken2", "{\"tempC\":60}", ApiRig::lease(lease)).body["error"]["code"]
                 .get<std::string>(),
             std::string("NOT_CONTROL_HOLDER"));
    auto ok = t.tcp("PUT", "/api/v1/channels/1/temperature/setpoint", "optoken", "{\"tempC\":60}", ApiRig::lease(lease));
    CHECK_EQ(ok.status, 200);
    CHECK_EQ(ok.body["result"]["tempSpC"].get<int>(), 60);
    // 远程控制中：触屏控制类 → 409 MODE_REMOTE；触屏停止类照样可以
    auto lr = t.local("PUT", "/api/v1/channels/2/temperature/setpoint", "{\"tempC\":60}");
    CHECK_EQ(lr.status, 409);
    CHECK_EQ(lr.body["error"]["code"].get<std::string>(), std::string("MODE_REMOTE"));
    CHECK_EQ(t.local("POST", "/api/v1/stop-all", "{}").status, 200);
    // 心跳
    auto hb = t.tcp("POST", "/api/v1/control/heartbeat", "optoken", "{}", ApiRig::lease(lease));
    CHECK_EQ(hb.status, 200);
    CHECK_EQ(hb.body["result"]["mode"].get<std::string>(), std::string("remote"));
    CHECK_EQ(t.tcp("POST", "/api/v1/control/heartbeat", "viewtoken", "{}", ApiRig::lease(lease)).status, 403);
    // 快照 mode 跟着变
    CHECK(wait_until([&] { return t.tcp("GET", "/api/v1/snapshot", "optoken").body["mode"]["control"] == "remote"; }));
    // 心跳丢失（2 s）→ 宽限 2 s → 回本地；旧租约 → CONTROL_LEASE_EXPIRED（再授权之后才分得出）
    CHECK(wait_until([&] { return t.tcp("GET", "/api/v1/control", "optoken").body["state"] == "local"; }, 8000));
    std::string lease2 = t.take_control("optoken2", "PC-2");
    CHECK_EQ(lease2.size(), static_cast<size_t>(32));
    CHECK_EQ(t.tcp("PUT", "/api/v1/channels/1/temperature/setpoint", "optoken", "{\"tempC\":61}", ApiRig::lease(lease)).body["error"]["code"]
                 .get<std::string>(),
             std::string("CONTROL_LEASE_EXPIRED"));
    // 释放 → 回本地
    CHECK_EQ(t.tcp("POST", "/api/v1/control/release", "optoken2", "{}", ApiRig::lease(lease2)).status, 200);
    CHECK_EQ(t.tcp("GET", "/api/v1/control", "optoken").body["mode"].get<std::string>(), std::string("local"));
    // 拒绝后 30 s 冷却 → 429
    auto r3 = t.tcp("POST", "/api/v1/control/request", "optoken", "{\"clientName\":\"PC-1\"}", ApiRig::idem("r3"));
    std::string rid3 = r3.body["result"]["requestId"];
    CHECK_EQ(t.local("POST", "/api/v1/control/requests/" + rid3 + "/deny", "{}").status, 200);
    auto r4 = t.tcp("POST", "/api/v1/control/request", "optoken", "{\"clientName\":\"PC-1\"}", ApiRig::idem("r4"));
    CHECK_EQ(r4.status, 429);
    CHECK_EQ(r4.body["error"]["code"].get<std::string>(), std::string("RATE_LIMITED"));
}

TEST(api_server, idempotency_replay_mismatch_and_error_mapping) {
    ApiRig t;
    auto a = t.tcp("POST", "/api/v1/stop-all", "optoken", "{}", ApiRig::idem("k1"));
    CHECK_EQ(a.status, 200);
    auto b = t.tcp("POST", "/api/v1/stop-all", "optoken", "{}", ApiRig::idem("k1"));
    CHECK_EQ(b.status, 200);
    CHECK_EQ(b.h("idempotent-replay"), std::string("true"));
    CHECK_EQ(a.raw, b.raw);
    CHECK_EQ(a.body["opId"].get<std::string>(), std::string("k1"));
    auto c = t.tcp("POST", "/api/v1/stop-all", "optoken", "{\"stopStir\":true}", ApiRig::idem("k1"));
    CHECK_EQ(c.status, 409);
    CHECK_EQ(c.body["error"]["code"].get<std::string>(), std::string("IDEMPOTENCY_MISMATCH"));
    // 另一个令牌同键：互不影响
    CHECK_EQ(t.tcp("POST", "/api/v1/stop-all", "optoken2", "{\"stopStir\":true}", ApiRig::idem("k1")).status, 200);
    // 400：坏 JSON / 类型不对；404 CHANNEL_NOT_FOUND；422 PARAM_OUT_OF_RANGE；428
    auto bad = t.local("PUT", "/api/v1/channels/1/temperature/setpoint", "{oops");
    CHECK_EQ(bad.status, 400);
    CHECK(bad.body["error"]["message"].get<std::string>().rfind("正文不是合法 JSON", 0) == 0);
    auto ty = t.local("PUT", "/api/v1/channels/1/temperature/setpoint", "{\"tempC\":\"x\"}");
    CHECK_EQ(ty.body["error"]["message"].get<std::string>(), std::string("tempC 须为数值"));
    auto nf = t.local("POST", "/api/v1/channels/9/heat/start", "{}");
    CHECK_EQ(nf.status, 404);
    CHECK_EQ(nf.body["error"]["code"].get<std::string>(), std::string("CHANNEL_NOT_FOUND"));
    CHECK_EQ(t.tcp("GET", "/api/v1/channels/0", "optoken").status, 404);
    auto rg = t.local("PUT", "/api/v1/channels/1/temperature/setpoint", "{\"tempC\":999}");
    CHECK_EQ(rg.status, 422);
    CHECK_EQ(rg.body["error"]["detail"]["max"].get<int>(), 200);
    CHECK_EQ(t.local("POST", "/api/v1/run", "{}").status, 428);
    // 尾逗号 / 注释照收（同 C# JsonDocumentOptions）
    CHECK_EQ(t.local("PUT", "/api/v1/channels/1/temperature/setpoint", "{\"tempC\":50, /* x */ }").status, 200);
    // 写命令信封字段顺序（C# CommandResponseDto）
    CHECK(a.raw.rfind("{\"ok\":true,\"opId\":\"k1\",\"status\":\"confirmed\",\"result\":", 0) == 0);
    CHECK(rg.raw.rfind("{\"ok\":false,\"opId\":", 0) == 0);
    // GET 错误：{ok:false, error:{code, message, detail}}
    auto e = t.tcp("GET", "/api/v1/files?type=x", "optoken");
    CHECK_EQ(e.status, 400);
    CHECK(e.raw.rfind("{\"ok\":false,\"error\":{\"code\":\"BAD_REQUEST\"", 0) == 0);
}

TEST(api_server, program_put_run_with_ws_progress_and_runs) {
    ApiRig t;
    nettest::WsClient ws;
    CHECK(ws.open_fd(nettest::connect_unix(t.sock), "/api/v1/ws", ""));
    ws.send_text("{\"type\":\"subscribe\",\"topics\":[\"telemetry\",\"events\",\"alarms\",\"progress\"]}");
    std::string steps = "[{\"type\":\"stir\",\"params\":{\"rpm\":100}},{\"type\":\"hold\",\"params\":{\"until\":\"time\",\"time\":0.02}}]";
    auto put = t.local("PUT", "/api/v1/channels/1/program", "{\"steps\":" + steps + ",\"name\":\"短程序\",\"sourceId\":\"P1\"}");
    CHECK_EQ(put.status, 200);
    if (put.status != 200) std::printf("    PUT 程序：%s\n", put.raw.c_str());
    auto gp = t.local("GET", "/api/v1/channels/1/program");
    CHECK_EQ(gp.body["name"].get<std::string>(), std::string("短程序"));
    CHECK_EQ(gp.body["sourceId"].get<std::string>(), std::string("P1"));
    CHECK(gp.body["steps"].size() >= 2);   // 引擎补一个结束步
    CHECK_EQ(t.local("POST", "/api/v1/channels/1/enable", "{}").status, 200);
    auto run = t.local("POST", "/api/v1/channels/1/program/run", "{}", ApiRig::idem("run-1"));
    CHECK_EQ(run.status, 202);
    if (run.status != 202) std::printf("    下发：%s\n", run.raw.c_str());
    bool tele = false, prog = false, op = false;
    std::string m;
    for (int i = 0; i < 400 && !(tele && prog && op) && ws.next_text(m, 5000); ++i) {
        auto j = json::parse(m, nullptr, false);
        if (j["type"] == "telemetry") tele = tele || (j.contains("revs") && j.contains("system") && j["channels"][0].contains("heatOn"));
        if (j["type"] == "progress")
            for (auto& c : j["channels"])
                if (c["id"] == 1 && c["programName"] == "短程序" && c["steps"].size() >= 2 && c["plan"]["segs"].size() == c["steps"].size()) prog = true;
        if (j["type"] == "opEvent" && j["action"] == "program.run") op = j["httpStatus"] == 202 && j["source"] == "local";
    }
    CHECK(tele);
    CHECK(prog);
    CHECK(op);
    // 跑完：program/run 不再 running；运行记录里有一条
    CHECK(wait_until([&] {
        auto r = t.local("GET", "/api/v1/channels/1/program/run");
        return r.status == 200 && r.body["run"]["running"] == false;
    }, 15000));
    CHECK(wait_until([&] {
        auto r = t.local("GET", "/api/v1/runs");
        return r.body["count"].get<int>() >= 1 && !r.body["items"][0]["end"].is_null();
    }, 5000));
    auto runs = t.tcp("GET", "/api/v1/runs", "viewtoken");
    if (runs.body["count"].get<int>() < 1) return;
    std::string rid = runs.body["items"][0]["id"];
    CHECK_EQ(runs.body["items"][0]["programName"].get<std::string>(), std::string("短程序"));
    auto one = t.tcp("GET", "/api/v1/runs/" + rid, "viewtoken");
    CHECK(one.body.contains("trend") && one.body.contains("program") && one.body.contains("stepMinutes"));
    CHECK_EQ(t.local("PUT", "/api/v1/runs/" + rid + "/note", "{\"note\":\"批号 B-17\"}").status, 200);
    CHECK_EQ(t.local("GET", "/api/v1/runs/" + rid).body["note"].get<std::string>(), std::string("批号 B-17"));
    // 趋势：有数据
    std::string from = reactor::store::fmt_utc_json(reactor::wall_ms() - 60000), to = reactor::store::fmt_utc_json(reactor::wall_ms() + 1000);
    auto tr = t.tcp("GET", "/api/v1/trend?from=" + from + "&to=" + to + "&stepMs=1000", "viewtoken");
    CHECK_EQ(tr.status, 200);
    CHECK_EQ(tr.body["channels"].size(), static_cast<size_t>(8));
    bool any = false;
    for (auto& v : tr.body["channels"][0]["tempC"]) any = any || v.is_number();
    CHECK(any);
}

TEST(api_server, ws_events_resync_and_gap) {
    ApiRig t;
    for (int i = 0; i < 5100; ++i)
        t.logger->event(reactor::store::WallTime::local(reactor::wall_ms()), "info", "测试", "批量事件", std::to_string(i));
    int64_t head = t.events->head();
    nettest::WsClient ws;
    CHECK(ws.open(t.port, "/api/v1/ws", "viewtoken"));
    std::string m;
    CHECK(ws.next_text(m));
    auto hello = json::parse(m);
    CHECK_EQ(hello["type"].get<std::string>(), std::string("hello"));
    CHECK(hello["eventSeqHead"].get<int64_t>() >= head);
    ws.send_text("{\"type\":\"subscribe\",\"topics\":[\"events\"],\"resumeAfterEventSeq\":1,\"seqEpoch\":1}");
    bool gap = false, subscribed = false;
    for (int i = 0; i < 50 && !(gap && subscribed) && ws.next_text(m); ++i) {
        auto j = json::parse(m);
        if (j["type"] == "gap") gap = j["reason"] == "gap_too_large";
        if (j["type"] == "subscribed") subscribed = j["topics"].size() == 1 && j["replayed"] == 0;
    }
    CHECK(gap && subscribed);
    ws.send_text("{\"type\":\"resync\",\"afterSeq\":" + std::to_string(head - 3) + "}");
    int replayed = -1;
    for (int i = 0; i < 50 && replayed < 0 && ws.next_text(m); ++i) {
        auto j = json::parse(m);
        if (j["type"] == "resynced") replayed = j["replayed"];
    }
    CHECK(replayed >= 3);
    ws.send_text("{\"type\":\"subscribe\",\"topics\":[\"events\"],\"resumeAfterEventSeq\":1,\"seqEpoch\":7}");
    bool epoch = false;
    for (int i = 0; i < 50 && !epoch && ws.next_text(m); ++i) epoch = json::parse(m)["reason"] == "epoch_changed";
    CHECK(epoch);
    ws.send_text("{\"type\":\"ping\"}");
    bool pong = false;
    for (int i = 0; i < 50 && !pong && ws.next_text(m); ++i) pong = json::parse(m)["type"] == "pong";
    CHECK(pong);
    ws.send_text("{\"type\":\"what\"}");
    bool err = false;
    for (int i = 0; i < 50 && !err && ws.next_text(m); ++i) err = json::parse(m)["type"] == "error";
    CHECK(err);
    // /events 分页
    auto page = t.tcp("GET", "/api/v1/events?afterSeq=0&limit=10", "viewtoken");
    CHECK_EQ(page.body["count"].get<int>(), 10);
    CHECK(page.body["more"].get<bool>());
    CHECK(page.raw.rfind("{\"seqEpoch\":1,\"eventSeqHead\":", 0) == 0);
    CHECK_EQ(t.tcp("GET", "/api/v1/events?afterSeq=x", "viewtoken").status, 400);
}

TEST(api_server, file_download_with_range_and_telemetry_stream) {
    ApiRig t;
    std::string name = "telemetry-20260101.csv";
    auto path = reactor::store::safe_path::try_resolve(t.dir, "telemetry", name);
    CHECK(path.has_value());
    reactor::store::make_dirs(reactor::store::dir_name(*path));
    {
        std::ofstream f(*path);
        f << "时间,标记\n2026-01-01T00:00:01.000+00:00,a\n2026-01-01T00:00:02.000+00:00,b\n";
    }
    auto full = t.tcp("GET", "/api/v1/files/telemetry/" + name, "viewtoken");
    CHECK_EQ(full.status, 200);
    CHECK(!full.h("etag").empty());
    auto part = t.tcp("GET", "/api/v1/files/telemetry/" + name, "viewtoken", "", "Range: bytes=0-9\r\n");
    CHECK_EQ(part.status, 206);
    CHECK_EQ(part.raw.size(), static_cast<size_t>(10));
    CHECK(part.h("content-range").rfind("bytes 0-9/", 0) == 0);
    CHECK_EQ(t.tcp("GET", "/api/v1/files/telemetry/..%2Fx", "viewtoken").status, 404);
    auto list = t.tcp("GET", "/api/v1/files?type=telemetry", "viewtoken");
    CHECK(list.body["count"].get<int>() >= 1);
    CHECK(list.body["items"][0].contains("desc"));
    auto stream = t.tcp("GET", "/api/v1/telemetry?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z", "viewtoken");
    CHECK_EQ(stream.status, 200);
    CHECK(stream.raw.find("2026-01-01T00:00:02") != std::string::npos);
    CHECK_EQ(t.tcp("GET", "/api/v1/telemetry?from=2026-01-01T00:00:00&to=2026-01-02T00:00:00Z", "viewtoken").status, 400);
}

TEST(api_server, compat_json_shapes_vs_csharp_dtos) {
    ApiRig t;
    auto keys = [](const json& j) {
        std::vector<std::string> k;
        for (auto it = j.begin(); it != j.end(); ++it) k.push_back(it.key());
        return k;
    };
    // nlohmann::json 按字母序存键：比顺序用原文，比集合用 keys()
    auto prefix_in_order = [](const std::string& raw, const std::vector<std::string>& names) {
        size_t pos = 0;
        for (const auto& n : names) {
            size_t p = raw.find("\"" + n + "\":", pos);
            if (p == std::string::npos) return false;
            pos = p;
        }
        return true;
    };
    auto snap = t.tcp("GET", "/api/v1/snapshot", "viewtoken");
    // C# TelemetrySnapshot（顺序照抄）+ 末尾的新增
    CHECK(prefix_in_order(snap.raw, {"type", "seq", "ts", "simulated", "eventSeqApplied", "mode", "estop", "comm", "channels", "stir", "gas",
                                     "program", "programs", "leakTest", "session", "calib", "autotune", "system", "revs"}));
    for (const char* k : {"settings", "programs", "library", "runs", "files", "leak", "calib", "network", "usb", "users", "device", "pid"})
        CHECK(snap.body["revs"].contains(k));
    CHECK_EQ(snap.body["system"]["languageCode"].get<std::string>(), std::string("zh"));
    auto ch = t.tcp("GET", "/api/v1/channels/3", "viewtoken");
    const char* chan_fields[] = {"id", "installed", "state", "alarmCause", "overTempTripped", "overPressureTripped", "startedBy", "tempC", "tempSpC",
                                 "tempSpDeviceC", "heatOutputPct", "tempOnline", "autotuning", "atMode", "spMode", "approach", "profile",
                                 "pressurePsi", "pressureRaw", "pressureSpPsi", "gasConsumed", "valveOpen", "programOwned", "program", "gasRate",
                                 "solutionVolume"};
    for (const char* f : chan_fields) CHECK(ch.body["channel"].contains(f));
    CHECK(ch.body.contains("seq") && ch.body.contains("ts"));
    CHECK(ch.body["channel"]["profile"].is_object());   // /channels/{id} 带曲线
    for (const char* f : chan_fields) CHECK(snap.body["channels"][0].contains(f));
    for (const char* f : {"bandC", "antiOvershoot", "heatOn"}) CHECK(snap.body["channels"][0].contains(f));
    // SettingsDto
    auto st = t.tcp("GET", "/api/v1/settings", "viewtoken");
    CHECK(prefix_in_order(st.raw, {"limits", "alarms", "leakTest", "approach", "stir", "pressure", "tempBands", "notes", "revision", "volume"}));
    for (const char* f : {"tempSpMaxC", "pressureSpMaxPsi", "rpmMax", "volumeMaxMl"}) CHECK(st.body["limits"].contains(f));
    for (const char* f : {"overPressurePsi", "overTempC", "stopHeatOnOverPressure", "heatTimeoutMin", "pressDeviationPsi"})
        CHECK(st.body["alarms"].contains(f));
    for (const char* f : {"testPsi", "settleMin", "measureMin", "rateLimitPsiPerHr", "reminderDays", "lastTestUtc"})
        CHECK(st.body["leakTest"].contains(f));
    for (const char* f : {"deltaC", "rateCPerMin", "fastRateCPerMin", "finalZoneC", "settleBandC", "settleSec"}) CHECK(st.body["approach"].contains(f));
    for (const char* f : {"peakCurrentA", "standbyPct", "rampMsPer1000Rpm", "microstep", "stirGearRatio"}) CHECK(st.body["stir"].contains(f));
    for (const char* f : {"vesselVolume", "gasLineVolume", "manifoldVolume", "effectiveVolume", "volumeCalibratedAt"})
        CHECK(st.body["volume"].contains(f));
    CHECK_EQ(st.body["volume"]["effectiveVolume"].size(), static_cast<size_t>(8));
    CHECK(st.body["tempBands"][0].contains("loC") && st.body["tempBands"][0].contains("hiC") && st.body["tempBands"][0].contains("index"));
    for (const char* f : {"ui", "program", "storage"}) CHECK(st.body.contains(f));
    // ProgramSchemaDto
    auto ps = t.tcp("GET", "/api/v1/program-schema", "viewtoken");
    CHECK(prefix_in_order(ps.raw, {"apiVersion", "settingsRevision", "stepTypes", "steps"}));
    CHECK_EQ(ps.body["stepTypes"].size(), static_cast<size_t>(9));
    auto s0 = ps.body["steps"][0];
    for (const char* f : {"type", "label", "chip", "help", "fields"}) CHECK(s0.contains(f));
    for (const char* f : {"key", "label", "kind", "unit", "default", "min", "max", "declaredMin", "declaredMax", "options", "available", "reason",
                          "itemFields", "minItems", "maxItems"})
        CHECK(s0["fields"][0].contains(f));
    // 其他 GET 的 DTO 字段
    auto dev = t.tcp("GET", "/api/v1/device", "viewtoken");
    for (const char* f : {"model", "serial", "hmiVersion", "apiVersion", "channelCount", "channels", "installedChannels", "hardware", "units",
                          "simulated", "simRefused", "bootTime", "bootId", "clockSynced", "eventSeqHead", "seqEpoch", "io"})
        CHECK(dev.body.contains(f));
    auto comm = t.tcp("GET", "/api/v1/device/comm", "viewtoken");
    for (const char* f : {"ts", "snapshotSeq", "temp", "pressure", "stir", "io", "tick", "api", "logger", "modules"}) CHECK(comm.body.contains(f));
    auto pid = t.tcp("GET", "/api/v1/pid", "viewtoken");
    CHECK_EQ(pid.body["bands"].size(), static_cast<size_t>(3));
    CHECK_EQ(pid.body["bands"][0]["name"].get<std::string>(), std::string("低温档"));
    for (const char* f : {"index", "p", "i", "d", "tunedSpC", "tunedAt", "manualEditedAt"}) CHECK(pid.body["channels"][0]["bands"][0].contains(f));
    auto ctl = t.tcp("GET", "/api/v1/control", "viewtoken");
    CHECK(prefix_in_order(ctl.raw, {"mode", "holder", "holderToken", "state", "leaseExpiresAt", "leaseHardExpiresAt", "pending", "autoGrant",
                                    "heartbeatTimeoutSec", "leaseGraceSec", "requestTimeoutSec", "onRemoteLost"}));
    auto ses = t.tcp("GET", "/api/v1/sessions", "viewtoken");
    CHECK(ses.body.contains("count") && ses.body.contains("items") && ses.body["current"].contains("active"));
    CHECK(keys(t.tcp("GET", "/api/v1/alarms", "viewtoken").body).size() == 2);
    auto pf = t.tcp("GET", "/api/v1/preflight", "viewtoken");
    CHECK(prefix_in_order(pf.raw, {"canRun", "summary", "checks", "channels"}));
}

TEST(api_server, contract_settings_library_and_misc) {
    ApiRig t;
    auto st = t.local("GET", "/api/v1/settings");
    int64_t rev = st.body["revision"];
    // PATCH：点路径键、全有或全无、revision
    auto p = t.local("PATCH", "/api/v1/settings",
                     "{\"revision\":" + std::to_string(rev) + ",\"values\":{\"alarms.overTempC\":230,\"ui.theme\":\"dark\",\"alarms.stopHeatOnOverPressure\":false}}");
    CHECK_EQ(p.status, 200);
    if (p.status != 200) { std::printf("    PATCH：%s\n", p.raw.c_str()); return; }
    CHECK_EQ(p.body["result"]["revision"].get<int64_t>(), rev + 1);
    CHECK_EQ(p.body["result"]["changed"].size(), static_cast<size_t>(3));
    auto st2 = t.local("GET", "/api/v1/settings");
    CHECK(wait_until([&] { return t.local("GET", "/api/v1/settings").body["ui"]["theme"] == "dark"; }));
    (void)st2;
    auto conflict = t.local("PATCH", "/api/v1/settings", "{\"revision\":" + std::to_string(rev) + ",\"values\":{\"alarms.overTempC\":170}}");
    CHECK_EQ(conflict.status, 409);
    CHECK_EQ(conflict.body["error"]["code"].get<std::string>(), std::string("REVISION_CONFLICT"));
    CHECK_EQ(conflict.body["error"]["detail"]["currentRevision"].get<int64_t>(), rev + 1);
    auto unknown = t.local("PATCH", "/api/v1/settings", "{\"revision\":" + std::to_string(rev + 1) + ",\"values\":{\"nope\":1}}");
    CHECK_EQ(unknown.status, 400);
    CHECK_EQ(unknown.body["error"]["detail"]["key"].get<std::string>(), std::string("nope"));
    auto range = t.local("PATCH", "/api/v1/settings", "{\"revision\":" + std::to_string(rev + 1) + ",\"values\":{\"limits.tempSpMaxC\":9999}}");
    CHECK_EQ(range.status, 422);
    CHECK_EQ(range.body["error"]["detail"]["key"].get<std::string>(), std::string("limits.tempSpMaxC"));
    // 语言：短码
    CHECK_EQ(t.local("PUT", "/api/v1/system/language", "{\"code\":\"en\"}").status, 200);
    CHECK(wait_until([&] { return t.local("GET", "/api/v1/snapshot").body["system"]["languageCode"] == "en"; }));
    CHECK_EQ(t.local("PUT", "/api/v1/system/language", "{\"code\":\"xx\"}").status, 422);
    // 档位分界
    auto bb = t.local("PUT", "/api/v1/pid/bands", "{\"lowMidC\":90,\"midHighC\":190}");
    CHECK_EQ(bb.status, 200);
    CHECK_EQ(bb.body["result"]["bands"][1]["loC"].get<int>(), 90);
    // 到位范围 / 防超调（两边：TCP 上要租约）
    CHECK_EQ(t.local("PUT", "/api/v1/channels/2/temperature/band", "{\"bandC\":3}").status, 200);
    CHECK_EQ(t.local("PUT", "/api/v1/channels/2/temperature/band", "{\"bandC\":30}").status, 422);
    CHECK_EQ(t.local("PUT", "/api/v1/channels/2/temperature/anti-overshoot", "{\"on\":false}").status, 200);
    CHECK(wait_until([&] {
        auto s = t.local("GET", "/api/v1/snapshot");
        return s.body["channels"][1]["bandC"] == 3 && s.body["channels"][1]["antiOvershoot"] == false;
    }));
    // 手动阀控开关
    auto mv = t.local("PUT", "/api/v1/gas/manual", "{\"on\":true}");
    CHECK_EQ(mv.status, 200);
    CHECK(mv.body["result"]["manualValve"].get<bool>());
    // 全部确认
    auto ack = t.tcp("POST", "/api/v1/alarms/ack-all", "optoken", "{}");
    CHECK_EQ(ack.status, 200);
    CHECK(ack.body["result"].contains("acked"));
    // 程序库：建 / 列 / 改（revision）/ 冲突 / 删；模板重名 NAME_CONFLICT；内置模板 READ_ONLY
    std::string prog = "{\"name\":\"测试程序\",\"steps\":[{\"type\":\"stir\",\"params\":{\"rpm\":100}}],\"modified\":\"1999-01-01T00:00:00\",\"by\":\"x\"}";
    CHECK_EQ(t.tcp("POST", "/api/v1/library/programs", "optoken", prog).status, 428);
    auto c = t.tcp("POST", "/api/v1/library/programs", "optoken", prog, ApiRig::idem("lib1"));
    CHECK_EQ(c.status, 200);
    std::string pid = c.body["result"]["id"];
    CHECK_EQ(c.body["result"]["revision"].get<int>(), 1);
    auto list = t.local("GET", "/api/v1/library/programs");
    CHECK_EQ(list.body["max"].get<int>(), 50);
    CHECK_EQ(list.body["items"][0]["by"].get<std::string>(), std::string("pc1"));   // 服务端按身份填
    CHECK(list.body["items"][0]["modified"].get<std::string>() != "1999-01-01T00:00:00");
    auto u = t.local("PUT", "/api/v1/library/programs/" + pid, "{\"name\":\"改名\",\"steps\":[],\"revision\":1}");
    CHECK_EQ(u.status, 200);
    CHECK_EQ(u.body["result"]["revision"].get<int>(), 2);
    auto uc = t.local("PUT", "/api/v1/library/programs/" + pid, "{\"name\":\"再改\",\"steps\":[],\"revision\":1}");
    CHECK_EQ(uc.status, 409);
    CHECK_EQ(uc.body["error"]["detail"]["currentRevision"].get<int>(), 2);
    CHECK_EQ(t.local("PUT", "/api/v1/library/programs/" + pid, "{\"name\":\"再改\",\"steps\":[]}").status, 428);
    CHECK_EQ(t.local("DELETE", "/api/v1/library/programs/" + pid + "?revision=2").status, 200);
    CHECK_EQ(t.local("DELETE", "/api/v1/library/programs/" + pid + "?revision=2").status, 404);
    auto tpls = t.local("GET", "/api/v1/library/templates");
    CHECK(tpls.body["count"].get<int>() >= 4);
    CHECK(tpls.body["items"][0]["builtIn"].get<bool>());
    std::string bid = tpls.body["items"][0]["program"]["id"];
    std::string bname = tpls.body["items"][0]["program"]["name"];
    auto ro = t.local("DELETE", "/api/v1/library/templates/" + bid + "?revision=" + std::to_string(tpls.body["items"][0]["revision"].get<int>()));
    CHECK_EQ(ro.body["error"]["code"].get<std::string>(), std::string("READ_ONLY"));
    auto nc = t.local("POST", "/api/v1/library/templates", "{\"note\":\"\",\"program\":{\"name\":\"" + bname + "\",\"steps\":[]}}", ApiRig::idem("t1"));
    CHECK_EQ(nc.status, 409);
    CHECK_EQ(nc.body["error"]["code"].get<std::string>(), std::string("NAME_CONFLICT"));
    // 导入：没插 U 盘
    CHECK_EQ(t.local("POST", "/api/v1/library/import", "{\"source\":\"usb\"}", ApiRig::idem("imp")).body["error"]["code"].get<std::string>(),
             std::string("USB_NOT_PRESENT"));
    // 导出：没插 U 盘 / PDF
    CHECK_EQ(t.local("POST", "/api/v1/export", "{\"day\":\"20260101\"}", ApiRig::idem("ex1")).body["error"]["code"].get<std::string>(),
             std::string("USB_NOT_PRESENT"));
    CHECK_EQ(t.local("POST", "/api/v1/export", "{\"day\":\"20260101\",\"kinds\":[\"pdf\"]}", ApiRig::idem("ex2")).status, 422);
    // 网络：没有 nmcli
    auto nw = t.local("GET", "/api/v1/network");
    CHECK_EQ(nw.status, 200);
    CHECK_EQ(t.local("POST", "/api/v1/network/wifi/scan", "{}").body["error"]["code"].get<std::string>(), std::string("HW_NOT_CONFIGURED"));
    // 杂项
    CHECK_EQ(t.local("GET", "/api/v1/users").body["mode"].get<std::string>(), std::string("single"));
    CHECK(t.local("GET", "/api/v1/storage").body.contains("diskFree"));
    CHECK_EQ(t.local("GET", "/api/v1/leak-tests/current/samples").status, 404);
    CHECK_EQ(t.local("POST", "/api/v1/calib/continue", "{}").body["error"]["code"].get<std::string>(), std::string("CALIB_NOT_WAITING"));
    auto ct = t.local("POST", "/api/v1/device/comm/temp/test", "{}");
    CHECK_EQ(ct.status, 200);
    CHECK(ct.body["result"]["ok"].get<bool>());
    CHECK_EQ(t.local("POST", "/api/v1/device/comm/x/test", "{}").status, 404);
    auto al = t.local("GET", "/api/v1/alarms");
    if (al.body["count"].get<int>() > 0) CHECK(al.body["items"][0].contains("tier") && al.body["items"][0].contains("did"));
}

TEST(api_server, sessions_and_estop_alias) {
    ApiRig t;
    auto b = t.tcp("POST", "/api/v1/sessions", "optoken", "{\"testId\":\"T-9\"}", ApiRig::idem("s1"));
    CHECK_EQ(b.status, 200);
    std::string id = b.body["result"]["id"];
    CHECK_EQ(t.tcp("GET", "/api/v1/sessions/current", "viewtoken").body["id"].get<std::string>(), id);
    CHECK_EQ(t.tcp("POST", "/api/v1/sessions", "optoken2", "{\"testId\":\"T-10\"}", ApiRig::idem("s2")).status, 409);
    CHECK_EQ(t.tcp("DELETE", "/api/v1/sessions/current", "optoken2").status, 403);
    CHECK_EQ(t.tcp("POST", "/api/v1/sessions/current/markers", "optoken2", "{\"text\":\"加料\"}", ApiRig::idem("m1")).status, 200);
    CHECK_EQ(t.tcp("DELETE", "/api/v1/sessions/current", "optoken").status, 200);
    CHECK_EQ(t.tcp("GET", "/api/v1/sessions/current", "viewtoken").status, 404);
    CHECK_EQ(t.tcp("GET", "/api/v1/sessions/" + id, "viewtoken").status, 200);
    // /estop：兼容 = 全部停止（含搅拌），202，没有急停锁存
    auto e = t.tcp("POST", "/api/v1/estop", "optoken", "{\"note\":\"测试\"}");
    CHECK_EQ(e.status, 202);
    CHECK(wait_until([&] { return t.tcp("GET", "/api/v1/snapshot", "viewtoken").body["estop"]["active"] == false; }));
    CHECK_EQ(t.tcp("POST", "/api/v1/estop", "viewtoken", "{}").status, 403);
}

// 全部停止 / 单路停止：有动作的通道在线上是 state "stopped"（PC 端 StateMap 认得 → 「已停止」），没动作的不变；
// 扩展字段 hasActivity 给触屏算按钮上的路数（决定记录 2026-11-27）
TEST(api_server, stop_all_marks_only_active_channels_stopped) {
    ApiRig t;
    auto ch = [&](int i) { return t.tcp("GET", "/api/v1/snapshot", "viewtoken").body["channels"][static_cast<size_t>(i - 1)]; };
    CHECK_EQ(t.local("POST", "/api/v1/channels/1/heat/start", "{\"tempC\":60}", ApiRig::idem("h1")).status / 100, 2);
    CHECK(wait_until([&] { return ch(1)["state"] == "heating" && ch(1)["hasActivity"] == true; }));
    CHECK(ch(3)["hasActivity"] == false);
    CHECK_EQ(ch(3)["state"].get<std::string>(), std::string("ready"));
    auto r = t.local("POST", "/api/v1/stop-all", "{}", ApiRig::idem("sa1"));
    CHECK_EQ(r.status, 200);
    CHECK_EQ(r.body["result"]["channels"].size(), 1u);
    CHECK(wait_until([&] { return ch(1)["state"] == "stopped"; }));
    CHECK(ch(1)["hasActivity"] == false);
    for (int i = 2; i <= 8; ++i) CHECK_EQ(ch(i)["state"].get<std::string>(), std::string("ready"));
    // 从手动停止重新开始加热，再单路停止 → 又是 stopped；没动作的单路停止不变
    CHECK_EQ(t.local("POST", "/api/v1/channels/1/heat/start", "{\"tempC\":60}", ApiRig::idem("h2")).status / 100, 2);
    CHECK(wait_until([&] { return ch(1)["state"] == "heating"; }));
    CHECK_EQ(t.local("POST", "/api/v1/channels/1/stop", "{}", ApiRig::idem("st1")).status, 200);
    CHECK(wait_until([&] { return ch(1)["state"] == "stopped"; }));
    CHECK_EQ(t.local("POST", "/api/v1/channels/2/stop", "{}", ApiRig::idem("st2")).status, 200);
    CHECK_EQ(ch(2)["state"].get<std::string>(), std::string("ready"));
}
