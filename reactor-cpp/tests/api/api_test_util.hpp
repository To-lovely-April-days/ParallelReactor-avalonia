// tests/api 公用：在进程里起完整的后台（模拟硬件 + 控制线程 + 自动化 + 接口层 + 真 socket：127.0.0.1:随机端口 + 临时 Unix socket）。
#pragma once
#include <unistd.h>

#include <chrono>
#include <cstdlib>
#include <memory>
#include <string>
#include <thread>

#include "api/api_service.hpp"
#include "ctl/ctl_automation.hpp"
#include "ctl/ctl_clock.hpp"
#include "ctl/ctl_core.hpp"
#include "hw/hardware.hpp"
#include "net/crypto.hpp"
#include "net/net_test_client.hpp"
#include "net/server.hpp"
#include "store/data_logger.hpp"
#include "store/event_store.hpp"
#include "store/program_library.hpp"
#include "store/session.hpp"

namespace apitest {

using namespace reactor;
using json = nlohmann::json;

struct Resp {
    int status = 0;
    json body;
    std::string raw;
    std::map<std::string, std::string> headers;
    std::string h(const std::string& n) const {
        auto it = headers.find(n);
        return it == headers.end() ? "" : it->second;
    }
};

struct ApiRig {
    std::string dir;
    std::unique_ptr<store::EventStore> events;
    std::unique_ptr<store::DataLogger> logger;
    std::unique_ptr<store::SessionBook> sessions;
    std::unique_ptr<store::ProgramLibrary> library;
    std::unique_ptr<hw::Hardware> hw;
    ctl::SystemClock clock;
    std::unique_ptr<ctl::ControlCore> core;
    std::unique_ptr<ctl::Automation> au;
    std::unique_ptr<api::ApiService> api;
    std::unique_ptr<net::Server> srv;
    std::string sock;
    int port = 0;

    explicit ApiRig(std::function<void(net::ApiConfig&)> tweak = {}) {
        char tmpl[] = "/tmp/rapi-XXXXXX";
        dir = mkdtemp(tmpl);
        events = std::make_unique<store::EventStore>(dir);
        logger = std::make_unique<store::DataLogger>(dir, nullptr, events.get());
        sessions = std::make_unique<store::SessionBook>([this](const std::string& rel, const std::string& j) { logger->write_file(rel, j); });
        library = std::make_unique<store::ProgramLibrary>(dir);
        hw = hw::make_hardware(hw::HardwareConfig::for_sim());
        ctl::CoreOptions opt;
        opt.tick_ms = 200;
        opt.watchdog = false;
        ctl::CoreDeps d;
        d.hw = hw.get();
        d.clock = &clock;
        d.logger = logger.get();
        core = std::make_unique<ctl::ControlCore>(std::move(d), opt);
        ctl::AutomationDeps ad;
        ad.core = core.get();
        ad.logger = logger.get();
        ad.sessions = sessions.get();
        ad.library = library.get();
        au = std::make_unique<ctl::Automation>(ad);

        net::ApiConfig cfg;
        cfg.enabled = true;
        cfg.bind = "127.0.0.1";
        cfg.http_port = 0;
        cfg.heartbeat_timeout_sec = 2;
        cfg.lease_grace_sec = 2;
        cfg.control_request_timeout_sec = 30;
        cfg.write_rate_limit_per_sec = 1000;
        auto tok = [](const char* name, const char* t, const char* role, std::optional<bool> estop = std::nullopt) {
            net::ApiToken k;
            k.name = name;
            k.sha256 = net::sha256_hex(t);
            k.role = role;
            k.estop = estop;
            return k;
        };
        cfg.tokens = {tok("pc1", "optoken", "operator"), tok("pc2", "optoken2", "operator"), tok("adm", "admtoken", "admin"),
                      tok("view", "viewtoken", "viewer"), tok("stopper", "esttoken", "viewer", true)};
        if (tweak) tweak(cfg);

        api::ApiDeps apid;
        apid.cfg = cfg;
        apid.core = core.get();
        apid.automation = au.get();
        apid.logger = logger.get();
        apid.events = events.get();
        apid.sessions = sessions.get();
        apid.library = library.get();
        apid.hw = hw.get();
        apid.data_dir = dir;
        apid.usb_root = dir + "/media";
        apid.config_dir = dir;
        apid.nmcli = {"/nonexistent-nmcli"};
        api = std::make_unique<api::ApiService>(apid);
        core->start();

        net::ServerOptions so = net::server_options_from_config(cfg);
        so.tcp_bind = "127.0.0.1";
        so.tcp_port = 0;
        so.tcp_bind_retry_ms = 0;
        so.worker_threads = 8;
        so.method_mismatch_as_404 = true;
        sock = dir + "/hmi.sock";
        so.unix_path = sock;
        srv = std::make_unique<net::Server>(so, cfg);
        api->register_routes(srv->router());
        api->attach(srv.get());
        api->start();
        std::string err;
        srv->start(&err);
        port = srv->tcp_port();
        for (int i = 0; i < 100 && !core->snapshot(); ++i) std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    ~ApiRig() {
        api->stop_ticker();
        srv->stop();
        api->stop();
        core->shutdown(true);
        api.reset();
        srv.reset();
        au.reset();
        logger->stop();
        events->stop();
        std::system(("rm -rf '" + dir + "'").c_str());
    }

    // TCP（token 为空 = 不带令牌）或 Unix（unix = true）
    Resp call(bool unix, const std::string& method, const std::string& path, const std::string& token = "", const std::string& body = "",
              const std::string& extra = "") {
        int fd = unix ? nettest::connect_unix(sock) : nettest::connect_tcp(port);
        Resp r;
        if (fd < 0) return r;
        std::string hdr = extra;
        if (!body.empty()) hdr += "Content-Type: application/json\r\n";
        nettest::send_all(fd, nettest::build(method, path, token, body, hdr));
        std::string buf;
        auto h = nettest::read_response(fd, buf, 15000, method == "HEAD");
        ::close(fd);
        r.status = h.status;
        r.raw = h.body;
        r.headers = h.headers;
        r.body = json::parse(h.body, nullptr, false);
        return r;
    }
    Resp tcp(const std::string& m, const std::string& p, const std::string& tok, const std::string& body = "", const std::string& extra = "") {
        return call(false, m, p, tok, body, extra);
    }
    Resp local(const std::string& m, const std::string& p, const std::string& body = "", const std::string& extra = "") {
        return call(true, m, p, "", body, extra);
    }
    static std::string idem(const std::string& k) { return "Idempotency-Key: " + k + "\r\n"; }
    static std::string lease(const std::string& l) { return "X-Control-Lease: " + l + "\r\n"; }

    // pc1 申请并让触屏允许，返回 leaseId
    std::string take_control(const std::string& token = "optoken", const std::string& name = "PC-1") {
        auto r = tcp("POST", "/api/v1/control/request", token, "{\"clientName\":\"" + name + "\"}", idem("req-" + name + std::to_string(rand())));
        std::string rid = r.body["result"]["requestId"], key = r.body["result"]["requestKey"];
        local("POST", "/api/v1/control/requests/" + rid + "/grant", "{}");
        auto q = tcp("GET", "/api/v1/control/requests/" + rid, token, "", "X-Control-Request-Key: " + key + "\r\n");
        return q.body.value("leaseId", std::string());
    }
};

template <class F>
bool wait_until(F f, int ms = 5000) {
    for (int i = 0; i < ms / 20; ++i) {
        if (f()) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }
    return f();
}

}  // namespace apitest
