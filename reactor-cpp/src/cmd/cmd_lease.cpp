#include "cmd/cmd_lease.hpp"

#include <cmath>

#include "cmd/cmd_util.hpp"
#include "core/num_format.hpp"
#include "core/op_codes.hpp"
#include "net/crypto.hpp"
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace reactor::cmd {

namespace oc = core::op_codes;

namespace {
std::string secs(int64_t ms) { return core::fmt_dec(static_cast<double>(ms) / 1000.0, 0); }
}  // namespace

const char* lease_state_word(LeaseState s) {
    switch (s) {
        case LeaseState::Requested: return "requested";
        case LeaseState::Remote: return "remote";
        case LeaseState::RemoteLost: return "remoteLost";
        default: return "local";
    }
}

const char* lease_event_name(LeaseEventKind k) {
    switch (k) {
        case LeaseEventKind::Requested: return "requested";
        case LeaseEventKind::Granted: return "granted";
        case LeaseEventKind::Denied: return "denied";
        case LeaseEventKind::RequestExpired: return "requestExpired";
        case LeaseEventKind::Released: return "released";
        case LeaseEventKind::TakenBack: return "takenBack";
        case LeaseEventKind::Lost: return "lost";
        case LeaseEventKind::Resumed: return "resumed";
        case LeaseEventKind::Expired: return "expired";
        case LeaseEventKind::Invalidated: return "invalidated";
    }
    return "unknown";
}

const char* request_result_word(RequestResult r) {
    switch (r) {
        case RequestResult::Pending: return "pending";
        case RequestResult::Granted: return "granted";
        case RequestResult::Denied: return "denied";
        case RequestResult::Expired: return "expired";
        default: return "unknown";
    }
}

ControlLease::ControlLease(ControlArbiter& arbiter) : arb_(arbiter) {}

void ControlLease::configure(int64_t hb, int64_t grace, int64_t req, std::optional<int64_t> cooldown) {
    std::lock_guard<std::mutex> lk(mu_);
    if (hb > 0) hb_ms_ = hb;
    if (grace >= 0) grace_ms_ = grace;
    if (req > 0) req_ms_ = req;
    if (cooldown && *cooldown >= 0) cooldown_ms_ = *cooldown;
}
int64_t ControlLease::heartbeat_timeout_ms() const { std::lock_guard<std::mutex> lk(mu_); return hb_ms_; }
int64_t ControlLease::grace_ms() const { std::lock_guard<std::mutex> lk(mu_); return grace_ms_; }
int64_t ControlLease::request_timeout_ms() const { std::lock_guard<std::mutex> lk(mu_); return req_ms_; }
int64_t ControlLease::deny_cooldown_ms() const { std::lock_guard<std::mutex> lk(mu_); return cooldown_ms_; }

LeaseState ControlLease::state() const {
    std::lock_guard<std::mutex> lk(mu_);
    return state_;
}

std::optional<int64_t> ControlLease::hard_expiry_locked() const {
    if (state_ == LeaseState::Remote) return deadline_ + grace_ms_;
    if (state_ == LeaseState::RemoteLost) return deadline_;
    return std::nullopt;
}

LeaseInfo ControlLease::lease_locked() const {
    LeaseInfo l;
    l.lease_id = lease_id_;
    l.token = token_;
    l.client = client_;
    l.expires_at = state_ == LeaseState::Remote ? deadline_ : last_hb_ + hb_ms_;
    l.hard_expires_at = hard_expiry_locked().value_or(deadline_);
    return l;
}

LeaseSnapshot ControlLease::snapshot(int64_t now) {
    tick(now);
    std::lock_guard<std::mutex> lk(mu_);
    LeaseSnapshot s;
    s.state = state_;
    s.token = token_;
    s.client = client_;
    if (state_ == LeaseState::Requested) {
        s.request_id = request_id_;
        s.request_deadline = deadline_;
    }
    if (state_ == LeaseState::Remote) s.lease_expires_at = deadline_;
    else if (state_ == LeaseState::RemoteLost) s.lease_expires_at = last_hb_ + hb_ms_;
    s.hard_expires_at = hard_expiry_locked();
    s.has_lease = state_ == LeaseState::Remote || state_ == LeaseState::RemoteLost;
    return s;
}

LeaseEvent ControlLease::ev(LeaseEventKind k, std::string detail, int64_t now) const {
    LeaseEvent e;
    e.kind = k;
    e.token = token_;
    e.client = client_;
    if (!request_id_.empty()) e.request_id = request_id_;
    if (!lease_id_.empty()) e.lease_id = lease_id_;
    e.detail = std::move(detail);
    e.at = now;
    return e;
}

void ControlLease::remember(const std::string& id, Rec r) {
    requests_[id] = std::move(r);
    request_order_.push_back(id);
    while (static_cast<int>(request_order_.size()) > kRequestHistory) {
        requests_.erase(request_order_.front());
        request_order_.pop_front();
    }
}

void ControlLease::cooldown_locked(int64_t now) {
    if (cooldown_ms_ > 0 && !token_.empty()) cooldown_[token_] = now + cooldown_ms_;
}

void ControlLease::to_local_locked(const char* reason) {
    state_ = LeaseState::Local;
    lease_id_.clear();
    request_id_.clear();
    token_.clear();
    client_.clear();
    if (reason) arb_.return_to_local(reason);
}

// ===================== 申请 / 允许 / 拒绝 =====================

RequestOutcome ControlLease::request(const std::string& token_in, const std::string& client_in, int64_t now, bool auto_grant) {
    std::vector<LeaseEvent> events;
    RequestOutcome out;
    {
        std::lock_guard<std::mutex> lk(mu_);
        bool changed = advance_locked(now, events);
        std::string token = token_in;
        std::string client = sanitize_client_name(client_in);
        if (client.empty()) client = sanitize_client_name(token);
        if (client.empty()) client = "远程客户端";

        if (state_ == LeaseState::RemoteLost && token_ == token && client_ == client) {
            events.push_back(ev(LeaseEventKind::Invalidated, client_ + " 在宽限期内重新申请 · 旧租约作废", now));
            to_local_locked("重新申请");
            changed = true;
        }
        auto cd = cooldown_.find(token);
        if (state_ == LeaseState::Remote || state_ == LeaseState::RemoteLost) {
            out.code = oc::kNotControlHolder;
            out.message = "当前控制权属于「" + client_ + "」";
        } else if (state_ == LeaseState::Requested && token_ == token && client_ == client) {
            out.request_id = request_id_;
            out.deadline = deadline_;
            auto it = requests_.find(request_id_);
            if (it != requests_.end()) out.secret = it->second.secret;
        } else if (state_ == LeaseState::Requested) {
            out.code = oc::kControlRequestPending;
            out.message = std::string(kMsgRequestPending) + "（" + client_ + "）";
        } else if (state_ == LeaseState::Local && !auto_grant && cd != cooldown_.end() && now < cd->second) {
            out.code = oc::kRateLimited;
            out.message = "触屏刚拒绝了该令牌的申请（或申请超时）· " +
                          core::fmt_dec(std::ceil(static_cast<double>(cd->second - now) / 1000.0), 0) + " s 后可再申请";
        } else {
            cooldown_.erase(token);
            request_id_ = random_hex(8);
            token_ = token;
            client_ = client;
            deadline_ = now + req_ms_;
            state_ = LeaseState::Requested;
            std::string secret = random_hex(16);
            Rec r;
            r.result = RequestResult::Pending;
            r.token = token;
            r.client = client;
            r.deadline = deadline_;
            r.secret = secret;
            remember(request_id_, r);
            events.push_back(ev(LeaseEventKind::Requested,
                                client + "（令牌 " + token + "）申请远程控制 · " +
                                    (auto_grant ? std::string("自动授权") : "等待触屏确认（" + secs(req_ms_) + " s）"),
                                now));
            changed = true;
            out.request_id = request_id_;
            out.secret = secret;
            if (auto_grant) out.lease = grant_locked(now, events);
            else out.deadline = deadline_;
        }
        commit(events, changed);
    }
    drain();
    return out;
}

std::optional<LeaseInfo> ControlLease::grant(const std::string& request_id, int64_t now) {
    std::vector<LeaseEvent> events;
    std::optional<LeaseInfo> lease;
    {
        std::lock_guard<std::mutex> lk(mu_);
        bool changed = advance_locked(now, events);
        if (state_ == LeaseState::Requested && request_id_ == request_id) {
            lease = grant_locked(now, events);
            changed = true;
        }
        commit(events, changed);
    }
    drain();
    return lease;
}

LeaseInfo ControlLease::grant_locked(int64_t now, std::vector<LeaseEvent>& events) {
    lease_id_ = random_hex(16);
    issued_.insert(lease_id_);
    issued_order_.push_back(lease_id_);
    while (static_cast<int>(issued_order_.size()) > kIssuedLeaseHistory) {
        issued_.erase(issued_order_.front());
        issued_order_.pop_front();
    }
    last_hb_ = now;
    deadline_ = now + hb_ms_;
    state_ = LeaseState::Remote;
    auto it = requests_.find(request_id_);
    if (it != requests_.end()) {
        it->second.result = RequestResult::Granted;
        it->second.lease_id = lease_id_;
    }
    events.push_back(ev(LeaseEventKind::Granted,
                        "已授权 " + client_ + "（令牌 " + token_ + "）远程控制 · 心跳超时 " + secs(hb_ms_) + " s · 宽限 " +
                            secs(grace_ms_) + " s",
                        now));
    arb_.enter_remote(token_, client_);
    return lease_locked();
}

bool ControlLease::deny(const std::string& request_id, int64_t now) {
    std::vector<LeaseEvent> events;
    bool ok = false;
    {
        std::lock_guard<std::mutex> lk(mu_);
        bool changed = advance_locked(now, events);
        if (state_ == LeaseState::Requested && request_id_ == request_id) {
            auto it = requests_.find(request_id_);
            if (it != requests_.end()) it->second.result = RequestResult::Denied;
            events.push_back(ev(LeaseEventKind::Denied, "触屏拒绝 " + client_ + "（令牌 " + token_ + "）的远程控制申请", now));
            cooldown_locked(now);
            to_local_locked("拒绝申请");
            ok = changed = true;
        }
        commit(events, changed);
    }
    drain();
    return ok;
}

std::optional<RequestStatus> ControlLease::query_request(const std::string& request_id, const std::string& token,
                                                         const std::string& secret, int64_t now) {
    tick(now);
    std::lock_guard<std::mutex> lk(mu_);
    auto it = requests_.find(request_id);
    if (it == requests_.end()) return std::nullopt;
    const Rec& rec = it->second;
    RequestStatus s;
    s.result = rec.result;
    s.request_id = request_id;
    s.token = rec.token;
    s.client = rec.client;
    if (s.result == RequestResult::Granted) {
        bool current = (state_ == LeaseState::Remote || state_ == LeaseState::RemoteLost) && lease_id_ == rec.lease_id;
        if (!current) s.result = RequestResult::Expired;
        else if (rec.token == token && !secret.empty() && net::constant_time_equal(secret, rec.secret)) s.lease = lease_locked();
    }
    if (s.result == RequestResult::Pending) s.deadline = rec.deadline;
    return s;
}

// ===================== 校验 / 心跳 / 释放 / 夺回 =====================

LeaseCheck ControlLease::validate_locked(const std::string& lease_in, const std::string& token) const {
    LeaseCheck r;
    if (!(state_ == LeaseState::Remote || state_ == LeaseState::RemoteLost) || arb_.mode() != ControlMode::Remote) {
        r.code = oc::kModeLocal;
        r.message = ControlArbiter::kMsgModeLocal;
        return r;
    }
    std::string lease = lease_in;
    while (!lease.empty() && (lease.back() == ' ' || lease.back() == '\t')) lease.pop_back();
    while (!lease.empty() && (lease.front() == ' ' || lease.front() == '\t')) lease.erase(0, 1);
    if (lease.empty()) {
        r.code = oc::kNotControlHolder;
        r.message = kMsgNoLease;
        return r;
    }
    if (lease != lease_id_) {
        if (issued_.count(lease)) {
            r.code = oc::kControlLeaseExpired;
            r.message = kMsgLeaseExpired;
        } else {
            r.code = oc::kNotControlHolder;
            r.message = std::string(kMsgNotCurrentLease) + " · 当前控制权属于「" + client_ + "」";
        }
        return r;
    }
    if (token != token_) {
        r.code = oc::kNotControlHolder;
        r.message = "租约属于令牌「" + token_ + "」";
        return r;
    }
    r.ok = true;
    r.token = token_;
    r.client = client_;
    return r;
}

LeaseCheck ControlLease::validate(const std::string& lease_header, const std::string& token, int64_t now) {
    std::vector<LeaseEvent> events;
    LeaseCheck r;
    {
        std::lock_guard<std::mutex> lk(mu_);
        bool changed = advance_locked(now, events);
        r = validate_locked(lease_header, token);
        if (r.ok) changed |= touch_locked(now, events);
        commit(events, changed);
    }
    drain();
    return r;
}

HeartbeatResult ControlLease::heartbeat(const std::string& lease_id, const std::string& token, int64_t now) {
    std::vector<LeaseEvent> events;
    HeartbeatResult r;
    {
        std::lock_guard<std::mutex> lk(mu_);
        bool changed = advance_locked(now, events);
        auto c = validate_locked(lease_id, token);
        if (!c.ok) {
            r.code = c.code;
            r.message = c.message;
        } else {
            bool resumed = touch_locked(now, events);
            changed |= resumed;
            r.ok = true;
            r.resumed = resumed;
            r.expires_at = deadline_;
            r.hard_expires_at = hard_expiry_locked();
        }
        commit(events, changed);
    }
    drain();
    return r;
}

bool ControlLease::touch_locked(int64_t now, std::vector<LeaseEvent>& events) {
    last_hb_ = now;
    deadline_ = now + hb_ms_;
    if (state_ != LeaseState::RemoteLost) return false;
    state_ = LeaseState::Remote;
    events.push_back(ev(LeaseEventKind::Resumed, client_ + " 在宽限期内恢复心跳 · 继续远程控制", now));
    return true;
}

LeaseCheck ControlLease::release(const std::string& lease_id, const std::string& token, int64_t now) {
    std::vector<LeaseEvent> events;
    LeaseCheck r;
    {
        std::lock_guard<std::mutex> lk(mu_);
        bool changed = advance_locked(now, events);
        r = validate_locked(lease_id, token);
        if (r.ok) {
            events.push_back(ev(LeaseEventKind::Released, client_ + " 交还控制权 · 回到本地", now));
            to_local_locked("远程释放");
            changed = true;
        }
        commit(events, changed);
    }
    drain();
    return r;
}

bool ControlLease::take_back(int64_t now) {
    std::vector<LeaseEvent> events;
    bool was = false;
    {
        std::lock_guard<std::mutex> lk(mu_);
        bool changed = advance_locked(now, events);
        if (state_ == LeaseState::Requested) {
            auto it = requests_.find(request_id_);
            if (it != requests_.end()) it->second.result = RequestResult::Denied;
            events.push_back(ev(LeaseEventKind::Denied, "触屏切回本地 · " + client_ + " 的申请按拒绝处理", now));
            cooldown_locked(now);
            to_local_locked("本地切回");
            was = changed = true;
        } else if (state_ == LeaseState::Remote || state_ == LeaseState::RemoteLost) {
            events.push_back(ev(LeaseEventKind::TakenBack, "触屏切回本地 · " + client_ + " 的租约作废", now));
            to_local_locked("本地切回");
            was = changed = true;
        }
        commit(events, changed);
    }
    drain();
    return was;
}

std::vector<LeaseEvent> ControlLease::tick(int64_t now) {
    std::vector<LeaseEvent> events;
    {
        std::lock_guard<std::mutex> lk(mu_);
        bool changed = advance_locked(now, events);
        commit(events, changed);
    }
    drain();
    return events;
}

bool ControlLease::advance_locked(int64_t now, std::vector<LeaseEvent>& events) {
    bool changed = false;
    if ((state_ == LeaseState::Remote || state_ == LeaseState::RemoteLost) && arb_.mode() != ControlMode::Remote) {
        events.push_back(ev(LeaseEventKind::Invalidated, "控制权已被收回（" + arb_.last_return_reason() + "）· " + client_ + " 的租约作废", now));
        to_local_locked(nullptr);
        return true;
    }
    if (state_ == LeaseState::Requested && now >= deadline_) {
        auto it = requests_.find(request_id_);
        if (it != requests_.end()) it->second.result = RequestResult::Expired;
        events.push_back(ev(LeaseEventKind::RequestExpired, client_ + " 的远程控制申请 " + secs(req_ms_) + " s 内未处理 · 按拒绝", now));
        cooldown_locked(now);
        to_local_locked("申请超时");
        changed = true;
    } else if (state_ == LeaseState::Remote && now >= deadline_) {
        state_ = LeaseState::RemoteLost;
        deadline_ = now + grace_ms_;
        events.push_back(ev(LeaseEventKind::Lost,
                            client_ + " 心跳超时（" + secs(hb_ms_) + " s）· 宽限 " + secs(grace_ms_) +
                                " s 内凭同一租约可恢复 · 设备继续运行",
                            now));
        changed = true;
        if (now >= deadline_) {   // grace = 0：立即作废
            events.push_back(ev(LeaseEventKind::Expired, client_ + " 宽限期满 · 租约作废、回到本地控制", now));
            to_local_locked("心跳丢失");
        }
    } else if (state_ == LeaseState::RemoteLost && now >= deadline_) {
        events.push_back(ev(LeaseEventKind::Expired, client_ + " 宽限期满 · 租约作废、回到本地控制", now));
        to_local_locked("心跳丢失");
        changed = true;
    }
    return changed;
}

// ===================== 迁移送达（锁外、按序、不嵌套）=====================

void ControlLease::commit(std::vector<LeaseEvent>& events, bool changed) {
    if (events.empty() && !changed) return;
    for (auto& e : events) e.state = state_;
    std::lock_guard<std::mutex> lk(pend_mu_);
    pending_.emplace_back(events, changed);
}

void ControlLease::drain() {
    if (drainer_.load() == std::this_thread::get_id()) return;   // 回调里再调本类：外层循环接着送
    while (true) {
        {
            std::lock_guard<std::mutex> lk(pend_mu_);
            if (pending_.empty()) return;
        }
        if (!drain_mu_.try_lock()) return;   // 别的线程正在送：它会把这一批一起送完
        drainer_.store(std::this_thread::get_id());
        while (true) {
            std::pair<std::vector<LeaseEvent>, bool> batch;
            {
                std::lock_guard<std::mutex> lk(pend_mu_);
                if (pending_.empty()) break;
                batch = std::move(pending_.front());
                pending_.pop_front();
            }
            for (const auto& e : batch.first)
                if (on_transition) on_transition(e);
            if (batch.second && on_changed) on_changed();
        }
        drainer_.store(std::thread::id());
        drain_mu_.unlock();
    }
}

}  // namespace reactor::cmd
