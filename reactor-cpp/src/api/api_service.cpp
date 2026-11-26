// 接口层：组装、写命令公共管线、控制权迁移 → 审计 / WS、WS 中枢线程、各 DTO 的 JSON。
// C#: Api/Endpoints.Write.cs CommandAsync、Api/CommandResults.cs、Api/ControlService.cs、Api/WsHub.cs（扇出部分）、Api/Hmi/HmiApiSources.cs
#include "api/api_service.hpp"

#include <chrono>
#include <cmath>

#include "api/api_routes.hpp"
#include "api/api_runs.hpp"
#include "api/api_system.hpp"
#include "api/api_util.hpp"
#include "cmd/cmd_util.hpp"
#include "common/clock.hpp"
#include "common/log.hpp"
#include "core/op_codes.hpp"
#include "core/program/program_limits.hpp"
#include "core/program/program_schema.hpp"
#include "core/program/program_run_info.hpp"
#include "net/api_errors.hpp"
#include "net/api_headers.hpp"
#include "store/store_util.hpp"
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

namespace reactor::api {

namespace oc = core::op_codes;

namespace {
cmd::CommandsDeps commands_deps(const ApiDeps& d, cmd::ControlArbiter& a, cmd::ControlLease& l) {
    cmd::CommandsDeps c;
    c.core = d.core;
    c.automation = d.automation;
    c.logger = d.logger;
    c.sessions = d.sessions;
    c.hw = d.hw;
    c.arbiter = &a;
    c.lease = &l;
    c.mono_ms = d.mono_ms ? d.mono_ms : [] { return reactor::mono_ms(); };
    c.unit = d.unit;
    c.simulated = d.hw ? d.hw->simulated : false;
    if (!d.data_dir.empty()) c.prefs_path = store::join_path(d.data_dir, "channel-prefs.json");
    return c;
}
ApiDeps fill_defaults(ApiDeps d) {
    if (!d.mono_ms) d.mono_ms = [] { return reactor::mono_ms(); };
    if (!d.wall_ms) d.wall_ms = [] { return reactor::wall_ms(); };
    return d;
}
std::string trunc200(const std::string& s) {
    std::string t = utf16_len(s) <= 200 ? s : truncate_note(s, 200) + "…";
    for (auto& c : t)
        if (c == '\n' || c == '\r') c = ' ';
    return t;
}
}  // namespace

ApiService::ApiService(ApiDeps d)
    : d_(fill_defaults(std::move(d))), lease_(arbiter_), commands_(commands_deps(d_, arbiter_, lease_)), alive_(std::make_shared<std::atomic<bool>>(true)) {
    boot_id_ = cmd::random_hex(4);
    boot_wall_ms_ = d_.wall_ms();
    const auto& c = d_.cfg;
    lease_.configure(static_cast<int64_t>(c.heartbeat_timeout_sec) * 1000, static_cast<int64_t>(c.lease_grace_sec) * 1000,
                     static_cast<int64_t>(c.control_request_timeout_sec) * 1000);
    lease_.on_transition = [this](const cmd::LeaseEvent& e) { on_lease_event(e); };
    d_.core->set_mode_hooks(this);
    runs_ = std::make_shared<RunsBook>(d_.data_dir);
    // WS progress（设计文档 §5.4）：程序引擎的步骤 / 结束回调在控制线程上，只做入队
    auto alive = alive_;
    auto& eng = d_.automation->programs();
    eng.on_step = [this, alive](int rv, int index, const std::string& state, const std::string& message) {
        (void)message;
        if (!alive->load()) return;
        runs_->on_step(rv, index, state, now_wall());
    };
    eng.on_finished = [this, alive](int rv, core::program::ProgramRunPhase phase) {
        (void)rv;
        (void)phase;
        if (alive->load()) bump_programs();
    };
}

ApiService::~ApiService() {
    stop_ticker();
    stop();
    d_.core->set_mode_hooks(nullptr);   // 只在核心已停止（或还没启动）时析构
}

void ApiService::attach(net::Server* srv) { srv_ = srv; }

void ApiService::start() {
    if (started_) return;
    started_ = true;
    started_at_ms_ = now_wall();
    auto alive = alive_;
    hub_thread_ = std::thread([this] { hub_loop(); });
    snap_sub_ = d_.core->snapshots().subscribe([this, alive](const ctl::SnapshotStore::Ptr& s) {
        if (!alive->load()) return;
        std::lock_guard<std::mutex> lk(hub_mu_);
        hub_frame_ = s;
        hub_cv_.notify_one();
    });
    if (d_.events)
        event_sub_ = d_.events->subscribe([this, alive](const store::EventRecord& r) {
            if (!alive->load() || ws_count_.load() == 0) return;
            ojson j = ojson::parse(store::event_to_json(r), nullptr, false);
            if (j.is_discarded()) return;
            ojson m;
            m["type"] = "event";
            for (auto it = j.begin(); it != j.end(); ++it) m[it.key()] = it.value();
            hub_push(HubKind::Event, dump(m));
        });
    alarm_sub_ = d_.core->subscribe_alarms([this, alive](const ctl::AlarmItem& a) {
        if (!alive->load() || ws_count_.load() == 0) return;
        auto cur = snap();
        hub_push(HubKind::Alarm, dump(ojson{{"type", "alarm"}, {"alarm", alarm_json(a, cur.get())}}));
    });
    notice_sub_ = d_.core->subscribe_notices([this, alive](const std::string& kind, const std::string& msg) {
        if (!alive->load() || ws_count_.load() == 0) return;
        hub_push(HubKind::Local, dump(ojson{{"type", "notice"}, {"kind", kind}, {"message", msg}, {"ts", utc_iso(now_wall())}}));
    });
    if (d_.lease_ticker)
        ticker_ = std::thread([this] {
            std::unique_lock<std::mutex> lk(ticker_mu_);
            while (!ticker_stop_.load()) {
                ticker_cv_.wait_for(lk, std::chrono::seconds(1));
                if (ticker_stop_.load()) break;
                lk.unlock();
                lease_.tick(now_mono());
                lk.lock();
            }
        });
    // C# ApiHost.TryStart 的配置警告（net 只记传输相关的）
    const auto& c = d_.cfg;
    if (c.enabled && c.tokens.empty())
        audit_plain("warn", "接口未配置任何令牌 · 除 /health 外所有请求都会返回 401",
                    "请在 " + c.source_file_name() + " 的 tokens 里加入令牌的 SHA-256" + (c.file_found ? "" : "（该文件不存在，当前为 PR_API=1 强制启用）"));
    if (c.enabled && (c.https_port > 0 || (c.cert_path && !c.cert_path->empty())))
        audit_plain("warn", "接口 HTTPS 未实现 · 已忽略 httpsPort / certPath", "只提供 HTTP（令牌明文传输），依赖网段隔离与白名单");
    for (const auto& w : c.warnings) audit_plain("warn", "接口配置已修正", c.source_file_name() + "：" + w);
    if (c.enabled && c.auto_grant_remote)
        audit_plain("warn", "远程控制申请免触屏确认（autoGrantRemote）", "任何 operator 令牌申请即获控制权；只用于无人值守 / 验收，现场请关闭");
}

void ApiService::stop_ticker() {
    ticker_stop_.store(true);
    {
        std::lock_guard<std::mutex> lk(ticker_mu_);
        ticker_cv_.notify_all();
    }
    if (ticker_.joinable()) ticker_.join();
}

void ApiService::stop() {
    if (!started_) {
        alive_->store(false);
        return;
    }
    started_ = false;
    alive_->store(false);
    if (snap_sub_) d_.core->snapshots().unsubscribe(snap_sub_);
    if (event_sub_ && d_.events) d_.events->unsubscribe(event_sub_);
    snap_sub_ = event_sub_ = 0;
    {
        std::lock_guard<std::mutex> lk(hub_mu_);
        hub_stop_ = true;
        hub_cv_.notify_all();
    }
    if (hub_thread_.joinable()) hub_thread_.join();
}

ojson ApiService::mode() const {
    bool remote = arbiter_.mode() == cmd::ControlMode::Remote;
    return ojson{{"control", remote ? "remote" : "local"}, {"holder", remote ? ojson(arbiter_.holder_name()) : ojson(nullptr)}};
}

std::optional<std::string> ApiService::utc_of_mono(std::optional<int64_t> mono) const {
    if (!mono) return std::nullopt;
    return utc_iso(now_wall() + (*mono - now_mono()));
}

int64_t ApiService::audit(const std::string& level, const std::string& title, const std::string& detail, const std::string& source,
                          const std::optional<std::string>& client, const std::optional<std::string>& op_id, const std::string& kind) {
    if (!d_.logger) return 0;
    auto r = d_.logger->event(store::WallTime::local(now_wall()), level, "接口", title, detail, source, client, op_id, kind);
    return r ? r->seq : 0;
}

void ApiService::audit_plain(const std::string& level, const std::string& title, const std::string& detail) {
    if (d_.logger) d_.logger->event(store::WallTime::local(now_wall()), level, "接口", title, detail, "system", std::nullopt, std::nullopt, "system.api");
}

// ===================== 控制权迁移 → 审计 / 推送 / onRemoteLost（C# ControlService.OnTransition） =====================

void ApiService::on_lease_event(const cmd::LeaseEvent& e) {
    using K = cmd::LeaseEventKind;
    const char *kind = "control.mode", *level = "info", *title = "控制模式变化";
    switch (e.kind) {
        case K::Requested: kind = "control.requested"; title = "远程申请控制权"; break;
        case K::Granted: kind = "control.granted"; title = "已授权远程控制"; break;
        case K::Denied: kind = "control.denied"; title = "拒绝远程控制申请"; break;
        case K::RequestExpired: kind = "control.requestExpired"; level = "warn"; title = "远程控制申请超时"; break;
        case K::Released: kind = "control.released"; title = "远程释放控制权 · 回到本地"; break;
        case K::TakenBack: kind = "control.takenBack"; title = "本地切回 · 远程租约作废"; break;
        case K::Lost: kind = "control.lost"; level = "warn"; title = "远程心跳丢失 · 设备继续运行"; break;
        case K::Resumed: kind = "control.resumed"; title = "远程心跳恢复"; break;
        case K::Expired: kind = "control.expired"; level = "warn"; title = "远程宽限期满 · 租约作废、回到本地"; break;
        case K::Invalidated: kind = "control.mode"; level = "warn"; title = "控制权被收回 · 远程租约作废"; break;
    }
    std::string detail = e.detail + (e.kind == K::Expired ? " · onRemoteLost = " + d_.cfg.on_remote_lost : "");
    if (!e.token.empty()) detail += " · 令牌 " + e.token;
    if (e.lease_id) detail += " · 租约 " + e.lease_id->substr(0, 8) + "…";
    audit(level, title, detail, "remote", e.client.empty() ? std::nullopt : std::optional<std::string>(e.client), std::nullopt, kind);

    const char* result = nullptr;
    switch (e.kind) {
        case K::Granted: case K::Resumed: result = "granted"; break;
        case K::Denied: result = "denied"; break;
        case K::RequestExpired: result = "expired"; break;
        case K::Requested: result = "pending"; break;
        case K::Lost: result = "lost"; break;
        case K::Expired: case K::TakenBack: case K::Released: case K::Invalidated: result = "revoked"; break;
    }
    bool remote = arbiter_.mode() == cmd::ControlMode::Remote;
    std::optional<int64_t> lease_exp;
    if (e.state == cmd::LeaseState::Remote || e.state == cmd::LeaseState::RemoteLost) lease_exp = e.at + lease_.heartbeat_timeout_ms();
    ojson m;
    m["type"] = "control";
    m["event"] = cmd::lease_event_name(e.kind);
    m["mode"] = remote ? "remote" : "local";
    m["holder"] = remote ? ojson(arbiter_.holder_name()) : ojson(nullptr);
    m["state"] = cmd::lease_state_word(e.state);
    m["requestId"] = opt_str(e.request_id);
    m["result"] = result ? ojson(result) : ojson(nullptr);
    m["leaseId"] = nullptr;   // leaseId 不进 WS（同一令牌的另一台 PC 也在听）
    m["leaseExpiresAt"] = opt_str(utc_of_mono(lease_exp));
    m["heartbeatTimeoutSec"] = d_.cfg.heartbeat_timeout_sec;
    m["detail"] = e.detail;
    m["ts"] = utc_iso(now_wall());
    // 触屏弹窗要的字段（新增，PC 端忽略）：申请方的客户端名 / 令牌名
    m["client"] = e.client.empty() ? ojson(nullptr) : ojson(e.client);
    m["token"] = e.token.empty() ? ojson(nullptr) : ojson(e.token);
    // 待处理的申请（触屏据此弹「允许 / 拒绝」框，调 POST /control/requests/{id}/grant|deny）
    m["pending"] = e.state == cmd::LeaseState::Requested && e.request_id
                       ? ojson{{"requestId", *e.request_id}, {"clientName", e.client}, {"tokenName", e.token},
                               {"expiresAt", opt_str(utc_of_mono(e.at + lease_.request_timeout_ms()))}}
                       : ojson(nullptr);
    hub_push(HubKind::Control, dump(m));

    if (e.kind == K::Lost) {
        std::string client = e.client;
        int grace = d_.cfg.lease_grace_sec;
        d_.core->post([this, client, grace] {
            d_.core->emit_notice("warn", "远程连接丢失（" + client + "）· 设备继续运行 · 宽限 " + std::to_string(grace) + " s");
        });
    }
    if (e.kind == K::Expired) {
        std::string policy = d_.cfg.on_remote_lost, token = e.token, client = e.client;
        d_.core->post([this, policy, token, client] { commands_.on_remote_lost(policy, token, client); });
    }
}

// ===================== WS 中枢（C# WsHub 扇出） =====================

void ApiService::hub_push(HubKind k, std::string json) {
    std::lock_guard<std::mutex> lk(hub_mu_);
    if (hub_stop_) return;
    if (hub_q_.size() >= 20000) {
        // 中枢跟不上（不应发生）：不静默丢，清空并让全部连接按 seq 重同步（C# OnMiscOverflow）
        hub_q_.clear();
        if (srv_)
            for (auto& s : srv_->ws_sessions()) s->close(1008, "resync required");
        return;
    }
    hub_q_.push_back(HubItem{k, std::make_shared<const std::string>(std::move(json))});
    hub_cv_.notify_one();
}

namespace {
struct WsState {
    std::atomic<bool> telemetry{true}, events{true}, alarms{true}, progress{false};
    std::string token;
    bool local = false;
};
WsState* state_of(const net::WsSession& s) { return static_cast<WsState*>(s.user.get()); }
}  // namespace

void ApiService::hub_loop() {
    while (true) {
        std::deque<HubItem> items;
        ctl::SnapshotStore::Ptr frame;
        {
            std::unique_lock<std::mutex> lk(hub_mu_);
            hub_cv_.wait(lk, [this] { return hub_stop_ || !hub_q_.empty() || hub_frame_; });
            if (hub_stop_) return;
            items.swap(hub_q_);
            frame.swap(hub_frame_);
        }
        if (!srv_) {
            if (frame) on_frame(*frame);
            continue;
        }
        for (const auto& it : items) {
            switch (it.kind) {
                case HubKind::Event:
                    srv_->ws_broadcast(it.json, net::WsQueue::Event, [](const net::WsSession& s) {
                        auto st = state_of(s);
                        return st && st->events.load();
                    });
                    break;
                case HubKind::Alarm:
                    srv_->ws_broadcast(it.json, net::WsQueue::Event, [](const net::WsSession& s) {
                        auto st = state_of(s);
                        return st && st->alarms.load();
                    });
                    break;
                case HubKind::Control:
                    srv_->ws_broadcast(it.json, net::WsQueue::Event, [](const net::WsSession& s) { return state_of(s) != nullptr; });
                    break;
                case HubKind::Local:
                    srv_->ws_broadcast(it.json, net::WsQueue::Event, [](const net::WsSession& s) {
                        auto st = state_of(s);
                        return st && st->local;
                    });
                    break;
            }
        }
        if (frame) {
            on_frame(*frame);
            if (ws_count_.load() == 0) continue;
            auto json = std::make_shared<const std::string>(dump(augment_frame(*frame)));
            srv_->ws_broadcast(json, net::WsQueue::Telemetry, [](const net::WsSession& s) {
                auto st = state_of(s);
                return st && st->telemetry.load();
            });
            // 接口契约 §2.2：每个周期整份推一次（订阅了 progress 的连接）
            auto prog = std::make_shared<const std::string>(dump(progress_json(*frame)));
            srv_->ws_broadcast(prog, net::WsQueue::Telemetry, [](const net::WsSession& s) {
                auto st = state_of(s);
                return st && st->progress.load();
            });
        }
    }
}

void ApiService::ws_open(const net::WsSessionPtr& s, const net::Request& q) {
    auto st = std::make_shared<WsState>();
    st->token = q.auth.name;
    st->local = q.auth.local;
    s->user = st;
    ws_count_.fetch_add(1);
    s->send(dump(hello_json()), net::WsQueue::Event);
    if (auto cur = snap()) s->send(dump(augment_frame(*cur)), net::WsQueue::Telemetry);   // 不必等下一个周期
}

int ApiService::ws_replay(const net::WsSessionPtr& s, int64_t after) {
    auto r = d_.events->resync(after, 1000);
    if (r.gap) {
        s->send(dump(ojson{{"type", "gap"}, {"reason", "gap_too_large"}, {"eventSeqHead", r.head}, {"oldestBufferedSeq", r.oldest_buffered}}));
        return 0;
    }
    for (const auto& e : r.items) {
        ojson j = ojson::parse(store::event_to_json(e), nullptr, false);
        ojson m;
        m["type"] = "event";
        if (!j.is_discarded())
            for (auto it = j.begin(); it != j.end(); ++it) m[it.key()] = it.value();
        s->send(dump(m));
    }
    return static_cast<int>(r.items.size());
}

void ApiService::ws_message(const net::WsSessionPtr& s, const std::string& text) {
    auto st = state_of(*s);
    nlohmann::json root = nlohmann::json::parse(text, nullptr, false);
    if (root.is_discarded() || !root.is_object()) {
        if (root.is_discarded()) {
            s->send(dump(ojson{{"type", "error"}, {"code", "BAD_REQUEST"}, {"message", "消息不是合法 JSON"}}));
            return;
        }
    }
    std::string type = root.is_object() && root.contains("type") && root["type"].is_string() ? root["type"].get<std::string>() : "";
    auto num64 = [&](const char* k) -> std::optional<int64_t> {
        if (!root.is_object() || !root.contains(k) || !root[k].is_number_integer()) return std::nullopt;
        return root[k].get<int64_t>();
    };
    if (type == "ping") {
        s->send(dump(ojson{{"type", "pong"}, {"ts", utc_iso(now_wall())}}));
    } else if (type == "hb") {
        if (root.contains("lease") && root["lease"].is_string() && !root["lease"].get<std::string>().empty()) {
            auto hb = lease_.heartbeat(root["lease"].get<std::string>(), st ? st->token : "", now_mono());
            if (!hb.ok) s->send(dump(ojson{{"type", "error"}, {"code", hb.code}, {"message", hb.message}}));
        }
    } else if (type == "subscribe") {
        std::vector<std::string> topics;
        if (root.contains("topics") && root["topics"].is_array())
            for (const auto& x : root["topics"])
                if (x.is_string()) topics.push_back(x.get<std::string>());
        auto has = [&](const char* t) {
            for (const auto& x : topics)
                if (x == t) return true;
            return false;
        };
        if (!topics.empty() && st) {
            st->telemetry = has("telemetry");
            st->events = has("events");
            st->alarms = has("alarms");
            st->progress = has("progress");
        }
        int replayed = 0;
        if (auto after = num64("resumeAfterEventSeq")) {
            auto ep = num64("seqEpoch");
            if (ep && *ep != d_.events->seq_epoch())
                s->send(dump(ojson{{"type", "gap"}, {"reason", "epoch_changed"}, {"eventSeqHead", d_.events->head()},
                                   {"oldestBufferedSeq", d_.events->oldest_buffered()}}));
            else replayed = ws_replay(s, *after);
        }
        ojson t = ojson::array();
        if (st && st->telemetry) t.push_back("telemetry");
        if (st && st->events) t.push_back("events");
        if (st && st->alarms) t.push_back("alarms");
        if (st && st->progress) t.push_back("progress");
        s->send(dump(ojson{{"type", "subscribed"}, {"topics", t}, {"replayed", replayed}, {"eventSeqHead", d_.events->head()}}));
    } else if (type == "resync") {
        int64_t after = num64("afterSeq").value_or(0);
        int n = ws_replay(s, after);
        s->send(dump(ojson{{"type", "resynced"}, {"topics", ojson::array()}, {"replayed", n}, {"eventSeqHead", d_.events->head()}}));
    } else if (type == "unsubscribe") {
        if (st) st->telemetry = st->events = st->alarms = st->progress = false;
    } else {
        s->send(dump(ojson{{"type", "error"},
                           {"code", "BAD_REQUEST"},
                           {"message", "未知消息类型「" + type + "」（支持 subscribe / unsubscribe / resync / ping / hb）"}}));
    }
}

void Routes::ws(ApiService& s, net::Router& r) {
    net::WsHandlers h;
    h.on_open = [&s](const net::WsSessionPtr& ws, const net::Request& q) { s.ws_open(ws, q); };
    h.on_message = [&s](const net::WsSessionPtr& ws, const std::string& text, bool binary) {
        if (!binary) s.ws_message(ws, text);
    };
    h.on_close = [&s](const net::WsSessionPtr& ws, uint16_t, const std::string&) {
        if (ws->user) s.ws_count_.fetch_sub(1);
    };
    r.ws("/api/v1/ws", h);
}

// ===================== DTO =====================

ojson ApiService::hello_json() {
    auto cur = snap();
    ojson m;
    m["type"] = "hello";
    m["apiVersion"] = kApiVersion;
    m["serverTime"] = utc_iso(now_wall());
    {
        std::lock_guard<std::mutex> lk(probe_mu_);
        if (!clock_probed_) {
            clock_synced_ = probe_clock_synced();
            clock_probed_ = true;
        }
        m["clockSynced"] = clock_synced_ ? ojson(*clock_synced_) : ojson(nullptr);
    }
    m["bootId"] = boot_id_;
    m["simulated"] = d_.hw ? d_.hw->simulated : false;
    m["seqEpoch"] = d_.events->seq_epoch();
    m["eventSeqHead"] = d_.events->head();
    m["telemetrySeqHead"] = cur ? cur->seq : 0;
    m["control"] = mode();
    m["eventRingSize"] = store::EventStore::kRingSize;
    return m;
}

ojson ApiService::control_state_json() {
    auto s = lease_.snapshot(now_mono());
    ojson m = mode();
    ojson j;
    j["mode"] = m["control"];
    j["holder"] = m["holder"];
    j["holderToken"] = s.has_lease ? ojson(s.token) : ojson(nullptr);
    j["state"] = cmd::lease_state_word(s.state);
    j["leaseExpiresAt"] = opt_str(utc_of_mono(s.lease_expires_at));
    j["leaseHardExpiresAt"] = opt_str(utc_of_mono(s.hard_expires_at));
    j["pending"] = s.state == cmd::LeaseState::Requested
                       ? ojson{{"requestId", s.request_id.value_or("")},
                               {"clientName", s.client},
                               {"tokenName", s.token},
                               {"expiresAt", opt_str(utc_of_mono(s.request_deadline))}}
                       : ojson(nullptr);
    j["autoGrant"] = d_.cfg.auto_grant_remote;
    j["heartbeatTimeoutSec"] = d_.cfg.heartbeat_timeout_sec;
    j["leaseGraceSec"] = d_.cfg.lease_grace_sec;
    j["requestTimeoutSec"] = d_.cfg.control_request_timeout_sec;
    j["onRemoteLost"] = d_.cfg.on_remote_lost;
    return j;
}

ojson ApiService::request_result_json(const cmd::RequestOutcome& r) {
    ojson j;
    j["requestId"] = r.request_id;
    if (r.lease) {
        j["result"] = "granted";
        j["expiresAt"] = nullptr;
        j["leaseId"] = r.lease->lease_id;
        j["leaseExpiresAt"] = opt_str(utc_of_mono(r.lease->expires_at));
    } else {
        j["result"] = "pending";
        j["expiresAt"] = opt_str(utc_of_mono(r.deadline));
        j["leaseId"] = nullptr;
        j["leaseExpiresAt"] = nullptr;
    }
    j["heartbeatTimeoutSec"] = d_.cfg.heartbeat_timeout_sec;
    j["leaseGraceSec"] = d_.cfg.lease_grace_sec;
    j["requestKey"] = r.secret.empty() ? ojson(nullptr) : ojson(r.secret);
    return j;
}

ojson ApiService::request_status_json(const cmd::RequestStatus& s) {
    ojson j;
    j["requestId"] = s.request_id;
    j["result"] = cmd::request_result_word(s.result);
    j["clientName"] = s.client;
    j["expiresAt"] = opt_str(utc_of_mono(s.deadline));
    j["leaseId"] = s.lease ? ojson(s.lease->lease_id) : ojson(nullptr);
    j["leaseExpiresAt"] = s.lease ? opt_str(utc_of_mono(s.lease->expires_at)) : ojson(nullptr);
    j["heartbeatTimeoutSec"] = d_.cfg.heartbeat_timeout_sec;
    j["leaseGraceSec"] = d_.cfg.lease_grace_sec;
    return j;
}

ojson ApiService::device_json() {
    auto cur = snap();
    std::vector<int> installed = cur ? cur->installed : std::vector<int>{1, 2, 3, 4, 5, 6, 7, 8};
    ojson chans = ojson::array(), inst = ojson::array();
    for (int i = 1; i <= 8; ++i) {
        bool in = false;
        for (int x : installed) in = in || x == i;
        chans.push_back(ojson{{"id", i}, {"installed", in}});
    }
    for (int x : installed) inst.push_back(x);
    bool sim = d_.hw && d_.hw->simulated;
    auto module = [&](bool configured, const std::string& port, int baud, ojson fs) {
        return ojson{{"configured", configured}, {"port", sim ? ojson("sim") : (port.empty() ? ojson(nullptr) : ojson(port))},
                     {"baud", port.empty() && !sim ? ojson(nullptr) : ojson(baud)}, {"fullScaleMPa", fs}};
    };
    ojson hw;
    if (d_.hw) {
        const auto& c = d_.hw->cfg;
        hw["temp"] = module(d_.hw->temp_configured(), c.temp.port, c.temp.baud, nullptr);
        hw["pressure"] = module(d_.hw->press_configured(), c.press.port, c.press.baud,
                                cur ? jnum(cur->settings.settings.press_full_scale_mpa) : ojson(nullptr));
        hw["stir"] = module(d_.hw->stir_configured(), c.stir.port, c.stir.baud, nullptr);
        hw["io"] = module(d_.hw->io_configured(), c.io.port, c.io.baud, nullptr);
    } else {
        ojson none{{"configured", false}, {"port", nullptr}, {"baud", nullptr}, {"fullScaleMPa", nullptr}};
        hw = ojson{{"temp", none}, {"pressure", none}, {"stir", none}, {"io", none}};
    }
    ojson j;
    j["model"] = kModel;
    j["serial"] = opt_str(d_.cfg.serial);
    j["hmiVersion"] = kDaemonVersion;
    j["apiVersion"] = kApiVersion;
    j["channelCount"] = 8;
    j["channels"] = chans;
    j["installedChannels"] = inst;
    j["hardware"] = hw;
    j["units"] = ojson{{"temperature", "C"}, {"pressure", "psi"}, {"speed", "rpm"}};
    j["simulated"] = sim;
    j["simRefused"] = d_.hw ? d_.hw->sim_refused : false;
    j["bootTime"] = utc_iso(boot_wall_ms_);
    j["bootId"] = boot_id_;
    {
        std::lock_guard<std::mutex> lk(probe_mu_);
        if (!clock_probed_) {
            clock_synced_ = probe_clock_synced();
            clock_probed_ = true;
        }
        j["clockSynced"] = clock_synced_ ? ojson(*clock_synced_) : ojson(nullptr);
    }
    j["eventSeqHead"] = d_.events->head();
    j["seqEpoch"] = d_.events->seq_epoch();
    // 接口契约 §3.13：阀门 / 压力的接线（IO16R 线圈下标、8AI 通道号）
    j["io"] = ojson{{"inletCoils", ojson::array({8, 9, 10})}, {"ventCoil", 11}, {"svCoils", ojson::array({0, 1, 2, 3, 4, 5, 6, 7})},
                    {"aiChannels", ojson::array({1, 2, 3, 4, 5, 6, 7, 8})}, {"ioModel", "IO16R"}, {"aiModel", "8AI"}};
    return j;
}

ojson ApiService::settings_json(const store::SettingsState& ss) const {
    const auto& s = ss.settings;
    ojson j;
    j["limits"] = ojson{{"tempSpMaxC", jnum(s.t_max_sp)}, {"pressureSpMaxPsi", jnum(s.p_max_sp)}, {"rpmMax", jnum(s.rpm_max)},
                        {"volumeMaxMl", jnum(s.vol_max)}};
    j["alarms"] = ojson{{"overPressurePsi", jnum(s.over_pressure)}, {"overTempC", jnum(s.over_temp)},
                        {"stopHeatOnOverPressure", s.stop_heat_on_over_pressure}, {"heatTimeoutMin", jnum(s.heat_timeout)},
                        {"pressDeviationPsi", jnum(s.press_deviation)}};
    j["leakTest"] = ojson{{"testPsi", jnum(s.leak_test_psi)}, {"settleMin", jnum(s.leak_settle_min)}, {"measureMin", jnum(s.leak_measure_min)},
                          {"rateLimitPsiPerHr", jnum(s.leak_rate)}, {"reminderDays", jnum(s.leak_reminder_days)},
                          {"lastTestUtc", opt_utc(s.last_leak_test_utc_ms)}};
    j["approach"] = ojson{{"deltaC", jnum(s.approach_delta)}, {"rateCPerMin", jnum(s.approach_rate)}, {"fastRateCPerMin", jnum(s.approach_fast_rate)},
                          {"finalZoneC", jnum(s.approach_final_zone)}, {"settleBandC", jnum(s.approach_settle_band)},
                          {"settleSec", jnum(s.approach_settle_sec)}};
    j["stir"] = ojson{{"peakCurrentA", jnum(s.stir_current)}, {"standbyPct", jnum(s.stir_standby_pct)}, {"rampMsPer1000Rpm", jnum(s.stir_ramp_ms)},
                      {"microstep", jnum(s.stir_microstep)}, {"stirGearRatio", jnum(s.stir_gear_ratio)}};
    j["pressure"] = ojson{{"fullScaleMPa", jnum(s.press_full_scale_mpa)}};
    ojson bands = ojson::array();
    auto br = store::band_ranges(ss.band_bounds);
    for (size_t i = 0; i < br.size(); ++i) bands.push_back(ojson{{"index", i}, {"loC", jnum(br[i].lo)}, {"hiC", jnum(br[i].hi)}});
    j["tempBands"] = bands;
    // C# 的 unused 里 heatTimeoutMin / pressDeviationPsi 新后端已实现（三级警告），不再列入
    j["notes"] = ojson{{"unused", ojson::array({"reminderDays", "pressureSpMaxPsi"})}};
    j["revision"] = ss.revision;
    ojson eff = ojson::array(), cal = ojson::array();
    for (size_t i = 0; i < 8; ++i) {
        eff.push_back(opt_num(s.effective_volume[i]));
        cal.push_back(opt_utc(s.volume_calib_utc_ms[i]));
    }
    j["volume"] = ojson{{"vesselVolume", jnum(s.vessel_volume)}, {"gasLineVolume", jnum(s.gas_line_volume)},
                        {"manifoldVolume", opt_num(s.manifold_volume)}, {"effectiveVolume", eff}, {"volumeCalibratedAt", cal}};
    // —— 新增（PC 端忽略未知字段）：界面语言、全部设置项的当前值（PATCH /settings 的键名）、档位分界 ——
    j["language"] = cmd::wire_language(s.language);
    ojson values;
    for (const auto& spec : store::setting_specs::all()) values[spec.name] = jnum(s.*(spec.field));
    j["values"] = values;
    j["bandBounds"] = ojson::array({jnum(ss.band_bounds[0]), jnum(ss.band_bounds[1])});
    // 接口契约 §3.1
    j["ui"] = ojson{{"pressureUnit", s.ui_pressure_unit}, {"tempUnit", s.ui_temp_unit}, {"theme", s.ui_theme}, {"keyboard", s.ui_keyboard},
                    {"preflightChecklist", s.ui_preflight_checklist}};
    j["program"] = ojson{{"gasWaitMaxMin", jnum(s.program_gas_wait_min)}};
    j["storage"] = ojson{{"retentionDays", jnum(s.storage_retention_days)}};
    return j;
}

ojson ApiService::pid_json(const ctl::Snapshot& s) const {
    static const char* kNames[] = {"低温档", "中温档", "高温档"};
    auto br = store::band_ranges(s.settings.band_bounds);
    ojson bands = ojson::array();
    for (size_t i = 0; i < br.size(); ++i)
        bands.push_back(ojson{{"index", i}, {"name", kNames[i]}, {"loC", jnum(br[i].lo)}, {"hiC", jnum(br[i].hi)}});
    ojson chans = ojson::array();
    for (int id = 1; id <= 8; ++id) {
        size_t ci = static_cast<size_t>(id - 1);
        bool tuning = s.channels[ci].autotuning;
        ojson tune_band = nullptr, tune_sp = nullptr;
        if (tuning && s.autotune.is_object() && s.autotune.contains("channels"))
            for (const auto& a : s.autotune["channels"])
                if (a.value("channel", 0) == id && a.value("running", false)) {
                    if (a.contains("band") && a["band"].is_number_integer() && a["band"].get<int>() >= 0) tune_band = a["band"];
                    if (a.contains("spC") && a["spC"].is_number()) tune_sp = a["spC"];
                }
        ojson vals = ojson::array();
        for (size_t b = 0; b < store::kBandCount; ++b) {
            const auto& p = s.settings.band_pids[ci][b];
            const auto& m = s.settings.band_tune[ci][b];
            vals.push_back(ojson{{"index", b}, {"p", jnum(p.p)}, {"i", jnum(p.i)}, {"d", jnum(p.d)},
                                 {"tunedSpC", m ? opt_num(m->tuned_sp_c) : ojson(nullptr)},
                                 {"tunedAt", m ? opt_utc(m->tuned_at_ms) : ojson(nullptr)},
                                 {"manualEditedAt", m ? opt_utc(m->manual_edited_at_ms) : ojson(nullptr)}});
        }
        // 接口契约 §3.2：振荡周期数仪表不回读（0），进度按 30 min 估
        double el = 0;
        if (tuning && s.autotune.is_object() && s.autotune.contains("channels"))
            for (const auto& a : s.autotune["channels"])
                if (a.value("channel", 0) == id && a.contains("elapsedSec") && a["elapsedSec"].is_number()) el = a["elapsedSec"].get<double>();
        chans.push_back(ojson{{"id", id}, {"autotuning", tuning}, {"tuneBand", tune_band}, {"tuneSpC", tune_sp}, {"bands", vals},
                              {"tuneCycle", 0}, {"tuneCycles", 0},
                              {"tuneLeftSec", tuning ? jnum(std::max(0.0, std::round(1800 - el))) : ojson(nullptr)},
                              {"tuneFraction", tuning ? jnum(std::round(std::min(0.99, el / 1800.0) * 100) / 100) : ojson(0)}});
    }
    return ojson{{"seq", s.seq}, {"ts", utc_iso(s.ts_ms)}, {"bands", bands}, {"channels", chans}, {"controller", "宇电 AI-8"}};
}

std::string ApiService::schema_json() {
    auto cur = snap();
    int64_t rev = cur ? cur->settings.revision : 0;
    std::lock_guard<std::mutex> lk(schema_mu_);
    if (cur && rev == schema_rev_ && !schema_cache_.empty()) return schema_cache_;
    core::program::ProgramLimitSettings lim{200, 250, 450, 500, 510};
    double leak_rate = core::program::step_specs::kLeakRateDefault;
    if (cur) {
        const auto& s = cur->settings.settings;
        lim = core::program::ProgramLimitSettings{s.t_max_sp, s.over_temp, s.rpm_max, s.p_max_sp, s.over_pressure};
        leak_rate = s.leak_rate;
    }
    std::string out = dump(core::program::program_schema::build(lim, rev, leak_rate));
    if (cur) {
        schema_cache_ = out;
        schema_rev_ = rev;
    }
    return out;
}

// ===================== 写命令公共管线 =====================

namespace {
struct Pending {
    std::mutex mu;
    std::condition_variable cv;
    bool started = false, cancelled = false, head_done = false, done = false;
    ctl::OpResult result;
    std::function<void(const ctl::OpResult&)> late;
};
}  // namespace

net::Response ApiService::command(const net::Request& q, const WriteSpec& spec, const Binder& bind) {
    const auto& auth = q.auth;
    const bool local = auth.local;
    const std::string method = q.method, path = q.path;
    const std::string key = trim(q.header(net::headers::kIdempotency));
    const std::string op_id = key.empty() ? cmd::new_guid() : key;
    const std::string ip = local ? "local" : (q.peer.ip_text.empty() ? "-" : q.peer.ip_text);
    const std::string lease_header = trim(q.header(net::headers::kLease));
    const std::string lease_tag = lease_header.empty() ? "-" : lease_header.size() > 8 ? lease_header.substr(0, 8) + "…" : lease_header;
    const std::string source = local ? "local" : "remote";
    const std::string who = local ? "触屏命令 " : "远程命令 ";
    const std::string kind = "op." + spec.action;
    std::string client = local ? "触屏" : auth.name;
    net::IdemEntryPtr entry;
    bool owned = false;
    const std::string& raw = q.body;

    auto detail = [&](const std::string& message) {
        std::string who_part = local ? std::string("触屏") : client + "（令牌 " + auth.name + " · ip " + ip + " · 租约 " + lease_tag + "）";
        std::string shown = spec.redact_body ? std::string("（正文含密码，不记录）") : trunc200(raw);
        return method + " " + path + " · " + who_part + " · " + shown + (message.empty() ? "" : " · " + message);
    };
    auto publish = [this, source](const std::string& op, const std::string& action, const std::string& cl, const CommandHttp& h,
                                  int64_t seq) {
        hub_push(HubKind::Control, dump(ojson{{"type", "opEvent"}, {"opId", op}, {"action", action}, {"client", cl}, {"source", source},
                                              {"httpStatus", h.status}, {"status", h.word}, {"code", h.code}, {"message", h.message},
                                              {"opEventSeq", seq}, {"ts", utc_iso(now_wall())}}));
    };
    auto reject = [&](const std::string& code, const std::string& message, const std::string& in_progress_op = "",
                      const ojson& rdetail = nullptr) {
        const std::string& op = in_progress_op.empty() ? op_id : in_progress_op;
        int64_t seq = audit("warn", who + spec.action + " · " + std::to_string(net::status_of(code)) + " " + code, detail(message), source,
                            client, op, kind);
        auto h = error_http(op, code, message, rdetail, seq);
        if (owned && entry) idem_.abandon(entry);   // 未执行就被拒：不缓存，修正后同键重发可以正常执行
        return json_response(h.status, h.body);
    };

    // 0. 来源标签：带了有效租约就用申请时的客户端名
    if (!local && !lease_header.empty()) {
        auto pre = lease_.validate(lease_header, auth.name, now_mono());
        if (pre.ok) client = pre.client;
    }
    // 1. 角色 / 急停权限位
    if (!local) {
        if (spec.estop) {
            if (!auth.estop) return reject(oc::kForbiddenRole, "该令牌没有急停权限（estop）");
        } else if (!auth.has_role(spec.role) && !(spec.cls == cmd::ActionClass::Stop && auth.estop)) {
            return reject(oc::kForbiddenRole, spec.role == net::Role::Admin              ? "该操作需要 admin 角色"
                                              : spec.cls == cmd::ActionClass::Stop ? "停止类命令需要 operator 角色或 estop 权限位"
                                                                                   : "viewer 令牌只能读取 · 写命令需要 operator 及以上角色");
        }
    }
    // 2. 正文
    auto body = parse_body(raw);
    if (!body.error.empty()) return reject(oc::kBadRequest, body.error);
    // 3. 幂等（按令牌隔离）
    if (!key.empty()) {
        if (key.size() > net::kIdemKeyMaxLen) return reject(oc::kBadRequest, "Idempotency-Key 过长（≤ 128 字符）");
        auto a = idem_.acquire(net::IdempotencyStore::scoped_key(auth.name, key), net::IdempotencyStore::hash(method, path, raw), op_id,
                               now_mono());
        switch (a.kind) {
            case net::IdemBegin::New:
                entry = a.entry;
                owned = true;
                break;
            case net::IdemBegin::Mismatch:
                return reject(oc::kIdempotencyMismatch, "同一个 Idempotency-Key 带了不同的请求（方法 / 路径 / 正文）");
            case net::IdemBegin::InProgress:
                return reject(oc::kIdempotencyInProgress, "同键命令仍在执行 · 请稍后用同一 Idempotency-Key 查询", a.entry ? a.entry->op_id : "");
            case net::IdemBegin::Replay: {
                auto resp = json_response(a.entry->status, a.entry->body);
                resp.headers.set(net::headers::kIdempotentReplay, "true");
                return resp;
            }
        }
    } else if (spec.idem_required) {
        return reject(oc::kPreconditionRequired, method + " " + path + " 非幂等 · 必须带 Idempotency-Key（§7.1）");
    }
    // 4. 动作类 → 租约（远程控制类）。本地：仲裁在命令层 admit 里判（远程控制中 → MODE_REMOTE）
    if (spec.cls == cmd::ActionClass::Control && !local) {
        auto chk = lease_.validate(lease_header, auth.name, now_mono());
        if (!chk.ok) return reject(chk.code, chk.message);
        client = chk.client;
    }
    cmd::Source src = local ? cmd::Source::local() : cmd::Source::remote(auth.name, client);
    // 5. 参数绑定
    Bound b = bind(body.ptr(), q, src);
    if (!b.code.empty()) return reject(b.code, b.message, "", b.detail);

    // 6. 执行
    enum class Kind { Completed, Unconfirmed, UiTimeout, Unknown } outcome = Kind::Completed;
    ctl::OpResult result;
    auto st = std::make_shared<Pending>();
    auto exec = b.exec;
    auto finish = [st](const ctl::OpResult& r) {
        std::function<void(const ctl::OpResult&)> late;
        {
            std::lock_guard<std::mutex> lk(st->mu);
            if (st->done) return;
            st->done = true;
            st->result = r;
            late = st->late;
        }
        if (late) late(r);
        st->cv.notify_all();
    };
    std::string action = spec.action;
    auto guarded = [this, st, exec, src, action, op_id, finish]() {
        try {
            exec(commands_, src, finish);
        } catch (const std::exception& ex) {
            audit("err", "远程命令 " + action + " · 执行异常", ex.what(), "remote", std::nullopt, op_id, "op." + action + ".exception");
            finish(ctl::OpResult::fail(oc::kInternal, "命令执行异常 · 详见事件记录（按 opId 查）"));
        }
        {
            std::lock_guard<std::mutex> lk(st->mu);
            st->head_done = true;
        }
        st->cv.notify_all();
    };
    if (spec.inline_exec) {
        {
            std::lock_guard<std::mutex> lk(st->mu);
            st->started = true;
        }
        guarded();
    } else {
        bool posted = d_.core->post([st, guarded] {
            {
                std::lock_guard<std::mutex> lk(st->mu);
                if (st->cancelled) return;
                st->started = true;
            }
            guarded();
        });
        if (!posted) {
            std::lock_guard<std::mutex> lk(st->mu);
            st->cancelled = true;
        }
    }
    {
        std::unique_lock<std::mutex> lk(st->mu);
        if (!st->started && !st->cancelled) st->cv.wait_for(lk, std::chrono::seconds(5), [&] { return st->started || st->head_done; });
        if (!st->started) {
            st->cancelled = true;
            outcome = Kind::UiTimeout;   // 确定未执行
        } else {
            if (!st->head_done) st->cv.wait_for(lk, std::chrono::seconds(5), [&] { return st->head_done; });
            if (!st->head_done && !st->done) outcome = Kind::Unknown;
            else if (!st->done) {
                st->cv.wait_for(lk, std::chrono::seconds(2), [&] { return st->done; });
                if (!st->done) outcome = Kind::Unconfirmed;
            }
            if (outcome == Kind::Completed) result = st->result;
        }
    }

    // 7. 审计 op.<action>（seq = opEventSeq）→ 响应 → opEvent → 幂等表
    auto build = [&](int64_t seq) {
        switch (outcome) {
            case Kind::Unconfirmed: return unconfirmed_http(op_id, seq);
            case Kind::UiTimeout:
                return error_http(op_id, oc::kUiThreadTimeout, "控制线程 5 s 内未响应，命令已撤销、确定未执行，可重试", nullptr, seq);
            case Kind::Unknown:
                return error_http(op_id, oc::kOutcomeUnknown, "命令已进入控制线程但 5 s 内未执行完 · 不得直接重发，请用同一 Idempotency-Key 查询结果",
                                  nullptr, seq);
            default: return result_http(result, op_id, seq);
        }
    };
    auto preview = build(0);
    int64_t seq = audit(preview.ok ? "info" : "warn", who + spec.action + " · " + summary(preview), detail(preview.message), source, client,
                        op_id, kind);
    auto http = build(seq);
    publish(op_id, spec.action, client, http, seq);

    if (outcome == Kind::Unconfirmed || outcome == Kind::Unknown) {
        // 最终结果回来后记 op.<action>.result（同一 opId）、推第二条 opEvent、回填幂等表
        std::string token = auth.name, cl = client, who_s = who, src_word = source;
        std::string tail_who = local ? std::string("触屏") : cl + "（令牌 " + token + " · ip " + ip + " · 租约 " + lease_tag + "）";
        auto e = entry;
        auto late = [this, e, op_id, cl, action, who_s, src_word, kind, tail_who, publish](const ctl::OpResult& r) {
            auto fin = result_http(r, op_id, 0);
            int64_t s2 = audit(r.ok ? "info" : "warn", who_s + action + " · 最终结果 " + summary(fin),
                               tail_who + " · " + r.code + (r.message.empty() ? "" : " · " + r.message), src_word, cl, op_id, kind + ".result");
            fin = result_http(r, op_id, s2);
            publish(op_id, action + ".result", cl, fin, s2);
            if (e) idem_.complete(e, fin.status, fin.body);
        };
        bool already = false;
        ctl::OpResult done_r;
        {
            std::lock_guard<std::mutex> lk(st->mu);
            if (st->done) {
                already = true;
                done_r = st->result;
            } else {
                st->late = late;
            }
        }
        if (already) late(done_r);
        return json_response(http.status, http.body);
    }
    if (entry) idem_.complete(entry, http.status, http.body);
    return json_response(http.status, http.body);
}

void ApiService::register_routes(net::Router& r) {
    // 组内其余路径（含 TCP 上的本机专用路由、方法不对）：统一 404 JSON（C# MapFallback）
    r.set_fallback([](const net::Request&, net::Responder res) { res.send(api_error("NOT_FOUND", "没有这个端点")); });
    Routes::read(*this, r);
    Routes::control(*this, r);
    Routes::write(*this, r);
    Routes::local(*this, r);
    Routes::extras(*this, r);
    Routes::system(*this, r);
    Routes::ws(*this, r);
}

}  // namespace reactor::api
