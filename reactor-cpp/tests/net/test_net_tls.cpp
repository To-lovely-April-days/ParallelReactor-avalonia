// PC 入口加密（HTTPS / WSS）：证书仓库 + 服务器 TLS 层。客户端用 tls_test_client.hpp（mbedTLS，不验 CA，比对指纹 = PC 端的做法）。
#include <stdlib.h>
#include <sys/stat.h>
#include <unistd.h>

#include <chrono>
#include <cstdio>
#include <fstream>
#include <memory>
#include <string>
#include <thread>

#include "net/server.hpp"
#include "net/tls.hpp"
#include "net_test_client.hpp"
#include "nlohmann/json.hpp"
#include "testing.hpp"
#include "tls_test_client.hpp"
#include <functional>
#include <sstream>

using namespace reactor::net;
using nlohmann::json;

namespace {

std::string mktemp_dir() {
    char tmpl[] = "/tmp/nettls-XXXXXX";
    return mkdtemp(tmpl);
}

void rm_rf(const std::string& d) {
    if (std::system(("rm -rf '" + d + "'").c_str()) != 0) {}
}

std::string slurp(const std::string& p) {
    std::ifstream f(p, std::ios::binary);
    std::stringstream ss;
    ss << f.rdbuf();
    return ss.str();
}

bool fp_format_ok(const std::string& fp) {
    if (fp.size() != 95) return false;
    for (size_t i = 0; i < fp.size(); ++i) {
        char c = fp[i];
        if (i % 3 == 2) { if (c != ':') return false; }
        else if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) return false;
    }
    return true;
}

// 带 TLS 的服务器（tls = false 时不给证书 = 明文）
struct TlsFixture {
    std::string dir, sock;
    std::shared_ptr<TlsCertStore> store;
    std::unique_ptr<Server> srv;
    int port = 0;
    std::string big;

    explicit TlsFixture(bool tls = true, std::function<void(ServerOptions&)> tweak = {}) {
        dir = mktemp_dir();
        sock = dir + "/hmi.sock";
        for (int i = 0; i < 300 * 1024; ++i) big += static_cast<char>('a' + i % 26);
        ApiConfig cfg;
        cfg.enabled = true;
        ServerOptions o = server_options_from_config(cfg);
        o.tcp_bind = "127.0.0.1";
        o.tcp_port = 0;
        o.tcp_bind_retry_ms = 0;
        o.unix_path = sock;
        o.ws_keepalive_ms = 0;
        if (tls) {
            store = std::make_shared<TlsCertStore>(dir + "/tls", "test");
            CHECK(store->load_or_create());
            o.tls = store;
        }
        if (tweak) tweak(o);
        srv = std::make_unique<Server>(o, cfg);
        Router& r = srv->router();
        r.get("/api/v1/health", [](const Request&) { return Response::json(200, json{{"ok", true}}); }, RouteOptions{RateClass::None, true, false});
        r.post("/api/v1/echo", [](const Request& q) { return Response::text(200, q.body); }, RouteOptions{RateClass::None, true, false});
        r.get("/api/v1/big", [this](const Request&) { return Response::text(200, big); }, RouteOptions{RateClass::None, true, false});
        WsHandlers h;
        h.on_open = [](const WsSessionPtr& s, const Request&) { s->send(json{{"type", "hello"}}.dump()); };
        h.on_message = [](const WsSessionPtr& s, const std::string& m, bool) { s->send("echo:" + m); };
        r.ws("/api/v1/ws", h, RouteOptions{RateClass::None, true, false});
        std::string err;
        CHECK(srv->start(&err));
        port = srv->tcp_port();
    }
    ~TlsFixture() {
        srv->stop();
        srv.reset();
        rm_rf(dir);
    }
};

}  // namespace

// ———————————————————————————— 证书仓库 ————————————————————————————

TEST(net_tls, store_create_persist_reload) {
    std::string d = mktemp_dir();
    std::string fp1;
    {
        TlsCertStore s(d + "/tls", "box-1");
        bool created = false;
        std::string why, err;
        CHECK(s.load_or_create(&err, &created, &why));
        CHECK(created);
        CHECK(!why.empty());
        auto i = s.info();
        CHECK(i.valid);
        CHECK(fp_format_ok(i.fingerprint_sha256));
        CHECK(i.subject.find("CN=HT-PR8 box-1") != std::string::npos);
        CHECK(i.not_after_ms - i.not_before_ms > 19LL * 365 * 24 * 3600 * 1000);
        CHECK(i.created_ms > 0);
        CHECK(s.server_config() != nullptr);
        struct stat st{};
        CHECK(::stat(s.key_path().c_str(), &st) == 0);
        CHECK_EQ(static_cast<int>(st.st_mode & 0777), 0600);
        // 文件里的证书算出来的指纹和仓库给的一致（= openssl x509 -fingerprint -sha256）
        auto fi = tls_cert_info_from_pem(slurp(s.cert_path()));
        CHECK_EQ(fi.fingerprint_sha256, i.fingerprint_sha256);
        fp1 = i.fingerprint_sha256;
    }
    {
        TlsCertStore s(d + "/tls", "box-1");   // 重启：读回同一张证书
        bool created = true;
        CHECK(s.load_or_create(nullptr, &created));
        CHECK(!created);
        CHECK_EQ(s.info().fingerprint_sha256, fp1);
        // 重新生成：指纹变，文件也变
        CHECK(s.regenerate());
        CHECK(s.info().fingerprint_sha256 != fp1);
        CHECK_EQ(tls_cert_info_from_pem(slurp(s.cert_path())).fingerprint_sha256, s.info().fingerprint_sha256);
    }
    rm_rf(d);
}

TEST(net_tls, store_bad_files_regenerate) {
    std::string d = mktemp_dir();
    std::string fp1;
    {
        TlsCertStore s(d + "/tls");
        CHECK(s.load_or_create());
        fp1 = s.info().fingerprint_sha256;
    }
    { std::ofstream f(d + "/tls/cert.pem"); f << "garbage"; }
    {
        TlsCertStore s(d + "/tls");
        bool created = false;
        std::string why;
        CHECK(s.load_or_create(nullptr, &created, &why));
        CHECK(created);
        CHECK(why.find("原证书不能用") != std::string::npos);
        CHECK(s.info().fingerprint_sha256 != fp1);
    }
    ::unlink((d + "/tls/key.pem").c_str());   // 缺私钥
    {
        TlsCertStore s(d + "/tls");
        bool created = false;
        std::string why;
        CHECK(s.load_or_create(nullptr, &created, &why));
        CHECK(created);
        CHECK(why.find("key.pem") != std::string::npos);
    }
    // 私钥和证书不配对（换一套的私钥）
    {
        std::string d2 = mktemp_dir();
        TlsCertStore other(d2 + "/tls");
        CHECK(other.load_or_create());
        { std::ofstream f(d + "/tls/key.pem"); f << slurp(other.key_path()); }
        TlsCertStore s(d + "/tls");
        bool created = false;
        std::string why;
        CHECK(s.load_or_create(nullptr, &created, &why));
        CHECK(created);
        CHECK(why.find("不配对") != std::string::npos);
        rm_rf(d2);
    }
    rm_rf(d);
}

// ———————————————————————————— 服务器 ————————————————————————————

TEST(net_tls, https_get_fingerprint_keepalive) {
    TlsFixture f;
    nettest::TlsClient c;
    CHECK(c.connect(f.port));
    CHECK_EQ(c.peer_fingerprint, f.store->info().fingerprint_sha256);   // PC 端看到的指纹 = 设置页显示的指纹
    CHECK_EQ(c.version, std::string("TLSv1.3"));
    auto r = c.request("GET", "/api/v1/health");
    CHECK_EQ(r.status, 200);
    CHECK(r.body.find("\"ok\":true") != std::string::npos);
    auto r2 = c.request("POST", "/api/v1/echo", "", "hello tls");   // 同一连接第二个请求（keep-alive）
    CHECK_EQ(r2.status, 200);
    CHECK_EQ(r2.body, std::string("hello tls"));
}

TEST(net_tls, tls12_client) {
    TlsFixture f;
    nettest::TlsClient c;
    CHECK(c.connect(f.port, /*allow13=*/false));
    CHECK_EQ(c.version, std::string("TLSv1.2"));
    CHECK(c.suite.find("ECDHE-ECDSA") != std::string::npos);
    CHECK(c.suite.find("GCM") != std::string::npos || c.suite.find("CHACHA20") != std::string::npos);
    CHECK_EQ(c.peer_fingerprint, f.store->info().fingerprint_sha256);
    CHECK_EQ(c.request("GET", "/api/v1/health").status, 200);
}

TEST(net_tls, big_body_both_ways) {
    TlsFixture f;
    nettest::TlsClient c;
    CHECK(c.connect(f.port));
    auto r = c.request("GET", "/api/v1/big");   // 300 KB：多条记录 + 背压
    CHECK_EQ(r.status, 200);
    CHECK(r.body == f.big);
    std::string up(60 * 1024, 'u');
    for (size_t i = 0; i < up.size(); i += 7) up[i] = 'x';
    auto r2 = c.request("POST", "/api/v1/echo", "", up);   // 60 KB 上传：一次 poll 里收到好几条记录
    CHECK_EQ(r2.status, 200);
    CHECK(r2.body == up);
    // 流水线：两个请求一次发出，服务器要把缓冲里剩下的密文也解出来（poll 不再报可读）
    CHECK(c.send_all(nettest::build("POST", "/api/v1/echo", "", "one") + nettest::build("POST", "/api/v1/echo", "", "two")));
    std::string buf;
    auto a = nettest::read_response_io(c.reader(), buf, 3000);
    auto b = nettest::read_response_io(c.reader(), buf, 3000);
    CHECK_EQ(a.body, std::string("one"));
    CHECK_EQ(b.body, std::string("two"));
}

TEST(net_tls, wss_hello_and_echo) {
    TlsFixture f;
    nettest::TlsClient c;
    CHECK(c.connect(f.port));
    nettest::WsClient ws;
    ws.sender = c.sender();
    ws.reader = c.reader();
    CHECK(ws.open_fd(c.fd, "/api/v1/ws", ""));
    std::string m;
    CHECK(ws.next_text(m));
    CHECK(m.find("hello") != std::string::npos);
    CHECK(ws.send_text("ping-1"));
    CHECK(ws.next_text(m));
    CHECK_EQ(m, std::string("echo:ping-1"));
    std::string big(60 * 1024, 'w');   // 入站上限 64 KB
    CHECK(ws.send_text(big));
    CHECK(ws.next_text(m));
    CHECK_EQ(m.size(), big.size() + 5);
    CHECK(ws.send_frame(reactor::net::ws::kClose, std::string("\x03\xe8", 2)));
    CHECK_EQ(ws.wait_close(), 1000);
}

TEST(net_tls, regenerate_hot_swap) {
    TlsFixture f;
    std::string fp1 = f.store->info().fingerprint_sha256;
    nettest::TlsClient old_conn;
    CHECK(old_conn.connect(f.port));
    CHECK_EQ(old_conn.peer_fingerprint, fp1);
    CHECK(f.store->regenerate());
    std::string fp2 = f.store->info().fingerprint_sha256;
    CHECK(fp2 != fp1);
    // 新连接立刻看到新证书
    std::string seen;
    auto r = nettest::tls_request(f.port, "GET", "/api/v1/health", "", "", "", &seen);
    CHECK_EQ(r.status, 200);
    CHECK_EQ(seen, fp2);
    // 已有连接照常工作
    CHECK_EQ(old_conn.request("GET", "/api/v1/health").status, 200);
}

TEST(net_tls, plain_http_on_tls_port_rejected) {
    TlsFixture f;
    auto r = nettest::request(f.port, "GET", "/api/v1/health");
    CHECK_EQ(r.status, 400);
    CHECK(r.body.find("TLS_REQUIRED") != std::string::npos);
    CHECK(r.closed);
    // 端口仍然好用
    CHECK_EQ(nettest::tls_request(f.port, "GET", "/api/v1/health").status, 200);
}

TEST(net_tls, unix_socket_stays_plain) {
    TlsFixture f;
    int fd = nettest::connect_unix(f.sock);
    CHECK(fd >= 0);
    nettest::send_all(fd, nettest::build("GET", "/api/v1/health"));
    std::string buf;
    auto r = nettest::read_response(fd, buf);
    ::close(fd);
    CHECK_EQ(r.status, 200);
}

TEST(net_tls, no_store_means_plaintext) {
    TlsFixture f(false);   // api.json "tls": false 的情形
    CHECK_EQ(nettest::request(f.port, "GET", "/api/v1/health").status, 200);
    nettest::TlsClient c;
    CHECK(!c.connect(f.port));   // 对明文端口握手失败
}

TEST(net_tls, handshake_timeout_and_garbage) {
    TlsFixture f(true, [](ServerOptions& o) { o.tls_handshake_timeout_ms = 300; });
    int fd = nettest::connect_tcp(f.port);   // 连上不说话
    CHECK(fd >= 0);
    std::string buf;
    bool eof = false;
    auto t0 = std::chrono::steady_clock::now();
    while (!eof && std::chrono::steady_clock::now() - t0 < std::chrono::seconds(3)) nettest::read_more(fd, buf, 200, &eof);
    CHECK(eof);
    ::close(fd);
    // 乱码（不像 TLS 也不像 HTTP）：直接断开
    fd = nettest::connect_tcp(f.port);
    nettest::send_all(fd, std::string("\x16\x03\x01\x00\x05garbagegarbage", 19));
    eof = false;
    buf.clear();
    t0 = std::chrono::steady_clock::now();
    while (!eof && std::chrono::steady_clock::now() - t0 < std::chrono::seconds(3)) nettest::read_more(fd, buf, 200, &eof);
    CHECK(eof);
    ::close(fd);
    CHECK_EQ(nettest::tls_request(f.port, "GET", "/api/v1/health").status, 200);
}
