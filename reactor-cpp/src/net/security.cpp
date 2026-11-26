#include "net/security.hpp"

#include <algorithm>
#include <cctype>

#include "net/api_errors.hpp"

namespace reactor::net {

Gate::Gate(const ApiConfig& cfg, GateOptions opt, ApiStats* stats, AuditFn audit)
    : opt_(std::move(opt)), stats_(stats), audit_(std::move(audit)) {
    update_config(cfg);
}

std::vector<std::string> Gate::update_config(const ApiConfig& cfg) {
    auto s = std::make_shared<const State>(cfg);
    write_.set_permits(cfg.write_rate_limit_per_sec > 0 ? cfg.write_rate_limit_per_sec : 10);
    std::vector<std::string> bad = s->cidr.invalid();
    std::lock_guard<std::mutex> lk(mu_);
    st_ = std::move(s);
    return bad;
}

std::shared_ptr<const Gate::State> Gate::state() const {
    std::lock_guard<std::mutex> lk(mu_);
    return st_;
}

bool Gate::token_name_valid(const std::string& name) const { return state()->tokens.has_name(name); }
std::vector<std::string> Gate::invalid_cidrs() const { return state()->cidr.invalid(); }
size_t Gate::cidr_count() const { return state()->cidr.count(); }
int Gate::write_rate_limit() const { return state()->write_rate; }

bool Gate::prefix_ok(const std::string& path) const {
    const std::string& p = opt_.api_prefix;
    if (p.empty()) return true;
    if (path.size() < p.size() || !iequals(path.substr(0, p.size()), p)) return false;
    return path.size() == p.size() || path[p.size()] == '/';
}

Gate::Decision Gate::check(Request& req, const Router::Match& m, int64_t now_ms) {
    RateClass rc = m.route ? effective_rate_class(m.route->opts.rate, req.method) : RateClass::Read;
    req.rate_class = rc;
    if (req.peer.transport == Transport::Unix) return check_unix(req, m);
    return check_tcp(req, m, now_ms);
}

Gate::Decision Gate::check_unix(Request& req, const Router::Match&) {
    Decision d;
    if (!opt_.unix_allowed_uids.empty() &&
        std::find(opt_.unix_allowed_uids.begin(), opt_.unix_allowed_uids.end(), req.peer.uid) == opt_.unix_allowed_uids.end()) {
        d.reject = Response::error(codes::kIpNotAllowed, "本机进程（uid " + std::to_string(req.peer.uid) + "）不允许连接");
        d.close_connection = true;
        return d;
    }
    if (!prefix_ok(req.path)) {
        d.reject = Response::error(codes::kNotFound, "接口前缀为 " + opt_.api_prefix);
        return d;
    }
    req.auth = AuthContext::local_identity();   // 本地：不鉴权、不限流（设计文档 §5.1）
    return d;
}

Gate::Decision Gate::check_tcp(Request& req, const Router::Match& m, int64_t now_ms) {
    Decision d;
    auto st = state();
    if (!st->cidr.is_allowed(req.peer.ip)) {
        d.reject = Response::error(codes::kIpNotAllowed, req.peer.ip_text + " 不在允许的网段内");
        d.close_connection = true;
        return d;
    }
    if (!prefix_ok(req.path)) {
        d.reject = Response::error(codes::kNotFound, "接口前缀为 " + opt_.api_prefix);
        return d;
    }
    bool no_auth = m.route && m.route->opts.no_auth;
    std::string key;
    if (no_auth) {
        req.auth = AuthContext::denied();
        key = "ip:" + req.peer.ip_text;
    } else {
        if (req.has_query("access_token")) {
            d.reject = Response::error(codes::kUnauthorized, "不支持 ?access_token=，请用 Authorization: Bearer 头（§4.2）");
            return d;
        }
        std::string authz = req.header("Authorization");
        req.auth = st->tokens.authenticate_header(authz);
        if (!req.auth.ok) {
            if (stats_) stats_->auth_failures++;
            const std::string& ip = req.peer.ip_text;
            // 先校验再判限速：同一 NAT 后的合法令牌不受别人猜令牌的影响（C#: GateAsync）
            if (auth_fail_.is_blocked(ip, now_ms)) {
                d.reject = Response::error(codes::kRateLimited,
                                           "鉴权失败过多：同一地址每秒最多 " + std::to_string(kAuthFailuresPerSec) + " 次");
                return d;
            }
            auth_fail_.record_failure(ip, now_ms);
            if (audit_ && auth_audit_.allow(ip, now_ms))
                audit_("warn", "接口鉴权失败",
                       ip + " " + req.method + " " + req.path + (authz.empty() ? " · 未带令牌" : " · 令牌无效") +
                           "（同一地址每分钟只记一条）");
            Response r = Response::error(codes::kUnauthorized, "缺少或无效的 Bearer 令牌");
            r.headers.set("WWW-Authenticate", "Bearer");
            d.reject = std::move(r);
            return d;
        }
        key = "tok:" + req.auth.name;
    }

    RateClass rc = req.rate_class;
    if (rc == RateClass::None) return d;
    auto limited = [&](const std::string& msg) {
        d.reject = Response::error(codes::kRateLimited, msg);
        return d;
    };
    if (!read_.try_acquire(key, now_ms)) return limited("只读请求限 " + std::to_string(kReadRateLimitPerSec) + " 次/秒");
    switch (rc) {
        case RateClass::Write:
            if (!write_.try_acquire(key, now_ms)) return limited("写命令限 " + std::to_string(st->write_rate) + " 次/秒/令牌");
            break;
        case RateClass::Estop:
            if (!estop_.try_acquire(key, now_ms))
                return limited("/estop 限 " + std::to_string(kEstopRateLimitPerSec) + " 次/秒/令牌（单独分区，不与写命令共用）");
            break;
        case RateClass::Heartbeat:
            if (!heartbeat_.try_acquire(key, now_ms))
                return limited("/control/heartbeat 限 " + std::to_string(kHeartbeatRateLimitPerSec) + " 次/秒/令牌（单独分区）");
            break;
        case RateClass::Preflight:
            if (!preflight_.try_acquire(key, now_ms))
                return limited("/preflight 限 " + std::to_string(kPreflightRateLimitPerSec) + " 次/秒（要进 UI 线程）");
            break;
        case RateClass::Download:
            d.download_slot = downloads_.try_acquire();
            if (!d.download_slot) {
                d.reject = Response::error(codes::kDownloadBusy, "同时最多 " + std::to_string(kDownloadConcurrency) + " 个下载流");
                return d;
            }
            break;
        default:
            break;
    }
    return d;
}

}  // namespace reactor::net
