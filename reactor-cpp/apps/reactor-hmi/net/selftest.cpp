#include "selftest.hpp"

#include "backend.hpp"
#include "fake_server.hpp"
#include "wire.hpp"
#include "ws_client.hpp"

#include <atomic>
#include <climits>
#include <cstdint>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <thread>
#include <unistd.h>

namespace reactor::hmi::model {
extern const char* const kSampleProgramSchema;
}

namespace reactor::hmi::net {

namespace {
int g_pass = 0, g_fail = 0;
void check(bool ok, const std::string& what) {
    if (ok) ++g_pass;
    else {
        ++g_fail;
        std::printf("  FAIL  %s\n", what.c_str());
    }
}
std::string hex(const std::array<uint8_t, 20>& h) {
    static const char* H = "0123456789abcdef";
    std::string s;
    for (auto b : h) { s.push_back(H[b >> 4]); s.push_back(H[b & 15]); }
    return s;
}
void sleep_ms(int ms) { std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }

// ------------------------------------------------------------ 单元
void unit_crypto() {
    check(hex(sha1("abc")) == "a9993e364706816aba3e25717850c26c9cd0d89d", "sha1(abc)");
    check(hex(sha1("")) == "da39a3ee5e6b4b0d3255bfef95601890afd80709", "sha1(空)");
    check(hex(sha1(std::string(1000000, 'a'))) == "34aa973cd4c4daa4f61eeb2bdbad27316534016f", "sha1(100 万个 a)");
    check(base64("") == "" && base64("f") == "Zg==" && base64("fo") == "Zm8=" && base64("foobar") == "Zm9vYmFy", "base64 RFC 4648");
    check(ws_accept("dGhlIHNhbXBsZSBub25jZQ==") == "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", "WS accept key（RFC 6455 示例）");
    std::string u = uuid4();
    check(u.size() == 36 && u[14] == '4' && u != uuid4(), "uuid4");
    check(parse_iso_ms("1970-01-01T00:00:01Z").value_or(-1) == 1000, "ISO 1970");
    check(parse_iso_ms("2026-11-06T08:12:30.1234567Z").value_or(0) == 1793952750123LL, "ISO 7 位小数");
    check(parse_iso_ms("2026-11-06T16:12:30+08:00").value_or(0) == 1793952750000LL, "ISO 带时区");
    check(iso_ms(1793952750120LL) == "2026-11-06T08:12:30.120Z", "ISO 输出");
    check(!parse_iso_ms("hello"), "ISO 不认识");
    check(url_encode("Lab 2.4G/中") == "Lab%202.4G%2F%E4%B8%AD", "url_encode");
}

void unit_http() {
    {   // chunked，一个字节一个字节喂；带扩展和尾部头
        std::string r = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5;x=y\r\nhello\r\nA\r\n 012345678\r\n0\r\nX-T: 1\r\n\r\n";
        HttpParser p;
        p.reset();
        int st = 0;
        size_t used = 0;
        for (size_t i = 0; i < r.size() && st == 0; ++i) st = p.feed(&r[i], 1, &used);
        check(st == 1 && p.msg().body == "hello 012345678" && p.msg().status == 200, "HTTP chunked 逐字节");
    }
    {   // 100 Continue 之后才是真正的响应；后面还有一条（流水线）
        std::string r = "HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 201 Created\r\nContent-Length: 2\r\n\r\nokHTTP/1.1 204 No Content\r\n\r\n";
        HttpParser p;
        p.reset();
        size_t used = 0;
        int st = p.feed(r.data(), r.size(), &used);
        check(st == 1 && p.msg().status == 201 && p.msg().body == "ok", "HTTP 100 Continue");
        std::string rest = r.substr(used);
        p.reset();
        st = p.feed(rest.data(), rest.size(), &used);
        check(st == 1 && p.msg().status == 204 && p.msg().body.empty(), "HTTP 流水线第二条 / 204 无正文");
    }
    {   // 没有长度：读到断开为止
        std::string r = "HTTP/1.0 200 OK\r\nContent-Type: application/json\r\n\r\n{\"a\":1}";
        HttpParser p;
        p.reset();
        size_t used = 0;
        int st = p.feed(r.data(), r.size(), &used);
        check(st == 0 && p.finish_eof() == 1 && p.msg().body == "{\"a\":1}" && !p.msg().keep_alive, "HTTP 读到断开为止");
    }
    {   // 定长没收完就断开 = 错
        std::string r = "HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nabc";
        HttpParser p;
        p.reset();
        size_t used = 0;
        p.feed(r.data(), r.size(), &used);
        check(p.finish_eof() == -1, "HTTP 正文不完整");
    }
    {
        HttpParser p;
        p.reset();
        size_t used = 0;
        check(p.feed("garbage\r\n", 9, &used) == -1, "HTTP 垃圾响应");
    }
    Endpoint e;
    check(parse_endpoint("tcp://127.0.0.1:8080", e) && !e.is_unix && e.port == 8080, "地址 tcp://");
    check(parse_endpoint("unix:/run/reactord/hmi.sock", e) && e.is_unix && e.path == "/run/reactord/hmi.sock", "地址 unix:");
    check(!parse_endpoint("ftp://x", e), "地址不认识");
}

void unit_ws() {
    // 服务端 → 客户端：不加掩码；分片中间夹 Ping
    std::string text(70000, 'x');
    std::string f = ws_frame(WsOp::Text, text.substr(0, 100), false, false) + ws_frame(WsOp::Ping, "p", true, false) +
                    ws_frame(WsOp::Cont, text.substr(100, 300), false, false) + ws_frame(WsOp::Cont, text.substr(400), true, false) +
                    ws_frame(WsOp::Text, "中文", true, false) + ws_frame(WsOp::Close, ws_close_payload(1001, "bye"), true, false);
    WsDecoder d(false);
    bool ok = true;
    for (size_t i = 0; i < f.size(); i += 997) ok &= d.feed(f.data() + i, std::min<size_t>(997, f.size() - i));
    WsMsg m1, m2, m3, m4;
    check(ok && d.next(m1) && m1.op == WsOp::Ping && m1.data == "p", "WS 分片中间的 Ping 先出来");
    check(d.next(m2) && m2.op == WsOp::Text && m2.data == text, "WS 分片拼成 70000 字节（127 长度）");
    check(d.next(m3) && m3.data == "中文", "WS 中文");
    check(d.next(m4) && m4.op == WsOp::Close && m4.close_code == 1001 && m4.data == "bye", "WS 关闭码");
    // 客户端发的帧必须加掩码（假服务器按这个解）
    WsDecoder ds(true);
    std::string cf = ws_frame(WsOp::Text, std::string(300, 'y'), true, true);
    WsMsg m5;
    check(ds.feed(cf.data(), cf.size()) && ds.next(m5) && m5.data == std::string(300, 'y'), "WS 掩码帧（126 长度）");
    WsDecoder dm(false);
    check(!dm.feed(cf.data(), cf.size()), "WS 服务端发掩码帧 = 协议错误");
    WsDecoder dc(false);
    std::string bad = ws_frame(WsOp::Ping, std::string(200, 'z'), true, false);
    check(!dc.feed(bad.data(), bad.size()), "WS 控制帧超过 125 字节 = 协议错误");
    WsDecoder dx(false);
    std::string cont = ws_frame(WsOp::Cont, "a", true, false);
    check(!dx.feed(cont.data(), cont.size()), "WS 多出来的续帧 = 协议错误");
}

// ------------------------------------------------------------ 集成：假服务器
json telemetry_json(int64_t seq) {
    json chs = json::array();
    for (int i = 1; i <= 8; ++i) {
        json c = {{"id", i}, {"installed", i <= 6}, {"state", i <= 6 ? (i == 1 ? "heating" : "ready") : "idle"}, {"alarmCause", nullptr},
                  {"overTempTripped", false}, {"overPressureTripped", false}, {"tempC", i == 1 ? 78.5 : 25.0}, {"tempSpC", 80},
                  {"tempOnline", true}, {"autotuning", false}, {"spMode", "fixed"},
                  {"approach", {{"phase", "off"}, {"targetC", nullptr}, {"currentSpC", nullptr}, {"statusText", ""}}},
                  {"profile", nullptr}, {"pressurePsi", 290.1}, {"pressureSpPsi", 290.1}, {"gasConsumed", 1.25}, {"valveOpen", false},
                  {"programOwned", i == 1}, {"gasRate", 0.01}, {"solutionVolume", 5.0},
                  {"bandC", i == 1 ? 1.5 : 2.0}, {"antiOvershoot", i != 1}, {"heatOn", i == 1}};
        if (i == 1)
            c["program"] = {{"running", true}, {"stepIndex", 4}, {"stepCount", 9}, {"state", "running"}, {"message", ""},
                            {"startedBy", "触屏"}, {"startedAt", "2026-11-26T05:00:00Z"}, {"waitingGas", false}};
        chs.push_back(c);
    }
    return {{"type", "telemetry"}, {"seq", seq}, {"ts", "2026-11-26T06:00:01.200Z"}, {"simulated", true}, {"eventSeqApplied", 3},
            {"mode", {{"control", "local"}, {"holder", nullptr}}},
            {"estop", {{"active", false}, {"heat", ""}, {"stir", ""}, {"valves", ""}, {"latched", false}, {"restoredOnBoot", false}}},
            {"comm", {{"temp", true}, {"pressure", true}, {"stir", true}, {"io", true}}},
            {"channels", chs},
            {"stir", {{"on", true}, {"rpmSp", 400}, {"online", true}, {"locked", false}, {"rampMsPer1000Rpm", 1000}}},
            {"gas", {{"inlets", {{{"id", "inert"}, {"open", false}}, {{"id", "a"}, {"open", false}}}}, {"ventOpen", false}, {"manualValve", false}}},
            {"program", {{"running", false}, {"status", ""}, {"stepIndex", 0}, {"stepCount", 0}, {"phase", ""}, {"detail", ""}}},
            {"programs", {{"running", 1}, {"channels", {1}}, {"gasHolder", nullptr}, {"waitingGas", json::array()}, {"summary", ""}}},
            {"leakTest", {{"running", false}, {"progress", 0}, {"done", false}, {"failed", false}}},
            {"session", {{"active", false}}},
            {"system", {{"languageCode", "zh"}, {"bootAt", "2026-11-26T00:00:00Z"}}},
            {"revs", {{"settings", 1}, {"library", 1}, {"runs", 1}}}};
}

const char* kOk = R"({"ok":true,"opId":"op","status":"confirmed","result":{},"warnings":[],"opEventSeq":1})";

struct ServerState {
    std::atomic<int64_t> settingsRev{7};
    std::atomic<bool> conflictOnce{true};
};

FakeResp handle(const FakeReq& q, ServerState& st) {
    std::string p = q.path();
    auto is = [&](const char* m, const char* path) { return q.method == m && p == path; };
    if (is("GET", "/api/v1/settings")) {
        json s = {{"limits", {{"tempSpMaxC", 200}, {"pressureSpMaxPsi", 507.6}, {"rpmMax", 450}, {"volumeMaxMl", 8}}},
                  {"alarms", {{"overPressurePsi", 377.1}, {"overTempC", 200}, {"stopHeatOnOverPressure", true}, {"heatTimeoutMin", 20}, {"pressDeviationPsi", 8}}},
                  {"leakTest", {{"testPsi", 50}, {"settleMin", 10}, {"measureMin", 30}, {"rateLimitPsiPerHr", 2}, {"reminderDays", 7}, {"lastTestUtc", nullptr}}},
                  {"pressure", {{"fullScaleMPa", 4.0}}}, {"revision", st.settingsRev.load()},
                  {"ui", {{"theme", "dark"}, {"preflightChecklist", false}}}, {"program", {{"gasWaitMaxMin", 45}}}, {"storage", {{"retentionDays", 120}}}};
        return {200, s.dump()};
    }
    if (is("PATCH", "/api/v1/settings")) {
        json b = json::parse(q.body, nullptr, false);
        if (st.conflictOnce.exchange(false)) {
            st.settingsRev = st.settingsRev + 1;
            return {409, R"({"ok":false,"opId":"x","error":{"code":"REVISION_CONFLICT","message":"设置已被改过，请刷新后再改","detail":{"currentRevision":8}},"opEventSeq":0})"};
        }
        if (j_int(b, "revision") != st.settingsRev) return {409, R"({"ok":false,"error":{"code":"REVISION_CONFLICT","message":"x"}})"};
        st.settingsRev = st.settingsRev + 1;
        return {200, json{{"ok", true}, {"opId", "o"}, {"status", "confirmed"}, {"result", {{"revision", st.settingsRev.load()}}}, {"warnings", json::array()}, {"opEventSeq", 2}}.dump()};
    }
    if (is("GET", "/api/v1/program-schema")) return {200, model::kSampleProgramSchema, FakeResp::Chunked};
    if (is("GET", "/api/v1/alarms")) return {200, R"({"count":0,"items":[]})"};
    if (is("GET", "/api/v1/snapshot")) return {200, telemetry_json(99).dump()};
    if (is("GET", "/api/v1/events")) {
        json items = json::array();
        if (q.target.find("afterSeq=10&") != std::string::npos) {
            for (int s = 11; s <= 12; ++s) items.push_back({{"seq", s}, {"seqEpoch", 1}, {"ts", "2026-11-26T06:00:00Z"}, {"level", "info"}, {"kind", "x"}, {"title", "补发"}, {"message", ""}});
        } else {
            for (int s = 1; s <= 3; ++s) items.push_back({{"seq", s}, {"seqEpoch", 1}, {"ts", "2026-11-26T05:00:00Z"}, {"level", "info"}, {"kind", "x"}, {"title", "旧"}, {"message", ""}});
        }
        return {200, json{{"seqEpoch", 1}, {"eventSeqHead", 20}, {"afterSeq", 0}, {"count", items.size()}, {"more", false}, {"items", items}}.dump()};
    }
    if (is("GET", "/api/v1/library/programs")) {
        json e = {{"id", "P1"}, {"name", "加氢筛选"}, {"model", "PR8"}, {"modified", "2026-11-14T16:20:00"}, {"by", "触屏"}, {"draft", false}, {"revision", 3},
                  {"steps", {{{"type", "leak"}, {"name", "气密检查"}, {"params", {{"pres", 3.0}, {"maxDrop", 0.0138}, {"onFail", "stop"}}}}, {{"type", "end"}, {"params", json::object()}}}}};
        return {200, json{{"count", 1}, {"max", 50}, {"items", {e}}}.dump(), FakeResp::Chunked};
    }
    if (is("GET", "/api/v1/runs"))
        return {200, R"({"count":1,"items":[{"id":"R1","programName":"加氢筛选","result":"done","resultText":"4.20 mmol","start":"2026-11-25T05:00:00Z","end":"2026-11-25T09:00:00Z","channels":[1,2],"pressPsi":290.1,"abort":{"ch":2,"quantity":"pressure","value":377.1,"setpoint":290.1,"limit":377.1,"rangeHi":580.2,"at":"2026-11-25T08:00:00Z","title":"超压","did":""}}]})"};
    if (is("GET", "/api/v1/runs/R1"))
        return {200, R"({"id":"R1","programName":"加氢筛选","result":"done","start":"2026-11-25T05:00:00Z","channels":[1,2],"stepMinutes":[5,10],"trend":{"t0":"2026-11-25T05:00:00Z","stepMs":60000,"count":2,"channels":[{"id":1,"gasMmol":[0,0.5]}]},"program":{"name":"加氢筛选","steps":[{"type":"end","params":{}}]}})"};
    if (is("GET", "/api/v1/trend"))
        return {200, R"({"t0":"2026-11-26T05:59:40Z","stepMs":10000,"count":3,"channels":[{"id":1,"tempC":[77,null,78.5],"pressurePsi":[290.1,290.1,290.1],"gasMmol":[1,1.1,1.25]}]})", FakeResp::Close};
    if (is("GET", "/api/v1/files")) {
        bool tel = q.target.find("telemetry") != std::string::npos;
        return {200, json{{"type", tel ? "telemetry" : "events"}, {"count", 1},
                          {"items", {{{"name", tel ? "telemetry-20261125.csv" : "events-20261125.jsonl"}, {"bytes", tel ? 1000 : 50}, {"modifiedAt", "2026-11-25T10:00:00Z"},
                                      {"from", "2026-11-25T01:00:00Z"}, {"to", "2026-11-25T10:00:00Z"}, {"day", "20261125"}, {"desc", "每 1.2 s 一行"}, {"lines", tel ? 0 : 12}}}}}.dump()};
    }
    if (is("GET", "/api/v1/chunked")) return {200, std::string(5000, 'c'), FakeResp::Chunked};
    if (is("GET", "/api/v1/close")) return {200, "{\"closed\":true}", FakeResp::Close};
    if (is("PUT", "/api/v1/channels/2/valve"))
        return {409, R"({"ok":false,"opId":"o","error":{"code":"GAS_BUSY","message":"气路正被「泄漏测试」占用","detail":{"gasTask":"泄漏测试"}},"opEventSeq":5})"};
    if (is("PUT", "/api/v1/channels/3/temperature/setpoint"))
        return {202, R"({"ok":true,"opId":"o","status":"accepted","result":{"applied":false},"warnings":["加热仍在按 147.0 °C 运行"],"opEventSeq":6})"};
    if (q.method != "GET") return {200, kOk};
    return {404, R"({"ok":false,"error":{"code":"NOT_FOUND","message":"没有这个端点"}})"};
}

std::shared_ptr<const model::Model> g_cur;
std::shared_ptr<const model::Model> latest(model::ModelMailbox& mb) {
    if (auto m = mb.take()) g_cur = m;
    return g_cur;
}
bool wait_model(model::ModelMailbox& mb, const std::function<bool(const model::Model&)>& pred, int ms) {
    int64_t end = mono_ms() + ms;
    for (;;) {
        auto m = latest(mb);
        if (m && pred(*m)) return true;
        if (mono_ms() > end) return false;
        sleep_ms(10);
    }
}
const FakeReq* find_req(const std::vector<FakeReq>& log, const std::string& method, const std::string& target_prefix) {
    for (auto& r : log)
        if (r.method == method && r.target.rfind(target_prefix, 0) == 0) return &r;
    return nullptr;
}
json last_subscribe(const FakeServer& s) {
    json last;
    for (auto& t : s.ws_log()) {
        json j = json::parse(t, nullptr, false);
        if (j.is_object() && j_str(j, "type") == "subscribe") last = j;
    }
    return last;
}
int count_subscribe(const FakeServer& s) {
    int n = 0;
    for (auto& t : s.ws_log()) n += t.find("\"subscribe\"") != std::string::npos;
    return n;
}

void integ_http(const std::string& path) {
    ServerState st;
    FakeServer srv(path);
    check(srv.start([&](const FakeReq& q) { return handle(q, st); }), "假服务器起来");
    Endpoint ep;
    parse_endpoint("unix:" + path, ep);
    HttpConn c(ep, "tok");
    HttpRequest r;
    HttpResponse res;
    std::string err;
    r.target = "/api/v1/chunked";
    check(c.exec(r, res, &err) && res.body == std::string(5000, 'c'), "HttpConn chunked 5000 字节");
    int conns = srv.http_connects();
    r.target = "/api/v1/alarms";
    check(c.exec(r, res, &err) && res.status == 200 && srv.http_connects() == conns, "HttpConn keep-alive 复用同一条连接");
    auto lg = srv.log();
    check(!lg.empty() && lg.back().header("Authorization") && *lg.back().header("Authorization") == "Bearer tok", "带 Bearer 令牌");
    r.target = "/api/v1/close";
    check(c.exec(r, res, &err) && res.body == "{\"closed\":true}", "HttpConn 读到断开为止");
    r.target = "/api/v1/alarms";
    check(c.exec(r, res, &err) && res.status == 200 && srv.http_connects() == conns + 1, "对端关了之后自动重连");
    // 服务端把空闲连接关掉：下一条在收到字节前失败 → 重连重发一次
    srv.stop();
    FakeServer srv2(path);
    srv2.start([&](const FakeReq& q) { return handle(q, st); });
    check(c.exec(r, res, &err) && res.status == 200, "服务端重启后旧 keep-alive 连接失效 → 重发成功");
    r.method = "POST";
    r.target = "/api/v1/stop-all";
    r.body = "{}";
    check(c.exec(r, res, &err) && res.status == 200, "POST 正文");
    auto l2 = srv2.log();
    check(!l2.empty() && l2.back().body == "{}" && l2.back().header("Content-Length") && *l2.back().header("Content-Length") == "2", "Content-Length 对");
    srv2.stop();
    HttpConn dead(ep, "");
    r.method = "GET";
    check(!dead.exec(r, res, &err) && !err.empty(), "连不上时返回错误：" + err);
}

void integ_backend(const std::string& path) {
    ServerState st;
    auto srv = std::make_unique<FakeServer>(path);
    srv->start([&](const FakeReq& q) { return handle(q, st); });
    Endpoint ep;
    parse_endpoint("unix:" + path, ep);
    model::ModelMailbox mb;
    UiQueue ui;
    g_cur.reset();
    NetBackend b(ep, "", mb, ui);
    std::vector<std::pair<bool, std::string>> results;
    b.actions().on_result = [&](bool ok, const std::string& msg) { results.push_back({ok, msg}); };
    b.start();
    check(wait_model(mb, [](const model::Model& m) { return !m.connected && m.snap.channels.size() == 8; }, 1000), "开始时 connected=false，8 路占位");
    check(srv->wait_for([&] { return count_subscribe(*srv) >= 1; }, 3000), "连上后发 subscribe");
    json sub0 = last_subscribe(*srv);
    bool has_progress = false;
    for (auto& t : sub0["topics"]) has_progress |= t == "progress";
    check(has_progress && !sub0.contains("resumeAfterEventSeq"), "第一次订阅：含 progress，不带 resume");
    // 遥测分 3 片发，中间夹一个 Ping
    srv->push(telemetry_json(5).dump(), 3, true);
    check(wait_model(mb, [](const model::Model& m) { return m.connected && m.ch(1).tempC && *m.ch(1).tempC == 78.5; }, 2000), "遥测进 Model（分片 + Ping）");
    check(srv->wait_for([&] { return srv->pongs >= 1; }, 2000), "服务端的 Ping 收到 Pong");
    auto m = latest(mb);
    check(m->chanset[0].band == 1.5 && !m->chanset[0].antiOvershoot && m->chanset[0].heatOn, "通道扩展 bandC / antiOvershoot / heatOn");
    check(!m->ch(7).installed && m->ch(2).installed, "idle 的路在界面上算停用");
    check(m->bootAt == parse_iso_ms("2026-11-26T00:00:00Z").value(), "system.bootAt");
    // progress
    json pg = {{"type", "progress"}, {"ts", "2026-11-26T06:00:01Z"},
               {"channels", {{{"id", 1}, {"programName", "加氢筛选-A"}, {"steps", {{{"type", "leak"}, {"name", "气密检查"}}, {{"type", "heat"}, {"name", "升温"}}}},
                              {"stepIndex", 1}, {"stepTarget", {{"quantity", "temp"}, {"value", 80}}}, {"stepFraction", 0.5}, {"elapsedSec", 600},
                              {"remainingSec", 1200}, {"gasQueueRank", nullptr}, {"waitingFor", json::array()},
                              {"plan", {{"t0", "2026-11-26T05:00:00Z"}, {"segs", {{{"kind", "heat"}, {"name", "升温"}, {"startSec", 0}, {"endSec", 1800}}}},
                                        {"temp", {{0, 25}, {1800, 80}}}, {"press", {{0, 0}, {1800, 290.0754755}}}}}}}},
               {"autotune", {{{"id", 3}, {"cycle", 2}, {"cycles", 3}, {"leftSec", 300}, {"fraction", 0.4}}}}};
    srv->push(pg.dump());
    check(wait_model(mb, [](const model::Model& mm) { return mm.progress[0].programName == "加氢筛选-A"; }, 2000), "progress 进 Model");
    m = latest(mb);
    check(m->progress[0].stepTarget == "80 °C" && m->progress[0].stepNames.size() == 2 && m->progress[0].remainingSec.value_or(0) == 1200, "progress 步骤 / 目标 / 约剩");
    check(m->plans[0].pressPlan.size() == 2 && std::fabs(m->plans[0].pressPlan[1].v - 2.0) < 1e-6, "计划压力 psi → MPa");
    check(m->x.pid.channels[2].tuneCycle == 2 && std::fabs(m->x.pid.channels[2].tuneFraction - 0.4) < 1e-9, "自整定进度");
    // 报警（一级，压力量值换成 MPa）
    srv->push(R"({"type":"alarm","alarm":{"id":"A1","level":"err","rv":"","channel":7,"title":"7 号超压","message":"已关阀","raisedAt":"2026-11-26T06:00:00Z","state":"active","clearedAt":null,"tier":1,"source":"channel","quantity":"pressure","value":377.1,"limit":377.1,"did":["关闭进气阀"],"next":["等压力降下来"]}})");
    check(wait_model(mb, [](const model::Model& mm) { return !mm.alarms.empty(); }, 2000), "报警进 Model");
    m = latest(mb);
    check(m->alarms[0].ext.tier == 1 && std::fabs(*m->alarms[0].ext.value - 2.6) < 0.001 && m->alarms[0].ext.did.size() == 1, "报警 tier / 量值 MPa / 已经做了");
    srv->push(R"({"type":"event","seq":10,"seqEpoch":1,"ts":"2026-11-26T06:00:02Z","tsLocal":"14:00:02","level":"info","kind":"heat.started","channel":1,"source":"local","origin":"hmi","client":null,"opId":null,"title":"1 号开始加热","message":""})");
    check(wait_model(mb, [](const model::Model& mm) { return !mm.events.empty() && mm.events.front().seq == 10; }, 2000), "事件进 Model（新的在前）");
    // REST 数据
    check(wait_model(mb, [](const model::Model& mm) { return mm.settings.revision == 7 && !mm.x.schema.empty() && !mm.x.library.empty(); }, 4000),
          "设置 / schema（chunked）/ 程序库取回来");
    m = latest(mb);
    check(m->x.prefs.theme == "dark" && !m->x.prefs.preflightChecklist && m->x.prefs.gasWaitMaxMin == 45 && m->x.prefs.retentionDays == 120,
          "设置扩展 ui / program / storage");
    check(!m->x.library.empty() && std::fabs(m->x.library[0].prog.steps[0].num("pres") - 435.1) < 0.01 &&
              std::fabs(m->x.library[0].prog.steps[0].num("maxDrop") - 2.0) < 0.01 && m->x.library[0].prog.revision == 3,
          "程序库 MPa → psi（压力 1 位、压降 2 位）、revision");
    check(m->events.size() >= 4, "开机先取最近的事件（GET /events），和实时的合并去重");
    check(wait_model(mb, [](const model::Model& mm) { return mm.x.trend && mm.x.trend->size() == 3; }, 4000), "趋势（读到断开为止的响应）");
    m = latest(mb);
    check(std::isnan(m->x.trend->temp[0][1]) && std::fabs(m->x.trend->press[0][0] - 2.0) < 0.001, "趋势 null → NaN、压力 MPa");
    check(wait_model(mb, [](const model::Model& mm) { return !mm.x.runs.empty() && mm.x.runs[0].trend && !mm.x.days.empty(); }, 4000), "运行记录 + 详情 + 每日文件");
    m = latest(mb);
    check(m->x.runs[0].abort && std::fabs(m->x.runs[0].abort->value - 2.6) < 0.001 && m->x.runs[0].stepMinutes.size() == 2, "运行记录的中止信息（MPa）、每步用时");
    check(m->x.days.size() == 1 && m->x.days[0].files.size() == 2 && m->x.days[0].runs.size() == 1 && m->x.days[0].bytes == 1050, "每日文件按天分组");
    // ---- 动作 → 端点 ----
    srv->clear_log();
    auto& A = b.actions();
    A.stop_all(true);
    A.set_temp_sp(3, 85.5);
    A.patch_setting("alarms.overTempC", 180);
    A.patch_setting("ui.preflightChecklist", 0);
    A.patch_setting("ui.retentionDays", 30);
    model::ProgramDoc d;
    d.name = "新程序";
    model::StepDoc s1;
    s1.type = "leak";
    s1.set_num("pres", 435.1);
    s1.set_num("maxDrop", 2.0);
    d.steps.push_back(s1);
    A.lib_save(false, d);
    A.set_valve(2, true);
    A.lib_delete(true, "U1", 3);
    A.wifi_forget("Lab 2.4G");
    A.abort_program(4);
    A.copy_to_all(1);
    model::ExportReq ex;
    ex.day = "20261125";
    ex.kinds = {"telemetry"};
    A.export_usb(ex);
    A.ack_all();
    A.put_program(5, d);
    A.leak_start({2, 3});
    check(srv->wait_for([&] { return find_req(srv->log(), "POST", "/api/v1/leak-tests") != nullptr; }, 4000), "动作都发出去了");
    sleep_ms(600);  // 冲突重发走 read 队列再回 cmd 队列，等它们走完
    auto lg = srv->log();
    auto* r1 = find_req(lg, "POST", "/api/v1/stop-all");
    check(r1 && json::parse(r1->body)["stopStir"] == true && r1->header("Idempotency-Key") && r1->header("Idempotency-Key")->size() == 36,
          "全部停止 → POST /stop-all {stopStir} + Idempotency-Key");
    auto* r2 = find_req(lg, "PUT", "/api/v1/channels/3/temperature/setpoint");
    check(r2 && json::parse(r2->body)["tempC"] == 85.5, "设定温度 → PUT …/temperature/setpoint");
    int over_n = 0;
    bool saw_retry = false, saw_bool = false, saw_ret = false;
    for (auto& r : lg) {
        if (r.method != "PATCH") continue;
        json b2 = json::parse(r.body);
        if (b2["values"].contains("alarms.overTempC")) {
            ++over_n;
            if (b2["revision"].get<int64_t>() >= 8) saw_retry = true;
        }
        if (b2["values"].contains("ui.preflightChecklist") && b2["values"]["ui.preflightChecklist"] == false) saw_bool = true;
        if (b2["values"].contains("storage.retentionDays") && b2["values"]["storage.retentionDays"] == 30) saw_ret = true;
    }
    check(over_n == 2, "PATCH /settings：REVISION_CONFLICT 后取最新 revision 重发一次（只重发一次）");
    check(saw_retry, "重发用的是新的 revision（≥ 8）");
    check(st.settingsRev >= 11, "三项设置最后都改成功了（revision 7 → 冲突 8 → 三次成功 11）");
    check(saw_bool && saw_ret, "开关类设置发 JSON 布尔；ui.retentionDays → storage.retentionDays");
    auto* r3 = find_req(lg, "POST", "/api/v1/library/programs");
    check(r3 && std::fabs(json::parse(r3->body)["steps"][0]["params"]["pres"].get<double>() - 3.0) < 1e-3 &&
              std::fabs(json::parse(r3->body)["steps"][0]["params"]["maxDrop"].get<double>() - 0.01379) < 1e-4,
          "存程序库 → POST /library/programs，压力换成 MPa");
    check(find_req(lg, "DELETE", "/api/v1/library/templates/U1?revision=3") != nullptr, "删模板 → DELETE …?revision=3");
    check(find_req(lg, "DELETE", "/api/v1/network/wifi/saved/Lab%202.4G") != nullptr, "忘记 WiFi → SSID 做了 URL 编码");
    check(find_req(lg, "DELETE", "/api/v1/channels/4/program/run") != nullptr, "中止程序 → DELETE …/program/run");
    auto* r4 = find_req(lg, "POST", "/api/v1/channels/copy-config");
    check(r4 && json::parse(r4->body)["from"] == 1 && json::parse(r4->body)["to"].size() == 5, "复制到其余启用的路（跳过停用 / 程序占用）");
    auto* r5 = find_req(lg, "POST", "/api/v1/export");
    check(r5 && json::parse(r5->body)["day"] == "20261125", "导出 → POST /export");
    check(find_req(lg, "POST", "/api/v1/alarms/ack-all") != nullptr, "全部确认 → POST /alarms/ack-all");
    auto* r6 = find_req(lg, "PUT", "/api/v1/channels/5/program");
    check(r6 && json::parse(r6->body)["steps"][0]["params"]["pres"] == 435.1 && json::parse(r6->body)["name"] == "新程序", "下发程序 → PUT …/program（psi）");
    auto* r7 = find_req(lg, "POST", "/api/v1/leak-tests");
    check(r7 && json::parse(r7->body)["channels"].size() == 2, "泄漏测试 → POST /leak-tests {channels}");
    // 结果经 UiQueue 回到「LVGL 线程」
    sleep_ms(200);
    ui.drain();
    bool err_ok = false, warn_ok = false;
    for (auto& r : results) {
        if (!r.first && r.second == "气路正被「泄漏测试」占用") err_ok = true;
        if (r.first && r.second == "加热仍在按 147.0 °C 运行") warn_ok = true;
    }
    check(err_ok, "失败照抄服务端的中文 message");
    check(warn_ok, "成功但有 warnings → 提示");
    // ---- 断线 / 重连 / 重订阅 ----
    int subs = count_subscribe(*srv);
    srv->kill_ws();
    check(wait_model(mb, [](const model::Model& mm) { return !mm.connected; }, 2000), "WS 断开 → connected=false（界面盖层）");
    check(srv->wait_for([&] { return count_subscribe(*srv) > subs; }, 4000), "自动重连并重新订阅");
    json sub1 = last_subscribe(*srv);
    check(sub1.value("resumeAfterEventSeq", 0) == 10 && sub1.value("seqEpoch", 0) == 1, "重订阅带 resumeAfterEventSeq=10、seqEpoch");
    srv->push(telemetry_json(6).dump());
    check(wait_model(mb, [](const model::Model& mm) { return mm.connected; }, 2000), "重连后第一帧遥测 → connected=true");
    // gap → GET /events?afterSeq=10 → GET /snapshot → 重新订阅（resume 12）
    srv->clear_log();
    subs = count_subscribe(*srv);
    srv->push(R"({"type":"gap","reason":"gap_too_large","eventSeqHead":20,"oldestBufferedSeq":15})");
    check(srv->wait_for([&] { return count_subscribe(*srv) > subs; }, 3000), "gap 之后重新订阅");
    lg = srv->log();
    check(find_req(lg, "GET", "/api/v1/events?afterSeq=10&") != nullptr && find_req(lg, "GET", "/api/v1/snapshot") != nullptr,
          "gap → GET /events?afterSeq=10 + GET /snapshot");
    check(last_subscribe(*srv).value("resumeAfterEventSeq", 0) == 12, "补完以后从 12 接着订阅");
    check(wait_model(mb, [](const model::Model& mm) { return !mm.events.empty() && mm.events.front().seq == 12; }, 2000), "补发的事件进 Model");
    // 服务端发了坏帧（加了掩码）→ 按协议错误断开、重连
    int wc = srv->ws_connects();
    srv->push_raw(ws_frame(WsOp::Text, "{}", true, true));
    check(srv->wait_for([&] { return srv->ws_connects() > wc; }, 4000), "坏帧 → 断开重连");
    // 后台整个停掉 → 一直重试；再起来 → 连上
    srv->stop();
    srv.reset();
    check(wait_model(mb, [](const model::Model& mm) { return !mm.connected && !mm.linkStatus.empty(); }, 2000), "后台停了：connected=false，盖层有说明");
    check(wait_model(mb, [](const model::Model& mm) { return mm.linkStatus.find("连不上") != std::string::npos; }, 4000),
          "重试连不上时盖层写出原因");
    m = latest(mb);
    std::printf("  （盖层说明：%s）\n", m->linkStatus.c_str());
    srv = std::make_unique<FakeServer>(path);
    srv->start([&](const FakeReq& q) { return handle(q, st); });
    check(srv->wait_for([&] { return count_subscribe(*srv) >= 1; }, 8000), "后台重新起来以后自动连上");
    srv->push(telemetry_json(7).dump());
    check(wait_model(mb, [](const model::Model& mm) { return mm.connected; }, 2000), "恢复 connected=true");
    b.stop();
    srv->stop();
}
}  // namespace

int fake_serve_main(const std::string& path, int seconds) {
    ServerState st;
    st.conflictOnce = false;
    FakeServer srv(path);
    if (!srv.start([&](const FakeReq& q) { return handle(q, st); })) {
        std::fprintf(stderr, "起不来：%s\n", path.c_str());
        return 1;
    }
    std::printf("假后台在 unix:%s（Ctrl-C 停）\n", path.c_str());
    std::fflush(stdout);
    int64_t end = seconds > 0 ? mono_ms() + seconds * 1000LL : INT64_MAX;
    for (int64_t seq = 1; mono_ms() < end; ++seq) {
        json t = telemetry_json(seq);
        t["ts"] = iso_ms(wall_ms());
        srv.push(t.dump());
        srv.push(R"({"type":"progress","channels":[{"id":1,"programName":"加氢筛选-A","steps":[{"type":"leak","name":"气密检查"},{"type":"exchange","name":"置换"},{"type":"stir","name":"搅拌"},{"type":"heat","name":"升温"},{"type":"heat","name":"升温"},{"type":"press","name":"充压"},{"type":"react","name":"反应"},{"type":"vent","name":"放空"},{"type":"end","name":"结束"}],"stepIndex":4,"stepTarget":{"quantity":"temp","value":80},"stepFraction":0.6,"elapsedSec":3600,"remainingSec":7200}]})");
        sleep_ms(1200);
    }
    srv.stop();
    return 0;
}

int selftest_main() {
    std::printf("reactor-hmi 网络层自检\n");
    unit_crypto();
    unit_http();
    unit_ws();
    std::string path = "/tmp/rhmi-selftest-" + std::to_string(::getpid()) + ".sock";
    integ_http(path);
    integ_backend(path);
    std::printf("%s：通过 %d，失败 %d\n", g_fail ? "有失败" : "全部通过", g_pass, g_fail);
    return g_fail ? 1 : 0;
}

}  // namespace reactor::hmi::net
