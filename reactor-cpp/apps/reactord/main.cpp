// reactord：后台服务入口（设计文档 §3 apps/reactord）。组装 存储 + 设备层 + 控制核心 + 自动化（程序 / 泄漏 / 标定 / 整定）+ 接口层。
//
//   reactord [--sim] [--hw 文件] [--config 目录] [--data 目录] [--api-config 文件] [--socket 路径] [--hmi-uid N] [--port N] [--seconds N]
//
//   --sim         用模拟硬件（HardwareConfig::for_sim()，忽略 --hw）；配置 / 数据目录缺省 ./reactord-config-sim / ./reactord-data-sim，
//                 Unix socket 缺省 ./reactord-run/hmi.sock
//   --hw          hardware.json（缺省 /etc/reactord/hardware.json）
//   --config      配置目录（settings.json、api.json；缺省 /etc/reactord）
//   --data        数据目录（缺省 /var/lib/reactord）
//   --api-config  api.json 路径（缺省 <config>/api.json）。enabled=false 时只开 Unix 入口（触屏照常可用）
//   --socket      触屏用的 Unix socket（缺省 /run/reactord/hmi.sock）
//   --hmi-uid     只认这个 uid 的本机连接（缺省不限；部署填 reactor-hmi 的运行用户）
//   --port        覆盖 api.json 的 httpPort（冒烟测试用）
//   --seconds     运行 N 秒后自行退出（冒烟测试用）；缺省一直运行到 SIGINT / SIGTERM
//   --usb-root    U 盘挂载点的根目录（缺省 /media）。开发机联调：在 <根>/<卷名>/ 里放空文件 .reactor-fake-usb 就当作一个 U 盘
//
// 组装顺序（ctl 报告 §2.1 / api_service.hpp）：存储 → 设备层 → ControlCore → Automation → ApiService → core.start() → 接口服务器。
// 退出顺序：停租约计时 → 停接口服务器（不再收命令）→ 停 WS 中枢 → 结束会话 → core.shutdown(true)（停搅拌 / 8 路 At=4 / 阀门全关，
// 设置落盘）→ 析构 Automation → 停记录器 → 停事件存储。
#include <sys/stat.h>

#include <atomic>
#include <chrono>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <string>
#include <thread>

#include "api/api_service.hpp"
#include "common/clock.hpp"
#include "common/log.hpp"
#include "ctl/ctl_automation.hpp"
#include "ctl/ctl_core.hpp"
#include "hw/hardware.hpp"
#include "hw/hardware_config.hpp"
#include "net/api_config.hpp"
#include "net/server.hpp"
#include "store/data_logger.hpp"
#include "store/event_store.hpp"
#include "store/program_library.hpp"
#include "store/session.hpp"
#include "store/settings.hpp"
#include "store/store_util.hpp"
#include <cstdint>
#include <optional>

namespace {

std::atomic<bool> g_quit{false};
void on_signal(int) { g_quit = true; }

struct Args {
    bool sim = false;
    std::string hw = "/etc/reactord/hardware.json";
    std::string config = "/etc/reactord";
    std::string data = "/var/lib/reactord";
    std::string api_config;
    std::string socket = "/run/reactord/hmi.sock";
    bool config_set = false, data_set = false, socket_set = false;
    int64_t hmi_uid = -1;
    int port = -1;
    int seconds = 0;
    std::string usb_root;
};

bool parse(int argc, char** argv, Args& a) {
    for (int i = 1; i < argc; ++i) {
        std::string s = argv[i];
        auto next = [&](std::string& out) {
            if (i + 1 >= argc) return false;
            out = argv[++i];
            return true;
        };
        std::string v;
        if (s == "--sim") a.sim = true;
        else if (s == "--hw") { if (!next(a.hw)) return false; }
        else if (s == "--config") { if (!next(a.config)) return false; a.config_set = true; }
        else if (s == "--data") { if (!next(a.data)) return false; a.data_set = true; }
        else if (s == "--api-config") { if (!next(a.api_config)) return false; }
        else if (s == "--socket") { if (!next(a.socket)) return false; a.socket_set = true; }
        else if (s == "--hmi-uid") { if (!next(v)) return false; a.hmi_uid = std::atoll(v.c_str()); }
        else if (s == "--port") { if (!next(v)) return false; a.port = std::atoi(v.c_str()); }
        else if (s == "--seconds") { if (!next(v)) return false; a.seconds = std::atoi(v.c_str()); }
        else if (s == "--usb-root") { if (!next(a.usb_root)) return false; }
        else return false;
    }
    if (a.sim) {
        if (!a.config_set) a.config = "./reactord-config-sim";
        if (!a.data_set) a.data = "./reactord-data-sim";
        if (!a.socket_set) a.socket = "./reactord-run/hmi.sock";
    }
    if (a.api_config.empty()) a.api_config = a.config + "/api.json";
    return true;
}

}  // namespace

int main(int argc, char** argv) {
    using namespace reactor;
    Args args;
    if (!parse(argc, argv, args)) {
        std::fprintf(stderr,
                     "用法：reactord [--sim] [--hw 文件] [--config 目录] [--data 目录] [--api-config 文件] [--socket 路径] [--hmi-uid N] "
                     "[--port N] [--seconds N] [--usb-root 目录]\n");
        return 2;
    }
    std::signal(SIGINT, on_signal);
    std::signal(SIGTERM, on_signal);
    std::signal(SIGPIPE, SIG_IGN);
    auto now = [] { return store::WallTime::local(wall_ms()); };

    // ---- 存储 ----
    auto on_err = [](const std::string& msg) { LOGW("store", "%s", msg.c_str()); };
    store::EventStore events(args.data, on_err);
    store::DataLogger logger(args.data, on_err, &events);
    store::SettingsStore settings_store(args.config);
    auto loaded = settings_store.load();
    store::SettingsState settings = loaded.state ? *loaded.state : store::SettingsState{};
    if (!loaded.backup_path.empty())
        logger.event(now(), "warn", "系统", "设置文件已备份", loaded.backup_path, "system", std::nullopt, std::nullopt, "system.settingsBackup");
    if (!loaded.fixes.empty()) {
        for (const auto& f : loaded.fixes)
            logger.event(now(), "warn", "系统", "设置已自动纠正", f, "system", std::nullopt, std::nullopt, "system.settingsFixed");
        std::string serr;
        if (!settings_store.save_now(settings, &serr)) LOGW("reactord", "纠正后的设置写不回去：%s", serr.c_str());
    }
    if (loaded.found && !loaded.state)
        logger.event(now(), "err", "系统", "设置文件读不了 · 按默认值启动", loaded.error, "system", std::nullopt, std::nullopt,
                     "system.settingsUnreadable");
    auto fixed = store::SessionBook::recover_interrupted(args.data);
    if (!fixed.empty()) {
        std::string ids;
        for (const auto& f : fixed) ids += (ids.empty() ? "" : "、") + f;
        logger.event(now(), "warn", "系统", "上次未结束的测试会话已标为 interrupted", ids, "system", std::nullopt, std::nullopt,
                     "session.interrupted");
    }
    store::SessionBook sessions([&logger](const std::string& rel, const std::string& json) { logger.write_file(rel, json); });
    store::ProgramLibrary library(args.data);
    for (const auto& e : library.load_errors())
        logger.event(now(), "warn", "系统", "程序库文件读不了 · 原样保留", e, "system", std::nullopt, std::nullopt, "library.loadError");

    // ---- 设备层 ----
    hw::HardwareConfig cfg;
    if (args.sim) {
        cfg = hw::HardwareConfig::for_sim();
    } else {
        std::string err;
        if (!hw::load_hardware_config(args.hw, cfg, &err)) {
            LOGE("reactord", "读 %s 失败（%s）：按全离线启动", args.hw.c_str(), err.c_str());
            cfg = hw::HardwareConfig{};
            cfg.temp.port.clear();
            cfg.stir.port.clear();
            cfg.press.port.clear();
            cfg.io.port.clear();
            logger.event(now(), "err", "系统", "hardware.json 读不了 · 全部模块按离线处理", args.hw + " · " + err, "system", std::nullopt,
                         std::nullopt, "system.hwConfigUnreadable");
        }
    }
    auto hw = hw::make_hardware(cfg);

    // ---- 接口配置 ----
    std::string api_err;
    net::ApiConfig api_cfg = net::load_api_config(args.api_config, &api_err);
    net::apply_api_env(api_cfg, [](const char* k) { return std::getenv(k); });
    if (args.port >= 0) api_cfg.http_port = args.port;
    if (!api_err.empty())
        logger.event(now(), "err", "接口", "接口配置读取失败 · 按默认（未启用）", api_err, "system", std::nullopt, std::nullopt, "system.api");

    // ---- PC 连接加密（自签证书，<配置目录>/tls/）----
    std::shared_ptr<net::TlsCertStore> tls;
    if (api_cfg.enabled && api_cfg.tls) {
        tls = std::make_shared<net::TlsCertStore>(args.config + "/tls");
        std::string terr, why;
        bool created = false;
        if (!tls->load_or_create(&terr, &created, &why)) {
            // 证书生成都失败（目录不可写等）：TCP 入口不开（宁可连不上也不退回明文），触屏照常
            LOGE("reactord", "证书不能用（%s）：PC 入口不开", terr.c_str());
            logger.event(now(), "err", "接口", "证书不能用 · PC 入口未开启", terr, "system", std::nullopt, std::nullopt, "system.tls");
            api_cfg.enabled = false;
            tls.reset();
        } else {
            auto inf = tls->info();
            LOGI("reactord", "PC 连接已加密 · 证书指纹 SHA-256 %s", inf.fingerprint_sha256.c_str());
            if (created)
                logger.event(now(), "info", "接口", "已生成新的连接证书", why + " · 指纹 " + inf.fingerprint_sha256.substr(0, 11) + "…", "system",
                             std::nullopt, std::nullopt, "system.tlsCreated");
        }
    } else if (api_cfg.enabled) {
        LOGW("reactord", "PC 连接未加密（api.json \"tls\": false）");
    }

    // ---- 控制核心 + 自动化 ----
    ctl::SystemClock clock;
    ctl::CoreDeps deps;
    deps.hw = hw.get();
    deps.clock = &clock;
    deps.logger = &logger;
    deps.settings_store = &settings_store;
    deps.settings = settings;
    ctl::ControlCore core(std::move(deps));
    ctl::AutomationDeps ad;
    ad.core = &core;
    ad.logger = &logger;
    ad.sessions = &sessions;
    ad.library = &library;
    auto automation = std::make_unique<ctl::Automation>(ad);
    for (const auto& e : automation->seed_result().errors)
        logger.event(now(), "warn", "系统", "内置程序模板", e, "system", std::nullopt, std::nullopt, "library.seed");

    // ---- 接口层（构造时注册 ModeHooks / 程序进度回调：必须在 core.start() 之前） ----
    api::ApiDeps apid;
    apid.cfg = api_cfg;
    apid.core = &core;
    apid.automation = automation.get();
    apid.logger = &logger;
    apid.events = &events;
    apid.sessions = &sessions;
    apid.library = &library;
    apid.hw = hw.get();
    apid.data_dir = args.data;
    apid.config_dir = args.config;
    if (!args.usb_root.empty()) apid.usb_root = args.usb_root;
    apid.request_exit = [] { g_quit = true; };
    apid.tls = tls;
    auto api = std::make_unique<api::ApiService>(apid);

    core.snapshots().subscribe([](const ctl::SnapshotStore::Ptr& s) {
        if (s->seq % 50 == 1)
            LOGI("reactord", "快照 #%lld · 温控%s 压力%s 阀门%s 搅拌%s", static_cast<long long>(s->seq), s->comm.temp ? "在线" : "离线",
                 s->comm.pressure ? "在线" : "离线", s->comm.io ? "在线" : "离线", s->comm.stir ? "在线" : "离线");
    });
    core.start();

    net::ServerOptions sopt = net::server_options_from_config(api_cfg);
    sopt.tcp_enabled = api_cfg.enabled;
    sopt.tls = tls;
    sopt.unix_path = args.socket;
    sopt.worker_threads = 8;
    sopt.method_mismatch_as_404 = true;   // 决定记录：方法不对 → 404「没有这个端点」，同 C#
    if (args.hmi_uid >= 0) sopt.gate.unix_allowed_uids = {args.hmi_uid};
    sopt.audit = [&logger, now](const std::string& level, const std::string& title, const std::string& detail) {
        logger.event(now(), level, "接口", title, detail, "system", std::nullopt, std::nullopt, "system.api");
    };
    {
        std::string dir = store::dir_name(args.socket);
        if (!dir.empty() && dir != ".") {
            store::make_dirs(dir);
            if (args.sim) ::chmod(dir.c_str(), 0750);
        }
    }
    auto srv = std::make_unique<net::Server>(sopt, api_cfg);
    api->register_routes(srv->router());
    api->attach(srv.get());
    api->start();
    std::string serr;
    if (!srv->start(&serr)) {
        LOGE("reactord", "接口服务器启动失败：%s（控制照常运行，触屏连不上）", serr.c_str());
        logger.event(now(), "err", "接口", "接口服务器启动失败", serr, "system", std::nullopt, std::nullopt, "system.api");
    }
    LOGI("reactord", "已启动（%s）· 数据 %s · 配置 %s · TCP %s · Unix %s", hw->simulated ? "模拟硬件" : "真机", args.data.c_str(),
         args.config.c_str(), api_cfg.enabled ? (api_cfg.bind + ":" + std::to_string(srv->tcp_port())).c_str() : "未启用",
         args.socket.c_str());

    int64_t t_end = args.seconds > 0 ? mono_ms() + args.seconds * 1000LL : 0;
    while (!g_quit && (t_end == 0 || mono_ms() < t_end)) std::this_thread::sleep_for(std::chrono::milliseconds(100));

    LOGI("reactord", "正在退出…");
    api->stop_ticker();
    srv->stop();
    api->stop();
    {
        // 退出时结束测试会话（C# ExitAppTailAsync 的 EndTestSession）
        auto f = core.call([&] { api->commands().end_session_on_exit(); return 0; });
        f.wait_for(std::chrono::seconds(3));
    }
    core.shutdown(true);
    api.reset();
    srv.reset();
    automation.reset();
    logger.stop();
    events.stop();
    if (auto s = core.snapshot()) LOGI("reactord", "已退出 · 共发布快照 %lld 帧", static_cast<long long>(s->seq));
    return 0;
}
