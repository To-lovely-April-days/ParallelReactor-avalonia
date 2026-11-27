#include "backend.hpp"

#include "wire.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

namespace reactor::hmi::net {

namespace {
constexpr int64_t kTrendStepMs = 10000;            // 曲线每 10 s 一点（和模拟源一致）
constexpr int64_t kTrendSpanMs = 6 * 3600 * 1000;  // 连上时先取最近 6 h
constexpr int64_t kTrendKeepMs = 24 * 3600 * 1000; // 最多留 24 h
constexpr size_t kEventsKeep = 500;
constexpr size_t kAlarmsKeep = 300;
constexpr int kRunDetails = 20;                    // 取详情（含每分钟吸气量）的最近几条运行记录

const char* group_name(NetBackend::G g) {
    static const char* n[] = {"settings", "schema", "device", "alarms", "programs", "library", "templates", "pid", "comm", "network",
                              "usb", "storage", "users", "runs", "files", "leak", "leakSamples", "calib", "trend"};
    return n[g];
}
}  // namespace

NetBackend::NetBackend(Endpoint ep, std::string token, model::ModelMailbox& mailbox, UiQueue& ui)
    : ep_(ep), mailbox_(mailbox), ui_(ui) {
    client_ = std::make_unique<NetClient>(ep, std::move(token), this);
    actions_ = std::make_unique<NetActions>(*this);
    m_.connected = false;
    m_.alarmsLoaded = false;
    m_.snap.channels.clear();
    pad_channels(m_.snap.channels);
    m_.linkStatus = "正在连接 " + ep.text();
    m_.snap.ts = wall_ms();
    for (size_t i = 0; i < 8; ++i) m_.x.pid.channels[i].id = static_cast<int>(i + 1);
}

NetBackend::~NetBackend() { stop(); }

void NetBackend::start() {
    {
        std::lock_guard<std::mutex> lk(mu_);
        publish_locked();
    }
    client_->start();
}

void NetBackend::stop() { client_->stop(); }

model::Actions& NetBackend::actions() { return *actions_; }

std::shared_ptr<const model::Model> NetBackend::model_copy() const {
    std::lock_guard<std::mutex> lk(mu_);
    return std::make_shared<const model::Model>(m_);
}

void NetBackend::publish_locked() { mailbox_.post(std::make_shared<const model::Model>(m_)); }

void NetBackend::invalidate(G g) {
    std::lock_guard<std::mutex> lk(mu_);
    grp_[g].dirty = true;
}

void NetBackend::refetch_now(G g) {
    {
        std::lock_guard<std::mutex> lk(mu_);
        auto& s = grp_[g];
        if (!connected_ || s.inflight) {
            s.dirty = true;
            return;
        }
        s.dirty = false;
        s.inflight = true;
        s.last = mono_ms();
    }
    fetch(g);
}

void NetBackend::get(const std::string& path, std::function<void(const Reply&)> fn, int timeout_ms) {
    Call c;
    c.method = "GET";
    c.path = path;
    c.timeout_ms = timeout_ms;
    c.done = std::move(fn);
    client_->call(Lane::Read, std::move(c));
}

// ============================================================ WS
void NetBackend::on_ws_open() {
    std::lock_guard<std::mutex> lk(mu_);
    m_.linkStatus = "已连上 " + ep_.text() + "，等后台的第一帧数据";
    publish_locked();
}

void NetBackend::on_ws_closed(const std::string& why) {
    std::lock_guard<std::mutex> lk(mu_);
    connected_ = false;
    m_.connected = false;
    m_.linkStatus = "和后台服务断开（" + why + "），正在重连 " + ep_.text();
    std::fprintf(stderr, "[net] 断开：%s\n", why.c_str());
    resyncing_ = false;
    publish_locked();
}

void NetBackend::send_subscribe() {
    json s = {{"type", "subscribe"}, {"topics", {"telemetry", "events", "alarms", "progress"}}};
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (last_seq_ > 0 && seq_epoch_ >= 0) {
            s["resumeAfterEventSeq"] = last_seq_;
            s["seqEpoch"] = seq_epoch_;
        }
    }
    client_->ws_send(s.dump());
}

void NetBackend::handle_hello(const json& j) {
    int epoch = static_cast<int>(j_int(j, "seqEpoch"));
    int64_t head = j_int(j, "eventSeqHead");
    std::string boot = j_str(j, "bootId");
    bool initial = false;
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (seq_epoch_ != epoch) {  // 纪元变了（或第一次连上）：本地记住的 seq 作废
            m_.events.clear();
            last_seq_ = 0;
            seq_epoch_ = epoch;
            events_loaded_ = false;
        }
        head_seq_ = head;
        boot_id_ = boot;
        initial = !events_loaded_;
        for (int g = 0; g < kGroups; ++g) grp_[g].dirty = true;
        schema_rev_ = -1;
    }
    send_subscribe();
    if (initial) fetch_events(std::max<int64_t>(0, head - 200), 1, false);
    // 连上之前就有的控制申请（WS 只推变化）
    get("/control", [this](const Reply& r) {
        if (r.ok() && r.json.is_object()) handle_control(r.json);
    });
}

void NetBackend::refresh_derived_locked() {
    // 泄漏测试能选的通道：和后台规则一致——停用（idle）或已结束（done），且没被程序占用（Leak-Setup）
    auto& sel = m_.x.leak.selectable;
    sel.clear();
    for (auto& c : m_.snap.channels)
        if ((c.state == "idle" || c.state == "done" || c.state == "stopped") && !c.programOwned) sel.push_back(c.id);
    // 正用着程序库哪一条（「正用在」）：后端没给 usedBy 时按 sourceId 自己算
    for (auto* lib : {&m_.x.library, &m_.x.templates})
        for (auto& e : *lib) {
            std::vector<int> used;
            for (int i = 0; i < 8; ++i)
                if (!e.prog.id.empty() && m_.x.programSrcId[static_cast<size_t>(i)] == e.prog.id) used.push_back(i + 1);
            if (!used.empty() || e.usedBy.empty()) e.usedBy = used;
        }
}

void NetBackend::handle_telemetry(const json& j) {
    model::TelemetrySnapshot s;
    SnapExt ext;
    if (!parse_snapshot(j, s, ext)) return;
    std::lock_guard<std::mutex> lk(mu_);
    bool autotune_before = false, autotune_now = false;
    for (auto& c : m_.snap.channels) autotune_before |= c.autotuning;
    for (auto& c : s.channels) autotune_now |= c.autotuning;
    bool leak_was = m_.snap.leakTest.running, calib_was = m_.snap.calib && m_.snap.calib->running;
    m_.snap = std::move(s);
    if (ext.hasChanset) m_.chanset = ext.chanset;
    else
        for (size_t i = 0; i < 8; ++i) {  // 后端还没给 bandC / antiOvershoot：只同步曲线段和加热状态
            m_.chanset[i].curve = ext.chanset[i].curve;
            m_.chanset[i].heatOn = ext.chanset[i].heatOn;
        }
    if (!ext.languageCode.empty()) m_.languageCode = ext.languageCode;
    if (ext.bootAt) m_.bootAt = ext.bootAt;
    // revs：变了的那几组重取
    have_revs_ = ext.hasRevs;
    if (ext.hasRevs) {
        static const std::map<std::string, std::vector<G>> kMap = {
            {"settings", {Settings, Schema}}, {"programs", {Programs}}, {"library", {Library, Templates}}, {"runs", {Runs}},
            {"files", {Files}}, {"leak", {Leak, LeakSamples}}, {"calib", {Calib}}, {"network", {Network}}, {"usb", {Usb}},
            {"users", {Users}}, {"device", {Device}}, {"pid", {Pid}}};
        for (auto& kv : ext.revs) {
            auto it = revs_.find(kv.first);
            if (it != revs_.end() && it->second == kv.second) continue;
            revs_[kv.first] = kv.second;
            auto mi = kMap.find(kv.first);
            if (mi != kMap.end())
                for (G g : mi->second) grp_[g].dirty = true;
        }
    }
    // 状态从「进行中」变成结束：结果页要最后一份数据
    if (leak_was && !m_.snap.leakTest.running) grp_[Leak].dirty = grp_[LeakSamples].dirty = true;
    if (calib_was && !(m_.snap.calib && m_.snap.calib->running)) grp_[Calib].dirty = true;
    if (autotune_before && !autotune_now) grp_[Pid].dirty = true;
    refresh_derived_locked();
    if (!connected_) {
        connected_ = true;
        m_.linkStatus.clear();
        std::fprintf(stderr, "[net] 已连上 %s\n", ep_.text().c_str());
    }
    m_.connected = true;
    publish_locked();
}

void NetBackend::add_events_locked(std::vector<model::EventRecord> ev) {
    auto& v = m_.events;
    for (auto& e : ev) {
        if (e.seq <= 0) continue;
        last_seq_ = std::max(last_seq_, e.seq);
        auto pos = std::lower_bound(v.begin(), v.end(), e.seq, [](const model::EventRecord& a, int64_t s) { return a.seq > s; });
        if (pos != v.end() && pos->seq == e.seq) continue;  // 重复（补发和实时流重叠）
        v.insert(pos, std::move(e));
    }
    if (v.size() > kEventsKeep) v.resize(kEventsKeep);
}

void NetBackend::handle_event(const model::EventRecord& e) {
    // 按 kind 让相关数据重取（后端给了 revs 时这里只是多一次保险，重复标脏没有副作用）
    const std::string& k = e.kind;
    auto starts = [&](const char* p) { return k.rfind(p, 0) == 0; };
    std::lock_guard<std::mutex> lk(mu_);
    if (starts("settings.") || starts("pid.bands")) grp_[Settings].dirty = grp_[Schema].dirty = true;
    if (starts("library.")) grp_[Library].dirty = grp_[Templates].dirty = true;
    if (starts("program.")) grp_[Programs].dirty = grp_[Runs].dirty = true;
    if (starts("leak.")) grp_[Leak].dirty = grp_[LeakSamples].dirty = true;
    if (starts("calib.")) grp_[Calib].dirty = true;
    if (starts("network.")) grp_[Network].dirty = true;
    if (starts("export.") || starts("usb.")) grp_[Usb].dirty = true;
    if (starts("pid.") || starts("autotune.")) grp_[Pid].dirty = true;
    if (starts("session.")) grp_[Files].dirty = grp_[Runs].dirty = true;
    // 导出 / WiFi 连接这类后台慢慢做完的，结果用 toast 告诉操作员
    if (starts("export.") || k == "network.wifiConnected" || k == "network.wifiFailed") {
        bool ok = e.level == "info";
        std::string msg = !e.message.empty() ? e.title + (e.title.empty() ? "" : "：") + e.message : e.title;
        auto* act = actions_.get();
        ui_.post([act, ok, msg] {
            if (act->on_result) act->on_result(ok, msg);
        });
    }
}

void NetBackend::fetch_events(int64_t after, int pages_left, bool resubscribe) {
    get("/events?afterSeq=" + std::to_string(after) + "&limit=1000", [this, after, pages_left, resubscribe](const Reply& r) {
        if (!r.ok()) {
            std::lock_guard<std::mutex> lk(mu_);
            resyncing_ = false;
            return;
        }
        int epoch = 0;
        int64_t head = 0;
        bool more = false;
        auto ev = parse_events_page(r.json, &epoch, &head, &more);
        int64_t next = after;
        for (auto& e : ev) next = std::max(next, e.seq);
        {
            std::lock_guard<std::mutex> lk(mu_);
            add_events_locked(std::move(ev));
            events_loaded_ = true;
            head_seq_ = head;
            publish_locked();
        }
        if (more && pages_left > 1 && next > after) {
            fetch_events(next, pages_left - 1, resubscribe);
            return;
        }
        if (resubscribe) {
            {
                std::lock_guard<std::mutex> lk(mu_);
                resyncing_ = false;
                // 补不完（缺口超过 5 页）：从最新一页接着走，不再要求服务端补发
                if (more) last_seq_ = std::max(last_seq_, head_seq_);
            }
            send_subscribe();
        }
    });
}

void NetBackend::resync_after_gap() {
    int64_t after;
    {
        std::lock_guard<std::mutex> lk(mu_);
        if (resyncing_) return;
        resyncing_ = true;
        // 缺口很大就只取最近 200 条（界面只留 500 条）
        after = head_seq_ - last_seq_ > 5000 ? std::max<int64_t>(0, head_seq_ - 200) : last_seq_;
        grp_[Alarms].dirty = true;
    }
    get("/snapshot", [this](const Reply& r) {
        if (r.ok()) handle_telemetry(r.json);
    });
    fetch_events(after, 5, true);
}

void NetBackend::on_ws_text(const std::string& text) {
    json j = json::parse(text, nullptr, false);
    if (!j.is_object()) return;
    std::string type = j_str(j, "type");
    if (type == "telemetry") handle_telemetry(j);
    else if (type == "progress") {
        std::lock_guard<std::mutex> lk(mu_);
        parse_progress(j, m_.progress, m_.plans, m_.x.pid);
        update_plan_actuals_locked();
        publish_locked();
    } else if (type == "alarm") {
        const json& a = j.contains("alarm") ? j["alarm"] : j;
        model::AlarmItem it = parse_alarm(a);
        std::lock_guard<std::mutex> lk(mu_);
        auto& v = m_.alarms;
        auto f = std::find_if(v.begin(), v.end(), [&](const model::AlarmItem& x) { return x.dto.id == it.dto.id; });
        if (f != v.end()) *f = it;
        else v.insert(v.begin(), it);
        std::stable_sort(v.begin(), v.end(), [](const model::AlarmItem& a1, const model::AlarmItem& b1) { return a1.dto.raisedAt > b1.dto.raisedAt; });
        if (v.size() > kAlarmsKeep) v.resize(kAlarmsKeep);
        publish_locked();
    } else if (type == "event") {
        model::EventRecord e = parse_event(j);
        handle_event(e);
        std::lock_guard<std::mutex> lk(mu_);
        if (seq_epoch_ >= 0 && e.seqEpoch != seq_epoch_ && e.seqEpoch != 0) return;
        add_events_locked({e});
        publish_locked();
    } else if (type == "hello") handle_hello(j);
    else if (type == "gap") {
        std::fprintf(stderr, "[net] gap：%s\n", j_str(j, "reason").c_str());
        {
            std::lock_guard<std::mutex> lk(mu_);
            head_seq_ = j_int(j, "eventSeqHead", head_seq_);
            if (j_str(j, "reason") == "epoch_changed") {
                m_.events.clear();
                last_seq_ = 0;
                seq_epoch_ = -1;  // 下一页 /events 的 seqEpoch 为准
            }
        }
        resync_after_gap();
    } else if (type == "subscribed" || type == "resynced") {
        std::lock_guard<std::mutex> lk(mu_);
        head_seq_ = j_int(j, "eventSeqHead", head_seq_);
    } else if (type == "error") {
        std::fprintf(stderr, "[net] WS error %s %s\n", j_str(j, "code").c_str(), j_str(j, "message").c_str());
    }
    else if (type == "control") handle_control(j);
    // pong / opEvent：界面不用（控制权状态在快照 mode 里）
}

// WS control / GET /control：只关心有没有待处理的申请（pending 为 null = 没有 / 已处理 / 已过期）
void NetBackend::handle_control(const json& j) {
    std::lock_guard<std::mutex> lk(mu_);
    static const json kNull;
    const json& p = j.contains("pending") ? j["pending"] : kNull;
    if (p.is_object() && !j_str(p, "requestId").empty()) {
        model::ControlPending c;
        c.requestId = j_str(p, "requestId");
        c.clientName = j_str(p, "clientName");
        c.tokenName = j_str(p, "tokenName");
        c.expiresAt = j_time(p, "expiresAt").value_or(0);
        m_.controlPending = c;
    } else {
        m_.controlPending.reset();
    }
    publish_locked();
}

void NetBackend::clear_pending(const std::string& id) {
    std::lock_guard<std::mutex> lk(mu_);
    if (m_.controlPending && m_.controlPending->requestId == id) {
        m_.controlPending.reset();
        publish_locked();
    }
}

// ============================================================ 定时 / 取数
int64_t NetBackend::interval_of(G g) const {
    // 每次都定时刷新的（变化太频繁，不进 revs）
    switch (g) {
        case Comm: return 5000;
        case Storage: return 60000;
        case Trend: return kTrendStepMs;
        default: break;
    }
    bool running_leak = m_.snap.leakTest.running, running_calib = m_.snap.calib && m_.snap.calib->running;
    bool tuning = false;
    for (auto& c : m_.snap.channels) tuning |= c.autotuning;
    if (g == Pid) return tuning ? 2000 : (have_revs_ ? 0 : 60000);
    if (have_revs_) return 0;  // 其余靠 revs
    switch (g) {
        case Settings: return 30000;
        case Programs: return 10000;
        case Library: case Templates: return 30000;
        case Runs: return 30000;
        case Files: return 60000;
        case Network: return 10000;
        case Usb: return 5000;
        case Users: return 60000;
        case Leak: return running_leak ? 10000 : 60000;
        case LeakSamples: return running_leak ? 5000 : 0;
        case Calib: return running_calib ? 3000 : 0;
        default: return 0;
    }
}

void NetBackend::on_tick(int64_t now) {
    std::vector<G> todo;
    {
        std::lock_guard<std::mutex> lk(mu_);
        // 断线时盖层上的说明（ws 线程连不上时不会走 on_ws_closed）
        if (!client_->ws_up()) {
            std::string err = client_->last_error();
            int tries = client_->reconnects();
            if (err != link_err_ || tries != link_tries_ || now - clock_pub_ >= 1000) {
                link_err_ = err;
                link_tries_ = tries;
                clock_pub_ = now;
                m_.snap.ts = wall_ms();  // 顶栏的钟照走
                if (!connected_) m_.linkStatus = "正在连接 " + ep_.text() + (err.empty() ? "" : "（" + err + "）");
                publish_locked();
            }
        }
        if (!connected_) return;
        for (int g = 0; g < kGroups; ++g) {
            auto& s = grp_[g];
            int64_t iv = interval_of(static_cast<G>(g));
            if (iv > 0 && now - s.last >= iv) s.dirty = true;
            if (s.dirty && !s.inflight) {
                s.dirty = false;
                s.inflight = true;
                s.last = now;
                todo.push_back(static_cast<G>(g));
            }
        }
    }
    for (G g : todo) fetch(g);
}

void NetBackend::done(G g) {
    std::lock_guard<std::mutex> lk(mu_);
    grp_[g].inflight = false;
}

void NetBackend::fetch(G g) {
    auto fail = [this, g](const Reply& r) {
        if (r.status != 404) std::fprintf(stderr, "[net] 取 %s 失败：%s\n", group_name(g), r.message().c_str());
        done(g);
    };
    switch (g) {
        case Settings:
            get("/settings", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                std::lock_guard<std::mutex> lk(mu_);
                parse_settings(r.json, m_.settings, m_.x);
                if (schema_rev_ != m_.settings.revision) grp_[Schema].dirty = true;
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Schema:
            get("/program-schema", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                model::ProgramSchema s;
                std::string err;
                if (!model::parse_program_schema(r.body, s, &err)) {
                    std::fprintf(stderr, "[net] program-schema 解析失败：%s\n", err.c_str());
                    return done(g);
                }
                std::lock_guard<std::mutex> lk(mu_);
                m_.x.schema = std::move(s);
                schema_rev_ = m_.x.schema.settingsRevision;
                grp_[g].inflight = false;
                grp_[Library].dirty = grp_[Templates].dirty = true;  // 压力换算按 schema 的单位
                publish_locked();
            });
            break;
        case Device:
            get("/device", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                std::lock_guard<std::mutex> lk(mu_);
                int64_t boot = 0;
                parse_device(r.json, m_.x, &boot);
                if (boot && !m_.bootAt) m_.bootAt = boot;
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Alarms:
            get("/alarms", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                auto v = parse_alarms(r.json);
                std::lock_guard<std::mutex> lk(mu_);
                if (v.size() > kAlarmsKeep) v.resize(kAlarmsKeep);
                m_.alarms = std::move(v);
                m_.alarmsLoaded = true;
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Programs: {
            auto left = std::make_shared<int>(8);
            for (int ch = 1; ch <= 8; ++ch)
                get("/channels/" + std::to_string(ch) + "/program", [this, g, ch, left](const Reply& r) {
                    std::lock_guard<std::mutex> lk(mu_);
                    size_t i = static_cast<size_t>(ch - 1);
                    if (r.ok()) {
                        model::ProgramDoc d;
                        std::string src;
                        if (parse_channel_program(r.json, d, src)) {
                            m_.x.programs[i] = std::move(d);
                            m_.x.programSrcId[i] = src;
                        } else {
                            m_.x.programs[i] = model::ProgramDoc{};
                            m_.x.programSrcId[i].clear();
                        }
                    } else if (r.status == 404) {
                        m_.x.programs[i] = model::ProgramDoc{};
                        m_.x.programSrcId[i].clear();
                    }
                    if (--*left == 0) {
                        grp_[g].inflight = false;
                        refresh_derived_locked();
                        publish_locked();
                    }
                });
            break;
        }
        case Library:
        case Templates: {
            bool tpl = g == Templates;
            get(tpl ? "/library/templates" : "/library/programs", [this, g, tpl, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                std::lock_guard<std::mutex> lk(mu_);
                int max = m_.x.libraryMax;
                auto v = parse_library(r.json, tpl, m_.x.schema, &max);
                (tpl ? m_.x.templates : m_.x.library) = std::move(v);
                if (!tpl) m_.x.libraryMax = max;
                grp_[g].inflight = false;
                refresh_derived_locked();
                publish_locked();
            });
            break;
        }
        case Pid:
            get("/pid", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                std::lock_guard<std::mutex> lk(mu_);
                parse_pid(r.json, m_.x.pid);
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Comm:
            get("/device/comm", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                std::lock_guard<std::mutex> lk(mu_);
                parse_comm(r.json, m_.x.comm);
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Network:
            get("/network", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                std::lock_guard<std::mutex> lk(mu_);
                parse_network(r.json, m_.x.net);
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Usb:
            get("/usb", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                std::lock_guard<std::mutex> lk(mu_);
                parse_usb(r.json, m_.x.usb);
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Storage:
            get("/storage", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                std::lock_guard<std::mutex> lk(mu_);
                parse_storage(r.json, m_.x.storage);
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Users:
            get("/users", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                std::lock_guard<std::mutex> lk(mu_);
                parse_users(r.json, m_.x.users);
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Runs:
            get("/runs?limit=50", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                auto v = parse_runs(r.json);
                std::vector<std::string> need;
                {
                    std::lock_guard<std::mutex> lk(mu_);
                    int64_t now = mono_ms();
                    for (size_t i = 0; i < v.size(); ++i) {
                        auto& rr = v[i];
                        auto it = run_detail_.find(rr.id);
                        bool have = it != run_detail_.end() && it->second.have;
                        if (have) {
                            if (!rr.trend) rr.trend = it->second.trend;
                            if (rr.program.steps.empty()) rr.program = it->second.program;
                            if (rr.stepMinutes.empty()) rr.stepMinutes = it->second.stepMinutes;
                        }
                        bool stale = have && rr.result == "running" && now - it->second.at > 60000;
                        if (static_cast<int>(i) < kRunDetails && (!have || stale) && !run_inflight_.count(rr.id)) need.push_back(rr.id);
                    }
                    m_.x.runs = std::move(v);
                    m_.x.days = build_days([&] {
                        auto f = files_tel_;
                        f.insert(f.end(), files_ev_.begin(), files_ev_.end());
                        return f;
                    }(), m_.x.runs);
                    grp_[g].inflight = false;
                    publish_locked();
                }
                fetch_run_details(need);
            });
            break;
        case Files: {
            auto left = std::make_shared<int>(2);
            for (const char* type : {"telemetry", "events"})
                get(std::string("/files?type=") + type, [this, g, type, left](const Reply& r) {
                    std::lock_guard<std::mutex> lk(mu_);
                    if (r.ok()) (std::string(type) == "telemetry" ? files_tel_ : files_ev_) = parse_files(r.json);
                    if (--*left == 0) {
                        auto f = files_tel_;
                        f.insert(f.end(), files_ev_.begin(), files_ev_.end());
                        m_.x.days = build_days(f, m_.x.runs);
                        grp_[g].inflight = false;
                        publish_locked();
                    }
                });
            break;
        }
        case Leak:
            get("/leak-tests", [this, g, fail](const Reply& r) {
                if (!r.ok()) return fail(r);
                std::lock_guard<std::mutex> lk(mu_);
                m_.x.leak.history = parse_leak_tests(r.json);
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case LeakSamples:
            get("/leak-tests/current/samples", [this, g](const Reply& r) {
                std::lock_guard<std::mutex> lk(mu_);
                if (r.ok()) m_.x.leak.samples = parse_leak_samples(r.json);
                else if (r.status == 404) m_.x.leak.samples.clear();
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Calib:
            get("/calib", [this, g](const Reply& r) {
                std::lock_guard<std::mutex> lk(mu_);
                if (r.ok()) parse_calib(r.json, m_.x.calib);
                grp_[g].inflight = false;
                publish_locked();
            });
            break;
        case Trend: {
            int64_t to = wall_ms(), from;
            bool replace;
            {
                std::lock_guard<std::mutex> lk(mu_);
                replace = !trend_ || trend_->size() == 0;
                // 增量取数从「已有的最后一桶」重新取起：上次取的时候这一桶还没走完（to = 现在），
                // 桶里可能还没有任何一帧 → 后台给 null。原来从下一桶接着取，这个 null 就永远留下，
                // 曲线每隔几个点断一下（2026-11-27 曲线页断成一段一段）。apply_trend 会用新数据覆盖这一桶
                from = replace ? to - kTrendSpanMs : trend_->t_end();
            }
            from = from / kTrendStepMs * kTrendStepMs;
            if (from > to) return done(g);
            get("/trend?from=" + url_encode(iso_ms(from)) + "&to=" + url_encode(iso_ms(to)) + "&stepMs=" + std::to_string(kTrendStepMs),
                [this, g, replace, fail](const Reply& r) {
                    if (!r.ok()) return fail(r);
                    apply_trend(parse_trend(r.json), replace);
                    done(g);
                },
                20000);
            break;
        }
        case kGroups: break;
    }
}

void NetBackend::fetch_run_details(const std::vector<std::string>& ids) {
    for (auto& id : ids) {
        {
            std::lock_guard<std::mutex> lk(mu_);
            run_inflight_.insert(id);
        }
        get("/runs/" + url_encode(id), [this, id](const Reply& r) {
            std::lock_guard<std::mutex> lk(mu_);
            run_inflight_.erase(id);
            if (!r.ok()) return;
            model::RunRecord d = parse_run(r.json);
            auto& det = run_detail_[id];
            det.trend = d.trend;
            det.program = d.program;
            det.stepMinutes = d.stepMinutes;
            det.at = mono_ms();
            det.have = true;
            for (auto& rr : m_.x.runs)
                if (rr.id == id) {
                    if (d.trend) rr.trend = d.trend;
                    if (!d.program.steps.empty()) rr.program = d.program;
                    if (!d.stepMinutes.empty()) rr.stepMinutes = d.stepMinutes;
                }
            if (run_detail_.size() > 60) run_detail_.erase(run_detail_.begin());  // 只是缓存：太多就丢
            publish_locked();
        }, 20000);
    }
}

void NetBackend::apply_trend(const std::shared_ptr<model::Trend>& t, bool replace) {
    if (!t) return;
    std::lock_guard<std::mutex> lk(mu_);
    auto merged = std::make_shared<model::Trend>();
    if (replace || !trend_ || trend_->stepMs != t->stepMs) {
        *merged = *t;
    } else {
        *merged = *trend_;
        if (t->size() > 0) {
            int64_t off = (t->t0 - merged->t0) / merged->stepMs;   // 新数据第一个点在旧数组里的下标
            if (off < 0 || off > merged->size() + 1000) {
                *merged = *t;                                       // 对不上（时钟跳了）：整段换
            } else {
                size_t o = static_cast<size_t>(off);
                for (size_t c = 0; c < 8; ++c) {
                    for (auto pr : {std::make_pair(&merged->temp[c], &t->temp[c]), std::make_pair(&merged->press[c], &t->press[c]),
                                     std::make_pair(&merged->gas[c], &t->gas[c])}) {
                        auto& dst = *pr.first;
                        const auto& src = *pr.second;
                        if (dst.size() < o) dst.resize(o, NAN);
                        dst.resize(o);
                        dst.insert(dst.end(), src.begin(), src.end());
                    }
                }
            }
        }
    }
    // 只留 24 h
    int64_t extra = merged->size() - kTrendKeepMs / merged->stepMs;
    if (extra > 0) {
        for (size_t c = 0; c < 8; ++c)
            for (auto* v : {&merged->temp[c], &merged->press[c], &merged->gas[c]}) v->erase(v->begin(), v->begin() + extra);
        merged->t0 += extra * merged->stepMs;
    }
    trend_ = merged;
    m_.x.trend = trend_;
    update_plan_actuals_locked();
    publish_locked();
}

void NetBackend::update_plan_actuals_locked() {
    // 方案总览点开一路时画的「已走过的实测」：从这一路程序开始到现在，每分钟一点（取自趋势数据）
    if (!trend_ || trend_->size() == 0) return;
    const auto& T = *trend_;
    int every = static_cast<int>(std::max<int64_t>(1, 60000 / T.stepMs));
    for (size_t i = 0; i < 8; ++i) {
        auto& pe = m_.plans[i];
        pe.tempActual.clear();
        pe.pressActual.clear();
        const auto& c = m_.snap.channels[i];
        if (!c.program || !c.program->startedAt) continue;
        int64_t start = *c.program->startedAt;
        int k0 = static_cast<int>(std::max<int64_t>(0, (start - T.t0 + T.stepMs - 1) / T.stepMs));
        for (int k = k0; k < T.size(); k += every) {
            float tv = T.temp[i][static_cast<size_t>(k)], pv = T.press[i][static_cast<size_t>(k)];
            if (!std::isnan(tv)) pe.tempActual.push_back({T.t_at(k), tv});
            if (!std::isnan(pv)) pe.pressActual.push_back({T.t_at(k), pv});
        }
        if (c.tempC) pe.tempActual.push_back({m_.snap.ts, *c.tempC});
        if (c.pressurePsi) pe.pressActual.push_back({m_.snap.ts, psi2mpa(*c.pressurePsi)});
    }
}

// ============================================================ 动作
void NetActions::result(bool ok, const std::string& msg) {
    b_.ui_.post([this, ok, msg] {
        if (on_result) on_result(ok, msg);
    });
}

void NetActions::cmd(const std::string& method, const std::string& path, json body, std::vector<NetBackend::G> refresh,
                     const std::string& ok_msg, bool urgent, std::function<void(const Reply&)> after) {
    Call c;
    c.method = method;
    c.path = path;
    if (!body.is_null()) c.body = body.dump();
    else if (method == "POST" || method == "PUT" || method == "PATCH") c.body = "{}";
    c.idem = true;  // 全部写命令都带 Idempotency-Key：重发安全，opId 也对得上
    c.urgent = urgent;
    c.timeout_ms = 12000;
    c.done = [this, refresh, ok_msg, after, method, path](const Reply& r) {
        if (r.ok()) {
            for (auto g : refresh) b_.invalidate(g);
            std::string msg = ok_msg;
            if (r.json.is_object() && r.json.contains("warnings") && r.json["warnings"].is_array())
                for (auto& w : r.json["warnings"])
                    if (w.is_string() && !w.get<std::string>().empty()) msg += (msg.empty() ? "" : "；") + w.get<std::string>();
            if (!msg.empty()) result(true, msg);
        } else {
            std::fprintf(stderr, "[net] %s %s 失败：%d %s %s\n", method.c_str(), path.c_str(), r.status, r.code().c_str(), r.message().c_str());
            result(false, r.message());
        }
        if (after) after(r);
    };
    b_.client_->call(Lane::Command, std::move(c));
}

static std::string chp(int ch, const char* tail) { return "/channels/" + std::to_string(ch) + tail; }

void NetActions::stop_all(bool s) { cmd("POST", "/stop-all", {{"stopStir", s}}, {}, "", true); }
void NetActions::stir_set_rpm(int rpm) { cmd("PUT", "/stir", {{"rpm", rpm}}); }
void NetActions::stir_run(bool on) { cmd("PUT", "/stir", {{"on", on}}, {}, "", !on); }
void NetActions::manual_valve(bool on) { cmd("PUT", "/gas/manual", {{"on", on}}); }
void NetActions::set_temp_sp(int ch, double c) { cmd("PUT", chp(ch, "/temperature/setpoint"), {{"tempC", c}}); }
void NetActions::heat(int ch, bool on) {
    if (on) cmd("POST", chp(ch, "/heat/start"), json::object());
    else cmd("POST", chp(ch, "/heat/stop"), json::object(), {}, "", true);
}
void NetActions::set_heat_mode(int ch, const std::string& mode) { cmd("PUT", chp(ch, "/temperature/mode"), {{"spMode", mode}}); }
void NetActions::set_band(int ch, double c) { cmd("PUT", chp(ch, "/temperature/band"), {{"bandC", c}}); }
void NetActions::set_anti_overshoot(int ch, bool on) { cmd("PUT", chp(ch, "/temperature/anti-overshoot"), {{"on", on}}); }
void NetActions::set_profile(int ch, const std::vector<model::CurveSeg>& segs) {
    json a = json::array();
    for (auto& s : segs) a.push_back({{"tempC", s.tempC}, {"minutes", s.minutes}, {"enabled", true}});
    cmd("PUT", chp(ch, "/profile"), {{"segments", a}});
}
void NetActions::start_profile(int ch) { cmd("POST", chp(ch, "/profile/start"), json::object()); }
void NetActions::set_valve(int ch, bool open) { cmd("PUT", chp(ch, "/valve"), {{"open", open}}, {}, "", !open); }
void NetActions::set_gas_source(int k, bool open) {
    static const char* ids[3] = {"inert", "A", "B"};
    if (k == 3) cmd("PUT", "/gas/vent", {{"open", open}}, {}, "", !open);
    else if (k >= 0 && k < 3) cmd("PUT", std::string("/gas/inlets/") + ids[k], {{"open", open}}, {}, "", !open);
}
void NetActions::gas_zero(int ch) { cmd("POST", chp(ch, "/gas-counter/reset"), json::object()); }
void NetActions::set_solution_volume(int ch, double ml) { cmd("PUT", chp(ch, "/volume"), {{"ml", ml}}); }
void NetActions::channel_enable(int ch, bool on) {
    cmd("POST", chp(ch, on ? "/enable" : "/disable"), json::object(), {NetBackend::Programs});
}
void NetActions::copy_to_all(int ch) {
    std::vector<int> to;
    {
        std::lock_guard<std::mutex> lk(b_.mu_);
        for (auto& c : b_.m_.snap.channels)
            if (c.id != ch && c.installed && !c.programOwned) to.push_back(c.id);
    }
    if (to.empty()) {
        result(false, "没有可以复制到的通道（其余的都停用了或者在跑程序）");
        return;
    }
    cmd("POST", "/channels/copy-config", {{"from", ch}, {"to", to}}, {}, "已复制到 " + fmt_channels(to));
}
void NetActions::abort_program(int ch) { cmd("DELETE", chp(ch, "/program/run"), nullptr, {NetBackend::Programs, NetBackend::Runs}, "", true); }
void NetActions::ack_alarm(const std::string& id) { cmd("POST", "/alarms/" + url_encode(id) + "/ack", json::object(), {NetBackend::Alarms}); }
void NetActions::ack_all() { cmd("POST", "/alarms/ack-all", json::object(), {NetBackend::Alarms}); }
void NetActions::set_language(const std::string& code) { cmd("PUT", "/system/language", {{"code", code}}); }

void NetActions::put_program(int ch, const model::ProgramDoc& p) {
    json body = {{"steps", steps_to_wire(p)}, {"name", p.name}};
    if (!p.id.empty()) body["sourceId"] = p.id;
    cmd("PUT", chp(ch, "/program"), body, {NetBackend::Programs});
}
void NetActions::start_program(int ch) {
    cmd("POST", chp(ch, "/program/run"), json::object(), {NetBackend::Programs, NetBackend::Runs});
}
void NetActions::lib_save(bool tpl, const model::ProgramDoc& p) {
    json body;
    {
        std::lock_guard<std::mutex> lk(b_.mu_);
        body = library_to_wire(p, tpl, b_.m_.x.schema);
    }
    std::string kind = tpl ? "/library/templates" : "/library/programs";
    std::vector<NetBackend::G> rf = {tpl ? NetBackend::Templates : NetBackend::Library};
    if (p.id.empty()) cmd("POST", kind, body, rf);
    else cmd("PUT", kind + "/" + url_encode(p.id), body, rf);
}
void NetActions::lib_delete(bool tpl, const std::string& id, int64_t revision) {
    std::string kind = tpl ? "/library/templates/" : "/library/programs/";
    cmd("DELETE", kind + url_encode(id) + "?revision=" + std::to_string(revision), nullptr,
        {tpl ? NetBackend::Templates : NetBackend::Library});
}
void NetActions::lib_import_usb() {
    cmd("POST", "/library/import", {{"source", "usb"}}, {NetBackend::Library, NetBackend::Templates}, "", false, [this](const Reply& r) {
        if (!r.ok() || !r.json.is_object()) return;
        const json& res = r.json.contains("result") ? r.json["result"] : json::object();
        int add = static_cast<int>(j_int(res.contains("programs") ? res["programs"] : json::object(), "added") +
                                   j_int(res.contains("templates") ? res["templates"] : json::object(), "added"));
        result(true, "已从 U 盘导入 " + std::to_string(add) + " 个程序 / 模板");
    });
}
void NetActions::leak_start(const std::vector<int>& chs) { cmd("POST", "/leak-tests", {{"channels", chs}}, {NetBackend::Leak}); }
void NetActions::leak_abort() { cmd("DELETE", "/leak-tests/current", nullptr, {NetBackend::Leak}, "", true); }
void NetActions::run_note(const std::string& id, const std::string& note) {
    cmd("PUT", "/runs/" + url_encode(id) + "/note", {{"note", note}}, {NetBackend::Runs});
}
void NetActions::export_usb(const model::ExportReq& r) {
    json b = {{"kinds", r.kinds}, {"channels", r.channels}, {"stepSec", r.stepSec}};
    if (!r.runId.empty()) b["runId"] = r.runId;
    else if (!r.file.empty()) b["file"] = r.file;
    else if (!r.day.empty()) b["day"] = r.day;
    else {
        b["from"] = iso_ms(r.from);
        b["to"] = iso_ms(r.to);
    }
    cmd("POST", "/export", b, {NetBackend::Usb});
}

void NetActions::patch(json value, const std::string& key, bool retried) {
    Call c;
    c.method = "PATCH";
    c.path = "/settings";
    c.idem = true;
    c.timeout_ms = 12000;
    // revision 在真正发出去时才取：连着改几项时，后一项要用前一项成功后的 revision
    c.body_fn = [this, value, key] {
        std::lock_guard<std::mutex> lk(b_.mu_);
        return json{{"revision", std::max(b_.m_.settings.revision, b_.settings_rev_hint_)}, {"values", {{key, value}}}}.dump();
    };
    c.done = [this, value, key, retried](const Reply& r) {
        if (r.ok()) {
            const json& res = r.json.is_object() && r.json.contains("result") ? r.json["result"] : json::object();
            if (res.contains("revision") && res["revision"].is_number()) {
                std::lock_guard<std::mutex> lk(b_.mu_);
                b_.settings_rev_hint_ = res["revision"].get<int64_t>();
            }
            b_.refetch_now(NetBackend::Settings);  // 马上取回来，界面不用等下一轮定时刷新
            return;
        }
        if (r.code() == "REVISION_CONFLICT" && !retried) {
            // 后台自己改过设置（标定结果、泄漏测试时间）：取最新的 revision 再发一次
            b_.get("/settings", [this, value, key](const Reply& s) {
                if (s.ok()) {
                    std::lock_guard<std::mutex> lk(b_.mu_);
                    parse_settings(s.json, b_.m_.settings, b_.m_.x);
                }
                patch(value, key, true);
            });
            return;
        }
        result(false, r.message());
        b_.invalidate(NetBackend::Settings);  // 界面上的值回到后台的真实值
    };
    b_.client_->call(Lane::Command, std::move(c));
}

void NetActions::patch_setting(const std::string& key, double v) {
    std::string k = wire_setting_key(key);
    if (setting_is_bool(k)) patch(json(v != 0), k, false);
    else patch(json(v), k, false);
}
void NetActions::patch_setting_str(const std::string& key, const std::string& v) { patch(json(v), wire_setting_key(key), false); }

void NetActions::set_pid(int ch, int band, double p, double i, double d) {
    cmd("PUT", chp(ch, "/pid/") + std::to_string(band), {{"p", p}, {"i", i}, {"d", d}}, {NetBackend::Pid});
}
void NetActions::set_pid_bands(double lo, double hi) {
    int64_t rev;
    {
        std::lock_guard<std::mutex> lk(b_.mu_);
        rev = std::max(b_.m_.settings.revision, b_.settings_rev_hint_);
    }
    cmd("PUT", "/pid/bands", {{"lowMidC", lo}, {"midHighC", hi}, {"revision", rev}}, {NetBackend::Pid, NetBackend::Settings});
}
void NetActions::autotune(int ch, int band, double sp) {
    cmd("POST", chp(ch, "/autotune"), {{"tempC", sp}, {"band", band}}, {NetBackend::Pid});
}
void NetActions::autotune_cancel(int ch) { cmd("DELETE", chp(ch, "/autotune"), nullptr, {NetBackend::Pid}, "", true); }
void NetActions::calib_start(int ch, double ml, bool redo_manifold) {
    cmd("POST", "/calib", {{"channel", ch}, {"waterMl", ml}, {"redoManifold", redo_manifold}}, {NetBackend::Calib});
}
void NetActions::calib_continue(double ml) {
    cmd("POST", "/calib/continue", ml > 0 ? json{{"waterMl", ml}} : json::object(), {NetBackend::Calib});
}
void NetActions::calib_abort() { cmd("DELETE", "/calib/current", nullptr, {NetBackend::Calib}, "", true); }
void NetActions::wifi_enable(bool on) { cmd("PUT", "/network/wifi", {{"enabled", on}}, {NetBackend::Network}); }
void NetActions::wifi_scan() { cmd("POST", "/network/wifi/scan", json::object(), {NetBackend::Network}); }
void NetActions::wifi_connect(const std::string& ssid, const std::string& pw) {
    json b = {{"ssid", ssid}};
    if (!pw.empty()) b["password"] = pw;   // 不打印、不进日志
    cmd("POST", "/network/wifi/connect", b, {NetBackend::Network});
}
void NetActions::wifi_forget(const std::string& ssid) { cmd("DELETE", "/network/wifi/saved/" + url_encode(ssid), nullptr, {NetBackend::Network}); }
void NetActions::wifi_disconnect() { cmd("POST", "/network/wifi/disconnect", json::object(), {NetBackend::Network}); }
void NetActions::set_ip_mode(bool dhcp) { cmd("PUT", "/network/ip", {{"mode", dhcp ? "dhcp" : "manual"}}, {NetBackend::Network}); }
void NetActions::comm_test(const std::string& module) {
    cmd("POST", "/device/comm/" + url_encode(module) + "/test", json::object(), {NetBackend::Comm}, "", false, [this](const Reply& r) {
        if (!r.ok() || !r.json.is_object() || !r.json.contains("result")) return;
        std::string msg = j_str(r.json["result"], "message");
        if (!msg.empty()) result(j_bool(r.json["result"], "ok", true), msg);
    });
}
void NetActions::comm_reconnect(const std::string& module) {
    cmd("POST", "/device/comm/" + url_encode(module) + "/reconnect", json::object(), {NetBackend::Comm});
}
void NetActions::quit_app() { b_.quit_ = true; }  // 只退界面；reactord 照常跑
// 允许 / 拒绝 PC 的控制申请（本地专用）。先在本地清掉待处理，弹窗不再弹；结果以后台推的 control 消息为准
void NetActions::control_grant(const std::string& id) {
    b_.clear_pending(id);
    cmd("POST", "/control/requests/" + url_encode(id) + "/grant", json::object(), {}, "已允许 PC 远程控制");
}
void NetActions::control_take_back() { cmd("POST", "/control/take-back", json::object(), {}, "已收回控制权 · 回到触屏"); }
void NetActions::control_deny(const std::string& id) {
    b_.clear_pending(id);
    cmd("POST", "/control/requests/" + url_encode(id) + "/deny", json::object(), {}, "已拒绝 PC 的控制申请");
}

}  // namespace reactor::hmi::net
