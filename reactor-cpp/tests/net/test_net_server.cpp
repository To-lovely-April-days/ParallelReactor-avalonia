// 集成测试：真起服务器（127.0.0.1:0 + 临时 Unix socket），用 net_test_client.hpp 的最小客户端发真请求。
#include <stdlib.h>
#include <sys/stat.h>
#include <unistd.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <fstream>
#include <mutex>
#include <thread>

#include "common/clock.hpp"
#include "net/api_errors.hpp"
#include "net/crypto.hpp"
#include "net/idempotency.hpp"
#include "net/server.hpp"
#include "net_test_client.hpp"
#include "nlohmann/json.hpp"
#include "testing.hpp"

using namespace reactor::net;
using nettest::HttpResp;
using nlohmann::json;
using WsEv = reactor::net::ws::MessageAssembler::Event;

namespace {

void sleep_ms(int ms) { std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }

ApiToken mk(const std::string& name, const std::string& plain, const std::string& role, std::optional<bool> estop = std::nullopt) {
    ApiToken t;
    t.name = name;
    t.sha256 = sha256_hex(plain);
    t.role = role;
    t.estop = estop;
    return t;
}

std::string code_of(const HttpResp& r) {
    try { return json::parse(r.body)["error"]["code"].get<std::string>(); } catch (...) { return ""; }
}

template <class F>
bool wait_until(F f, int ms = 3000) {
    for (int i = 0; i < ms / 10; ++i) {
        if (f()) return true;
        sleep_ms(10);
    }
    return f();
}

// 一台带常用测试路由的服务器
struct Fixture {
    ApiConfig cfg;
    ServerOptions opt;
    std::unique_ptr<Server> srv;
    std::string dir, sock, file_path, file_content;
    IdempotencyStore idem;
    std::atomic<int> run_count{0};
    std::mutex mu;
    std::vector<std::string> audits;
    std::vector<std::pair<int, std::string>> closes;   // (code, reason)
    std::vector<WsSessionPtr> opened;
    int port = 0;

    explicit Fixture(std::function<void(ApiConfig&, ServerOptions&)> tweak = {}) {
        char tmpl[] = "/tmp/netsrv-XXXXXX";
        dir = mkdtemp(tmpl);
        sock = dir + "/hmi.sock";
        file_path = dir + "/data.csv";
        for (int i = 0; i < 5000; ++i) file_content += static_cast<char>('a' + i % 26);
        { std::ofstream f(file_path, std::ios::binary); f << file_content; }

        cfg.enabled = true;
        cfg.tokens = {mk("viewer", "v-tok", "viewer"), mk("op", "op-tok", "operator"), mk("op2", "op2-tok", "operator"),
                      mk("admin", "ad-tok", "admin"), mk("rl", "rl-tok", "operator"), mk("hb", "hb-tok", "operator"),
                      mk("es", "es-tok", "viewer", true), mk("pf", "pf-tok", "viewer"), mk("rd", "rd-tok", "viewer")};
        cfg.write_rate_limit_per_sec = 3;
        cfg.max_clients = 2;
        opt = server_options_from_config(cfg);
        opt.tcp_bind = "127.0.0.1";
        opt.tcp_port = 0;
        opt.tcp_bind_retry_ms = 0;
        opt.unix_path = sock;
        opt.worker_threads = 4;
        opt.ws_keepalive_ms = 0;
        opt.audit = [this](const std::string& l, const std::string& t, const std::string& d) {
            std::lock_guard<std::mutex> lk(mu);
            audits.push_back(l + "|" + t + "|" + d);
        };
        if (tweak) tweak(cfg, opt);
        srv = std::make_unique<Server>(opt, cfg);
        routes();
        std::string err;
        bool ok = srv->start(&err);
        CHECK(ok);
        if (!ok) std::printf("  start error: %s\n", err.c_str());
        port = srv->tcp_port();
    }
    ~Fixture() {
        srv->stop();
        srv.reset();
        unlink(file_path.c_str());
        unlink(sock.c_str());
        rmdir(dir.c_str());
    }

    void routes() {
        Router& r = srv->router();
        r.get("/api/v1/health", [](const Request&) { return Response::json(200, json{{"ok", true}}); },
              RouteOptions{RateClass::None, true, false});
        r.get("/api/v1/echo", [](const Request& q) {
            return Response::json(200, json{{"name", q.auth.name}, {"role", role_name(q.auth.role)}, {"local", q.auth.local},
                                            {"uid", q.peer.uid}, {"pid", q.peer.pid}, {"ip", q.peer.ip_text},
                                            {"q", q.query_param("q")}, {"path", q.path}});
        });
        r.post("/api/v1/echo", [](const Request& q) { return Response::text(200, q.body); });
        r.put("/api/v1/channels/{id}/valve", [](const Request& q) { return Response::json(200, json{{"id", q.param("id")}}); });
        r.post("/api/v1/estop", [](const Request&) { return Response::json(202, json{{"ok", true}}); }, RouteOptions{RateClass::Estop});
        r.post("/api/v1/control/heartbeat", [](const Request&) { return Response::json(200, json{{"ok", true}}); },
               RouteOptions{RateClass::Heartbeat});
        r.get("/api/v1/preflight", [](const Request&) { return Response::json(200, json{{"ok", true}}); },
              RouteOptions{RateClass::Preflight});
        r.get("/api/v1/files/data", [this](const Request& q) { return file_response(q, file_path, "text/csv; charset=utf-8", "data.csv"); },
              RouteOptions{RateClass::Download});
        r.get("/api/v1/slow-download", [](const Request&) { sleep_ms(400); return Response::text(200, "slow"); },
              RouteOptions{RateClass::Download});
        // 延迟响应：处理器立即返回，另一个线程 100 ms 后回复
        r.add("GET", "/api/v1/deferred", [](const Request&, Responder res) {
            std::thread([res] { sleep_ms(100); res.send(Response::json(200, json{{"late", true}})); }).detach();
        });
        r.add("GET", "/api/v1/forget", [](const Request&, Responder) {});
        r.get("/api/v1/throw", [](const Request&) -> Response { throw std::runtime_error("boom"); });
        // 流式 CSV（chunked，背压）
        r.add("GET", "/api/v1/telemetry", [](const Request&, Responder res) {
            Headers h;
            h.set("Content-Type", "text/csv; charset=utf-8");
            auto w = res.stream(200, h);
            if (!w) return;
            for (int i = 0; i < 20000; ++i) {
                std::string line = "2026-11-26 08:00:00," + std::to_string(i) + ",123.45,67.89,0,0,0,0,0,0,0,0\n";
                if (!w->write(line)) return;
            }
            w->end();
        }, RouteOptions{RateClass::Download});
        r.add("GET", "/api/v1/fixed-stream", [](const Request&, Responder res) {
            auto w = res.stream(200, Headers{}, 10);
            w->write("01234");
            w->write("56789");
            w->end();
        });
        // 幂等写命令（照 C# CommandAsync 第 3 步）
        r.post("/api/v1/run", [this](const Request& q) {
            std::string key = q.header(ApiHeaderIdem());
            if (key.empty()) return Response::error(codes::kPreconditionRequired, "POST /api/v1/run 非幂等 · 必须带 Idempotency-Key（§7.1）");
            if (key.size() > kIdemKeyMaxLen) return Response::error(codes::kBadRequest, "Idempotency-Key 过长（≤ 128 字符）");
            auto a = idem.acquire(IdempotencyStore::scoped_key(q.auth.name, key), IdempotencyStore::hash(q.method, q.path, q.body),
                                  key, reactor::mono_ms());
            if (a.kind == IdemBegin::Mismatch)
                return Response::error(codes::kIdempotencyMismatch, "同一个 Idempotency-Key 带了不同的请求（方法 / 路径 / 正文）");
            if (a.kind == IdemBegin::InProgress)
                return Response::error(codes::kIdempotencyInProgress, "同键命令仍在执行 · 请稍后用同一 Idempotency-Key 查询");
            if (a.kind == IdemBegin::Replay) {
                Response r2 = Response::json(a.entry->status, a.entry->body);
                r2.headers.set("Idempotent-Replay", "true");
                return r2;
            }
            if (q.body.find("slow") != std::string::npos) sleep_ms(300);
            std::string body = json{{"ok", true}, {"opId", key}, {"n", ++run_count}}.dump();
            idem.complete(a.entry, 202, body);
            return Response::json(202, body);
        });
        r.patch("/api/v1/settings", [](const Request&) { return Response::json(200, json{{"ok", true}}); },
                RouteOptions{RateClass::Auto, false, true});
        WsHandlers h;
        h.on_open = [this](const WsSessionPtr& s, const Request&) {
            s->send(json{{"type", "hello"}, {"client", s->auth().name}, {"local", s->auth().local}}.dump());
            std::lock_guard<std::mutex> lk(mu);
            opened.push_back(s);
        };
        h.on_message = [](const WsSessionPtr& s, const std::string& m, bool bin) {
            if (bin) { s->send_binary(m); return; }
            if (m == "flood") {   // 大消息灌满事件队列
                std::string big(64 * 1024, 'e');
                for (int i = 0; i < 400 && s->send(big); ++i) {}
                return;
            }
            if (m == "tele") {
                std::string big(64 * 1024, 't');
                for (int i = 0; i < 100; ++i) s->send(big, WsQueue::Telemetry);
                return;
            }
            if (m == "huge") { s->send(std::string(16 * 1024 * 1024, 'h')); return; }
            if (m == "bye") { s->close(1000, "再见"); return; }
            s->send("echo:" + m);
        };
        h.on_close = [this](const WsSessionPtr&, uint16_t code, const std::string& reason) {
            std::lock_guard<std::mutex> lk(mu);
            closes.emplace_back(code, reason);
        };
        r.ws("/api/v1/ws", h);
    }
    static std::string ApiHeaderIdem() { return "Idempotency-Key"; }
    size_t close_count() { std::lock_guard<std::mutex> lk(mu); return closes.size(); }
    std::pair<int, std::string> last_close() { std::lock_guard<std::mutex> lk(mu); return closes.empty() ? std::make_pair(0, std::string()) : closes.back(); }
    bool audit_has(const std::string& s) {
        std::lock_guard<std::mutex> lk(mu);
        for (auto& a : audits)
            if (a.find(s) != std::string::npos) return true;
        return false;
    }
};

}  // namespace

// ———————— 鉴权 / 网关 ————————

TEST(net_server, auth_basics) {
    Fixture f;
    auto h = nettest::request(f.port, "GET", "/api/v1/health");
    CHECK_EQ(h.status, 200);
    auto r = nettest::request(f.port, "GET", "/api/v1/echo");
    CHECK_EQ(r.status, 401);
    CHECK_EQ(code_of(r), std::string("UNAUTHORIZED"));
    CHECK_EQ(r.h("www-authenticate"), std::string("Bearer"));
    CHECK_EQ(r.h("content-type"), std::string("application/json; charset=utf-8"));
    CHECK_EQ(nettest::request(f.port, "GET", "/api/v1/echo", "wrong").status, 401);
    auto at = nettest::request(f.port, "GET", "/api/v1/echo?access_token=op-tok", "op-tok");
    CHECK_EQ(at.status, 401);
    CHECK(json::parse(at.body)["error"]["message"].get<std::string>().find("access_token") != std::string::npos);
    auto ok = nettest::request(f.port, "GET", "/api/v1/echo?q=%E6%B8%A9%E5%BA%A6", "op-tok");
    CHECK_EQ(ok.status, 200);
    auto j = json::parse(ok.body);
    CHECK(j["name"] == "op");
    CHECK(j["role"] == "operator");
    CHECK(j["local"] == false);
    CHECK(j["q"] == "温度");
    CHECK(j["ip"] == "127.0.0.1");
    CHECK(f.audit_has("接口鉴权失败"));
    CHECK(f.srv->stats().auth_failures.load() >= 2);
}

TEST(net_server, prefix_404_405_params) {
    Fixture f;
    auto p = nettest::request(f.port, "GET", "/other", "op-tok");
    CHECK_EQ(p.status, 404);
    CHECK(p.body.find("接口前缀为 /api/v1") != std::string::npos);
    auto n = nettest::request(f.port, "GET", "/api/v1/nothing", "op-tok");
    CHECK_EQ(n.status, 404);
    CHECK(n.body.find("没有这个端点") != std::string::npos);
    CHECK_EQ(nettest::request(f.port, "GET", "/api/v1/nothing").status, 401);   // 未知路径也先鉴权
    auto m = nettest::request(f.port, "DELETE", "/api/v1/echo", "op-tok");
    CHECK_EQ(m.status, 405);
    CHECK_EQ(m.h("allow"), std::string("GET, HEAD, POST"));
    auto v = nettest::request(f.port, "PUT", "/api/v1/channels/7/valve", "op-tok", "{\"open\":true}");
    CHECK_EQ(v.status, 200);
    CHECK(json::parse(v.body)["id"] == "7");
    // local_only 路由在 TCP 上 = 404
    CHECK_EQ(nettest::request(f.port, "PATCH", "/api/v1/settings", "ad-tok", "{}").status, 404);
}

TEST(net_server, method_mismatch_as_404_option) {
    Fixture f([](ApiConfig&, ServerOptions& o) { o.method_mismatch_as_404 = true; });
    auto m = nettest::request(f.port, "DELETE", "/api/v1/echo", "op-tok");
    CHECK_EQ(m.status, 404);
    CHECK(m.body.find("没有这个端点") != std::string::npos);
}

TEST(net_server, cidr_rejects_and_closes) {
    Fixture f([](ApiConfig& c, ServerOptions&) { c.allowed_cidrs = {"10.0.0.0/8", "bogus"}; });
    auto r = nettest::request(f.port, "GET", "/api/v1/health");
    CHECK_EQ(r.status, 403);
    CHECK_EQ(code_of(r), std::string("IP_NOT_ALLOWED"));
    CHECK(r.body.find("127.0.0.1 不在允许的网段内") != std::string::npos);
    CHECK(r.closed);
    CHECK(f.audit_has("接口白名单含无效条目"));
    CHECK(f.audit_has("bogus"));
}

TEST(net_server, ipv6_dual_stack_loopback) {
    Fixture f([](ApiConfig&, ServerOptions& o) { o.tcp_bind = "::"; });
    int fd6 = nettest::connect_tcp(f.port, "::1");
    if (fd6 < 0) { std::printf("  (本机无 IPv6，跳过)\n"); return; }
    nettest::send_all(fd6, nettest::build("GET", "/api/v1/echo", "op-tok"));
    std::string buf;
    auto r = nettest::read_response(fd6, buf);
    ::close(fd6);
    CHECK_EQ(r.status, 200);
    CHECK(json::parse(r.body)["ip"] == "::1");
    auto r4 = nettest::request(f.port, "GET", "/api/v1/echo", "op-tok");   // IPv4 经双栈 = ::ffff:127.0.0.1 → 按 IPv4 回环
    CHECK_EQ(r4.status, 200);
    CHECK(json::parse(r4.body)["ip"] == "127.0.0.1");
}

TEST(net_server, auth_failure_limiter) {
    Fixture f;
    int fd = nettest::connect_tcp(f.port);
    std::string buf;
    int n429 = 0, n401 = 0;
    for (int i = 0; i < 12; ++i) {
        nettest::send_all(fd, nettest::build("GET", "/api/v1/echo", "bad"));
        auto r = nettest::read_response(fd, buf);
        if (r.status == 401) ++n401;
        if (r.status == 429) { ++n429; CHECK_EQ(r.h("retry-after"), std::string("1")); }
    }
    ::close(fd);
    CHECK_EQ(n401, 10);
    CHECK_EQ(n429, 2);
    CHECK_EQ(nettest::request(f.port, "GET", "/api/v1/echo", "op-tok").status, 200);   // 有效令牌不受影响
}

// ———————— 限流 ————————

namespace {
// 在同一连接上连发 n 个请求，返回 429 的个数
int burst(int port, int n, const std::string& method, const std::string& path, const std::string& token, std::string* retry = nullptr) {
    int fd = nettest::connect_tcp(port);
    std::string buf;
    int limited = 0;
    for (int i = 0; i < n; ++i) {
        nettest::send_all(fd, nettest::build(method, path, token, method == "GET" ? "" : "{}"));
        auto r = nettest::read_response(fd, buf);
        if (r.status == 429) {
            ++limited;
            if (retry) *retry = r.h("retry-after");
        }
    }
    ::close(fd);
    return limited;
}
}  // namespace

TEST(net_server, rate_limit_partitions) {
    Fixture f;
    std::string ra;
    CHECK_EQ(burst(f.port, 6, "POST", "/api/v1/estop", "es-tok", &ra), 1);          // 5/s
    CHECK_EQ(ra, std::string("1"));
    CHECK_EQ(burst(f.port, 12, "POST", "/api/v1/control/heartbeat", "hb-tok"), 2);  // 10/s
    CHECK_EQ(burst(f.port, 3, "GET", "/api/v1/preflight", "pf-tok"), 1);            // 2/s
    CHECK_EQ(burst(f.port, 5, "POST", "/api/v1/echo", "rl-tok"), 2);                // writeRateLimitPerSec = 3
    CHECK_EQ(burst(f.port, 55, "GET", "/api/v1/echo", "rd-tok"), 5);                // 读 50/s
    // /health 不限
    CHECK_EQ(burst(f.port, 60, "GET", "/api/v1/health", ""), 0);
    // 分区按令牌：别的令牌不受影响
    CHECK_EQ(nettest::request(f.port, "POST", "/api/v1/echo", "op-tok", "x").status, 200);
}

TEST(net_server, download_concurrency) {
    Fixture f;
    std::atomic<int> ok{0}, busy{0};
    std::vector<std::thread> ts;
    for (int i = 0; i < 3; ++i)
        ts.emplace_back([&, i] {
            sleep_ms(i * 30);
            auto r = nettest::request(f.port, "GET", "/api/v1/slow-download", i == 2 ? "op2-tok" : "op-tok");
            if (r.status == 200) ++ok;
            if (r.status == 429 && code_of(r) == "DOWNLOAD_BUSY") ++busy;
        });
    for (auto& t : ts) t.join();
    CHECK_EQ(ok.load(), 2);
    CHECK_EQ(busy.load(), 1);
    CHECK_EQ(nettest::request(f.port, "GET", "/api/v1/slow-download", "op-tok").status, 200);   // 许可已归还
}

// ———————— HTTP 细节 ————————

TEST(net_server, keepalive_pipelining_chunked_body_limits) {
    Fixture f;
    int fd = nettest::connect_tcp(f.port);
    std::string two = nettest::build("GET", "/api/v1/echo?q=1", "op-tok") + nettest::build("GET", "/api/v1/echo?q=2", "op-tok");
    nettest::send_all(fd, two);
    std::string buf;
    auto a = nettest::read_response(fd, buf);
    auto b = nettest::read_response(fd, buf);
    CHECK(json::parse(a.body)["q"] == "1");
    CHECK(json::parse(b.body)["q"] == "2");
    // chunked 请求正文
    nettest::send_all(fd, "POST /api/v1/echo HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer op-tok\r\nTransfer-Encoding: chunked\r\n\r\n"
                          "3\r\nabc\r\n4\r\ndefg\r\n0\r\n\r\n");
    auto c = nettest::read_response(fd, buf);
    CHECK_EQ(c.body, std::string("abcdefg"));
    // Expect: 100-continue
    nettest::send_all(fd, "POST /api/v1/echo HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer op-tok\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\n");
    auto cont = nettest::read_response(fd, buf);
    CHECK_EQ(cont.status, 100);
    nettest::send_all(fd, "hi");
    auto d = nettest::read_response(fd, buf);
    CHECK_EQ(d.body, std::string("hi"));
    ::close(fd);
    // 正文超 64 KB → 413 并断开
    auto big = nettest::request(f.port, "POST", "/api/v1/echo", "op-tok", std::string(70 * 1024, 'x'));
    CHECK_EQ(big.status, 413);
    CHECK(big.closed);
    // Connection: close
    auto cl = nettest::request(f.port, "GET", "/api/v1/echo", "op-tok", "", "Connection: close\r\n");
    CHECK(cl.closed);
    CHECK_EQ(cl.h("connection"), std::string("close"));
    // 垃圾请求行 → 400 并断开
    int g = nettest::connect_tcp(f.port);
    nettest::send_all(g, "HELLO\r\n\r\n");
    std::string gb;
    auto gr = nettest::read_response(g, gb);
    ::close(g);
    CHECK_EQ(gr.status, 400);
}

TEST(net_server, deferred_forget_throw) {
    Fixture f;
    auto d = nettest::request(f.port, "GET", "/api/v1/deferred", "op-tok");
    CHECK_EQ(d.status, 200);
    CHECK(json::parse(d.body)["late"] == true);
    auto fg = nettest::request(f.port, "GET", "/api/v1/forget", "op-tok");
    CHECK_EQ(fg.status, 500);
    CHECK(fg.body.find("处理器未返回响应") != std::string::npos);
    auto th = nettest::request(f.port, "GET", "/api/v1/throw", "op-tok");
    CHECK_EQ(th.status, 500);
    CHECK(th.body.find("boom") != std::string::npos);
}

TEST(net_server, file_range_head_etag) {
    Fixture f;
    auto full = nettest::request(f.port, "GET", "/api/v1/files/data", "op-tok");
    CHECK_EQ(full.status, 200);
    CHECK(full.body == f.file_content);
    std::string etag = full.h("etag");
    CHECK(etag.rfind("W/\"", 0) == 0);
    auto part = nettest::request(f.port, "GET", "/api/v1/files/data", "op-tok", "", "Range: bytes=100-199\r\n");
    CHECK_EQ(part.status, 206);
    CHECK(part.body == f.file_content.substr(100, 100));
    CHECK_EQ(part.h("content-range"), std::string("bytes 100-199/5000"));
    auto head = nettest::request(f.port, "HEAD", "/api/v1/files/data", "op-tok");
    CHECK_EQ(head.status, 200);
    CHECK_EQ(head.h("content-length"), std::string("5000"));
    CHECK(head.body.empty());
    auto nm = nettest::request(f.port, "GET", "/api/v1/files/data", "op-tok", "", "If-None-Match: " + etag + "\r\n");
    CHECK_EQ(nm.status, 304);
    auto bad = nettest::request(f.port, "GET", "/api/v1/files/data", "op-tok", "", "Range: bytes=9000-\r\n");
    CHECK_EQ(bad.status, 416);
}

TEST(net_server, streaming_chunked_csv) {
    Fixture f;
    auto r = nettest::request(f.port, "GET", "/api/v1/telemetry", "op-tok");
    CHECK_EQ(r.status, 200);
    CHECK_EQ(r.h("transfer-encoding"), std::string("chunked"));
    std::string expect;
    for (int i = 0; i < 20000; ++i) expect += "2026-11-26 08:00:00," + std::to_string(i) + ",123.45,67.89,0,0,0,0,0,0,0,0\n";
    CHECK_EQ(r.body.size(), expect.size());
    CHECK(r.body == expect);
    auto fx = nettest::request(f.port, "GET", "/api/v1/fixed-stream", "op-tok");
    CHECK_EQ(fx.h("content-length"), std::string("10"));
    CHECK_EQ(fx.body, std::string("0123456789"));
}

TEST(net_server, idempotency_flow) {
    Fixture f;
    std::string key = "Idempotency-Key: k-123\r\n";
    auto a = nettest::request(f.port, "POST", "/api/v1/run", "op-tok", "{\"x\":1}", key);
    CHECK_EQ(a.status, 202);
    CHECK(json::parse(a.body)["n"] == 1);
    CHECK(a.h("idempotent-replay").empty());
    auto b = nettest::request(f.port, "POST", "/api/v1/run", "op-tok", "{\"x\":1}", key);
    CHECK_EQ(b.status, 202);
    CHECK_EQ(b.body, a.body);
    CHECK_EQ(b.h("idempotent-replay"), std::string("true"));
    CHECK_EQ(f.run_count.load(), 1);
    auto c = nettest::request(f.port, "POST", "/api/v1/run", "op-tok", "{\"x\":2}", key);
    CHECK_EQ(c.status, 409);
    CHECK_EQ(code_of(c), std::string("IDEMPOTENCY_MISMATCH"));
    auto d = nettest::request(f.port, "POST", "/api/v1/run", "op2-tok", "{\"x\":1}", key);   // 另一令牌：独立
    CHECK_EQ(d.status, 202);
    CHECK(json::parse(d.body)["n"] == 2);
    CHECK_EQ(nettest::request(f.port, "POST", "/api/v1/run", "op2-tok", "{}").status, 428);   // op-tok 已用满 3 次/秒
    CHECK_EQ(nettest::request(f.port, "POST", "/api/v1/run", "op2-tok", "{}", "Idempotency-Key: " + std::string(129, 'k') + "\r\n").status, 400);
    // 同键并发：第二个等第一个完成后重放
    HttpResp s1, s2;
    std::thread t1([&] { s1 = nettest::request(f.port, "POST", "/api/v1/run", "ad-tok", "{\"slow\":1}", "Idempotency-Key: c\r\n"); });
    sleep_ms(80);
    s2 = nettest::request(f.port, "POST", "/api/v1/run", "ad-tok", "{\"slow\":1}", "Idempotency-Key: c\r\n");
    t1.join();
    CHECK_EQ(s1.status, 202);
    CHECK_EQ(s2.status, 202);
    CHECK_EQ(s1.body, s2.body);
    CHECK_EQ(s2.h("idempotent-replay"), std::string("true"));
}

// ———————— Unix socket ————————

TEST(net_server, unix_socket_local_identity) {
    Fixture f;
    struct stat st{};
    CHECK(stat(f.sock.c_str(), &st) == 0 && S_ISSOCK(st.st_mode));
    CHECK_EQ(static_cast<int>(st.st_mode & 0777), 0660);
    int fd = nettest::connect_unix(f.sock);
    CHECK(fd >= 0);
    nettest::send_all(fd, nettest::build("GET", "/api/v1/echo", ""));
    std::string buf;
    auto r = nettest::read_response(fd, buf);
    CHECK_EQ(r.status, 200);
    auto j = json::parse(r.body);
    CHECK(j["local"] == true);
    CHECK(j["name"] == "local");
    CHECK(j["role"] == "admin");
    CHECK(j["uid"] == static_cast<int64_t>(getuid()));
    CHECK(j["pid"] == static_cast<int64_t>(getpid()));
    nettest::send_all(fd, nettest::build("PATCH", "/api/v1/settings", "", "{}"));
    auto p = nettest::read_response(fd, buf);
    CHECK_EQ(p.status, 200);   // 只限本地的路由在 Unix 上可用
    ::close(fd);
}

TEST(net_server, unix_uid_allowlist_and_stale_socket) {
    // 残留的 socket 文件（没人监听）：启动时删掉重建
    char tmpl[] = "/tmp/netstale-XXXXXX";
    std::string d = mkdtemp(tmpl);
    std::string path = d + "/hmi.sock";
    {
        ServerOptions o;
        o.tcp_enabled = false;
        o.unix_path = path;
        o.gate.unix_allowed_uids = {static_cast<int64_t>(getuid()) + 12345};
        ApiConfig c;
        Server s(o, c);
        s.router().get("/api/v1/echo", [](const Request&) { return Response::text(200, "x"); });
        CHECK(s.start());
        int fd = nettest::connect_unix(path);
        nettest::send_all(fd, nettest::build("GET", "/api/v1/echo", ""));
        std::string buf;
        auto r = nettest::read_response(fd, buf);
        ::close(fd);
        CHECK_EQ(r.status, 403);
        CHECK(r.body.find("不允许连接") != std::string::npos);
        // 第二个实例：已有人监听 → 失败
        Server s2(o, c);
        std::string err;
        CHECK(!s2.start(&err));
        CHECK(err.find("已有进程在监听") != std::string::npos);
        s.stop();
    }
    struct stat st{};
    CHECK(stat(path.c_str(), &st) != 0);   // stop 时删除
    // 造一个残留 socket 文件
    {
        int fd = ::socket(AF_UNIX, SOCK_STREAM, 0);
        sockaddr_un a{};
        a.sun_family = AF_UNIX;
        std::strncpy(a.sun_path, path.c_str(), sizeof(a.sun_path) - 1);
        CHECK(::bind(fd, reinterpret_cast<sockaddr*>(&a), sizeof a) == 0);
        ::close(fd);   // 文件留下，没人监听
    }
    {
        ServerOptions o;
        o.tcp_enabled = false;
        o.unix_path = path;
        ApiConfig c;
        Server s(o, c);
        std::string err;
        CHECK(s.start(&err));
    }
    // 不是 socket 的同名文件：不删、启动失败
    { std::ofstream f(path); f << "x"; }
    {
        ServerOptions o;
        o.tcp_enabled = false;
        o.unix_path = path;
        ApiConfig c;
        Server s(o, c);
        std::string err;
        CHECK(!s.start(&err));
    }
    unlink(path.c_str());
    rmdir(d.c_str());
}

// ———————— WebSocket ————————

TEST(net_server, ws_handshake_messages_close) {
    Fixture f;
    nettest::WsClient c;
    CHECK(c.open(f.port, "/api/v1/ws", "op-tok"));
    CHECK_EQ(c.handshake.h("sec-websocket-accept"), std::string("s3pPLMBiTxaQ9kYGzzhZRbK+xOo="));
    std::string m;
    CHECK(c.next_text(m));
    auto hello = json::parse(m);
    CHECK(hello["type"] == "hello");   // hello 一定是第一条
    CHECK(hello["client"] == "op");
    c.send_text("abc");
    CHECK(c.next_text(m));
    CHECK_EQ(m, std::string("echo:abc"));
    // 分片 + 中间夹 ping
    c.send_frame(ws::kText, "frag", false);
    c.send_frame(ws::kPing, "pp");
    c.send_frame(ws::kContinuation, "ment", true);
    WsEv e;
    CHECK(c.next(e, 3000, false));
    CHECK(e.kind == WsEv::Pong && e.data == "pp");
    CHECK(c.next_text(m));
    CHECK_EQ(m, std::string("echo:fragment"));
    // 二进制
    c.send_frame(ws::kBinary, std::string("\x00\x01\x02", 3));
    CHECK(c.next(e));
    CHECK(e.kind == WsEv::Message && e.binary && e.data.size() == 3);
    // 中文 UTF-8
    c.send_text("温度");
    CHECK(c.next_text(m));
    CHECK_EQ(m, std::string("echo:温度"));
    CHECK_EQ(f.srv->stats().clients.load(), 1);
    // 客户端发起关闭
    const uint8_t mask[4] = {1, 2, 3, 4};
    nettest::send_all(c.fd, ws::encode_close(1000, "bye", mask));
    CHECK_EQ(c.wait_close(), 1000);
    CHECK(wait_until([&] { return f.close_count() == 1; }));
    CHECK_EQ(f.last_close().first, 1000);
    CHECK_EQ(f.last_close().second, std::string("客户端关闭"));
    CHECK(wait_until([&] { return f.srv->stats().clients.load() == 0; }));
    CHECK(f.audit_has("接口客户端已连接"));
    CHECK(f.audit_has("接口客户端已断开"));
}

TEST(net_server, ws_server_close_and_errors) {
    Fixture f;
    {
        nettest::WsClient c;
        CHECK(c.open(f.port, "/api/v1/ws", "op-tok"));
        std::string m;
        c.next_text(m);
        c.send_text("bye");
        CHECK_EQ(c.wait_close(), 1000);
        const uint8_t mask[4] = {1, 2, 3, 4};
        nettest::send_all(c.fd, ws::encode_close(1000, "", mask));
        CHECK(wait_until([&] { return f.close_count() == 1; }));
        CHECK_EQ(f.last_close().second, std::string("再见"));
    }
    {
        // 入站超过 64 KB → 1009
        nettest::WsClient c;
        CHECK(c.open(f.port, "/api/v1/ws", "op-tok"));
        std::string m;
        c.next_text(m);
        c.send_text(std::string(65 * 1024, 'x'));
        CHECK_EQ(c.wait_close(), 1009);
        CHECK(wait_until([&] { return f.close_count() == 2; }));
        CHECK_EQ(f.last_close().second, std::string("消息过大"));
    }
    {
        // 未加掩码 → 1002
        nettest::WsClient c;
        CHECK(c.open(f.port, "/api/v1/ws", "op-tok"));
        std::string m;
        c.next_text(m);
        c.send_frame(ws::kText, "x", true, false);
        CHECK_EQ(c.wait_close(), 1002);
    }
    // 不带令牌 → 401；非升级请求 → 400；版本不对 → 400
    {
        nettest::WsClient c;
        CHECK(!c.open(f.port, "/api/v1/ws", ""));
        CHECK_EQ(c.status, 401);
    }
    auto plain = nettest::request(f.port, "GET", "/api/v1/ws", "op-tok");
    CHECK_EQ(plain.status, 400);
    CHECK(plain.body.find("此路径只接受 WebSocket 升级请求") != std::string::npos);
    auto badv = nettest::request(f.port, "GET", "/api/v1/ws", "op-tok", "",
                                 "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 8\r\n");
    CHECK_EQ(badv.status, 400);
    CHECK_EQ(badv.h("sec-websocket-version"), std::string("13"));
}

TEST(net_server, ws_max_clients_and_broadcast) {
    Fixture f;   // maxClients = 2
    nettest::WsClient a, b, c;
    CHECK(a.open(f.port, "/api/v1/ws", "op-tok"));
    CHECK(b.open(f.port, "/api/v1/ws", "v-tok"));
    CHECK(!c.open(f.port, "/api/v1/ws", "op-tok"));
    CHECK_EQ(c.status, 503);
    CHECK_EQ(code_of(c.handshake), std::string("TOO_MANY_CLIENTS"));
    std::string m;
    CHECK(a.next_text(m));
    CHECK(b.next_text(m));
    CHECK(wait_until([&] { return f.srv->ws_broadcast("{\"type\":\"probe\"}", WsQueue::Event) == 2; }));
    CHECK(a.next_text(m));
    CHECK_EQ(m, std::string("{\"type\":\"probe\"}"));
    CHECK(b.next_text(m));
    // 过滤：只发给 viewer
    size_t n = f.srv->ws_broadcast("only-viewer", WsQueue::Telemetry, [](const WsSession& s) { return s.auth().name == "viewer"; });
    CHECK_EQ(n, size_t(1));
    CHECK(b.next_text(m));
    CHECK_EQ(m, std::string("only-viewer"));
    WsEv e;
    CHECK(!a.next(e, 200));
    // Unix 入口单独计数：PC 占满时触屏照样能连
    nettest::WsClient u;
    CHECK(u.open_fd(nettest::connect_unix(f.sock), "/api/v1/ws", ""));
    CHECK(u.next_text(m));
    CHECK(json::parse(m)["local"] == true);
    // 关掉一个后名额释放
    a.close_fd();
    CHECK(wait_until([&] {
        nettest::WsClient d;
        return d.open(f.port, "/api/v1/ws", "op-tok");
    }));
}

TEST(net_server, ws_event_overflow_closes_1008) {
    Fixture f([](ApiConfig&, ServerOptions& o) { o.ws_event_queue = 20; });
    nettest::WsClient c;
    CHECK(c.open(f.port, "/api/v1/ws", "op-tok"));
    std::string m;
    c.next_text(m);
    c.send_text("flood");   // 400 × 64 KB，客户端先不读
    sleep_ms(500);
    CHECK_EQ(c.wait_close(5000), 1008);
    CHECK(wait_until([&] { return f.close_count() == 1; }));
    CHECK_EQ(f.last_close().second, std::string("事件队列溢出（慢客户端）"));
}

TEST(net_server, ws_telemetry_drops_oldest) {
    Fixture f([](ApiConfig&, ServerOptions& o) { o.ws_telemetry_queue = 5; });
    nettest::WsClient c;
    CHECK(c.open(f.port, "/api/v1/ws", "op-tok"));
    std::string m;
    c.next_text(m);
    c.send_text("tele");   // 100 × 64 KB 遥测，客户端先不读
    WsSessionPtr s;
    CHECK(wait_until([&] { std::lock_guard<std::mutex> lk(f.mu); if (!f.opened.empty()) s = f.opened.back(); return s != nullptr; }));
    CHECK(wait_until([&] { return s->dropped_telemetry() > 0; }));
    CHECK(f.srv->stats().dropped_telemetry_frames.load() > 0);
    CHECK(s->is_open());   // 遥测丢帧不断开
    int got = 0;
    while (c.next_text(m, 500)) ++got;
    CHECK(got > 0 && got < 100);
    CHECK(s->is_open());
}

TEST(net_server, ws_send_timeout) {
    Fixture f([](ApiConfig&, ServerOptions& o) { o.ws_send_timeout_ms = 500; });
    nettest::WsClient c;
    CHECK(c.open(f.port, "/api/v1/ws", "op-tok"));
    std::string m;
    c.next_text(m);
    c.send_text("huge");   // 16 MB 单条消息，客户端不读 → 发不完
    CHECK(wait_until([&] { return f.close_count() == 1; }, 5000));
    CHECK(f.last_close().second.find("发送超时") != std::string::npos);
    CHECK_EQ(f.last_close().first, 1008);
}

TEST(net_server, ws_token_revocation) {
    Fixture f;
    nettest::WsClient c;
    CHECK(c.open(f.port, "/api/v1/ws", "op-tok"));
    std::string m;
    c.next_text(m);
    ApiConfig cfg2 = f.cfg;
    cfg2.tokens.erase(std::remove_if(cfg2.tokens.begin(), cfg2.tokens.end(), [](const ApiToken& t) { return t.name == "op"; }),
                      cfg2.tokens.end());
    f.srv->update_config(cfg2);
    CHECK_EQ(c.wait_close(), 1008);
    CHECK_EQ(nettest::request(f.port, "GET", "/api/v1/echo", "op-tok").status, 401);
    CHECK_EQ(nettest::request(f.port, "GET", "/api/v1/echo", "op2-tok").status, 200);
}

TEST(net_server, stop_closes_ws_and_calls_on_close) {
    auto f = std::make_unique<Fixture>();
    nettest::WsClient c;
    CHECK(c.open(f->port, "/api/v1/ws", "op-tok"));
    std::string m;
    c.next_text(m);
    f->srv->stop();
    CHECK_EQ(f->close_count(), size_t(1));
    CHECK_EQ(f->last_close().first, 1001);
    CHECK_EQ(c.wait_close(), 1001);
    f.reset();
}
